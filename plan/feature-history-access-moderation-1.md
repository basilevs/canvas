---
goal: Govern the replay-history read path — membership access control plus owner-controlled visibility cut-offs (HiddenRanges) — for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, moderation, history, replay, access-control, rest-api]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Govern **who may read the replay history and what they see**. This plan owns the two access concerns layered onto the replay-history read path: (1) the **membership access gate** — only members of a board may read its history (non-members are rejected with `403`), and (2) **owner-controlled visibility moderation** — applying the board owner's HiddenRanges cut-offs so that, when watching history playback, non-owner members do not see contributions the owner has hidden, while the owner can still replay the full unfiltered history. It is the bridge that sits at the intersection of three otherwise-independent post-MVP features: it consumes the **event log + history endpoint** from the replay/history plan, the **membership model** from the board-administration plan, and the **HiddenRange model + ownership** from the visibility-moderation plan. It owns no new data model — it only layers access control and serve-time filtering onto the history read path, mirroring how [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) gates and filters the live snapshot. Keeping both the membership gate and the HiddenRanges filter here lets [feature-replay-history-1.md](./feature-replay-history-1.md) build standalone on the MVP (its history endpoint is open to any identified caller until this plan layers access control on).

> **Scope: post-MVP enhancement requiring other features.** This plan is meaningless on its own; it is only applicable once [feature-replay-history-1.md](./feature-replay-history-1.md) (history endpoint + event log), [feature-board-administration-1.md](./feature-board-administration-1.md) (membership / `IsMemberAsync`), and [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) (HiddenRanges + owner-only full view) are in place. It was extracted into its own plan precisely because it depends on **all three** and belongs to none alone.

## 1. Requirements & Constraints

- **REQ-001**: When a non-owner member replays history, events whose stroke is hidden by a HiddenRange (the stroke's `UserId` matches a HiddenRange entry and the stroke's `Timestamp` ≤ that range's `HiddenBefore`) are excluded from the served history
- **REQ-002**: The board owner can replay the **complete unfiltered** history regardless of HiddenRanges
- **REQ-003**: Restoring a member's contributions (removing the HiddenRange) makes them reappear in subsequent replays — no historical data is mutated, so this is automatic
- **REQ-004**: The history endpoint enforces **membership access control**: the caller's server-assigned identity must be a member of the board; non-members are rejected with `403 Forbidden` (for a private board) consistent with how board-administration gates `SendStroke` and the snapshot endpoint. This membership gate is owned by this plan, not by [feature-replay-history-1.md](./feature-replay-history-1.md) (whose endpoint is open to any identified caller standalone), because the membership model it relies on is introduced by [feature-board-administration-1.md](./feature-board-administration-1.md)
- **CON-001**: Filtering is applied at serve-time on the history read path; the `StrokeEvent` log is never mutated by a hide/restore operation (consistent with PAT-001 of the visibility-moderation plan)
- **CON-002**: Ownership, membership, and the hidden/visible decision are resolved server-side from the server-assigned identity; client-supplied identity is never trusted
- **CON-003**: Backend is ASP.NET Core 10.0 with MongoDB Atlas
- **DEP-001**: Depends on the history endpoint `GET /api/boards/{name}/history` and `StrokeEventService.GetEventsAsync(boardId)` introduced by [feature-replay-history-1.md](./feature-replay-history-1.md)
- **DEP-002**: Depends on the boundary normalization of the route slug to its canonical `boardId` (GUD-012, no database lookup), `BoardService.GetHiddenRangesAsync(boardId)`, the `HiddenRange` model, and the board ownership check introduced by [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md)
- **DEP-003**: Depends on `Middleware/UserIdentityMiddleware.cs` for userId resolution on REST requests
- **DEP-004**: Depends on `BoardService.IsMemberAsync(boardId, userId)` and the membership model introduced by [feature-board-administration-1.md](./feature-board-administration-1.md) for the membership access gate (REQ-004)
- **PAT-001**: Serve-time filtering — history visibility is a read-path projection over the immutable event log, not a write-path mutation

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Layer membership access control and HiddenRanges filtering onto the history endpoint with owner bypass

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | In the `GET /api/boards/{name}/history` handler (defined in [feature-replay-history-1.md](./feature-replay-history-1.md), TASK-002/003), normalize the route slug to its canonical `boardId` (GUD-012, no database mapping step), then resolve the caller's userId and enforce the **membership access gate** (REQ-004): verify `BoardService.IsMemberAsync(boardId, userId)` and return `403 Forbidden` for non-members of a private board (public boards remain open, consistent with the snapshot endpoint). Then determine whether the caller is the board owner. | | |
| TASK-002 | If the caller is the owner, return the events unchanged (full unfiltered history). | | |
| TASK-003 | If the caller is a non-owner member, fetch `BoardService.GetHiddenRangesAsync(boardId)` and exclude every event where `event.Stroke.UserId` matches a HiddenRange entry AND `event.Timestamp <= hiddenRange.HiddenBefore`. Apply the filter before pagination so page counts reflect the visible set. | | |
| TASK-004 | Ensure the membership gate and the HiddenRanges filter are composed as a thin wrapper/projection over the replay plan's event retrieval, so that replay-history remains usable standalone (open + unfiltered) when this plan is not applied. | | |

### Implementation Phase 2

- GOAL-002: Testing and validation of history access control and cut-off filtering

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-005 | Add integration test `Tests/HistoryAccessModerationTests.cs` — a non-owner member receives history with a hidden member's pre-cut-off events excluded; the owner receives the complete unfiltered history. | | |
| TASK-006 | Add integration test — after the owner restores a member's contributions, a subsequent non-owner history request includes the previously hidden events (no data was mutated). | | |
| TASK-007 | Add integration test — filtering is applied before pagination: `totalEvents`/`totalPages` for a non-owner reflect only the visible events. | | |
| TASK-009 | Add integration test — the **membership gate** (REQ-004): a non-member calling `GET /api/boards/{name}/history` on a private board is rejected with `403`; a member and the owner are served; a public board remains open. | | |
| TASK-008 | Manual test — As a non-owner on a board with hidden contributions, trigger replay and verify the hidden user's strokes do NOT appear during playback. As the owner, trigger replay and verify they DO appear. | | |

## 3. Alternatives

- **ALT-001**: Bake the membership gate and HiddenRanges filtering directly into the replay-history plan — rejected; it would force replay-history to depend on the membership model (board-administration) and the moderation/HiddenRange model (visibility-moderation), coupling three otherwise-independent features. Extracting this bridge keeps each base plan usable on its own (replay-history's history endpoint stays open + unfiltered standalone).
- **ALT-002**: Filter on the client (drop hidden events in the ReplayEngine) — rejected; a non-owner client must never receive hidden strokes, so filtering must happen server-side before serving.
- **ALT-003**: Persist a separate per-member filtered event log — rejected; write amplification and a second source of truth. Serve-time filtering over the single immutable log is simpler and always consistent.

## 4. Dependencies

- **DEP-001**: `Program.cs` — the `GET /api/boards/{name}/history` endpoint handler (from [feature-replay-history-1.md](./feature-replay-history-1.md)) is the injection point for the filter
- **DEP-002**: `Services/StrokeEventService.cs` — `GetEventsAsync(boardId)` provides the unfiltered events to project over (from [feature-replay-history-1.md](./feature-replay-history-1.md))
- **DEP-003**: `Services/BoardService.cs` — `GetHiddenRangesAsync(boardId)` and the ownership check (from [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md)); the route slug is normalized to its canonical `boardId` at the boundary (GUD-012), so no name→id lookup helper is needed
- **DEP-004**: `Models/HiddenRange.cs` — `UserId` + `HiddenBefore` cut-off (from [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md))
- **DEP-005**: `Models/StrokeEvent.cs` — carries the embedded `Stroke` (`UserId`, `Timestamp`) the filter keys off (from [feature-replay-history-1.md](./feature-replay-history-1.md))
- **DEP-006**: `Services/BoardService.cs` — `IsMemberAsync(boardId, userId)` and the membership model (from [feature-board-administration-1.md](./feature-board-administration-1.md)) backing the membership access gate (REQ-004)

## 5. Files

- **FILE-001**: `Program.cs` — Extend the `GET /api/boards/{name}/history` handler with the membership access gate and the owner-bypass HiddenRanges filter (Phase 1)
- **FILE-002**: `Tests/HistoryAccessModerationTests.cs` — Integration tests for the membership gate, owner-vs-non-owner history filtering, and restore behavior

## 6. Testing

- **TEST-001**: Integration test — Non-owner member receives history with a hidden member's events before the cut-off excluded
- **TEST-002**: Integration test — Owner receives the complete unfiltered history regardless of HiddenRanges
- **TEST-003**: Integration test — After restore, a non-owner's subsequent history request includes the previously hidden events
- **TEST-004**: Integration test — Pagination metadata (`totalEvents`/`totalPages`) for a non-owner reflects only the visible events
- **TEST-006**: Integration test — Membership gate (REQ-004): a non-member is rejected with `403` on a private board; a member and the owner are served; a public board stays open
- **TEST-005**: Manual test — Replay as non-owner omits hidden strokes; replay as owner includes them

## 7. Risks & Assumptions

- **RISK-001**: Applying the filter after fetching all events then paginating in memory could be costly for very large boards — acceptable at demo scale; a future optimization could push the cut-off predicate into the MongoDB query
- **ASSUMPTION-001**: Each `StrokeEvent` carries an accurate server-side `Stroke.UserId` and `Stroke.Timestamp`, so the cut-off filter is reliable (same assumption as live-snapshot moderation)
- **ASSUMPTION-002**: Board ownership is permanent (per the parent plan), so moderation authority over history does not transfer

## 8. Related Specifications / Further Reading

- [Replay & History View feature plan](./feature-replay-history-1.md) — defines the event log and the `GET /api/boards/{name}/history` endpoint this plan gates and filters
- [Visibility moderation feature plan](./feature-visibility-moderation-1.md) — defines the HiddenRange model and owner-only full view; this plan is its history-side counterpart
- [Board administration feature plan](./feature-board-administration-1.md) — defines the membership model (`IsMemberAsync`) backing this plan's history-endpoint access gate
- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — the MVP these features build upon
- [ASP.NET Core minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis)
