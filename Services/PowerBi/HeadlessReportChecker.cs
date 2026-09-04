using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Reports_Sanity_Check.Services.PowerBi.Models;

namespace Reports_Sanity_Check.Services.PowerBi;

/// <summary>
/// Options controlling a single headless run, mirroring the values the interactive UI passes to the
/// browser module so the headless and UI paths behave identically.
/// </summary>
public sealed record HeadlessCheckOptions
{
    public int RenderTimeoutMs { get; init; }
    public int BookmarkApplyTimeoutMs { get; init; }
    public int OverallTimeoutMs { get; init; }

    /// <summary>Maximum time to wait for a page to re-render after it is activated.</summary>
    public int PageTimeoutMs { get; init; }

    /// <summary>Upper bound on pages rendered per report (already resolved; <see cref="int.MaxValue"/> = unlimited).</summary>
    public int MaxPagesPerReport { get; init; }

    /// <summary>Time to wait after an interaction for the report to settle/re-render, in ms.</summary>
    public int InteractionSettleMs { get; init; }

    /// <summary>Upper bound on drill-through interactions per report (resolved; <see cref="int.MaxValue"/> = unlimited).</summary>
    public int MaxInteractionsPerReport { get; init; }

    /// <summary>How many levels of drill-through to follow when exploring the DOM (resolved; minimum 1).</summary>
    public int MaxDrillThroughDepth { get; init; } = 3;

    /// <summary>Case-insensitive text matching the "Drill through" item in the report context menu.</summary>
    public string DrillThroughMenuText { get; init; } = "Drill through";

    /// <summary>Case-insensitive substring identifying toggle-style custom visuals by type/title.</summary>
    public string ToggleVisualMatch { get; init; } = "toggle";

    /// <summary>
    /// DEBUG ONLY. When true, drill-through discovery dumps the raw opened context-menu HTML (and any
    /// submenu flyout) into the report's diagnostic notes and the log, to reveal how the current
    /// Fabric/Power BI renders the drill-through menu.
    /// </summary>
    public bool DebugDrillThroughDom { get; init; }

    /// <summary>Extra time added on top of the overall budget before the runner force-closes the page.</summary>
    public int InteropBufferMs { get; init; }

    /// <summary>How many reports to render concurrently (each in its own browser tab).</summary>
    public int MaxParallelReports { get; init; }

}

/// <summary>
/// Renders Power BI reports in a server-side headless Chromium (via Playwright) so an unattended
/// caller â€” e.g. the Fabric pipeline API, which has no browser â€” can run the exact same visual
/// sanity check the interactive Blazor UI runs. It reuses the self-hosted Power BI SDK and the
/// shared <c>wwwroot/js/powerbi-sanity.js</c> module (exposed on the headless host page), so there
/// is a single source of truth for "did it render / any visual break / all bookmarks ok".
/// </summary>
public interface IHeadlessReportChecker
{
    /// <summary>
    /// Renders and checks each report and returns the per-report interop results in the same order
    /// as <paramref name="embeds"/>. Never throws per report: a navigation/render failure becomes an
    /// <see cref="SanityStatus.Error"/> result for that report.
    /// </summary>
    /// <param name="hostBaseUri">Base URL of the running app (the headless host page is served from here).</param>
    Task<IReadOnlyList<ReportCheckInteropResult>> CheckReportsAsync(
        Uri hostBaseUri,
        IReadOnlyList<EmbedConfig> embeds,
        HeadlessCheckOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class HeadlessReportChecker : IHeadlessReportChecker
{
    // The container id and the global the host page (wwwroot/headless-check.html) exposes.
    private const string HostPagePath = "headless-check.html";
    private const string ContainerId = "pbi-headless-host";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<HeadlessReportChecker> _logger;

    public HeadlessReportChecker(ILogger<HeadlessReportChecker> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<ReportCheckInteropResult>> CheckReportsAsync(
        Uri hostBaseUri,
        IReadOnlyList<EmbedConfig> embeds,
        HeadlessCheckOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = new ReportCheckInteropResult[embeds.Count];
        if (embeds.Count == 0)
        {
            return results;
        }

        var hostPageUrl = new Uri(hostBaseUri, HostPagePath).ToString();
        var maxParallel = Math.Clamp(options.MaxParallelReports, 1, 10);
        var runStopwatch = Stopwatch.StartNew();
        var setupStopwatch = Stopwatch.StartNew();

        using var playwright = await Playwright.CreateAsync();

        // --no-sandbox: the container image runs as root, and Chromium's setuid sandbox refuses to
        //   start as root; the content (our own Power BI reports) is trusted and the container is the
        //   isolation boundary, so disabling the in-process sandbox is the standard server-side choice.
        // --disable-dev-shm-usage: keeps Chromium off the small default /dev/shm in containers, which
        //   otherwise causes renderer crashes on larger reports.
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
        });

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            // 1600x1200 rather than 1280x720. The report canvas scales to fit the viewport, and the table grid
            // is virtualized - it only materialises rows that intersect the visible area. At 720px tall the
            // "Summary Tables" visual rendered its header row but no body rows, so drill-through could never be
            // reached. A taller viewport gives the grid room to create real data rows.
            ViewportSize = new ViewportSize { Width = 1600, Height = 1200 },
            IgnoreHTTPSErrors = true
        });
        setupStopwatch.Stop();

        _logger.LogInformation(
            "Headless check starting for {Count} report(s) via {HostPageUrl} (max parallel {MaxParallel}); " +
            "Playwright/browser/context setup took {SetupMs} ms.",
            embeds.Count, hostPageUrl, maxParallel, setupStopwatch.ElapsedMilliseconds);

        // Render reports in chunks the size of the parallel pool; each report gets its own page (an
        // isolated DOM) so several can render at once without colliding on the single container id.
        for (var offset = 0; offset < embeds.Count; offset += maxParallel)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = Enumerable
                .Range(offset, Math.Min(maxParallel, embeds.Count - offset))
                .ToList();

            var tasks = chunk.Select(async index =>
            {
                results[index] = await CheckSingleReportAsync(
                    context,
                    hostPageUrl,
                    embeds[index],
                    options,
                    runStopwatch,
                    cancellationToken);
            });

            await Task.WhenAll(tasks);
        }

        return results;
    }

    private async Task<ReportCheckInteropResult> CheckSingleReportAsync(
        IBrowserContext context,
        string hostPageUrl,
        EmbedConfig embed,
        HeadlessCheckOptions options,
        Stopwatch contextStopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(embed.EmbedToken))
        {
            // Server already failed to produce a token; record without launching a page.
            return new ReportCheckInteropResult
            {
                Status = SanityStatus.Error,
                Message = "Embed token could not be generated for this report."
            };
        }

        IPage? page = null;
        var pageStopwatch = Stopwatch.StartNew();
        var performance = new PerformanceDiagnostics
        {
            ContextAgeAtStartMs = contextStopwatch.ElapsedMilliseconds
        };

        // Hard guard: the browser module always resolves via its own deadline, but if a page hangs we
        // force-close it after the overall budget plus a buffer so the run can't stall indefinitely.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.OverallTimeoutMs + options.InteropBufferMs);

        try
        {
            var phaseStopwatch = Stopwatch.StartNew();
            page = await context.NewPageAsync();
            AddPhase(performance, "Page create", phaseStopwatch);
            var activePage = page;
            await using var registration = timeoutCts.Token.Register(() => _ = activePage.CloseAsync());

            phaseStopwatch.Restart();
            await page.GotoAsync(hostPageUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = options.OverallTimeoutMs
            });
            AddPhase(performance, "Host navigation", phaseStopwatch);

            // The host page imports the module asynchronously; wait until checkReport is exposed.
            phaseStopwatch.Restart();
            await page.WaitForFunctionAsync(
                "() => window.__sanityReady === true && typeof window.__sanityCheckReport === 'function'",
                null,
                new PageWaitForFunctionOptions { Timeout = options.RenderTimeoutMs });
            AddPhase(performance, "Host ready", phaseStopwatch);
            performance.ResourceSamples.Add(await CaptureBrowserResourceSampleAsync(
                page, "Host ready", pageStopwatch.ElapsedMilliseconds));

            // The browser module reads camelCase keys, so pass explicit camelCase payloads. The result
            // is JSON.stringify'd in the browser and deserialized here with the app's own options so we
            // don't depend on Playwright's serializer for the SanityStatus string-enum mapping.
            var arg = new
            {
                embed = new
                {
                    reportId = embed.ReportId,
                    embedUrl = embed.EmbedUrl,
                    embedToken = embed.EmbedToken
                },
                options = new
                {
                    renderTimeoutMs = options.RenderTimeoutMs,
                    bookmarkApplyTimeoutMs = options.BookmarkApplyTimeoutMs,
                    overallTimeoutMs = options.OverallTimeoutMs,
                    checkBookmarks = true,
                    checkAllPages = true,
                    pageTimeoutMs = options.PageTimeoutMs,
                    maxPagesPerReport = options.MaxPagesPerReport,
                    // Tell the browser module to keep the embedded report alive after the SDK checks so
                    // the DOM-driven interaction pass below can observe its error events.
                    runInteractions = true
                },
                containerId = ContainerId
            };

            const string expression = @"async ({ embed, options, containerId }) => {
                const result = await window.__sanityCheckReport(containerId, embed, options);
                return JSON.stringify(result);
            }";

            phaseStopwatch.Restart();
            var json = await page.EvaluateAsync<string>(expression, arg);
            AddPhase(performance, "SDK total", phaseStopwatch);

            var interop = JsonSerializer.Deserialize<ReportCheckInteropResult>(json, JsonOptions);
            if (interop is null)
            {
                return new ReportCheckInteropResult
                {
                    Status = SanityStatus.Error,
                    Message = "The headless check returned an empty result."
                };
            }

            interop.Performance = performance;
            performance.Phases.Add(new PerformancePhase
            {
                Name = "SDK initial render",
                DurationMs = interop.RenderDurationMs,
                Count = 1
            });
            performance.Phases.Add(new PerformancePhase
            {
                Name = "SDK bookmark traversal",
                DurationMs = interop.Bookmarks.Sum(bookmark => bookmark.DurationMs),
                Count = interop.Bookmarks.Count
            });
            performance.Phases.Add(new PerformancePhase
            {
                Name = "SDK page traversal",
                DurationMs = interop.Pages.Sum(reportPage => reportPage.DurationMs),
                Count = interop.Pages.Count
            });
            performance.ResourceSamples.Add(await CaptureBrowserResourceSampleAsync(
                page, "After SDK checks", pageStopwatch.ElapsedMilliseconds));

            // DOM-driven interactions the embed SDK cannot trigger: drill-through from a table/matrix
            // (right-click a data row -> "Drill through") and flipping toggle-style custom visuals. Best
            // effort: failures here are recorded per interaction and never throw. Only runs if the report
            // rendered, so we don't fight an already-broken embed.
            if (interop.Status is SanityStatus.Passed or SanityStatus.Failed)
            {
                try
                {
                    phaseStopwatch.Restart();
                    interop.Interactions = await RunInteractionsAsync(
                        page,
                        options,
                        interop.DrillThrough,
                        embed.DrillThroughTargets,
                        embed.VisualDefinitions,
                        performance,
                        timeoutCts.Token);
                    AddPhase(performance, "Interaction total", phaseStopwatch, interop.Interactions.Count);
                    performance.ResourceSamples.Add(await CaptureBrowserResourceSampleAsync(
                        page, "After interactions", pageStopwatch.ElapsedMilliseconds));
                    FoldInteractionOutcome(interop);
                }
                catch (Exception ex) when (!timeoutCts.IsCancellationRequested)
                {
                    _logger.LogWarning(ex,
                        "Interaction pass failed for report {ReportId}; SDK results retained.", embed.ReportId);
                }
                finally
                {
                    // Always release the kept-alive report so the next report in this page's pool starts clean.
                    phaseStopwatch.Restart();
                    try { await page.EvaluateAsync("() => window.__sanityEndInteractions && window.__sanityEndInteractions()"); }
                    catch { /* best-effort */ }
                    AddPhase(performance, "Interaction cleanup", phaseStopwatch);
                }
            }

            _logger.LogInformation(
                "Performance report {ReportId}: context {ContextStartMs}-{ContextEndMs} ms, page {PageLifetimeMs} ms, " +
                "phases [{Phases}], peak browser working set {PeakBrowserMb:F1} MB, peak JS heap {PeakHeapMb:F1} MB.",
                embed.ReportId,
                performance.ContextAgeAtStartMs,
                contextStopwatch.ElapsedMilliseconds,
                pageStopwatch.ElapsedMilliseconds,
                string.Join("; ", performance.Phases.Select(item =>
                    $"{item.Name}={item.DurationMs}ms/{item.Count}" +
                    (string.IsNullOrEmpty(item.Scope) ? string.Empty : $" ({item.Scope})"))),
                performance.PeakBrowserWorkingSetBytes / 1024d / 1024d,
                performance.PeakJavaScriptHeapUsedBytes / 1024d / 1024d);

            return interop;
        }
        catch (Exception) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Headless check for report {ReportId} exceeded its time budget and was stopped.", embed.ReportId);
            return new ReportCheckInteropResult
            {
                Status = SanityStatus.Timeout,
                Message = "The report did not finish within the configured time budget."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Headless check failed for report {ReportId}.", embed.ReportId);
            return new ReportCheckInteropResult
            {
                Status = SanityStatus.Error,
                Message = $"Headless check failed: {ex.Message}"
            };
        }
        finally
        {
            performance.ContextAgeAtEndMs = contextStopwatch.ElapsedMilliseconds;
            performance.PageLifetimeMs = pageStopwatch.ElapsedMilliseconds;
            if (page is not null)
            {
                try
                {
                    performance.ResourceSamples.Add(await CaptureBrowserResourceSampleAsync(
                        page, "Before page close", pageStopwatch.ElapsedMilliseconds));
                }
                catch
                {
                    // The timeout guard may already have closed the page.
                }
                try
                {
                    await page.CloseAsync();
                }
                catch
                {
                    // Page may already be closed by the timeout guard; ignore.
                }
            }
        }
    }

    private static void AddPhase(
        PerformanceDiagnostics diagnostics,
        string name,
        Stopwatch stopwatch,
        int count = 0,
        string? scope = null)
    {
        stopwatch.Stop();
        diagnostics.Phases.Add(new PerformancePhase
        {
            Name = name,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Count = count,
            Scope = scope
        });
    }

    private static async Task<BrowserResourceSample> CaptureBrowserResourceSampleAsync(
        IPage page,
        string phase,
        long elapsedMs)
    {
        var sample = new BrowserResourceSample { Phase = phase, ElapsedMs = elapsedMs };

        try
        {
            var heap = await page.EvaluateAsync<long[]>(
                "() => performance.memory ? [performance.memory.usedJSHeapSize, performance.memory.totalJSHeapSize] : [0, 0]");
            sample.JavaScriptHeapUsedBytes = heap.ElementAtOrDefault(0);
            sample.JavaScriptHeapTotalBytes = heap.ElementAtOrDefault(1);
        }
        catch
        {
            // The page may be closing; retain the process sample if available.
        }

        ICDPSession? session = null;
        try
        {
            session = await page.Context.NewCDPSessionAsync(page);
            var processInfo = await session.SendAsync("SystemInfo.getProcessInfo");
            if (processInfo is JsonElement root &&
                root.TryGetProperty("processInfo", out var processes) &&
                processes.ValueKind == JsonValueKind.Array)
            {
                foreach (var processElement in processes.EnumerateArray())
                {
                    if (!processElement.TryGetProperty("id", out var idElement) ||
                        !idElement.TryGetInt32(out var processId))
                    {
                        continue;
                    }

                    try
                    {
                        using var process = Process.GetProcessById(processId);
                        sample.BrowserWorkingSetBytes += process.WorkingSet64;
                        sample.BrowserPrivateMemoryBytes += process.PrivateMemorySize64;
                        sample.BrowserProcessCount++;
                    }
                    catch
                    {
                        // A short-lived Chromium utility process can exit between enumeration and sampling.
                    }
                }
            }
        }
        catch
        {
            // Browser process sampling is best-effort and must not affect report coverage.
        }
        finally
        {
            if (session is not null)
            {
                await session.DetachAsync();
            }
        }

        return sample;
    }

    /// <summary>
    /// Performs the DOM-driven interactions the embed SDK cannot trigger: drill-through from each
    /// table/matrix visual (right-click a data row -> "Drill through"), and flipping toggle-style custom
    /// visuals so a visual that only appears in one toggle state is still exercised. The report renders in
    /// a cross-origin Power BI iframe, so this must be done with a Playwright <see cref="IFrameLocator"/>;
    /// in-page JS cannot reach across the origin. Everything here is best-effort: a target that can't be
    /// found is recorded as <see cref="SanityStatus.Pending"/> (skipped), never an error.
    /// </summary>
    private async Task<List<InteractionCheckResult>> RunInteractionsAsync(
        IPage page,
        HeadlessCheckOptions options,
        DrillThroughDiagnostics diagnostics,
        IReadOnlyList<DrillThroughTarget> declaredTargets,
        IReadOnlyList<ReportVisualDefinition> visualDefinitions,
        PerformanceDiagnostics performance,
        CancellationToken cancellationToken)
    {
        var results = new List<InteractionCheckResult>();

        // Surface the metadata-declared drill-through map on the diagnostics so it is always visible in
        // the email, whether or not each target is verified below.
        if (declaredTargets.Count > 0 && diagnostics.DeclaredTargets.Count == 0)
        {
            diagnostics.DeclaredTargets = declaredTargets.ToList();
        }

        // The Power BI SDK embeds the report in a single iframe inside our container.
        var frame = page.FrameLocator($"#{ContainerId} iframe");

        // Drill-through and toggle visuals do NOT all live on the page the report lands on: the real
        // drillable tables/charts are frequently on OTHER visible pages. So enumerate every VISIBLE page
        // via the host-page SDK and run the interaction passes on each. Hidden pages are drill-through
        // destinations and are intentionally excluded here (they are reached via the drill-through action
        // itself). If page enumeration isn't available we fall back to the single landing page so behavior
        // degrades gracefully.
        var phaseStopwatch = Stopwatch.StartNew();
        var pages = await GetVisiblePagesForInteractionAsync(page);
        AddPhase(performance, "Interaction page discovery", phaseStopwatch, pages.Count);
        if (pages.Count == 0)
        {
            pages = new List<VisiblePage> { new VisiblePage(string.Empty, "(current page)") };
        }

        phaseStopwatch.Restart();
        foreach (var visiblePage in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Activate the page (no-op for the synthetic "current page" fallback). Wait for it to settle
            // before probing the iframe DOM, since navigation happens through the host-page SDK.
            if (!string.IsNullOrEmpty(visiblePage.Name))
            {
                var switched = await SetActivePageForInteractionAsync(page, visiblePage.Name, options.InteractionSettleMs);
                if (!switched)
                {
                    diagnostics.Notes.Add($"[page '{visiblePage.DisplayName}'] could not be activated; skipped.");
                    continue;
                }
            }

            diagnostics.Notes.Add($"[page '{visiblePage.DisplayName}'] running interaction passes.");

            // Toggles may exist on any page; don't emit a "no toggle" note per page (it would be noisy),
            // only when none were found across the whole report (handled by reportWhenNone: false here).
            await RunToggleInteractionsAsync(
                page,
                frame,
                options,
                results,
                cancellationToken,
                reportWhenNone: false);
        }
        AddPhase(performance, "Visible page and toggle traversal", phaseStopwatch, pages.Count);

        var state = new DrillExploreState();

        var interactionBudgetMs = Math.Max(30_000, Math.Min(
            (int)(options.OverallTimeoutMs * 0.6),
            options.OverallTimeoutMs - 30_000));
        state.Deadline = DateTime.UtcNow.AddMilliseconds(interactionBudgetMs);
        phaseStopwatch.Restart();
        var bookmarks = await GetApplyableBookmarksAsync(page);
        AddPhase(performance, "Interaction bookmark discovery", phaseStopwatch, bookmarks.Count);

            // Drill-through lives on real data visuals (e.g. a summary table). Explore each visible page in
            // its default state; drill-through destination pages are hidden and reached via the action.
            foreach (var visiblePage in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsNativeCoverageComplete(declaredTargets))
                {
                    break;
                }
                if (state.BudgetExpired)
                {
                    diagnostics.Notes.Add(
                        "Drill-through exploration stopped early: interaction time budget reached " +
                        "(partial results retained; the report itself rendered fine).");
                    break;
                }

                if (!string.IsNullOrEmpty(visiblePage.Name))
                {
                    var switched = await SetActivePageForInteractionAsync(page, visiblePage.Name, options.InteractionSettleMs);
                    if (!switched)
                    {
                        continue;
                    }
                }

                await page.EvaluateAsync("() => window.__sanityBeginInteractions && window.__sanityBeginInteractions()");

                // GENERIC SOURCE DISCOVERY: before right-clicking anything, ask each visual on this page
                // for its own data. A visual that projects every field a declared destination filters on
                // IS a drill-through source, and its first data row is exactly where the right-click must
                // land. This is derived from the report's own metadata, so it holds for any report.
                phaseStopwatch.Restart();
                await DiscoverDrillThroughSourcesAsync(
                    page, diagnostics, declaredTargets, visualDefinitions, visiblePage.Name, visiblePage.DisplayName, cancellationToken);
                AddPhase(performance, "Source discovery", phaseStopwatch, scope: visiblePage.DisplayName);

                phaseStopwatch.Restart();
                await ExploreDrillThroughAsync(
                    page, options, results, diagnostics, declaredTargets, visualDefinitions, performance, state, depth: 1,
                    pathPrefix: visiblePage.DisplayName, cancellationToken);
                AddPhase(performance, "Native candidate probing", phaseStopwatch, scope: visiblePage.DisplayName);

                if (IsNativeCoverageComplete(declaredTargets))
                {
                    break;
                }

                // Some authored bookmarks materialize tables/charts that do not exist in the default DOM.
                // Exercise each leaf bookmark as a separate source state, then scope all probes to the
                // metadata-identified source visual ids. This is required for reports where a tab/bookmark
                // controls which source matrix is rendered.
                foreach (var bookmark in bookmarks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsNativeCoverageComplete(declaredTargets))
                    {
                        break;
                    }
                    if (state.BudgetExpired)
                    {
                        break;
                    }

                    if (!string.IsNullOrEmpty(visiblePage.Name))
                    {
                        var restored = await SetActivePageForInteractionAsync(
                            page, visiblePage.Name, options.InteractionSettleMs);
                        if (!restored)
                        {
                            continue;
                        }
                    }

                    phaseStopwatch.Restart();
                    if (!await ApplyBookmarkForInteractionAsync(
                        page, bookmark.Name, options.InteractionSettleMs))
                    {
                        AddPhase(performance, "Interaction bookmark apply", phaseStopwatch, scope: bookmark.DisplayName);
                        diagnostics.Notes.Add(
                            $"[{visiblePage.DisplayName}] bookmark '{bookmark.DisplayName}' could not be applied; skipped for drill-through.");
                        continue;
                    }
                    AddPhase(performance, "Interaction bookmark apply", phaseStopwatch, 1, bookmark.DisplayName);

                    await page.EvaluateAsync(
                        "() => window.__sanityBeginInteractions && window.__sanityBeginInteractions()");

                    phaseStopwatch.Restart();
                    await ExploreDrillThroughAsync(
                        page, options, results, diagnostics, declaredTargets, visualDefinitions, performance, state, depth: 1,
                        pathPrefix: $"{visiblePage.DisplayName} [bookmark: {bookmark.DisplayName}]",
                        cancellationToken);
                    AddPhase(performance, "Bookmark-state native probing", phaseStopwatch, scope: bookmark.DisplayName);
                }
            }

            if (state.CheckedDestinations.Count > 0)
            {
                diagnostics.Notes.Add(
                    $"Native drill-through destinations checked once per report: " +
                    string.Join(", ", state.CheckedDestinations.OrderBy(name => name)) + ".");
            }

            var unreachedTargets = declaredTargets
                .Where(target => target.VerificationMethod != DrillThroughVerificationMethod.RealGesture)
                .Select(target => target.PageDisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unreachedTargets.Count > 0)
            {
                diagnostics.Notes.Add(
                    "Declared drill-through targets not exposed by a native right-click path were left unverified " +
                    "to avoid opening parameter-dependent pages without their real interaction context: " +
                    string.Join(", ", unreachedTargets) + ".");
            }
        // If no toggles were found on any page, record a single summary note so
        // the email still explains the toggle pass ran and found nothing.
        if (!results.Any(r => r.Kind == InteractionKind.Toggle))
        {
            results.Add(new InteractionCheckResult
            {
                Kind = InteractionKind.Toggle,
                Status = SanityStatus.Pending,
                Message = $"No toggle-style visual matching '{options.ToggleVisualMatch}' was found on any visible page."
            });
        }

        return results;
    }



    /// <summary>
    /// Identifies which visuals are drill-through SOURCES and captures one real data row from each, using
    /// three INDEPENDENT paths so a weakness in any one does not hide the answer:
    ///
    /// A. Report definition (authoritative, needs no data): each visual's own part declares the fields it
    ///    projects. A visual projecting all of a destination's bound fields can raise that drill-through.
    ///    Unaffected by RLS, empty results, or export permissions.
    /// B. Live SDK capabilities: asks each rendered visual for its data roles/fields, reflecting what the
    ///    embed actually built rather than what the definition declares.
    /// C. Data extraction: exports one row from each candidate (Summarized, falling back to Underlying)
    ///    to obtain the concrete first row that a right-click must target.
    ///
    /// A and B answer "which visual has drill-through"; C answers "where exactly do we click". When C
    /// returns nothing while A found sources, the visual is a source that currently has no rows — a
    /// materially different diagnosis from "no source exists", and it is reported as such.
    /// </summary>
    private async Task DiscoverDrillThroughSourcesAsync(
        IPage page,
        DrillThroughDiagnostics diagnostics,
        IReadOnlyList<DrillThroughTarget> declaredTargets,
        IReadOnlyList<ReportVisualDefinition> visualDefinitions,
        string pageName,
        string pageDisplayName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Matching must be PER TARGET. Pooling every target's fields and requiring a visual to project
        // all of them is wrong: distinct destinations bind distinct fields, so the pooled set is a
        // requirement no real source visual satisfies. A visual is a source when it projects all the
        // fields of ANY single destination.
        var targetsWithFields = declaredTargets
            .Where(t => t.Fields.Any(f => !string.IsNullOrWhiteSpace(f.Column)))
            .ToList();

        var boundFields = targetsWithFields
            .SelectMany(t => t.Fields)
            .Where(f => !string.IsNullOrWhiteSpace(f.Column))
            .DistinctBy(f => $"{f.Table}.{f.Column}")
            .ToList();

        if (boundFields.Count == 0)
        {
            // A destination may be known while its bound field(s) are not. Continue anyway: without
            // bound fields nothing can be MATCHED, but every visual can still be probed for data, which
            // answers "does this visual have rows?" independently of drill-through matching.
            diagnostics.Notes.Add(
                $"[{pageDisplayName}] No bound drill-through field(s) available; reporting every data " +
                $"visual with its declared fields and row data as drill-through source candidates.");
        }

        var boundJson = JsonSerializer.Serialize(
            boundFields.Select(f => new { table = f.Table, column = f.Column }));

        // Merge all paths into one record per visual, keyed by the visual's internal name.
        var merged = new Dictionary<string, DrillThroughSourceVisual>(StringComparer.OrdinalIgnoreCase);

        // ---- PATH A: report definition -------------------------------------------------------
        var pageVisuals = visualDefinitions
            .Where(v => string.IsNullOrEmpty(pageName) ||
                        string.Equals(v.PageName, pageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var v in pageVisuals)
        {
            // A visual qualifies when it projects all bound fields of ANY single destination, not the
            // union of every destination's fields.
            var matchedTarget = targetsWithFields.FirstOrDefault(t =>
                v.ProjectsAll(t.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Column))));

            var entry = new DrillThroughSourceVisual
            {
                Page = pageDisplayName,
                VisualName = v.VisualName,
                Title = v.Title,
                VisualType = v.VisualType,
                DeclaredFields = v.Fields,
                IsDrillThroughSource = matchedTarget is not null
            };

            if (entry.IsDrillThroughSource)
            {
                entry.DetectedBy.Add("definition");
                entry.MatchedFields = matchedTarget!.Fields
                    .Where(f => !string.IsNullOrWhiteSpace(f.Column))
                    .Select(b => new DrillThroughCell { Column = $"{b.Table}.{b.Column}", Value = null })
                    .ToList();
            }

            merged[entry.VisualName] = entry;
        }

        diagnostics.Notes.Add(
            $"[{pageDisplayName}] Path A (definition): {pageVisuals.Count} visual(s) declared, " +
            $"{merged.Values.Count(v => v.IsDrillThroughSource)} match the drill-through field(s).");

        // ---- PATH B: live SDK capabilities ---------------------------------------------------
        foreach (var e in await EvaluateVisualProbeAsync(
            page, diagnostics, pageDisplayName, "__sanityProbeVisualCapabilities", boundJson, "Path B (live SDK)"))
        {
            var name = GetString(e, "visual");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!merged.TryGetValue(name, out var entry))
            {
                entry = new DrillThroughSourceVisual { Page = pageDisplayName, VisualName = name };
                merged[name] = entry;
            }

            // The live embed is the better source for the rendered title/type.
            entry.Title = GetString(e, "title") is { Length: > 0 } t ? t : entry.Title;
            entry.VisualType = GetString(e, "type") is { Length: > 0 } ty ? ty : entry.VisualType;

            if (e.TryGetProperty("isDrillThroughSource", out var s) && s.ValueKind == JsonValueKind.True)
            {
                entry.IsDrillThroughSource = true;
                if (!entry.DetectedBy.Contains("live-sdk"))
                {
                    entry.DetectedBy.Add("live-sdk");
                }
            }
        }

        // ---- PATH C: data extraction ---------------------------------------------------------
        // Probe EVERY data visual, not just confirmed sources. Restricting this to matched visuals meant a
        // visual missed by field matching produced no row information at all, leaving "does this visual
        // have data?" unanswerable. Exporting one row from each separates an empty visual from an
        // unmatched one, which is the distinction that actually diagnoses a failed drill-through.
        foreach (var entry in merged.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string rowJson;
            try
            {
                rowJson = await page.EvaluateAsync<string>(
                    @"async (name) => window.__sanityFirstDataRow
                        ? await window.__sanityFirstDataRow(name)
                        : JSON.stringify({ error: 'helper unavailable' })",
                    entry.VisualName);
            }
            catch (Exception ex)
            {
                entry.Error = $"Data extraction failed: {ex.Message}";
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(rowJson);
                var r = doc.RootElement;

                entry.RowCount = r.TryGetProperty("rowCount", out var rc) && rc.TryGetInt32(out var rcv) ? rcv : 0;
                entry.Columns = r.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array
                    ? cols.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToList()
                    : new List<string>();
                entry.FirstRow = ReadCells(r, "firstRow", "column");
                entry.Error = r.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String
                    ? er.GetString()
                    : null;

                if (entry.FirstRow.Count > 0 && !entry.DetectedBy.Contains("data"))
                {
                    entry.DetectedBy.Add("data");
                }

                // Fill in the real values for the bound fields now that a row is available.
                foreach (var m in entry.MatchedFields)
                {
                    var col = m.Column.Contains('.') ? m.Column[(m.Column.LastIndexOf('.') + 1)..] : m.Column;
                    var cell = entry.FirstRow.FirstOrDefault(c =>
                        string.Equals(c.Column, col, StringComparison.OrdinalIgnoreCase) ||
                        c.Column.EndsWith($".{col}", StringComparison.OrdinalIgnoreCase));
                    if (cell is not null)
                    {
                        m.Value = cell.Value;
                    }
                }
            }
            catch (JsonException)
            {
                entry.Error = "Data extraction result unparseable.";
            }
        }

        diagnostics.SourceVisuals.AddRange(merged.Values);

        // ---- Data census ----------------------------------------------------------------------
        // Report row availability for EVERY visual, independent of drill-through matching, so
        // "which visuals actually have data" is always answerable from the email alone.
        var withRows = merged.Values.Where(v => v.FirstRow.Count > 0).ToList();
        var withoutRows = merged.Values.Where(v => v.FirstRow.Count == 0).ToList();

        diagnostics.Notes.Add(
            $"[{pageDisplayName}] Data census: {withRows.Count} of {merged.Count} visual(s) returned data. " +
            $"With rows: {(withRows.Count == 0 ? "(none)" : string.Join(" | ", withRows.Select(v => $"{v.Title} [{v.VisualType}] rows={v.RowCount}")))}");

        if (withoutRows.Count > 0)
        {
            diagnostics.Notes.Add(
                $"[{pageDisplayName}] No data returned by: " +
                string.Join(" | ", withoutRows.Select(v => $"{v.Title} [{v.VisualType}] ({v.Error ?? "empty"})")));
        }

        // Print the first row of every visual that has one, so the data is in the email regardless of
        // whether that visual was identified as a drill-through source.
        foreach (var v in withRows)
        {
            diagnostics.Notes.Add($"[{pageDisplayName}] DATA '{v.Title}' [{v.VisualType}] first row: {v.FirstRowSummary}");
        }

        // ---- Verdict --------------------------------------------------------------------------
        var sources = merged.Values.Where(v => v.IsDrillThroughSource).ToList();
        if (sources.Count == 0)
        {
            diagnostics.Notes.Add(
                $"[{pageDisplayName}] No drill-through source visual found by any path. " +
                $"Declared field(s): {(boundFields.Count == 0 ? "(unknown)" : string.Join(", ", boundFields.Select(f => $"{f.Table}.{f.Column}")))}.");
            return;
        }

        foreach (var s in sources)
        {
            var via = string.Join("+", s.DetectedBy);
            if (s.FirstRow.Count > 0)
            {
                diagnostics.Notes.Add(
                    $"[{pageDisplayName}] DRILL-THROUGH SOURCE '{s.Title}' [{s.VisualType}] (via {via}). " +
                    $"First data row: {s.FirstRowSummary}");
            }
            else
            {
                // Identified as a source, but no row exists to click — the decisive distinction.
                diagnostics.Notes.Add(
                    $"[{pageDisplayName}] DRILL-THROUGH SOURCE '{s.Title}' [{s.VisualType}] (via {via}), " +
                    $"but it returned NO data rows, so there is nothing to right-click. " +
                    $"{s.Error ?? "The visual rendered empty under the current effective identity."}");
            }
        }
    }

    // Runs a browser-side visual probe and returns its result array, tolerating any failure shape.
    private async Task<List<JsonElement>> EvaluateVisualProbeAsync(
        IPage page,
        DrillThroughDiagnostics diagnostics,
        string pageDisplayName,
        string helperName,
        string boundJson,
        string label)
    {
        string json;
        try
        {
            json = await page.EvaluateAsync<string>(
                $@"async (fields) => window.{helperName}
                    ? await window.{helperName}(fields)
                    : JSON.stringify({{ error: 'helper unavailable' }})",
                boundJson);
        }
        catch (Exception ex)
        {
            diagnostics.Notes.Add($"[{pageDisplayName}] {label} failed: {ex.Message}");
            return new List<JsonElement>();
        }

        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : json;
                diagnostics.Notes.Add($"[{pageDisplayName}] {label} returned no visuals: {err}");
                return new List<JsonElement>();
            }

            var items = doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
            diagnostics.Notes.Add(
                $"[{pageDisplayName}] {label}: probed {items.Count} visual(s), " +
                $"{items.Count(i => i.TryGetProperty("isDrillThroughSource", out var s) && s.ValueKind == JsonValueKind.True)} match.");
            return items;
        }
        catch (JsonException ex)
        {
            diagnostics.Notes.Add($"[{pageDisplayName}] {label} result unparseable: {ex.Message}");
            return new List<JsonElement>();
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static List<DrillThroughCell> ReadCells(JsonElement element, string arrayName, string columnProp)
    {
        var cells = new List<DrillThroughCell>();
        if (!element.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return cells;
        }

        foreach (var c in arr.EnumerateArray())
        {
            cells.Add(new DrillThroughCell
            {
                Column = GetString(c, columnProp),
                Value = c.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null
            });
        }

        return cells;
    }

    /// <summary>
    /// Best-effort attempt to open the context menu on a data element, trying SEVERAL gesture strategies
    /// in turn because Power BI visuals differ in how they accept a right-click: (1) a plain right-click,
    /// (2) hover-then-right-click (some charts only arm a mark on hover), (3) a right-click at the element's
    /// bounding-box center via the mouse (bypasses odd hit-testing), and (4) left-click to select then the
    /// keyboard context-menu key. Returns true as soon as one strategy fires without throwing.
    /// </summary>
    private async Task<bool> TryRightClickAsync(
        IPage page,
        ILocator locator,
        int timeoutMs,
        string checkpointContext,
        CancellationToken cancellationToken)
    {
        var budget = Math.Min(timeoutMs, 8000);

        try
        {
            if (await locator.CountAsync() == 0)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        // Strategy 1: plain forced right-click.
        try
        {
            await locator.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right, Timeout = budget, Force = true });
            return true;
        }
        catch { /* try next strategy */ }

        // Strategy 2: hover to arm the mark, then right-click.
        try
        {
            await locator.HoverAsync(new LocatorHoverOptions { Timeout = 3000, Force = true });
            await locator.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right, Timeout = budget, Force = true });
            return true;
        }
        catch { /* try next strategy */ }

        // Strategy 3: right-click at the element's bounding-box center via the page mouse (bypasses odd
        // per-visual hit-testing that can reject a locator click).
        try
        {
            var box = await locator.BoundingBoxAsync();
            if (box is not null && box.Width > 0 && box.Height > 0)
            {
                var x = box.X + box.Width / 2;
                var y = box.Y + box.Height / 2;
                await page.Mouse.MoveAsync(x, y);
                await page.Mouse.ClickAsync(x, y, new MouseClickOptions { Button = MouseButton.Right });
                return true;
            }
        }
        catch { /* try next strategy */ }

        // Strategy 4: left-click to select the data point, then press the keyboard context-menu key.
        try
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = budget, Force = true });
            await page.Keyboard.PressAsync("ContextMenu");
            return true;
        }
        catch { /* out of strategies */ }

        return false;
    }

    /// <summary>One table cell as read from the DOM: its query-ref plus whether it is a drillable
    /// body cell (as opposed to a column/row header, which only ever opens the header menu).</summary>
    private sealed class CellProbe
    {
        public string? QueryRef { get; set; }
        public bool Body { get; set; }
    }

    /// <summary>Mutable state shared across the recursive drill-through exploration of one report.</summary>
    private sealed class DrillExploreState
    {
        public int Performed;
        public readonly HashSet<string> VisitedPages = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> CheckedDestinations = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> DestinationsInProgress = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ProbedSourceVisuals = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Wall-clock deadline for the whole drill-through exploration phase. When reached the
        /// explorer stops gracefully and returns partial results instead of being force-killed by the
        /// global page timeout (which surfaces as a hard "Timeout â€¦ exceeded" Error).</summary>
        public DateTime Deadline = DateTime.MaxValue;

        public bool BudgetExpired => DateTime.UtcNow >= Deadline;
    }

    private static bool IsNativeCoverageComplete(IReadOnlyList<DrillThroughTarget> declaredTargets)
    {
        var actionableTargets = declaredTargets
            .Where(target => target.Fields.Count > 0)
            .GroupBy(
                target => string.IsNullOrWhiteSpace(target.PageDisplayName)
                    ? target.PageName
                    : target.PageDisplayName,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToList();

        return actionableTargets.Count > 0 && actionableTargets.All(group => group.Any(target =>
            target.Status == SanityStatus.Passed &&
            target.VerificationMethod == DrillThroughVerificationMethod.RealGesture));
    }

    /// <summary>
    /// DOM-FIRST, EXHAUSTIVE drill-through explorer. For the CURRENT report page this: (1) scans real data
    /// elements across candidate visuals (grid cells, row/column headers, chart marks), (2) right-clicks
    /// each to open the native Power BI context menu, (3) finds the "Drill through" item and reads the
    /// submenu of destination pages, (4) clicks each destination one at a time, verifies the page rendered
    /// without visual errors, and (5) RECURSES into the destination page to follow further drill-through
    /// levels (up to <see cref="HeadlessCheckOptions.MaxDrillThroughDepth"/>), also re-checking toggles
    /// there. This mirrors exactly what a user does and needs no metadata. Best-effort: anything not found
    /// is skipped and noted, never failed. Never saves or edits the report.
    /// </summary>
    private async Task ExploreDrillThroughAsync(
        IPage page,
        HeadlessCheckOptions options,
        List<InteractionCheckResult> results,
        DrillThroughDiagnostics diagnostics,
        IReadOnlyList<DrillThroughTarget> declaredTargets,
        IReadOnlyList<ReportVisualDefinition> visualDefinitions,
        PerformanceDiagnostics performance,
        DrillExploreState state,
        int depth,
        string pathPrefix,
        CancellationToken cancellationToken)
    {
        if (depth > options.MaxDrillThroughDepth)
        {
            return;
        }

        if (state.BudgetExpired)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Include the full path so the same destination can be verified from different source visuals or
        // filter contexts. MaxDrillThroughDepth remains the hard cycle guard for A -> B -> A chains.
        var currentInfo = await ActivePageInfoAsync(page);
        var currentKey = string.IsNullOrEmpty(currentInfo.Name) ? currentInfo.DisplayName : currentInfo.Name;
        if (!string.IsNullOrEmpty(currentKey) && !state.VisitedPages.Add($"{depth}:{pathPrefix}:{currentKey}"))
        {
            // An exact duplicate traversal path was already processed.
            return;
        }

        if (depth > 1)
        {
            var sourceStopwatch = Stopwatch.StartNew();
            await DiscoverDrillThroughSourcesAsync(
                page,
                diagnostics,
                declaredTargets,
                visualDefinitions,
                currentInfo.Name,
                currentInfo.DisplayName,
                cancellationToken);
            AddPhase(performance, "Recursive source discovery", sourceStopwatch, scope: $"depth {depth}: {pathPrefix}");
        }

        var frame = page.FrameLocator($"#{ContainerId} iframe");
        var knownSourceVisuals = diagnostics.SourceVisuals
            .Where(v => v.IsDrillThroughSource &&
                        !string.IsNullOrWhiteSpace(v.VisualName) &&
                        string.Equals(v.Page, currentInfo.DisplayName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.VisualName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        var sourceVisuals = knownSourceVisuals
            .Where(source => !state.ProbedSourceVisuals.Contains(
                $"{currentInfo.DisplayName}:{source.VisualName}"))
            .ToList();

        if (knownSourceVisuals.Count > 0 && sourceVisuals.Count == 0)
        {
            diagnostics.Notes.Add(
                $"[{pathPrefix}] native menus already enumerated for every drill-through source visual; skipped duplicate probing.");
            return;
        }

        // A table/matrix renders its cells asynchronously after the page (or bookmark) is applied. Probing
        // immediately can miss a drillable summary table. When metadata identifies source visuals, wait only
        // inside a source container that is actually visible in this bookmark state. Waiting frame-wide for
        // ten seconds made every unrelated bookmark pay the full timeout even though its source was hidden.
        var waitedForVisibleSource = false;
        foreach (var source in sourceVisuals)
        {
            var container = VisualContainerLocator(frame, source.VisualName);
            try
            {
                if (!await container.IsVisibleAsync())
                {
                    continue;
                }

                waitedForVisibleSource = true;
                await container.Locator(
                        "[role='gridcell'], [role='rowheader'], [role='cell'], " +
                        "svg rect.column, svg rect.bar, svg circle, svg path.line, svg path.slice, " +
                        ".selectableDataPoint")
                    .First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 10_000 });
                break;
            }
            catch (TimeoutException)
            {
                // The visible source may be an empty visual; other candidate families still apply.
                break;
            }
            catch
            {
                // A bookmark can replace the visual DOM during the visibility check; probe what remains.
            }
        }

        // Without metadata there is no authoritative source container to scope to, so preserve the generic
        // wait used by the best-effort discovery path rather than reducing coverage.
        if (sourceVisuals.Count == 0 && !waitedForVisibleSource)
        {
            try
            {
                await frame.Locator("[role='gridcell']")
                    .First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 10_000 });
            }
            catch (TimeoutException)
            {
                // No table in this page/bookmark state; the chart-mark families below still apply.
            }
        }

        // DIAGNOSTIC CENSUS: the probe has repeatedly reported zero table cells on a page that visibly
        // contains matrix visuals, so report what the frame ACTUALLY contains rather than inferring it from
        // a selector that may not match. This counts generic structures and lists the visual containers by
        // name, which pins down whether we are querying the wrong frame or merely the wrong selector.
        try
        {
            var census = await frame.Locator(":root").EvaluateAsync<string>(
                @"root => {
                    const d = root.ownerDocument;
                    const count = sel => { try { return d.querySelectorAll(sel).length; } catch { return -1; } };
                    const visuals = Array.from(d.querySelectorAll('visual-container, .visualContainer'))
                        .map(v => (v.querySelector('.visualTitle, [class*=title]')?.textContent
                            || v.getAttribute('aria-label') || '?').trim().slice(0, 60));
                    return JSON.stringify({
                        gridcell: count('[role=\""gridcell\""]'),
                        gridcellClasses: Array.from(d.querySelectorAll('[role=\""gridcell\""]'))
                            .map(c => (c.className || '?').toString().slice(0, 60)),
                        rowheader: count('[role=\""rowheader\""]'),
                        cellsContainer: count('.scrollable-cells-container'),
                        canvas: count('canvas'),
                        textSvg: count('svg text'),
                        // Does the literal rendered value exist as DOM text anywhere? This is the decisive
                        // question for whether a real right-click on a data value is possible at all.
                        // Search the WHOLE tree for the known values rather than sampling the first N
                        // nodes: an earlier capped walk stopped inside the header and wrongly suggested
                        // the table values were absent.
                        valueHits: (() => {
                            const wanted = ['IA', 'STAFF', 'VENDOR', 'Field Inspection'];
                            const hits = {};
                            for (const w of wanted) { hits[w] = 0; }
                            const walk = d.createTreeWalker(d.body, NodeFilter.SHOW_TEXT);
                            let n;
                            while ((n = walk.nextNode())) {
                                const t = (n.textContent || '').trim();
                                if (hits[t] !== undefined) { hits[t]++; }
                            }
                            return hits;
                        })(),
                        totalTextNodes: (() => {
                            let c = 0;
                            const walk = d.createTreeWalker(d.body, NodeFilter.SHOW_TEXT);
                            while (walk.nextNode()) { c++; }
                            return c;
                        })(),
                        textNodes: (() => {
                            const out = [];
                            const walk = d.createTreeWalker(d.body, NodeFilter.SHOW_TEXT);
                            let n;
                            while ((n = walk.nextNode()) && out.length < 40) {
                                const t = (n.textContent || '').trim();
                                if (t && t.length < 30) { out.push(t); }
                            }
                            return out;
                        })(),
                        pivot: count('.pivotTable, .tableEx, .columnChart, .barChart'),
                        svg: count('svg'),
                        rect: count('svg rect'),
                        visualContainers: visuals.length,
                        visuals: visuals.filter(v => v && v !== '?')
                    });
                }");
            diagnostics.Notes.Add($"[{pathPrefix}] frame census: {census}");
        }
        catch (Exception ex)
        {
            diagnostics.Notes.Add($"[{pathPrefix}] frame census failed: {ex.Message}");
        }

        // Candidate data elements to right-click, most-to-least likely to carry a drill-through.
        //
        // Prefer chart data marks before generic grid cells when metadata cannot identify a source visual.
        // "Select Row" cells are accessibility/action controls rather than business data; right-clicking one
        // can expose a destination but omit the bar/category context required by that destination.
        var candidateFamilies = new (string Kind, ILocator Locator)[]
        {
            ("chart mark", frame.Locator("svg rect.column, svg rect.bar, svg circle, svg path.line, svg path.slice, .columnChart rect, .barChart rect, .scatterChart circle, .lineChart circle, .donutChart path, .pieChart path")),
            ("data point", frame.Locator("[class*='dataPoint'], [class*='data-point'], .selectableDataPoint")),
            ("data cell", frame.Locator("[role='gridcell']").Filter(new() { HasNotTextString = "Select Row" })),
            ("row header", frame.Locator("[role='rowheader']")),
        };

        // VISUAL-SCOPED CANDIDATES (preferred): ask the SDK which DATA visuals are on this page, then
        // right-click INSIDE each one. Generic frame-wide selectors keep landing on navigation buttons
        // because those are the only elements they happen to match; scoping by the visual's own container
        // guarantees the gesture happens on the visual that can actually offer a drill-through.
        var visualScoped = new List<(string Kind, ILocator Locator)>();
        try
        {
            var visualsJson = await page.EvaluateAsync<string>(
                "() => window.__sanityDataVisuals ? window.__sanityDataVisuals() : '[]'");

            using var doc = JsonDocument.Parse(visualsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var v in doc.RootElement.EnumerateArray())
                {
                    var name = v.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var source = sourceVisuals.FirstOrDefault(item =>
                        string.Equals(item.VisualName, name, StringComparison.OrdinalIgnoreCase));
                    if (source is null)
                    {
                        continue;
                    }

                    var title = v.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var type = v.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                    title = string.IsNullOrWhiteSpace(title) ? source.Title : title;

                    names.Add($"{title} [{type}]");

                    // Power BI labels the visual container ITSELF with the title via aria-label; earlier
                    // revisions only matched a DESCENDANT carrying the label, which is why every
                    // visual-scoped family reported zero candidates while the frame census proved the
                    // cells existed. Match both shapes, and accept the labelled element itself as the
                    // container when no wrapper element is present.
                    var container = VisualContainerLocator(frame, source.VisualName);

                    visualScoped.Add((
                        $"source visual '{title}' [{type}] data cell",
                        container.Locator("[role='gridcell'], [role='rowheader'], [role='cell']")));
                    visualScoped.Add((
                        $"source visual '{title}' [{type}] chart mark",
                        container.Locator(
                            "svg rect.column, svg rect.bar, svg circle, svg path.line, svg path.slice, " +
                            ".selectableDataPoint, [class*='dataPoint'], [class*='data-point']")));
                }

                diagnostics.Notes.Add(
                    $"[{pathPrefix}] SDK data visuals ({names.Count}): {string.Join(" | ", names)}");
            }
            else
            {
                diagnostics.Notes.Add($"[{pathPrefix}] SDK data visuals unavailable: {visualsJson}");
            }
        }
        catch (Exception ex)
        {
            diagnostics.Notes.Add($"[{pathPrefix}] SDK data visual lookup failed: {ex.Message}");
        }

        // When metadata identifies source visuals, never fall back to frame-wide candidates. The audit
        // proved those selectors hit cards, tab navigators, slicers, and Back controls. A missing scoped
        // source is an honest "not rendered in this state", not permission to click unrelated chrome.
        candidateFamilies = visualScoped.Count > 0 || sourceVisuals.Count > 0
            ? visualScoped.ToArray()
            : candidateFamilies;

        // VALUE-TEXT CANDIDATES (highest fidelity): right-click the ACTUAL rendered value, e.g. the "IA"
        // cell of the Assignee Type Table. Source discovery already exported those values, so target them
        // by their own text rather than by structural role. Power BI renders table cells as plain divs and
        // chart labels as <text> inside svg, neither of which carries role="gridcell", which is why the
        // role-based families found only layout containers. Placed first so a genuine data value is
        // right-clicked before any chrome element is considered.
        var valueCandidates = new List<(string Kind, ILocator Locator)>();
        var seenValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var visual in sourceVisuals)
        {
            var container = VisualContainerLocator(frame, visual.VisualName);
            foreach (var cell in visual.FirstRow)
            {
                var value = cell.Value?.Trim();
                var visualValueKey = $"{visual.VisualName}:{value}";

                // Only non-numeric, reasonably short values: a category label like "IA" identifies a data
                // point, whereas "0" matches axis ticks and chrome all over the canvas.
                if (string.IsNullOrEmpty(value)
                    || value.Length > 40
                    || double.TryParse(value, out _)
                    || !seenValues.Add(visualValueKey))
                {
                    continue;
                }

                valueCandidates.Add((
                    $"value '{value}' ({visual.Title})",
                    container.GetByText(value, new LocatorGetByTextOptions { Exact = true })));
            }
        }

        if (valueCandidates.Count > 0)
        {
            diagnostics.Notes.Add(
                $"[{pathPrefix}] value-text candidates: {string.Join(", ", valueCandidates.Select(v => v.Kind))}");
        }

        candidateFamilies = valueCandidates.Concat(candidateFamilies).ToArray();

        const int maxMarksPerFamily = 8;
        var probedSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (kind, locator) in candidateFamilies)
        {
            if (state.BudgetExpired)
            {
                return;
            }

            if (state.Performed >= options.MaxInteractionsPerReport)
            {
                diagnostics.CapReached = true;
                diagnostics.Notes.Add(
                    $"Interaction cap ({options.MaxInteractionsPerReport}) reached during DOM drill-through exploration.");
                return;
            }

            int markCount;
            try { markCount = await locator.CountAsync(); } catch { markCount = 0; }

            diagnostics.Notes.Add($"[{pathPrefix}] '{kind}' candidates found: {markCount}.");

            if (markCount == 0)
            {
                continue;
            }

            diagnostics.DrillablePointsFound += markCount;

            var attempts = Math.Min(markCount, maxMarksPerFamily);
            for (var i = 0; i < attempts; i++)
            {
                if (state.BudgetExpired)
                {
                    return;
                }

                if (state.Performed >= options.MaxInteractionsPerReport)
                {
                    diagnostics.CapReached = true;
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var mark = locator.Nth(i);

                // Identify the owning visual and bound field BEFORE right-clicking, so the probe record is
                // complete even when the menu turns out to have no drill-through.
                var owningVisual = await OwningVisualNameAsync(mark);
                var sourceVisual = sourceVisuals.FirstOrDefault(source =>
                    kind.Contains(source.Title, StringComparison.OrdinalIgnoreCase) ||
                    owningVisual.Contains(source.Title, StringComparison.OrdinalIgnoreCase) ||
                    owningVisual.Contains(source.VisualName, StringComparison.OrdinalIgnoreCase));
                var sourceKey = sourceVisual is null
                    ? null
                    : $"{currentInfo.DisplayName}:{sourceVisual.VisualName}";
                if (sourceKey is not null && state.ProbedSourceVisuals.Contains(sourceKey))
                {
                    continue;
                }
                var probe = new DrillThroughProbe
                {
                    Page = currentInfo.DisplayName,
                    Visual = owningVisual,
                    ElementKind = kind,
                    FieldRef = await SafeAttributeAsync(mark, "title")
                               ?? await SafeAttributeAsync(mark, "aria-label"),
                    ClickedData = await ReadClickedDataAsync(mark),
                    SourceRow = sourceVisual?.FirstRow
                        .Select(cell => new DrillThroughCell { Column = cell.Column, Value = cell.Value })
                        .ToList() ?? new List<DrillThroughCell>()
                };
                diagnostics.Probes.Add(probe);

                // Open the context menu on this data element.
                await page.EvaluateAsync("() => window.__sanityBeginInteractions && window.__sanityBeginInteractions()");
                var opened = await TryRightClickAsync(
                    page,
                    mark,
                    options.PageTimeoutMs,
                    $"{pathPrefix}-{kind}-{i + 1}",
                    cancellationToken);
                if (!opened)
                {
                    continue;
                }
                if (sourceKey is not null)
                {
                    probedSourceKeys.Add(sourceKey);
                }

                // Record every menu item that appeared, so a wrong-element right-click (e.g. a sort menu)
                // is diagnosable rather than looking identical to "this visual has no drill-through".
                probe.MenuItems = await ReadMenuItemTextsAsync(frame);
                probe.HasDrillThrough = probe.MenuItems.Any(
                    m => string.Equals(m, options.DrillThroughMenuText, StringComparison.OrdinalIgnoreCase));

                // Read the "Drill through" submenu of destination page(s) from the native menu.
                var targets = await EnumerateDrillThroughTargetsAsync(
                    page,
                    frame,
                    options,
                    $"{pathPrefix}-{kind}-{i + 1}",
                    cancellationToken);
                probe.Targets = targets;

                if (targets.Count == 0)
                {
                    await DismissMenuAsync(page);
                    continue;
                }

                diagnostics.SourceVisualsWithDrillThrough++;

                foreach (var targetLabel in targets)
                {
                    if (state.CheckedDestinations.Contains(targetLabel) ||
                        !state.DestinationsInProgress.Add(targetLabel))
                    {
                        continue;
                    }

                    if (state.Performed >= options.MaxInteractionsPerReport)
                    {
                        state.DestinationsInProgress.Remove(targetLabel);
                        diagnostics.CapReached = true;
                        await DismissMenuAsync(page);
                        return;
                    }

                    if (!diagnostics.DiscoveredTargets.Contains(targetLabel, StringComparer.OrdinalIgnoreCase))
                    {
                        diagnostics.DiscoveredTargets.Add(targetLabel);
                    }

                    state.Performed++;
                    var drillPath = string.IsNullOrEmpty(pathPrefix) ? targetLabel : $"{pathPrefix} -> {targetLabel}";
                    var dataContext = string.IsNullOrWhiteSpace(probe.ClickedDataSummary)
                        ? string.Empty
                        : $" using [{probe.ClickedDataSummary}]";
                    var result = new InteractionCheckResult
                    {
                        Kind = InteractionKind.DrillThrough,
                        Target = $"Drill through ({kind}) to '{targetLabel}'{dataContext}",
                        Page = currentInfo.DisplayName
                    };
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        // Re-open the menu for THIS target (a previous target click navigated away/closed it).
                        await page.EvaluateAsync("() => window.__sanityBeginInteractions && window.__sanityBeginInteractions()");
                        var reopened = await TryRightClickAsync(
                            page,
                            mark,
                            options.PageTimeoutMs,
                            $"reopen-{drillPath}",
                            cancellationToken);
                        var clicked = reopened && await ClickDrillThroughTargetAsync(
                            page,
                            frame,
                            options,
                            targetLabel,
                            drillPath,
                            cancellationToken);

                        if (!clicked)
                        {
                            result.Status = SanityStatus.Pending;
                            result.Message = $"Drill through '{targetLabel}' ({drillPath}) could not be clicked; skipped.";
                            await DismissMenuAsync(page);
                        }

                        else
                        {
                            state.CheckedDestinations.Add(targetLabel);
                            await page.WaitForTimeoutAsync(Math.Max(options.InteractionSettleMs, 1500));

                            var destInfo = await ActivePageInfoAsync(page);
                            var errors = await DrainInteractionErrorsAsync(page);
                            result.Errors = errors;
                            result.ErrorCount = errors.Count;

                            var landed = string.IsNullOrEmpty(destInfo.DisplayName) ? targetLabel : destInfo.DisplayName;
                            if (errors.Count > 0)
                            {
                                result.Status = SanityStatus.Failed;
                                result.Message =
                                    $"Drilled through to '{landed}' ({drillPath}) which rendered with {errors.Count} visual error(s).";
                            }
                            else
                            {
                                result.Status = SanityStatus.Passed;
                                result.Message = $"Drilled through to '{landed}' ({drillPath}); rendered without visual errors.";
                            }

                            var declaredTarget = declaredTargets.FirstOrDefault(target =>
                                string.Equals(target.PageDisplayName, targetLabel, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target.PageName, targetLabel, StringComparison.OrdinalIgnoreCase));
                            if (declaredTarget is not null)
                            {
                                declaredTarget.Status = result.Status;
                                declaredTarget.Message = result.Message;
                                declaredTarget.Errors = errors.ToList();
                                declaredTarget.VerificationMethod = DrillThroughVerificationMethod.RealGesture;
                            }

                            // Also flip toggles that only exist on this destination page.
                            await RunToggleInteractionsAsync(
                                page,
                                frame,
                                options,
                                results,
                                cancellationToken,
                                reportWhenNone: false);

                            // RECURSE: follow further drill-through levels from the destination page.
                            var recursionStopwatch = Stopwatch.StartNew();
                            await ExploreDrillThroughAsync(
                                page, options, results, diagnostics, declaredTargets, visualDefinitions,
                                performance, state, depth + 1, drillPath, cancellationToken);
                            AddPhase(performance, "Drill recursion", recursionStopwatch, scope: $"depth {depth + 1}: {drillPath}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.Status = SanityStatus.Pending;
                        result.Message = $"Drill through '{targetLabel}' ({drillPath}) threw: {ex.Message}";
                        _logger.LogDebug(ex, "DOM drill-through to {Target} failed.", targetLabel);
                    }
                    finally
                    {
                        state.DestinationsInProgress.Remove(targetLabel);
                        sw.Stop();
                        result.DurationMs = sw.ElapsedMilliseconds;
                        results.Add(result);

                        // Return to the page this branch started on before trying the next target/mark.
                        await ReturnToBaselineAsync(page, cancellationToken);
                        if (!string.IsNullOrEmpty(currentInfo.Name))
                        {
                            await SetActivePageForInteractionAsync(page, currentInfo.Name, options.InteractionSettleMs);
                        }
                    }
                }
            }
        }

        state.ProbedSourceVisuals.UnionWith(probedSourceKeys);
    }

    /// <summary>
    /// Walks up from a data element to its owning visual container and returns that visual's accessible
    /// name, so the email can list exactly which visuals were probed for drill-through.
    /// </summary>
    private static string EscapeForCss(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static ILocator VisualContainerLocator(IFrameLocator frame, string visualName)
    {
        var labelId = EscapeForCss($"visualsLabel-{visualName}");
        return frame.Locator(
            $"visual-container:has([id={labelId}]), " +
            $".visualContainer:has([id={labelId}])").First;
    }

    private static async Task<string> OwningVisualNameAsync(ILocator mark)
    {
        try
        {
            var name = await mark.EvaluateAsync<string?>(
                @"el => {
                    const host = el.closest('.visualContainer');
                    if (!host) return null;
                    const label = host.getAttribute('aria-label');
                    return label ? label.trim() : null;
                }");
            return string.IsNullOrWhiteSpace(name) ? "(unnamed visual)" : name;
        }
        catch
        {
            return "(unknown visual)";
        }
    }

    /// <summary>Reads an attribute from a locator, returning null when it is absent or unreadable.</summary>
    private static async Task<string?> SafeAttributeAsync(ILocator locator, string attribute)
    {
        try
        {
            return await locator.GetAttributeAsync(attribute, new LocatorGetAttributeOptions { Timeout = 1000 });
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<DrillThroughCell>> ReadClickedDataAsync(ILocator locator)
    {
        try
        {
            var json = await locator.EvaluateAsync<string>(
                @"element => JSON.stringify({
                    text: (element.textContent || '').trim().slice(0, 200),
                    attributes: Array.from(element.attributes || [])
                        .filter(attribute => attribute.name.startsWith('data-')
                            || attribute.name === 'aria-label'
                            || attribute.name === 'title')
                        .map(attribute => ({ name: attribute.name, value: attribute.value.slice(0, 200) }))
                })");
            using var document = JsonDocument.Parse(json);
            var cells = new List<DrillThroughCell>();
            var root = document.RootElement;

            if (root.TryGetProperty("text", out var textElement))
            {
                var text = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    cells.Add(new DrillThroughCell { Column = "rendered text", Value = text });
                }
            }

            if (root.TryGetProperty("attributes", out var attributes) &&
                attributes.ValueKind == JsonValueKind.Array)
            {
                foreach (var attribute in attributes.EnumerateArray())
                {
                    var name = GetString(attribute, "name");
                    var value = GetString(attribute, "value");
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
                    {
                        cells.Add(new DrillThroughCell { Column = name, Value = value });
                    }
                }
            }

            return cells;
        }
        catch
        {
            return new List<DrillThroughCell>();
        }
    }

    /// <summary>
    /// With a context menu OPEN, finds the "Drill through" item and returns the labels of its submenu
    /// destination page(s). Works by snapshotting the visible menu-item texts, hovering "Drill through"
    /// to reveal its flyout, and returning the newly-appeared items. Returns an empty list when there is
    /// no drill-through on the right-clicked data point. Never throws.
    /// </summary>
    private async Task<List<string>> EnumerateDrillThroughTargetsAsync(
        IPage page,
        IFrameLocator frame,
        HeadlessCheckOptions options,
        string checkpointContext,
        CancellationToken cancellationToken)
    {
        var targets = new List<string>();
        try
        {
            var drillItem = DrillThroughParentLocator(frame, options);
            if (await drillItem.CountAsync() == 0)
            {
                return targets;
            }

            var items = frame.Locator("[data-testid^=\"pbimenu-item.\"]");
            var existingTestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingCount = await items.CountAsync();
            for (var i = 0; i < existingCount; i++)
            {
                var testId = await SafeAttributeAsync(items.Nth(i), "data-testid");
                if (!string.IsNullOrWhiteSpace(testId))
                {
                    existingTestIds.Add(testId);
                }
            }

            // CLICK (not hover) to expand the flyout. Power BI marks the expanded parent with
            // aria-expanded="true" and renders the destination list in a SEPARATE cdk-overlay-pane as its
            // own <pbi-menu role="menu">, so the targets are not siblings of the parent in the DOM.
            try
            {
                await drillItem.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            }
            catch { return targets; }

            // Read only items newly introduced by expanding the submenu. The original context menu remains
            // mounted in another overlay and contains unrelated commands such as Freeze/Unfreeze row headers;
            // treating every pbimenu item as a destination causes false recursive navigation.
            var parentTestId = $"pbimenu-item.{options.DrillThroughMenuText}";
            var submenuDeadline = DateTime.UtcNow.AddSeconds(2);
            var count = existingCount;
            while (count <= existingCount && DateTime.UtcNow < submenuDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await page.WaitForTimeoutAsync(100);
                count = await items.CountAsync();
            }

            if (count <= existingCount)
            {
                return targets;
            }

            for (var i = 0; i < count; i++)
            {
                var item = items.Nth(i);

                string? testId = null;
                try { testId = await item.GetAttributeAsync("data-testid", new LocatorGetAttributeOptions { Timeout = 1000 }); }
                catch { continue; }

                if (string.IsNullOrEmpty(testId) ||
                    existingTestIds.Contains(testId) ||
                    string.Equals(testId, parentTestId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hasPopup = await SafeAttributeAsync(item, "aria-haspopup");
                if (string.Equals(hasPopup, "true", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? label = null;
                try { label = await item.GetAttributeAsync("title", new LocatorGetAttributeOptions { Timeout = 1000 }); }
                catch { }

                if (string.IsNullOrWhiteSpace(label))
                {
                    label = testId["pbimenu-item.".Length..];
                }

                label = label.Trim();
                if (label.Length == 0 ||
                    string.Equals(label, options.DrillThroughMenuText, StringComparison.OrdinalIgnoreCase) ||
                    IsNoiseMenuLabel(label) ||
                    targets.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                targets.Add(label);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Enumerating drill-through submenu targets failed.");
        }

        return targets;
    }

    /// <summary>
    /// With a context menu open, expands "Drill through" and clicks the destination page entry for
    /// <paramref name="targetLabel"/>. Returns true if the destination was clicked.
    /// </summary>
    private async Task<bool> ClickDrillThroughTargetAsync(
        IPage page,
        IFrameLocator frame,
        HeadlessCheckOptions options,
        string targetLabel,
        string drillPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var drillItem = DrillThroughParentLocator(frame, options);
            if (await drillItem.CountAsync() == 0)
            {
                return false;
            }

            // Click to expand the flyout, unless a previous click already left it expanded.
            var expanded = false;
            try { expanded = await drillItem.GetAttributeAsync("aria-expanded", new LocatorGetAttributeOptions { Timeout = 1000 }) == "true"; }
            catch { }

            if (!expanded)
            {
                await drillItem.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                await page.WaitForTimeoutAsync(600);
            }

            var targetItem = MenuItemByLabelLocator(frame, targetLabel);
            if (await targetItem.CountAsync() == 0)
            {
                return false;
            }

            await targetItem.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clicking drill-through target '{Target}' failed.", targetLabel);
            return false;
        }
    }

    /// <summary>
    /// Locates the "Drill through" PARENT menu item, i.e. the entry that owns the destination submenu.
    /// The aria-haspopup filter is what separates it from a destination such as "Level 1 Drill Through".
    /// </summary>
    private static ILocator DrillThroughParentLocator(IFrameLocator frame, HeadlessCheckOptions options)
    {
        var label = options.DrillThroughMenuText;
        return frame.Locator(
            $"[data-testid=\"pbimenu-item.{label}\"][aria-haspopup=\"true\"], " +
            $"[data-testid=\"pbimenu-item.{label}\"], " +
            $"[role=\"menuitem\"][aria-haspopup=\"true\"][title=\"{label}\"], " +
            $"[role=\"menuitem\"][aria-haspopup=\"true\"]:has-text(\"{label}\")").First;
    }

    /// <summary>Locates a specific menu item by its exact label.</summary>
    private static ILocator MenuItemByLabelLocator(IFrameLocator frame, string label)
    {
        return frame.Locator(
            $"[data-testid=\"pbimenu-item.{label}\"], [role=\"menuitem\"][title=\"{label}\"]").First;
    }

    /// <summary>Reads the visible context-menu item texts inside the report iframe.</summary>
    private static async Task<List<string>> ReadMenuItemTextsAsync(IFrameLocator frame)
    {
        var texts = new List<string>();
        try
        {
            // Prefer the exact label carried in the title attribute of each pbimenu item; fall back to the
            // rendered inner text. Reading across ALL overlay panes captures submenu flyouts too, since
            // Power BI renders each submenu in its own cdk-overlay-pane at the same document level.
            var items = frame.Locator("[data-testid^=\"pbimenu-item.\"], [role=\"menuitem\"]");
            var count = await items.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var item = items.Nth(i);
                string? t = null;
                try { t = await item.GetAttributeAsync("title", new LocatorGetAttributeOptions { Timeout = 1000 }); }
                catch { /* fall through to inner text */ }
                if (string.IsNullOrWhiteSpace(t))
                {
                    try { t = await item.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1500 }); }
                    catch { t = null; }
                }
                if (!string.IsNullOrWhiteSpace(t) && !texts.Contains(t.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    texts.Add(t.Trim());
                }
            }
        }
        catch
        {
            // No menu items readable; return whatever we have.
        }
        return texts;
    }

    /// <summary>Standard Power BI context-menu entries that are never drill-through destinations.</summary>
    private static bool IsNoiseMenuLabel(string label)
    {
        string[] noise =
        {
            "Show as a table", "Show data point as a table", "Include", "Exclude", "Copy",
            "Analyze", "Sort", "Sort ascending", "Sort descending", "Group", "Summarize",
            "Focus mode", "Spotlight", "Get insights", "See records", "Expand", "Collapse",
            "Drill down", "Drill up", "Show next level", "Expand to next level", "Add note"
        };
        return noise.Any(n => label.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Dismisses any open context menu by pressing Escape.</summary>
    private static async Task DismissMenuAsync(IPage page)
    {
        try { await page.Keyboard.PressAsync("Escape"); }
        catch { /* best-effort */ }
        try { await page.WaitForTimeoutAsync(150); } catch { }
    }

    /// <summary>Reads the active page's name/displayName/isHidden via the host-page SDK helper.</summary>
    private static async Task<(string Name, string DisplayName, bool IsHidden)> ActivePageInfoAsync(IPage page)
    {
        try
        {
            var json = await page.EvaluateAsync<string?>(
                "async () => window.__sanityActivePageInfo ? await window.__sanityActivePageInfo() : null");
            if (string.IsNullOrWhiteSpace(json))
            {
                return (string.Empty, string.Empty, false);
            }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var display = root.TryGetProperty("displayName", out var d) ? d.GetString() ?? name : name;
            var hidden = root.TryGetProperty("isHidden", out var h) && h.ValueKind == JsonValueKind.True;
            return (name, display, hidden);
        }
        catch
        {
            return (string.Empty, string.Empty, false);
        }
    }


    /// <summary>Parses the JSON array returned by <c>window.__sanityDrainInteractionErrors</c>.</summary>
    private static List<VisualError> ParseVisualErrors(string? json)
    {
        var errors = new List<VisualError>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return errors;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return errors;
            }
            foreach (var err in doc.RootElement.EnumerateArray())
            {
                var visual = err.TryGetProperty("visual", out var v) ? v.GetString() : null;
                var message = err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText();
                errors.Add(new VisualError { Visual = visual, Message = message ?? "Unknown visual error." });
            }
        }
        catch (JsonException)
        {
            // Leave errors empty on unparseable payloads.
        }
        return errors;
    }

    /// <summary>A visible (directly navigable) report page discovered via the host-page SDK.</summary>
    private readonly record struct VisiblePage(string Name, string DisplayName);

    /// <summary>Enumerates the report's visible pages via the host-page SDK (never the cross-origin iframe).</summary>
    private async Task<List<VisiblePage>> GetVisiblePagesForInteractionAsync(IPage page)
    {
        try
        {
            var json = await page.EvaluateAsync<string?>(
                "() => window.__sanityVisiblePages ? window.__sanityVisiblePages() : '[]'");
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<VisiblePage>();
            }

            using var doc = JsonDocument.Parse(json);
            var list = new List<VisiblePage>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var display = item.TryGetProperty("displayName", out var d) ? d.GetString() ?? name : name;
                list.Add(new VisiblePage(name, display));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate visible pages for the interaction pass.");
            return new List<VisiblePage>();
        }
    }

    /// <summary>Activates a report page by internal name via the host-page SDK and waits for it to settle.</summary>
    private async Task<bool> SetActivePageForInteractionAsync(IPage page, string name, int settleMs)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                "async ({ name, settleMs }) => window.__sanitySetActivePage ? await window.__sanitySetActivePage(name, settleMs) : false",
                new { name, settleMs });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not activate page {Page} for the interaction pass.", name);
            return false;
        }
    }

    /// <summary>An applyable (leaf) bookmark discovered via the host-page SDK.</summary>
    private readonly record struct ApplyableBookmark(string Name, string DisplayName);

    /// <summary>Enumerates applyable (leaf) bookmarks via the host-page SDK.</summary>
    private async Task<List<ApplyableBookmark>> GetApplyableBookmarksAsync(IPage page)
    {
        try
        {
            var json = await page.EvaluateAsync<string?>(
                "() => window.__sanityApplyableBookmarks ? window.__sanityApplyableBookmarks() : '[]'");
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<ApplyableBookmark>();
            }

            using var doc = JsonDocument.Parse(json);
            var list = new List<ApplyableBookmark>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var display = item.TryGetProperty("displayName", out var d) ? d.GetString() ?? name : name;
                if (!string.IsNullOrEmpty(name))
                {
                    list.Add(new ApplyableBookmark(name, display));
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate applyable bookmarks for the interaction pass.");
            return new List<ApplyableBookmark>();
        }
    }

    /// <summary>Applies a bookmark by name via the host-page SDK and waits for the re-render to settle.</summary>
    private async Task<bool> ApplyBookmarkForInteractionAsync(IPage page, string name, int settleMs)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                "async ({ name, settleMs }) => window.__sanityApplyBookmark ? await window.__sanityApplyBookmark(name, settleMs) : false",
                new { name, settleMs });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply bookmark {Bookmark} for the interaction pass.", name);
            return false;
        }
    }


    /// <summary>
    /// Flips toggle-style custom visuals (e.g. the BENE.BIZ Toggle Switch). We identify candidate visuals
    /// by the configured match text appearing on the visual chrome/title, click them to switch state, wait
    /// for the report to settle, and record any errors surfaced while the new state was active.
    /// </summary>
    private async Task RunToggleInteractionsAsync(
        IPage page,
        IFrameLocator frame,
        HeadlessCheckOptions options,
        List<InteractionCheckResult> results,
        CancellationToken cancellationToken,
        bool reportWhenNone = true)
    {
        // Toggle custom visuals still render inside a standard visual container; match by the visual's
        // aria-label/title (which carries the visual name/type) containing the configured token.
        var toggles = frame.Locator(
            $"visual-container:has([aria-label*='{options.ToggleVisualMatch}' i]), " +
            $".visualContainer:has([aria-label*='{options.ToggleVisualMatch}' i]), " +
            $"[aria-label*='{options.ToggleVisualMatch}' i]");

        int count;
        try
        {
            count = await toggles.CountAsync();
        }
        catch
        {
            count = 0;
        }

        if (count == 0)
        {
            if (reportWhenNone)
            {
                results.Add(new InteractionCheckResult
                {
                    Kind = InteractionKind.Toggle,
                    Status = SanityStatus.Pending,
                    Message = $"No toggle-style visual matching '{options.ToggleVisualMatch}' was found."
                });
            }
            return;
        }

        for (var i = 0; i < count && i < options.MaxInteractionsPerReport; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toggle = toggles.Nth(i);
            var label = await SafeLabelAsync(toggle) ?? $"Toggle #{i + 1}";
            var result = new InteractionCheckResult { Kind = InteractionKind.Toggle, Target = label };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await page.EvaluateAsync("() => window.__sanityBeginInteractions && window.__sanityBeginInteractions()");
                await toggle.ClickAsync(new LocatorClickOptions { Timeout = options.PageTimeoutMs, Force = true });
                await page.WaitForTimeoutAsync(options.InteractionSettleMs);

                var errors = await DrainInteractionErrorsAsync(page);
                result.Errors = errors;
                result.ErrorCount = errors.Count;
                result.Status = errors.Count == 0 ? SanityStatus.Passed : SanityStatus.Failed;
                result.Message = errors.Count == 0
                    ? $"Toggled '{label}' with no visual errors."
                    : $"Toggling '{label}' surfaced {errors.Count} visual error(s).";
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                result.Status = SanityStatus.Pending;
                result.Message = $"Could not toggle '{label}': {ex.Message}";
            }

            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            results.Add(result);
        }
    }

    /// <summary>
    /// Returns the report to its original (baseline) page after a drill-through navigated away, using
    /// Power BI's built-in "Back" affordance where available and otherwise ignoring failures. Best-effort:
    /// the next interaction re-checks from wherever the report lands.
    /// </summary>
    private async Task ReturnToBaselineAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            // The drill-through destination shows a "Back" button/link that returns to the source page.
            var frame = page.FrameLocator($"#{ContainerId} iframe");
            var back = frame.GetByText("Back", new FrameLocatorGetByTextOptions { Exact = false }).First;
            if (await back.IsVisibleAsync())
            {
                await back.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                await page.WaitForTimeoutAsync(500);
            }
        }
        catch
        {
            // No back affordance; the next attempt re-opens the menu from the current page. Ignore.
        }
    }

    /// <summary>Reads the interaction error buffer from the browser module and maps it to <see cref="VisualError"/>.</summary>
    private async Task<List<VisualError>> DrainInteractionErrorsAsync(IPage page)
    {
        try
        {
            var json = await page.EvaluateAsync<string>(
                "() => window.__sanityDrainInteractionErrors ? window.__sanityDrainInteractionErrors() : '[]'");
            var errors = JsonSerializer.Deserialize<List<VisualError>>(json, JsonOptions);
            return errors ?? new List<VisualError>();
        }
        catch
        {
            return new List<VisualError>();
        }
    }

    /// <summary>
    /// Reads the report's current active page display name from the kept-alive SDK instance, so the
    /// runner can verify a drill-through/button actually navigated to a destination page (not just that
    /// a menu item was clicked). Returns null if it can't be resolved.
    /// </summary>
    private static async Task<string?> ActivePageNameAsync(IPage page)
    {
        try
        {
            var name = await page.EvaluateAsync<string?>(
                "async () => window.__sanityActivePage ? await window.__sanityActivePage() : null");
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SafeLabelAsync(ILocator locator)
    {
        try
        {
            var label = await locator.GetAttributeAsync("aria-label");
            return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Folds interaction outcomes into the report status: any interaction that surfaced visual errors
    /// marks the report Failed (if it was Passed). Skipped/Pending interactions never change the status.
    /// </summary>
    private static void FoldInteractionOutcome(ReportCheckInteropResult interop)
    {
        var failing = interop.Interactions.Count(x => x.Status == SanityStatus.Failed);
        if (failing == 0)
        {
            return;
        }

        if (interop.Status == SanityStatus.Passed)
        {
            interop.Status = SanityStatus.Failed;
            interop.Message = $"Report rendered, but {failing} interaction(s) surfaced visual errors.";
        }
    }
}
