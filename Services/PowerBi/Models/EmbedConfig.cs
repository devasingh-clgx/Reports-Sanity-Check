namespace Reports_Sanity_Check.Services.PowerBi.Models;

/// <summary>
/// The embed configuration the server hands to the browser so the Power BI JavaScript SDK can
/// load a single report. Contains a short-lived embed token (never the service-principal secret).
/// </summary>
public sealed class EmbedConfig
{
    public string ReportId { get; set; } = string.Empty;

    public string ReportName { get; set; } = string.Empty;

    public string WorkspaceId { get; set; } = string.Empty;

    public string EmbedUrl { get; set; } = string.Empty;

    /// <summary>The short-lived Power BI embed token (TokenType.Embed on the client).</summary>
    public string EmbedToken { get; set; } = string.Empty;

    public DateTimeOffset TokenExpiry { get; set; }

    /// <summary>
    /// The drill-through targets DECLARED by the report definition (read from Power BI/Fabric metadata
    /// as a workspace member), so the headless checker can deterministically open each hidden
    /// destination page in reading mode instead of guessing via DOM right-clicks.
    /// </summary>
    public List<DrillThroughTarget> DrillThroughTargets { get; set; } = new();

    /// <summary>
    /// Why <see cref="DrillThroughTargets"/> is empty (REST status, missing parts, parse miss, etc.).
    /// Null when targets were found. Surfaced in the email so an empty map is diagnosable without
    /// access to container logs.
    /// </summary>
    public string? DrillThroughMapDiagnostic { get; set; }

    /// <summary>
    /// Every visual declared in the report definition and the field(s) it projects. Read statically
    /// from metadata, so drill-through SOURCE visuals can be identified by field overlap even when the
    /// report renders no data rows.
    /// </summary>
    public List<ReportVisualDefinition> VisualDefinitions { get; set; } = new();
}
