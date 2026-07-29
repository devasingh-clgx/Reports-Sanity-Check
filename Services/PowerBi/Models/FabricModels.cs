namespace Reports_Sanity_Check.Services.PowerBi.Models;

/// <summary>
/// A folder inside a workspace. Returned by the Fabric Core REST API
/// GET /v1/workspaces/{workspaceId}/folders. Used to scope report discovery to a folder subtree.
/// </summary>
public sealed class FabricFolder
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The parent folder id, or null when the folder sits at the workspace root.</summary>
    public Guid? ParentFolderId { get; set; }
}

/// <summary>
/// A workspace item (report, dataset, lakehouse, ...) returned by the Fabric Core REST API
/// GET /v1/workspaces/{workspaceId}/items. Unlike the flat Power BI report listing, each item
/// carries the <see cref="FolderId"/> it lives in, enabling folder-scoped discovery.
/// </summary>
public sealed class FabricItem
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Item type, e.g. "Report", "SemanticModel", "Lakehouse".</summary>
    public string? Type { get; set; }

    /// <summary>The folder the item lives in, or null when it sits at the workspace root.</summary>
    public Guid? FolderId { get; set; }
}
