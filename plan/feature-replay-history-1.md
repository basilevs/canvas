---
goal: Implement video-like history replay (with inactivity compression) and single-step undo for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, replay, history, undo, animation, frontend, rest-api]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Implement a video-like replay system that allows users to watch the board's drawing history animated chronologically. The replay skips periods of inactivity (configurable threshold), animates individual strokes point-by-point using stored temporal metadata, and provides VCR-style playback controls.

Because replay is the sole consumer of the append-only event log, this plan also **introduces** that foundation (relocated out of the MVP, which persists only the live `Board.ActiveStrokes` snapshot): the `StrokeEvent` document, `StrokeEventService`, the `StrokeEvents` **native time-series collection**, and the layering of event-append onto the core hub `SendStroke`. Introducing the log also **inverts the source of truth**: from this plan onward the append-only log is authoritative and the live `Board.ActiveStrokes` snapshot becomes a **derived materialized view** of current state (the always-current keyframe, always rebuildable by folding the log), rather than the standalone store it is in the MVP. See [feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md) for the MVP that this builds upon.

This plan also **owns undo** (single-step removal of the caller's last stroke). Undo is the producer of `Remove` `StrokeEvent`s, so it belongs with the event log it depends on: it queries the log for the caller's most recent stroke, removes it from the live snapshot, records a `Remove` event, and broadcasts the removal. Undo (Phase 5) depends only on the Phase 0 event-log foundation and can be implemented immediately after it, independently of the replay UI (Phases 1–4).

## 1. Requirements & Constraints

- **REQ-001**: Users can trigger a replay mode that animates the board's stroke history in chronological order
- **REQ-002**: Replay uses `Stroke.Timestamp` for inter-stroke timing and `Point.TimeOffset` for intra-stroke point-by-point animation
- **REQ-003**: Inactivity gaps exceeding a configurable threshold (default 3 seconds) are compressed/skipped during playback
- **REQ-004**: Playback speed is adjustable: 1x, 2x, 4x multipliers
- **REQ-005**: VCR controls: play, pause, seek (timeline scrubber), stop (exit replay)
- **REQ-006**: On its own (layered only on the MVP) this plan's history endpoint is **open to any identified caller** and serves the **complete unfiltered** stroke history — consistent with the MVP where all boards are public. The route slug is only a boundary input; the handler resolves `/api/boards/{name}` to `boardId` before querying internal services. The **membership access gate** (reject non-members with `403`) and owner-controlled visibility filtering of replay (HiddenRanges cut-offs) are separate concerns layered on by [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md), which depends on the membership and HiddenRange models from the administration/moderation plans
- **REQ-007**: Replay data is fetched from a dedicated REST endpoint, paginated for large boards
- **REQ-008**: This plan introduces the append-only event log stored in a **native MongoDB time-series collection**: every accepted stroke is recorded as an `Add` `StrokeEvent` keyed on `Timestamp` (the time-series `timeField`) and `BoardId` (the `metaField`), carrying the embedded stroke's temporal metadata. Events are ordered chronologically by `Timestamp` (ties broken by the stable stroke `Id`) — there is no per-board `SequenceNumber`
- **REQ-009**: A user can undo their own most recently drawn stroke; undo records a `Remove` `StrokeEvent` (the authoritative log entry), then removes the stroke from the derived `Board.ActiveStrokes` snapshot projection, and broadcasts the removal so every connected client updates. Undo only affects the caller's own strokes and is a no-op when the caller has no remaining strokes
- **CON-001**: Frontend is vanilla JavaScript on HTML5 Canvas (no framework)
- **CON-002**: Backend is ASP.NET Core 10.0 with MongoDB Atlas
- **CON-003**: `StrokeEvent` documents carry `Timestamp` (the `timeField`), `Type`, `Stroke.Duration`, and `Stroke.Points[].TimeOffset`, and are persisted in a **native time-series collection** (`metaField` = `BoardId`, `granularity` = `seconds`). The `Stroke`/`Point` temporal fields are reused from the MVP data model, but the event-log document and its collection are introduced by this plan (Phase 0)
- **DEP-001**: `Services/StrokeEventService.cs` — introduced by this plan (Phase 0); provides `GetEventsAsync(boardId)` and filtered event queries
- **DEP-002**: `Services/BoardService.cs` — provides `GetBoardByNameAsync(name)` so the history route can resolve the URL slug to `boardId` before querying the event log
- **DEP-003**: Depends on `Middleware/UserIdentityMiddleware.cs` for userId resolution on REST requests
- **DEP-004**: This plan's history endpoint requires **no** membership model and builds on the MVP alone (it is open to any identified caller, REQ-006). The member-access gate (`BoardService.IsMemberAsync`, from [feature-board-administration-1.md](./feature-board-administration-1.md)) is layered onto the endpoint by [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md), not by this plan

## 2. Implementation Steps

### Implementation Phase 0

- GOAL-000: Introduce the append-only event log foundation (relocated from the MVP) that records every stroke for later replay

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-030 | Create `Models/StrokeEvent.cs` — append-only event document: `Id` (`ObjectId`), `BoardId` (string — the time-series `metaField`), `Type` (enum `EventType { Add, Remove }`), `Stroke` (embedded `Stroke`, reusing the MVP model with its `Duration`/`Points[].TimeOffset` temporal metadata), `Timestamp` (UTC `DateTime` — the time-series `timeField`, native BSON date). Stored in the `StrokeEvents` native time-series collection (TASK-033). No `SequenceNumber` — ordering is by `Timestamp`, with the embedded stroke's stable `Id` as the tiebreaker. `Add` events are produced by `SendStroke` (Phase 0); `Remove` events are produced by undo (Phase 5). | | |
| TASK-031 | Add a `StrokeEvents` typed collection accessor to `Services/MongoDbContext.cs` (and `IMongoDbContext`), extending the MVP context which exposes only `Boards` and `Users`. | | |
| TASK-032 | Create `Services/StrokeEventService.cs` (and `IStrokeEventService`) with: `AppendEventAsync(boardId, EventType type, Stroke stroke)` (stamps the server `Timestamp` and inserts the measurement; **the de-duplication authority** — since the time-series collection supports no unique index (TASK-033), it de-duplicates `Add` events at the application level by the stroke's client-generated `Id`, skipping the insert when an `Add` for that `Id` already exists on the board, so a stroke re-sent on reconnect is never double-logged; this is the role the MVP located in the snapshot `$push`, now moved to the authoritative log), `GetEventsAsync(boardId)` (all events ordered by `Timestamp`, then stroke `Id`), `GetRecentEventsAsync(boardId, count)`, and `GetEventsSinceAsync(boardId, sinceTimestamp)` (incremental cursor; callers de-duplicate by stroke `Id` at the boundary since timestamps can tie). Register `IStrokeEventService` in DI in `Program.cs`. | | |
| TASK-033 | Create the `StrokeEvents` collection as a **native MongoDB time-series collection**: `db.createCollection("StrokeEvents", { timeseries: { timeField: "Timestamp", metaField: "BoardId", granularity: "seconds" } })`. This auto-provisions the internal clustered `{ metaField, timeField }` index that serves per-board chronological reads. Add a secondary index on the embedded stroke `Id` to support undo's last-stroke lookup and read-side de-duplication. Note: time-series collections **do not support unique indexes**, so per-board ordering relies on the `timeField` rather than a DB-enforced unique sequence. | | |
| TASK-034 | Layer the append-only event log onto the core hub `SendStroke` (MVP TASK-026) and **invert the MVP's persistence order**: now that the log exists it is the single source of truth, so first call `StrokeEventService.AppendEventAsync(boardId, EventType.Add, stroke)` (the durable record and the idempotency/de-duplication authority — TASK-032), then **project** the stroke into the derived `Board.ActiveStrokes` snapshot via `AddStrokeToSnapshotAsync(boardId, stroke)` (a materialized current-state view, always rebuildable by folding the log), then broadcast `StrokeReceived(stroke)`. Log append comes first so history is durable before the derived view is updated; a failed snapshot projection self-heals on the next fold/keyframe rebuild, whereas a failed append would lose history. In the MVP (no log) the snapshot was necessarily authoritative; from this plan onward the log is authoritative and the snapshot is its projection. This is the only producer of `Add` events in scope. | | |
| TASK-035 | Add MSTest integration tests for the event log in `Tests/StrokeEventServiceTests.cs` (relocated from the MVP): verify `AppendEventAsync` stamps a server `Timestamp` and that `GetEventsAsync` returns events in chronological (`Timestamp`) order. Exercises a MongoDB test database. | | |

### Implementation Phase 1

- GOAL-001: Implement the REST API endpoint that serves replay history with access control and pagination

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Add REST endpoint `GET /api/boards/{name}/history` in `Program.cs` with query parameters `page` (int, default 1) and `pageSize` (int, default 100, max 500). The caller's identity is **not** a query/body parameter — it is resolved server-side from `HttpContext.Items["UserId"]` (see TASK-002) and never trusted from the client (consistent with the parent plan's SEC-002), so it cannot be supplied or spoofed via the URL (avoids leaking identity through `Referer`/logs/history). Route: `app.MapGet("/api/boards/{name}/history", ...)` | | |
| TASK-002 | Implement endpoint handler: resolve userId from `HttpContext.Items["UserId"]` (so an identity is always present), then fetch the board (return `404` if absent). On its own this plan applies **no** membership gate — any identified caller is served (REQ-006); the `IsMemberAsync` `403` gate is layered on later by [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md). | | |
| TASK-003 | Return all StrokeEvents for the board ordered by Timestamp, paginated. Note: the membership access gate and owner-controlled HiddenRanges filtering for non-owner members are layered onto this handler by [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md); on its own this plan serves the unfiltered history to any identified caller. | | |
| TASK-004 | Return JSON response: `{ "events": [...], "page": 1, "pageSize": 100, "totalEvents": N, "totalPages": M }`. Each event includes: `id`, `type` (Add/Remove), `stroke` (with id, points, color, width, duration), `timestamp`. | | |
| TASK-005 | Add integration test `Tests/ReplayHistoryEndpointTests.cs`: verify pagination metadata and that an identified caller receives the complete history after the route slug is resolved to `boardId`. (The membership-`403` gate and HiddenRanges filtering tests live in [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md).) | | |

### Implementation Phase 2

- GOAL-002: Implement the frontend replay engine with temporal animation and gap compression

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-006 | Create `wwwroot/js/replay.js` — export `ReplayEngine` class with constructor accepting a canvas 2D context reference and configuration `{ gapThresholdMs: 3000, speed: 1 }`. | | |
| TASK-007 | Implement `ReplayEngine.loadHistory(boardName)` — fetch all pages from `GET /api/boards/{boardName}/history` (auto-paginate until all pages consumed), store events in `this.events` array sorted by `timestamp` (ties broken by stroke `id`). | | |
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
| TASK-025 | Integration test `Tests/ReplayHistoryEndpointTests.cs`: verify the endpoint returns events with correct pagination metadata and that any identified caller receives the complete history. (The membership-`403` gate and HiddenRanges filtering tests live in [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md).) | | |
| TASK-026 | Manual test — Draw 5 strokes with 10+ second pauses between some. Click replay. Verify inactivity gaps are compressed, strokes animate point-by-point, total replay duration is much shorter than real elapsed time. | | |
| TASK-027 | Manual test — During replay, click pause, move scrubber to 50%, resume. Verify canvas shows correct state at midpoint and continues animating from there. | | |
| TASK-028 | Manual test — Change speed from 1x to 4x during playback. Verify animation speeds up smoothly without jumping. | | |

### Implementation Phase 5

- GOAL-005: Implement single-step undo (remove the caller's last stroke) on top of the Phase 0 event log. Depends only on Phase 0 and is independent of the replay UI (Phases 1–4).

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-036 | Extend `IStrokeEventService`/`StrokeEventService` with `GetLastAddByUserAsync(boardId, userId)` — return the caller's most recent `Add` event whose stroke is still present in the snapshot (no later `Remove` for the same stroke id), or `null` if the caller has no removable stroke. | | |
| TASK-037 | Add `BoardService.RemoveStrokeFromSnapshotAsync(boardId, strokeId)` — remove the stroke with the given id from the board's `ActiveStrokes` snapshot. | | |
| TASK-038 | Add the hub method `UndoLastStroke(boardName)` to `Hubs/WhiteboardHub.cs` (`Hub<IWhiteboardClient>`): resolve userId from `Context.GetHttpContext().Items["UserId"]` (server-assigned); resolve the route slug to `boardId` once, then find the caller's last removable stroke via `GetLastAddByUserAsync`; if none, no-op. Otherwise append a `Remove` `StrokeEvent` for the stroke first (`AppendEventAsync(boardId, EventType.Remove, stroke)` — the authoritative log entry), then remove it from the derived snapshot projection (`RemoveStrokeFromSnapshotAsync(boardId, strokeId)`), and broadcast `StrokeRemoved(strokeId)` to the group, consistent with the log-first ordering of `SendStroke` (TASK-034). Per GUD-009, extend `IWhiteboardClient` with `StrokeRemoved(strokeId)` and broadcast via the typed proxy, never `SendAsync`. Note: member-access enforcement on private boards is layered onto this method by [feature-board-administration-1.md](./feature-board-administration-1.md), consistent with the guard on `SendStroke`. | | |
| TASK-039 | Add an undo control to the `wwwroot/index.html` toolbar (`<button id="btn-undo">↶ Undo</button>`) wired to call `UndoLastStroke(currentBoardName)`. | | |
| TASK-040 | In `wwwroot/js/connection.js` register a `StrokeRemoved(strokeId)` handler; in `wwwroot/js/app.js` handle it by removing the stroke from the local canvas state and re-rendering. | | |
| TASK-041 | Add integration test `Tests/UndoTests.cs` — `UndoLastStroke` removes only the caller's most recent stroke from the snapshot, appends a `Remove` event, and broadcasts `StrokeRemoved`; repeated undo removes strokes in reverse draw order; undo by a caller with no strokes is a no-op; one user's undo never removes another user's stroke. | | |

## 3. Alternatives

- **ALT-001**: Server-side video rendering (FFmpeg) to produce MP4 — rejected; adds heavy server dependency, eliminates interactivity (seek/pause), and increases infrastructure cost
- **ALT-002**: WebGL-based canvas rendering for replay — rejected; raw Canvas 2D API is sufficient for stroke replay and avoids WebGL complexity for a drawing app
- **ALT-003**: Stream events via SignalR during replay instead of REST fetch — rejected; REST with pagination is simpler, cacheable, and doesn't tie up a WebSocket for a read-only operation
- **ALT-004**: Store pre-computed replay timeline in MongoDB — rejected; timeline computation is lightweight (client-side) and storing it would create stale data on every undo or hide operation

## 4. Dependencies

- **DEP-001**: `Services/StrokeEventService.cs` — introduced by this plan (Phase 0); provides `GetEventsAsync(boardId)` returning all events ordered by Timestamp
- **DEP-002**: `Services/BoardService.cs` — provides `GetBoardByNameAsync(name)` so the history route can resolve the URL slug to `boardId` before querying the event log
- **DEP-003**: `Middleware/UserIdentityMiddleware.cs` — resolves userId from HttpOnly cookie on REST requests
- **DEP-004**: `Models/StrokeEvent.cs` — introduced by this plan (Phase 0); time-series document with `Type`, `Stroke` (containing `Points[].TimeOffset`, `Duration`), and `Timestamp` (the `timeField`)
- **DEP-005**: `Models/Point.cs` — must include `TimeOffset` (long, milliseconds since stroke start)
- **DEP-006**: `Models/Stroke.cs` — must carry a stable stroke `Id`: undo targets and broadcasts a specific stroke (`StrokeRemoved(strokeId)`), the event log uses it as the tiebreaker when two events share a `Timestamp`, and it is the natural idempotency / de-duplication key for events

## 5. Files

- **FILE-001**: `Program.cs` — Add `MapGet("/api/boards/{name}/history", ...)` endpoint registration
- **FILE-002**: `wwwroot/js/replay.js` — ReplayEngine class: history loading, timeline computation, animation loop, gap compression, seek, speed control
- **FILE-003**: `wwwroot/index.html` — Add replay button + replay overlay HTML (scrubber, controls, time display) and the undo button (Phase 5)
- **FILE-004**: `wwwroot/css/style.css` — Add replay overlay styles (positioned over canvas, control bar at bottom)
- **FILE-005**: `Tests/ReplayHistoryEndpointTests.cs` — Integration tests for the history REST endpoint
- **FILE-006**: `Models/StrokeEvent.cs` — Append-only event log document introduced by this plan (Phase 0); `Type` (Add/Remove), embedded `Stroke`, `Timestamp` (the `timeField`); stored in a native time-series collection keyed by `BoardId` (the `metaField`)
- **FILE-007**: `Services/StrokeEventService.cs` (+ `IStrokeEventService`) — Event append and query operations (Phase 0); plus `GetLastAddByUserAsync` for undo (Phase 5); registered in DI in `Program.cs`
- **FILE-008**: `Services/MongoDbContext.cs` — Add the `StrokeEvents` collection accessor (extends the MVP context) and the `StrokeEvents` indexes (Phase 0)
- **FILE-009**: `Hubs/WhiteboardHub.cs` (+ `Hubs/IWhiteboardClient.cs`) — Layer `AppendEventAsync(..., EventType.Add, ...)` onto the core `SendStroke` (Phase 0); add the `UndoLastStroke` method and the `StrokeRemoved` client event (Phase 5)
- **FILE-010**: `Tests/StrokeEventServiceTests.cs` — Integration tests for the event log (relocated from the MVP; exercises a test database)
- **FILE-011**: `Services/BoardService.cs` — Add `RemoveStrokeFromSnapshotAsync` for undo (Phase 5)
- **FILE-012**: `wwwroot/js/connection.js` — Register the `StrokeRemoved` handler (Phase 5)
- **FILE-013**: `wwwroot/js/app.js` — Remove the undone stroke from the local canvas and re-render (Phase 5)
- **FILE-014**: `Tests/UndoTests.cs` — Undo integration tests (Phase 5)

## 6. Testing

- **TEST-001**: Integration test — `GET /api/boards/{name}/history` returns paginated events with correct `totalPages` and `totalEvents` counts
- **TEST-002**: Integration test — Non-member receives 403 from history endpoint
- **TEST-005**: Unit test — `computeTimeline()` with events [t=0s, t=1s, t=15s, t=16s] compresses the 14s gap to 3s, resulting in totalDuration = 0 + 1 + 3 + 1 = 5s of playback
- **TEST-006**: Unit test — `seek(0.5)` on a timeline with 4 strokes renders exactly the first 2 strokes completely
- **TEST-007**: Manual test — Full replay with gap compression behaves as described in Phase 4 TASK-026–028
- **TEST-008**: Integration test — `StrokeEventService.AppendEventAsync` stamps a server `Timestamp` and `GetEventsAsync` returns events in chronological order (Phase 0 event log)
- **TEST-009**: Integration test — `UndoLastStroke` removes only the caller's most recent stroke from the snapshot, appends a `Remove` event, and broadcasts `StrokeRemoved(strokeId)`; repeated undo removes strokes in reverse draw order
- **TEST-010**: Integration test — Undo by a caller with no strokes is a no-op, and a caller's undo never removes another user's stroke

## 7. Risks & Assumptions

- **RISK-001**: Very large boards (10,000+ events) may cause slow initial fetch — mitigated by pagination and potential future streaming/chunked loading (the keyframe + lazy-history approach is specified in [feature-history-compaction-1.md](./feature-history-compaction-1.md))
- **RISK-002**: `requestAnimationFrame` timing inconsistencies across browsers may cause slight playback drift — mitigated by using `performance.now()` for elapsed time rather than frame counting
- **RISK-003**: Seek to arbitrary position requires re-rendering all prior strokes (O(n) for n events before seek point) — acceptable for demo scale; could be optimized with periodic snapshots for very large boards (the keyframe/segment approach is specified in [feature-history-compaction-1.md](./feature-history-compaction-1.md))
- **ASSUMPTION-001**: The `Stroke`/`Point` temporal fields (`Timestamp`, `Points[].TimeOffset`) are populated by the MVP whiteboard implementation; this plan's Phase 0 records them into `StrokeEvent` documents
- **ASSUMPTION-002**: Canvas 2D context can be cleared and re-rendered fast enough for seek operations (target: <100ms for 1000 strokes)
- **ASSUMPTION-003**: Replay is read-only — drawing tools are disabled during playback and re-enabled on stop
- **ASSUMPTION-004**: Native time-series collections cannot enforce unique indexes, so event insertion is at-least-once: exactly-once is approximated by de-duplicating on the stable stroke `Id` at read time (and on the `GetEventsSinceAsync` boundary). For throwaway demo data this read-side dedup is sufficient; no DB-level uniqueness constraint is required

## 8. Related Specifications / Further Reading

- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — parent plan containing data model, SignalR hub, and persistence layer
- [History access & moderation feature plan](./feature-history-access-moderation-1.md) — layers the membership access gate and owner-controlled HiddenRanges filtering onto the history endpoint defined here
- [Visibility moderation feature plan](./feature-visibility-moderation-1.md) — the live-snapshot counterpart of cut-off moderation; defines the HiddenRange model reused for history filtering
- [History compaction feature plan](./feature-history-compaction-1.md) — keeps large-history boards fast to load via derived keyframes over the event log this plan introduces, while retaining full history for on-demand replay
- [requestAnimationFrame documentation](https://developer.mozilla.org/en-US/docs/Web/API/window/requestAnimationFrame)
- [Canvas 2D API — Path2D and drawing](https://developer.mozilla.org/en-US/docs/Web/API/CanvasRenderingContext2D)
- [performance.now() for high-resolution timing](https://developer.mozilla.org/en-US/docs/Web/API/Performance/now)
