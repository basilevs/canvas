---
goal: Implement owner-controlled visibility moderation (hide/restore member contributions) for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, moderation, access-control, signalr, mongodb]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Implement a moderation capability that lets a board owner hide all contributions of any member up to a chosen cut-off moment, restore them later, and optionally view the full unfiltered history themselves. Hidden strokes remain in storage but become invisible to all non-owner members. Filtering is applied at serve-time based on the caller's identity; the stored snapshot and event log are never mutated by a hide operation.

> **Scope: post-MVP enhancement.** This feature is intentionally excluded from the MVP ([feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md)). In the MVP, the snapshot and history endpoints serve all strokes unfiltered; this plan adds owner-controlled visibility filtering on top.

## 1. Requirements & Constraints

- **REQ-001**: Board owner can hide all contributions of any member up to the current moment; hidden strokes become invisible to all other members but remain in storage
- **REQ-002**: Board owner can restore previously hidden contributions of a member, making them visible to all members again
- **REQ-003**: Board owner can toggle a personal "show hidden" view to see all strokes including hidden ones (other members always see the filtered view)
- **REQ-004**: The **snapshot** endpoint is filtered by the owner's visibility settings (HiddenRanges) for non-owner members; only the owner can access the unfiltered view. (Applying the same HiddenRanges to the replay **history** endpoint is handled by [feature-history-access-moderation-1.md](./feature-history-access-moderation-1.md).)
- **CON-001**: Hiding never deletes data — the stored `ActiveStrokes` snapshot and StrokeEvent log always contain all strokes; filtering is applied at serve-time
- **CON-002**: Only the board owner may invoke hide/restore/show-hidden operations; the server enforces ownership and never trusts client-supplied identity
- **CON-003**: Backend is ASP.NET Core 10.0 with MongoDB Atlas; real-time via SignalR
- **DEP-001**: Depends on `Models/Board.cs` providing the `HiddenRanges` collection
- **DEP-002**: Depends on `Services/BoardService.cs` snapshot accessors and ownership checks
- **DEP-003**: Depends on `Hubs/WhiteboardHub.cs` for server-assigned identity (`Context.GetHttpContext().Items["UserId"]`)
- **DEP-004**: Depends on `Models/Stroke.cs` carrying `UserId` and `Timestamp` for filtering
- **PAT-001**: Serve-time filtering — visibility is a read-path projection, not a write-path mutation

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Implement the HiddenRange data model and BoardService filtering logic

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Create `Models/HiddenRange.cs` — properties: `UserId` (string, whose strokes are hidden), `HiddenBefore` (DateTime, all strokes by this user with Timestamp ≤ this value are hidden from non-owner members). Adding a range hides; removing it restores. | | |
| TASK-002 | Add `HiddenRanges` (List<HiddenRange>, default empty) property to `Models/Board.cs` document model. | | |
| TASK-003 | Implement `BoardService.HideContributionsAsync(boardId, targetUserId, hiddenBefore)` — upsert a HiddenRange for `targetUserId` with the given cut-off (replace existing entry for that user if present). | | |
| TASK-004 | Implement `BoardService.RestoreContributionsAsync(boardId, targetUserId)` — remove the HiddenRange entry for `targetUserId`; return the list of strokes that become visible again (for broadcast). | | |
| TASK-005 | Implement `BoardService.GetHiddenRangesAsync(boardId)` — return the current list of HiddenRange entries for a board. | | |
| TASK-006 | Implement `BoardService.GetSnapshotAsync(boardName, requestingUserId)` — return active strokes filtered by HiddenRanges for non-owner callers (exclude strokes where `stroke.UserId` matches a HiddenRange and `stroke.Timestamp <= hiddenRange.HiddenBefore`). | | |
| TASK-007 | Implement `BoardService.GetFullSnapshotAsync(boardName)` — return unfiltered active strokes; callers must verify ownership before invoking. | | |

### Implementation Phase 2

- GOAL-002: Implement SignalR hub methods for hide/restore/show-hidden with owner enforcement

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-008 | Add hub methods to `Hubs/WhiteboardHub.cs`: `HideContributions(boardName, targetUserId)`, `RestoreContributions(boardName, targetUserId)`, `ToggleShowHidden(boardName, showHidden)` — all resolve userId from `Context.GetHttpContext().Items["UserId"]`. Per the parent plan's strongly typed hub convention (GUD-009), extend `IWhiteboardClient` with the server→client events this plan introduces (`StrokesHidden`, `StrokesRestored`) and broadcast them via the typed proxy (e.g. `Clients.Group(name).StrokesHidden(...)`), never `SendAsync`. | | |
| TASK-009 | Implement `HideContributions(boardName, targetUserId)`: verify caller is owner (reject otherwise). Call `BoardService.HideContributionsAsync(boardId, targetUserId, DateTime.UtcNow)`. Broadcast `StrokesHidden(targetUserId, hiddenBefore)` to non-owner members. Owner's view unchanged. | | |
| TASK-010 | Implement `RestoreContributions(boardName, targetUserId)`: verify caller is owner. Call `BoardService.RestoreContributionsAsync(boardId, targetUserId)`. Broadcast `StrokesRestored(targetUserId, restoredStrokes[])` to non-owner members. | | |
| TASK-011 | Implement `ToggleShowHidden(boardName, showHidden)`: verify caller is owner. If showHidden=true, send full unfiltered snapshot to caller only (via `GetFullSnapshotAsync`). If false, send filtered snapshot. Personal view toggle — no broadcast. | | |
| TASK-012 | Ensure `JoinBoard` snapshot delivery uses `GetSnapshotAsync(boardName, callerUserId)` so newly joining non-owner members receive the filtered snapshot, and owners receive the full snapshot. | | |

### Implementation Phase 3

- GOAL-003: Implement frontend controls and event handling for visibility moderation

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-013 | In `wwwroot/js/admin.js` (owner panel), add per-member "Hide contributions" / "Restore contributions" buttons in the members list, wired to `HideContributions`/`RestoreContributions`. | | |
| TASK-014 | In `wwwroot/js/admin.js`, add a "Show hidden" checkbox wired to `ToggleShowHidden(boardName, checked)`; on snapshot response, re-render the canvas with the returned strokes. | | |
| TASK-015 | In `wwwroot/js/connection.js`, register handlers for `StrokesHidden(targetUserId, hiddenBefore)` and `StrokesRestored(targetUserId, restoredStrokes[])` events. | | |
| TASK-016 | In `wwwroot/js/app.js`, handle `StrokesHidden` by removing the affected strokes from the local canvas (strokes by `targetUserId` with `Timestamp <= hiddenBefore`) and re-rendering; handle `StrokesRestored` by adding the restored strokes back and re-rendering. | | |

### Implementation Phase 4

- GOAL-004: Testing and validation of visibility moderation

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-017 | Add unit test in `Tests/BoardServiceVisibilityTests.cs` — `GetSnapshotAsync` excludes strokes matching HiddenRanges for non-owner callers and includes them for owner callers. | | |
| TASK-018 | Add integration test `Tests/VisibilityModerationTests.cs` — owner hides member contributions; non-owner members receive `StrokesHidden` and affected strokes disappear; owner still sees them via `ToggleShowHidden(true)`. | | |
| TASK-019 | Add integration test — owner restores hidden contributions; non-owner members receive `StrokesRestored` and re-render the restored strokes. | | |
| TASK-020 | Add integration test — new member joining after a hide sees the filtered snapshot; owner joining sees the full snapshot. | | |
| TASK-021 | Add integration test — non-owner cannot call `HideContributions`, `RestoreContributions`, or `ToggleShowHidden` (rejected with error). | | |
| TASK-022 | Manual test — As owner, hide a member's contributions, toggle "show hidden" on/off, verify the canvas updates correctly for both owner and a second (non-owner) browser tab. | | |

## 3. Alternatives

- **ALT-001**: Soft-delete hidden strokes by setting a flag on each stroke document — rejected; mutating per-stroke state on every hide/restore is expensive and complicates undo/replay. A single HiddenRange per user is O(1) to apply and trivially reversible
- **ALT-002**: Maintain a separate filtered snapshot per member — rejected; storage and write amplification grow with membership. Serve-time filtering keeps a single source of truth
- **ALT-003**: Hide individual strokes by id rather than a per-user time cut-off — rejected; the requirement is "hide all contributions up to a moment", which a cut-off timestamp models exactly and compactly

## 4. Dependencies

- **DEP-001**: `Models/Board.cs` — must include the `HiddenRanges` collection
- **DEP-002**: `Models/Stroke.cs` — must carry `UserId` and `Timestamp` for filtering
- **DEP-003**: `Services/BoardService.cs` — snapshot accessors and ownership checks
- **DEP-004**: `Hubs/WhiteboardHub.cs` — server-assigned identity and group broadcasting
- **DEP-005**: `wwwroot/js/admin.js`, `connection.js`, `app.js` — owner panel and client event handling

## 5. Files

- **FILE-001**: `Models/HiddenRange.cs` — Value object defining per-user visibility cut-off (UserId + HiddenBefore timestamp)
- **FILE-002**: `Models/Board.cs` — Add `HiddenRanges` collection property
- **FILE-003**: `Services/BoardService.cs` — Add `HideContributionsAsync`, `RestoreContributionsAsync`, `GetHiddenRangesAsync`, and filtered/full snapshot accessors
- **FILE-004**: `Hubs/WhiteboardHub.cs` (+ `Hubs/IWhiteboardClient.cs`) — Add `HideContributions`, `RestoreContributions`, `ToggleShowHidden` methods; extend `IWhiteboardClient` with this plan's client events (GUD-009)
- **FILE-005**: `wwwroot/js/admin.js` — Hide/restore buttons and "show hidden" checkbox
- **FILE-006**: `wwwroot/js/connection.js` — Register `StrokesHidden`/`StrokesRestored` handlers
- **FILE-007**: `wwwroot/js/app.js` — Apply hide/restore to the local canvas
- **FILE-008**: `Tests/BoardServiceVisibilityTests.cs` — Unit tests for serve-time filtering
- **FILE-009**: `Tests/VisibilityModerationTests.cs` — Integration tests for hide/restore/show-hidden flows

## 6. Testing

- **TEST-001**: Unit test — `BoardService.GetSnapshotAsync` filters out strokes matching HiddenRanges for non-owner callers and includes them for the owner
- **TEST-002**: Integration test — Owner hides member contributions; non-owner members receive `StrokesHidden` and affected strokes disappear from their view; owner still sees them with `ToggleShowHidden(true)`
- **TEST-003**: Integration test — Owner restores hidden contributions; non-owner members receive `StrokesRestored` with the restored strokes and re-render them
- **TEST-004**: Integration test — New member joining after a hide sees the filtered snapshot (hidden strokes excluded); owner joining sees the full snapshot
- **TEST-005**: Integration test — Non-owner cannot call `HideContributions`, `RestoreContributions`, or `ToggleShowHidden` (rejected with error)
- **TEST-006**: Manual test — As owner, hide a member's contributions, toggle "show hidden" on/off, verify the canvas updates correctly across two browser tabs

## 7. Risks & Assumptions

- **RISK-001**: A non-owner client could attempt to reconstruct hidden strokes from prior in-memory state — mitigated by the server never sending hidden strokes to non-owners and `StrokesHidden` instructing clients to drop them; a malicious client that already received strokes before the hide is out of scope for a demo
- **RISK-002**: Concurrent hide and draw operations may race — mitigated by serve-time filtering being idempotent and applied on every read
- **ASSUMPTION-001**: Each stroke carries an accurate server-side `Timestamp` and `UserId` so the cut-off filter is reliable
- **ASSUMPTION-002**: Board ownership is permanent (per the parent plan), so moderation authority does not transfer

## 8. Related Specifications / Further Reading

- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — parent plan containing the data model, SignalR hub, and persistence layer
- [History access & moderation feature plan](./feature-history-access-moderation-1.md) — applies these HiddenRanges (and the membership gate) to filter replay history (the history-side counterpart of this live-snapshot moderation)
- [Replay & History View feature plan](./feature-replay-history-1.md) — defines the event log and history endpoint that cut-off moderation filters
- [ASP.NET Core SignalR groups](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups)
