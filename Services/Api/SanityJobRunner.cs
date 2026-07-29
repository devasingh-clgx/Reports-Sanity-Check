using Reports_Sanity_Check.Services.Notifications;
using Reports_Sanity_Check.Services.PowerBi;
using Reports_Sanity_Check.Services.PowerBi.Models;

namespace Reports_Sanity_Check.Services.Api;

/// <summary>
/// Run-once entry point for the Azure Container Apps Job. Unlike the web/API path, a Job has no
/// ingress: it starts, runs the sanity check to completion, and exits. The web host is still started
/// briefly so the headless browser can load the app's own <c>headless-check.html</c> page on
/// localhost, then it is stopped and the process returns an exit code.
///
/// Exit codes intentionally treat only <em>workflow</em> failures as job failures:
/// <list type="bullet">
///   <item><description><c>0</c> — the workflow ran to completion. Individual Power BI reports may
///   have rendered with errors/timeouts; those are recorded in the run summary and are NOT job
///   failures.</description></item>
///   <item><description><c>1</c> — the workflow itself failed (bad input, no workspace resolved,
///   report discovery/auth error, headless browser failure, or a persistence failure). Because Log
///   Analytics is disabled for the Job, these failures also raise an email alert to the configured
///   <c>SendGrid:AdminEmails</c> so they can be tracked.</description></item>
/// </list>
///
/// The job is always triggered from the Fabric workspace, which passes parameters as env vars:
/// <c>WorkspaceId</c> (the target workspace), optional <c>ToEmails</c> (comma/semicolon separated
/// recipients), and the optional RLS effective identity <c>EffectiveIdentityUsername</c> plus
/// <c>EffectiveIdentityRoles</c> (comma/semicolon separated). Each falls back to appsettings.json for
/// local dev. Inputs are sanitized before use so hidden characters (e.g. a trailing <c>\n</c> from
/// JSON) can't cause opaque failures.
/// </summary>
public static class SanityJobRunner
{
    public static async Task<int> RunAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SanityJobRunner");

        // Hold the scope for the whole run so the (scoped) email notifier is still usable from the
        // catch block to raise an operational alert if the workflow throws.
        using var scope = app.Services.CreateScope();
        var emailNotifier = scope.ServiceProvider.GetRequiredService<IEmailNotificationService>();

        // Because Log Analytics is disabled for the Job, the admin email is the ONLY failure signal.
        // Log up front (at Error) when it can't be delivered so a silent "no email" run is diagnosable
        // from container stdout instead of looking like nothing ran.
        var emailOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>().Value;
        if (!emailOptions.CanSendAdminAlerts)
        {
            logger.LogError(
                "Admin failure alerts are NOT deliverable: {Reason}. Workflow failures will be logged here but no email will be sent.",
                emailOptions.DescribeAdminAlertGap());
        }

        // Keep the raw inputs so any failure alert can show them with hidden whitespace made visible.
        string? rawWorkspaceId = null;
        string? rawRecipients = null;
        string? rawIdentityUsername = null;
        string? rawIdentityRoles = null;
        string? rawMaxReports = null;
        var stage = "Startup";
        var hostStarted = false;

        try
        {
            var sanity = scope.ServiceProvider.GetRequiredService<IReportSanityCheckService>();
            var headless = scope.ServiceProvider.GetRequiredService<IHeadlessReportChecker>();
            var embed = scope.ServiceProvider.GetRequiredService<IPowerBiEmbedService>();
            var powerBi = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PowerBiOptions>>().Value;
            var api = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value;
            var config = app.Configuration;

            stage = "Input validation";

            rawWorkspaceId = config["WorkspaceId"];
            rawRecipients = config["ToEmails"];
            rawIdentityUsername = config["EffectiveIdentityUsername"];
            rawIdentityRoles = config["EffectiveIdentityRoles"];
            rawMaxReports = config["MaxReportsToCheck"];

            // WorkspaceId is supplied by Fabric per run; the config value is only a local-dev fallback.
            var workspaceId = Sanitize(rawWorkspaceId);
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                workspaceId = Sanitize(powerBi.WorkspaceId);
            }

            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                const string error = "No workspace resolved. The Fabric trigger must pass WorkspaceId (or set PowerBi:WorkspaceId for local dev).";
                logger.LogError("Job: {Error}", error);
                await emailNotifier.SendJobFailureAsync(stage, error, BuildInputContext(rawWorkspaceId, rawRecipients, rawIdentityUsername, rawIdentityRoles, rawMaxReports));
                return 1;
            }

            // A Power BI workspace (group) id is always a GUID. Validating it here turns the class of
            // bug that previously slipped through — a hidden trailing newline in the JSON input — into
            // a clear, actionable alert instead of an opaque downstream Power BI API error.
            if (!Guid.TryParse(workspaceId, out _))
            {
                var error =
                    $"Resolved WorkspaceId is not a valid GUID: '{workspaceId}'. Check the pipeline input " +
                    "for hidden characters such as a trailing newline (\\n), carriage return (\\r), or tab (\\t).";
                logger.LogError("Job: {Error}", error);
                await emailNotifier.SendJobFailureAsync(stage, error, BuildInputContext(rawWorkspaceId, rawRecipients, rawIdentityUsername, rawIdentityRoles, rawMaxReports));
                return 1;
            }

            var recipients = ParseRecipients(rawRecipients);

            // Apply the RLS effective-identity overrides passed from the Fabric pipeline onto the shared
            // options the embed service reads. Each field only overrides appsettings.json when supplied,
            // and is sanitized so a hidden \n can't corrupt the username or a role name.
            ApplyEffectiveIdentityOverrides(powerBi.EffectiveIdentity, rawIdentityUsername, rawIdentityRoles);

            // Apply the optional per-run report cap from Fabric (debug/rollout throttle) onto the shared
            // options the sanity service reads. Ignored unless a valid non-negative integer is supplied.
            ApplyMaxReportsOverride(powerBi, rawMaxReports, logger);

            // Preflight: verify the service principal can authenticate AND actually reach this workspace
            // in one cheap REST call, BEFORE starting the host or the long render loop. This turns what
            // was a silent failed run into a fast, actionable alert and also resolves the friendly name.
            stage = "Workspace access check";
            var access = await embed.VerifyWorkspaceAccessAsync(workspaceId);
            if (!access.HasAccess)
            {
                logger.LogError("Job: workspace access check failed: {Error}", access.Error);
                await emailNotifier.SendJobFailureAsync(stage, access.Error ?? "Workspace access check failed.", BuildInputContext(rawWorkspaceId, rawRecipients, rawIdentityUsername, rawIdentityRoles, rawMaxReports));
                return 1;
            }

            var workspaceName = FirstNonBlank(access.WorkspaceName) ?? workspaceId;
            var run = new SanityCheckRun { WorkspaceId = workspaceId, WorkspaceName = workspaceName };

            stage = "Report discovery";
            logger.LogInformation("Job sanity run starting for workspace {WorkspaceId} ({Name}).", workspaceId, workspaceName);
            var embeds = await sanity.PrepareRunAsync(workspaceId);

            if (embeds.Count == 0)
            {
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                await sanity.SaveRunAsync(run, recipients);
                logger.LogWarning("Job: no reports found in workspace {WorkspaceId}.", workspaceId);
                return 0;
            }

            var options = new HeadlessCheckOptions
            {
                RenderTimeoutMs = sanity.RenderTimeoutSeconds * 1000,
                BookmarkApplyTimeoutMs = sanity.BookmarkApplyTimeoutSeconds * 1000,
                OverallTimeoutMs = sanity.OverallTimeoutSeconds * 1000,
                CheckBookmarks = sanity.CheckBookmarks,
                CheckAllPages = sanity.CheckAllPages,
                PageTimeoutMs = sanity.PageTimeoutSeconds * 1000,
                MaxPagesPerReport = sanity.GetMaxPagesPerReport(),
                CheckDrillThrough = sanity.CheckDrillThrough,
                CheckToggles = sanity.CheckToggles,
                InteractionSettleMs = sanity.InteractionSettleSeconds * 1000,
                MaxInteractionsPerReport = sanity.GetMaxInteractionsPerReport(),
                MaxDrillThroughDepth = sanity.GetMaxDrillThroughDepth(),
                DrillThroughMenuText = sanity.DrillThroughMenuText,
                ToggleVisualMatch = sanity.ToggleVisualMatch,
                DebugDrillThroughDom = sanity.DebugDrillThroughDom,
                InteropBufferMs = sanity.InteropTimeoutBufferSeconds * 1000,
                MaxParallelReports = sanity.MaxParallelReports
            };

            var hostBaseUri = !string.IsNullOrWhiteSpace(api.HostBaseUrl) && Uri.TryCreate(api.HostBaseUrl, UriKind.Absolute, out var u)
                ? u
                : new Uri("http://localhost:8080/", UriKind.Absolute);

            // Start Kestrel now (not at the top) so the headless browser can reach headless-check.html
            // on localhost. Kept INSIDE the try so a bind/startup failure raises the admin alert instead
            // of crashing before the catch block — the original silent-failure cause.
            stage = "Host startup";
            await app.StartAsync();
            hostStarted = true;

            stage = "Headless render";
            var interop = await headless.CheckReportsAsync(hostBaseUri, embeds, options);
            for (var i = 0; i < embeds.Count; i++)
            {
                run.Results.Add(sanity.BuildResult(embeds[i], interop[i]));
            }

            stage = "Persist and notify";
            await sanity.SaveRunAsync(run, recipients);
            logger.LogInformation("Job sanity run {RunId} finished: {Passed}/{Total} passed.", run.RunId, run.PassedCount, run.TotalReports);

            // A report rendering with errors/timeouts is NOT a job failure — it is recorded in the run
            // summary. The job only fails (exit 1) when the workflow itself breaks (handled below).
            return 0;
        }
        catch (Exception ex)
        {
            // Any exception that reaches here is a workflow/infrastructure failure (not a single report
            // breaking, which HeadlessReportChecker already contains). Alert admins with full detail.
            logger.LogError(ex, "Job workflow failed at stage '{Stage}'.", stage);
            await emailNotifier.SendJobFailureAsync(stage, ex.ToString(), BuildInputContext(rawWorkspaceId, rawRecipients, rawIdentityUsername, rawIdentityRoles, rawMaxReports));
            return 1;
        }
        finally
        {
            // Only stop the host if it was actually started; a preflight failure returns before startup.
            // Shutdown must never change the job's outcome: the summary/failure email has already been sent
            // by this point, so a slow or throwing Kestrel/Playwright teardown must NOT flip a successful
            // run's exit code to non-zero (which the Container App Job would report as "failed"). Guard the
            // stop with a bounded timeout and swallow any error so the computed return code stands.
            if (hostStarted)
            {
                try
                {
                    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await app.StopAsync(stopCts.Token);
                }
                catch (Exception stopEx)
                {
                    logger.LogWarning(stopEx, "Host shutdown after the sanity run did not complete cleanly; ignoring so the job outcome is unaffected.");
                }
            }
        }
    }

    private static IReadOnlyList<string>? ParseRecipients(string? value) =>
        ParseDelimitedList(value, ignoreCase: true);

    /// <summary>
    /// Splits a comma/semicolon separated value into a sanitized, de-duplicated list, or null when the
    /// value is blank. Recipients are matched case-insensitively; RLS role names case-sensitively.
    /// </summary>
    private static List<string>? ParseDelimitedList(string? value, bool ignoreCase)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var cleaned = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Sanitize)
            .Where(static v => !string.IsNullOrWhiteSpace(v))
            .Select(static v => v!)
            .Distinct(comparer)
            .ToList();

        return cleaned.Count > 0 ? cleaned : null;
    }

    /// <summary>
    /// Applies the RLS effective-identity overrides supplied by the Fabric pipeline
    /// (<c>EffectiveIdentityUsername</c>, <c>EffectiveIdentityRoles</c>). Each field only overrides the
    /// appsettings.json value when actually provided, so local dev keeps its fallback. All values are
    /// sanitized so a hidden \n/\r/\t can't corrupt the username or a role name.
    /// </summary>
    private static void ApplyEffectiveIdentityOverrides(
        EffectiveIdentityOptions identity,
        string? username,
        string? roles)
    {
        var cleanUsername = Sanitize(username);
        if (!string.IsNullOrWhiteSpace(cleanUsername))
        {
            identity.Username = cleanUsername;
        }

        var parsedRoles = ParseDelimitedList(roles, ignoreCase: false);
        if (parsedRoles is not null)
        {
            identity.Roles = parsedRoles;
        }
    }

    /// <summary>
    /// Applies the optional <c>MaxReportsToCheck</c> override supplied by the Fabric pipeline onto the
    /// shared options. Only overrides appsettings.json when the value is a valid non-negative integer;
    /// a blank or malformed value is ignored (with a warning) so a bad parameter can't silently change
    /// behaviour. <c>0</c> means "no limit".
    /// </summary>
    private static void ApplyMaxReportsOverride(PowerBiOptions powerBi, string? rawMaxReports, ILogger logger)
    {
        var clean = Sanitize(rawMaxReports);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return;
        }

        if (int.TryParse(clean, out var max) && max >= 0)
        {
            powerBi.MaxReportsToCheck = max;
            logger.LogInformation("Fabric override: MaxReportsToCheck = {Max} ({Meaning}).", max, max == 0 ? "no limit" : "capped");
        }
        else
        {
            logger.LogWarning("Ignoring invalid MaxReportsToCheck parameter '{Raw}'; expected a non-negative integer.", clean);
        }
    }

    /// <summary>
    /// Removes control characters (newlines, carriage returns, tabs, etc.) that JSON-derived pipeline
    /// inputs sometimes carry and that otherwise cause opaque downstream failures, then trims
    /// surrounding whitespace. Null/empty values pass through unchanged.
    /// </summary>
    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var filtered = new string(value.Where(static ch => !char.IsControl(ch)).ToArray());
        return filtered.Trim();
    }

    /// <summary>Returns the first non-null, non-whitespace value (trimmed), or null when all are blank.</summary>
    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static v => !string.IsNullOrWhiteSpace(v))?.Trim();

    /// <summary>
    /// Builds the raw-input diagnostic map for a failure alert. The values are shown with hidden
    /// whitespace made visible by the email service so characters like <c>\n</c> are obvious.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> BuildInputContext(
        string? workspaceId,
        string? recipients,
        string? identityUsername,
        string? identityRoles,
        string? maxReports = null) =>
        new Dictionary<string, string?>
        {
            ["WorkspaceId (raw)"] = workspaceId,
            ["ToEmails (raw)"] = recipients,
            ["EffectiveIdentityUsername (raw)"] = identityUsername,
            ["EffectiveIdentityRoles (raw)"] = identityRoles,
            ["MaxReportsToCheck (raw)"] = maxReports
        };
}
