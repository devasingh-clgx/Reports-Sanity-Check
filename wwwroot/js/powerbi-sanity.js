// Power BI report sanity-check browser module.
//
// Power BI reports only render inside a browser, so the actual "did it load without a visual
// break" check has to happen here. For each report we:
//   1. Embed it with the server-issued embed token.
//   2. Wait for the first `rendered` event (i.e. the loading spinner is gone and visuals drew).
//   3. Capture any `error` events raised by the report or its visuals.
//   4. Optionally apply every bookmark and re-check after each re-render.
// The result is returned to .NET via JS interop and persisted server-side.
//
// Requires the powerbi-client UMD bundle to be loaded first (see App.razor), which exposes
// `window.powerbi` (the service) and `window['powerbi-client']` (the library with `models`).

const STATUS = {
    Passed: 'Passed',
    Failed: 'Failed',
    Timeout: 'Timeout',
    Error: 'Error'
};

function getLibrary() {
    const lib = window['powerbi-client'];
    const service = window.powerbi;
    if (!lib || !service) {
        return null;
    }
    return { models: lib.models, service };
}

// Interaction-phase state. Drill-through (from a table/matrix) and the BENE.BIZ Toggle Switch cannot
// be triggered through the embed SDK, so HeadlessReportChecker (Playwright) clicks them directly on
// the report DOM after `checkReport` returns. Those clicks still surface Power BI `error` events on
// the embedded report object that lives in THIS page's SDK, so we keep the last report alive and
// route its errors into a buffer the .NET runner can drain and reset between interactions.
const __interactionState = {
    report: null,
    container: null,
    errors: [],
    active: false
};

// Called by the runner before it starts a drill-through/toggle interaction. Clears the buffer so the
// next `drainInteractionErrors()` only returns errors caused by that interaction. Returns true if a
// live report is available to observe.
export function beginInteractions() {
    __interactionState.errors = [];
    __interactionState.active = true;
    return __interactionState.report !== null;
}

// Returns (and clears) the errors captured since the last begin/drain as a JSON string, matching the
// VisualError shape the .NET side already deserializes.
export function drainInteractionErrors() {
    const drained = __interactionState.errors.slice();
    __interactionState.errors = [];
    return JSON.stringify(drained);
}

// Tears down the kept-alive report once all interactions for a report are done.
export function endInteractions() {
    __interactionState.active = false;
    __interactionState.errors = [];
    try {
        if (__interactionState.container) {
            const lib = getLibrary();
            if (lib) {
                lib.service.reset(__interactionState.container);
            }
        }
    } catch { /* best-effort cleanup */ }
    __interactionState.report = null;
    __interactionState.container = null;
}

// Returns the active page's display name (or null) from the kept-alive report, so the Playwright
// runner can verify that a drill-through/button actually NAVIGATED to a destination page rather than
// just that a menu item was clicked. Cross-origin iframe DOM can't tell us the logical page, but the
// SDK object living in THIS page can.
export async function getActivePageNameForInteraction() {
    try {
        if (!__interactionState.report) {
            return null;
        }
        const page = await __interactionState.report.getActivePage();
        return page ? (page.displayName ?? page.name ?? null) : null;
    } catch {
        return null;
    }
}

// Returns the ACTIVE page as a JSON string { name, displayName, isHidden }. The drill-through explorer
// uses this to detect that a right-click -> "Drill through" -> target click actually NAVIGATED to a
// (usually hidden) destination page, and to label the drill path in results. Never throws.
export async function getActivePageInfoForInteraction() {
    const info = { name: '', displayName: '', isHidden: false };
    try {
        if (!__interactionState.report) {
            return JSON.stringify(info);
        }
        const page = await __interactionState.report.getActivePage();
        if (page) {
            info.name = page.name ?? '';
            info.displayName = page.displayName ?? page.name ?? '';
            // visibility: 0 = Visible, 1 = Hidden (models.PageVisibility). Treat anything non-zero as hidden.
            info.isHidden = !(page.visibility === undefined || page.visibility === null || page.visibility === 0);
        }
    } catch {
        // fall through with defaults
    }
    return JSON.stringify(info);
}

// OPENS A DRILL-THROUGH DESTINATION PAGE DIRECTLY.
//
// The native right-click gesture requires a rendered data cell to click on. When no such cell is
// available the destination page can still be opened deterministically: activate the hidden page and
// apply the drill-through field(s) as page-level filter context, which is exactly what Power BI does
// internally when a user picks a destination from the Drill through submenu.
//
// `filters` is an array of { table, column, value }. When empty the page is opened with no filter
// context, which still verifies the page renders (reported as 'partial').
//
// Returns JSON: { opened, filtersApplied, page, error }.
export async function openDrillThroughPageForInteraction(pageName, filters, settleMs) {
    const outcome = { opened: false, filtersApplied: false, page: pageName, error: null };
    try {
        if (!__interactionState.report || !pageName) {
            outcome.error = 'No report or page name.';
            return JSON.stringify(outcome);
        }

        const pages = await __interactionState.report.getPages();
        const target = Array.isArray(pages) ? pages.find(p => p.name === pageName) : null;
        if (!target) {
            outcome.error = 'Page not found.';
            return JSON.stringify(outcome);
        }

        await target.setActive();
        await waitForRenderOrSettle(__interactionState.report, Math.max(250, Math.min(settleMs || 2500, 60000)));
        outcome.opened = true;

        const list = Array.isArray(filters) ? filters : [];
        if (list.length > 0) {
            const basicFilters = list
                .filter(f => f && f.table && f.column)
                .map(f => ({
                    $schema: 'http://powerbi.com/product/schema#basic',
                    target: { table: f.table, column: f.column },
                    operator: 'In',
                    values: [f.value],
                    filterType: 1 // models.FilterType.Basic
                }));

            if (basicFilters.length > 0) {
                try {
                    // Replace rather than add so repeated opens don't stack filter context.
                    await target.updateFilters(0 /* models.FiltersOperations.Replace */, basicFilters);
                    await waitForRenderOrSettle(__interactionState.report, Math.max(250, Math.min(settleMs || 2500, 60000)));
                    outcome.filtersApplied = true;
                } catch (ex) {
                    outcome.error = 'Filter apply failed: ' + ex.message;
                }
            }
        }
    } catch (ex) {
        outcome.error = ex.message;
    }
    return JSON.stringify(outcome);
}

// Returns the report's VISIBLE (directly navigable) pages as a JSON string of {name, displayName}.
// Hidden pages are drill-through destinations and are deliberately excluded: the drill-through pass
// reaches them through an actual drill-through action. Used by the runner to iterate every visible
// page during the interaction phase so drill-through/toggle visuals on non-landing pages are covered.
export async function getVisiblePagesForInteraction() {
    try {
        if (!__interactionState.report) {
            return '[]';
        }
        const pages = await __interactionState.report.getPages();
        if (!Array.isArray(pages)) {
            return '[]';
        }
        const visible = pages
            .filter(p => {
                // models.PageVisibility.AlwaysVisible === 0; treat undefined as visible.
                return p.visibility === undefined || p.visibility === null || p.visibility === 0;
            })
            .map(p => ({ name: p.name, displayName: p.displayName ?? p.name }));
        return JSON.stringify(visible);
    } catch {
        return '[]';
    }
}

// Activates the page identified by its internal name and waits briefly for a re-render to settle so
// the runner can safely probe the (now-current) page's DOM in the cross-origin iframe. Returns true
// if the page was found and activated.
export async function setActivePageForInteraction(name, settleMs) {
    try {
        if (!__interactionState.report || !name) {
            return false;
        }
        const pages = await __interactionState.report.getPages();
        const page = Array.isArray(pages) ? pages.find(p => p.name === name) : null;
        if (!page) {
            return false;
        }
        await page.setActive();
        await waitForRenderOrSettle(__interactionState.report, Math.max(250, Math.min(settleMs || 2500, 60000)));
        return true;
    } catch {
        return false;
    }
}


// Returns applyable (leaf) bookmarks as a JSON string of {name, displayName}. Groups (containers)
// are flattened out because only leaf bookmarks can be applied by name. Used by the runner to reveal
// bookmark-gated visuals (e.g. a "Summary Table" bookmark that shows an otherwise-hidden drillable
// table) before scanning for drill-through data points.
export async function getApplyableBookmarksForInteraction() {
    try {
        if (!__interactionState.report) {
            return '[]';
        }
        let bookmarks = await __interactionState.report.bookmarksManager.getBookmarks();
        bookmarks = flattenBookmarks(bookmarks);
        const mapped = bookmarks.map(b => ({ name: b.name, displayName: b.displayName ?? b.name }));
        return JSON.stringify(mapped);
    } catch {
        return '[]';
    }
}

// Applies the named bookmark and waits briefly for the re-render to settle, so a bookmark that
// reveals a drillable table brings that table into the DOM before the runner probes it. Returns
// true if the bookmark was applied without throwing.
export async function applyBookmarkForInteraction(name, settleMs) {
    try {
        if (!__interactionState.report || !name) {
            return false;
        }
        await __interactionState.report.bookmarksManager.apply(name);
        await waitForRenderOrSettle(__interactionState.report, Math.max(250, Math.min(settleMs || 2500, 60000)));
        return true;
    } catch {
        return false;
    }
}


// Returns the DATA visuals on the active page as JSON: {name, type, title, x, y, width, height}.
// Drill-through is offered by data-bound visuals (tables, matrices, charts), never by images, shapes,
// text boxes or slicers, so those are filtered out. The layout rectangle lets the runner right-click a
// point INSIDE the real visual instead of scanning the whole frame for arbitrary elements.
const NON_DATA_VISUAL_TYPES = new Set([
    'image', 'textbox', 'shape', 'basicShape', 'actionButton', 'bookmarkNavigator',
    'pageNavigator', 'slicer', 'advancedSlicerVisual'
]);

export async function getDataVisualsForInteraction() {
    try {
        if (!__interactionState.report) {
            return '[]';
        }
        const pages = await __interactionState.report.getPages();
        const active = Array.isArray(pages) ? pages.find(p => p.isActive) : null;
        if (!active) {
            return '[]';
        }
        const visuals = await active.getVisuals();
        const mapped = (Array.isArray(visuals) ? visuals : [])
            .filter(v => !NON_DATA_VISUAL_TYPES.has(v.type))
            .map(v => ({
                name: v.name,
                type: v.type,
                title: v.title ?? v.name,
                x: v.layout?.x ?? null,
                y: v.layout?.y ?? null,
                width: v.layout?.width ?? null,
                height: v.layout?.height ?? null
            }));
        return JSON.stringify(mapped);
    } catch (ex) {
        return JSON.stringify({ error: ex.message });
    }
}

// Splits a single CSV line, honouring quoted fields that may themselves contain commas.
function splitCsvLine(line) {
    const out = [];
    let cur = '';
    let quoted = false;
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (quoted) {
            if (c === '"') {
                if (line[i + 1] === '"') { cur += '"'; i++; }
                else { quoted = false; }
            } else { cur += c; }
        } else if (c === '"') {
            quoted = true;
        } else if (c === ',') {
            out.push(cur); cur = '';
        } else {
            cur += c;
        }
    }
    out.push(cur);
    return out.map(v => v.trim());
}

// A visual's exported column header may be the bare column name ("Region"), the qualified name
// ("Sales.Region"), or a renamed caption. Compare on the column token so the match holds in any report.
function headerMatchesField(header, field) {
    const h = String(header ?? '').trim().toLowerCase();
    const col = String(field.column ?? '').trim().toLowerCase();
    const tbl = String(field.table ?? '').trim().toLowerCase();
    if (!h || !col) return false;
    return h === col || h === `${tbl}.${col}` || h.endsWith(`.${col}`);
}

/**
 * Generic drill-through SOURCE discovery, driven entirely by the report's own metadata.
 *
 * A drill-through destination page declares the field(s) it is filtered by. Any visual that
 * projects those same field(s) is therefore a valid right-click source for that destination.
 * This asks each data visual on the active page for its own data via exportData() — which
 * returns a CSV whose header is the visual's field list and whose first row is real data —
 * and reports which visuals carry the bound fields, plus that first data row.
 *
 * No report, page, or visual name is assumed anywhere.
 *
 * @param {string} boundFieldsJson JSON array of { table, column } from the declared targets.
 * @returns {string} JSON array of per-visual results including firstRow and matchedFields.
 */
export async function findDrillThroughSourceVisuals(boundFieldsJson) {
    try {
        if (!__interactionState.report) {
            return JSON.stringify({ error: 'No report attached.' });
        }

        let boundFields = [];
        try {
            const parsed = JSON.parse(boundFieldsJson || '[]');
            if (Array.isArray(parsed)) boundFields = parsed;
        } catch { /* treat as no bound fields */ }

        const models = window['powerbi-client']?.models;
        const pages = await __interactionState.report.getPages();
        const active = Array.isArray(pages) ? pages.find(p => p.isActive) : null;
        if (!active) {
            return JSON.stringify({ error: 'No active page.' });
        }

        const visuals = await active.getVisuals();
        const candidates = (Array.isArray(visuals) ? visuals : [])
            .filter(v => !NON_DATA_VISUAL_TYPES.has(v.type));

        const results = [];
        for (const v of candidates) {
            const entry = {
                page: active.name,
                pageDisplayName: active.displayName ?? active.name,
                visual: v.name,
                title: v.title ?? v.name,
                type: v.type,
                columns: [],
                firstRow: null,
                matchedFields: [],
                isDrillThroughSource: false,
                rowCount: 0,
                error: null
            };

            try {
                // Summarized export of a single row: enough to read the field list and one real
                // data row, cheap enough to run against every visual on the page.
                const exported = await v.exportData(
                    models ? models.ExportDataType.Summarized : 0,
                    1);
                const csv = (exported?.data ?? '').trim();
                if (!csv) {
                    entry.error = 'Visual returned no data (empty export).';
                } else {
                    const lines = csv.split(/\r?\n/).filter(l => l.length > 0);
                    entry.columns = lines.length > 0 ? splitCsvLine(lines[0]) : [];
                    entry.rowCount = Math.max(0, lines.length - 1);
                    if (lines.length > 1) {
                        const values = splitCsvLine(lines[1]);
                        entry.firstRow = entry.columns.map((c, i) => ({
                            column: c,
                            value: i < values.length ? values[i] : null
                        }));
                    }
                }
            } catch (ex) {
                // Visuals that cannot export (unsupported type, no data) are simply not sources.
                entry.error = ex?.message ?? String(ex);
            }

            for (const f of boundFields) {
                const hit = entry.columns.find(c => headerMatchesField(c, f));
                if (hit) {
                    const cell = entry.firstRow?.find(r => r.column === hit);
                    entry.matchedFields.push({
                        table: f.table,
                        column: f.column,
                        matchedColumn: hit,
                        value: cell ? cell.value : null
                    });
                }
            }

            // A visual is a source when it projects EVERY field the destination filters on.
            entry.isDrillThroughSource =
                boundFields.length > 0 && entry.matchedFields.length === boundFields.length;

            results.push(entry);
        }

        return JSON.stringify(results);
    } catch (ex) {
        return JSON.stringify({ error: ex?.message ?? String(ex) });
    }
}

/**
 * PATH B — live SDK interrogation.
 *
 * Asks each visual directly for the fields it actually renders, via getDataFields()/getSlicerState-style
 * SDK surface, and for one page of result data via exportData. Unlike the definition path this reflects
 * what the embed really rendered; unlike CSV-only probing it also reports the field list when the visual
 * has no rows, which is what distinguishes "not a source" from "a source with no data".
 *
 * @param {string} boundFieldsJson JSON array of { table, column }.
 * @returns {string} JSON array of per-visual capability + data findings.
 */
export async function probeVisualCapabilities(boundFieldsJson) {
    try {
        if (!__interactionState.report) {
            return JSON.stringify({ error: 'No report attached.' });
        }

        let boundFields = [];
        try {
            const parsed = JSON.parse(boundFieldsJson || '[]');
            if (Array.isArray(parsed)) boundFields = parsed;
        } catch { /* no bound fields */ }

        const pages = await __interactionState.report.getPages();
        const active = Array.isArray(pages) ? pages.find(p => p.isActive) : null;
        if (!active) {
            return JSON.stringify({ error: 'No active page.' });
        }

        const visuals = await active.getVisuals();
        const out = [];

        for (const v of (Array.isArray(visuals) ? visuals : [])) {
            if (NON_DATA_VISUAL_TYPES.has(v.type)) continue;

            const entry = {
                page: active.name,
                pageDisplayName: active.displayName ?? active.name,
                visual: v.name,
                title: v.title ?? v.name,
                type: v.type,
                dataFields: [],
                matchedFields: [],
                isDrillThroughSource: false,
                error: null
            };

            // Enumerate the visual's data roles and the fields placed in each. Role names differ per
            // visual type (Category/Y/Values/Rows/Columns...), so every role is swept rather than
            // assuming a fixed set.
            try {
                const roles = ['Category', 'Y', 'Values', 'Rows', 'Columns', 'Series', 'Axis', 'Legend', 'Details'];
                for (const role of roles) {
                    let fields;
                    try {
                        fields = await v.getDataFields(role);
                    } catch {
                        continue; // Role not supported by this visual type.
                    }
                    if (!Array.isArray(fields)) continue;

                    for (let i = 0; i < fields.length; i++) {
                        let label = null;
                        try {
                            label = await v.getDataFieldDisplayName(role, i);
                        } catch { /* fall back to the raw descriptor */ }

                        const f = fields[i] ?? {};
                        entry.dataFields.push({
                            role,
                            table: f.table ?? f.queryRef?.split('.')?.[0] ?? null,
                            column: f.column ?? f.measure ?? f.hierarchyLevel ?? null,
                            displayName: label
                        });
                    }
                }
            } catch (ex) {
                entry.error = ex?.message ?? String(ex);
            }

            for (const b of boundFields) {
                const hit = entry.dataFields.find(df =>
                    (df.column && String(df.column).toLowerCase() === String(b.column).toLowerCase()) ||
                    (df.displayName && String(df.displayName).toLowerCase() === String(b.column).toLowerCase()));
                if (hit) {
                    entry.matchedFields.push({ table: b.table, column: b.column, matchedColumn: hit.column ?? hit.displayName, value: null });
                }
            }

            entry.isDrillThroughSource =
                boundFields.length > 0 && entry.matchedFields.length === boundFields.length;

            out.push(entry);
        }

        return JSON.stringify(out);
    } catch (ex) {
        return JSON.stringify({ error: ex?.message ?? String(ex) });
    }
}

/**
 * PATH C — data extraction for a SPECIFIC visual, by internal name.
 *
 * Given a visual the definition already identified as a source, pull one real data row from it. Tries
 * Summarized export first and falls back to Underlying, because some chart types reject one but accept
 * the other. Returns the first row so the exact right-click coordinates are known.
 *
 * @param {string} visualName The visual's internal name.
 * @returns {string} JSON with columns, firstRow, rowCount, and the export mode that succeeded.
 */
export async function getFirstDataRowForVisual(visualName) {
    try {
        if (!__interactionState.report) {
            return JSON.stringify({ error: 'No report attached.' });
        }

        const pages = await __interactionState.report.getPages();
        const active = Array.isArray(pages) ? pages.find(p => p.isActive) : null;
        if (!active) {
            return JSON.stringify({ error: 'No active page.' });
        }

        const visuals = await active.getVisuals();
        const v = (Array.isArray(visuals) ? visuals : []).find(x => x.name === visualName);
        if (!v) {
            return JSON.stringify({ error: `Visual '${visualName}' not found on the active page.` });
        }

        const models = window['powerbi-client']?.models;
        const modes = models
            ? [['Summarized', models.ExportDataType.Summarized], ['Underlying', models.ExportDataType.Underlying]]
            : [['Summarized', 0], ['Underlying', 1]];

        const errors = [];
        for (const [modeName, mode] of modes) {
            try {
                const exported = await v.exportData(mode, 1);
                const csv = (exported?.data ?? '').trim();
                if (!csv) {
                    errors.push(`${modeName}: empty result`);
                    continue;
                }

                const lines = csv.split(/\r?\n/).filter(l => l.length > 0);
                const columns = lines.length > 0 ? splitCsvLine(lines[0]) : [];
                let firstRow = null;
                if (lines.length > 1) {
                    const values = splitCsvLine(lines[1]);
                    firstRow = columns.map((c, i) => ({ column: c, value: i < values.length ? values[i] : null }));
                }

                return JSON.stringify({
                    visual: v.name,
                    title: v.title ?? v.name,
                    type: v.type,
                    mode: modeName,
                    columns,
                    firstRow,
                    rowCount: Math.max(0, lines.length - 1),
                    error: firstRow ? null : `${modeName}: header only, no data rows`
                });
            } catch (ex) {
                errors.push(`${modeName}: ${ex?.message ?? ex}`);
            }
        }

        return JSON.stringify({
            visual: v.name,
            title: v.title ?? v.name,
            type: v.type,
            columns: [],
            firstRow: null,
            rowCount: 0,
            error: errors.join('; ')
        });
    } catch (ex) {
        return JSON.stringify({ error: ex?.message ?? String(ex) });
    }
}

// The powerbi-client UMD bundle is loaded asynchronously from a CDN (see App.razor), so on a
// fresh circuit the sanity check can run before the script has finished executing. Poll for it
// for up to `timeoutMs` instead of failing on the first miss.
function waitForLibrary(timeoutMs = 10000) {
    return new Promise((resolve, reject) => {
        const existing = getLibrary();
        if (existing) {
            resolve(existing);
            return;
        }

        const start = Date.now();
        const timer = setInterval(() => {
            const lib = getLibrary();
            if (lib) {
                clearInterval(timer);
                resolve(lib);
            } else if (Date.now() - start >= timeoutMs) {
                clearInterval(timer);
                reject(new Error(
                    'powerbi-client library is not loaded. Confirm the CDN script in App.razor ' +
                    'is reachable (https://cdn.jsdelivr.net/npm/powerbi-client) and not blocked.'));
            }
        }, 100);
    });
}

function normalizeError(detail, bookmarkName) {
    detail = detail || {};
    return {
        page: detail.page ?? detail.pageName ?? null,
        visual: detail.visual ?? detail.visualName ?? detail.title ?? null,
        bookmark: bookmarkName ?? null,
        level: detail.level !== undefined && detail.level !== null ? String(detail.level) : null,
        message: detail.message ?? detail.errorCode ?? 'Unknown Power BI error',
        detailedMessage: detail.detailedMessage ?? (detail.technicalDetails ? JSON.stringify(detail.technicalDetails) : null)
    };
}

// Resolves the next time `eventName` fires on the report, or rejects after `timeoutMs`.
function waitForEvent(report, eventName, timeoutMs) {
    return new Promise((resolve, reject) => {
        let settled = false;

        const timer = setTimeout(() => {
            if (settled) return;
            settled = true;
            report.off(eventName, handler);
            reject(new Error(`Timed out after ${timeoutMs} ms waiting for '${eventName}'.`));
        }, timeoutMs);

        const handler = (event) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            report.off(eventName, handler);
            resolve(event && event.detail);
        };

        report.on(eventName, handler);
    });
}

// `getBookmarks()` returns a tree: a *group* is a container with a `children` array and cannot be
// applied by name. Only leaf bookmarks are applyable, so flatten the tree and drop the groups.
function flattenBookmarks(bookmarks) {
    const leaves = [];
    for (const bookmark of bookmarks || []) {
        if (Array.isArray(bookmark.children) && bookmark.children.length > 0) {
            leaves.push(...flattenBookmarks(bookmark.children));
        } else {
            leaves.push(bookmark);
        }
    }
    return leaves;
}

async function checkBookmarks(report, options, errors, errorSink) {
    const results = [];

    let bookmarks = [];
    try {
        bookmarks = await report.bookmarksManager.getBookmarks();
    } catch (ex) {
        // No bookmarks capability (or a report without bookmarks) is not a failure.
        return results;
    }

    bookmarks = flattenBookmarks(bookmarks);
    if (bookmarks.length === 0) {
        return results;
    }

    // Capture a clean baseline so every bookmark is evaluated from the same starting point. This is
    // what stops a misbehaving cross-page bookmark (e.g. one that targets the "Average Age" page
    // while we're on "Depreciation") from leaking its state into the next bookmark.
    let baseline = null;
    try {
        const captured = await report.bookmarksManager.capture();
        baseline = captured && captured.state ? captured.state : null;
    } catch {
        // capture() not supported on this report; we just won't reset between bookmarks.
    }

    // Briefly wait for a re-render after applying a bookmark, but DON'T treat its absence as a
    // failure: a bookmark that doesn't change the current page legitimately renders nothing.
    const settleMs = Math.max(250, Math.min(options.bookmarkApplyTimeoutMs, 2500));

    for (const bookmark of bookmarks) {
        const displayName = bookmark.displayName ?? bookmark.name;

        // Stop applying bookmarks once the overall time budget is spent so a slow report still
        // returns a measured result instead of being cancelled by the .NET interop timeout.
        if (typeof options.deadline === 'number' && performance.now() >= options.deadline) {
            results.push({
                name: bookmark.name,
                displayName: displayName,
                page: null,
                status: STATUS.Timeout,
                errorCount: 0,
                durationMs: 0,
                message: 'Skipped: overall time budget exceeded.'
            });
            continue;
        }

        // Reset to the clean baseline first so this bookmark can't inherit a broken state from the
        // previous one. Errors during the reset are ignored (they belong to the prior bookmark).
        await restoreBaseline(report, baseline, errorSink, settleMs);

        // Redirect captured visual errors to this bookmark so they don't fail the whole report, and
        // stamp them with the friendly display name rather than the internal bookmark id.
        const bookmarkErrors = [];
        errorSink.current = bookmarkErrors;
        errorSink.currentBookmark = displayName;

        let status = STATUS.Passed;
        let message = null;
        let page = null;

        // Time only this bookmark's apply + re-render, so the per-bookmark column reflects the cost
        // of the bookmark itself (the report-level duration remains the total across all bookmarks).
        const bookmarkStarted = performance.now();
        try {
            await report.bookmarksManager.apply(bookmark.name);
            // Wait for a re-render if one comes; a no-op bookmark simply settles without rendering.
            await waitForRenderOrSettle(report, settleMs);
            // Record which page the bookmark actually landed on (helps explain cross-page bookmarks).
            page = await getActivePageName(report);
        } catch (ex) {
            status = STATUS.Failed;
            message = ex.message;
            bookmarkErrors.push(normalizeError({ message: ex.message, level: 'Error' }, displayName));
        } finally {
            // Always restore the report-level sink before moving on.
            errorSink.current = errors;
            errorSink.currentBookmark = null;
        }
        const bookmarkDurationMs = Math.round(performance.now() - bookmarkStarted);

        if (status === STATUS.Passed && bookmarkErrors.length > 0) {
            status = STATUS.Failed;
            message = bookmarkErrors[0].message;
        }

        results.push({
            name: bookmark.name,
            displayName: displayName,
            page: page,
            status: status,
            errorCount: bookmarkErrors.length,
            durationMs: bookmarkDurationMs,
            message: message,
            errors: bookmarkErrors
        });
    }

    // Leave the report back on its clean baseline.
    await restoreBaseline(report, baseline, errorSink, settleMs);
    return results;
}

// Applies the captured baseline state (if any) and waits briefly for the re-render to settle.
// Errors raised while restoring are discarded so they aren't blamed on the next bookmark.
async function restoreBaseline(report, baselineState, errorSink, settleMs) {
    if (!baselineState) {
        return;
    }
    const previousSink = errorSink.current;
    errorSink.current = [];
    try {
        await report.bookmarksManager.applyState(baselineState);
        await waitForRenderOrSettle(report, settleMs);
    } catch {
        // A failed restore isn't a bookmark failure; ignore and continue.
    } finally {
        errorSink.current = previousSink;
    }
}

// Resolves when the report fires the next `rendered` event, or after `settleMs` if it doesn't.
// Never rejects: a bookmark that causes no visual change is valid, not a timeout.
function waitForRenderOrSettle(report, settleMs) {
    return new Promise((resolve) => {
        let settled = false;
        const finish = () => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            report.off('rendered', onRendered);
            resolve();
        };
        const onRendered = () => finish();
        const timer = setTimeout(finish, settleMs);
        report.on('rendered', onRendered);
    });
}

// Returns the active page's display name, or null if it can't be determined.
async function getActivePageName(report) {
    try {
        const page = await report.getActivePage();
        return page ? (page.displayName ?? page.name ?? null) : null;
    } catch {
        return null;
    }
}

// Renders the report's *directly navigable* pages. Hidden pages are drill-through DESTINATIONS
// (e.g. "Category", "YoY / MoM"): they only render correctly when reached via drill-through, which
// passes the filter context/parameters they depend on. Activating them directly produces false
// "visual error" results because the incoming parameters are missing, so we deliberately SKIP them
// here and rely on the DOM-driven drill-through pass (HeadlessReportChecker) to exercise them with
// their real context instead.
async function checkPages(report, options, errors, errorSink) {
    const results = [];

    let pages = [];
    try {
        pages = await report.getPages();
    } catch (ex) {
        // A report that can't enumerate pages isn't a page-level failure; skip page checks.
        return results;
    }

    if (!Array.isArray(pages) || pages.length === 0) {
        return results;
    }

    // Cap how many pages we render so a report with a very large number of pages can't blow past
    // the overall budget.
    const maxPages = typeof options.maxPagesPerReport === 'number' && options.maxPagesPerReport > 0
        ? options.maxPagesPerReport
        : pages.length;
    if (pages.length > maxPages) {
        pages = pages.slice(0, maxPages);
    }

    const settleMs = Math.max(250, Math.min(options.pageTimeoutMs || 0, 60000)) || 2500;

    for (const page of pages) {
        const displayName = page.displayName ?? page.name;
        const isHidden = page.visibility !== undefined && page.visibility !== null
            ? page.visibility !== 0 // models.PageVisibility.AlwaysVisible === 0
            : false;

        // Skip hidden pages: they are drill-through destinations that need parameters passed by the
        // drill-through action. Rendering them cold would report spurious visual errors. They are
        // covered by the drill-through interaction pass instead. Record them as skipped so the run
        // still shows they exist and were intentionally not rendered directly.
        if (isHidden) {
            results.push({
                name: page.name,
                displayName: displayName,
                isHidden: true,
                status: STATUS.Passed,
                errorCount: 0,
                durationMs: 0,
                visualCount: 0,
                skipped: true,
                message: 'Skipped direct render: drill-through destination page (exercised via drill-through).'
            });
            continue;
        }

        // Stop rendering pages once the overall time budget is spent so a slow report still returns a
        // measured result instead of being cancelled by the .NET interop timeout.
        if (typeof options.deadline === 'number' && performance.now() >= options.deadline) {
            results.push({
                name: page.name,
                displayName: displayName,
                isHidden: isHidden,
                status: STATUS.Timeout,
                errorCount: 0,
                durationMs: 0,
                visualCount: 0,
                message: 'Skipped: overall time budget exceeded.'
            });
            continue;
        }

        // Redirect captured visual errors to this page so they don't fail the whole report, and stamp
        // them with the friendly page name.
        const pageErrors = [];
        errorSink.current = pageErrors;
        errorSink.currentBookmark = null;

        let status = STATUS.Passed;
        let message = null;
        let visualCount = 0;

        const pageStarted = performance.now();
        try {
            await page.setActive();
            // Wait for a re-render if one comes; a page that's already active simply settles.
            await waitForRenderOrSettle(report, settleMs);

            // Count visuals so the result confirms the page actually drew something (and so drill-through
            // destination visuals are represented even when they don't error).
            try {
                const visuals = await page.getVisuals();
                visualCount = Array.isArray(visuals) ? visuals.length : 0;
            } catch {
                // getVisuals not available on this page; leave the count at 0.
            }
        } catch (ex) {
            status = STATUS.Failed;
            message = ex.message;
            pageErrors.push(normalizeError({ page: displayName, message: ex.message, level: 'Error' }, null));
        } finally {
            // Always restore the report-level sink before moving on.
            errorSink.current = errors;
            errorSink.currentBookmark = null;
        }
        const pageDurationMs = Math.round(performance.now() - pageStarted);

        // Stamp any captured errors with the page name if the SDK didn't provide one.
        for (const err of pageErrors) {
            if (!err.page) {
                err.page = displayName;
            }
        }

        if (status === STATUS.Passed && pageErrors.length > 0) {
            status = STATUS.Failed;
            message = pageErrors[0].message;
        }

        results.push({
            name: page.name,
            displayName: displayName,
            isHidden: isHidden,
            status: status,
            errorCount: pageErrors.length,
            durationMs: pageDurationMs,
            visualCount: visualCount,
            message: message,
            errors: pageErrors
        });
    }

    return results;
}

// Checks a single report. `embed` is the server EmbedConfig; `options` carries the timeouts.
export async function checkReport(containerId, embed, options) {
    const started = performance.now();
    const errors = [];

    // Time for just the initial load+render (loader gone, report fully drawn), measured separately
    // from the total so callers can judge "page load time" on the slowest single render rather than
    // the cumulative time spent applying every bookmark.
    let renderDurationMs = 0;

    // Per-page results (including hidden drill-through pages). Declared here so `finish` can always
    // include whatever pages were checked, even on an early return.
    let pageResults = [];

    // Absolute wall-clock budget for this whole report (initial render + every bookmark). When set,
    // every wait below is capped by the time remaining so the browser always returns a measured
    // result before the .NET JS-interop call times out (which surfaces as "A task was canceled").
    if (typeof options.overallTimeoutMs === 'number' && options.overallTimeoutMs > 0) {
        options.deadline = started + options.overallTimeoutMs;
    }

    const finish = (status, message, bookmarks) => ({
        status,
        durationMs: Math.round(performance.now() - started),
        renderDurationMs,
        message: message ?? null,
        errors,
        bookmarks: bookmarks ?? [],
        pages: pageResults
    });

    if (!embed || !embed.embedToken || !embed.embedUrl) {
        return finish(STATUS.Error, 'No embed token/url was provided for this report.');
    }

    const container = document.getElementById(containerId);
    if (!container) {
        return finish(STATUS.Error, `Embed container '${containerId}' was not found.`);
    }

    let lib;
    try {
        lib = await waitForLibrary();
    } catch (ex) {
        return finish(STATUS.Error, ex.message);
    }

    const { models, service } = lib;

    // Always start from a clean container so a previous report can't leak state.
    service.reset(container);

    const config = {
        type: 'report',
        tokenType: models.TokenType.Embed,
        accessToken: embed.embedToken,
        embedUrl: embed.embedUrl,
        id: embed.reportId,
        permissions: models.Permissions.Read,
        settings: {
            panes: {
                filters: { visible: false },
                pageNavigation: { visible: false }
            },
            background: models.BackgroundType.Transparent
        }
    };

    let report;
    try {
        report = service.embed(container, config);
    } catch (ex) {
        return finish(STATUS.Error, `Embedding failed: ${ex.message}`);
    }

    // Errors are routed through a swappable "sink" so a bookmark's transient errors (e.g. while it
    // switches the report to its own page) are attributed to that bookmark instead of being charged
    // to the whole report. The sink defaults to the report-level `errors` array. `currentBookmark`
    // holds the active bookmark's friendly display name so captured errors are stamped with the
    // name a user recognises, not the internal bookmark id.
    const errorSink = { current: errors, currentBookmark: null };
    report.on('error', (event) => {
        const normalized = normalizeError(event && event.detail, errorSink.currentBookmark);
        errorSink.current.push(normalized);
        // During the DOM-driven interaction phase, also buffer errors so the Playwright runner can
        // attribute them to the specific drill-through/toggle it just performed.
        if (__interactionState.active) {
            __interactionState.errors.push(normalized);
        }
    });

    try {
        // Wait for the loader to disappear and the report to fully render, capped by the overall
        // budget so an unusually slow (high-PLT) report is flagged as a Timeout with a real
        // duration instead of being killed by the .NET interop timeout.
        const renderWaitMs = typeof options.deadline === 'number'
            ? Math.min(options.renderTimeoutMs, Math.max(0, options.deadline - performance.now()))
            : options.renderTimeoutMs;
        await waitForEvent(report, 'rendered', renderWaitMs);
        renderDurationMs = Math.round(performance.now() - started);
    } catch (ex) {
        const result = finish(STATUS.Timeout, ex.message);
        service.reset(container);
        return result;
    }

    // Render every page (including hidden drill-through destinations) before bookmarks, so page-level
    // failures are captured against the page and bookmarks still restore a clean baseline afterwards.
    if (options.checkAllPages) {
        try {
            pageResults = await checkPages(report, options, errors, errorSink);
        } catch (ex) {
            errors.push(normalizeError({ message: `Page check failed: ${ex.message}`, level: 'Error' }));
        }
    }

    let bookmarks = [];
    if (options.checkBookmarks) {
        try {
            bookmarks = await checkBookmarks(report, options, errors, errorSink);
        } catch (ex) {
            errors.push(normalizeError({ message: `Bookmark check failed: ${ex.message}`, level: 'Error' }));
        }
    }

    // The report itself is healthy if it rendered and produced no report-level errors. A bookmark or
    // page that fails is reported against that bookmark/page and flagged here, but it no longer fails
    // the whole report on its own unless it produced a report-level error.
    const bookmarkFailures = bookmarks.filter(b => b.status !== STATUS.Passed).length;
    const pageFailures = pageResults.filter(p => p.status !== STATUS.Passed).length;
    let status;
    let message;
    if (errors.length > 0) {
        status = STATUS.Failed;
        message = `${errors.length} visual error(s) detected.`;
    } else if (pageFailures > 0) {
        status = STATUS.Failed;
        message = `Report rendered, but ${pageFailures} page(s) failed to render cleanly.`;
    } else if (bookmarkFailures > 0) {
        status = STATUS.Failed;
        message = `Report rendered, but ${bookmarkFailures} bookmark(s) failed to apply cleanly.`;
    } else {
        status = STATUS.Passed;
        message = 'Report rendered successfully with no visual errors.';
    }

    const result = finish(status, message, bookmarks);
    // When the runner will perform DOM-driven interactions (drill-through/toggle), keep the embedded
    // report alive so its `error` events can still be captured; the runner calls endInteractions()
    // to reset it afterwards. Otherwise reset immediately to free the container.
    if (options.runInteractions) {
        __interactionState.report = report;
        __interactionState.container = container;
    } else {
        service.reset(container);
    }
    return result;
}
