# PRD: Board Aspect Ratio Negotiation

## 1. Feature Name

Board Aspect Ratio Negotiation — a fixed, per-board drawing surface whose proportions are fixed by the board's creator and that every later joiner reproduces, with all stroke coordinates stored normalized to board width so the drawing is identical (up to uniform scale) on every screen.

## 2. Epic

- **Parent PRD:** [Collaborative Whiteboard MVP](../../../../../plan/prd-collaborative-whiteboard-mvp.md)
- **Parent Architecture / Feature plan:** [Collaborative Whiteboard feature plan](../../../../../plan/feature-collaborative-whiteboard-1.md)

This feature builds directly on the MVP's real-time drawing, snapshot/late-join, and SignalR `JoinBoard` flow. It changes two cross-cutting concerns established there: the coordinate space of strokes (REQ-001, TASK-042, TASK-054) and the board document model (TASK-013), and adds an aspect-ratio negotiation step to the existing `JoinBoard` handshake (TASK-024).

## 3. Goal

### Problem

In the MVP every client sizes its canvas to its own viewport and captures stroke points in device-dependent CSS pixels relative to that canvas (see `#toCanvasPoint` in [wwwroot/js/canvas.js](../../../../../wwwroot/js/canvas.js)). As a result, the same board looks different on every screen: a stroke drawn at the bottom-right of a wide desktop lands off-canvas — or in the wrong place — on a phone, and the relative composition of a shared drawing is not preserved across participants. Collaboration is further impeded because the canvas can exceed the viewport, forcing users to scroll just to see the whole board or reach the toolbar. There is no shared notion of "the board's shape," so collaborators cannot reliably point at or build on the same region.

### Solution

Give every board a single, immutable aspect ratio that is **negotiated once** — fixed by the first client to join (the creator) — and **reproduced** by every client that later joins the same board. Stroke coordinates are stored **normalized to board width** (a resolution-independent fraction) rather than in device pixels, so a stroke occupies the same relative position and size on every screen and scales uniformly with the rendered board. Each client fits the whole board plus the drawing/replay toolbar into its viewport; scrolling pans to reveal off-screen controls rather than revealing more canvas.

### Impact

- **Consistent shared composition:** every collaborator sees the same drawing geometry, increasing the share of multi-collaborator sessions where late joiners see a faithful (not distorted) snapshot.
- **Reduced friction:** the board and toolbar fit any screen with no horizontal scroll-to-draw, improving time-to-first-stroke and perceived usability on mobile.
- **Forward compatibility:** normalized, width-relative coordinates make replay, undo, and visibility moderation (sibling sub-plans) resolution-independent without rework.

## 4. User Personas

- **Anonymous collaborator (board creator):** the first person to open a board URL. Their device's viewport proportions establish the board's aspect ratio for everyone who follows.
- **Anonymous collaborator (later joiner):** anyone who opens an already-created board, possibly on a very differently shaped or sized screen (phone, tablet, ultrawide monitor). They draw on a canvas reshaped to the board's established aspect ratio.

Both are the single anonymous-collaborator actor from the MVP; this feature does not add roles or ownership.

## 5. User Stories

- **US-1 — Creator establishes the board shape:** As a board creator, I want the board to adopt my screen's proportions when I first open it, so that I draw on a surface that naturally fits my viewport and that becomes the shared shape for everyone.
- **US-2 — Joiner reproduces the board shape:** As a later joiner, I want my canvas to take on the board's established aspect ratio, so that I see and contribute to the same composition every other collaborator sees, regardless of my screen.
- **US-3 — Whole board fits any screen:** As a collaborator on any device, I want the entire board and the drawing/replay toolbar to fit within my viewport without scrolling to draw, so that I can work without panning around to find controls or the edges of the board.
- **US-4 — Scroll reveals controls, not more board:** As a collaborator whose screen cannot show every control at once, I want scrolling to move the board aside and reveal the rest of the controls, so that the board remains a fixed, fully-visible surface and scrolling never changes the drawing area.
- **US-5 — Strokes scale with the board:** As a collaborator, I want strokes to keep their relative position and thickness as the board is scaled to fit my screen, so that the drawing looks the same (just larger or smaller) to everyone.
- **US-6 — Consistent late-join geometry:** As a late joiner, I want the existing drawing to render in the correct relative positions on my reshaped canvas, so that I see exactly what others see rather than a stretched or clipped picture.
- **US-7 — Concurrent first-joiners converge (edge case):** As one of several people who open a brand-new board URL at nearly the same moment, I want exactly one aspect ratio to win and apply to all of us, so that the board does not flicker between shapes or disagree between clients.
- **US-8 — Accept scale discrepancy across very different screens (edge case):** As a collaborator on a screen whose proportions differ greatly from the board's, I accept that the board is letterboxed/pillarboxed (margins) and rendered smaller, in exchange for everyone sharing one faithful composition.
- **US-9 — Maximize canvas on window resize / device rotation:** As a collaborator who resizes the browser window or rotates a mobile device, I want the layout to update and the board to grow to fill the maximum space its fixed aspect ratio allows in the new viewport, so that I always use as much of my screen as possible, with all content redrawn to scale to the new canvas size.

## 6. Requirements

### Functional Requirements

- **FR-1 — Board carries an aspect ratio.** The board document gains a single immutable numeric `AspectRatio` (width ÷ height) field. It is set exactly once, at board creation, and never changes thereafter.
- **FR-2 — Creator fixes the aspect ratio.** When the first client joins a not-yet-materialized board, the server records that client's reported aspect ratio as the board's `AspectRatio`. This happens atomically with board creation (the existing `GetOrCreateBoardAsync` upsert / `$setOnInsert`), so it is established in a single race-free step.
- **FR-3 — Client reports its aspect ratio on join.** The `JoinBoard` hub call carries the joining client's proposed aspect ratio (derived from its available drawing viewport). The server uses it only when creating the board; for an existing board the proposed value is ignored.
- **FR-4 — Join returns the board's aspect ratio.** The `JoinBoard` response includes the board's established `AspectRatio` so the caller can reshape its canvas. A late joiner therefore always receives the creator's ratio, not its own.
- **FR-5 — Canvas reshaped to the board ratio.** On receiving the board's aspect ratio, every client sizes its drawing canvas to that exact ratio, scaling it as large as fits within the available viewport area (after reserving space for the toolbar). Unused space becomes inert margin (letterbox/pillarbox), not drawable canvas.
- **FR-6 — Strokes stored normalized to board width.** Stroke point coordinates are stored as fractions of board width: `x = 0` is the left edge and `x = 1` is the right edge; `y` uses the **same** width-based unit so that `y` ranges `0`…`1/AspectRatio` from top to bottom and the unit is square (no distortion). Stroke width is likewise stored as a fraction of board width.
- **FR-7 — Client maps between normalized and device coordinates.** On capture, the client converts pointer positions from canvas/device pixels to normalized (width-relative) coordinates before sending; on render, it converts normalized coordinates back to device pixels using the current rendered board size and device pixel ratio.
- **FR-8 — Cursors normalized too.** Live cursor positions (`MoveCursor`/`CursorMoved`) are transmitted in the same normalized, width-relative coordinate space so remote cursors land in the correct relative location on every screen.
- **FR-9 — Board + toolbar fit the viewport.** The layout guarantees the whole board and the drawing/replay toolbar are reachable within the viewport: the board is scaled to fit, and the toolbar is always visible or reachable without resizing the board.
- **FR-10 — Scrolling pans, not resizes.** When controls cannot all fit on screen at once, scrolling moves the board/controls to reveal the remaining controls; scrolling never grows, shrinks, or reveals additional drawable board area — the board remains a fixed, fully-visible surface.
- **FR-11 — Responsive re-fit.** On viewport resize or orientation change, the client re-fits the board to the largest size its fixed aspect ratio allows in the new viewport (maximizing used space after reserving toolbar space) and re-renders all strokes at the new scale; the board's stored aspect ratio and normalized strokes are unchanged.

### Non-Functional Requirements

- **NFR-1 — Geometric fidelity.** A stroke drawn on one client must render at the same relative position and proportional thickness on every other client, within sub-pixel rounding, independent of each screen's size or DPR.
- **NFR-2 — No added round-trip.** Aspect-ratio negotiation must reuse the existing single `JoinBoard` request/response handshake — no extra network round-trip on join.
- **NFR-3 — Concurrency safety.** Establishing the creator's ratio must be race-free under concurrent first-joiners (atomic with the existing board upsert); exactly one ratio wins and all clients converge to it.
- **NFR-4 — Performance.** Re-fitting and re-rendering on resize must remain smooth (target: no perceptible jank on a mid-range mobile device for boards of typical stroke counts); coordinate conversion adds negligible per-point cost.
- **NFR-5 — Accessibility & input parity.** Mouse, trackpad, pen, and touch must all map correctly into normalized coordinates; the fitted board must remain fully usable on small touch screens.
- **NFR-6 — Data integrity & privacy.** No new personal data is collected; the aspect ratio is a non-sensitive scalar. Existing DTO-only boundary rules (never serialize domain models) and server-authoritative identity are preserved.
- **NFR-7 — Validation at the boundary.** The client-proposed aspect ratio is validated/clamped to a sane range at the server boundary before being persisted, so a malformed or absurd value cannot create a degenerate board.

## 7. Acceptance Criteria

### US-1 — Creator establishes the board shape

- Given a board name that does not yet exist, When the first client joins reporting aspect ratio `R`, Then the board is created with `AspectRatio = R` (after boundary clamping) and the join response returns that ratio.
- Given the board has been created, When the same creator's canvas is sized, Then it matches ratio `R` and fits within their viewport.

### US-2 / US-6 — Joiner reproduces the board shape & sees consistent geometry

- Given an existing board with `AspectRatio = R`, When a client on a differently shaped screen joins (reporting some other ratio `R'`), Then the join response returns `R` (not `R'`) and the client reshapes its canvas to `R`.
- Given the board already has strokes, When the late joiner renders the snapshot, Then every stroke appears at the same relative position and proportional thickness as on the creator's screen (letterboxed/pillarboxed as needed), with no stretching or clipping.

### US-3 / US-4 — Fit and scrolling behavior

- Given any viewport size, When the board loads, Then the entire board and the drawing/replay toolbar are reachable without horizontally scrolling to draw, and the board is fully visible within the fitted area.
- Given the controls cannot all fit on screen at once, When the user scrolls, Then the board/controls pan to reveal the remaining controls, And the drawable board area does not change size and no additional canvas is revealed.

### US-9 — Maximize canvas on window resize / device rotation

- Given a rendered board, When the browser window is resized or a mobile device is rotated, Then the layout updates and the board grows or shrinks to the largest size its fixed aspect ratio allows within the new viewport (after reserving toolbar space), maximizing used screen space.
- Given the viewport has changed, When the board re-fits, Then all content is redrawn to scale to the new canvas size, preserving identical relative positions and proportional thickness, with no stretching, clipping, or distortion.
- Given a rapid sequence of resize events, When the board re-fits, Then the final rendered board matches the final viewport without leaving stale or mis-scaled content.

### US-5 / FR-11 — Strokes scale with the board

- Given a rendered board, When the viewport is resized or the device is rotated, Then the board re-fits to its fixed aspect ratio and all strokes re-render at the new scale keeping identical relative positions and proportional thickness.
- Given two clients of very different screen sizes, When both view the same stroke, Then it occupies the same fraction of board width on both (it merely looks larger or smaller in absolute pixels).

### US-7 / NFR-3 — Concurrent first-joiners converge

- Given several clients open the same brand-new board URL near-simultaneously with differing reported ratios, When the board is created, Then exactly one `AspectRatio` is persisted and every client's join response returns that same value (no per-client divergence, no flicker between shapes).

### US-8 / FR-5 — Accepted scale discrepancy

- Given a screen whose proportions differ greatly from the board's, When the board is fitted, Then it is centered with inert margins and rendered smaller, And the margins are not drawable (pointer events there produce no stroke).

### FR-6 / FR-7 / FR-8 — Normalized coordinates

- Given a stroke drawn at a known fractional position, When it is persisted and re-fetched, Then its coordinates are width-relative fractions (x in `0..1`, y in `0..1/AspectRatio`) independent of the drawing device's pixel dimensions.
- Given a remote cursor update, When it is received by another client, Then the cursor renders at the same relative board location it had on the sender's screen.

## 8. Out of Scope

- **Changing a board's aspect ratio after creation** — the ratio is immutable; no resize, re-crop, or re-negotiation of an existing board.
- **Per-user or per-view zoom/pan within the board** — the board is fit-to-screen only; pinch-zoom/pan navigation of the canvas is not part of this feature.
- **Infinite or scrollable canvas** — the board is a single fixed-proportion surface; scrolling reveals controls, never more canvas.
- **Letting users pick a specific aspect ratio** (e.g. 16:9 presets) — the ratio is derived implicitly from the creator's viewport, not chosen from a menu.
- **Owner/admin control over board shape** — there is no ownership in this feature; the creator only sets the ratio implicitly by being first.
- **Cross-board layout, thumbnails, or previews** at a normalized aspect ratio.
