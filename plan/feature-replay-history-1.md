---
goal: Implement video-like history replay (with inactivity compression) and single-step undo for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-15
owner: basilevs
status: 'Planned'
tags: [feature, replay, history, undo, animation, frontend, rest-api]
---

# Introduction

![Status: Planned](https://img.shields.io/badge/status-Planned-blue)

Implement a video-like replay system that allows users to watch the board's drawing history animated chronologically. The replay skips periods of inactivity (configurable threshold), animates individual strokes point-by-point using stored temporal metadata, and provides VCR-style playback controls.

Because replay is the sole consumer of the append-only event log, this plan also **introduces** that foundation: the `StrokeEvent` document, `StrokeEventService`, the `StrokeEvents` **native time-series collection**, and the layering of event-append onto the core hub `SendStroke`. The append-only event log becomes the **single source of truth** for board state. Strokes are delivered to clients via `StrokeReceived` events (not from a snapshot). Volatile metadata (user list, etc.) is updated via dedicated metadata events. Board snapshots are eliminated as a concept; in Phase 0, clients build board state purely from the event stream. (Later, [feature-history-compaction-1.md](./feature-history-compaction-1.md) will introduce keyframes as an optimization.) This plan builds upon [feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md) (the MVP) — the MVP is finalized and will not be modified.

This plan also **owns undo** (single-step removal of the caller's last stroke). Undo is the producer of `Remove` `StrokeEvent`s, so it belongs with the event log it depends on: it queries the log for the caller's most recent removable stroke (a bounded, index-backed lookup — never a full-history scan), records a `Remove` event, and broadcasts the removal. Undo (Phase 5) depends only on the Phase 0 event-log foundation and can be implemented immediately after it, independently of the replay UI (Phases 1–4).

## 1. Requirements & Constraints

- **REQ-001**: Users can trigger a replay mode that animates the board's stroke history in chronological order
- **REQ-002**: Replay uses `Stroke.Timestamp` for inter-stroke timing and `Point.TimeOffset` for intra-stroke point-by-point animation
- **REQ-003**: Inactivity gaps exceeding a configurable threshold (default 3 seconds) are compressed/skipped during playback
- **REQ-004**: The textual timestamp display (shown during replay progress) displays the actual wall-clock `Stroke.Timestamp` (ISO 8601 or localized format) of the currently animated stroke, providing historical context. The scrubber position and total replay duration use compressed time
- **REQ-005**: Playback speed is adjustable: 1x, 2x, 4x multipliers
- **REQ-006**: VCR controls: play, pause, seek (timeline scrubber), stop (exit replay)
- **REQ-007**: On its own this plan's history endpoint is **open to any identified caller** and serves the **complete unfiltered** stroke history. The route slug is only a boundary input; the handler **normalizes** `/api/boards/{name}` to its canonical `boardId` (the board's `_id`) before querying internal services — no name→id database lookup is involved. The **membership access gate** (reject non-members with `403`) and owner-controlled visibility filtering of replay (HiddenRanges cut-offs) are separate concerns layered on by [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md), which depends on the membership and HiddenRange models from the administration/moderation plans
- **REQ-008**: Replay data is fetched from a dedicated REST endpoint with fixed path-based pagination (no query parameters). Pages are ordered **oldest-first (chronological)**, so pages `1..N-1` are immutable and only the final (newest) page grows as strokes are added. Each page URL (`/api/boards/{name}/history/{pageNumber}`) is independently cacheable; a large fixed page size optimized for throughput (default 5000 events per page) is used; requesting a non-existent page returns `404`
- **REQ-009**: The history endpoint response includes a `Last-Modified` header set to the timestamp of the last (most recent) event on the returned page. Clients automatically send `If-Modified-Since` in subsequent requests; the server returns `304 Not Modified` if no new events exist on that page, enabling proxy caching. Each page is independently cached. Because pages are oldest-first, the immutable pages `1..N-1` carry a stable `Last-Modified` and 304 indefinitely; only the final page's `Last-Modified` advances
- **REQ-010**: Incremental updates (events since the client's last-known timestamp) are delivered via a dedicated SignalR hub method that clients call after fetching the full paginated history. This separates cacheable full history (REST) from incremental live sync (SignalR)
- **REQ-011**: When a client joins a board, it fetches all history pages from the REST endpoint oldest-first (cached by browser/proxies), extracts the timestamp of the last stroke on the **final** page (the most recent stroke overall), calls the hub to request incremental updates since that timestamp, then subscribes to live events going forward
- **REQ-012**: This plan introduces the append-only event log stored in a **native MongoDB time-series collection**: every accepted stroke is recorded as an `Add` `StrokeEvent` keyed on `Timestamp` (the time-series `timeField`) and `BoardId` (the `metaField`), carrying the embedded stroke's temporal metadata. Events are ordered chronologically by `Timestamp` (ties broken by the stable stroke `Id`) — there is no per-board `SequenceNumber`
- **REQ-013**: A user can undo their own most recently drawn stroke; undo records a `Remove` `StrokeEvent` (the authoritative log entry) and broadcasts the removal so every connected client updates. Undo only affects the caller's own strokes and is a no-op when the caller has no remaining strokes
- **CON-001**: Frontend is vanilla JavaScript on HTML5 Canvas (no framework)
- **CON-002**: Backend is ASP.NET Core 10.0 with MongoDB Atlas
- **CON-003**: `StrokeEvent` documents carry `Timestamp` (the `timeField`), `Type`, `Stroke.Duration`, and `Stroke.Points[].TimeOffset`, and are persisted in a **native time-series collection** (`metaField` = `BoardId`, `granularity` = `seconds`). The `Stroke`/`Point` temporal fields are reused from the MVP data model, but the event-log document and its collection are introduced by this plan (Phase 0)
- **GUD-001**: Deriving the current board picture by folding the log (Add/Remove → visible strokes, dedup by stroke `Id`) is the **client's** concern — it already downloads the paginated history. The **server never materializes the full log in memory**: server operations are bounded — paginated reads (endpoint), since-timestamp reads (incremental sync), and a single-document index-backed lookup for undo's last removable stroke

## 2. Implementation Steps

### Implementation Phase 0

- GOAL-000: Introduce the append-only event log foundation (relocated from the MVP) that records every stroke for later replay

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Create `Models/StrokeEvent.cs` — append-only event document: `Id` (`ObjectId`), `BoardId` (string — the time-series `metaField`), `Type` (enum `EventType { Add, Remove }`), `Stroke` (embedded `Stroke`, reusing the MVP model with its `UserId`, `Id`, and `Duration`/`Points[].TimeOffset` temporal metadata), `Timestamp` (UTC `DateTime` — the time-series `timeField`, native BSON date). Stored in the `StrokeEvents` native time-series collection (TASK-004). No `SequenceNumber` — ordering is by `Timestamp`, with the embedded stroke's stable `Id` as the tiebreaker. `Add` events are produced by `SendStroke` (Phase 0); `Remove` events are produced by undo (Phase 5). | | |
| TASK-002 | Add a `StrokeEvents` typed collection accessor to `Services/MongoDbContext.cs` (and `IMongoDbContext`), extending the MVP context which exposes only `Boards` and `Users`. | | |
| TASK-003 | Create `Services/StrokeEventService.cs` (and `IStrokeEventService`) as the persistence/query layer with: `AppendEventAsync(boardId, EventType type, Stroke stroke)` (stamps the server `Timestamp` and inserts the measurement; **the insert-time de-duplication authority** — since the time-series collection supports no unique index (TASK-004), it de-duplicates `Add` events at the application level by the stroke's client-generated `Id`, skipping the insert when an `Add` for that `Id` already exists on the board, so a stroke re-sent on reconnect is never double-logged; this is the role the MVP located in the snapshot `$push`, now moved to the authoritative log), `GetEventsPageAsync(boardId, pageNumber, pageSize)` (oldest-first page slice plus total counts, for the REST endpoint), `GetEventsSinceAsync(boardId, sinceTimestamp)` (incremental cursor for the hub), and `GetLastRemovableStrokeByUserAsync(boardId, userId)` (**bounded, index-backed** lookup for undo — the caller's most recent `Add` whose stroke has no later `Remove`, or `null`; resolved by a `Timestamp`-descending query limited to one result, never by scanning the whole log into memory). Register `IStrokeEventService` in DI in `Program.cs`. | | |
| TASK-004 | Create the `StrokeEvents` collection as a **native MongoDB time-series collection**: `db.createCollection("StrokeEvents", { timeseries: { timeField: "Timestamp", metaField: "BoardId", granularity: "seconds" } })`. This auto-provisions the internal clustered `{ metaField, timeField }` index that serves per-board chronological reads (paginated endpoint + incremental cursor). Add a secondary index on the embedded stroke `Id` (to resolve a `Remove`'s target and for read-side de-duplication) and a secondary index on `(Stroke.UserId, Timestamp)` to back the bounded last-removable-stroke lookup for undo. Note: time-series collections **do not support unique indexes**, so per-board ordering relies on the `timeField` rather than a DB-enforced unique sequence. | | |
| TASK-005 | Layer the append-only event log onto the core hub `SendStroke` (MVP TASK-026): call `StrokeEventService.AppendEventAsync(boardId, EventType.Add, stroke)` (the durable record and the idempotency/de-duplication authority — TASK-003), then broadcast `StrokeReceived(stroke)` via the `StrokeReceived` client method (see GUD-009, use the typed proxy, never `SendAsync`). The event log is the single source of truth; strokes are delivered to clients via events (not from a snapshot). Log append comes first so history is durable before clients receive the stroke broadcast. This is the only producer of `Add` events in scope. | | |
| TASK-006 | Add MSTest integration tests for the event log in `Tests/StrokeEventServiceTests.cs` (relocated from the MVP): verify `AppendEventAsync` stamps a server `Timestamp`, that `GetEventsPageAsync` returns events oldest-first with correct total counts, that a re-sent `Add` with a duplicate stroke `Id` is not double-logged, and that `GetLastRemovableStrokeByUserAsync` returns the caller's most recent non-removed `Add` (and `null` when none remain). Exercises a MongoDB test database. | | |

### Implementation Phase 1

- GOAL-001: Implement the REST API endpoint that serves replay history with path-based pagination and HTTP caching

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-007 | Add REST endpoint `GET /api/boards/{name}/history/{pageNumber}` in `Program.cs` with fixed path-based pagination (no query parameters). Returns paginated history in **chronological (oldest-first) order** with a large fixed page size optimized for throughput (default 5000 events per page). Route: `app.MapGet("/api/boards/{name}/history/{pageNumber}", ...)` | | |
| TASK-008 | Implement endpoint handler: normalize the URL slug `{name}` to its canonical `boardId` and fetch the board, return `404` if absent. Validate `pageNumber` ≥ 1; return `404` if the page exceeds total pages. On its own this plan applies **no** membership gate and needs **no** caller identity — any identified caller is served (REQ-007); the `IsMemberAsync` `403` gate and caller identity resolution are layered on later by [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md). | | |
| TASK-009 | Return the page of `StrokeEvents` for the board in **chronological (oldest-first) order** via `GetEventsPageAsync` (TASK-003). The JSON response is `{ "events": [...], "pageNumber": 1, "totalEvents": N, "totalPages": M }`. Each event includes: `id`, `type` (Add/Remove), `stroke` (with id, userId, points, color, width, duration), `timestamp`. | | |
| TASK-010 | Add a `Last-Modified` response header to the history endpoint, set to the timestamp of the last (most recent) event on the returned page. Because pages are oldest-first, pages `1..N-1` are immutable and carry a stable `Last-Modified`; only the final page advances. This enables proxy caching of each page independently. | | |
| TASK-011 | Implement conditional GET handling via the `If-Modified-Since` request header: compare the request header timestamp with the page's `Last-Modified` timestamp (the most recent event on that page). If the page has not been modified since the client's timestamp, return `304 Not Modified` (empty body, cached representation reused). Otherwise, return `200` with full page content. This allows browsers and proxies to serve cached pages without re-fetching when no new events exist on that page. | | |
| TASK-012 | Add SignalR hub method `GetHistoryUpdates(boardName, sinceTimestamp)` to `Hubs/WhiteboardHub.cs`: normalize the slug to `boardId`, return all `StrokeEvent`s (both `Add` and `Remove`) with `Timestamp > sinceTimestamp` via `GetEventsSinceAsync`, ordered chronologically. Send this incremental batch to the calling client only (not broadcast). This lets clients sync events added since they fetched the last page from the REST endpoint. | | |
| TASK-013 | Implement join-time history population in `wwwroot/js/app.js` (REQ-011): when a user joins, fetch all history pages from `GET /api/boards/{boardName}/history/1`, `/history/2`, … oldest-first (auto-paginate until a `404`, each page cached independently by the browser), extract the `Timestamp` of the last stroke on the final page, connect to the Hub, call `GetHistoryUpdates(boardName, lastTimestamp)` to fetch any events added after the last page was generated (deduping by stroke `Id` at the boundary), render both full history and incremental updates to the canvas, then subscribe to live events going forward. | | |
| TASK-014 | Add integration test `Tests/ReplayHistoryEndpointTests.cs`: verify pagination metadata (`totalEvents`, `totalPages`), oldest-first ordering, that each page has a `Last-Modified` header equal to the timestamp of that page's final (most recent) stroke, that a conditional GET with `If-Modified-Since` on an unchanged page returns `304`, that requesting a non-existent page returns `404`, and that all pages combined contain the complete unfiltered history. (The membership-`403` gate and HiddenRanges filtering tests live in [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md).) | | |

### Implementation Phase 2

- GOAL-002: Implement the frontend replay engine with temporal animation and gap compression

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-015 | Create `wwwroot/js/replay.js` — export `ReplayEngine` class with constructor accepting a canvas 2D context reference and configuration `{ gapThresholdMs: 3000, speed: 1 }`. | | |
| TASK-016 | Implement `ReplayEngine.loadHistory(boardName)` — fetch all pages from `GET /api/boards/{boardName}/history/1`, `/history/2`, … oldest-first (auto-paginate until a `404` is encountered), store events in `this.events` array sorted by `timestamp` (ties broken by stroke `id`). | | |
| TASK-017 | Implement `ReplayEngine.computeTimeline()` — precompute a timeline array: for each event, calculate `realTimeGapMs` (time since previous event's timestamp). If `realTimeGapMs > gapThresholdMs`, compress to `gapThresholdMs`. Store `playbackOffsetMs` (cumulative adjusted time) for each event. Compute `totalDurationMs` for the entire replay. | | |
| TASK-018 | Implement `ReplayEngine.play()` — start a `requestAnimationFrame` loop. Track `elapsedMs` using `performance.now()` scaled by `this.speed`. For each frame, determine which events should have started based on `elapsedMs` vs `playbackOffsetMs`. For active strokes, animate points using `Point.TimeOffset` scaled by speed. | | |
| TASK-019 | Implement intra-stroke animation: when rendering a stroke in progress, draw points incrementally — only points where `(elapsedMs - strokeStartMs) * speed >= point.TimeOffset` are rendered. Use `ctx.beginPath()` / `ctx.lineTo()` for smooth progressive drawing. | | |
| TASK-020 | Implement `ReplayEngine.pause()` — pause the `requestAnimationFrame` loop, store current `elapsedMs`. | | |
| TASK-021 | Implement `ReplayEngine.resume()` — resume from stored `elapsedMs`. | | |
| TASK-022 | Implement `ReplayEngine.seek(positionRatio)` — accept 0.0–1.0, compute target `elapsedMs = positionRatio * totalDurationMs`, clear canvas, re-render all events that complete before target time (instant render), then set playback position for animation to continue from there. | | |
| TASK-023 | Implement `ReplayEngine.setSpeed(multiplier)` — accept 1, 2, or 4. Adjust elapsed time tracking so playback continues smoothly from current position at new speed. | | |
| TASK-024 | Implement `ReplayEngine.stop()` — stop animation loop, clear canvas, fire `onStop` callback to notify UI. | | |
| TASK-025 | Handle `Remove` events in replay: when a Remove event's playback time is reached, erase the referenced stroke from the canvas (re-render all prior visible strokes excluding removed ones). | | |

### Implementation Phase 3

- GOAL-003: Implement the replay UI controls and integrate with the whiteboard page

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-026 | Add replay controls to `wwwroot/index.html`: (1) In toolbar: add replay button (`<button id="btn-replay">▶ Replay</button>` — **always visible**), plus replay-mode-only controls (play/pause button, stop button, speed selector, timestamp display — **hidden by default, shown during replay**). (2) Under canvas: add `<div id="replay-controls">` containing the timeline scrubber (`<input type="range" id="replay-scrubber" min="0" max="1" step="0.001">` — **hidden by default, shown during replay**). | | |
| TASK-027 | Wire replay button click (in toolbar): instantiate `ReplayEngine(canvasCtx, config)`, call `loadHistory(boardName)`, show the replay-controls div and replay-mode toolbar buttons (set `display: block`/`inline-block`), disable drawing tools, call `play()`. | | |
| TASK-028 | Wire replay-mode controls during replay: play/pause button toggles `pause()`/`resume()`, stop calls `stop()` and hides the replay-controls div and replay-mode toolbar buttons + re-enables drawing tools + resyncs board state by refetching all events from the history endpoint, scrubber `input` event calls `seek(value)`, speed selector `change` calls `setSpeed(value)`. | | |
| TASK-029 | Wire `ReplayEngine.onProgress` callback: update scrubber position based on compressed playback time (`currentCompressedMs / totalCompressedDurationMs`), and display the wall-clock `Timestamp` of the currently animated stroke formatted as ISO 8601 or localized date/time string. | | |
| TASK-030 | Wire `ReplayEngine.onStop` callback: hide the replay-controls div and replay-mode toolbar buttons (set `display: none`), restore drawing tools, resync board state by refetching all events from the history endpoint to rebuild the current canvas. | | |
| TASK-031 | Add CSS in `wwwroot/css/style.css` for the replay controls: the replay-controls div positioned directly under the canvas with full width, styled scrubber input. The replay-controls div and replay-mode toolbar controls hidden by default (`display: none`); the replay button always visible. | | |

### Implementation Phase 4

- GOAL-004: Testing and validation of replay functionality

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-032 | Unit test `Tests/ReplayEngineTests.js` (or inline in an HTML test page): verify `computeTimeline()` compresses gaps > 3s, verify `totalDurationMs` is correct, verify `seek` renders the correct state at the midpoint. | | |
| TASK-033 | Manual test — Draw 5 strokes with 10+ second pauses between some. Click replay. Verify inactivity gaps are compressed, strokes animate point-by-point, total replay duration is much shorter than real elapsed time. Verify the textual timestamp display shows the actual wall-clock time of each stroke (not compressed). | | |
| TASK-034 | Manual test — During replay, click pause, move scrubber to 50%, resume. Verify canvas shows correct state at midpoint and continues animating from there. | | |
| TASK-035 | Manual test — Change speed from 1x to 4x during playback. Verify animation speeds up smoothly without jumping. | | |

### Implementation Phase 5

- GOAL-005: Implement single-step undo (remove the caller's last stroke) on top of the Phase 0 event log. Depends only on Phase 0 and is independent of the replay UI (Phases 1–4).

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-036 | Add the hub method `UndoLastStroke(boardName)` to `Hubs/WhiteboardHub.cs` (`Hub<IWhiteboardClient>`) — the **single owner** of undo's log-append and broadcast: resolve userId from `Context.GetHttpContext().Items["UserId"]` (server-assigned); normalize the route slug to its canonical `boardId` once; find the caller's last removable stroke via `StrokeEventService.GetLastRemovableStrokeByUserAsync(boardId, userId)` (TASK-003 — a bounded index-backed query, not a full-history fold). If none, no-op. Otherwise append a `Remove` `StrokeEvent` for that stroke first (`AppendEventAsync(boardId, EventType.Remove, stroke)` — the authoritative log entry), then broadcast `StrokeRemoved(strokeId)` to the group, consistent with the log-first ordering of `SendStroke` (TASK-005). Per GUD-009, extend `IWhiteboardClient` with `StrokeRemoved(strokeId)` and broadcast via the typed proxy, never `SendAsync`. Note: member-access enforcement on private boards is layered onto this method by [feature-board-administration-1.md](./feature-board-administration-1.md), consistent with the guard on `SendStroke`. | | |
| TASK-037 | Add an undo control to the `wwwroot/index.html` toolbar (`<button id="btn-undo">↶ Undo</button>`) wired to call `UndoLastStroke(currentBoardName)`. | | |
| TASK-038 | In `wwwroot/js/connection.js` register a `StrokeRemoved(strokeId)` handler; in `wwwroot/js/app.js` handle it by removing the stroke from the local canvas state and re-rendering. | | |
| TASK-039 | Add integration test `Tests/UndoTests.cs` — `UndoLastStroke` removes only the caller's most recent stroke, appends a `Remove` event, and broadcasts `StrokeRemoved`; repeated undo removes strokes in reverse draw order; undo by a caller with no strokes is a no-op; one user's undo never removes another user's stroke. | | |

## 3. Alternatives

- **ALT-001**: Server-side video rendering (FFmpeg) to produce MP4 — rejected; adds heavy server dependency, eliminates interactivity (seek/pause), and increases infrastructure cost
- **ALT-002**: WebGL-based canvas rendering for replay — rejected; raw Canvas 2D API is sufficient for stroke replay and avoids WebGL complexity for a drawing app
- **ALT-003**: Stream all events (history + incremental) via SignalR during join instead of REST history fetch — rejected; REST with fixed oldest-first pagination enables proxy caching of immutable history; incremental updates via SignalR hub method keeps concerns separated and scales better for large boards
- **ALT-004**: Store pre-computed replay timeline in MongoDB — rejected; timeline computation is lightweight (client-side) and storing it would create stale data on every undo or hide operation
- **ALT-005**: Newest-first pagination — rejected; it shifts page boundaries on every new stroke (breaking immutable-page caching) and would force the join flow to read the last page for the oldest, not newest, timestamp. Oldest-first keeps pages `1..N-1` immutable and the final page the only mutable one
- **ALT-006**: Materialize/fold the entire event log in memory server-side (e.g. to derive current state or to find undo's target) — rejected; it does not scale with history size. The server uses bounded, index-backed queries instead (paginated reads, since-timestamp reads, a single-document last-removable-stroke lookup); folding the full log into a picture happens only on the client, which already streams the paginated history

## 4. Dependencies

- **DEP-001**: `Services/StrokeEventService.cs` (+ `IStrokeEventService`) — introduced by this plan (Phase 0); persistence/query layer: `AppendEventAsync`, `GetEventsPageAsync`, `GetEventsSinceAsync`, and the bounded `GetLastRemovableStrokeByUserAsync` for undo
- **DEP-002**: `Services/BoardService.cs` — the history route normalizes the URL slug to its canonical `boardId` (the board `_id`) at the boundary; `BoardService.GetBoardAsync(boardId)` confirms the board exists before querying the event log (no name→id lookup needed)
- **DEP-003**: `Middleware/UserIdentityMiddleware.cs` (MVP) — resolves userId from the HttpOnly cookie / connection context. Consumed only by the undo hub method (`UndoLastStroke`, Phase 5) to match the caller against `Stroke.UserId`; the REST history endpoint needs **no** caller identity (REQ-007)
- **DEP-004**: `Models/StrokeEvent.cs` — introduced by this plan (Phase 0); time-series document with `Type`, `Stroke` (containing `UserId`, `Id`, `Points[].TimeOffset`, `Duration`), and `Timestamp` (the `timeField`)
- **DEP-005**: `Models/Point.cs` — must include `TimeOffset` (`ushort`, milliseconds since stroke start; ≤ ~65 s, no sub-millisecond precision — serialized by the driver as BSON Int32, half the storage of the former `long`, which matters as every point is persisted in the event log)
- **DEP-006**: `Models/Stroke.cs` — must carry a stable stroke `Id` and the drawing `UserId`: undo targets and broadcasts a specific stroke (`StrokeRemoved(strokeId)`) and matches ownership by `UserId`, the event log uses `Id` as the tiebreaker when two events share a `Timestamp`, and `Id` is the natural idempotency / de-duplication key for events

## 5. Files

- **FILE-001**: `Program.cs` — Add `MapGet("/api/boards/{name}/history/{pageNumber}", ...)` endpoint registration (path-based pagination) and register `IStrokeEventService` in DI
- **FILE-002**: `wwwroot/js/replay.js` — ReplayEngine class: history loading, timeline computation, animation loop, gap compression, seek, speed control
- **FILE-003**: `wwwroot/index.html` — Add replay button + replay controls (under-canvas scrubber, play/pause/stop, speed selector, timestamp display) and the undo button (Phase 5)
- **FILE-004**: `wwwroot/css/style.css` — Add replay control styles (scrubber div under the canvas, replay-mode controls hidden by default)
- **FILE-005**: `Tests/ReplayHistoryEndpointTests.cs` — Integration tests for the history REST endpoint
- **FILE-006**: `Models/StrokeEvent.cs` — Append-only event log document introduced by this plan (Phase 0); `Type` (Add/Remove), embedded `Stroke`, `Timestamp` (the `timeField`); stored in a native time-series collection keyed by `BoardId` (the `metaField`)
- **FILE-007**: `Services/StrokeEventService.cs` (+ `IStrokeEventService`) — Event append and query operations including the bounded `GetLastRemovableStrokeByUserAsync` for undo (Phase 0); registered in DI in `Program.cs`
- **FILE-008**: `Services/MongoDbContext.cs` — Add the `StrokeEvents` collection accessor (extends the MVP context) and the `StrokeEvents` indexes (Phase 0)
- **FILE-009**: `Hubs/WhiteboardHub.cs` (+ `Hubs/IWhiteboardClient.cs`) — Layer `AppendEventAsync(..., EventType.Add, ...)` onto the core `SendStroke` (Phase 0); add `GetHistoryUpdates` (Phase 1); add the `UndoLastStroke` method and the `StrokeRemoved` client event (Phase 5)
- **FILE-010**: `Tests/StrokeEventServiceTests.cs` — Integration tests for the event log (relocated from the MVP; exercises a test database)
- **FILE-011**: `wwwroot/js/connection.js` — Register the `StrokeRemoved` handler (Phase 5)
- **FILE-012**: `wwwroot/js/app.js` — Join-time history population (Phase 1); remove the undone stroke from the local canvas and re-render (Phase 5)
- **FILE-013**: `Tests/UndoTests.cs` — Undo integration tests (Phase 5)

## 6. Testing

- **TEST-001**: Integration test — `GET /api/boards/{name}/history/{pageNumber}` returns paginated events oldest-first with correct `totalPages` and `totalEvents` counts
- **TEST-002**: Integration test — each page carries a `Last-Modified` header equal to the timestamp of that page's final (most recent) stroke; requesting a non-existent page returns `404`; all pages combined contain the complete unfiltered history
- **TEST-003**: Integration test — a conditional GET with `If-Modified-Since` on an unchanged page returns `304 Not Modified`
- **TEST-004**: Unit test — `computeTimeline()` with events [t=0s, t=1s, t=15s, t=16s] compresses the 14s gap to 3s, resulting in totalDuration = 0 + 1 + 3 + 1 = 5s of playback
- **TEST-005**: Unit test — `seek(0.5)` on a timeline with 4 strokes renders exactly the first 2 strokes completely
- **TEST-006**: Integration test — `StrokeEventService`: `AppendEventAsync` stamps a server `Timestamp`, `GetEventsPageAsync` returns events oldest-first with correct counts, a re-sent `Add` with a duplicate stroke `Id` is not double-logged, and `GetLastRemovableStrokeByUserAsync` returns the caller's most recent non-removed `Add` (Phase 0 event log)
- **TEST-007**: Integration test — `UndoLastStroke` removes only the caller's most recent stroke, appends a `Remove` event, and broadcasts `StrokeRemoved(strokeId)`; repeated undo removes strokes in reverse draw order
- **TEST-008**: Integration test — Undo by a caller with no strokes is a no-op, and a caller's undo never removes another user's stroke
- **TEST-009**: Manual test — Full replay with gap compression behaves as described in Phase 4 (TASK-033–035)

## 7. Risks & Assumptions

- **RISK-001**: Very large boards (10,000+ events) may cause slow initial fetch — mitigated by pagination and potential future streaming/chunked loading (the keyframe + lazy-history approach is specified in [feature-history-compaction-1.md](./feature-history-compaction-1.md))
- **RISK-002**: `requestAnimationFrame` timing inconsistencies across browsers may cause slight playback drift — mitigated by using `performance.now()` for elapsed time rather than frame counting
- **RISK-003**: Seek to arbitrary position requires re-rendering all prior strokes (O(n) for n events before seek point) — acceptable for demo scale; could be optimized with periodic snapshots for very large boards (the keyframe/segment approach is specified in [feature-history-compaction-1.md](./feature-history-compaction-1.md))
- **ASSUMPTION-001**: The `Stroke`/`Point` temporal fields (`Timestamp`, `Points[].TimeOffset`) are populated by the MVP whiteboard implementation; this plan's Phase 0 records them into `StrokeEvent` documents
- **ASSUMPTION-002**: Canvas 2D context can be cleared and re-rendered fast enough for seek operations (target: <100ms for 1000 strokes)
- **ASSUMPTION-003**: Replay is read-only — drawing tools are disabled during playback and re-enabled on stop
- **ASSUMPTION-004**: Native time-series collections cannot enforce unique indexes, so event insertion is at-least-once: exactly-once is approximated by insert-time dedup in `StrokeEventService` and read-side dedup by the stable stroke `Id` on the client (and on the `GetEventsSinceAsync` boundary). For throwaway demo data this is sufficient; no DB-level uniqueness constraint is required
- **ASSUMPTION-005**: Clients build board state from the event stream (no snapshots). Stop replay resyncs by refetching all events from the history endpoint. Later, keyframes ([feature-history-compaction-1.md](./feature-history-compaction-1.md)) will optimize large-board resync

## 8. Related Specifications / Further Reading

- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — parent plan containing data model, SignalR hub, and persistence layer
- [History access & moderation feature plan](./feature-history-access-moderation-1.md) — layers the membership access gate and owner-controlled HiddenRanges filtering onto the history endpoint defined here
- [Visibility moderation feature plan](./feature-visibility-moderation-1.md) — the live-snapshot counterpart of cut-off moderation; defines the HiddenRange model reused for history filtering
- [History compaction feature plan](./feature-history-compaction-1.md) — keeps large-history boards fast to load via derived keyframes over the event log this plan introduces, while retaining full history for on-demand replay
- [requestAnimationFrame documentation](https://developer.mozilla.org/en-US/docs/Web/API/window/requestAnimationFrame)
- [Canvas 2D API — Path2D and drawing](https://developer.mozilla.org/en-US/docs/Web/API/CanvasRenderingContext2D)
- [performance.now() for high-resolution timing](https://developer.mozilla.org/en-US/docs/Web/API/Performance/now)
