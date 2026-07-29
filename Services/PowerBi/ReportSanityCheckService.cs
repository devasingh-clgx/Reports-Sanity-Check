using Microsoft.Extensions.Options;
using Reports_Sanity_Check.Services.Notifications;
using Reports_Sanity_Check.Services.PowerBi.Models;

namespace Reports_Sanity_Check.Services.PowerBi;

/// <summary>
/// Orchestrates a full sanity-check run: resolves the reports to check, hands embed configs to the
/// browser (via the page/JS interop), assembles per-report results, and persists the run.
///
/// The actual "did it render / any visual errors / all bookmarks ok" work happens in the browser
/// (powerbi-sanity.js) because Power BI rendering only occurs client-side. This service provides the
/// server-side pieces the page needs and records the outcome.
/// </summary>
public interface IReportSanityCheckService
{
    /// <summary>Resolves the reports to check and builds an embed config for each.</summary>
    /// <param name="workspaceId">
    /// Optional workspace GUID to target (e.g. a DEV/QA/UAT/PROD environment). When null/blank the
    /// configured default workspace is used.
    /// </param>
    Task<IReadOnlyList<EmbedConfig>> PrepareRunAsync(string? workspaceId = null, CancellationToken cancellationToken = default);

    /// <summary>How long the browser should wait for a report's first render before flagging a timeout.</summary>
    int RenderTimeoutSeconds { get; }

    /// <summary>How long the browser should wait for a re-render after applying a bookmark.</summary>
    int BookmarkApplyTimeoutSeconds { get; }

    /// <summary>Overall wall-clock budget for a single report's browser check.</summary>
    int OverallTimeoutSeconds { get; }

    /// <summary>Buffer added to the overall budget for the .NET JS-interop timeout.</summary>
    int InteropTimeoutBufferSeconds { get; }

    /// <summary>Whether bookmarks should be applied and re-checked for each report.</summary>
    bool CheckBookmarks { get; }

    /// <summary>Whether every page (including hidden drill-through pages) should be rendered and checked.</summary>
    bool CheckAllPages { get; }

    /// <summary>Maximum time to wait for a page to re-render after it is activated.</summary>
    int PageTimeoutSeconds { get; }

    /// <summary>Resolved upper bound on pages rendered per report (int.MaxValue = unlimited).</summary>
    int GetMaxPagesPerReport();

    /// <summary>Whether table/matrix drill-through should be driven via DOM interaction.</summary>
    bool CheckDrillThrough { get; }

    /// <summary>Whether toggle-style custom visuals (e.g. BENE.BIZ Toggle Switch) should be flipped and re-checked.</summary>
    bool CheckToggles { get; }

    /// <summary>Time to wait after an interaction for the report to settle/re-render.</summary>
    int InteractionSettleSeconds { get; }

    /// <summary>Resolved upper bound on drill-through interactions per report (int.MaxValue = unlimited).</summary>
    int GetMaxInteractionsPerReport();

    /// <summary>Resolved number of drill-through levels to follow (minimum 1).</summary>
    int GetMaxDrillThroughDepth();

    /// <summary>Context-menu text used to trigger drill-through.</summary>
    string DrillThroughMenuText { get; }

    /// <summary>Substring identifying toggle-style custom visuals by type/title.</summary>
    string ToggleVisualMatch { get; }

    /// <summary>DEBUG ONLY. Whether to dump the raw drill-through context-menu DOM into diagnostics/logs.</summary>
    bool DebugDrillThroughDom { get; }

    /// <summary>How many reports the browser may embed and check at the same time (1-10).</summary>
    int MaxParallelReports { get; }

    /// <summary>
    /// Builds a <see cref="ReportSanityResult"/> from the browser interop payload, attaching report
    /// identity from the embed config that was used.
    /// </summary>
    ReportSanityResult BuildResult(EmbedConfig embed, ReportCheckInteropResult interopResult);

    /// <summary>Persists a completed run and returns the saved location.</summary>
    /// <param name="recipientsOverride">
    /// Optional email recipients (e.g. supplied by the Fabric pipeline) that override the configured
    /// <c>SendGrid:ToEmails</c> for this run's summary email.
    /// </param>
    Task<string> SaveRunAsync(
        SanityCheckRun run,
        IReadOnlyList<string>? recipientsOverride = null,
        CancellationToken cancellationToken = default);
}

public sealed class ReportSanityCheckService : IReportSanityCheckService
{
    private readonly IPowerBiEmbedService _embedService;
    private readonly ISanityResultStore _resultStore;
    private readonly IEmailNotificationService _emailNotifier;
    private readonly PowerBiOptions _options;
    private readonly ILogger<ReportSanityCheckService> _logger;

    public ReportSanityCheckService(
        IPowerBiEmbedService embedService,
        ISanityResultStore resultStore,
        IEmailNotificationService emailNotifier,
        IOptions<PowerBiOptions> options,
        ILogger<ReportSanityCheckService> logger)
    {
        _embedService = embedService;
        _resultStore = resultStore;
        _emailNotifier = emailNotifier;
        _options = options.Value;
        _logger = logger;
    }

    public int RenderTimeoutSeconds => _options.RenderTimeoutSeconds;

    public int BookmarkApplyTimeoutSeconds => _options.BookmarkApplyTimeoutSeconds;

    public int OverallTimeoutSeconds => _options.OverallTimeoutSeconds;

    public int InteropTimeoutBufferSeconds => _options.InteropTimeoutBufferSeconds;

    public bool CheckBookmarks => _options.CheckBookmarks;

    public bool CheckAllPages => _options.CheckAllPages;

    public int PageTimeoutSeconds => _options.PageTimeoutSeconds;

    public int GetMaxPagesPerReport() => _options.GetMaxPagesPerReport();

    public bool CheckDrillThrough => _options.CheckDrillThrough;

    public bool CheckToggles => _options.CheckToggles;

    public int InteractionSettleSeconds => _options.InteractionSettleSeconds;

    public int GetMaxInteractionsPerReport() => _options.GetMaxInteractionsPerReport();

    public int GetMaxDrillThroughDepth() => _options.GetMaxDrillThroughDepth();

    public string DrillThroughMenuText => _options.DrillThroughMenuText;

    public string ToggleVisualMatch => _options.ToggleVisualMatch;

    public bool DebugDrillThroughDom => _options.DebugDrillThroughDom;

    public int MaxParallelReports => _options.GetMaxParallelReports();

    public async Task<IReadOnlyList<EmbedConfig>> PrepareRunAsync(string? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var reports = await _embedService.GetReportsToCheckAsync(workspaceId, cancellationToken);

        // DEBUG ONLY: when a debug report name is configured, filter the run down to just that single
        // report (matched case-insensitively by display name). This lets drill-through/toggle behaviour
        // be investigated on one report (e.g. "Cycle Time Report") without waiting for the whole workspace.
        var debugName = _options.DebugReportName;
        if (!string.IsNullOrWhiteSpace(debugName))
        {
            var filtered = reports
                .Where(r => string.Equals(r.DisplayName, debugName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filtered.Count == 0)
            {
                _logger.LogWarning(
                    "DebugReportName='{DebugName}' matched no report; the run will proceed with all {Total} discovered report(s).",
                    debugName, reports.Count);
            }
            else
            {
                _logger.LogWarning(
                    "DEBUG MODE: DebugReportName='{DebugName}' is limiting this run to {Count} matching report(s).",
                    debugName, filtered.Count);
                reports = filtered;
            }
        }

        // Optional rollout/debug throttle: cap the number of reports actually checked so a smoke-test
        // run finishes quickly instead of waiting for the full workspace. Applied before embed tokens
        // are generated, so skipped reports cost nothing. 0 = no limit.
        var limit = _options.GetMaxReportsToCheck();
        if (reports.Count > limit)
        {
            _logger.LogWarning(
                "MaxReportsToCheck={Limit} is capping this run to {Limit} of {Total} discovered report(s).",
                limit, limit, reports.Count);
            reports = reports.Take(limit).ToList();
        }

        _logger.LogInformation("Preparing sanity run for {Count} report(s).", reports.Count);

        var configs = new List<EmbedConfig>(reports.Count);
        foreach (var report in reports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var embed = await _embedService.GetEmbedConfigAsync(report, cancellationToken);

                // Both of these metadata reads exist ONLY to feed the drill-through pass. When drill-through
                // is disabled they are skipped entirely: they cost extra REST calls per report, and leaving
                // DrillThroughTargets populated would still render the drill-through email section.
                if (CheckDrillThrough)
                {
                    // Read the DECLARED drill-through targets from report metadata (workspace-member access),
                    // so the headless checker can open each destination page deterministically instead of
                    // guessing via DOM right-clicks. Never throws; empty when the report has none.
                    var drillThroughTargets = await _embedService.GetDrillThroughMapAsync(report, cancellationToken);
                    embed.DrillThroughTargets = drillThroughTargets.ToList();
                    embed.DrillThroughMapDiagnostic = _embedService.LastDrillThroughMapDiagnostic;

                    // Read every visual and the fields it projects, also from metadata. Combined with the
                    // targets above this identifies which visuals can raise each drill-through without
                    // needing the report to render or return any rows.
                    var visualDefinitions = await _embedService.GetVisualFieldMapAsync(report, cancellationToken);
                    embed.VisualDefinitions = visualDefinitions.ToList();
                }

                configs.Add(embed);
            }
            catch (Exception ex)
            {
                // A token/embed failure for one report shouldn't abort the whole run; surface it as a
                // failed result so it still shows up (and can be emailed about later).
                _logger.LogError(ex, "Failed to build embed config for report {ReportId}.", report.ReportId);

                configs.Add(new EmbedConfig
                {
                    ReportId = report.ReportId,
                    ReportName = report.DisplayName ?? report.ReportId,
                    WorkspaceId = report.WorkspaceId ?? workspaceId ?? string.Empty,
                    EmbedUrl = string.Empty,
                    EmbedToken = string.Empty,
                    TokenExpiry = DateTimeOffset.UtcNow
                });
            }
        }

        return configs;
    }

    public ReportSanityResult BuildResult(EmbedConfig embed, ReportCheckInteropResult interopResult)
    {
        // An embed config with no token means we never even got to the browser check.
        var status = string.IsNullOrEmpty(embed.EmbedToken)
            ? SanityStatus.Error
            : interopResult.Status;

        var message = string.IsNullOrEmpty(embed.EmbedToken)
            ? interopResult.Message ?? "Embed token could not be generated for this report."
            : interopResult.Message;

        // Carry the metadata-declared drill-through targets onto the persisted diagnostics so they are
        // always visible in the email, whether or not the browser pass verified each one.
        var drillThrough = interopResult.DrillThrough;
        if (embed.DrillThroughTargets.Count > 0 && drillThrough.DeclaredTargets.Count == 0)
        {
            drillThrough.DeclaredTargets = embed.DrillThroughTargets;
        }

        // When no targets were declared, record WHY so the email explains an empty map instead of
        // silently claiming the report has none.
        if (embed.DrillThroughTargets.Count == 0 && !string.IsNullOrWhiteSpace(embed.DrillThroughMapDiagnostic))
        {
            drillThrough.Notes.Add($"Declared drill-through map empty: {embed.DrillThroughMapDiagnostic}");
        }

        return new ReportSanityResult
        {
            ReportId = embed.ReportId,
            ReportName = embed.ReportName,
            WorkspaceId = embed.WorkspaceId,
            Status = status,
            DurationMs = interopResult.DurationMs,
            RenderDurationMs = interopResult.RenderDurationMs,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Message = message,
            Errors = interopResult.Errors,
            Bookmarks = interopResult.Bookmarks,
            Pages = interopResult.Pages,
            Interactions = interopResult.Interactions,
            DrillThrough = drillThrough
        };
    }

    public async Task<string> SaveRunAsync(
        SanityCheckRun run,
        IReadOnlyList<string>? recipientsOverride = null,
        CancellationToken cancellationToken = default)
    {
        run.CompletedAtUtc = DateTimeOffset.UtcNow;

        if (run.HasFailures)
        {
            _logger.LogWarning(
                "Sanity run {RunId} completed with {Failed} failing report(s) out of {Total}.",
                run.RunId, run.FailedCount, run.TotalReports);
        }
        else
        {
            _logger.LogInformation(
                "Sanity run {RunId} completed: all {Total} report(s) healthy.",
                run.RunId, run.TotalReports);
        }

        // Persist first so the saved file is the source of truth, then notify. The email service
        // never throws into the caller, so a notification failure can't fail the run.
        var savedPath = await _resultStore.SaveAsync(run, cancellationToken);

        await _emailNotifier.SendRunSummaryAsync(run, recipientsOverride, cancellationToken);

        return savedPath;
    }
}
