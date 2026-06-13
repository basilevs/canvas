---
goal: Implement board administration and owner controls (invites, public/private visibility, member removal) for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'Planned'
tags: [feature, access-control, administration, signalr, mongodb]
---

# Introduction

![Status: Planned](https://img.shields.io/badge/status-Planned-blue)

Implement the board owner's administration capabilities layered on top of the core membership model: generating single-use invite links, delegating invite creation to members, toggling board visibility between public and private, and removing members (with immediate disconnection). These controls govern who may access a board and how new members are admitted. Core ownership establishment (first accessor becomes owner) and the membership primitives (`AddMemberAsync`, `IsMemberAsync`, `GetOrCreateBoardAsync`) are provided by the parent whiteboard plan.

## 1. Requirements & Constraints

- **REQ-001**: Board owner can generate single-use invite links; each link is valid for exactly one user to join, then expires
- **REQ-002**: Board owner can enable or disable the ability for other members to create invite links (default: disabled)
- **REQ-003**: Board owner can toggle board visibility between public (anyone with the URL can join) and private (invite-only) at any time
- **REQ-004**: Board owner can remove existing members from the board; removed members lose access immediately and their active connections are disconnected
- **CON-001**: Only the board owner may invoke `SetPublic`, `SetMembersCanInvite`, and `RemoveMember`; the server enforces ownership and never trusts client-supplied identity
- **CON-002**: The owner cannot be removed from their own board
- **CON-003**: Invite tokens are cryptographically random, URL-safe, and single-use (atomic redemption)
- **CON-004**: Backend is ASP.NET Core 10.0 with MongoDB Atlas; real-time via SignalR
- **DEP-001**: Depends on `Models/Board.cs` providing `OwnerId`, `IsPublic`, `MembersCanInvite`, and `Members`
- **DEP-002**: Depends on core `BoardService` membership primitives: `GetOrCreateBoardAsync`, `AddMemberAsync`, `IsMemberAsync`
- **DEP-003**: Depends on `Hubs/WhiteboardHub.cs` for server-assigned identity and the core `JoinBoard` flow
- **DEP-004**: Depends on `Services/MongoDbContext.cs` exposing the `Invites` collection
- **PAT-001**: Atomic single-use redemption via MongoDB `findOneAndUpdate` (compare-and-set on `IsUsed`)

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Implement the invite data model, invite service, and board administration service methods

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Create `Models/Invite.cs` — properties: `Id` (ObjectId), `BoardId` (string), `Token` (string, unique random URL-safe token), `CreatedBy` (string, UUID), `CreatedAt` (DateTime), `UsedBy` (string?, UUID of user who redeemed), `UsedAt` (DateTime?), `IsUsed` (bool, default false). Single-use: once redeemed, cannot be used again. | | |
| TASK-002 | Create `Services/InviteService.cs` — methods: `CreateInviteAsync(boardId, createdByUserId)` (generates cryptographic random token via `RandomNumberGenerator`), `RedeemInviteAsync(token, userId)` (atomic `findOneAndUpdate`: mark used + return boardId, fail if already used), `GetPendingInvitesAsync(boardId)`. | | |
| TASK-003 | Add administration methods to `Services/BoardService.cs`: `SetPublicAsync(boardId, isPublic)`, `SetMembersCanInviteAsync(boardId, canInvite)`, `RemoveMemberAsync(boardId, userId)` (no-op/guard if userId is the owner). | | |
| TASK-004 | Add MongoDB indexes (in the index-creation routine): unique index on `Invites` for `{ Token: 1 }`, index on `Invites` for `{ BoardId: 1, IsUsed: 1 }`. | | |

### Implementation Phase 2

- GOAL-002: Implement SignalR hub methods for invites, visibility toggling, and member removal with owner enforcement

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-005 | Add hub methods to `Hubs/WhiteboardHub.cs`: `JoinBoardWithInvite(token)`, `CreateInvite(boardName)`, `SetPublic(boardName, isPublic)`, `SetMembersCanInvite(boardName, canInvite)`, `RemoveMember(boardName, targetUserId)` — all resolve userId from `Context.GetHttpContext().Items["UserId"]`. | | |
| TASK-006 | Implement `JoinBoardWithInvite(token)`: call `InviteService.RedeemInviteAsync(token, userId)` — if successful, add user as member (`AddMemberAsync`), join the SignalR group, send snapshot + member list. If token invalid/used, return `InvalidInvite` error. | | |
| TASK-007 | Implement `CreateInvite(boardName)`: verify caller is owner OR (`MembersCanInvite` is true AND caller is member). Call `InviteService.CreateInviteAsync`, return invite URL to caller. | | |
| TASK-008 | Implement `SetPublic(boardName, isPublic)`: verify caller is owner, call `BoardService.SetPublicAsync`. Broadcast `BoardSettingsChanged` to group. | | |
| TASK-009 | Implement `SetMembersCanInvite(boardName, canInvite)`: verify caller is owner, call `BoardService.SetMembersCanInviteAsync`. Broadcast `BoardSettingsChanged` to group. | | |
| TASK-010 | Implement `RemoveMember(boardName, targetUserId)`: verify caller is owner AND target is not owner. Call `BoardService.RemoveMemberAsync`. Find target's connections via the connection-tracking dictionary, force-disconnect them from the group, send `Kicked` event to target's connections. Broadcast `UserRemoved(targetUserId)` to group. | | |
| TASK-011 | Add a connection-tracking dictionary (userId → set of connection ids) maintained in `OnConnectedAsync`/`OnDisconnectedAsync` (or a shared singleton) so `RemoveMember` can locate and disconnect a target's connections. | | |
| TASK-012 | Ensure the core `JoinBoard` flow enforces private-board access: if board `IsPublic` is false, reject non-members with `AccessDenied`; if public, auto-add as member. (This enforcement is the consumer of REQ-003 and lives in the core hub.) | | |

### Implementation Phase 3

- GOAL-003: Implement the owner administration panel and invite landing flow on the frontend

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-013 | In `wwwroot/js/admin.js` (owner panel), wire buttons: `SetPublic` toggle, `SetMembersCanInvite` toggle, `CreateInvite` (copy generated link to clipboard), and per-member `RemoveMember` buttons. Show/hide the panel based on ownership. | | |
| TASK-014 | In `wwwroot/js/admin.js`, handle `BoardSettingsChanged` to update the public/private and members-can-invite UI state. | | |
| TASK-015 | In `wwwroot/index.html`, add the invite-token landing UI (detect `?invite=TOKEN` or `/invite/TOKEN`, present a join prompt). | | |
| TASK-016 | In `wwwroot/js/connection.js`, register handlers for `BoardSettingsChanged`, `UserRemoved(targetUserId)`, `Kicked`, `AccessDenied`, and `InvalidInvite` events, and expose `joinBoardWithInvite(token)`. | | |
| TASK-017 | In `wwwroot/js/app.js`, detect an invite token in the URL and route to `joinBoardWithInvite`; handle `Kicked` (show message, disable canvas) and `UserRemoved` (update members list). | | |

### Implementation Phase 4

- GOAL-004: Testing and validation of board administration

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-018 | Add integration test `Tests/InviteFlowTests.cs` — verify invite creation (owner-only by default), redemption adds member, double-redemption fails with `InvalidInvite`, member invite succeeds when `MembersCanInvite` enabled and fails when disabled. | | |
| TASK-019 | Add integration test `Tests/MemberRemovalTests.cs` — verify owner can remove member, removed member's connection receives `Kicked`, removed user cannot rejoin a private board, non-owner cannot remove others. | | |
| TASK-020 | Add integration test in `Tests/BoardAdminTests.cs` — non-owner cannot call `SetPublic`, `SetMembersCanInvite`, or `RemoveMember` (rejected with error); owner cannot be removed. | | |
| TASK-021 | Add integration test — non-member joining a private board receives `AccessDenied`; joining with a valid invite succeeds and adds membership; after toggling to public, any user may join. | | |
| TASK-022 | Manual test — Generate an invite link as owner, open it in an incognito window, verify the new user gains access to a private board; redeem the same link a second time and verify it is rejected. | | |
| TASK-023 | Manual test — As owner, remove a member who has a second browser tab open; verify their tab receives `Kicked` and they cannot rejoin the private board. | | |

## 3. Alternatives

- **ALT-001**: Multi-use invite links with a usage counter — rejected; single-use links give the owner precise control over who joins and naturally expire, matching REQ-001
- **ALT-002**: Time-expiring invite tokens (TTL) instead of single-use — rejected for the demo; single-use is simpler to reason about, though a TTL index could be added later
- **ALT-003**: Role-based access control (multiple admin roles) — rejected as overengineering; a single permanent owner plus an optional "members can invite" flag is sufficient for the demo
- **ALT-004**: Soft-removing members (flag) rather than removing the membership entry — rejected; immediate removal plus forced disconnection best satisfies "lose access immediately"

## 4. Dependencies

- **DEP-001**: `Models/Board.cs` — `OwnerId`, `IsPublic`, `MembersCanInvite`, `Members`
- **DEP-002**: `Services/BoardService.cs` — core membership primitives (`GetOrCreateBoardAsync`, `AddMemberAsync`, `IsMemberAsync`)
- **DEP-003**: `Services/MongoDbContext.cs` — `Invites` collection accessor
- **DEP-004**: `Hubs/WhiteboardHub.cs` — server-assigned identity, core `JoinBoard`, group broadcasting
- **DEP-005**: `wwwroot/js/admin.js`, `connection.js`, `app.js`, `index.html` — owner panel and invite landing UI

## 5. Files

- **FILE-001**: `Models/Invite.cs` — Single-use invite token document
- **FILE-002**: `Services/InviteService.cs` — Invite creation, atomic redemption, pending invite queries
- **FILE-003**: `Services/BoardService.cs` — Add `SetPublicAsync`, `SetMembersCanInviteAsync`, `RemoveMemberAsync`
- **FILE-004**: `Hubs/WhiteboardHub.cs` — Add `JoinBoardWithInvite`, `CreateInvite`, `SetPublic`, `SetMembersCanInvite`, `RemoveMember`, and connection tracking
- **FILE-005**: `wwwroot/js/admin.js` — Owner settings panel (invites, public/private, member removal)
- **FILE-006**: `wwwroot/js/connection.js` — Register `BoardSettingsChanged`/`UserRemoved`/`Kicked`/`AccessDenied`/`InvalidInvite` handlers
- **FILE-007**: `wwwroot/js/app.js` — Invite URL detection and kick handling
- **FILE-008**: `wwwroot/index.html` — Invite token landing UI
- **FILE-009**: `Tests/InviteFlowTests.cs` — Invite system integration tests
- **FILE-010**: `Tests/MemberRemovalTests.cs` — Member removal integration tests
- **FILE-011**: `Tests/BoardAdminTests.cs` — Owner-only control enforcement tests

## 6. Testing

- **TEST-001**: Integration test — Invite token can be redeemed exactly once; second redemption attempt fails with `InvalidInvite`
- **TEST-002**: Integration test — Owner can remove member; removed member's connection receives `Kicked`; removed member cannot rejoin private board
- **TEST-003**: Integration test — Non-owner cannot call `SetPublic`, `SetMembersCanInvite`, or `RemoveMember` (rejected with error)
- **TEST-004**: Integration test — When `MembersCanInvite` is true, non-owner member can create invites; when false, only owner can
- **TEST-005**: Integration test — Non-member joining a private board receives `AccessDenied`; joining with a valid invite succeeds and adds membership
- **TEST-006**: Integration test — Owner cannot be removed from their own board
- **TEST-007**: Manual test — Generate invite link as owner, open in incognito window, verify new user gains access to private board

## 7. Risks & Assumptions

- **RISK-001**: A removed member with an in-flight stroke could race the removal — mitigated by membership checks on every `SendStroke` and forced disconnection
- **RISK-002**: Connection-tracking dictionary may grow stale on ungraceful disconnects — mitigated by cleanup in `OnDisconnectedAsync` and the dictionary being best-effort
- **RISK-003**: Invite token guessing — mitigated by cryptographically random, sufficiently long tokens (e.g., 128-bit) and a unique index
- **ASSUMPTION-001**: Board ownership is permanent and cannot be transferred (per the parent plan)
- **ASSUMPTION-002**: Single-server deployment, so the in-memory connection-tracking dictionary is authoritative (no SignalR backplane needed for demo scale)

## 8. Related Specifications / Further Reading

- [Main whiteboard implementation plan](./feature-collaborative-whiteboard-1.md) — parent plan containing the data model, core membership, SignalR hub, and persistence layer
- [Visibility Moderation feature plan](./feature-visibility-moderation-1.md) — owner moderation of member contributions (separate owner-control concern)
- [ASP.NET Core SignalR groups and connection management](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups)
- [MongoDB findOneAndUpdate (atomic operations)](https://www.mongodb.com/docs/manual/reference/method/db.collection.findOneAndUpdate/)
