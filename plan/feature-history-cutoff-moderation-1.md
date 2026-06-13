---
goal: Apply owner-controlled visibility cut-offs (HiddenRanges) to replay history for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, moderation, history, replay, access-control, rest-api]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Apply the board owner's visibility cut-offs (HiddenRanges) to the **replay history** so that, when watching history playback, non-owner members do not see contributions the owner has hidden, while the owner can still replay the full unfiltered history. This is a thin bridge that sits at the intersection of two independent post-MVP features: it consumes the **event log + history endpoint** from the replay/history plan and the **HiddenRange model + ownership** from the visibility-moderation plan. It owns no new data model — it only layers serve-time filtering onto the history read path, mirroring how [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) filters the live snapshot.

> **Scope: post-MVP enhancement requiring two other features.** This plan is meaningless on its own; it is only applicable once both [feature-replay-history-1.md](./feature-replay-history-1.md) (history endpoint + event log) and [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) (HiddenRanges + owner-only full view) are in place. It was extracted into its own plan precisely because it depends on **both** administration/moderation and history, and belongs to neither alone.

## 1. Requirements & Constraints

- **REQ-001**: When a non-owner member replays history, events whose stroke is hidden by a HiddenRange (the stroke's `UserId` matches a HiddenRange entry and the stroke's `Timestamp` ≤ that range's `HiddenBefore`) are excluded from the served history
- **REQ-002**: The board owner can replay the **complete unfiltered** history regardless of HiddenRanges
- **REQ-003**: Restoring a member's contributions (removing the HiddenRange) makes them reappear in subsequent replays — no historical data is mutated, so this is automatic
- **CON-001**: Filtering is applied at serve-time on the history read path; the `StrokeEvent` log is never mutated by a hide/restore operation (consistent with PAT-001 of the visibility-moderation plan)
- **CON-002**: Ownership and the hidden/visible decision are resolved server-side from the server-assigned identity; client-supplied identity is never trusted
- **CON-003**: Backend is ASP.NET Core 10.0 with MongoDB Atlas
- **DEP-001**: Depends on the history endpoint `GET /api/boards/{name}/history` and `StrokeEventService.GetEventsAsync(boardName)` introduced by [feature-replay-history-1.md](./feature-replay-history-1.md)
- **DEP-002**: Depends on `BoardService.GetHiddenRangesAsync(boardName)`, the `HiddenRange` model, and the board ownership check introduced by [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md)
- **DEP-003**: Depends on `Middleware/UserIdentityMiddleware.cs` for userId resolution on REST requests
- **PAT-001**: Serve-time filtering — history visibility is a read-path projection over the immutable event log, not a write-path mutation

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Layer HiddenRanges filtering onto the history endpoint with owner bypass

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | In the `GET /api/boards/{name}/history` handler (defined in [feature-replay-history-1.md](./feature-replay-history-1.md), TASK-002/003), after resolving the caller's userId and verifying membership, determine whether the caller is the board owner. | | |
| TASK-002 | If the caller is the owner, return the events unchanged (full unfiltered history). | | |
| TASK-003 | If the caller is a non-owner member, fetch `BoardService.GetHiddenRangesAsync(boardName)` and exclude every event where `event.Stroke.UserId` matches a HiddenRange entry AND `event.Timestamp <= hiddenRange.HiddenBefore`. Apply the filter before pagination so page counts reflect the visible set. | | |
| TASK-004 | Ensure the filter is composed as a thin wrapper/projection over the replay plan's event retrieval, so that replay-history remains usable standalone (unfiltered) when this plan is not applied. | | |

### Implementation Phase 2

- GOAL-002: Testing and validation of history cut-off filtering

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-005 | Add integration test `Tests/HistoryCutoffModerationTests.cs` — a non-owner member receives history with a hidden member's pre-cut-off events excluded; the owner receives the complete unfiltered history. | | |
| TASK-006 | Add integration test — after the owner restores a member's contributions, a subsequent non-owner history request includes the previously hidden events (no data was mutated). | | |
| TASK-007 | Add integration test — filtering is applied before pagination: `totalEvents`/`totalPages` for a non-owner reflect only the visible events. | | |
| TASK-008 | Manual test — As a non-owner on a board with hidden contributions, trigger replay and verify the hidden user's strokes do NOT appear during playback. As the owner, trigger replay and verify they DO appear. | | |

## 3. Alternatives

- **ALT-001**: Bake the filtering directly into the replay-history plan — rejected; it would force replay-history to depend on the moderation/HiddenRange model (and transitively on administration/ownership), coupling two otherwise-independent features. Extracting the bridge keeps each base plan usable on its own.
- **ALT-002**: Filter on the client (drop hidden events in the ReplayEngine) — rejected; a non-owner client must never receive hidden strokes, so filtering must happen server-side before serving.
- **ALT-003**: Persist a separate per-member filtered event log — rejected; write amplification and a second source of truth. Serve-time filtering over the single immutable log is simpler and always consistent.

## 4. Dependencies

- **DEP-001**: `Program.cs` — the `GET /api/boards/{name}/history` endpoint handler (from [feature-replay-history-1.md](./feature-replay-history-1.md)) is the injection point for the filter
- **DEP-002**: `Services/StrokeEventService.cs` — `GetEventsAsync(boardName)` provides the unfiltered events to project over (from [feature-replay-history-1.md](./feature-replay-history-1.md))
- **DEP-003**: `Services/BoardService.cs` — `GetHiddenRangesAsync(boardName)` and the ownership check (from [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md))
- **DEP-004**: `Models/HiddenRange.cs` — `UserId` + `HiddenBefore` cut-off (from [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md))
- **DEP-005**: `Models/StrokeEvent.cs` — carries the embedded `Stroke` (`UserId`, `Timestamp`) the filter keys off (from [feature-replay-history-1.md](./feature-replay-history-1.md))

## 5. Files

- **FILE-001**: `Program.cs` — Extend the `GET /api/boards/{name}/history` handler with the owner-bypass HiddenRanges filter (Phase 1)
- **FILE-002**: `Tests/HistoryCutoffModerationTests.cs` — Integration tests for owner-vs-non-owner history filtering and restore behavior

## 6. Testing

- **TEST-001**: Integration test — Non-owner member receives history with a hidden member's events before the cut-off excluded
- **TEST-002**: Integration test — Owner receives the complete unfiltered history regardless of HiddenRanges
- **TEST-003**: Integration test — After restore, a non-owner's subsequent history request includes the previously hidden events
- **TEST-004**: Integration test — Pagination metadata (`totalEvents`/`totalPages`) for a non-owner reflects only the visible events
- **TEST-005**: Manual test — Replay as non-owner omits hidden strokes; replay as owner includes them

## 7. Risks & Assumptions

- **RISK-001**: Applying the filter after fetching all events then paginating in memory could be costly for very large boards — acceptable at demo scale; a future optimization could push the cut-off predicate into the MongoDB query
- **ASSUMPTION-001**: Each `StrokeEvent` carries an accurate server-side `Stroke.UserId` and `Stroke.Timestamp`, so the cut-off filter is reliable (same assumption as live-snapshot moderation)
- **ASSUMPTION-002**: Board ownership is permanent (per the parent plan), so moderation authority over history does not transfer

## 8. Related Specifications / Further Reading

- [Replay & History View feature plan](./feature-replay-history-1.md) — defines the event log and the `GET /api/boards/{name}/history` endpoint this plan filters
- [Visibility moderation feature plan](./feature-visibility-moderation-1.md) — defines the HiddenRange model and owner-only full view; this plan is its history-side counterpart
- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — the MVP both features build upon
- [ASP.NET Core minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis)
