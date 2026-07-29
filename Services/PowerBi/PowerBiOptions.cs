namespace Reports_Sanity_Check.Services.PowerBi;

/// <summary>
/// Strongly-typed configuration for the Power BI sanity-check feature, bound from the "PowerBi"
/// section of appsettings.json. The service-principal secret (<see cref="ClientSecret"/>) is
/// expected to be supplied at runtime via <c>PowerBi__ClientSecret</c> (env var / Key Vault)
/// rather than appsettings.json.
/// </summary>
public sealed class PowerBiOptions
{
    public const string SectionName = "PowerBi";

    /// <summary>Microsoft Entra tenant id used to acquire the app-only token.</summary>
    public string? TenantId { get; set; }

    /// <summary>Service principal (application) id.</summary>
    public string? ClientId { get; set; }

    /// <summary>Service principal secret. Prefer user-secrets / Key Vault over appsettings.json.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Entra authority base url.</summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>The Power BI resource scope for the app-only token.</summary>
    public string Scope { get; set; } = "https://analysis.windows.net/powerbi/api/.default";

    /// <summary>Base url of the Power BI REST API.</summary>
    public string ApiUrl { get; set; } = "https://api.powerbi.com/";

    /// <summary>
    /// Base url of the Microsoft Fabric REST API, used to discover reports by folder (the Power BI
    /// REST report listing is flat and has no folder information).
    /// </summary>
    public string FabricApiUrl { get; set; } = "https://api.fabric.microsoft.com/";

    /// <summary>The Microsoft Fabric resource scope for the app-only token used by folder discovery.</summary>
    public string FabricScope { get; set; } = "https://api.fabric.microsoft.com/.default";

    /// <summary>
    /// Optional fallback Power BI workspace (group) GUID for local dev/testing only. In production the
    /// job is always triggered from the Fabric workspace, which supplies the workspace via the
    /// <c>WorkspaceId</c> job parameter, so this is normally left unset. The workspace's friendly name
    /// is resolved live from the id at runtime, so it never needs to be configured.
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// When true, every report in the target workspace is discovered automatically.
    /// When false, only the reports listed in <see cref="Reports"/> are checked.
    /// </summary>
    public bool AutoDiscoverReports { get; set; } = true;

    /// <summary>
    /// When set, auto-discovery only includes reports under this top-level workspace folder (and its
    /// nested sub-folders). Leave blank to include reports from anywhere in the workspace. Matched
    /// case-insensitively against the Fabric folder display name (e.g. "Reports"). Requires the
    /// Fabric REST API to be reachable; otherwise discovery falls back to the flat report listing.
    /// </summary>
    public string? IncludeReportsFolder { get; set; }

    /// <summary>
    /// Folder display names to exclude from auto-discovery even when nested under
    /// <see cref="IncludeReportsFolder"/> (e.g. "tests"). Matched case-insensitively.
    /// </summary>
    public List<string> ExcludeReportsFolders { get; set; } = new();

    /// <summary>
    /// How many reports to embed and check at the same time. Clamped to 1-10. The default of 5
    /// balances throughput against browser/circuit load; set to 1 for strictly sequential runs.
    /// </summary>
    public int MaxParallelReports { get; set; } = 5;

    /// <summary>
    /// Optional cap on how many discovered reports are actually checked in a run. <c>0</c> (the
    /// default) means no limit — every discovered report is checked. A positive value is a rollout/
    /// debug throttle so a run finishes quickly (e.g. set to 6 to smoke-test a deployment without
    /// waiting for all 30 reports). The cap is applied to the discovered list before embed tokens are
    /// generated, so the skipped reports incur no cost. Fabric can override this per run via the
    /// <c>MaxReportsToCheck</c> job parameter.
    /// </summary>
    public int MaxReportsToCheck { get; set; }

    /// <summary>Maximum time to wait for a report's first full render before flagging a timeout.</summary>
    public int RenderTimeoutSeconds { get; set; } = 120;

    /// <summary>Maximum time to wait for a re-render after applying a bookmark.</summary>
    public int BookmarkApplyTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Overall wall-clock budget for a single report's browser check (initial render plus every
    /// bookmark). A report that exceeds this is reported as a <c>Timeout</c> with its measured
    /// duration rather than being abandoned. Defaults to Power BI's official 4-minute visual load
    /// ceiling. The .NET JS-interop call is given this budget plus <see cref="InteropTimeoutBufferSeconds"/>
    /// so the browser always returns a result before .NET cancels the interop call.
    /// </summary>
    public int OverallTimeoutSeconds { get; set; } = 240;

    /// <summary>
    /// Extra time added on top of <see cref="OverallTimeoutSeconds"/> for the .NET JS-interop
    /// timeout, ensuring the browser-side budget expires first and a proper result is returned
    /// instead of a generic "A task was canceled" interop cancellation.
    /// </summary>
    public int InteropTimeoutBufferSeconds { get; set; } = 30;

    /// <summary>When true, every bookmark in each report is applied and re-checked.</summary>
    public bool CheckBookmarks { get; set; } = true;

    /// <summary>
    /// When true, every page in each report is activated and re-checked, including hidden pages.
    /// Hidden pages are typically drill-through destinations, so rendering them is how the check
    /// exercises drill-through targets (2–3 levels deep) — the embed SDK exposes no method to trigger
    /// a drill programmatically, so visiting the destination pages directly is the supported approach.
    /// </summary>
    public bool CheckAllPages { get; set; } = true;

    /// <summary>Maximum time to wait for a page to re-render after it is activated.</summary>
    public int PageTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Upper bound on how many pages to render per report so a report with a very large number of
    /// (drill-through) pages can't blow past the overall budget. <c>0</c> means unlimited.
    /// </summary>
    public int MaxPagesPerReport { get; set; } = 25;

    /// <summary>Returns the effective per-report page cap, treating 0 as unlimited.</summary>
    public int GetMaxPagesPerReport() => MaxPagesPerReport <= 0 ? int.MaxValue : MaxPagesPerReport;

    /// <summary>
    /// When true, the runner drives a drill-through from each table/matrix visual by right-clicking its
    /// first data row and choosing "Drill through" from the report context menu, then checks the target
    /// page. Drill-through cannot be triggered through the embed SDK, so this is done via Playwright DOM
    /// interaction against the report iframe and is best-effort (a target that can't be found is skipped
    /// and logged, not failed).
    /// </summary>
    public bool CheckDrillThrough { get; set; } = true;

    /// <summary>
    /// When true, the runner flips toggle-style custom visuals (e.g. the BENE.BIZ Toggle Switch) between
    /// their ON/OFF states and re-checks after each flip, so a visual that only appears in one toggle
    /// state is still exercised. Also DOM-driven and best-effort.
    /// </summary>
    public bool CheckToggles { get; set; } = true;

    /// <summary>Time to wait after a drill-through/toggle interaction for the report to settle and re-render.</summary>
    public int InteractionSettleSeconds { get; set; } = 10;

    /// <summary>Upper bound on how many table/matrix drill-through interactions to attempt per report. 0 = unlimited.</summary>
    public int MaxInteractionsPerReport { get; set; } = 20;

    /// <summary>Returns the effective per-report interaction cap, treating 0 as unlimited.</summary>
    public int GetMaxInteractionsPerReport() => MaxInteractionsPerReport <= 0 ? int.MaxValue : MaxInteractionsPerReport;

    /// <summary>
    /// How many levels of drill-through to follow. A report can chain drill-through pages (e.g. a table
    /// drills to a Category page whose own table drills to a YoY/MoM page). At depth N the explorer opens
    /// the context menu on data points of the destination page and follows its drill-through targets too.
    /// Default 3 covers the common 2–3 level chains; 1 = only first-level drill-through; 0 is treated as 1.
    /// </summary>
    public int MaxDrillThroughDepth { get; set; } = 3;

    /// <summary>Returns the effective drill-through recursion depth (minimum 1).</summary>
    public int GetMaxDrillThroughDepth() => MaxDrillThroughDepth <= 0 ? 1 : MaxDrillThroughDepth;

    /// <summary>
    /// Case-insensitive text used to match the "Drill through" item in the report's context menu.
    /// English default; override for a localized report UI.
    /// </summary>
    public string DrillThroughMenuText { get; set; } = "Drill through";

    /// <summary>
    /// Case-insensitive substring used to identify toggle-style custom visuals by their visual type or
    /// title (e.g. matches the BENE.BIZ Toggle Switch). Override to target a specific visual name.
    /// </summary>
    public string ToggleVisualMatch { get; set; } = "toggle";

    /// <summary>
    /// DEBUG ONLY. When set, the run is filtered to the single report whose display name matches this
    /// value (case-insensitive), so drill-through/toggle behaviour can be investigated on one report
    /// without waiting for the whole workspace. Leave blank in production to check every report.
    /// </summary>
    public string DebugReportName { get; set; } = string.Empty;

    /// <summary>
    /// DEBUG ONLY. When true, the drill-through discovery dumps the raw opened context-menu HTML (and any
    /// submenu flyout) into the report's diagnostic notes and the log, so the exact element structure and
    /// menu-item text can be inspected to understand how the current Fabric/Power BI renders drill-through.
    /// </summary>
    public bool DebugDrillThroughDom { get; set; }

    /// <summary>Explicit list of reports to check
    public List<PowerBiReportConfig> Reports { get; set; } = new();

    /// <summary>
    /// Row-level-security identity to impersonate when generating embed tokens. Required when the
    /// semantic model enforces RLS, because an app-only service principal cannot supply its own
    /// effective identity. Leave <see cref="EffectiveIdentity.Username"/> blank to disable.
    /// </summary>
    public EffectiveIdentityOptions EffectiveIdentity { get; set; } = new();

    /// <summary>
    /// Returns the effective authority url including the tenant id, e.g.
    /// https://login.microsoftonline.com/{tenantId}.
    /// </summary>
    public string GetAuthority() => $"{Authority.TrimEnd('/')}/{TenantId}";

    /// <summary><see cref="MaxParallelReports"/> clamped to the supported 1-10 range.</summary>
    public int GetMaxParallelReports() => Math.Clamp(MaxParallelReports, 1, 10);

    /// <summary>
    /// <see cref="MaxReportsToCheck"/> normalized: a non-positive value means "no limit" and is
    /// returned as <see cref="int.MaxValue"/> so callers can always <c>Take(GetMaxReportsToCheck())</c>.
    /// </summary>
    public int GetMaxReportsToCheck() => MaxReportsToCheck > 0 ? MaxReportsToCheck : int.MaxValue;
}

/// <summary>
/// Row-level-security effective identity passed in the GenerateToken request when the semantic
/// model requires it. For a service principal, Power BI applies the named <see cref="Roles"/>
/// directly without re-checking Microsoft Entra group membership.
/// </summary>
public sealed class EffectiveIdentityOptions
{
    /// <summary>The user principal name (UPN) to impersonate. Blank disables effective identity.</summary>
    public string? Username { get; set; }

    /// <summary>The RLS role name(s) defined in the semantic model, e.g. "Participant Security".</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>True when a username has been configured and the identity should be sent.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Username);
}

/// <summary>
/// A single report to include in the sanity check. <see cref="WorkspaceId"/> is optional and
/// overrides <see cref="PowerBiOptions.WorkspaceId"/> when a report lives in another workspace.
/// </summary>
public sealed class PowerBiReportConfig
{
    /// <summary>The Power BI report GUID.</summary>
    public string ReportId { get; set; } = string.Empty;

    /// <summary>Friendly display name for reporting; falls back to the report id when omitted.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional workspace override for this report.</summary>
    public string? WorkspaceId { get; set; }
}
