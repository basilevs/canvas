---
goal: Implement board management REST endpoints (board discovery listing and board deletion) for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, rest-api, management]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Implement the board management REST surface that is not required to draw collaboratively: a public-board discovery listing and an owner-only board deletion endpoint. These are administrative/management conveniences layered on top of the core whiteboard, which is joined directly by URL.

> **Scope: post-MVP enhancement.** This feature is intentionally excluded from the MVP ([feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md)). In the MVP, a board is reached by navigating to its URL (which creates it on first access) and the single `GET /api/boards/{name}/snapshot` endpoint serves its state; board discovery and deletion are not needed.

## 1. Requirements & Constraints

- **REQ-001**: Provide `GET /api/boards` returning the list of public boards with their active stroke counts, for board discovery; requires no authentication
- **REQ-002**: Provide `DELETE /api/boards/{name}` allowing the board owner to delete a board, clearing its snapshot and full event history; non-owners are rejected
- **REQ-003**: Deleting a board must also remove any derived history-compaction keyframes and segment indexes for that board, and purge any pending invites for that board, so no stale load/replay artifact or redeemable invite token survives a hard delete
- **CON-001**: Backend is ASP.NET Core 10.0 minimal APIs with built-in OpenAPI (`AddOpenApi`/`MapOpenApi`); do not add Swashbuckle (inherits parent GUD-001)
- **CON-002**: Endpoints return `sealed record` response DTOs (never domain models), accept a `CancellationToken`, use correct HTTP status codes, and carry OpenAPI metadata (inherits parent GUD-003/005/006)
- **DEP-001**: Depends on the core `IBoardService` for listing boards and clearing a board snapshot
- **DEP-002**: Depends on the core `IStrokeEventService` for clearing a board's event history
- **DEP-003**: Depends on [feature-board-administration-1.md](./feature-board-administration-1.md) for board ownership (`Board.OwnerId`) — `DELETE` is owner-only and is meaningful only once ownership exists
- **PAT-001**: Map domain models → DTOs in the endpoint/service layer

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Implement the public-board discovery listing endpoint

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Create `Dtos/BoardSummaryResponse.cs` — `sealed record BoardSummaryResponse(string Name, int ActiveStrokeCount, DateTime LastActivityAt)` (UTC `DateTime`, no timezone/offset) with `<summary>` XML doc comments. | | |
| TASK-002 | Add to `IBoardService`/`BoardService`: `ListPublicBoardsAsync(CancellationToken)` returning board name + active stroke count + last activity. | | |
| TASK-003 | Add minimal-API endpoint `GET /api/boards` — returns `200 OK` with `IReadOnlyList<BoardSummaryResponse>`; no auth; accepts and forwards `CancellationToken`; chain `.WithName("ListBoards").WithSummary(...).Produces<IReadOnlyList<BoardSummaryResponse>>(200)`. | | |

### Implementation Phase 2

- GOAL-002: Implement the owner-only board deletion endpoint

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-004 | Add to `IBoardService`/`BoardService`: `DeleteBoardAsync(boardId, requestingUserId, CancellationToken)` — verifies the requesting user is the board owner (`Board.OwnerId`, provided by the board administration plan), deletes the board document, clears its events via `IStrokeEventService`, deletes all of the board's pending invites from the `Invites` collection (so deletion leaves no orphaned, redeemable invite tokens — the `Invites` collection is owned by [feature-board-administration-1.md](./feature-board-administration-1.md)), and, if history compaction is enabled, deletes that board's derived keyframes and segment indexes. Returns a result distinguishing not-found vs. forbidden vs. deleted. | | |
| TASK-005 | Add minimal-API endpoint `DELETE /api/boards/{name}` — owner-only; returns `204 No Content` on success, `404 Not Found` if absent, `403 Forbidden` for non-owners. Normalize the route slug to its canonical `boardId` (GUD-012) — no database mapping step — resolve the requesting userId from `HttpContext.Items["UserId"]`, then call `DeleteBoardAsync(boardId, requestingUserId, CancellationToken)`; accept `CancellationToken`; use `TypedResults` with an explicit `Results<NoContent, NotFound, ForbidHttpResult>` return type; chain `.WithName("DeleteBoard").WithSummary(...).Produces(204).Produces(404).Produces(403)`. | | |

### Implementation Phase 3

- GOAL-003: Testing and request samples

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-006 | Add a request for each endpoint (`GET /api/boards`, `DELETE /api/boards/{name}`) to `canvas.http`, including an error path (delete non-existent board → 404; delete as non-owner → 403). | | |
| TASK-007 | Add MSTest integration test `Tests/BoardManagementTests.cs` — listing returns only public boards with correct counts; owner can delete a board (snapshot + events + pending invites + derived keyframes removed); non-owner delete is rejected with 403; deleting a missing board returns 404. | | |

## 3. Alternatives

- **ALT-001**: Keep these endpoints in the MVP — rejected; neither is needed to draw collaboratively (boards are reached by URL), so they are deferred to keep the MVP minimal.
- **ALT-002**: Soft-delete (archive flag) instead of hard delete — rejected for the demo; hard delete is simpler and matches "clears snapshot and all history".

## 4. Dependencies

- **DEP-001**: `Services/BoardService.cs` (`IBoardService`) — board listing and snapshot clearing
- **DEP-002**: `Services/StrokeEventService.cs` (`IStrokeEventService`) — event history clearing
- **DEP-003**: [feature-board-administration-1.md](./feature-board-administration-1.md) — `Board.OwnerId` and ownership establishment (owner-only delete)
- **DEP-004**: [feature-history-compaction-1.md](./feature-history-compaction-1.md) — derived keyframes and segment indexes that must be purged on board deletion

## 5. Files

- **FILE-001**: `Dtos/BoardSummaryResponse.cs` — Board discovery DTO
- **FILE-002**: `Services/BoardService.cs` — Add `ListPublicBoardsAsync` and `DeleteBoardAsync`
- **FILE-003**: `Program.cs` (or board endpoints class) — Register `GET /api/boards` and `DELETE /api/boards/{name}`
- **FILE-004**: `canvas.http` — Requests for list and delete endpoints
- **FILE-005**: `Tests/BoardManagementTests.cs` — Board management integration tests

## 6. Testing

- **TEST-001**: Integration test — `GET /api/boards` returns public boards with correct active stroke counts
- **TEST-002**: Integration test — Owner can delete a board; its snapshot and events are removed; subsequent snapshot fetch returns 404
- **TEST-003**: Integration test — Non-owner `DELETE` is rejected with `403`; deleting a non-existent board returns `404`

## 7. Risks & Assumptions

- **RISK-001**: Listing all public boards could grow large — mitigated by limiting/paging if needed (out of scope for the demo)
- **ASSUMPTION-001**: Ownership is provided by the board administration plan; without it, `DELETE` cannot enforce owner-only access and should remain disabled

## 8. Related Specifications / Further Reading

- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — parent plan (core board, snapshot endpoint, services)
- [Board Administration feature plan](./feature-board-administration-1.md) — provides ownership used by the delete endpoint
- [Minimal APIs overview](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/overview)
