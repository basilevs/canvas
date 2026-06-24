# PRD: Stroke Rendering Smoothing

## 1. Feature Name

Stroke Rendering Smoothing — present the polyline that backs each stroke as a continuous, visually smooth curve (via interpolating splines or arcs) so that the angularity introduced by dropped pointer samples and point simplification disappears. The smoothing is applied at render time by default, but capture-time approaches (smoothing or resampling points as they are captured, before simplification/sync) are explicitly evaluated as an alternative or complement — see §10.

## 2. Epic

- **Parent PRD:** [Collaborative Whiteboard MVP](../../../../../plan/prd-collaborative-whiteboard-mvp.md)
- **Parent Architecture / Feature plan:** [Collaborative Whiteboard feature plan](../../../../../plan/feature-collaborative-whiteboard-1.md)
- **Sibling feature:** [Board Aspect Ratio Negotiation](../board-aspect-ratio-negotiation/prd.md) — established the normalized, width-relative coordinate space this feature renders.

This feature centers on the shared rendering primitive `drawStrokePath` in [wwwroot/js/canvas.js](../../../../../wwwroot/js/canvas.js) — the single function used by the live confirmed-stroke layer, the in-flight preview overlay, and the replay engine ([wwwroot/js/replay.js](../../../../../wwwroot/js/replay.js)) — because a render-time change there is the lowest-cost, lowest-risk path and benefits all surfaces at once. However, the scope is **not** restricted to rendering: capture-time changes (e.g. smoothing/resampling points in `#appendPoint` before they reach `simplifyPointsVisvalingamWhyatt` and the wire) are an evaluated alternative. §10 frames the placement decision and its trade-offs; render-time remains the recommended default unless a capture-time benefit (e.g. better fidelity feeding simplification) justifies the added cost of changing stored/synced data.

## 3. Goal

### Problem

Strokes are stored and rendered as a sequence of straight line segments connecting captured pointer samples. Two upstream effects make that polyline visibly faceted:

1. **Dropped samples during fast motion.** A quickly moving pointer emits fewer `pointermove` events per unit of arc length, so the page never receives some points along the true path. The gaps between the surviving points are bridged with long straight chords.
2. **Deliberate point reduction.** `simplifyPointsVisvalingamWhyatt` removes low-significance points to save storage and bandwidth and to keep redraw cheap (see `simplifyPointsVisvalingamWhyatt` in [wwwroot/js/canvas.js](../../../../../wwwroot/js/canvas.js)), which by design leaves a sparser polyline.

Because the renderer connects these sparse points with straight segments (`context.lineTo` per segment in `drawStrokePath`), curved gestures show flat facets and visible corners ("angular" strokes), degrading the perceived quality of every drawing for every collaborator and in replay.

### Solution

Present each stroke as a smooth curve (Bézier or circular arc) fitted through its points instead of straight chord-to-chord segments, continuing to honor per-point, pressure-driven line width and the single-tap dot case. Two placement strategies are in scope and compared in §10:

1. **Render-time (recommended default):** fit the curve inside `drawStrokePath` from the existing stored points. Stored/synced geometry is unchanged, and all three render surfaces — confirmed history, live preview, and replay — benefit identically with no protocol or storage changes.
2. **Capture-time (evaluated alternative/complement):** enrich the captured data so the smooth curve is reconstructable from it — while **keeping the retained anchors as true captured positions**. Filtering points (as `simplifyPointsVisvalingamWhyatt` already does) is expected; the principle is that the anchors we *keep* are real, un-relocated samples, not averaged/resampled ones. This means recording compact per-point direction/tangent hints, or changing the format to store curve parameters (Bézier controls) over the surviving anchors instead of a raw polyline. In-place smoothing/resampling that **moves** the points it keeps is rejected as lossy at those anchors. These options can improve fidelity under aggressive simplification but change stored/synced data and must be weighed against that cost.

The recommendation is to ship render-time first and treat capture-time as a deliberate, separately-justified follow-on rather than excluding it up front.

### Impact

- **Higher perceived drawing quality:** curved gestures look smooth rather than faceted on every client, improving the core "feels good to draw" experience that the MVP exists to validate.
- **Decouples visual quality from sample density:** aggressive simplification and unavoidable fast-motion sample loss no longer translate into visible angularity, so the existing storage/bandwidth savings can be kept (or even increased) without a quality penalty.
- **Low-cost default path:** the recommended render-time approach is a client-side render concern, so it ships without migrations, new fields, or added network traffic; capture-time options are considered only where their benefit outweighs the cost of changing stored/synced data.

## 4. User Personas

- **Anonymous collaborator (the artist):** anyone drawing freehand strokes (mouse, trackpad, pen, or touch) who wants their curved gestures to render as smooth curves rather than connected straight segments.
- **Anonymous collaborator (the viewer):** any participant watching others' strokes appear live, viewing the late-join snapshot, or watching a replay — they see the same smoothed result.

Both are the single anonymous-collaborator actor from the MVP; this feature adds no roles, ownership, or settings.

## 5. User Stories

- **US-1 — Smooth curved gestures:** As an artist, I want a curved gesture to render as a smooth curve, so that my drawing does not look faceted or angular.
- **US-2 — Fast strokes still look smooth:** As an artist drawing quickly (where some pointer samples are dropped), I want the resulting stroke to still appear smooth, so that drawing speed does not degrade visual quality.
- **US-3 — Simplified strokes still look smooth:** As a viewer of a stored/late-joined drawing, I want strokes that were point-reduced for storage to render smoothly, so that the optimization is invisible.
- **US-4 — Pressure thickness preserved:** As an artist using a pressure-sensitive pen, I want the smoothed stroke to keep varying its thickness with pressure along the curve, so that smoothing does not flatten the expressive line-weight.
- **US-5 — Faithful to intent (no overshoot):** As an artist, I want the smooth curve to stay faithful to where I actually drew — not bulge outward, loop, or cut corners past my points — so that the smoothed stroke still represents what I drew.
- **US-6 — Sharp corners stay reasonably sharp:** As an artist who intentionally draws a sharp corner or zig-zag, I want corners to remain recognizably sharp rather than being rounded into blobs, so that deliberate angularity is respected.
- **US-7 — Consistent across surfaces:** As a collaborator, I want live strokes, the preview of my own in-progress stroke, the late-join snapshot, and replay to all render with the same smoothing, so that a stroke never visibly changes shape as it moves between states.
- **US-8 — Single tap unchanged:** As an artist, I want a single tap/click to still render as a dot, so that smoothing does not break isolated points.
- **US-9 — Two-point stroke unchanged:** As an artist, I want a stroke with only two points to still render as a straight segment, so that there is nothing to (and nothing pretends to) smooth.
- **US-10 — Smooth replay (edge case):** As a viewer watching a replay that reveals a stroke point-by-point over time, I want the partially-revealed stroke to be smooth at each animation frame, so that replay matches the final smoothed look.

## 6. Requirements

### Functional Requirements

- **FR-1 — Curve-based path rendering.** `drawStrokePath` renders the path through the stored points as a smooth curve (Bézier or circular arc) instead of straight `lineTo` segments. The stored points remain the curve's defining/interpolated knots.
- **FR-2 — Single render primitive.** The smoothing lives entirely inside the shared `drawStrokePath` primitive so that the confirmed-stroke layer, volatile preview, and replay engine all use it with no per-call-site changes.
- **FR-3 — Interpolation, not approximation, of stored points.** The curve passes through (interpolates) every stored point rather than merely approximating them, so a smoothed stroke does not drift away from where the user drew. (The chosen algorithm — see §9 — must satisfy this; a corner-cutting/approximating scheme is acceptable only if it keeps maximum deviation below NFR-2's bound.)
- **FR-4 — Pressure-driven width preserved.** Line width continues to vary with per-point pressure along the curve. Because Canvas 2D cannot vary `lineWidth` within a single curve call, the curve is tessellated into short sub-segments whose width is interpolated from the bracketing points' pressure, preserving today's behavior (`baseWidth * pressureFactor * scale`). Null pressure continues to mean full width.
- **FR-5 — Normalized coordinates and scale unchanged.** Smoothing operates in the existing normalized (width-relative) coordinate space and is mapped to device pixels via the same `scale` argument; it adds no new coordinate space.
- **FR-6 — Two-point and single-point cases preserved.** A stroke with two points renders as a straight segment; a single isolated point continues to render as a filled dot sized to the line at that point (the existing `drewSegment`/`lastVisible` behavior is retained).
- **FR-7 — Replay time-clipping preserved.** The `upToMs` time cutoff used by replay continues to work: only points at or before the cutoff contribute to the curve, and the partially-revealed curve is smooth at each frame.
- **FR-8 — Placement decision is explicit and documented.** The chosen smoothing placement — render-time (inside `drawStrokePath`) versus capture-time (enriching captured data with per-point tangent hints, or storing curve parameters, while keeping retained anchors as true captured positions) — must be a deliberate, recorded decision per §10. For the **render-time** default, capture (`#appendPoint`/`#toCanvasPoint`), `simplifyPointsVisvalingamWhyatt`, the stroke/point DTOs, and hub messages remain untouched. If a **capture-time** approach is adopted, it must keep the anchors it retains as true captured positions — dropping/filtering points (as VW already does) is allowed, but in-place resampling/smoothing that **relocates** retained points is not — and it must justify the change against §10's trade-offs and keep simplification and sync working. **Backward compatibility of the data format is explicitly not required** — the project has no consumers yet, so the schema/DTOs/wire may change freely (see the decision in §10).
- **FR-9 — Both casing conventions tolerated.** The smoothing code reads point coordinates and pressure tolerating both `x/y/pressure` and `X/Y/Pressure` shapes, matching `drawStrokePath`'s existing dual-casing handling.
- **FR-10 — Tunable smoothing strength.** The algorithm exposes a small number of constants (e.g. spline tension and/or tessellation resolution) defined in one place so the smoothness-vs-fidelity and smoothness-vs-cost trade-offs can be adjusted without structural change.

### Non-Functional Requirements

- **NFR-1 — Performance / no jank.** Full-history redraws (resize, snapshot, every confirmed-stroke commit) and per-frame replay must remain smooth on a mid-range mobile device for boards of typical stroke counts. The added per-point cost of curve fitting and tessellation must be bounded (target: tessellation resolution chosen so cost scales linearly with stored point count and stays within today's redraw budget).
- **NFR-2 — Bounded deviation.** The rendered curve must not deviate from the original polyline by more than a small, defined fraction of board width at any point (no large bulges, loops, or corner cuts), so the smoothed stroke remains a faithful representation of the captured gesture.
- **NFR-3 — No cusps, loops, or overshoot.** The chosen algorithm must not produce self-intersections, cusps, or outward overshoot on sharp direction changes (this is the primary reason §9 recommends a centripetal parameterization over a uniform one).
- **NFR-4 — Visual consistency across surfaces.** A given stroke renders identically (same curve) whether shown as live preview, confirmed history, snapshot, or replay frame at full time — moving between states must not visibly alter the stroke's shape.
- **NFR-5 — Determinism.** Rendering is a pure function of the stored points and parameters: the same stroke always produces the same curve on every client and every redraw (no randomness, no dependence on capture timing).
- **NFR-6 — Input parity.** Smoothing applies equally to mouse, trackpad, pen, and touch strokes; pressure-bearing and pressure-less (`null`) strokes both render correctly.
- **NFR-7 — Maintainability.** The smoothing logic is isolated (a dedicated helper) with the algorithm and its parameters documented inline, so it can be tuned or swapped without touching unrelated capture, simplification, or sync code. If smoothing is placed at capture time, the helper must be reusable so render-time and capture-time share one implementation rather than diverging.

## 7. Acceptance Criteria

### US-1 / US-2 / US-3 — Smooth curves regardless of sample density

- Given a stroke whose stored points trace a curve, When it is rendered, Then the drawn path is a smooth curve with no visible facets at the stored points (tangent is continuous across interior points).
- Given a stroke captured during fast motion (sparse points) or after `simplifyPointsVisvalingamWhyatt`, When rendered, Then it appears as smooth as a densely-sampled stroke of the same shape.

### US-4 — Pressure thickness preserved

- Given a pressure-varying stroke, When rendered smoothed, Then line width still varies along the curve in proportion to per-point pressure, matching the pre-smoothing width at each stored point within rounding.
- Given a stroke with `null` pressure on every point (e.g. mouse), When rendered, Then the curve is drawn at the full configured width along its length.

### US-5 / US-6 — Faithful, no overshoot, corners respected

- Given any stroke, When rendered, Then the curve's maximum deviation from the original polyline is within the defined fraction of board width (NFR-2) — it does not bulge, loop, or self-intersect.
- Given a stroke with an intentional sharp corner, When rendered, Then the corner remains recognizably sharp (no large rounded blob) and exhibits no outward overshoot.

### US-7 — Consistent across surfaces

- Given the same stroke shown as live preview, confirmed history, late-join snapshot, and a full-time replay frame, When each is rendered, Then all four produce the same curve.

### US-8 / US-9 — Degenerate cases

- Given a single isolated point (tap/click), When rendered, Then a filled dot is drawn (unchanged from today).
- Given a stroke of exactly two points, When rendered, Then a straight segment is drawn.

### US-10 — Smooth replay

- Given a replay animating a stroke point-by-point via `upToMs`, When each frame is rendered, Then the partially-revealed stroke is smooth and only includes points at or before the cutoff, and the final frame matches the confirmed-history rendering.

### NFR — Performance

- Given a board with a typical number of strokes, When a full redraw (resize/snapshot/commit) or a replay frame occurs on a mid-range mobile device, Then there is no perceptible jank and frame timing stays within today's redraw budget.

## 8. Out of Scope

- Predictive input / lag-hiding or point *prediction* (synthesising points the device never reported). Capture-time **enrichment that keeps retained anchors as true captured positions** (per-point tangent hints, or storing curve parameters over the surviving points) is in scope and evaluated in §10; *predicting* unseen points, or in-place resampling/smoothing that relocates retained anchors, is not.
- User-facing controls or per-board settings to toggle or tune smoothing (a single internal constant may exist, but there is no UI).
- New brush styles, calligraphic/variable nib shapes, texture, or any rendering change beyond curve fitting and the existing pressure-width behavior.
- Server-side rendering or any change to the replay engine beyond its use of the shared `drawStrokePath` primitive.
- Replacing or re-tuning the `simplifyPointsVisvalingamWhyatt` thresholds themselves (a capture-time option may feed it cleaner input, but its scoring/threshold logic is not changed here).

## 9. Algorithm Options (for the smoothing curve)

These options define the *curve* used to smooth the polyline; they are independent of *where* the smoothing runs (§10 covers render-time vs capture-time placement). They are described against `drawStrokePath` because that is the render-time default, but the same curve math applies if smoothing is moved to capture time. Because Canvas 2D cannot vary `lineWidth` mid-curve and today's renderer already strokes one segment per pair of points to follow pressure, the recommended pattern for every curve option is: **compute the curve, then tessellate it into short sub-segments and interpolate pressure/width across them** (same per-segment `stroke()` model as today, just with more, curved-following segments). This keeps FR-4 (pressure width) intact.

### Option A — Quadratic Bézier through segment midpoints (a.k.a. "midpoint smoothing")

- **How:** For consecutive points `p[i-1], p[i], p[i+1]`, draw a quadratic curve from the midpoint of `(p[i-1], p[i])` to the midpoint of `(p[i], p[i+1])` using `p[i]` as the control point. Endpoints anchor to the first/last actual point.
- **Pros:** Very cheap and simple; never overshoots (curve stays within the control polygon's convex hull); rock-solid and the classic choice for signature/whiteboard smoothing; trivially supports `upToMs` clipping.
- **Cons:** The curve passes through the *midpoints*, not through the original points (it approximates, not interpolates) — corners are softened, so very sharp intentional corners round off a little. Deviation is bounded and small, satisfying NFR-2, but US-6 sharpness is slightly weaker than interpolating options.
- **Best when:** you want the lowest-risk, lowest-cost win and can accept gentle corner rounding.

### Option B — Centripetal Catmull-Rom spline → cubic Bézier (recommended)

- **How:** Treat the stored points as spline knots and convert each span to a cubic Bézier whose control points are derived from neighbors. Use the **centripetal** parameterization (exponent α = 0.5) rather than uniform.
- **Pros:** **Interpolates** every stored point (FR-3, faithful to where the user drew); smooth (C1) tangents; centripetal parameterization provably avoids cusps, self-intersections, and overshoot near sharp turns (directly satisfies NFR-3) — this is its key advantage over the naive (uniform) Catmull-Rom. A tension constant gives FR-10 tunability.
- **Cons:** Slightly more math per point than Option A; still needs tessellation for pressure width. Endpoint handling needs phantom/duplicated end knots.
- **Best when:** you want the curve to honor the actual points and behave well on sharp corners — the best overall fidelity/robustness balance. **Recommended default.**

### Option C — Circular-arc / biarc fitting (`arcTo` or explicit arcs)

- **How:** Fit a circular arc through each point triplet (or a biarc per span) and stroke arcs between points; `arcTo` can round each corner with a radius derived from local spacing.
- **Pros:** Arcs are geometrically natural for handwriting; constant-curvature segments can look very clean; no overshoot.
- **Cons:** Triplets that are nearly collinear or sharply reversed produce degenerate/huge radii needing special-casing; `arcTo`'s radius semantics make it awkward to interpolate width and to clip by `upToMs`. More edge cases than B for the same visual result.
- **Best when:** you specifically want arc aesthetics; otherwise B achieves similar smoothness with fewer degenerate cases.

### Option D — Chaikin corner-cutting subdivision

- **How:** Iteratively replace each point with two points at 1/4 and 3/4 of each segment (1–3 iterations), producing a denser polyline approximating a quadratic B-spline, then stroke the dense polyline with per-segment width as today.
- **Pros:** Dead simple, no spline math; stays within the convex hull (no overshoot); the dense output is already a per-segment polyline that fits the current width-interpolation model with almost no change.
- **Cons:** Approximates (cuts corners) rather than interpolating — corners pull inward, similar trade-off to Option A; pressure must be carried/interpolated onto the inserted points; fixed iteration count means smoothness depends on point spacing.
- **Best when:** you want a minimal diff to the existing per-segment loop and accept corner cutting.

### Recommendation

Adopt **Option B (centripetal Catmull-Rom → cubic Bézier with tessellated, pressure-interpolated sub-segments)** as the default: it interpolates the stored points (FR-3), is robust against overshoot/cusps on sharp turns (NFR-3), and exposes a tension constant for tuning (FR-10). If implementation cost or risk must be minimized for a first pass, **Option A (quadratic-midpoint)** is the safe fallback, with the understanding that it gently rounds intentional sharp corners. Tessellation resolution should be a single tunable constant chosen to keep per-stroke cost within the current redraw budget (NFR-1).

## 10. Smoothing Placement: Render-time vs Capture-time

The curve algorithm (§9) can be applied at two points in the pipeline. The placement is a deliberate decision (FR-8); the trade-offs differ significantly.

> **Decision — backward compatibility is out of scope.** The project has **no consumers yet**: there are no persisted boards, clients, or external integrations whose data or wire format must be preserved. Therefore a capture-time approach may change the stroke/point schema, DTOs, and hub messages freely, with **no migration, no dual-shape renderer, and no back-compat path required**. This removes the main cost historically attached to Option C2 and the optionality constraint on Option C1; the data format may simply be defined as desired. (If/when real consumers exist, this decision must be revisited.)

### Option R — Render-time (recommended default)

- **How:** fit the smoothing curve inside `drawStrokePath` from the already-stored points, every time a stroke is drawn (confirmed history, preview, replay frame).
- **Pros:** no change to captured/stored/synced data — zero migrations, no protocol or DTO changes, no risk to simplification or sync; all render surfaces benefit from one change; trivially reversible and retunable (it's pure presentation); deterministic and identical across clients regardless of capture timing.
- **Cons:** smoothing runs on every redraw, so cost is paid repeatedly (must stay within NFR-1's budget — e.g. via cached tessellation); it cannot improve the *input* to `simplifyPointsVisvalingamWhyatt`, so simplification still scores the raw, faceted polyline.
- **Best when:** you want the safe, low-risk win that ships independently — the recommended first step.

#### Rendering variants — converting points + curve into Canvas 2D calls

Once §9 has defined the curve geometry, `drawStrokePath` must emit actual Canvas 2D API calls. The hard constraint is that **a single stroked path uses one `lineWidth`**, so any variant that wants pressure-varying thickness (FR-4) must either break the path or stop stroking a centerline. The variants below trade fidelity for cost; all keep `lineCap`/`lineJoin = 'round'` and preserve the single-dot/endpoint case (FR-6) and `upToMs` clipping (FR-7).

- **R1 — Tessellated per-segment polyline.** Evaluate the §9 curve at many short sub-segments, then for each sub-segment `beginPath` → `moveTo`/`lineTo` → set `lineWidth` from the interpolated pressure → `stroke()`. This is the current model (one `stroke()` per pair) with more, curve-following segments. *Pros:* variable width works at sub-segment granularity; minimal change from today; trivially supports `upToMs` (stop emitting at the cutoff). *Cons:* many `stroke()` calls per stroke (the main cost; mitigate by choosing tessellation resolution per NFR-1 and/or caching the sub-segment list). *Algorithm pairing:* works with any §9 option since it consumes a flattened polyline — best with **Option B** (interpolates the points) or **Option A**; it is also the only variant that can render **Option D** (Chaikin) without throwing away its curve. *Best when:* you want the finest-grained pressure width.
- **R2 — Native curve calls, single stroke (`quadraticCurveTo`/`bezierCurveTo`).** Build one `Path2D`/path: `moveTo` the first point, then one `quadraticCurveTo`/`bezierCurveTo` per span (control points from §9), then a single `stroke()`. *Pros:* fewest API calls, best-quality native antialiasing of the curve and joins, cheapest per redraw; a `Path2D` can be **built once per stroke and cached**, then re-stroked on every redraw (big NFR-1 win). *Cons:* one `lineWidth` for the whole path — **cannot vary thickness along the curve**, so it only fits constant-width strokes (mouse / `null`-pressure, or if pressure width is dropped). *Algorithm pairing:* **Option A (quadratic-midpoint) is the natural fit** — it maps 1:1 onto `quadraticCurveTo` (stored point = control point, midpoints = on-curve joins) with no per-span control derivation; **Option B** is the runner-up via one `bezierCurveTo` per span when interpolation through the actual points is wanted. *Best when:* the stroke has uniform width; pair with R1/R3 for pressure strokes.
- **R3 — Per-span native curves, broken path (hybrid).** Like R2 but call `stroke()` once per span (or per few spans) with that span's averaged width, instead of one stroke for the whole path. *Pros:* native curve quality with width that varies at span granularity — far fewer `stroke()` calls than R1 for similar smoothness; *Cons:* width steps per span rather than per sub-segment (coarser than R1, though §9 pressure smoothing softens this); slightly more bookkeeping. *Algorithm pairing:* **Option A (quadratic-midpoint)** — one `quadraticCurveTo` per span, the simplest possible per-span path — or **Option B** via `bezierCurveTo` per span for interpolation. *Best when:* you want native-curve smoothness *and* pressure width with fewer draw calls than R1.
- **R4 — Filled variable-width ribbon (outline fill).** Don't stroke a centerline at all: offset the §9 curve by ±½·width(pressure) along its normal to build left/right boundary curves, close them into one `Path2D`, and `fill()` once (round caps via end arcs; use `'nonzero'` fill to absorb self-overlap on tight turns). *Pros:* true, continuously-varying width in a single `fill()` — the highest-quality "brush" look, closest to real ink; one cached `Path2D` per stroke. *Cons:* most math (normal/offset computation, self-intersection handling on sharp turns); harder to get right; `upToMs` clipping means rebuilding the partial outline per frame. *Algorithm pairing:* needs a smooth, differentiable centerline to offset cleanly — **Option B (centripetal Catmull-Rom)** is the best fit; Option A also works. *Best when:* width fidelity/aesthetics matter most and you can absorb the implementation cost.

**First explorative implementation: Option A + R3.** Start the spike with **Option A (quadratic-midpoint) rendered via R3 (per-span `quadraticCurveTo`, one `stroke()` per span at that span's averaged width).** Rationale: (1) it is the **easiest to implement** — quadratic midpoints need no tangent/control derivation and R3 is a small change to the existing per-segment loop; (2) it is **fast on mobile** — native curve calls with one `stroke()` per span mean far fewer draw calls than R1's full tessellation, which matters most on low-end touch devices; (3) it **keeps pressure-driven width**, which is **critical for the project's cross-platform support** (pen/touch pressure is a core input signal, so R2's constant-width path is unacceptable as the primary renderer); and (4) it lets us **measure how bad Option A's approximation error is during natural drawing** before investing in the interpolating Option B or the costlier R1/R4. If the corner-rounding error proves objectionable in practice, the fallback is to switch the *algorithm* to Option B (still under R3) or the *renderer* to R1 for finer width — both incremental from this starting point.

**Recommendation for Option R:** ship the **Option A + R3** spike first (above) and evaluate its error/perf on real devices. From there, escalate only as needed: switch to **Option B** (interpolation) if approximation error is too visible, to **R1** if you need sub-segment width fidelity, to **R2** as a cheap constant-width fast path for `null`-pressure strokes, or to **R4** if brush-quality width becomes a goal. Across all variants, build and **cache a per-stroke path/tessellation** and reuse it across redraws so confirmed-history repaints stay within the NFR-1 budget.

Capture-time approaches must **keep the retained anchors as true captured positions**. Dropping/filtering points is expected — `simplifyPointsVisvalingamWhyatt` already does it, and at least some true anchors always survive. What is an **anti-pattern and explicitly rejected** is smoothing or resampling that **relocates** the points it keeps (EMA/median/arc-length resampling that overwrites positions): it loses precision even at the points the user actually drew and corrupts the data every downstream consumer relies on. The two viable capture-time options below instead keep the surviving anchors at their true positions and add the curvature information *alongside* them.

### Option C1 — Capture-time tangent hints (exact anchors + per-point direction)

- **How:** keep every **retained** anchor at its true captured position (filtering still drops points as today), but record a compact, low-resolution **direction (tangent) hint** per surviving point — the local incoming/outgoing heading, quantized to a few bits. At render time the curve is a Hermite/Bézier spline that passes **through** each anchor using the stored tangent, instead of inferring the tangent from neighbors (as render-time Catmull-Rom must).
- **Pros:** retained anchors are true positions (lossless where kept); the curve reproduces the user's true direction at each point even after `simplifyPointsVisvalingamWhyatt` removes neighbors, so simplification can be more aggressive without changing the curve's shape; tangents are tiny (a quantized angle per point) so the storage/bandwidth cost is small; render-time work drops to evaluating a spline with known tangents (cheaper, no neighbor analysis).
- **Cons:** adds a per-point field to the point DTO; simplification must recompute/preserve the surviving points' tangents when it drops a neighbor; tangent must be captured/derived during `#appendPoint` from the local sample window. (No back-compat constraint on the field — see the §10 decision — so it need not be optional.)
- **Best when:** you want capture-time fidelity gains (especially better behavior under aggressive simplification) while keeping anchors exact and the format change minimal.

### Option C2 — Capture-time curve parameters (store the curve, not a polyline)

- **How:** change the stored representation from a polyline of sampled points to **curve parameters** — e.g. a sequence of cubic Bézier segments (anchor + control points) fit to the captured input at capture time. The retained anchors stay at their true captured positions as knots of the fitted curve; the controls carry the smoothing.
- **Pros:** the stored artifact *is* the smooth curve, so every consumer (other clients, replay, any future server-side tooling) renders identical smoothness with no render-time fitting; curve fitting can represent a gesture with far fewer parameters than sampled points (potential storage/bandwidth win beyond today's simplification); de-couples visual quality from sample density at the source.
- **Cons:** the largest format change — new stroke/point schema and DTO/wire changes (but **no migration or dual-shape renderer**, since there are no existing boards to preserve — see the §10 decision); replay's point-by-point `upToMs` reveal must be redefined in terms of curve arc length/parameter rather than point index; fitting adds capture-time cost and must bound deviation (NFR-2) so retained anchors stay faithful.
- **Best when:** you want curves as the canonical data model and are willing to take a deliberate format change to get cross-consumer consistency and maximal compression.

### Recommendation

Ship **Option R (render-time)** first — it delivers the visible smoothing win with no data-format risk and benefits all surfaces from a single, reversible change. If a capture-time fidelity gain is later justified, prefer **Option C1 (tangent hints)** as the incremental step: it keeps retained anchors at true positions and mainly helps strokes survive aggressive simplification without shape change. Reserve **Option C2 (curve parameters)** for when curves-as-data is a goal in its own right; with no consumers to migrate (§10 decision) its only real cost is the implementation itself. Do **not** adopt an in-place resample/smooth approach that relocates retained points — it is lossy at those anchors and rejected above. Whichever path is chosen, the curve math (§9) and its tunable constants (FR-10) are shared so render-time and capture-time never diverge (NFR-7), and the curve must be applied **once** to avoid double-smoothing if approaches are combined.
