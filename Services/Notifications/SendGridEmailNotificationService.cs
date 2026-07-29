using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Reports_Sanity_Check.Services.PowerBi.Models;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Reports_Sanity_Check.Services.Notifications;

/// <summary>
/// Sends a summary email after a sanity-check run completes. Implementations must never throw into
/// the caller: a notification failure should not fail the run itself.
/// </summary>
public interface IEmailNotificationService
{
    /// <summary>
    /// Sends a summary email for the supplied run when email is enabled and (optionally) when the
    /// run has failures. Returns silently when disabled or suppressed.
    /// </summary>
    /// <param name="recipientsOverride">
    /// When provided (and non-empty), these recipients are used instead of the configured
    /// <c>SendGrid:ToEmails</c> — e.g. addresses supplied by the Fabric pipeline for one run.
    /// </param>
    Task SendRunSummaryAsync(
        SanityCheckRun run,
        IReadOnlyList<string>? recipientsOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an operational alert to the configured admin recipients when the job <em>workflow</em>
    /// itself fails (e.g. bad pipeline input, auth/discovery error, headless browser failure, or a
    /// persistence failure) — the conditions that would otherwise be invisible because Log Analytics
    /// is disabled for the Container Apps Job. This is distinct from a Power BI report breaking,
    /// which is reported through <see cref="SendRunSummaryAsync"/>. Never throws into the caller.
    /// </summary>
    /// <param name="stage">The workflow stage that failed (e.g. "Input validation", "Report discovery").</param>
    /// <param name="error">The exception or error detail that caused the failure.</param>
    /// <param name="context">
    /// Optional diagnostic key/value pairs (e.g. the raw WorkspaceId/Environment/ToEmails inputs) shown
    /// in the alert with whitespace made visible so hidden characters like <c>\n</c> are obvious.
    /// </param>
    Task SendJobFailureAsync(
        string stage,
        string error,
        IReadOnlyDictionary<string, string?>? context = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Composes an HTML summary of a sanity-check run and delivers it through SendGrid. Configuration
/// comes from the "SendGrid" section; the API key is expected to be provided via App Service
/// Application Settings (SendGrid__ApiKey) or Key Vault rather than appsettings.json.
/// </summary>
public sealed class SendGridEmailNotificationService : IEmailNotificationService
{
    // Matches the UI's High PLT threshold (Components/Pages/PowerBiSanityCheck.razor) so the email
    // and the page agree on what "slow" means.
    private const long HighPltThresholdMs = 60_000;

    private readonly EmailOptions _options;
    private readonly ILogger<SendGridEmailNotificationService> _logger;

    public SendGridEmailNotificationService(
        IOptions<EmailOptions> options,
        ILogger<SendGridEmailNotificationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendRunSummaryAsync(
        SanityCheckRun run,
        IReadOnlyList<string>? recipientsOverride = null,
        CancellationToken cancellationToken = default)
    {
        // Pipeline-supplied recipients take precedence over the configured ToEmails for this run.
        var recipients = (recipientsOverride is { Count: > 0 } ? recipientsOverride : _options.ToEmails)
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Select(static a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var canSend =
            !string.IsNullOrWhiteSpace(_options.ApiKey)
            && !string.IsNullOrWhiteSpace(_options.FromEmail)
            && recipients.Count > 0;

        if (!canSend)
        {
            _logger.LogInformation(
                "Email notifications are not configured (missing SendGrid API key, sender, or recipients); skipping run {RunId}.",
                run.RunId);
            return;
        }

        if (_options.SendOnlyOnFailure && !run.HasFailures)
        {
            _logger.LogInformation(
                "Run {RunId} passed and SendOnlyOnFailure is enabled; skipping email.", run.RunId);
            return;
        }

        try
        {
            var client = new SendGridClient(_options.ApiKey);

            var message = new SendGridMessage
            {
                From = new EmailAddress(_options.FromEmail, _options.FromName),
                Subject = BuildSubject(run),
                HtmlContent = BuildHtmlBody(run),
                PlainTextContent = BuildPlainTextBody(run)
            };

            foreach (var address in recipients)
            {
                message.AddTo(new EmailAddress(address));
            }

            var response = await client.SendEmailAsync(message, cancellationToken);

            if (IsSuccess(response.StatusCode))
            {
                _logger.LogInformation(
                    "Sent sanity run {RunId} summary email to {Recipients} (status {Status}).",
                    run.RunId, string.Join(", ", recipients), (int)response.StatusCode);
            }
            else
            {
                var body = await response.Body.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "SendGrid rejected the run {RunId} summary email: {Status} {Body}",
                    run.RunId, (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // Never let a notification failure bubble up and fail the run.
            _logger.LogError(ex, "Failed to send the sanity run {RunId} summary email.", run.RunId);
        }
    }

    public async Task SendJobFailureAsync(
        string stage,
        string error,
        IReadOnlyDictionary<string, string?>? context = null,
        CancellationToken cancellationToken = default)
    {
        var recipients = _options.GetAdminRecipients();

        var canSend =
            !string.IsNullOrWhiteSpace(_options.ApiKey)
            && !string.IsNullOrWhiteSpace(_options.FromEmail)
            && recipients.Count > 0;

        if (!canSend)
        {
            // Log at Error so the failure is still visible in container stdout even when email is off.
            _logger.LogError(
                "Job workflow failed at stage '{Stage}' but email is not configured to alert admins: {Error}",
                stage, error);
            return;
        }

        try
        {
            var client = new SendGridClient(_options.ApiKey);

            var message = new SendGridMessage
            {
                From = new EmailAddress(_options.FromEmail, _options.FromName),
                Subject = $"\u274C Report Sanity Check JOB FAILED - {stage}",
                HtmlContent = BuildJobFailureHtmlBody(stage, error, context),
                PlainTextContent = BuildJobFailurePlainTextBody(stage, error, context)
            };

            foreach (var address in recipients)
            {
                message.AddTo(new EmailAddress(address));
            }

            var response = await client.SendEmailAsync(message, cancellationToken);

            if (IsSuccess(response.StatusCode))
            {
                _logger.LogInformation(
                    "Sent job failure alert (stage '{Stage}') to {Recipients} (status {Status}).",
                    stage, string.Join(", ", recipients), (int)response.StatusCode);
            }
            else
            {
                var body = await response.Body.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "SendGrid rejected the job failure alert (stage '{Stage}'): {Status} {Body}",
                    stage, (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // Never let the alert itself throw; the caller is already handling a failure.
            _logger.LogError(ex, "Failed to send the job failure alert for stage '{Stage}'.", stage);
        }
    }

    private static bool IsSuccess(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and < 300;

    private static string BuildSubject(SanityCheckRun run) =>
        $"Report Sanity check - {WorkspaceLabel(run)}";

    private string BuildHtmlBody(SanityCheckRun run)
    {
        var sb = new StringBuilder();
        var headerColor = run.HasFailures ? "#b3261e" : "#1e7e34";
        var headline = run.HasFailures
            ? $"{run.FailedCount} of {run.TotalReports} report(s) need attention"
            : $"All {run.TotalReports} report(s) healthy";

        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;color:#212529;\">");
        sb.Append($"<h2 style=\"color:{headerColor};margin-bottom:12px;\">{Encode(headline)}</h2>");

        // Summary header table: workspace, counts, timing, and run id.
        sb.Append("<table style=\"border-collapse:collapse;font-size:13px;margin-bottom:20px;border:1px solid #dee2e6;\">");
        AppendHeaderRow(sb, "Workspace Name", WorkspaceLabel(run), "#212529");
        AppendHeaderRow(sb, "Total", run.TotalReports.ToString(), "#212529");
        AppendHeaderRow(sb, "Passed", run.PassedCount.ToString(), "#1e7e34");
        AppendHeaderRow(sb, "Failed", run.FailedCount.ToString(), run.FailedCount > 0 ? "#b3261e" : "#6c757d");
        AppendHeaderRow(sb, "Started On", $"{run.StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC", "#212529");
        AppendHeaderRow(
            sb,
            "Completed On",
            run.CompletedAtUtc is { } completed ? $"{completed:yyyy-MM-dd HH:mm:ss} UTC" : "\u2014",
            "#212529");
        AppendHeaderRow(sb, "Run Id", run.RunId, "#6c757d");
        sb.Append("</table>");

        // Per-report detail table.
        sb.Append("<table style=\"border-collapse:collapse;width:100%;font-size:13px;\">");
        sb.Append("<thead><tr style=\"background:#f1f3f5;text-align:left;\">");
        AppendHeaderCell(sb, "Report");
        AppendHeaderCell(sb, "Status");
        AppendHeaderCell(sb, "Slowest load");
        AppendHeaderCell(sb, "Details");
        sb.Append("</tr></thead><tbody>");

        foreach (var result in run.Results)
        {
            var (label, color) = StatusBadge(result.Status);
            var highPlt = result.MaxBookmarkDurationMs >= HighPltThresholdMs;
            var loadText = FormatSeconds(result.MaxBookmarkDurationMs);
            if (highPlt)
            {
                loadText += " \u26A0\uFE0F High PLT";
            }

            sb.Append("<tr style=\"border-bottom:1px solid #e9ecef;\">");
            sb.Append($"<td style=\"padding:2px 2px;\">{Encode(result.ReportName)}</td>");
            sb.Append($"<td style=\"padding:2px 2px;color:{color};font-weight:600;\">{label}</td>");
            sb.Append($"<td style=\"padding:2px 2px;color:{(highPlt ? "#b35c00" : "#212529")};\">{Encode(loadText)}</td>");
            sb.Append($"<td style=\"padding:2px 2px;color:#6c757d;\">{BuildDetailHtml(result)}</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");

        AppendDrillThroughDiagnostics(sb, run);

        sb.Append("<p style=\"margin-top:16px;color:#adb5bd;font-size:12px;\">Sent automatically by Reports Sanity Check.</p>");
        sb.Append("</div>");

        return sb.ToString();
    }

    /// <summary>
    /// Diagnostic section: lists, per report, the drill-through destination pages (hidden pages) that
    /// exist and whether each was actually opened by the drill-through pass. This makes it obvious when a
    /// report has drill-through pages we are NOT exercising, so coverage gaps can be spotted at a glance.
    /// </summary>
    private static void AppendDrillThroughDiagnostics(StringBuilder sb, SanityCheckRun run)
    {
        // Include every report that DECLARES drill-through targets in its metadata (the authoritative map),
        // any report with hidden destination pages, and any report where data points were actually probed,
        // so the right-click audit is shown even when no target was ever discovered.
        // Hidden pages alone are NOT enough to show this section: when the drill-through pass is disabled
        // nothing was collected, and a report with hidden pages would otherwise render an empty/misleading
        // map. Require actual drill-through findings.
        var reportsWithDrill = run.Results
            .Where(r => r.DrillThrough.DeclaredTargets.Count > 0
                        || r.DrillThrough.Probes.Count > 0)
            .ToList();

        if (reportsWithDrill.Count == 0)
        {
            return;
        }

        sb.Append("<h3 style=\"margin-top:24px;font-size:15px;color:#212529;\">Drill-through map</h3>");
        sb.Append("<p style=\"margin:4px 0;color:#6c757d;font-size:12px;\">Drill-through targets are read from each report's definition metadata (workspace-member access), then each destination page is opened in reading mode with its bound field(s) as filter context. \u2713 = rendered without visual errors; \u2717 = rendered with errors; \u26A0 = opened but drill-through filter context not applied (partial); \u2014 = declared but not verified.</p>");

        foreach (var result in reportsWithDrill)
        {
            var d = result.DrillThrough;

            sb.Append($"<p style=\"margin:10px 0 2px;font-weight:600;font-size:13px;\">{Encode(result.ReportName)}</p>");

            if (d.DeclaredTargets.Count == 0)
            {
                sb.Append("<p style=\"margin:0 0 2px 18px;color:#6c757d;font-size:12px;\">No drill-through targets are declared in this report's metadata.</p>");
                AppendHiddenPageVerdicts(sb, result);
            }
            else
            {
                var verified = d.DeclaredTargets.Count(t => t.Status == SanityStatus.Passed);
                var failed = d.DeclaredTargets.Count(t => t.Status == SanityStatus.Failed);
                var skipped = d.DeclaredTargets.Count(t => t.Status is not SanityStatus.Passed and not SanityStatus.Failed);

                sb.Append("<p style=\"margin:0 0 2px 18px;color:#6c757d;font-size:12px;\">");
                sb.Append($"Declared targets: {d.DeclaredTargets.Count}; {verified} verified OK, {failed} with errors, {skipped} not verified.");
                if (d.CapReached)
                {
                    sb.Append(" <span style=\"color:#b35c00;font-weight:600;\">\u26A0\uFE0F cap reached \u2014 coverage may be incomplete.</span>");
                }
                sb.Append("</p>");

                sb.Append("<ul style=\"margin:2px 0 8px 18px;padding:0;font-size:13px;color:#495057;\">");
                foreach (var target in d.DeclaredTargets)
                {
                    var (mark, markColor) = target.Status switch
                    {
                        SanityStatus.Passed => ("\u2713", "#2f9e44"),
                        SanityStatus.Failed => ("\u2717", "#c92a2a"),
                        _ => ("\u2014", "#adb5bd")
                    };

                    var pageLabel = string.IsNullOrWhiteSpace(target.PageDisplayName) ? target.PageName : target.PageDisplayName;
                    var methodBadge = target.VerificationMethod switch
                    {
                        DrillThroughVerificationMethod.RealGesture => " <span style=\"color:#2b8a3e;font-size:11px;\">(right-click gesture)</span>",
                        DrillThroughVerificationMethod.MetadataFallback => " <span style=\"color:#e8590c;font-size:11px;\">(metadata fallback)</span>",
                        _ => string.Empty
                    };
                    sb.Append($"<li><span style=\"color:{markColor};font-weight:600;\">{mark}</span> {Encode(pageLabel)} <span style=\"color:#868e96;\">[{Encode(target.FieldSummary)}]</span>{methodBadge}");

                    if (!string.IsNullOrWhiteSpace(target.Message))
                    {
                        sb.Append($"<br/><span style=\"color:#6c757d;font-size:12px;\">{Encode(target.Message)}</span>");
                    }

                    if (target.Errors.Count > 0)
                    {
                        sb.Append("<ul style=\"margin:2px 0 2px 12px;padding:0;color:#c92a2a;font-size:12px;\">");
                        foreach (var err in target.Errors)
                        {
                            var visual = string.IsNullOrWhiteSpace(err.Visual) ? "visual" : err.Visual;
                            sb.Append($"<li>{Encode(visual)}: {Encode(err.Message ?? "Unknown visual error.")}</li>");
                        }
                        sb.Append("</ul>");
                    }

                    sb.Append("</li>");
                }
                sb.Append("</ul>");
            }

            // The "Force-open fallback: N hidden destination page(s) present" note is fully superseded by
            // the per-page verdict list rendered above, so drop it to avoid saying the same thing twice.
            var hasHiddenVerdicts = d.DeclaredTargets.Count == 0 && result.Pages.Any(p => p.IsHidden);
            foreach (var note in d.Notes)
            {
                if (hasHiddenVerdicts &&
                    note.Contains("Force-open fallback", StringComparison.OrdinalIgnoreCase) &&
                    note.Contains("present", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sb.Append($"<p style=\"margin:0 0 2px 18px;color:#6c757d;font-size:12px;\">Note: {Encode(note)}</p>");
            }

            AppendDrillThroughSources(sb, d);
            AppendDrillThroughProbes(sb, d);
        }
    }

    /// <summary>
    /// Renders which visuals were identified as drill-through SOURCES and the first data row of each,
    /// i.e. the exact cell a right-click must land on. Sources are matched generically: a visual qualifies
    /// when its own fields include every field a declared destination page filters on.
    /// </summary>
    private static void AppendDrillThroughSources(StringBuilder sb, DrillThroughDiagnostics d)
    {
        if (d.SourceVisuals.Count == 0)
        {
            return;
        }

        var sources = d.SourceVisuals.Where(v => v.IsDrillThroughSource).ToList();

        sb.Append("<p style=\"margin:8px 0 4px 18px;font-size:13px;font-weight:600;\">Drill-through source visuals</p>");

        if (sources.Count == 0)
        {
            var empty = d.SourceVisuals.Count(v => v.RowCount == 0);
            sb.Append(
                $"<p style=\"margin:0 0 6px 18px;color:#b02a37;font-size:12px;\">" +
                $"No source visual matched the declared drill-through fields. Probed {d.SourceVisuals.Count} visual(s); " +
                $"{empty} returned zero data rows.</p>");
        }

        sb.Append("<table style=\"margin:0 0 10px 18px;border-collapse:collapse;font-size:12px;\">");
        sb.Append(
            "<tr>" +
            "<th style=\"text-align:left;padding:3px 8px;border-bottom:1px solid #dee2e6;\">Page</th>" +
            "<th style=\"text-align:left;padding:3px 8px;border-bottom:1px solid #dee2e6;\">Visual</th>" +
            "<th style=\"text-align:left;padding:3px 8px;border-bottom:1px solid #dee2e6;\">Type</th>" +
            "<th style=\"text-align:left;padding:3px 8px;border-bottom:1px solid #dee2e6;\">Source?</th>" +
            "<th style=\"text-align:left;padding:3px 8px;border-bottom:1px solid #dee2e6;\">Detected by</th>" +
            "<th style=\"text-align:left;padding:3px 8px;border-bottom:1px solid #dee2e6;\">Rows</th>" +
            "<th style=\"text-align:left;padding:3px 8px;border-bottom:1px solid #dee2e6;\">First data row (right-click here)</th>" +
            "</tr>");

        // Confirmed sources first so the actionable rows are immediately visible.
        foreach (var v in d.SourceVisuals.OrderByDescending(v => v.IsDrillThroughSource).ThenBy(v => v.Title))
        {
            var flag = v.IsDrillThroughSource
                ? "<span style=\"color:#146c43;font-weight:600;\">YES</span>"
                : "<span style=\"color:#6c757d;\">no</span>";

            var row = v.FirstRow.Count > 0
                ? Encode(v.FirstRowSummary)
                : v.DeclaredFields.Count > 0
                    ? $"<span style=\"color:#6c757d;\">fields: {Encode(string.Join(", ", v.DeclaredFields.Select(f => $"{f.Table}.{f.Column}")))}</span>"
                    : $"<span style=\"color:#b02a37;\">{Encode(v.Error ?? "no data rows")}</span>";

            sb.Append(
                "<tr>" +
                $"<td style=\"padding:3px 8px;border-bottom:1px solid #f1f3f5;\">{Encode(v.Page)}</td>" +
                $"<td style=\"padding:3px 8px;border-bottom:1px solid #f1f3f5;\">{Encode(v.Title)}</td>" +
                $"<td style=\"padding:3px 8px;border-bottom:1px solid #f1f3f5;\">{Encode(v.VisualType)}</td>" +
                $"<td style=\"padding:3px 8px;border-bottom:1px solid #f1f3f5;\">{flag}</td>" +
                $"<td style=\"padding:3px 8px;border-bottom:1px solid #f1f3f5;\">{Encode(v.DetectedBy.Count > 0 ? string.Join(" + ", v.DetectedBy) : "-")}</td>" +
                $"<td style=\"padding:3px 8px;border-bottom:1px solid #f1f3f5;\">{v.RowCount}</td>" +
                $"<td style=\"padding:3px 8px;border-bottom:1px solid #f1f3f5;\">{row}</td>" +
                "</tr>");
        }

        sb.Append("</table>");
    }

    /// <summary>
    /// Renders the right-click audit trail: every visual that was probed, the context-menu items that
    /// actually appeared, and the drill-through destinations offered. Visuals that DO offer drill-through
    /// are listed first so coverage can be verified against the report at a glance.
    /// </summary>
    private static void AppendDrillThroughProbes(StringBuilder sb, DrillThroughDiagnostics d)
    {
        if (d.Probes.Count == 0)
        {
            return;
        }

        // Collapse repeated probes of the same visual: one row per (page, visual, element kind).
        var byVisual = d.Probes
            .GroupBy(p => (p.Page, p.Visual, p.ElementKind))
            .Select(g => new
            {
                g.Key.Page,
                g.Key.Visual,
                g.Key.ElementKind,
                HasDrill = g.Any(x => x.HasDrillThrough),
                Targets = g.SelectMany(x => x.Targets).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Fields = g.Where(x => !string.IsNullOrWhiteSpace(x.FieldRef))
                          .Select(x => x.FieldRef!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MenuItems = g.SelectMany(x => x.MenuItems).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            })
            .OrderByDescending(x => x.HasDrill)
            .ThenBy(x => x.Page, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Visual, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var withDrill = byVisual.Count(x => x.HasDrill);

        sb.Append("<p style=\"margin:10px 0 2px 18px;font-weight:600;font-size:12px;color:#495057;\">");
        sb.Append($"Right-click probe audit: {byVisual.Count} visual/element combination(s) probed, {withDrill} offered drill-through.");
        sb.Append("</p>");

        sb.Append("<table style=\"margin:2px 0 8px 18px;border-collapse:collapse;font-size:12px;color:#495057;\">");
        sb.Append("<tr style=\"background:#f1f3f5;\">");
        sb.Append("<th style=\"text-align:left;padding:3px 8px;border:1px solid #dee2e6;\">Page</th>");
        sb.Append("<th style=\"text-align:left;padding:3px 8px;border:1px solid #dee2e6;\">Visual</th>");
        sb.Append("<th style=\"text-align:left;padding:3px 8px;border:1px solid #dee2e6;\">Element</th>");
        sb.Append("<th style=\"text-align:left;padding:3px 8px;border:1px solid #dee2e6;\">Drill through?</th>");
        sb.Append("<th style=\"text-align:left;padding:3px 8px;border:1px solid #dee2e6;\">Destination pages</th>");
        sb.Append("</tr>");

        foreach (var v in byVisual)
        {
            var (mark, markColor) = v.HasDrill ? ("\u2713 yes", "#2f9e44") : ("\u2014 no", "#adb5bd");
            var targets = v.Targets.Count > 0 ? string.Join(", ", v.Targets) : "\u2014";

            sb.Append("<tr>");
            sb.Append($"<td style=\"padding:3px 8px;border:1px solid #dee2e6;\">{Encode(v.Page)}</td>");
            sb.Append($"<td style=\"padding:3px 8px;border:1px solid #dee2e6;\">{Encode(v.Visual)}</td>");
            sb.Append($"<td style=\"padding:3px 8px;border:1px solid #dee2e6;\">{Encode(v.ElementKind)}</td>");
            sb.Append($"<td style=\"padding:3px 8px;border:1px solid #dee2e6;color:{markColor};font-weight:600;\">{mark}</td>");
            sb.Append($"<td style=\"padding:3px 8px;border:1px solid #dee2e6;\">{Encode(targets)}</td>");
            sb.Append("</tr>");

            // For visuals with NO drill-through, show the menu we actually got. If this reads like a sort
            // or header menu, the right-click landed on the wrong element rather than the visual lacking
            // a drill-through.
            if (!v.HasDrill && v.MenuItems.Count > 0)
            {
                sb.Append("<tr><td colspan=\"5\" style=\"padding:2px 8px 6px 20px;border:1px solid #dee2e6;color:#868e96;font-size:11px;\">");
                sb.Append($"Menu seen: {Encode(string.Join(" | ", v.MenuItems))}");
                if (v.Fields.Count > 0)
                {
                    sb.Append($"<br/>Fields probed: {Encode(string.Join(", ", v.Fields))}");
                }
                sb.Append("</td></tr>");
            }
        }

        sb.Append("</table>");
    }

    /// <summary>
    /// When a report declares no drill-through targets in its metadata but has hidden destination pages,
    /// surface the actual force-open verdict for each hidden page (opened clean / rendered with errors /
    /// not opened) instead of only noting that the pages are "present". This closes the coverage gap so
    /// the drill-through map shows a real ✓/✗/— verdict for fallback-exercised pages.
    /// </summary>
    private static void AppendHiddenPageVerdicts(StringBuilder sb, ReportSanityResult result)
    {
        var hiddenPages = result.Pages.Where(p => p.IsHidden).ToList();
        if (hiddenPages.Count == 0)
        {
            return;
        }

        // The force-open fallback records each attempt as a DrillThrough interaction whose Page is the
        // hidden page's display name. Match on that to recover the verdict for each hidden page.
        InteractionCheckResult? FindVerdict(PageCheckResult p)
        {
            var display = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Name : p.DisplayName;
            return result.Interactions.FirstOrDefault(i =>
                i.Kind == InteractionKind.DrillThrough &&
                (string.Equals(i.Page, display, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(i.Page, p.Name, StringComparison.OrdinalIgnoreCase) ||
                 (i.Target?.Contains(display, StringComparison.OrdinalIgnoreCase) == true)));
        }

        sb.Append("<p style=\"margin:6px 0 2px 18px;color:#6c757d;font-size:12px;\">Hidden destination page(s) exercised via force-open fallback:</p>");
        sb.Append("<ul style=\"margin:2px 0 8px 18px;padding:0;font-size:13px;color:#495057;\">");
        foreach (var p in hiddenPages)
        {
            var display = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Name : p.DisplayName;
            var verdict = FindVerdict(p);
            var status = verdict?.Status ?? SanityStatus.Pending;

            // A page that opened clean but whose REQUIRED drill-through parameter(s) were never resolved was
            // not exercised through its true drill-through filter context, so it is only PARTIALLY verified.
            // Distinguish that from a full pass so a green ✓ always means "real drill-through verified".
            var partial = status == SanityStatus.Passed &&
                          verdict?.Message?.Contains("unresolved", StringComparison.OrdinalIgnoreCase) == true;

            var (mark, markColor) = (status, partial) switch
            {
                (SanityStatus.Passed, true) => ("\u26A0", "#b35c00"),
                (SanityStatus.Passed, false) => ("\u2713", "#2f9e44"),
                (SanityStatus.Failed, _) => ("\u2717", "#c92a2a"),
                _ => ("\u2014", "#adb5bd")
            };

            var partialBadge = partial
                ? " <span style=\"color:#b35c00;font-size:11px;\">(partial \u2014 filter context not applied)</span>"
                : string.Empty;

            sb.Append($"<li><span style=\"color:{markColor};font-weight:600;\">{mark}</span> {Encode(display)} <span style=\"color:#e8590c;font-size:11px;\">(force-open fallback)</span>{partialBadge}");

            if (!string.IsNullOrWhiteSpace(verdict?.Message))
            {
                sb.Append($"<br/><span style=\"color:#6c757d;font-size:12px;\">{Encode(verdict!.Message!)}</span>");
            }

            if (verdict is { Errors.Count: > 0 })
            {
                sb.Append("<ul style=\"margin:2px 0 2px 12px;padding:0;color:#c92a2a;font-size:12px;\">");
                foreach (var err in verdict.Errors)
                {
                    var visual = string.IsNullOrWhiteSpace(err.Visual) ? "visual" : err.Visual;
                    sb.Append($"<li>{Encode(visual)}: {Encode(err.Message ?? "Unknown visual error.")}</li>");
                }
                sb.Append("</ul>");
            }

            sb.Append("</li>");
        }
        sb.Append("</ul>");
    }

    private string BuildPlainTextBody(SanityCheckRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine(run.HasFailures
            ? $"{run.FailedCount} of {run.TotalReports} report(s) need attention."
            : $"All {run.TotalReports} report(s) healthy.");
        sb.AppendLine();
        sb.AppendLine($"Workspace Name: {WorkspaceLabel(run)}");
        sb.AppendLine($"Total: {run.TotalReports}");
        sb.AppendLine($"Passed: {run.PassedCount}");
        sb.AppendLine($"Failed: {run.FailedCount}");
        sb.AppendLine($"Started On: {run.StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Completed On: {(run.CompletedAtUtc is { } completed ? $"{completed:yyyy-MM-dd HH:mm:ss} UTC" : "\u2014")}");
        sb.AppendLine($"Run Id: {run.RunId}");
        sb.AppendLine();

        foreach (var result in run.Results)
        {
            var highPlt = result.MaxBookmarkDurationMs >= HighPltThresholdMs ? " [High PLT]" : string.Empty;
            sb.AppendLine(
                $"- {result.ReportName}: {result.Status} | slowest load {FormatSeconds(result.MaxBookmarkDurationMs)}{highPlt} | {BuildDetail(result)}");
        }

        // Drill-through map (plain text): list each report's declared drill-through targets and status.
        var reportsWithDrill = run.Results
            .Where(r => r.DrillThrough.DeclaredTargets.Count > 0
                        || r.DrillThrough.Probes.Count > 0)
            .ToList();
        if (reportsWithDrill.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Drill-through map [x = verified OK, ! = errors, blank = not verified]:");
            foreach (var result in reportsWithDrill)
            {
                var d = result.DrillThrough;
                sb.AppendLine($"  {result.ReportName}:");

                if (d.DeclaredTargets.Count == 0)
                {
                    sb.AppendLine("    No drill-through targets are declared in this report's metadata.");
                }
                else
                {
                    var verified = d.DeclaredTargets.Count(t => t.Status == SanityStatus.Passed);
                    var failed = d.DeclaredTargets.Count(t => t.Status == SanityStatus.Failed);
                    var skipped = d.DeclaredTargets.Count(t => t.Status is not SanityStatus.Passed and not SanityStatus.Failed);
                    sb.AppendLine($"    Declared targets: {d.DeclaredTargets.Count}; {verified} verified OK, {failed} with errors, {skipped} not verified{(d.CapReached ? " [CAP REACHED]" : string.Empty)}");

                    foreach (var target in d.DeclaredTargets)
                    {
                        var mark = target.Status switch
                        {
                            SanityStatus.Passed => "[x]",
                            SanityStatus.Failed => "[!]",
                            _ => "[ ]"
                        };
                        var pageLabel = string.IsNullOrWhiteSpace(target.PageDisplayName) ? target.PageName : target.PageDisplayName;
                        var methodBadge = target.VerificationMethod switch
                        {
                            DrillThroughVerificationMethod.RealGesture => " (right-click gesture)",
                            DrillThroughVerificationMethod.MetadataFallback => " (metadata fallback)",
                            _ => string.Empty
                        };
                        sb.AppendLine($"    {mark} {pageLabel} [{target.FieldSummary}]{methodBadge}");
                        if (!string.IsNullOrWhiteSpace(target.Message))
                        {
                            sb.AppendLine($"        {target.Message}");
                        }
                    }
                }

                foreach (var note in d.Notes)
                {
                    sb.AppendLine($"    Note: {note}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>The friendly workspace name when available, else the workspace id, else a placeholder.</summary>
    private static string WorkspaceLabel(SanityCheckRun run) =>
        !string.IsNullOrWhiteSpace(run.WorkspaceName)
            ? run.WorkspaceName!
            : !string.IsNullOrWhiteSpace(run.WorkspaceId)
                ? run.WorkspaceId!
                : "Unknown workspace";

    /// <summary>
    /// Builds specific, human-readable failure detail for a report: which visual (by name) failed and
    /// with what error (e.g. <c>QueryUserError</c>), and — for bookmark failures — the bookmark it
    /// happened on. Falls back to a healthy summary when there are no captured errors. Returns one
    /// entry per issue so the caller can render them on separate lines (HTML) or joined (plain text).
    /// </summary>
    private static IReadOnlyList<string> BuildDetailSegments(ReportSanityResult result)
    {
        var segments = new List<string>();

        // Report-level visual errors: name the specific visual (and page) plus the error code/message.
        foreach (var error in result.Errors)
        {
            segments.Add(DescribeVisualError(error, bookmarkFallback: null));
        }

        // Bookmark failures: attribute the failing visual to the bookmark it occurred on, by name.
        foreach (var bookmark in result.Bookmarks.Where(static b =>
                     b.Status is SanityStatus.Failed or SanityStatus.Timeout or SanityStatus.Error))
        {
            var label = BookmarkLabel(bookmark);

            if (bookmark.Errors.Count > 0)
            {
                foreach (var error in bookmark.Errors)
                {
                    segments.Add(DescribeVisualError(error, bookmarkFallback: label));
                }
            }
            else
            {
                var reason = !string.IsNullOrWhiteSpace(bookmark.Message)
                    ? bookmark.Message!.Trim()
                    : bookmark.Status.ToString();
                segments.Add($"Bookmark '{label}' failed: {reason}");
            }
        }

        // Page failures: hidden pages are usually drill-through destinations, so name the failing page
        // (and whether it was hidden) plus the specific visual that broke on it.
        foreach (var page in result.Pages.Where(static p =>
                     p.Status is SanityStatus.Failed or SanityStatus.Timeout or SanityStatus.Error))
        {
            var label = PageLabel(page);

            if (page.Errors.Count > 0)
            {
                foreach (var error in page.Errors)
                {
                    segments.Add(DescribeVisualError(error, bookmarkFallback: null));
                }
            }
            else
            {
                var reason = !string.IsNullOrWhiteSpace(page.Message)
                    ? page.Message!.Trim()
                    : page.Status.ToString();
                segments.Add($"Page '{label}' failed: {reason}");
            }
        }

        // Interaction failures: drill-through (from a table/matrix) and toggle-switch flips are DOM-driven
        // and not covered by bookmarks/pages, so name the interaction and the visual that broke under it.
        foreach (var interaction in result.Interactions.Where(static i =>
                     i.Status is SanityStatus.Failed or SanityStatus.Timeout or SanityStatus.Error))
        {
            var kind = interaction.Kind == InteractionKind.DrillThrough ? "Drill-through" : "Toggle";
            var target = !string.IsNullOrWhiteSpace(interaction.Target) ? $" '{interaction.Target!.Trim()}'" : string.Empty;

            if (interaction.Errors.Count > 0)
            {
                foreach (var error in interaction.Errors)
                {
                    segments.Add(DescribeVisualError(error, bookmarkFallback: $"{kind}{target}"));
                }
            }
            else
            {
                var reason = !string.IsNullOrWhiteSpace(interaction.Message)
                    ? interaction.Message!.Trim()
                    : interaction.Status.ToString();
                segments.Add($"{kind}{target} failed: {reason}");
            }
        }

        if (segments.Count > 0)
        {
            const int maxSegments = 8;
            if (segments.Count > maxSegments)
            {
                var trimmed = segments.Take(maxSegments).ToList();
                trimmed.Add($"+{segments.Count - maxSegments} more issue(s)");
                return trimmed;
            }

            return segments;
        }

        // No specific errors captured: surface the report message for non-passing states, else healthy.
        if (result.Status != SanityStatus.Passed && !string.IsNullOrWhiteSpace(result.Message))
        {
            return [result.Message!.Trim()];
        }

        var okParts = new List<string>();
        if (result.Pages.Count > 0)
        {
            okParts.Add($"{result.Pages.Count} page(s) OK");
        }
        if (result.Bookmarks.Count > 0)
        {
            okParts.Add($"{result.Bookmarks.Count} bookmark(s) OK");
        }

        var okInteractions = result.Interactions.Count(static i => i.Status == SanityStatus.Passed);
        if (okInteractions > 0)
        {
            okParts.Add($"{okInteractions} interaction(s) OK");
        }

        return okParts.Count > 0 ? [string.Join(", ", okParts)] : ["Rendered OK"];
    }

    /// <summary>Joins the detail segments into a single line for plain-text output.</summary>
    private static string BuildDetail(ReportSanityResult result) =>
        string.Join("; ", BuildDetailSegments(result));

    /// <summary>Renders the detail segments as separate lines for the HTML email cell.</summary>
    private static string BuildDetailHtml(ReportSanityResult result) =>
        string.Join("<br/>", BuildDetailSegments(result).Select(Encode));

    /// <summary>
    /// Formats a single visual error as "Bookmark 'B': visual 'V' on page 'P' failed: {reason}",
    /// omitting any part that is unknown. The reason prefers the SDK error code/message
    /// (e.g. <c>QueryUserError</c>) and falls back to the detailed message.
    /// </summary>
    private static string DescribeVisualError(VisualError error, string? bookmarkFallback)
    {
        var reason = FirstNonBlank(error.Message, error.DetailedMessage) ?? "error";

        var where = !string.IsNullOrWhiteSpace(error.Visual)
            ? $"visual '{error.Visual!.Trim()}'"
            : "a visual";

        if (!string.IsNullOrWhiteSpace(error.Page))
        {
            where += $" on page '{error.Page!.Trim()}'";
        }

        // The error may already be stamped with its bookmark (set in the browser); fall back to the
        // bookmark supplied by the caller when it isn't.
        var bookmark = FirstNonBlank(error.Bookmark, bookmarkFallback);
        var prefix = !string.IsNullOrWhiteSpace(bookmark) ? $"Bookmark '{bookmark}': " : string.Empty;

        return $"{prefix}{where} failed: {reason}";
    }

    /// <summary>The bookmark's friendly display name, then its id, then a generic label.</summary>
    private static string BookmarkLabel(BookmarkCheckResult bookmark) =>
        !string.IsNullOrWhiteSpace(bookmark.DisplayName)
            ? bookmark.DisplayName!.Trim()
            : !string.IsNullOrWhiteSpace(bookmark.Name)
                ? bookmark.Name.Trim()
                : "bookmark";

    /// <summary>The page's friendly display name (then id), suffixed with "(hidden)" for drill-through pages.</summary>
    private static string PageLabel(PageCheckResult page)
    {
        var name = !string.IsNullOrWhiteSpace(page.DisplayName)
            ? page.DisplayName!.Trim()
            : !string.IsNullOrWhiteSpace(page.Name)
                ? page.Name.Trim()
                : "page";

        return page.IsHidden ? $"{name} (hidden)" : name;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static (string Label, string Color) StatusBadge(SanityStatus status) => status switch
    {
        SanityStatus.Passed => ("Passed", "#1e7e34"),
        SanityStatus.Failed => ("Failed", "#b3261e"),
        SanityStatus.Timeout => ("Timeout", "#b35c00"),
        SanityStatus.Error => ("Error", "#b3261e"),
        _ => (status.ToString(), "#6c757d")
    };

    private static void AppendHeaderRow(StringBuilder sb, string label, string value, string color)
    {
        sb.Append("<tr style=\"border-bottom:1px solid #e9ecef;\">");
        sb.Append($"<td style=\"padding:2px 2px;background:#f8f9fa;font-weight:600;color:#495057;white-space:nowrap;\">{Encode(label)}</td>");
        sb.Append($"<td style=\"padding:2px 2px;color:{color};\">{Encode(value)}</td>");
        sb.Append("</tr>");
    }

    private static void AppendHeaderCell(StringBuilder sb, string text) =>
        sb.Append($"<th style=\"padding:2px 2px;border-bottom:2px solid #dee2e6;\">{Encode(text)}</th>");

    private static string FormatSeconds(long durationMs) =>
        durationMs <= 0 ? "\u2014" : $"{durationMs / 1000.0:0.0} s";

    private string BuildJobFailureHtmlBody(
        string stage,
        string error,
        IReadOnlyDictionary<string, string?>? context)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;color:#212529;\">");
        sb.Append("<h2 style=\"color:#b3261e;margin-bottom:4px;\">Report Sanity Check job FAILED</h2>");
        sb.Append("<p style=\"margin-top:0;color:#6c757d;\">The Container Apps Job workflow could not complete. "
            + "This is a job/infrastructure failure, not a Power BI report breaking.</p>");

        sb.Append("<table style=\"border-collapse:collapse;font-size:14px;margin-top:8px;\">");
        AppendHeaderRow(sb, "Failed stage", stage, "#b3261e");
        AppendHeaderRow(sb, "Time (UTC)", $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}", "#212529");
        AppendHeaderRow(sb, "Machine", Environment.MachineName, "#212529");
        sb.Append("</table>");

        sb.Append("<h3 style=\"margin-bottom:4px;\">Error detail</h3>");
        sb.Append($"<pre style=\"background:#f8f9fa;border:1px solid #e9ecef;padding:8px;white-space:pre-wrap;"
            + $"word-break:break-word;font-size:12px;\">{Encode(error)}</pre>");

        if (context is { Count: > 0 })
        {
            sb.Append("<h3 style=\"margin-bottom:4px;\">Input diagnostics</h3>");
            sb.Append("<p style=\"margin-top:0;color:#6c757d;font-size:12px;\">Hidden whitespace is shown with "
                + "visible markers (e.g. <code>\\n</code>, <code>\\r</code>, <code>\\t</code>) to reveal characters "
                + "that break JSON-derived inputs.</p>");
            sb.Append("<table style=\"border-collapse:collapse;font-size:14px;\">");
            foreach (var (key, value) in context)
            {
                AppendHeaderRow(sb, key, VisualizeWhitespace(value), "#212529");
            }
            sb.Append("</table>");
        }

        sb.Append("<p style=\"margin-top:16px;color:#adb5bd;font-size:12px;\">Sent automatically by Reports Sanity Check.</p>");
        sb.Append("</div>");

        return sb.ToString();
    }

    private static string BuildJobFailurePlainTextBody(
        string stage,
        string error,
        IReadOnlyDictionary<string, string?>? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Report Sanity Check job FAILED.");
        sb.AppendLine("This is a job/infrastructure failure, not a Power BI report breaking.");
        sb.AppendLine();
        sb.AppendLine($"Failed stage: {stage}");
        sb.AppendLine($"Time (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine("Error detail:");
        sb.AppendLine(error);

        if (context is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Input diagnostics (hidden whitespace shown as \\n, \\r, \\t):");
            foreach (var (key, value) in context)
            {
                sb.AppendLine($"- {key}: {VisualizeWhitespace(value)}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a value with normally-invisible control characters made visible so hidden
    /// <c>\n</c>/<c>\r</c>/<c>\t</c> (the class of bug that previously caused opaque JSON-input
    /// failures) is obvious in an alert. Returns a placeholder for null/empty values.
    /// </summary>
    private static string VisualizeWhitespace(string? value)
    {
        if (value is null)
        {
            return "(null)";
        }

        if (value.Length == 0)
        {
            return "(empty)";
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString()
            });
        }

        return sb.ToString();
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
