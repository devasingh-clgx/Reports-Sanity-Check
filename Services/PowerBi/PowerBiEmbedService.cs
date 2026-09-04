using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Reports_Sanity_Check.Services.PowerBi.Models;

namespace Reports_Sanity_Check.Services.PowerBi;

/// <summary>
/// Resolves the list of reports to check and produces per-report <see cref="EmbedConfig"/> values
/// (embed url + short-lived embed token) using a Microsoft Entra service principal
/// ("embed for your customers" / app-owns-data flow).
/// </summary>
public interface IPowerBiEmbedService
{
    /// <summary>Returns the reports to check, either auto-discovered from the workspace or from config.</summary>
    /// <param name="workspaceId">
    /// Optional workspace GUID to target (e.g. a DEV/QA/UAT/PROD environment). When null/blank the
    /// configured default workspace is used.
    /// </param>
    Task<IReadOnlyList<PowerBiReportConfig>> GetReportsToCheckAsync(string? workspaceId = null, CancellationToken cancellationToken = default);

    /// <summary>Generates an embed url + embed token for a single report.</summary>
    Task<EmbedConfig> GetEmbedConfigAsync(PowerBiReportConfig report, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a workspace's friendly display name from its GUID via the Power BI REST API, so the
    /// name doesn't have to be hardcoded in configuration. Returns <c>null</c> when the id is invalid,
    /// the workspace isn't accessible to the service principal, or the lookup fails for any reason —
    /// callers should fall back to a configured label or the id. Never throws.
    /// </summary>
    Task<string?> GetWorkspaceNameAsync(string? workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Preflight check that verifies the service principal can authenticate and actually reach the
    /// target workspace, in a single fast REST call (GET /v1.0/myorg/groups?$filter=id eq '...').
    /// Unlike <see cref="GetWorkspaceNameAsync"/> this returns a <em>structured</em> result so the job
    /// can fail fast with an actionable reason (bad credentials vs. no access vs. wrong id) before the
    /// expensive report-render loop. Never throws.
    /// </summary>
    Task<WorkspaceAccessResult> VerifyWorkspaceAccessAsync(string? workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the report's DECLARED drill-through targets from its definition via the Fabric
    /// <c>getDefinition</c> REST endpoint (PBIR format). Because the service principal is a workspace
    /// member, this returns the authoritative map of which hidden pages are drill-through destinations
    /// and which field(s) each is bound to — without any DOM/right-click guessing. Never throws;
    /// returns an empty list when the report has no drill-through targets or the definition can't be read.
    /// </summary>
    Task<IReadOnlyList<DrillThroughTarget>> GetDrillThroughMapAsync(PowerBiReportConfig report, CancellationToken cancellationToken = default);

    /// <summary>Reads report page identity and ordering from the Power BI REST API.</summary>
    Task<IReadOnlyList<PowerBiReportPage>> GetReportPagesAsync(PowerBiReportConfig report, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explains why the most recent <see cref="GetDrillThroughMapAsync"/> call produced no targets;
    /// null when targets were found.
    /// </summary>
    string? LastDrillThroughMapDiagnostic { get; }

    /// <summary>
    /// Reads every visual in the report from its definition, together with the field(s) each visual
    /// projects. Combined with <see cref="GetDrillThroughMapAsync"/> this identifies drill-through SOURCE
    /// visuals purely from metadata: a visual that projects all of a destination's bound fields can raise
    /// that drill-through. Requires no rendering and no data, so it is unaffected by RLS or empty results.
    /// Never throws; returns an empty list when the definition can't be read.
    /// </summary>
    Task<IReadOnlyList<ReportVisualDefinition>> GetVisualFieldMapAsync(PowerBiReportConfig report, CancellationToken cancellationToken = default);
}

public sealed class PowerBiReportPage
{
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int Order { get; set; }
}

/// <summary>
/// Outcome of a workspace access preflight check. <see cref="HasAccess"/> is true only when the
/// service principal authenticated and can see the workspace; <see cref="WorkspaceName"/> then holds
/// the resolved display name. When false, <see cref="Error"/> explains why (bad credentials, no
/// access, or an invalid id) so the job can raise an actionable alert and fail fast.
/// </summary>
public sealed record WorkspaceAccessResult(bool HasAccess, string? WorkspaceName, string? Error)
{
    public static WorkspaceAccessResult Success(string workspaceName) => new(true, workspaceName, null);

    public static WorkspaceAccessResult Fail(string error) => new(false, null, error);
}

public sealed class PowerBiEmbedService : IPowerBiEmbedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PowerBiOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PowerBiEmbedService> _logger;
    private readonly Lazy<IConfidentialClientApplication> _app;

    public PowerBiEmbedService(
        IOptions<PowerBiOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<PowerBiEmbedService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Built lazily so configuration problems surface only when a check is actually run.
        // MSAL caches app-only tokens in-memory, so reusing this instance avoids re-authenticating
        // on every report.
        _app = new Lazy<IConfidentialClientApplication>(BuildConfidentialClient);
    }

    public async Task<IReadOnlyList<PowerBiReportConfig>> GetReportsToCheckAsync(string? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var targetWorkspaceId = RequireWorkspaceId(
            !string.IsNullOrWhiteSpace(workspaceId) ? workspaceId : _options.WorkspaceId);

        // The Power BI report listing is flat (no folder info). When a folder include/exclude filter
        // is configured, use the Fabric API instead because it exposes each report's folder.
        var folderFilteringRequested =
            !string.IsNullOrWhiteSpace(_options.IncludeReportsFolder) || _options.ExcludeReportsFolders.Count > 0;

        if (folderFilteringRequested)
        {
            var byFolder = await DiscoverReportsByFolderAsync(targetWorkspaceId, cancellationToken);
            if (byFolder is not null)
            {
                _logger.LogInformation(
                    "Discovered {Count} report(s) under folder filter (include='{Include}', exclude=[{Exclude}]) in workspace {WorkspaceId}.",
                    byFolder.Count, _options.IncludeReportsFolder, string.Join(", ", _options.ExcludeReportsFolders), targetWorkspaceId);
                return byFolder;
            }

            _logger.LogWarning(
                "Folder-aware discovery was requested but unavailable; falling back to the flat report listing for workspace {WorkspaceId}.",
                targetWorkspaceId);
        }

        return await DiscoverAllReportsAsync(targetWorkspaceId, cancellationToken);
    }

    // Flat discovery: every report in the workspace via the Power BI REST API.
    private async Task<IReadOnlyList<PowerBiReportConfig>> DiscoverAllReportsAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        using var client = await CreateAuthorizedClientAsync(cancellationToken);
        var response = await client.GetFromJsonAsync<PowerBiListResponse<PowerBiRestReport>>(
            $"v1.0/myorg/groups/{workspaceId}/reports", JsonOptions, cancellationToken);

        var discovered = (response?.Value ?? new List<PowerBiRestReport>())
            .Where(r => r.Id != Guid.Empty)
            .Select(r => new PowerBiReportConfig
            {
                ReportId = r.Id.ToString(),
                DisplayName = r.Name,
                WorkspaceId = workspaceId.ToString()
            })
            .ToList();

        _logger.LogInformation("Discovered {Count} report(s) in workspace {WorkspaceId}.", discovered.Count, workspaceId);
        return discovered;
    }

    // Folder-aware discovery via the Fabric API. Returns null (so the caller can fall back) when the
    // Fabric API can't be reached or the service principal lacks access.
    private async Task<IReadOnlyList<PowerBiReportConfig>?> DiscoverReportsByFolderAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await CreateAuthorizedFabricClientAsync(cancellationToken);

            var folders = await ListFabricAsync<FabricFolder>(client, $"v1/workspaces/{workspaceId}/folders?recursive=true", cancellationToken);
            var reportItems = await ListFabricAsync<FabricItem>(client, $"v1/workspaces/{workspaceId}/items?type=Report&recursive=true", cancellationToken);

            var allowedFolderIds = ResolveAllowedFolderIds(folders);
            var hasIncludeFilter = !string.IsNullOrWhiteSpace(_options.IncludeReportsFolder);

            var discovered = reportItems
                .Where(item => item.Id != Guid.Empty && IsItemAllowed(item, allowedFolderIds, hasIncludeFilter))
                .Select(item => new PowerBiReportConfig
                {
                    ReportId = item.Id.ToString(),
                    DisplayName = item.DisplayName,
                    WorkspaceId = workspaceId.ToString()
                })
                .ToList();

            return discovered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Fabric folder-aware discovery failed for workspace {WorkspaceId}; will fall back to the flat report listing.",
                workspaceId);
            return null;
        }
    }

    // A report is allowed when its folder is in the allowed subtree. Reports at the workspace root
    // (no folder) are only included when no specific include-folder was requested.
    private static bool IsItemAllowed(FabricItem item, HashSet<Guid> allowedFolderIds, bool hasIncludeFilter) =>
        item.FolderId is Guid folderId ? allowedFolderIds.Contains(folderId) : !hasIncludeFilter;

    // Builds the set of folder ids whose reports should be checked: the included subtree(s) minus any
    // excluded subtree(s). All matching is by folder display name, case-insensitively.
    private HashSet<Guid> ResolveAllowedFolderIds(IReadOnlyList<FabricFolder> folders)
    {
        var childrenByParent = folders.ToLookup(f => f.ParentFolderId);

        HashSet<Guid> included;
        if (!string.IsNullOrWhiteSpace(_options.IncludeReportsFolder))
        {
            var includeRoots = folders
                .Where(f => string.Equals(f.DisplayName, _options.IncludeReportsFolder, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Id);
            included = CollectSubtree(includeRoots, childrenByParent);
        }
        else
        {
            included = folders.Select(f => f.Id).ToHashSet();
        }

        if (_options.ExcludeReportsFolders.Count > 0)
        {
            var excludedNames = new HashSet<string>(_options.ExcludeReportsFolders, StringComparer.OrdinalIgnoreCase);
            var excludeRoots = folders
                .Where(f => excludedNames.Contains(f.DisplayName))
                .Select(f => f.Id);
            included.ExceptWith(CollectSubtree(excludeRoots, childrenByParent));
        }

        return included;
    }

    // Returns the given folders plus all of their descendants (inclusive).
    private static HashSet<Guid> CollectSubtree(IEnumerable<Guid> roots, ILookup<Guid?, FabricFolder> childrenByParent)
    {
        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>(roots);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id))
            {
                continue;
            }

            foreach (var child in childrenByParent[id])
            {
                stack.Push(child.Id);
            }
        }

        return result;
    }

    // Reads every page of a Fabric list endpoint, following the continuation token.
    private static async Task<List<T>> ListFabricAsync<T>(HttpClient client, string relativeUrl, CancellationToken cancellationToken)
    {
        var all = new List<T>();
        string? continuationToken = null;

        do
        {
            var url = continuationToken is null
                ? relativeUrl
                : $"{relativeUrl}&continuationToken={Uri.EscapeDataString(continuationToken)}";

            var page = await client.GetFromJsonAsync<FabricListResponse<T>>(url, JsonOptions, cancellationToken);
            if (page?.Value is { Count: > 0 })
            {
                all.AddRange(page.Value);
            }

            continuationToken = page?.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));

        return all;
    }

    public async Task<EmbedConfig> GetEmbedConfigAsync(PowerBiReportConfig report, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(report.ReportId))
        {
            throw new ArgumentException("ReportId is required.", nameof(report));
        }

        var workspaceId = RequireWorkspaceId(report.WorkspaceId ?? _options.WorkspaceId);
        var reportId = ParseGuid(report.ReportId, nameof(report.ReportId));

        using var client = await CreateAuthorizedClientAsync(cancellationToken);

        var pbiReport = await client.GetFromJsonAsync<PowerBiRestReport>(
            $"v1.0/myorg/groups/{workspaceId}/reports/{reportId}", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Report {reportId} was not found in workspace {workspaceId}.");

        // App-owns-data: generate an embed token. Use the V2 (multi-resource) endpoint
        // POST /v1.0/myorg/GenerateToken because the V1 per-report endpoint
        // (groups/{id}/reports/{id}/GenerateToken) rejects DirectLake datasets with
        // "Embedding a DirectLake dataset is not supported with V1 embed token".
        var hasDataset = !string.IsNullOrWhiteSpace(pbiReport.DatasetId);

        // RLS semantic models require an effective identity; a service principal must supply one
        // explicitly (username + role + the datasets it applies to). Power BI applies the named
        // role directly without re-checking Microsoft Entra group membership.
        var rls = _options.EffectiveIdentity;
        var includeIdentity = rls.IsEnabled && hasDataset;

        var tokenRequest = new
        {
            reports = new[] { new { id = reportId.ToString() } },
            datasets = hasDataset
                ? new object[] { new { id = pbiReport.DatasetId } }
                : Array.Empty<object>(),
            targetWorkspaces = new[] { new { id = workspaceId.ToString() } },
            identities = includeIdentity
                ? new object[]
                {
                    new
                    {
                        username = rls.Username,
                        roles = rls.Roles.ToArray(),
                        datasets = new[] { pbiReport.DatasetId! }
                    }
                }
                : Array.Empty<object>()
        };

        using var tokenResponse = await client.PostAsJsonAsync(
            "v1.0/myorg/GenerateToken",
            tokenRequest,
            JsonOptions,
            cancellationToken);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            // EnsureSuccessStatusCode() hides the real reason; read the Power BI error body instead.
            var body = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);

            string hint;
            if (body.Contains("effective identity", StringComparison.OrdinalIgnoreCase))
            {
                // Direct Lake semantic models default to per-viewer SSO, which a service principal
                // cannot supply. The model must use a Fixed Identity (No-SSO) cloud connection.
                hint = " This Direct Lake semantic model is configured for per-user SSO, which an app-only " +
                    "service principal cannot satisfy. Configure the semantic model to use a Fixed Identity " +
                    "(No-SSO) connection: https://learn.microsoft.com/fabric/get-started/direct-lake-fixed-identity";
            }
            else if ((int)tokenResponse.StatusCode == 400)
            {
                hint = " Confirm the service principal is a Member/Admin of the workspace, the workspace is " +
                    "on a dedicated (Premium/Fabric/Embedded) capacity, and the report's dataset is accessible.";
            }
            else
            {
                hint = string.Empty;
            }

            throw new InvalidOperationException(
                $"GenerateToken failed for report {reportId} in workspace {workspaceId}: " +
                $"{(int)tokenResponse.StatusCode} {tokenResponse.ReasonPhrase}. {body}{hint}");
        }

        var embedToken = await tokenResponse.Content.ReadFromJsonAsync<PowerBiEmbedTokenResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Empty embed-token response for report {reportId}.");

        return new EmbedConfig
        {
            ReportId = pbiReport.Id.ToString(),
            ReportName = report.DisplayName ?? pbiReport.Name ?? pbiReport.Id.ToString(),
            WorkspaceId = workspaceId.ToString(),
            EmbedUrl = pbiReport.EmbedUrl ?? string.Empty,
            EmbedToken = embedToken.Token ?? string.Empty,
            TokenExpiry = embedToken.Expiration ?? DateTimeOffset.UtcNow.AddHours(1)
        };
    }

    /// <summary>
    /// Explains why the most recent <see cref="GetDrillThroughMapAsync"/> call produced no targets.
    /// Null after a call that found targets. Read by the caller so the reason reaches the email report.
    /// </summary>
    public string? LastDrillThroughMapDiagnostic { get; private set; }

    public async Task<IReadOnlyList<DrillThroughTarget>> GetDrillThroughMapAsync(PowerBiReportConfig report, CancellationToken cancellationToken = default)
    {
        LastDrillThroughMapDiagnostic = null;

        if (string.IsNullOrWhiteSpace(report.ReportId))
        {
            LastDrillThroughMapDiagnostic = "No ReportId configured; drill-through metadata was not requested.";
            return Array.Empty<DrillThroughTarget>();
        }

        try
        {
            var workspaceId = ParseGuid(report.WorkspaceId ?? _options.WorkspaceId ?? string.Empty, "WorkspaceId");
            var reportId = ParseGuid(report.ReportId, nameof(report.ReportId));

            // The Fabric getDefinition endpoint returns the report definition as a set of base64 parts.
            // For PBIR-format reports each page is a definition/pages/<page>/page.json part whose
            // "pageBinding"/"filters" carry the drill-through configuration. Ask for the PBIR format so
            // pages are exposed as discrete, parseable parts.
            using var client = await CreateAuthorizedFabricClientAsync(cancellationToken);

            using var response = await client.PostAsync(
                $"v1/workspaces/{workspaceId}/reports/{reportId}/getDefinition?format=PBIR",
                content: null,
                cancellationToken);

            FabricDefinitionResponse? definition;

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                // getDefinition is a Long-Running Operation: 202 means the result isn't inline. Poll the
                // Operation-Location until it completes, then fetch the operation result which carries the
                // { definition: { parts: [...] } } payload.
                definition = await PollDefinitionOperationAsync(client, response, reportId, cancellationToken);
                if (definition is null)
                {
                    LastDrillThroughMapDiagnostic =
                        "getDefinition long-running operation did not return a definition (timed out or failed).";
                    return Array.Empty<DrillThroughTarget>();
                }
            }
            else if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "getDefinition for report {ReportId} returned {Status}: {Body}. Drill-through map will be empty.",
                    reportId, (int)response.StatusCode, body);
                LastDrillThroughMapDiagnostic =
                    $"getDefinition returned HTTP {(int)response.StatusCode}. " +
                    $"{Truncate(body, 300)}";
                return Array.Empty<DrillThroughTarget>();
            }
            else
            {
                definition = await response.Content.ReadFromJsonAsync<FabricDefinitionResponse>(JsonOptions, cancellationToken);
            }

            var parts = definition?.Definition?.Parts;
            if (parts is null || parts.Count == 0)
            {
                _logger.LogWarning(
                    "getDefinition for report {ReportId} returned no definition parts; drill-through map is empty.", reportId);
                LastDrillThroughMapDiagnostic = "getDefinition returned no definition parts.";
                return Array.Empty<DrillThroughTarget>();
            }

            // Log the part paths so container logs reveal whether the report is enhanced-PBIR
            // (definition/pages/<page>/page.json) or legacy (report.json with a sections[] array).
            _logger.LogInformation(
                "getDefinition for report {ReportId} returned {Count} part(s): {Paths}",
                reportId, parts.Count, string.Join(", ", parts.Select(p => p.Path)));

            var targets = ParseDrillThroughTargets(parts);

            // Keep hidden pages visible as diagnostic candidates even when explicit PBIR targets were found.
            // A hidden page with no binding/fields is not actionable, but omitting it would hide possible
            // authoring gaps such as a page intended for drill-through but not configured as one.
            foreach (var hiddenPage in RecoverHiddenPagesAsTargets(parts))
            {
                var existing = targets.FirstOrDefault(target =>
                    (!string.IsNullOrWhiteSpace(hiddenPage.PageName) &&
                     string.Equals(target.PageName, hiddenPage.PageName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(hiddenPage.PageDisplayName) &&
                     string.Equals(target.PageDisplayName, hiddenPage.PageDisplayName, StringComparison.OrdinalIgnoreCase)));
                if (existing is null)
                {
                    targets.Add(hiddenPage);
                }
                else if (existing.Fields.Count == 0 && hiddenPage.Fields.Count > 0)
                {
                    existing.Fields = hiddenPage.Fields;
                }
            }

            _logger.LogInformation(
                "Parsed {Count} drill-through target(s) for report {ReportId}: {Targets}",
                targets.Count, reportId,
                targets.Count == 0 ? "(none)" : string.Join("; ", targets.Select(t => $"{t.PageDisplayName} [{t.FieldSummary}]")));

            var pageClassification = DescribePbirPages(parts);

            // GROUND TRUTH: when we find no targets, dump the real decoded page definitions so we can
            // see the actual JSON shape Fabric returns instead of guessing key names. This is emitted at
            // Information level so it shows up in the Container App Job logs without extra configuration.
            if (targets.Count == 0)
            {
                DumpDecodedPartsForDiagnostics(parts, reportId);
                LastDrillThroughMapDiagnostic =
                    $"getDefinition returned {parts.Count} part(s) but no drill-through targets were parsed. " +
                    $"Parts: {string.Join(", ", parts.Select(p => p.Path))}";
            }
            else
            {
                LastDrillThroughMapDiagnostic = pageClassification;
            }

            return targets;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read drill-through map for report {ReportId}; continuing with an empty map.", report.ReportId);
            LastDrillThroughMapDiagnostic = $"Failed to read drill-through metadata: {ex.GetType().Name}: {ex.Message}";
            return Array.Empty<DrillThroughTarget>();
        }
    }

    public async Task<IReadOnlyList<PowerBiReportPage>> GetReportPagesAsync(
        PowerBiReportConfig report,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(report.ReportId))
        {
            return Array.Empty<PowerBiReportPage>();
        }

        try
        {
            var workspaceId = ParseGuid(report.WorkspaceId ?? _options.WorkspaceId ?? string.Empty, "WorkspaceId");
            var reportId = ParseGuid(report.ReportId, nameof(report.ReportId));
            using var client = await CreateAuthorizedClientAsync(cancellationToken);
            var response = await client.GetFromJsonAsync<PowerBiListResponse<PowerBiReportPage>>(
                $"v1.0/myorg/groups/{workspaceId}/reports/{reportId}/pages",
                JsonOptions,
                cancellationToken);
            return response?.Value ?? new List<PowerBiReportPage>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read page metadata for report {ReportId}.", report.ReportId);
            return Array.Empty<PowerBiReportPage>();
        }
    }

    /// <summary>
    /// Reads every visual and the field(s) it projects from the report definition. Purely static: no
    /// rendering, no query, no data — so it works even when RLS returns zero rows.
    /// </summary>
    public async Task<IReadOnlyList<ReportVisualDefinition>> GetVisualFieldMapAsync(
        PowerBiReportConfig report, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(report.ReportId))
        {
            return Array.Empty<ReportVisualDefinition>();
        }

        try
        {
            var parts = await FetchDefinitionPartsAsync(report, cancellationToken);
            if (parts is null || parts.Count == 0)
            {
                return Array.Empty<ReportVisualDefinition>();
            }

            var visuals = ParseVisualFieldMap(parts);
            _logger.LogInformation(
                "Parsed {Count} visual definition(s) for report {ReportId}.", visuals.Count, report.ReportId);
            return visuals;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read visual field map for report {ReportId}; continuing with an empty map.", report.ReportId);
            return Array.Empty<ReportVisualDefinition>();
        }
    }

    // In PBIR each visual is its own part: definition/pages/<page>/visuals/<visual>/visual.json. The
    // visual's query projections name the fields it displays. Field names are read with the same
    // (Entity, Property) reader used for drill-through filters, so both sides are directly comparable.
    private List<ReportVisualDefinition> ParseVisualFieldMap(IReadOnlyList<FabricDefinitionPart> parts)
    {
        var visuals = new List<ReportVisualDefinition>();

        foreach (var part in parts)
        {
            if (part.Path is null || string.IsNullOrEmpty(part.Payload))
            {
                continue;
            }

            if (!part.Path.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string json;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(part.Payload));
            }
            catch (FormatException)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var visual = new ReportVisualDefinition
                {
                    // Path shape: definition/pages/<pageName>/visuals/<visualName>/visual.json
                    PageName = SegmentAfter(part.Path, "pages"),
                    VisualName = SegmentAfter(part.Path, "visuals"),
                    DefinitionPath = part.Path
                };

                if (TryGetProp(root, out var visualNode, "visual", "Visual"))
                {
                    visual.VisualType = GetStringProp(visualNode, "visualType", "VisualType") ?? string.Empty;
                    visual.Title = ReadVisualTitle(visualNode) ?? string.Empty;
                }

                // Collect every (Entity, Property) referenced anywhere in the visual's own definition.
                // A recursive sweep is used deliberately: projection containers vary by visual type
                // (categories/values/rows/columns/Y/X...), and every one of them ultimately holds the
                // same field expression shape.
                CollectFieldRefs(root, visual.Fields);

                if (string.IsNullOrEmpty(visual.Title))
                {
                    visual.Title = visual.VisualName;
                }

                visuals.Add(visual);
            }
            catch (JsonException)
            {
                // Malformed part — skip rather than failing the whole map.
            }
        }

        return visuals;
    }

    // Reads a visual's user-facing title, which may be a literal or an expression object.
    private static string? ReadVisualTitle(JsonElement visualNode)
    {
        if (!TryGetProp(visualNode, out var objects, "visualContainerObjects", "objects"))
        {
            return null;
        }

        if (!TryGetProp(objects, out var title, "title", "Title") || title.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in title.EnumerateArray())
        {
            if (TryGetProp(entry, out var props, "properties", "Properties") &&
                TryGetProp(props, out var text, "text", "Text"))
            {
                if (text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }

                if (TryGetProp(text, out var expr, "expr", "Expr") &&
                    TryGetProp(expr, out var literal, "Literal", "literal"))
                {
                    // Literals arrive quoted, e.g. 'Summary'.
                    return GetStringProp(literal, "Value", "value")?.Trim('\'');
                }
            }
        }

        return null;
    }

    // Recursively collects every field reference in a visual definition, de-duplicated.
    private static void CollectFieldRefs(JsonElement element, List<DrillThroughField> fields)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var (table, column) = ReadFieldEntityAndProperty(element);
                if (!string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(column) &&
                    !fields.Any(f => f.Table == table && f.Column == column))
                {
                    fields.Add(new DrillThroughField { Table = table!, Column = column! });
                }

                foreach (var prop in element.EnumerateObject())
                {
                    CollectFieldRefs(prop.Value, fields);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectFieldRefs(item, fields);
                }
                break;
        }
    }

    // Returns the path segment immediately following the given segment, e.g. for
    // "definition/pages/ReportSection1/visuals/abc123/visual.json" and "visuals" returns "abc123".
    private static string SegmentAfter(string path, string segment)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], segment, StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return string.Empty;
    }

    // Fetches and returns the report definition parts, transparently handling the Long-Running
    // Operation form. Shared by the drill-through map and the visual field map so both read one
    // definition through identical logic. Returns null when the definition can't be obtained.
    private async Task<IReadOnlyList<FabricDefinitionPart>?> FetchDefinitionPartsAsync(
        PowerBiReportConfig report, CancellationToken cancellationToken)
    {
        var workspaceId = ParseGuid(report.WorkspaceId ?? _options.WorkspaceId ?? string.Empty, "WorkspaceId");
        var reportId = ParseGuid(report.ReportId!, nameof(report.ReportId));

        using var client = await CreateAuthorizedFabricClientAsync(cancellationToken);

        using var response = await client.PostAsync(
            $"v1/workspaces/{workspaceId}/reports/{reportId}/getDefinition?format=PBIR",
            content: null,
            cancellationToken);

        FabricDefinitionResponse? definition;

        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            definition = await PollDefinitionOperationAsync(client, response, reportId, cancellationToken);
            if (definition is null)
            {
                LastDrillThroughMapDiagnostic =
                    "getDefinition long-running operation did not return a definition (timed out or failed).";
                return null;
            }
        }
        else if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "getDefinition for report {ReportId} returned {Status}: {Body}.",
                reportId, (int)response.StatusCode, body);
            LastDrillThroughMapDiagnostic =
                $"getDefinition returned HTTP {(int)response.StatusCode}. {Truncate(body, 300)}";
            return null;
        }
        else
        {
            definition = await response.Content.ReadFromJsonAsync<FabricDefinitionResponse>(JsonOptions, cancellationToken);
        }

        return definition?.Definition?.Parts;
    }

    // getDefinition can be a Long-Running Operation returning 202 Accepted with an Operation-Location
    // header. Poll that operation (honoring Retry-After) until it succeeds, then fetch its /result which
    // carries the { definition: { parts: [...] } } payload. Bounded so a stuck operation can't hang the run.
    private async Task<FabricDefinitionResponse?> PollDefinitionOperationAsync(
        HttpClient client, HttpResponseMessage accepted, Guid reportId, CancellationToken cancellationToken)
    {
        var operationLocation = accepted.Headers.Location?.ToString();
        if (accepted.Headers.TryGetValues("Operation-Location", out var opLoc))
        {
            operationLocation = opLoc.FirstOrDefault() ?? operationLocation;
        }

        if (string.IsNullOrWhiteSpace(operationLocation))
        {
            _logger.LogWarning(
                "getDefinition for report {ReportId} returned 202 without an Operation-Location; drill-through map is empty.",
                reportId);
            return null;
        }

        var retryAfter = accepted.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
        const int maxAttempts = 30;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter, cancellationToken);

            using var poll = await client.GetAsync(operationLocation, cancellationToken);
            if (!poll.IsSuccessStatusCode)
            {
                var body = await poll.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "getDefinition operation poll for report {ReportId} returned {Status}: {Body}.",
                    reportId, (int)poll.StatusCode, body);
                return null;
            }

            retryAfter = poll.Headers.RetryAfter?.Delta ?? retryAfter;

            using var doc = JsonDocument.Parse(await poll.Content.ReadAsStringAsync(cancellationToken));
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                // Some tenants inline the definition on the operation; otherwise fetch the /result.
                if (doc.RootElement.TryGetProperty("definition", out _))
                {
                    return JsonSerializer.Deserialize<FabricDefinitionResponse>(doc.RootElement.GetRawText(), JsonOptions);
                }

                var resultUrl = operationLocation.TrimEnd('/') + "/result";
                using var result = await client.GetAsync(resultUrl, cancellationToken);
                if (!result.IsSuccessStatusCode)
                {
                    var body = await result.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning(
                        "getDefinition operation result for report {ReportId} returned {Status}: {Body}.",
                        reportId, (int)result.StatusCode, body);
                    return null;
                }
                return await result.Content.ReadFromJsonAsync<FabricDefinitionResponse>(JsonOptions, cancellationToken);
            }

            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "getDefinition operation for report {ReportId} failed: {Body}.", reportId, doc.RootElement.GetRawText());
                return null;
            }
        }

        _logger.LogWarning(
            "getDefinition operation for report {ReportId} did not complete within {Attempts} polls; drill-through map is empty.",
            reportId, maxAttempts);
        return null;
    }

    // Parses PBIR page parts into drill-through targets. A page is a drill-through destination when its
    // page.json declares one or more drill-through filters ("Drillthrough"/"filterType": Drillthrough);
    // each such filter names the field it is bound to (Table.Column).
    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty :
        value.Length <= max ? value : value[..max] + "...";

    private List<DrillThroughTarget> ParseDrillThroughTargets(IReadOnlyList<FabricDefinitionPart> parts)
    {
        var targets = new List<DrillThroughTarget>();

        foreach (var part in parts)
        {
            if (part.Path is null || string.IsNullOrEmpty(part.Payload))
            {
                continue;
            }

            var isPbirPage = part.Path.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase);
            var isLegacyReport = part.Path.EndsWith("report.json", StringComparison.OrdinalIgnoreCase) ||
                                 part.Path.EndsWith("/definition.pbir", StringComparison.OrdinalIgnoreCase);

            if (!isPbirPage && !isLegacyReport)
            {
                continue;
            }

            string json;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(part.Payload));
            }
            catch (FormatException)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (isPbirPage)
                {
                    // Enhanced-PBIR: this part IS a single page.
                    AddTargetIfDrillThrough(root, targets, requirePageBinding: true);
                }
                else
                {
                    // Legacy layout: a single report.json whose "sections" array holds every page. Each
                    // section carries its own filters (often as an escaped JSON string in "filters").
                    if (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var section in sections.EnumerateArray())
                        {
                            AddTargetIfDrillThrough(section, targets, requirePageBinding: false);
                        }
                    }
                    else
                    {
                        // Some legacy definitions nest the layout under "layout"/"config"; fall back to a
                        // recursive scan of the whole document for any page-like object with drill-through.
                        AddTargetIfDrillThrough(root, targets, requirePageBinding: false);
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed part — skip it rather than failing the whole map.
            }
        }

        return targets;
    }

    // Recovers drill-through destinations from page visibility when filter parsing yields nothing.
    // In PBIR, hidden pages are almost always drill-through destinations: a page a user cannot navigate
    // to via tabs is reached by an action. Fields are read from whatever filters the page declares, so a
    // recovered target may have none — it is still reported so the destination is never silently lost.
    private List<DrillThroughTarget> RecoverHiddenPagesAsTargets(IReadOnlyList<FabricDefinitionPart> parts)
    {
        var recovered = new List<DrillThroughTarget>();

        foreach (var part in parts)
        {
            if (part.Path is null || string.IsNullOrEmpty(part.Payload) ||
                !part.Path.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string json;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(part.Payload));
            }
            catch (FormatException)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // PBIR marks non-navigable pages as "HiddenInViewMode" (or a boolean "visibility").
                var visibility = GetStringProp(root, "visibility", "Visibility");
                var isHidden =
                    string.Equals(visibility, "HiddenInViewMode", StringComparison.OrdinalIgnoreCase) ||
                    (root.TryGetProperty("visibility", out var vb) && vb.ValueKind == JsonValueKind.False);

                if (!isHidden)
                {
                    continue;
                }

                var pageName = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                var displayName = root.TryGetProperty("displayName", out var d) ? d.GetString() : null;

                // Treat every filter the hidden page declares as its drill-through context.
                var fields = ExtractDrillThroughFields(root, pageIsDrillThroughDestination: true);

                recovered.Add(new DrillThroughTarget
                {
                    PageName = pageName ?? string.Empty,
                    PageDisplayName = displayName ?? pageName ?? string.Empty,
                    Fields = fields
                });
            }
            catch (JsonException)
            {
                // Malformed part — skip it.
            }
        }

        return recovered;
    }

    private static string? DescribePbirPages(IReadOnlyList<FabricDefinitionPart> parts)
    {
        var descriptions = new List<string>();
        foreach (var part in parts.Where(part =>
                     part.Path?.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase) == true &&
                     !string.IsNullOrEmpty(part.Payload)))
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(part.Payload!));
                using var doc = JsonDocument.Parse(json);
                var page = doc.RootElement;
                var name = GetStringProp(page, "displayName", "DisplayName")
                           ?? GetStringProp(page, "name", "Name")
                           ?? part.Path!;
                var visibility = GetStringProp(page, "visibility", "Visibility") ?? "Visible";
                var binding = IsDrillThroughPageBinding(page) ? "Drillthrough" : "none";
                var markedFields = ExtractDrillThroughFields(page, pageIsDrillThroughDestination: false);
                descriptions.Add($"{name} [visibility={visibility}, pageBinding={binding}, markedFields={markedFields.Count}]");
            }
            catch (FormatException)
            {
                // Ignore malformed definition parts in best-effort diagnostics.
            }
            catch (JsonException)
            {
                // Ignore malformed definition parts in best-effort diagnostics.
            }
        }

        return descriptions.Count == 0
            ? null
            : "PBIR page classification: " + string.Join("; ", descriptions);
    }

    // Diagnostic-only: decode every page/report part and log its real JSON so we can read the ACTUAL
    // drill-through structure Fabric returns rather than guessing key names. Payloads are chunked because
    // log sinks truncate long lines. Called only when we discovered zero targets, so it costs nothing on
    // healthy runs. Sensitive data is not expected in report layout metadata (no row data is included).
    private void DumpDecodedPartsForDiagnostics(IReadOnlyList<FabricDefinitionPart> parts, Guid reportId)
    {
        foreach (var part in parts)
        {
            if (part.Path is null || string.IsNullOrEmpty(part.Payload))
            {
                continue;
            }

            // Focus on the parts that could carry page/section + filter metadata.
            var relevant = part.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                           part.Path.EndsWith(".pbir", StringComparison.OrdinalIgnoreCase);
            if (!relevant)
            {
                continue;
            }

            string json;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(part.Payload));
            }
            catch (FormatException)
            {
                _logger.LogInformation(
                    "DRILLTHROUGH-DIAG report {ReportId} part {Path}: payload is not base64 (PayloadType={PayloadType}); first 200 chars: {Preview}",
                    reportId, part.Path, part.PayloadType,
                    part.Payload.Length <= 200 ? part.Payload : part.Payload[..200]);
                continue;
            }

            // Only dump parts that actually mention drilling/filters so we don't flood the log with
            // theme/visual-container noise, but always include page.json/report.json roots.
            var mentionsFilter = json.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
                                 json.Contains("drill", StringComparison.OrdinalIgnoreCase);
            var isPageRoot = part.Path.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase) ||
                             part.Path.EndsWith("report.json", StringComparison.OrdinalIgnoreCase) ||
                             part.Path.EndsWith("/pages.json", StringComparison.OrdinalIgnoreCase);

            if (!mentionsFilter && !isPageRoot)
            {
                continue;
            }

            const int chunkSize = 3000;
            for (var offset = 0; offset < json.Length; offset += chunkSize)
            {
                var chunk = json.Substring(offset, Math.Min(chunkSize, json.Length - offset));
                _logger.LogInformation(
                    "DRILLTHROUGH-DIAG report {ReportId} part {Path} [{Start}/{Total}]: {Chunk}",
                    reportId, part.Path, offset, json.Length, chunk);
            }
        }
    }

    private static void AddTargetIfDrillThrough(
        JsonElement pageElement,
        List<DrillThroughTarget> targets,
        bool requirePageBinding)
    {
        // A page is a drill-through destination if EITHER shape says so:
        //  - Modern PBIR: the page declares "pageBinding": { "type": "Drillthrough" }, and its filters are
        //    marked "howCreated": "Drillthrough" rather than carrying a filter-level type.
        //  - Legacy: individual filters carry "type"/"filterType": "Drillthrough".
        // Only the filter-level shape was handled before, so modern PBIR reports parsed to zero targets
        // even though their destination pages exist.
        var isDrillThroughPage = IsDrillThroughPageBinding(pageElement);

        var fields = ExtractDrillThroughFields(pageElement, isDrillThroughPage);
        if (!isDrillThroughPage && (requirePageBinding || fields.Count == 0))
        {
            return;
        }

        // PBIR pages use "name"/"displayName"; legacy sections use "name"/"displayName" too, but the
        // stable id may live in "name" while the human title is "displayName".
        var pageName = pageElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        var displayName = pageElement.TryGetProperty("displayName", out var d) ? d.GetString() : null;

        targets.Add(new DrillThroughTarget
        {
            PageName = pageName ?? string.Empty,
            PageDisplayName = displayName ?? pageName ?? string.Empty,
            Fields = fields
        });
    }

    // True when the page declares itself a drill-through destination via its pageBinding, e.g.
    // "pageBinding": { "name": "...", "type": "Drillthrough" }.
    private static bool IsDrillThroughPageBinding(JsonElement pageElement)
    {
        if (!TryGetProp(pageElement, out var binding, "pageBinding", "PageBinding"))
        {
            return false;
        }

        var type = GetStringProp(binding, "type", "Type");
        return string.Equals(type, "Drillthrough", StringComparison.OrdinalIgnoreCase);
    }

    // Walks a page/section element looking for drill-through filters and returns the Table.Column
    // field(s) they are bound to. Tolerant of every layout: "filters"/"filterConfig" may be an array, an
    // object wrapping a "filters" array, or an escaped JSON *string* (legacy) that must be re-parsed. Only
    // filters whose type/filterType is "Drillthrough" are matched.
    private static List<DrillThroughField> ExtractDrillThroughFields(JsonElement root, bool pageIsDrillThroughDestination)
    {
        var fields = new List<DrillThroughField>();

        foreach (var propName in new[] { "filterConfig", "filters" })
        {
            if (!root.TryGetProperty(propName, out var raw))
            {
                continue;
            }

            CollectDrillThroughFields(raw, fields, pageIsDrillThroughDestination);
        }

        return fields;
    }

    // Normalizes a filters value (array, object-with-filters, or escaped JSON string) and collects the
    // bound fields of any drill-through filter into the accumulator. When the PAGE is already known to be
    // a drill-through destination, every filter it declares IS its drill-through context, so filters are
    // accepted without requiring a filter-level type marker (modern PBIR omits it).
    private static void CollectDrillThroughFields(
        JsonElement value, List<DrillThroughField> fields, bool pageIsDrillThroughDestination)
    {
        // Legacy: the value is a JSON string that itself encodes the filters array/object. Re-parse it.
        if (value.ValueKind == JsonValueKind.String)
        {
            var inner = value.GetString();
            if (string.IsNullOrWhiteSpace(inner))
            {
                return;
            }
            try
            {
                using var innerDoc = JsonDocument.Parse(inner);
                CollectDrillThroughFields(innerDoc.RootElement, fields, pageIsDrillThroughDestination);
            }
            catch (JsonException)
            {
                // Not JSON after all — ignore.
            }
            return;
        }

        // Object may itself hold a "filters" array (filterConfig shape).
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("filters", out var innerArray))
            {
                CollectDrillThroughFields(innerArray, fields, pageIsDrillThroughDestination);
            }
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var filter in value.EnumerateArray())
        {
            if (filter.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = filter.TryGetProperty("type", out var t) ? t.GetString()
                     : filter.TryGetProperty("filterType", out var ft) ? ft.GetString()
                     : null;

            // Modern PBIR marks the origin of the filter instead of its type.
            var howCreated = GetStringProp(filter, "howCreated", "HowCreated");

            var isDrillThroughFilter =
                string.Equals(type, "Drillthrough", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(howCreated, "Drillthrough", StringComparison.OrdinalIgnoreCase) ||
                pageIsDrillThroughDestination;

            if (!isDrillThroughFilter)
            {
                continue;
            }

            if (!filter.TryGetProperty("field", out var field) &&
                !filter.TryGetProperty("expression", out field))
            {
                continue;
            }

            var (table, column) = ReadFieldEntityAndProperty(field);
            if (!string.IsNullOrEmpty(table) && !string.IsNullOrEmpty(column) &&
                !fields.Any(f => f.Table == table && f.Column == column))
            {
                fields.Add(new DrillThroughField { Table = table!, Column = column! });
            }
        }
    }

    // Reads the (Entity, Property) pair from a field expression. Handles the PBIR shape, e.g.
    // { "Column": { "Expression": { "SourceRef": { "Entity": "Sales" } }, "Property": "Region" } }, and
    // the legacy camelCase shape, e.g.
    // { "column": { "expression": { "sourceRef": { "entity": "Sales" } }, "property": "Region" } }.
    private static (string? table, string? column) ReadFieldEntityAndProperty(JsonElement field)
    {
        foreach (var kind in new[] { "Column", "Measure", "HierarchyLevel", "column", "measure", "hierarchyLevel" })
        {
            if (!field.TryGetProperty(kind, out var expr))
            {
                continue;
            }

            string? property = GetStringProp(expr, "Property", "property");

            string? entity = null;
            if ((TryGetProp(expr, out var exprInner, "Expression", "expression")) &&
                TryGetProp(exprInner, out var sourceRef, "SourceRef", "sourceRef") &&
                TryGetProp(sourceRef, out var e, "Entity", "entity"))
            {
                entity = e.GetString();
            }

            if (!string.IsNullOrEmpty(property))
            {
                return (entity, property);
            }
        }

        return (null, null);
    }

    // Case-tolerant property lookup helpers for mixed PBIR/legacy JSON casing.
    private static bool TryGetProp(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            {
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? GetStringProp(JsonElement element, params string[] names)
        => TryGetProp(element, out var v, names) ? v.GetString() : null;

    public async Task<string?> GetWorkspaceNameAsync(string? workspaceId, CancellationToken cancellationToken = default)
    {
        var access = await VerifyWorkspaceAccessAsync(workspaceId, cancellationToken);
        return access.HasAccess ? access.WorkspaceName : null;
    }

    public async Task<WorkspaceAccessResult> VerifyWorkspaceAccessAsync(string? workspaceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || !Guid.TryParse(workspaceId, out var id))
        {
            return WorkspaceAccessResult.Fail($"WorkspaceId '{workspaceId}' is not a valid GUID.");
        }

        try
        {
            using var client = await CreateAuthorizedClientAsync(cancellationToken);

            // GET /v1.0/myorg/groups returns the workspaces the service principal can see. Filtering
            // by id server-side keeps the response to the single workspace we care about, and exercises
            // the whole credential -> token -> API-access chain in one cheap call.
            using var response = await client.GetAsync(
                $"v1.0/myorg/groups?$filter=id eq '{id}'", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Workspace access check for {WorkspaceId} returned {Status}: {Body}",
                    id, (int)response.StatusCode, body);

                var hint = (int)response.StatusCode == 401
                    ? " The service-principal credentials (PowerBi:TenantId/ClientId/ClientSecret) were rejected."
                    : " Confirm the service principal has been added to the workspace.";
                return WorkspaceAccessResult.Fail(
                    $"Power BI rejected the workspace access check: {(int)response.StatusCode} {response.ReasonPhrase}.{hint}");
            }

            var list = await response.Content.ReadFromJsonAsync<PowerBiListResponse<PowerBiRestGroup>>(JsonOptions, cancellationToken);
            var group = list?.Value.FirstOrDefault();

            if (group is null || string.IsNullOrWhiteSpace(group.Name))
            {
                // A 200 with an empty list means the token is valid but the SP isn't a member of the
                // workspace (or the id doesn't exist) — the exact "no access" case we want to catch.
                _logger.LogWarning(
                    "Workspace {WorkspaceId} is not visible to the service principal; it is likely not a member of the workspace.", id);
                return WorkspaceAccessResult.Fail(
                    $"The service principal cannot see workspace {id}. Add it as a Member/Admin of the workspace and ensure the workspace exists.");
            }

            return WorkspaceAccessResult.Success(group.Name.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace access check failed for {WorkspaceId}.", id);
            return WorkspaceAccessResult.Fail($"Workspace access check threw: {ex.Message}");
        }
    }

    private async Task<HttpClient> CreateAuthorizedClientAsync(CancellationToken cancellationToken)
    {
        var accessToken = await AcquireAppTokenAsync(cancellationToken);

        var client = _httpClientFactory.CreateClient("PowerBi");
        client.BaseAddress = new Uri(_options.ApiUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // Authorized client for the Fabric REST API (used by folder-aware discovery). Fabric requires a
    // token scoped to its own resource, separate from the Power BI token.
    private async Task<HttpClient> CreateAuthorizedFabricClientAsync(CancellationToken cancellationToken)
    {
        var accessToken = await AcquireTokenForScopeAsync(_options.FabricScope, cancellationToken);

        var client = _httpClientFactory.CreateClient("PowerBi");
        client.BaseAddress = new Uri(_options.FabricApiUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private Task<string> AcquireAppTokenAsync(CancellationToken cancellationToken) =>
        AcquireTokenForScopeAsync(_options.Scope, cancellationToken);

    private async Task<string> AcquireTokenForScopeAsync(string scope, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _app.Value
                .AcquireTokenForClient(new[] { scope })
                .ExecuteAsync(cancellationToken);

            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            _logger.LogError(ex, "Failed to acquire an app-only token for client {ClientId} (scope {Scope}).", _options.ClientId, scope);
            throw;
        }
    }

    private IConfidentialClientApplication BuildConfidentialClient()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId) ||
            string.IsNullOrWhiteSpace(_options.ClientSecret) ||
            string.IsNullOrWhiteSpace(_options.TenantId))
        {
            throw new InvalidOperationException(
                "Power BI credentials are not configured. Set PowerBi:TenantId/ClientId/ClientSecret " +
                "via env vars (PowerBi__ClientSecret) or Key Vault.");
        }

        return ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithClientSecret(_options.ClientSecret)
            .WithAuthority(_options.GetAuthority())
            .Build();
    }

    private static Guid RequireWorkspaceId(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.StartsWith('<'))
        {
            throw new InvalidOperationException(
                "PowerBi:WorkspaceId is not configured. Set it to the GUID of the workspace that holds the reports.");
        }

        return ParseGuid(workspaceId, "WorkspaceId");
    }

    private static Guid ParseGuid(string value, string name) =>
        Guid.TryParse(value, out var guid)
            ? guid
            : throw new InvalidOperationException($"'{name}' must be a valid GUID but was '{value}'.");
}

/// <summary>Generic Power BI REST list wrapper ({ "value": [ ... ] }).</summary>
internal sealed class PowerBiListResponse<T>
{
    public List<T> Value { get; set; } = new();
}/// <summary>
/// Generic Fabric REST list wrapper ({ "value": [ ... ], "continuationToken": "..." }) used for
/// the paged folders/items endpoints.
/// </summary>
internal sealed class FabricListResponse<T>
{
    public List<T> Value { get; set; } = new();

    public string? ContinuationToken { get; set; }
}

/// <summary>Minimal projection of a Power BI report from the REST API.</summary>
internal sealed class PowerBiRestReport
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? EmbedUrl { get; set; }
    public string? DatasetId { get; set; }
}

/// <summary>Minimal projection of a Power BI workspace (group) from the REST API.</summary>
internal sealed class PowerBiRestGroup
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
}

/// <summary>Response from the GenerateToken REST endpoint.</summary>
internal sealed class PowerBiEmbedTokenResponse
{
    public string? Token { get; set; }
    public string? TokenId { get; set; }
    public DateTimeOffset? Expiration { get; set; }
}

/// <summary>Response from the Fabric getDefinition endpoint ({ "definition": { "parts": [...] } }).</summary>
internal sealed class FabricDefinitionResponse
{
    public FabricDefinition? Definition { get; set; }
}

internal sealed class FabricDefinition
{
    public List<FabricDefinitionPart> Parts { get; set; } = new();
}

/// <summary>A single base64-encoded part of a Fabric item definition.</summary>
internal sealed class FabricDefinitionPart
{
    public string? Path { get; set; }
    public string? Payload { get; set; }
    public string? PayloadType { get; set; }
}
