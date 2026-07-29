# Drill-Through Root Cause Analysis

**Status:** analysis only — no code changes pending your review.
**Question:** why has the native drill-through gesture never fired in the headless run?

---

## 1. The one measurement that matters

Across **every** run, in every bookmark state:

```
[data-query-ref] anywhere in frame: 0
```

This is a whole-frame count (`frame.Locator("[data-query-ref]").CountAsync()`). It is
**independent of every selector I have written**. It does not care about
`cell-interactive`, `clearCatcher`, chart classes, or geometry filters.

Your portal capture (`before Right Click.txt`) shows the source cells DO exist there:

```html
<div data-query-ref="DIM ASSIGNMENT.PARTICIPANT_TYPE_BUCKET"
	 role="gridcell"
	 class="pivotTableCellWrap cell-interactive main-cell"
	 style="...pointer-events:auto">IA</div>
```

**Conclusion:** the pivot table renders in the **portal**, and has never rendered in the
**embed** our job drives. Everything else follows from this.

---

## 2. Why my last ~6 changes could not have worked

| # | Change | Result | Why it was doomed |
|---|--------|--------|-------------------|
| 1 | Added chart-mark selectors | cells 0 → 1 | Matched a 32px decorative `<circle>` in a shape visual |
| 2 | Bookmark-state probing | cells 1 → 8 | **Correct idea** — states do matter |
| 3 | `:not(.clearCatcher)` | cells 8 → 0 | Report doesn't use `.columnChart` class names |
| 4 | Geometry mark sweep | cells 0 → 10 | Matched a 1519×64 `ui-role-button-fill` |
| 5 | Non-data visual exclusion | cells 10 → 0 | Correctly rejected chrome — nothing real was left |
| 6 | `.cell-interactive` selector | cells still 0 | **The element is not in the DOM to select** |

Changes 1, 3, 4, 5, 6 were all attempts to *locate* an element that does not exist in the
frame. Only #2 addressed a real mechanism. I also caused **two report timeouts** (240s
budget × 2 hidden pages; and an 800ms-per-cell attribute loop).

---

## 3. Root cause hypothesis (ranked)

### H1 — RLS returns no rows under the service principal identity  ← most likely

You confirmed: **service principal auth** + **table depends on RLS**.

`appsettings.json` sets:
```json
"EffectiveIdentity": {
  "Username": "devasingh_cotality.com#EXT#@NextGearSolutions.onmicrosoft.com",
  "Roles": [ "Participant Security" ]
}
```

That username is a **guest (`#EXT#`) account**. RLS DAX filters commonly resolve via
`USERPRINCIPALNAME()` against a security/participant table. If the guest UPN does not
match a row there, the role evaluates to **zero rows**.

A Power BI table/matrix with zero rows **renders no cells at all** — no `data-query-ref`
anywhere. This matches the observation exactly, in every state.

Your screenshot corroborates it: *"Please select one carrier — Select one carrier to view
filtered results."* That is carrier-scoped data access.

**Predicts:** `visual-card` values render as `0`. Your screenshot shows `0` for
"Avg. Estimate Ready for Review to Approval (Days)". ✔ consistent

### H2 — The table visual never loads in the embed
`frames in page: 2` and visuals show `allow-deferred-rendering`. A visual scrolled out of
the viewport may defer. Weaker: we probe after bookmark apply + settle, and cards from the
*same* page do render.

### H3 — Embed token lacks the dataset scope for that visual
`GenerateToken` includes `identities` only when `rls.IsEnabled && hasDataset`. If
`DatasetId` were null, identity is silently dropped and RLS-gated visuals return nothing.
Cheap to verify from logs.

---

## 4. How to confirm — one run, no guessing

**Fastest (no deploy):** open the embed in your browser under the *same* identity —
Power BI → report → **Apps/Test as role** → role `Participant Security`, user the `#EXT#`
UPN above. If the pivot table is **empty there**, H1 is confirmed and no code change to
the checker can ever make the gesture work.

**Or, one diagnostic run:** emit per state — `data-query-ref` count, `visual-pivotTable`
present?, visible row count, and the `GenerateToken` request's `identities` block +
resolved `DatasetId`. That distinguishes H1/H2/H3 in a single 8-minute cycle instead of six.

---

## 5. What I recommend

1. **Confirm H1 first** (browser check above — costs minutes, no deploy).
2. **If H1 is true:** this is a *data/permission* issue, not a checker bug. Options:
   an RLS identity that actually returns rows; or accept that gesture drill-through is
   unverifiable under this identity and keep the honest `⚠ partial` verdict.
3. **Stop the selector churn.** No further changes to
   `TryGestureOpenHiddenPageAsync` until the frame demonstrably contains
   `data-query-ref` cells.

---

## 6. Known outstanding defects (independent of root cause)

- **Budget regression:** `GestureBudgetMs = 60_000` now truncates the sweep —
  `bookmark states probed: 6` then `3`, so `Summary Tables` is no longer reached.
  Was 10/10 at 150s. Needs a per-report budget, not per-hidden-page.
- **Dead code:** `MaxProbesPerState` geometry sweep and chart selectors are unused
  if the source is a pivot table. Should be removed once confirmed.
