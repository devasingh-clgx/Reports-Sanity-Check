using System.Text.Json.Serialization;

namespace Reports_Sanity_Check.Services.PowerBi.Models;

/// <summary>Outcome of a single report (or bookmark) sanity check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SanityStatus
{
    /// <summary>Not yet checked.</summary>
    Pending,

    /// <summary>Rendered fully with no visual errors.</summary>
    Passed,

    /// <summary>Rendered, but one or more visuals reported an error.</summary>
    Failed,

    /// <summary>The report did not finish rendering within the configured timeout.</summary>
    Timeout,

    /// <summary>The check could not run (e.g. embed token failure or SDK exception).</summary>
    Error
}

/// <summary>A single error surfaced by the Power BI SDK <c>error</c> event during a check.</summary>
public sealed class VisualError
{
    /// <summary>The report page (tab) that was active when the error fired, if known.</summary>
    public string? Page { get; set; }

    /// <summary>The visual name/title that errored, if the SDK provided it.</summary>
    public string? Visual { get; set; }

    /// <summary>The bookmark active when the error occurred, if any.</summary>
    public string? Bookmark { get; set; }

    /// <summary>SDK error level (e.g. Error, Fatal).</summary>
    public string? Level { get; set; }

    public string? Message { get; set; }

    public string? DetailedMessage { get; set; }
}

/// <summary>Result of applying and re-checking a single bookmark.</summary>
public sealed class BookmarkCheckResult
{
    public string Name { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>The report page the bookmark landed on (bookmarks can switch pages), if known.</summary>
    public string? Page { get; set; }

    public SanityStatus Status { get; set; } = SanityStatus.Pending;

    public int ErrorCount { get; set; }

    /// <summary>How long this bookmark took to apply and re-render, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Failure detail when the bookmark did not apply cleanly.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// The individual visual errors captured while this bookmark was active, so the summary email can
    /// name the specific visual that broke (e.g. "'Depreciation by Age' - QueryUserError") rather than
    /// only a count.
    /// </summary>
    public List<VisualError> Errors { get; set; } = new();
}

/// <summary>
/// Result of activating and re-checking a single report page. Rendering every page (including the
/// hidden pages that serve as drill-through destinations) is how the check exercises drill-through
/// targets, since the embed SDK exposes no method to trigger a drill programmatically.
/// </summary>
public sealed class PageCheckResult
{
    /// <summary>Internal page name (the stable id used by the SDK).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Friendly page (tab) name shown in the report.</summary>
    public string? DisplayName { get; set; }

    /// <summary>True when the page is hidden (drill-through destinations are usually hidden pages).</summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// True when the page was intentionally not rendered directly (a drill-through destination that
    /// needs incoming parameters and is exercised through the drill-through pass instead).
    /// </summary>
    public bool Skipped { get; set; }

    public SanityStatus Status { get; set; } = SanityStatus.Pending;

    public int ErrorCount { get; set; }

    /// <summary>How long this page took to activate and re-render, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Number of visuals the SDK reported on this page (helps confirm the page actually drew).</summary>
    public int VisualCount { get; set; }

    /// <summary>Failure detail when the page did not render cleanly.</summary>
    public string? Message { get; set; }

    /// <summary>The individual visual errors captured while this page was active, named per visual.</summary>
    public List<VisualError> Errors { get; set; } = new();
}

/// <summary>The kind of user interaction a <see cref="InteractionCheckResult"/> represents.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionKind
{
    /// <summary>A drill-through triggered from a table/matrix data point via the report context menu.</summary>
    DrillThrough,

    /// <summary>Flipping a BENE.BIZ Toggle Switch (or similar) custom visual between states.</summary>
    Toggle
}

/// <summary>
/// Result of a DOM-driven interaction that the embed SDK cannot trigger (drill-through from a table,
/// or flipping a toggle custom visual). These are performed with Playwright against the report iframe
/// and are best-effort: a target that can't be found is recorded as <see cref="SanityStatus.Pending"/>
/// (skipped) rather than failing the report.
/// </summary>
public sealed class InteractionCheckResult
{
    public InteractionKind Kind { get; set; }

    /// <summary>What was interacted with (e.g. the table visual title or the toggle's name/state).</summary>
    public string? Target { get; set; }

    /// <summary>The page the interaction started on, if known.</summary>
    public string? Page { get; set; }

    public SanityStatus Status { get; set; } = SanityStatus.Pending;

    public int ErrorCount { get; set; }

    /// <summary>How long the interaction and its re-check took, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Human-readable outcome (e.g. "Drill-through opened 'Detail' page" or why it was skipped).</summary>
    public string? Message { get; set; }

    /// <summary>Any visual errors surfaced by Power BI while the interaction was active, named per visual.</summary>
    public List<VisualError> Errors { get; set; } = new();
}

/// <summary>
/// Scan-level counters that explain drill-through COVERAGE for a report, so a missed drill-through is
/// visible rather than silent. These are recorded by the DOM interaction pass while it scans the report.
/// </summary>
public sealed class DrillThroughDiagnostics
{
    /// <summary>How many drillable data points (table cells / chart marks) the scan found.</summary>
    public int DrillablePointsFound { get; set; }

    /// <summary>How many distinct source visuals offered at least one drill-through target.</summary>
    public int SourceVisualsWithDrillThrough { get; set; }

    /// <summary>Every distinct drill-through TARGET NAME discovered while scanning (whether or not reached).</summary>
    public List<string> DiscoveredTargets { get; set; } = new();

    /// <summary>
    /// True when the scan stopped early because the interaction cap (MaxInteractionsPerReport) was hit,
    /// meaning some discovered drill-throughs may not have been exercised.
    /// </summary>
    public bool CapReached { get; set; }

    /// <summary>Human-readable notes about anything that limited coverage (e.g. cap hit, menu not found).</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>
    /// The drill-through targets DECLARED by the report definition (read from Power BI REST metadata as
    /// a workspace member), not guessed from the DOM. This is the authoritative map of which hidden
    /// pages are drill-through destinations and which field(s) each is bound to.
    /// </summary>
    public List<DrillThroughTarget> DeclaredTargets { get; set; } = new();

    /// <summary>
    /// One entry per data point that was right-clicked, recording the source visual, the menu items that
    /// actually appeared, and the drill-through destinations offered. This is the audit trail used to
    /// verify no drillable visual was missed and to pinpoint where a probe stalled.
    /// </summary>
    public List<DrillThroughProbe> Probes { get; set; } = new();

    /// <summary>
    /// The source visuals discovered generically: any visual whose own field projection contains every
    /// field a declared destination filters on, together with that visual's first data row. This is the
    /// answer to "which visual has a drill-through, and where do we right-click", derived from the
    /// report's own metadata and data rather than assumed.
    /// </summary>
    public List<DrillThroughSourceVisual> SourceVisuals { get; set; } = new();
}

/// <summary>
/// A visual identified as a drill-through SOURCE because it projects the field(s) a declared
/// destination page is filtered by, plus the first data row exported from it.
/// </summary>
public sealed class DrillThroughSourceVisual
{
    /// <summary>The page the visual lives on (display name).</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>The visual's internal name (stable id used by the SDK).</summary>
    public string VisualName { get; set; } = string.Empty;

    /// <summary>The visual's human title, as shown in the report.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The visual type (table, matrix, columnChart, ...).</summary>
    public string VisualType { get; set; } = string.Empty;

    /// <summary>True when the visual projects EVERY field a declared destination filters on.</summary>
    public bool IsDrillThroughSource { get; set; }

    /// <summary>The visual's exported column headers, i.e. its own field list.</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>The first data row: one Column=Value pair per column. Empty when the visual has no rows.</summary>
    public List<DrillThroughCell> FirstRow { get; set; } = new();

    /// <summary>The bound drill-through field(s) this visual matched, with the first row's value for each.</summary>
    public List<DrillThroughCell> MatchedFields { get; set; } = new();

    /// <summary>Rows returned by the export probe. Zero means the visual rendered but has no data.</summary>
    public int RowCount { get; set; }

    /// <summary>Why the visual could not be probed, when applicable.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Which independent detection paths identified this visual as a source. Recorded separately so a
    /// disagreement is visible rather than hidden: definition-based detection works without data,
    /// whereas data-based detection proves a real row exists to right-click.
    /// </summary>
    public List<string> DetectedBy { get; set; } = new();

    /// <summary>The fields this visual projects according to the report definition.</summary>
    public List<DrillThroughField> DeclaredFields { get; set; } = new();

    /// <summary>"Column=Value, ..." for the first row, for compact display.</summary>
    [JsonIgnore]
    public string FirstRowSummary =>
        FirstRow.Count == 0 ? "(no data rows)" : string.Join(", ", FirstRow.Select(c => $"{c.Column}={c.Value}"));
}

/// <summary>One column/value pair from a visual's exported data.</summary>
public sealed class DrillThroughCell
{
    public string Column { get; set; } = string.Empty;

    public string? Value { get; set; }
}

/// <summary>
/// A visual as declared in the report definition, with the field(s) it projects. Read statically from
/// the PBIR definition (no rendering, no query), so it is available even when the report returns no
/// rows. Used to identify drill-through sources by field overlap with a destination's bound fields.
/// </summary>
public sealed class ReportVisualDefinition
{
    /// <summary>The internal page name the visual belongs to.</summary>
    public string PageName { get; set; } = string.Empty;

    /// <summary>The visual's internal name (stable id, matches the SDK's visual.name).</summary>
    public string VisualName { get; set; } = string.Empty;

    /// <summary>The visual's declared title, falling back to its internal name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The visual type as declared (tableEx, pivotTable, columnChart, lineChart, ...).</summary>
    public string VisualType { get; set; } = string.Empty;

    /// <summary>Every field the visual projects.</summary>
    public List<DrillThroughField> Fields { get; set; } = new();

    /// <summary>The definition part this visual was read from, for traceability.</summary>
    public string DefinitionPath { get; set; } = string.Empty;

    /// <summary>
    /// True when this visual projects every field in <paramref name="boundFields"/>, meaning a
    /// right-click on its data can raise the drill-through those fields belong to.
    /// </summary>
    public bool ProjectsAll(IEnumerable<DrillThroughField> boundFields) =>
        boundFields.All(b => Fields.Any(f =>
            string.Equals(f.Column, b.Column, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(b.Table) ||
             string.Equals(f.Table, b.Table, StringComparison.OrdinalIgnoreCase))));

    /// <summary>"Table.Column" for each projected field, for compact display.</summary>
    [JsonIgnore]
    public string FieldSummary =>
        Fields.Count == 0 ? "(no fields)" : string.Join(", ", Fields.Select(f => $"{f.Table}.{f.Column}"));
}

/// <summary>
/// The result of right-clicking one data point: which visual it belonged to, what the context menu
/// contained, and which drill-through destination pages (if any) it offered.
/// </summary>
public sealed class DrillThroughProbe
{
    /// <summary>The report page the probed visual is on.</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>The source visual's accessible name, used to confirm coverage against the report.</summary>
    public string Visual { get; set; } = string.Empty;

    /// <summary>The kind of element right-clicked (data cell, row header, chart mark, data point).</summary>
    public string ElementKind { get; set; } = string.Empty;

    /// <summary>The field the right-clicked cell is bound to, when the element exposes one.</summary>
    public string? FieldRef { get; set; }

    /// <summary>Every menu item label that appeared after the right-click.</summary>
    public List<string> MenuItems { get; set; } = new();

    /// <summary>Whether a "Drill through" entry was present in that menu.</summary>
    public bool HasDrillThrough { get; set; }

    /// <summary>The destination page names listed under "Drill through".</summary>
    public List<string> Targets { get; set; } = new();
}

/// <summary>
/// A single drill-through TARGET page declared by the report definition, plus the field(s) its
/// drill-through filter is bound to. Discovered from Power BI REST metadata (never the DOM), so the
/// headless run can deterministically open the hidden destination page in reading mode with the
/// bound field applied as filter context and confirm it renders without visual errors.
/// </summary>
public sealed class DrillThroughTarget
{
    /// <summary>The target page's internal (stable) name, used to activate it via the embed SDK.</summary>
    public string PageName { get; set; } = string.Empty;

    /// <summary>The target page's friendly display name for humans/email.</summary>
    public string PageDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The field(s) the drill-through is bound to, formatted as "Table.Column". A target may bind more
    /// than one field; all are required to satisfy the destination page's filter context.
    /// </summary>
    public List<DrillThroughField> Fields { get; set; } = new();

    /// <summary>Verification outcome for this target after the headless run (Pending until checked).</summary>
    public SanityStatus Status { get; set; } = SanityStatus.Pending;

    /// <summary>Human-readable outcome (e.g. rendered clean, skipped, or why it failed).</summary>
    public string? Message { get; set; }

    /// <summary>Any visual errors surfaced while the target page was active with the bound filter applied.</summary>
    public List<VisualError> Errors { get; set; } = new();

    /// <summary>
    /// How this target was exercised: the real user gesture (right-click a data point -> "Drill through")
    /// or the metadata SDK fallback (open the destination page with filter context). Empty until checked.
    /// </summary>
    public DrillThroughVerificationMethod VerificationMethod { get; set; } = DrillThroughVerificationMethod.None;

    /// <summary>"Table.Column" for each bound field, for compact display.</summary>
    [JsonIgnore]
    public string FieldSummary =>
        Fields.Count == 0 ? "(no bound field)" : string.Join(", ", Fields.Select(f => $"{f.Table}.{f.Column}"));
}

/// <summary>How a drill-through target was exercised during the headless run.</summary>
public enum DrillThroughVerificationMethod
{
    /// <summary>Not yet verified.</summary>
    None = 0,

    /// <summary>Real user gesture: right-click a source data point -> "Drill through" -> target page.</summary>
    RealGesture = 1,

    /// <summary>Metadata fallback: SDK opened the destination page with the bound field filter context.</summary>
    MetadataFallback = 2
}

/// <summary>A field a drill-through target is bound to, identified by its table and column names.</summary>
public sealed class DrillThroughField
{
    public string Table { get; set; } = string.Empty;

    public string Column { get; set; } = string.Empty;
}

/// <summary>
/// The per-report payload returned from the browser (powerbi-sanity.js) via JS interop.
/// Report identity is attached server-side afterwards.
/// </summary>
public sealed class ReportCheckInteropResult
{
    public SanityStatus Status { get; set; } = SanityStatus.Error;

    public long DurationMs { get; set; }

    /// <summary>Time for just the initial load+render, excluding bookmark application.</summary>
    public long RenderDurationMs { get; set; }

    public string? Message { get; set; }

    public List<VisualError> Errors { get; set; } = new();

    public List<BookmarkCheckResult> Bookmarks { get; set; } = new();

    /// <summary>Per-page results, one for each page rendered (including hidden drill-through pages).</summary>
    public List<PageCheckResult> Pages { get; set; } = new();

    /// <summary>Results of DOM-driven interactions (table drill-through, toggle flips).</summary>
    public List<InteractionCheckResult> Interactions { get; set; } = new();

    /// <summary>Scan-level drill-through coverage counters, so missed drill-throughs are visible.</summary>
    public DrillThroughDiagnostics DrillThrough { get; set; } = new();
}

/// <summary>The full, persisted result for one report.</summary>
public sealed class ReportSanityResult
{
    public string ReportId { get; set; } = string.Empty;

    public string ReportName { get; set; } = string.Empty;

    public string WorkspaceId { get; set; } = string.Empty;

    public SanityStatus Status { get; set; } = SanityStatus.Pending;

    public long DurationMs { get; set; }

    /// <summary>Time for just the initial load+render, excluding bookmark application.</summary>
    public long RenderDurationMs { get; set; }

    public DateTimeOffset CheckedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? Message { get; set; }

    public List<VisualError> Errors { get; set; } = new();

    public List<BookmarkCheckResult> Bookmarks { get; set; } = new();

    /// <summary>Per-page results (including hidden drill-through pages) rendered for this report.</summary>
    public List<PageCheckResult> Pages { get; set; } = new();

    /// <summary>Results of DOM-driven interactions (table drill-through, toggle flips).</summary>
    public List<InteractionCheckResult> Interactions { get; set; } = new();

    /// <summary>Scan-level drill-through coverage counters, so missed drill-throughs are visible.</summary>
    public DrillThroughDiagnostics DrillThrough { get; set; } = new();

    public bool IsHealthy => Status == SanityStatus.Passed;

    /// <summary>The slowest single bookmark's apply time (ms), or 0 when there are no timed bookmarks.</summary>
    [JsonIgnore]
    public long MaxBookmarkDurationMs =>
        Bookmarks.Count == 0 ? 0 : Bookmarks.Max(b => b.DurationMs);

    /// <summary>The mean bookmark apply time (ms), or 0 when there are no timed bookmarks.</summary>
    [JsonIgnore]
    public long AverageBookmarkDurationMs =>
        Bookmarks.Count == 0 ? 0 : (long)Math.Round(Bookmarks.Average(b => b.DurationMs));

    /// <summary>
    /// The slowest single load in the run: the larger of the initial render and the slowest single
    /// bookmark apply. This is the value that should drive the "High PLT" flag, because a report that
    /// renders quickly but has many bookmarks accrues a large <see cref="DurationMs"/> total without
    /// any individual load being slow.
    /// </summary>
    [JsonIgnore]
    public long MaxSingleLoadDurationMs => Math.Max(RenderDurationMs, MaxBookmarkDurationMs);
}

/// <summary>A complete sanity-check run across all reports, suitable for persistence.</summary>
public sealed class SanityCheckRun
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The Power BI workspace (group) GUID this run targeted.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// Friendly workspace/environment name (e.g. "PROD") for display in the summary email. Falls
    /// back to the workspace id when no named environment matched.
    /// </summary>
    public string? WorkspaceName { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public List<ReportSanityResult> Results { get; set; } = new();

    public int TotalReports => Results.Count;

    public int PassedCount => Results.Count(r => r.Status == SanityStatus.Passed);

    public int FailedCount => Results.Count(r => r.Status is SanityStatus.Failed or SanityStatus.Timeout or SanityStatus.Error);

    /// <summary>True when at least one report failed, timed out, or errored.</summary>
    public bool HasFailures => FailedCount > 0;
}
