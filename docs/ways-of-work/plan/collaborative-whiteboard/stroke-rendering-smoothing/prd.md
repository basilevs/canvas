# PRD: Stroke Rendering Smoothing

## 1. Feature Name

Stroke Rendering Smoothing — render the polyline that backs each stroke as a continuous, visually smooth curve (via interpolating splines or arcs) so that the angularity introduced by dropped pointer samples and point simplification disappears, without changing how strokes are captured, normalized, stored, or synchronized.

## 2. Epic

- **Parent PRD:** [Collaborative Whiteboard MVP](../../../../../plan/prd-collaborative-whiteboard-mvp.md)
- **Parent Architecture / Feature plan:** [Collaborative Whiteboard feature plan](../../../../../plan/feature-collaborative-whiteboard-1.md)
- **Sibling feature:** [Board Aspect Ratio Negotiation](../board-aspect-ratio-negotiation/prd.md) — established the normalized, width-relative coordinate space this feature renders.

This feature is **render-only**. It builds on the MVP's freehand drawing and the shared rendering primitive `drawStrokePath` in [wwwroot/js/canvas.js](../../../../../wwwroot/js/canvas.js), which is the single function used by the live confirmed-stroke layer, the in-flight preview overlay, and the replay engine ([wwwroot/js/replay.js](../../../../../wwwroot/js/replay.js)). It does not touch the wire protocol, the persisted stroke model, the simplification step (`simplifyPointsVisvalingamWhyatt`), or coordinate normalization.

## 3. Goal

### Problem

Strokes are stored and rendered as a sequence of straight line segments connecting captured pointer samples. Two upstream effects make that polyline visibly faceted:

1. **Dropped samples during fast motion.** A quickly moving pointer emits fewer `pointermove` events per unit of arc length, so the page never receives some points along the true path. The gaps between the surviving points are bridged with long straight chords.
2. **Deliberate point reduction.** `simplifyPointsVisvalingamWhyatt` removes low-significance points to save storage and bandwidth and to keep redraw cheap (see `simplifyPointsVisvalingamWhyatt` in [wwwroot/js/canvas.js](../../../../../wwwroot/js/canvas.js)), which by design leaves a sparser polyline.

Because the renderer connects these sparse points with straight segments (`context.lineTo` per segment in `drawStrokePath`), curved gestures show flat facets and visible corners ("angular" strokes), degrading the perceived quality of every drawing for every collaborator and in replay.

### Solution

Replace the straight chord-to-chord rendering inside the shared rendering primitive with a smooth interpolating curve fitted through the existing stored points. The captured/stored geometry is unchanged; only the path drawn between consecutive points becomes a curve (Bézier or circular arc) instead of a straight line. Because the change lives in `drawStrokePath`, all three render surfaces — confirmed history, live preview, and replay — benefit identically with no protocol or storage changes. The curve must continue to honor per-point, pressure-driven line width and the single-tap dot case.

### Impact

- **Higher perceived drawing quality:** curved gestures look smooth rather than faceted on every client, improving the core "feels good to draw" experience that the MVP exists to validate.
- **Decouples visual quality from sample density:** aggressive simplification and unavoidable fast-motion sample loss no longer translate into visible angularity, so the existing storage/bandwidth savings can be kept (or even increased) without a quality penalty.
- **Zero protocol/storage cost:** smoothing is a client-side render concern, so it ships without migrations, new fields, or added network traffic.

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
- **FR-8 — Capture/storage/protocol untouched.** No change to point capture (`#appendPoint`/`#toCanvasPoint`), to `simplifyPointsVisvalingamWhyatt`, to the stroke/point DTOs, or to any hub message. Smoothing is purely a rendering transform applied at draw time.
- **FR-9 — Both casing conventions tolerated.** The smoothing code reads point coordinates and pressure tolerating both `x/y/pressure` and `X/Y/Pressure` shapes, matching `drawStrokePath`'s existing dual-casing handling.
- **FR-10 — Tunable smoothing strength.** The algorithm exposes a small number of constants (e.g. spline tension and/or tessellation resolution) defined in one place so the smoothness-vs-fidelity and smoothness-vs-cost trade-offs can be adjusted without structural change.

### Non-Functional Requirements

- **NFR-1 — Performance / no jank.** Full-history redraws (resize, snapshot, every confirmed-stroke commit) and per-frame replay must remain smooth on a mid-range mobile device for boards of typical stroke counts. The added per-point cost of curve fitting and tessellation must be bounded (target: tessellation resolution chosen so cost scales linearly with stored point count and stays within today's redraw budget).
- **NFR-2 — Bounded deviation.** The rendered curve must not deviate from the original polyline by more than a small, defined fraction of board width at any point (no large bulges, loops, or corner cuts), so the smoothed stroke remains a faithful representation of the captured gesture.
- **NFR-3 — No cusps, loops, or overshoot.** The chosen algorithm must not produce self-intersections, cusps, or outward overshoot on sharp direction changes (this is the primary reason §9 recommends a centripetal parameterization over a uniform one).
- **NFR-4 — Visual consistency across surfaces.** A given stroke renders identically (same curve) whether shown as live preview, confirmed history, snapshot, or replay frame at full time — moving between states must not visibly alter the stroke's shape.
- **NFR-5 — Determinism.** Rendering is a pure function of the stored points and parameters: the same stroke always produces the same curve on every client and every redraw (no randomness, no dependence on capture timing).
- **NFR-6 — Input parity.** Smoothing applies equally to mouse, trackpad, pen, and touch strokes; pressure-bearing and pressure-less (`null`) strokes both render correctly.
- **NFR-7 — Maintainability.** The smoothing logic is isolated (a dedicated helper) with the algorithm and its parameters documented inline, so it can be tuned or swapped without touching capture, simplification, or sync code.

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

- Any change to point capture, the `simplifyPointsVisvalingamWhyatt` simplification step, coordinate normalization, the stroke/point data model, persistence, or any hub/wire message.
- Smoothing or re-fitting the *captured* points (e.g. storing a smoothed polyline) — this feature smooths only at render time; stored geometry is untouched.
- Predictive input / lag-hiding, point prediction, or any change to how fast-motion samples are *captured* (this feature compensates at render time, it does not try to recover dropped samples).
- User-facing controls or per-board settings to toggle or tune smoothing (a single internal constant may exist, but there is no UI).
- New brush styles, calligraphic/variable nib shapes, texture, or any rendering change beyond curve fitting and the existing pressure-width behavior.
- Server-side or replay-engine rendering changes beyond their use of the shared `drawStrokePath` primitive.

## 9. Algorithm Options (for the smoothing curve)

All options below are render-time only and apply to the polyline of stored points inside `drawStrokePath`. Because Canvas 2D cannot vary `lineWidth` mid-curve and today's renderer already strokes one segment per pair of points to follow pressure, the recommended pattern for every curve option is: **compute the curve, then tessellate it into short sub-segments and interpolate pressure/width across them** (same per-segment `stroke()` model as today, just with more, curved-following segments). This keeps FR-4 (pressure width) intact.

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
