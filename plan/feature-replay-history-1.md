---
goal: Implement video-like history replay with inactivity compression for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, replay, history, animation, frontend, rest-api]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Implement a video-like replay system that allows users to watch the board's drawing history animated chronologically. The replay skips periods of inactivity (configurable threshold), animates individual strokes point-by-point using stored temporal metadata, and provides VCR-style playback controls.

## 1. Requirements & Constraints

- **REQ-001**: Users can trigger a replay mode that animates the board's stroke history in chronological order
- **REQ-002**: Replay uses `Stroke.Timestamp` for inter-stroke timing and `Point.TimeOffset` for intra-stroke point-by-point animation
- **REQ-003**: Inactivity gaps exceeding a configurable threshold (default 3 seconds) are compressed/skipped during playback
- **REQ-004**: Playback speed is adjustable: 1x, 2x, 4x multipliers
- **REQ-005**: VCR controls: play, pause, seek (timeline scrubber), stop (exit replay)
- **REQ-006**: Replay respects the requesting user's visibility permissions — non-owner members see history filtered by HiddenRanges; owner can replay full unfiltered history
- **REQ-007**: Replay data is fetched from a dedicated REST endpoint, paginated for large boards
- **CON-001**: Frontend is vanilla JavaScript on HTML5 Canvas (no framework)
- **CON-002**: Backend is ASP.NET Core 10.0 with MongoDB Atlas
- **CON-003**: StrokeEvent documents already exist with `Timestamp`, `SequenceNumber`, `Stroke.Duration`, and `Stroke.Points[].TimeOffset` fields (provided by the main whiteboard plan)
- **DEP-001**: Depends on `Services/StrokeEventService.cs` providing `GetEventsAsync(boardId)` and filtered event queries
- **DEP-002**: Depends on `Middleware/UserIdentityMiddleware.cs` for userId resolution on REST requests
- **DEP-003**: Depends on `Services/BoardService.cs` providing `GetHiddenRangesAsync(boardId)` and ownership checks

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Implement the REST API endpoint that serves replay history with access control and pagination

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Add REST endpoint `GET /api/boards/{name}/history` in `Program.cs` with query parameters: `userId` (string, resolved from cookie), `page` (int, default 1), `pageSize` (int, default 100, max 500). Route: `app.MapGet("/api/boards/{name}/history", ...)` | | |
| TASK-002 | Implement endpoint handler: resolve userId from `HttpContext.Items["UserId"]`, verify membership via `BoardService.IsMemberAsync`, return 403 if unauthorized. Fetch board to determine ownership and HiddenRanges. | | |
| TASK-003 | If caller is owner: return all StrokeEvents ordered by SequenceNumber, paginated. If caller is non-owner member: filter out events where `event.Stroke.UserId` matches a HiddenRange entry AND `event.Timestamp <= hiddenRange.HiddenBefore`. | | |
| TASK-004 | Return JSON response: `{ "events": [...], "page": 1, "pageSize": 100, "totalEvents": N, "totalPages": M }`. Each event includes: `id`, `type` (Add/Remove), `stroke` (with points, color, width, duration), `timestamp`, `sequenceNumber`. | | |
| TASK-005 | Add integration test `Tests/ReplayHistoryEndpointTests.cs`: verify pagination, verify HiddenRanges filtering for non-owner, verify owner receives unfiltered history, verify 403 for non-members. | | |

### Implementation Phase 2

- GOAL-002: Implement the frontend replay engine with temporal animation and gap compression

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-006 | Create `wwwroot/js/replay.js` — export `ReplayEngine` class with constructor accepting a canvas 2D context reference and configuration `{ gapThresholdMs: 3000, speed: 1 }`. | | |
| TASK-007 | Implement `ReplayEngine.loadHistory(boardName)` — fetch all pages from `GET /api/boards/{boardName}/history` (auto-paginate until all pages consumed), store events in `this.events` array sorted by `sequenceNumber`. | | |
| TASK-008 | Implement `ReplayEngine.computeTimeline()` — precompute a timeline array: for each event, calculate `realTimeGapMs` (time since previous event's timestamp). If `realTimeGapMs > gapThresholdMs`, compress to `gapThresholdMs`. Store `playbackOffsetMs` (cumulative adjusted time) for each event. Compute `totalDurationMs` for the entire replay. | | |
| TASK-009 | Implement `ReplayEngine.play()` — start a `requestAnimationFrame` loop. Track `elapsedMs` using `performance.now()` scaled by `this.speed`. For each frame, determine which events should have started based on `elapsedMs` vs `playbackOffsetMs`. For active strokes, animate points using `Point.TimeOffset` scaled by speed. | | |
| TASK-010 | Implement intra-stroke animation: when rendering a stroke in progress, draw points incrementally — only points where `(elapsedMs - strokeStartMs) * speed >= point.TimeOffset` are rendered. Use `ctx.beginPath()` / `ctx.lineTo()` for smooth progressive drawing. | | |
| TASK-011 | Implement `ReplayEngine.pause()` — pause the `requestAnimationFrame` loop, store current `elapsedMs`. | | |
| TASK-012 | Implement `ReplayEngine.resume()` — resume from stored `elapsedMs`. | | |
| TASK-013 | Implement `ReplayEngine.seek(positionRatio)` — accept 0.0–1.0, compute target `elapsedMs = positionRatio * totalDurationMs`, clear canvas, re-render all events that complete before target time (instant render), then set playback position for animation to continue from there. | | |
| TASK-014 | Implement `ReplayEngine.setSpeed(multiplier)` — accept 1, 2, or 4. Adjust elapsed time tracking so playback continues smoothly from current position at new speed. | | |
| TASK-015 | Implement `ReplayEngine.stop()` — stop animation loop, clear canvas, fire `onStop` callback to notify UI. | | |
| TASK-016 | Handle `Remove` events in replay: when a Remove event's playback time is reached, erase the referenced stroke from the canvas (re-render all prior visible strokes excluding removed ones). | | |

### Implementation Phase 3

- GOAL-003: Implement the replay UI controls and integrate with the whiteboard page

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-017 | Add replay button to `wwwroot/index.html` toolbar: `<button id="btn-replay">▶ Replay</button>` — visible to all board members. | | |
| TASK-018 | Create replay overlay in `index.html`: a semi-transparent overlay div containing: timeline scrubber (`<input type="range" min="0" max="1" step="0.001">`), play/pause button, stop button, speed selector (`<select>` with 1x/2x/4x options), elapsed time display (`00:00 / 00:00`). Hidden by default, shown during replay. | | |
| TASK-019 | Wire replay button click: instantiate `ReplayEngine(canvasCtx, config)`, call `loadHistory(boardName)`, show overlay, disable drawing tools, call `play()`. | | |
| TASK-020 | Wire overlay controls: play/pause toggles `pause()`/`resume()`, stop calls `stop()` and hides overlay + re-enables drawing tools + reloads current snapshot, scrubber `input` event calls `seek(value)`, speed selector `change` calls `setSpeed(value)`. | | |
| TASK-021 | Wire `ReplayEngine.onProgress` callback: update scrubber position and elapsed time display on each animation frame (`currentMs / totalDurationMs`). Format time as `MM:SS`. | | |
| TASK-022 | Wire `ReplayEngine.onStop` callback: hide overlay, restore drawing tools, reload board snapshot from SignalR (`JoinBoard` or request fresh snapshot). | | |
| TASK-023 | Add CSS in `wwwroot/css/style.css` for replay overlay: fixed positioning over canvas, dark semi-transparent background for controls bar at bottom, styled scrubber, responsive layout. | | |

### Implementation Phase 4

- GOAL-004: Testing and validation of replay functionality

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-024 | Unit test `Tests/ReplayEngineTests.js` (or inline in HTML test page): verify `computeTimeline()` compresses gaps > 3s, verify totalDurationMs is correct, verify seek renders correct state at midpoint. | | |
| TASK-025 | Integration test `Tests/ReplayHistoryEndpointTests.cs`: verify endpoint returns events with correct pagination metadata, verify HiddenRanges filtering excludes correct events, verify owner sees all events. | | |
| TASK-026 | Manual test — Draw 5 strokes with 10+ second pauses between some. Click replay. Verify inactivity gaps are compressed, strokes animate point-by-point, total replay duration is much shorter than real elapsed time. | | |
| TASK-027 | Manual test — During replay, click pause, move scrubber to 50%, resume. Verify canvas shows correct state at midpoint and continues animating from there. | | |
| TASK-028 | Manual test — Change speed from 1x to 4x during playback. Verify animation speeds up smoothly without jumping. | | |
| TASK-029 | Manual test — As non-owner on a board with hidden contributions: trigger replay. Verify hidden user's strokes do NOT appear in the replay. As owner, verify they DO appear. | | |

## 3. Alternatives

- **ALT-001**: Server-side video rendering (FFmpeg) to produce MP4 — rejected; adds heavy server dependency, eliminates interactivity (seek/pause), and increases infrastructure cost
- **ALT-002**: WebGL-based canvas rendering for replay — rejected; raw Canvas 2D API is sufficient for stroke replay and avoids WebGL complexity for a drawing app
- **ALT-003**: Stream events via SignalR during replay instead of REST fetch — rejected; REST with pagination is simpler, cacheable, and doesn't tie up a WebSocket for a read-only operation
- **ALT-004**: Store pre-computed replay timeline in MongoDB — rejected; timeline computation is lightweight (client-side) and storing it would create stale data on every undo/hide operation

## 4. Dependencies

- **DEP-001**: `Services/StrokeEventService.cs` — provides `GetEventsAsync(boardId)` returning all events ordered by SequenceNumber
- **DEP-002**: `Services/BoardService.cs` — provides `GetHiddenRangesAsync(boardId)`, `IsMemberAsync(boardId, userId)`, ownership check
- **DEP-003**: `Middleware/UserIdentityMiddleware.cs` — resolves userId from HttpOnly cookie on REST requests
- **DEP-004**: `Models/StrokeEvent.cs` — document with `Type`, `Stroke` (containing `Points[].TimeOffset`, `Duration`), `Timestamp`, `SequenceNumber`
- **DEP-005**: `Models/Point.cs` — must include `TimeOffset` (long, milliseconds since stroke start)

## 5. Files

- **FILE-001**: `Program.cs` — Add `MapGet("/api/boards/{name}/history", ...)` endpoint registration
- **FILE-002**: `wwwroot/js/replay.js` — ReplayEngine class: history loading, timeline computation, animation loop, gap compression, seek, speed control
- **FILE-003**: `wwwroot/index.html` — Add replay button to toolbar, add replay overlay HTML (scrubber, controls, time display)
- **FILE-004**: `wwwroot/css/style.css` — Add replay overlay styles (positioned over canvas, control bar at bottom)
- **FILE-005**: `Tests/ReplayHistoryEndpointTests.cs` — Integration tests for the history REST endpoint

## 6. Testing

- **TEST-001**: Integration test — `GET /api/boards/{name}/history` returns paginated events with correct `totalPages` and `totalEvents` counts
- **TEST-002**: Integration test — Non-member receives 403 from history endpoint
- **TEST-003**: Integration test — Non-owner member receives history filtered by HiddenRanges (hidden user's events before cut-off are excluded)
- **TEST-004**: Integration test — Owner receives complete unfiltered history regardless of HiddenRanges
- **TEST-005**: Unit test — `computeTimeline()` with events [t=0s, t=1s, t=15s, t=16s] compresses the 14s gap to 3s, resulting in totalDuration = 0 + 1 + 3 + 1 = 5s of playback
- **TEST-006**: Unit test — `seek(0.5)` on a timeline with 4 strokes renders exactly the first 2 strokes completely
- **TEST-007**: Manual test — Full replay with gap compression behaves as described in Phase 4 TASK-026–029

## 7. Risks & Assumptions

- **RISK-001**: Very large boards (10,000+ events) may cause slow initial fetch — mitigated by pagination and potential future streaming/chunked loading
- **RISK-002**: `requestAnimationFrame` timing inconsistencies across browsers may cause slight playback drift — mitigated by using `performance.now()` for elapsed time rather than frame counting
- **RISK-003**: Seek to arbitrary position requires re-rendering all prior strokes (O(n) for n events before seek point) — acceptable for demo scale; could be optimized with periodic snapshots for very large boards
- **ASSUMPTION-001**: StrokeEvent documents already contain `Timestamp` and `Stroke.Points[].TimeOffset` populated by the main whiteboard implementation
- **ASSUMPTION-002**: Canvas 2D context can be cleared and re-rendered fast enough for seek operations (target: <100ms for 1000 strokes)
- **ASSUMPTION-003**: Replay is read-only — drawing tools are disabled during playback and re-enabled on stop

## 8. Related Specifications / Further Reading

- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — parent plan containing data model, SignalR hub, and persistence layer
- [requestAnimationFrame documentation](https://developer.mozilla.org/en-US/docs/Web/API/window/requestAnimationFrame)
- [Canvas 2D API — Path2D and drawing](https://developer.mozilla.org/en-US/docs/Web/API/CanvasRenderingContext2D)
- [performance.now() for high-resolution timing](https://developer.mozilla.org/en-US/docs/Web/API/Performance/now)
