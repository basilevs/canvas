---
goal: Implement board administration and owner controls (invites, public/private visibility, member removal) for the collaborative whiteboard
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'On Hold'
tags: [feature, access-control, administration, signalr, mongodb]
---

# Introduction

![Status: On Hold](https://img.shields.io/badge/status-On_Hold-orange)

Implement the board owner's administration capabilities, including the underlying ownership and membership model that the MVP intentionally omits: establishing board ownership (first accessor becomes owner), persisted membership (`Board.Members`/`BoardMember`), generating single-use invite links, delegating invite creation to members, toggling board visibility between public and private, removing members (with immediate disconnection), and forcing a member's visible pseudonym. These controls govern who owns a board, who may access it, how new members are admitted, and how members are presented. The parent whiteboard MVP plan provides only the core `GetOrCreateBoardAsync` (board creation), server-assigned identity, and self-chosen display names; this plan adds ownership, membership, and the primitives (`AddMemberAsync`, `IsMemberAsync`, `GetMembersWithNamesAsync`) on top.

> **Scope: post-MVP enhancement.** This feature is intentionally excluded from the MVP ([feature-collaborative-whiteboard-1.md](./feature-collaborative-whiteboard-1.md)). In the MVP, boards are public (anyone with the URL can join); this plan adds the private/invite-only access model on top.

## 1. Requirements & Constraints

- **REQ-001**: Board owner can generate single-use invite links; each link is valid for exactly one user to join, then expires
- **REQ-002**: Board owner can enable or disable the ability for other members to create invite links (default: disabled)
- **REQ-003**: Board owner can toggle board visibility between public (anyone with the URL can join) and private (invite-only) at any time
- **REQ-004**: Board owner can remove existing members from the board; removed members lose access immediately and their active connections are disconnected
- **REQ-005**: Board owner can override (force) the visible pseudonym of any member on their board; the forced name is what all other members see regardless of the user's self-chosen name. The owner can clear the override, reverting to the member's self-chosen name.
- **REQ-006**: Once boards can be private or members can be removed, the core stroke-mutating operations (`SendStroke`, and `UndoLastStroke` once introduced by [feature-replay-history-1.md](./feature-replay-history-1.md)) and the REST snapshot endpoint must verify the caller is a member and reject non-members; this membership verification is owned by this plan and layered onto the core operations
- **CON-001**: Only the board owner may invoke `SetPublic`, `SetMembersCanInvite`, `RemoveMember`, `SetForcedName`, and `ClearForcedName`; the server enforces ownership and never trusts client-supplied identity
- **CON-002**: The owner cannot be removed from their own board
- **CON-003**: Invite tokens are cryptographically random, URL-safe, and single-use (atomic redemption)
- **CON-004**: Backend is ASP.NET Core 10.0 with MongoDB Atlas; real-time via SignalR
- **DEP-001**: Adds the ownership/membership fields to `Models/Board.cs` — `OwnerId`, `IsPublic`, `MembersCanInvite`, and `Members` (with the `BoardMember` value object); the MVP `Board` model has none of these
- **DEP-002**: Adds the membership primitives to `Services/BoardService.cs` (`AddMemberAsync`, `IsMemberAsync`, `GetMembersWithNamesAsync`) and owner establishment to `GetOrCreateBoardAsync`; depends only on the MVP's core `GetOrCreateBoardAsync` (board creation) as the extension point
- **DEP-003**: Depends on `Hubs/WhiteboardHub.cs` for server-assigned identity and the core `JoinBoard` flow
- **DEP-004**: Depends on `Services/MongoDbContext.cs` exposing the `Invites` collection
- **DEP-005**: Depends on `Services/UserProfileService.cs` for resolving a member's self-chosen `DisplayName` when an override is cleared
- **PAT-001**: Atomic single-use redemption via MongoDB `findOneAndUpdate` (compare-and-set on `IsUsed`)
- **PAT-002**: Display-name resolution is `BoardMember.ForcedName ?? UserProfile.DisplayName`

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Implement the ownership/membership model foundation, invite data model, invite service, and board administration service methods

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-035 | Introduce the ownership/membership model that the MVP omits: create `Models/BoardMember.cs` (`UserId`; `ForcedName` is added in Phase 5 / TASK-024); add `OwnerId` (string), `IsPublic` (bool, default true), `MembersCanInvite` (bool, default false), and `Members` (List<BoardMember>) to `Models/Board.cs`; extend `GetOrCreateBoardAsync` so the first caller is recorded as owner and member; add membership primitives to `Services/BoardService.cs` — `AddMemberAsync(boardId, userId)`, `IsMemberAsync(boardId, userId)`, `GetMembersWithNamesAsync(boardId)` (members with their `UserProfile.DisplayName`); and update the core `JoinBoard` hub flow to add the caller as a member and source the "members" list from `Members` instead of live presence. Defense-in-depth against session fixation (parent RISK-005): because ownership grants privilege, flag the owning user for identity rotation when they are first recorded as owner and have `UserIdentityMiddleware` reissue (rotate) their `uid` on the next HTTP response — a cookie cannot be set over the established SignalR WebSocket. | | |
| TASK-001 | Create `Models/Invite.cs` — properties: `Id` (ObjectId), `BoardId` (string), `Token` (string, unique random URL-safe token), `CreatedBy` (string, UUID), `CreatedAt` (DateTime), `UsedBy` (string?, UUID of user who redeemed), `UsedAt` (DateTime?), `IsUsed` (bool, default false). Single-use: once redeemed, cannot be used again. | | |
| TASK-002 | Create `Services/InviteService.cs` — methods: `CreateInviteAsync(boardId, createdByUserId)` (generates cryptographic random token via `RandomNumberGenerator`), `RedeemInviteAsync(token, userId)` (atomic `findOneAndUpdate`: mark used + return boardId, fail if already used), `GetPendingInvitesAsync(boardId)`. | | |
| TASK-003 | Add administration methods to `Services/BoardService.cs`: `SetPublicAsync(boardId, isPublic)`, `SetMembersCanInviteAsync(boardId, canInvite)`, `RemoveMemberAsync(boardId, userId)` (no-op/guard if userId is the owner). | | |
| TASK-004 | Add MongoDB indexes (in the index-creation routine): unique index on `Invites` for `{ Token: 1 }`, index on `Invites` for `{ BoardId: 1, IsUsed: 1 }`. | | |

### Implementation Phase 2

- GOAL-002: Implement SignalR hub methods for invites, visibility toggling, and member removal with owner enforcement, plus member-access enforcement on the core stroke operations

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-005 | Add hub methods to `Hubs/WhiteboardHub.cs`: `JoinBoardWithInvite(token)`, `CreateInvite(boardName)`, `SetPublic(boardName, isPublic)`, `SetMembersCanInvite(boardName, canInvite)`, `RemoveMember(boardName, targetUserId)` — all resolve userId from `Context.GetHttpContext().Items["UserId"]`. Per the parent plan's strongly typed hub convention (GUD-009), extend `IWhiteboardClient` with the server→client events this plan introduces (`BoardSettingsChanged`, `UserRemoved`, `Kicked`, `AccessDenied`, `InvalidInvite`) and broadcast them via the typed proxy (e.g. `Clients.Group(name).BoardSettingsChanged(...)`), never `SendAsync`. | | |
| TASK-006 | Implement `JoinBoardWithInvite(token)`: call `InviteService.RedeemInviteAsync(token, userId)` — if successful, add user as member (`AddMemberAsync`), join the SignalR group, send snapshot + member list. If token invalid/used, return `InvalidInvite` error. | | |
| TASK-007 | Implement `CreateInvite(boardName)`: verify caller is owner OR (`MembersCanInvite` is true AND caller is member). Call `InviteService.CreateInviteAsync`, return invite URL to caller. | | |
| TASK-008 | Implement `SetPublic(boardName, isPublic)`: verify caller is owner, call `BoardService.SetPublicAsync`. Broadcast `BoardSettingsChanged` to group. | | |
| TASK-009 | Implement `SetMembersCanInvite(boardName, canInvite)`: verify caller is owner, call `BoardService.SetMembersCanInviteAsync`. Broadcast `BoardSettingsChanged` to group. | | |
| TASK-010 | Implement `RemoveMember(boardName, targetUserId)`: verify caller is owner AND target is not owner. Call `BoardService.RemoveMemberAsync`. Find target's connections via the connection-tracking dictionary, force-disconnect them from the group, send `Kicked` event to target's connections. Broadcast `UserRemoved(targetUserId)` to group. | | |
| TASK-011 | Add a connection-tracking dictionary (userId → set of connection ids) maintained in `OnConnectedAsync`/`OnDisconnectedAsync` (or a shared singleton) so `RemoveMember` can locate and disconnect a target's connections. | | |
| TASK-012 | Ensure the core `JoinBoard` flow enforces private-board access: if board `IsPublic` is false, reject non-members with `AccessDenied`; if public, auto-add as member. (This enforcement is the consumer of REQ-003 and lives in the core hub.) | | |
| TASK-031 | Layer member-access enforcement onto the core `SendStroke` hub method (`Hubs/WhiteboardHub.cs`): before validating/persisting the stroke, verify the caller is a member via `BoardService.IsMemberAsync(boardId, userId)`; reject non-members (e.g. a removed member, or a non-member on a private board) with `AccessDenied` and neither persist nor broadcast the stroke. (This guard relies on the membership model introduced by TASK-035; it becomes meaningful once boards can be private or members can be removed.) | | |
| TASK-033 | Layer private-board access enforcement onto the core REST snapshot endpoint `GET /api/boards/{name}/snapshot`: for private boards (`IsPublic` is false), require a `userId` and verify membership via `BoardService.IsMemberAsync(boardId, userId)`, returning `403 Forbidden` for non-members; public boards remain open. | | |
| TASK-036 | Layer member-access enforcement onto the `UndoLastStroke` hub method (introduced by [feature-replay-history-1.md](./feature-replay-history-1.md)): before removing the caller's last stroke, verify the caller is a member via `BoardService.IsMemberAsync(boardId, userId)`; reject non-members with `AccessDenied`. (Only applicable once both this plan and the undo feature are in place; mirrors the `SendStroke` guard in TASK-031.) | | |

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
| TASK-034 | Add integration test `Tests/MemberAccessTests.cs` — on a private board, a non-member calling `SendStroke` (or `UndoLastStroke`, where present) is rejected with `AccessDenied` and no stroke is persisted/broadcast; a removed member can no longer send strokes; the `GET /api/boards/{name}/snapshot` endpoint returns `403` for a non-member of a private board and serves the snapshot for members and for public boards. | | |
| TASK-022 | Manual test — Generate an invite link as owner, open it in an incognito window, verify the new user gains access to a private board; redeem the same link a second time and verify it is rejected. | | |
| TASK-023 | Manual test — As owner, remove a member who has a second browser tab open; verify their tab receives `Kicked` and they cannot rejoin the private board. | | |

### Implementation Phase 5

- GOAL-005: Implement owner-forced display-name overrides (REQ-005)

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-024 | Add `ForcedName` (string?, owner-assigned pseudonym override) to the `Models/BoardMember.cs` value object embedded in `Board.Members`. | | |
| TASK-025 | Add to `Services/BoardService.cs`: `SetForcedNameAsync(boardId, targetUserId, forcedName)` (owner sets override) and `ClearForcedNameAsync(boardId, targetUserId)` (owner removes override). Update `GetMembersWithNamesAsync(boardId)` resolution to `ForcedName ?? UserProfile.DisplayName`. | | |
| TASK-026 | Add hub methods to `Hubs/WhiteboardHub.cs`: `SetForcedName(boardName, targetUserId, name)` (verify caller is owner, call `SetForcedNameAsync`, broadcast `UserRenamed(targetUserId, name)` to group) and `ClearForcedName(boardName, targetUserId)` (verify owner, call `ClearForcedNameAsync`, look up the member's self-chosen name, broadcast `UserRenamed(targetUserId, selfChosenName)`). | | |
| TASK-027 | Update the core `SetDisplayName` broadcast so a self-chosen name change resolves to `ForcedName ?? newName` (a forced override always wins over a member's self-update). | | |
| TASK-028 | In `wwwroot/js/admin.js`, add per-member forced-name inline edit (set/clear) wired to `SetForcedName`/`ClearForcedName`; handle `UserRenamed` to update the members list display. | | |
| TASK-029 | Add integration test `Tests/ForcedNameTests.cs` — owner sets a forced name and all members see it via `UserRenamed`; a member's self `SetDisplayName` does not override an active forced name; owner clears the forced name and members revert to the self-chosen name; non-owner cannot call `SetForcedName`/`ClearForcedName`. | | |
| TASK-030 | Manual test — As owner, force a member's pseudonym; in a second tab as that member, change the self-chosen name and verify the forced name still shows to others; clear the override and verify the self-chosen name returns. | | |

## 3. Alternatives

- **ALT-001**: Multi-use invite links with a usage counter — rejected; single-use links give the owner precise control over who joins and naturally expire, matching REQ-001
- **ALT-002**: Time-expiring invite tokens (TTL) instead of single-use — rejected for the demo; single-use is simpler to reason about, though a TTL index could be added later
- **ALT-003**: Role-based access control (multiple admin roles) — rejected as overengineering; a single permanent owner plus an optional "members can invite" flag is sufficient for the demo
- **ALT-004**: Soft-removing members (flag) rather than removing the membership entry — rejected; immediate removal plus forced disconnection best satisfies "lose access immediately"
- **ALT-005**: Store the forced name on the global `UserProfile` rather than per-board on `BoardMember` — rejected; an override must be scoped to the owner's board, not applied to the user everywhere

## 4. Dependencies

- **DEP-001**: `Models/Board.cs` — adds `OwnerId`, `IsPublic`, `MembersCanInvite`, `Members` (`BoardMember` value object) to the MVP model
- **DEP-002**: `Services/BoardService.cs` — adds membership primitives (`AddMemberAsync`, `IsMemberAsync`, `GetMembersWithNamesAsync`) and owner establishment; extends the MVP's `GetOrCreateBoardAsync`
- **DEP-003**: `Services/MongoDbContext.cs` — `Invites` collection accessor
- **DEP-004**: `Hubs/WhiteboardHub.cs` — server-assigned identity, core `JoinBoard`, `SendStroke`, `SetDisplayName`, group broadcasting (and `UndoLastStroke` once introduced by [feature-replay-history-1.md](./feature-replay-history-1.md))
- **DEP-005**: `Services/UserProfileService.cs` — resolve a member's self-chosen `DisplayName` when an override is cleared
- **DEP-006**: `wwwroot/js/admin.js`, `connection.js`, `app.js`, `index.html` — owner panel and invite landing UI

## 5. Files

- **FILE-001**: `Models/Invite.cs` — Single-use invite token document
- **FILE-002**: `Services/InviteService.cs` — Invite creation, atomic redemption, pending invite queries
- **FILE-003**: `Services/BoardService.cs` — Add owner establishment + membership primitives (`AddMemberAsync`, `IsMemberAsync`, `GetMembersWithNamesAsync`, owner in `GetOrCreateBoardAsync`) and `SetPublicAsync`, `SetMembersCanInviteAsync`, `RemoveMemberAsync`, `SetForcedNameAsync`, `ClearForcedNameAsync`
- **FILE-004**: `Models/BoardMember.cs` — Create the embedded member value object (`UserId`; `ForcedName` owner-assigned pseudonym override)
- **FILE-016**: `Models/Board.cs` — Add ownership/membership fields (`OwnerId`, `IsPublic`, `MembersCanInvite`, `Members`) to the MVP model
- **FILE-005**: `Hubs/WhiteboardHub.cs` (+ `Hubs/IWhiteboardClient.cs`) — Update core `JoinBoard` to add the caller as a member; add `JoinBoardWithInvite`, `CreateInvite`, `SetPublic`, `SetMembersCanInvite`, `RemoveMember`, `SetForcedName`, `ClearForcedName`, connection tracking, and member-access guards on the core `SendStroke` (and `UndoLastStroke`, where present) methods; extend `IWhiteboardClient` with this plan's client events (GUD-009)
- **FILE-006**: `wwwroot/js/admin.js` — Owner settings panel (invites, public/private, member removal, forced names)
- **FILE-007**: `wwwroot/js/connection.js` — Register `BoardSettingsChanged`/`UserRemoved`/`Kicked`/`AccessDenied`/`InvalidInvite` handlers
- **FILE-008**: `wwwroot/js/app.js` — Invite URL detection and kick handling
- **FILE-009**: `wwwroot/index.html` — Invite token landing UI
- **FILE-010**: `Tests/InviteFlowTests.cs` — Invite system integration tests
- **FILE-011**: `Tests/MemberRemovalTests.cs` — Member removal integration tests
- **FILE-012**: `Tests/BoardAdminTests.cs` — Owner-only control enforcement tests
- **FILE-013**: `Tests/ForcedNameTests.cs` — Owner-forced pseudonym integration tests
- **FILE-014**: `Tests/MemberAccessTests.cs` — Member-access enforcement tests for `SendStroke` (and `UndoLastStroke`, where present) and the REST snapshot endpoint
- **FILE-015**: The minimal-API board snapshot endpoint `GET /api/boards/{name}/snapshot` (from the parent plan) — Add private-board membership enforcement

## 6. Testing

- **TEST-001**: Integration test — Invite token can be redeemed exactly once; second redemption attempt fails with `InvalidInvite`
- **TEST-002**: Integration test — Owner can remove member; removed member's connection receives `Kicked`; removed member cannot rejoin private board
- **TEST-003**: Integration test — Non-owner cannot call `SetPublic`, `SetMembersCanInvite`, `RemoveMember`, `SetForcedName`, or `ClearForcedName` (rejected with error)
- **TEST-004**: Integration test — When `MembersCanInvite` is true, non-owner member can create invites; when false, only owner can
- **TEST-005**: Integration test — Non-member joining a private board receives `AccessDenied`; joining with a valid invite succeeds and adds membership
- **TEST-006**: Integration test — Owner cannot be removed from their own board
- **TEST-007**: Integration test — Owner sets a forced name; all members see it via `UserRenamed`; the member's self `SetDisplayName` does not override it; clearing the override reverts to the self-chosen name
- **TEST-008**: Manual test — Generate invite link as owner, open in incognito window, verify new user gains access to private board
- **TEST-009**: Manual test — Owner forces a member's pseudonym, the member changes their own name, others still see the forced name; owner clears it and the self-chosen name returns
- **TEST-010**: Integration test — On a private board, a non-member calling `SendStroke` (or `UndoLastStroke`, where present) is rejected with `AccessDenied` (nothing persisted/broadcast); a removed member can no longer send strokes; `GET /api/boards/{name}/snapshot` returns `403` for a non-member of a private board and serves the snapshot for members and public boards

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
