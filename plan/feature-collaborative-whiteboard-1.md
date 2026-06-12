---
goal: Build a collaborative web whiteboard with persistent history using ASP.NET Core and MongoDB
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'Planned'
tags: [feature, architecture, asp.net-core, mongodb, signalr, real-time, collaboration]
---

# Introduction

![Status: Planned](https://img.shields.io/badge/status-Planned-blue)

Technology demonstrator of ASP.NET Core and MongoDB implementing a collaborative web whiteboard. Multiple users can simultaneously draw on a shared canvas with full persistent stroke history, real-time synchronization via SignalR, and the ability to replay or undo the entire drawing timeline.

## 1. Requirements & Constraints

- **REQ-001**: Users can draw freehand strokes on an HTML5 Canvas element in a web browser
- **REQ-002**: Multiple users can collaborate on the same whiteboard simultaneously with live cursor/stroke visibility
- **REQ-003**: All drawing operations are persisted to MongoDB with timestamps and user identity
- **REQ-004**: History and snapshot are served via separate endpoints; both are filtered by the owner's visibility settings (HiddenRanges) for non-owner members. Only the owner can access the unfiltered view.
- **REQ-005**: Users can create and join named whiteboard sessions via URL
- **REQ-006**: The whiteboard state is reconstructed from snapshot on page load (late joiners see current state)
- **REQ-007**: The first user to access a board becomes its owner; ownership is permanent
- **REQ-008**: Board owner can generate single-use invite links; each link is valid for exactly one user to join, then expires
- **REQ-009**: Board owner can enable or disable the ability for other members to create invite links (default: disabled)
- **REQ-010**: Board owner can toggle board visibility between public (anyone with the URL can join) and private (invite-only) at any time
- **REQ-011**: Board owner can remove existing members from the board; removed members lose access immediately and their active connections are disconnected
- **REQ-012**: Board owner can hide all contributions of any member up to the current moment; hidden strokes become invisible to all other members but remain in storage
- **REQ-013**: Board owner can restore previously hidden contributions of a member, making them visible to all members again
- **REQ-014**: Board owner can toggle a personal "show hidden" view to see all strokes including hidden ones (other members always see the filtered view)
- **REQ-015**: Users can choose a pseudonym (display name) shown to other members on the board; stored per-user globally and editable at any time
- **REQ-016**: Board owner can override (force) the visible pseudonym of any member on their board; the forced name is what all other members see regardless of the user's self-chosen name
- **SEC-001**: User identity is bound to the browser instance via a server-issued session token (HttpOnly cookie or SignalR connection token), automatically established on first access — no login or name entry required
- **SEC-002**: Users cannot impersonate another user; the server assigns and validates identity — client-supplied user IDs are never trusted
- **CON-001**: Target framework is .NET 10.0 (already configured in canvas.csproj)
- **CON-002**: MongoDB Atlas (free tier M0, replica set) is the sole persistence layer — no local MongoDB daemon required
- **CON-003**: Real-time communication must use ASP.NET Core SignalR (WebSocket transport preferred)
- **CON-004**: Frontend must be vanilla JavaScript with HTML5 Canvas (no SPA framework required for demo)
- **CON-005**: MongoDB connection string must be stored in `dotnet user-secrets` (never committed to git)
- **GUD-001**: Follow ASP.NET Core minimal API patterns where applicable
- **GUD-002**: Use MongoDB.Driver official .NET driver (not Entity Framework)
- **PAT-001**: Event-sourcing pattern for stroke history — each stroke is an immutable event
- **PAT-002**: Store temporal metadata (timestamps, point time-offsets, stroke duration) sufficient to reproduce a video-like replay of edits with inactivity gaps compressed or skipped
- **PAT-003**: Maintain a materialized snapshot of the current board state (active strokes only) for fast client loading; full event history is a separate concern used only for replay and undo

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Set up project infrastructure — MongoDB connection, SignalR hub, and static file serving

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Add NuGet packages: `MongoDB.Driver` (latest stable) to `canvas.csproj`. SignalR is included in the framework. | | |
| TASK-002 | Initialize user-secrets: run `dotnet user-secrets init` then `dotnet user-secrets set "MongoDB:ConnectionString" "mongodb+srv://<user>:<pass>@<cluster>.mongodb.net/?retryWrites=true&w=majority"`. Add `"MongoDB": { "DatabaseName": "canvas" }` to `appsettings.json` (no secrets in this file). | | |
| TASK-003 | Create `Services/MongoDbContext.cs` — singleton service that reads `MongoDB:ConnectionString` from configuration (user-secrets in dev), exposes `IMongoDatabase` and typed collection accessors for `Boards` and `StrokeEvents` | | |
| TASK-004 | Register MongoDB services in `Program.cs` using `builder.Services.AddSingleton<MongoDbContext>()` | | |
| TASK-005 | Add `builder.Services.AddSignalR()` and map hub endpoint `app.MapHub<WhiteboardHub>("/hub/whiteboard")` in `Program.cs` | | |
| TASK-006 | Create identity middleware in `Middleware/UserIdentityMiddleware.cs`: on each request, check for `X-User-Id` HttpOnly cookie. If absent, generate a new UUID, set it as an HttpOnly/Secure/SameSite=Strict cookie, and attach to `HttpContext.Items["UserId"]`. SignalR hub reads userId from `Context.GetHttpContext().Items["UserId"]` — client never supplies its own identity. | | |
| TASK-007 | Register middleware in `Program.cs` before `UseStaticFiles`: `app.UseMiddleware<UserIdentityMiddleware>()` | | |
| TASK-008 | Add `app.UseStaticFiles()` in `Program.cs` and create `wwwroot/` directory for frontend assets | | |
| TASK-009 | Remove the default weather forecast endpoint from `Program.cs` | | |

### Implementation Phase 2

- GOAL-002: Implement domain models and MongoDB persistence layer for boards and strokes

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-010 | Create `Models/Board.cs` — properties: `Id` (ObjectId), `Name` (string), `OwnerId` (string, UUID of first user to access), `IsPublic` (bool, default true), `MembersCanInvite` (bool, default false), `Members` (List<BoardMember>), `HiddenRanges` (List<HiddenRange>, visibility filters), `CreatedAt` (DateTime), `LastActivityAt` (DateTime), `ActiveStrokes` (List<Stroke>, embedded snapshot — includes ALL strokes, filtering applied at serve time) | | |
| TASK-011 | Create `Models/BoardMember.cs` — properties: `UserId` (string), `ForcedName` (string?, owner-assigned pseudonym override; if set, displayed instead of user's self-chosen name). Embedded in Board.Members. | | |
| TASK-012 | Create `Models/UserProfile.cs` — properties: `Id` (ObjectId), `UserId` (string, unique, server-assigned UUID), `DisplayName` (string, user-chosen pseudonym, default "Anonymous"), `CreatedAt` (DateTime). Stored in a `Users` collection. | | |
| TASK-013 | Create `Models/HiddenRange.cs` — properties: `UserId` (string, whose strokes are hidden), `HiddenBefore` (DateTime, all strokes by this user with Timestamp ≤ this value are hidden from non-owner members). Adding a range hides; removing it restores. | | |
| TASK-014 | Create `Models/Invite.cs` — properties: `Id` (ObjectId), `BoardId` (string), `Token` (string, unique random URL-safe token), `CreatedBy` (string, UUID), `CreatedAt` (DateTime), `UsedBy` (string?, UUID of user who redeemed), `UsedAt` (DateTime?), `IsUsed` (bool, default false). Single-use: once redeemed, cannot be used again. | | |
| TASK-015 | Create `Models/Stroke.cs` — properties: `Id` (ObjectId), `BoardId` (string), `UserId` (string), `Points` (List<Point>), `Color` (string), `Width` (float), `Timestamp` (DateTime, server-assigned UTC time when stroke was received), `Duration` (long, milliseconds from first point to last point in the stroke as measured by client), `SequenceNumber` (long). | | |
| TASK-016 | Create `Models/StrokeEvent.cs` — properties: `Id` (ObjectId), `BoardId` (string), `Type` (enum: `Add`/`Remove`), `Stroke` (Stroke, the stroke added or removed), `UserId` (string), `Timestamp` (DateTime), `SequenceNumber` (long). Append-only event log for history replay and undo. | | |
| TASK-017 | Create `Models/Point.cs` — properties: `X` (double), `Y` (double), `Pressure` (double, optional), `TimeOffset` (long, milliseconds since stroke start — enables point-by-point animated replay within a single stroke) | | |
| TASK-018 | Create `Services/UserProfileService.cs` — methods: `GetOrCreateProfileAsync(userId)` (creates with default "Anonymous" name on first access), `SetDisplayNameAsync(userId, name)`, `GetDisplayNameAsync(userId)`, `GetDisplayNamesAsync(userIds)` (batch lookup) | | |
| TASK-019 | Create `Services/BoardService.cs` — methods: `CreateBoardAsync(name, ownerId)`, `GetBoardAsync(id)`, `GetOrCreateBoardAsync(name, userId)` (first caller becomes owner and member), `UpdateLastActivityAsync(boardId)`, `GetSnapshotAsync(boardName, requestingUserId)` (filter by HiddenRanges for non-owners), `GetFullSnapshotAsync(boardName)` (unfiltered, owner-only), `AddStrokeToSnapshotAsync(boardId, stroke)`, `RemoveStrokeFromSnapshotAsync(boardId, strokeId)`, `SetPublicAsync(boardId, isPublic)`, `SetMembersCanInviteAsync(boardId, canInvite)`, `AddMemberAsync(boardId, userId)`, `RemoveMemberAsync(boardId, userId)`, `IsMemberAsync(boardId, userId)`, `HideContributionsAsync(boardId, targetUserId, hiddenBefore)`, `RestoreContributionsAsync(boardId, targetUserId)`, `GetHiddenRangesAsync(boardId)`, `SetForcedNameAsync(boardId, targetUserId, forcedName)` (owner sets override), `ClearForcedNameAsync(boardId, targetUserId)` (owner removes override), `GetMembersWithNamesAsync(boardId)` (returns members with resolved display names: ForcedName ?? UserProfile.DisplayName) | | |
| TASK-020 | Create `Services/InviteService.cs` — methods: `CreateInviteAsync(boardId, createdByUserId)` (generates cryptographic random token), `RedeemInviteAsync(token, userId)` (atomic findAndModify: mark used + return boardId, fail if already used), `GetPendingInvitesAsync(boardId)` | | |
| TASK-021 | Create `Services/StrokeEventService.cs` — methods: `AppendEventAsync(strokeEvent)`, `GetEventsAsync(boardId)` (full history for replay), `GetRecentEventsAsync(boardId, count)` (for undo lookup), `GetEventsSinceAsync(boardId, sequenceNumber)` | | |
| TASK-022 | Create MongoDB indexes: compound index on `StrokeEvents` for `{ BoardId: 1, SequenceNumber: 1 }`, index on `{ BoardId: 1, Timestamp: 1 }` (replay), unique index on `Boards` for `{ Name: 1 }`, unique index on `Invites` for `{ Token: 1 }`, index on `Invites` for `{ BoardId: 1, IsUsed: 1 }`, unique index on `Users` for `{ UserId: 1 }`, and TTL index on `Boards.LastActivityAt` (optional, 30 days) | | |

### Implementation Phase 3

- GOAL-003: Implement SignalR hub for real-time collaboration with access control enforcement

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-023 | Create `Hubs/WhiteboardHub.cs` implementing `Hub` — all methods resolve userId from `Context.GetHttpContext().Items["UserId"]` (server-assigned, never from client). Methods: `JoinBoard(boardName)`, `JoinBoardWithInvite(token)`, `LeaveBoard(boardName)`, `SendStroke(boardName, strokeData)`, `UndoLastStroke(boardName)`, `CreateInvite(boardName)`, `SetPublic(boardName, isPublic)`, `SetMembersCanInvite(boardName, canInvite)`, `RemoveMember(boardName, targetUserId)`, `HideContributions(boardName, targetUserId)`, `RestoreContributions(boardName, targetUserId)`, `ToggleShowHidden(boardName, showHidden)`, `SetDisplayName(name)`, `SetForcedName(boardName, targetUserId, name)`, `ClearForcedName(boardName, targetUserId)` | | |
| TASK-024 | Implement `JoinBoard`: read userId from context, call `UserProfileService.GetOrCreateProfileAsync(userId)`, call `GetOrCreateBoardAsync(name, userId)` (first caller becomes owner+member). If board is private, verify `IsMemberAsync` — reject with `AccessDenied`. If public, auto-add as member. Send filtered snapshot + member list with resolved names to caller, broadcast `UserJoined(userId, displayName)` to group. | | |
| TASK-025 | Implement `JoinBoardWithInvite`: read userId from context, call `InviteService.RedeemInviteAsync(token, userId)` — if successful, add user as member, join group, send filtered snapshot + member list. If token invalid/used, return `InvalidInvite` error. | | |
| TASK-026 | Implement `SendStroke`: verify caller is a member, validate stroke data, add to snapshot, append event, broadcast to group | | |
| TASK-027 | Implement `UndoLastStroke`: verify caller is a member, query recent events for last `Add` by caller, remove from snapshot, append `Remove` event, broadcast `StrokeRemoved(strokeId)` | | |
| TASK-028 | Implement `CreateInvite`: verify caller is owner OR (`MembersCanInvite` is true AND caller is member). Call `InviteService.CreateInviteAsync`, return invite URL to caller. | | |
| TASK-029 | Implement `SetPublic(boardName, isPublic)`: verify caller is owner, call `BoardService.SetPublicAsync`. Broadcast `BoardSettingsChanged` to group. | | |
| TASK-030 | Implement `SetMembersCanInvite(boardName, canInvite)`: verify caller is owner, call `BoardService.SetMembersCanInviteAsync`. Broadcast `BoardSettingsChanged` to group. | | |
| TASK-031 | Implement `RemoveMember(boardName, targetUserId)`: verify caller is owner AND target is not owner. Call `BoardService.RemoveMemberAsync`. Find target's connections via tracking dictionary, force-disconnect them from group, send `Kicked` event to target's connections. Broadcast `UserRemoved(targetUserId)` to group. | | |
| TASK-032 | Implement `HideContributions(boardName, targetUserId)`: verify caller is owner. Call `BoardService.HideContributionsAsync(boardId, targetUserId, DateTime.UtcNow)`. Broadcast `StrokesHidden(targetUserId, hiddenBefore)` to non-owner members. Owner's view unchanged. | | |
| TASK-033 | Implement `RestoreContributions(boardName, targetUserId)`: verify caller is owner. Call `BoardService.RestoreContributionsAsync(boardId, targetUserId)`. Broadcast `StrokesRestored(targetUserId, restoredStrokes[])` to non-owner members. | | |
| TASK-034 | Implement `ToggleShowHidden(boardName, showHidden)`: verify caller is owner. If showHidden=true, send full unfiltered snapshot to caller only. If false, send filtered snapshot. Personal view toggle — no broadcast. | | |
| TASK-035 | Implement `SetDisplayName(name)`: any user can call. Validate name (non-empty, max 30 chars). Call `UserProfileService.SetDisplayNameAsync(userId, name)`. Broadcast `UserRenamed(userId, resolvedName)` to all groups the user is in (resolved = ForcedName ?? new name). | | |
| TASK-036 | Implement `SetForcedName(boardName, targetUserId, name)`: verify caller is owner. Call `BoardService.SetForcedNameAsync(boardId, targetUserId, name)`. Broadcast `UserRenamed(targetUserId, name)` to group. | | |
| TASK-037 | Implement `ClearForcedName(boardName, targetUserId)`: verify caller is owner. Call `BoardService.ClearForcedNameAsync(boardId, targetUserId)`. Look up user's self-chosen name, broadcast `UserRenamed(targetUserId, selfChosenName)` to group. | | |
| TASK-038 | Implement `OnDisconnectedAsync`: remove user from tracking, broadcast `UserLeft` event to relevant groups | | |
| TASK-039 | Add in-memory connection tracking: `ConcurrentDictionary<string, UserConnection>` mapping connectionId to (boardName, userId) for disconnect cleanup and forced removal | | |

### Implementation Phase 4

- GOAL-004: Build the frontend HTML5 Canvas whiteboard with SignalR client integration

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-040 | Create `wwwroot/index.html` — page with: board name input (or auto-join via URL path), invite token landing page, canvas element (full viewport), color picker, stroke width slider, undo button, display name input (editable), connected users list (showing resolved names), owner settings panel (hidden for non-owners) | | |
| TASK-041 | Add SignalR JavaScript client via CDN: `<script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js"></script>` | | |
| TASK-042 | Create `wwwroot/js/canvas.js` — Canvas drawing module: capture mousedown/mousemove/mouseup and touch events, collect points into stroke objects with `TimeOffset` (ms since stroke start via `performance.now()`), compute `Duration`, render strokes on canvas using `CanvasRenderingContext2D` path API | | |
| TASK-043 | Create `wwwroot/js/connection.js` — SignalR connection module: establish connection to `/hub/whiteboard` (identity via cookie, no client-supplied userId), implement `JoinBoard`/`JoinBoardWithInvite`, handle `LoadSnapshot`/`StrokeReceived`/`StrokeRemoved`/`UserJoined`/`UserLeft`/`UserRemoved`/`UserRenamed`/`BoardSettingsChanged`/`Kicked`/`AccessDenied`/`InvalidInvite`/`StrokesHidden`/`StrokesRestored` events | | |
| TASK-044 | Create `wwwroot/js/admin.js` — Owner panel module: show/hide based on ownership, wire buttons for `SetPublic`, `SetMembersCanInvite`, `CreateInvite` (copy link to clipboard), `RemoveMember`, `HideContributions`/`RestoreContributions`, `SetForcedName`/`ClearForcedName` (per-user in members list with inline edit), `ToggleShowHidden` checkbox. Handle `BoardSettingsChanged` and `UserRenamed` to update UI. | | |
| TASK-045 | Create `wwwroot/js/app.js` — Application orchestrator: detect invite token in URL, route join method, wire display name input to `SetDisplayName` on blur/enter, manage board state, handle reconnection, `Kicked`, `StrokesHidden`, `StrokesRestored`, `UserRenamed` (update members list) | | |
| TASK-046 | Create `wwwroot/css/style.css` — Minimal styling: full-viewport canvas, toolbar overlay, admin panel sidebar, name input, responsive layout | | |
| TASK-047 | Implement `LoadSnapshot` handler: on receiving active strokes array + member list with resolved names, clear canvas and render all strokes immediately, populate members panel | | |

### Implementation Phase 5

- GOAL-005: Implement history replay and REST API endpoints for board management

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-036 | Add REST endpoint `GET /api/boards` — returns list of public boards with active stroke counts (requires no auth) | | |
| TASK-037 | Add REST endpoint `GET /api/boards/{name}/snapshot` — returns current board snapshot; enforce access (public boards: open, private boards: require userId query param + membership check) | | |
| TASK-038 | Add REST endpoint `GET /api/boards/{name}/history?userId=UUID` — returns event log filtered by HiddenRanges for non-owner callers (events matching hidden user+timestamp are excluded); owner receives unfiltered history. Paginated, 100 per page. | | |
| TASK-039 | Add REST endpoint `DELETE /api/boards/{name}` — owner-only; clears snapshot and all history for a board | | |
| TASK-040 | Implement frontend replay mode in `wwwroot/js/replay.js`: fetch full history from `GET /api/boards/{name}/history`, step through stroke events using `Timestamp` for inter-stroke timing and `Point.TimeOffset` for intra-stroke animation; skip inactivity gaps exceeding a configurable threshold (default 3s); play/pause/seek controls; playback speed multiplier (1x/2x/4x) | | |
| TASK-041 | Add replay button to `index.html` toolbar that triggers replay mode overlay on the canvas | | |

### Implementation Phase 6

- GOAL-006: Testing, documentation, and deployment setup

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-042 | Create `README.md` with: project overview, architecture diagram (text), prerequisites (dotnet 10 SDK, Atlas account), setup instructions (user-secrets configuration), usage guide, invite flow explanation | | |
| TASK-043 | Add `.gitignore` entries to ensure user-secrets and `obj/`/`bin/` are excluded | | |
| TASK-044 | Add integration test: `Tests/WhiteboardHubTests.cs` — verify JoinBoard returns snapshot, SendStroke persists and broadcasts, UndoLastStroke removes correct stroke, access denied for non-members on private boards | | |
| TASK-045 | Add integration test: `Tests/InviteFlowTests.cs` — verify invite creation (owner-only by default), redemption adds member, double-redemption fails, member invite when `MembersCanInvite` enabled | | |
| TASK-046 | Add integration test: `Tests/MemberRemovalTests.cs` — verify owner can remove member, removed user cannot rejoin private board, non-owner cannot remove others | | |
| TASK-047 | Add unit test: `Tests/StrokeEventServiceTests.cs` — verify event append and query operations against Atlas (test database) | | |

## 3. Alternatives

- **ALT-001**: Use Entity Framework Core with MongoDB provider instead of raw MongoDB.Driver — rejected because EF Core MongoDB support is less mature and this demo aims to showcase direct MongoDB.Driver usage patterns
- **ALT-002**: Use Blazor WebAssembly for the frontend — rejected to keep frontend simple and framework-agnostic, showcasing SignalR interop with vanilla JS
- **ALT-003**: Use Redis Pub/Sub for real-time messaging instead of SignalR — rejected because SignalR is the idiomatic ASP.NET Core solution and supports WebSocket fallback automatically
- **ALT-004**: Use CRDT (Conflict-free Replicated Data Types) for conflict resolution — rejected as overengineering for a demo; sequential event-sourcing with server-authoritative ordering is sufficient
- **ALT-005**: Use Canvas API library (Fabric.js, Konva) — rejected to minimize dependencies and demonstrate raw Canvas 2D API usage

## 4. Dependencies

- **DEP-001**: MongoDB.Driver NuGet package (latest stable, currently ~3.x) — .NET driver for MongoDB
- **DEP-002**: MongoDB Atlas M0 free tier cluster (replica set, shared infrastructure)
- **DEP-003**: .NET 10.0 SDK (already configured)
- **DEP-004**: SignalR JavaScript client library (CDN-hosted, ~8.0.0)

## 5. Files

- **FILE-001**: `Program.cs` — Application entry point; configure services (MongoDB, SignalR, static files, identity middleware), map endpoints and hub
- **FILE-002**: `canvas.csproj` — Add MongoDB.Driver package reference
- **FILE-003**: `appsettings.json` — Add MongoDB database name configuration (no secrets)
- **FILE-004**: `Middleware/UserIdentityMiddleware.cs` — Assigns and validates server-side user identity via HttpOnly cookie
- **FILE-005**: `Models/Board.cs` — Board document model with ownership, membership (BoardMember with ForcedName), visibility, HiddenRanges, embedded `ActiveStrokes` snapshot
- **FILE-006**: `Models/BoardMember.cs` — Embedded member value object (UserId + optional ForcedName override)
- **FILE-007**: `Models/UserProfile.cs` — User document with self-chosen DisplayName (global, stored in Users collection)
- **FILE-008**: `Models/HiddenRange.cs` — Value object defining per-user visibility cut-off (UserId + HiddenBefore timestamp)
- **FILE-009**: `Models/Invite.cs` — Single-use invite token document
- **FILE-010**: `Models/Stroke.cs` — Stroke value object (embedded in Board and StrokeEvent)
- **FILE-011**: `Models/StrokeEvent.cs` — Append-only event log document (Add/Remove events for replay & undo)
- **FILE-012**: `Models/Point.cs` — Point value object with TimeOffset
- **FILE-013**: `Services/MongoDbContext.cs` — MongoDB connection and collection accessor (Boards, StrokeEvents, Invites, Users)
- **FILE-014**: `Services/UserProfileService.cs` — User profile CRUD (display name management)
- **FILE-015**: `Services/BoardService.cs` — Board CRUD, snapshot mutations, membership, visibility, forced name management
- **FILE-016**: `Services/InviteService.cs` — Invite creation, atomic redemption, pending invite queries
- **FILE-017**: `Services/StrokeEventService.cs` — Append-only event log persistence and querying
- **FILE-018**: `Hubs/WhiteboardHub.cs` — SignalR hub for real-time collaboration with access control and name management
- **FILE-019**: `wwwroot/index.html` — Main whiteboard page with admin panel and name editing
- **FILE-020**: `wwwroot/js/canvas.js` — Canvas drawing engine
- **FILE-021**: `wwwroot/js/connection.js` — SignalR client wrapper with identity via cookie
- **FILE-022**: `wwwroot/js/admin.js` — Owner settings panel (invites, visibility, member management, forced names)
- **FILE-023**: `wwwroot/js/app.js` — Application orchestrator with invite URL detection and name editing
- **FILE-024**: `wwwroot/js/replay.js` — History replay module (fetches event log, animates with gap compression)
- **FILE-025**: `wwwroot/css/style.css` — Stylesheet
- **FILE-026**: `.gitignore` — Exclude bin/, obj/, user-secrets, IDE files
- **FILE-027**: `README.md` — Project documentation with Atlas setup, invite flow, and name management
- **FILE-028**: `Tests/WhiteboardHubTests.cs` — Hub integration tests
- **FILE-029**: `Tests/InviteFlowTests.cs` — Invite system integration tests
- **FILE-030**: `Tests/MemberRemovalTests.cs` — Member removal integration tests
- **FILE-031**: `Tests/DisplayNameTests.cs` — Pseudonym and forced name integration tests
- **FILE-032**: `Tests/StrokeEventServiceTests.cs` — Event service unit tests

## 6. Testing

- **TEST-001**: Integration test — Joining a public board returns empty snapshot for new boards and populated snapshot for existing boards; first joiner becomes owner
- **TEST-002**: Integration test — Sending a stroke adds it to the board snapshot AND appends an Add event to the event log
- **TEST-003**: Integration test — Undo removes only the current user's last stroke from snapshot, appends Remove event, and broadcasts removal to group
- **TEST-004**: Integration test — Non-member joining a private board receives `AccessDenied`; joining with valid invite succeeds and adds membership
- **TEST-005**: Integration test — Invite token can be redeemed exactly once; second redemption attempt fails with `InvalidInvite`
- **TEST-006**: Integration test — Owner can remove member; removed member's connection receives `Kicked`; removed member cannot rejoin private board
- **TEST-007**: Integration test — Non-owner cannot call `SetPublic`, `SetMembersCanInvite`, or `RemoveMember` (rejected with error)
- **TEST-008**: Integration test — When `MembersCanInvite` is true, non-owner member can create invites; when false, only owner can
- **TEST-009**: Unit test — StrokeEventService.AppendEventAsync assigns incrementing sequence numbers per board
- **TEST-010**: Unit test — StrokeEventService.GetEventsAsync returns events ordered by SequenceNumber ascending
- **TEST-011**: Unit test — BoardService.GetSnapshotAsync returns only active strokes (excludes undone strokes); filters out strokes matching HiddenRanges for non-owner callers
- **TEST-012**: Integration test — Owner hides member contributions; non-owner members receive `StrokesHidden` and affected strokes disappear from their view; owner still sees them with `ToggleShowHidden(true)`
- **TEST-013**: Integration test — Owner restores hidden contributions; non-owner members receive `StrokesRestored` with the restored strokes and re-render them
- **TEST-014**: Integration test — New member joining after hide sees filtered snapshot (hidden strokes excluded); owner joining sees full snapshot
- **TEST-015**: Integration test — Non-owner cannot call `HideContributions` or `RestoreContributions` (rejected with error)
- **TEST-016**: Manual test — Open two browser tabs on the same board URL, draw in one, verify stroke appears in the other within 100ms
- **TEST-017**: Manual test — Refresh page after drawing; verify all active strokes load instantly from snapshot
- **TEST-018**: Manual test — Click replay button; verify strokes animate in chronological order with inactivity gaps compressed
- **TEST-019**: Manual test — Generate invite link as owner, open in incognito window, verify new user gains access to private board
- **TEST-020**: Manual test — As owner, hide a member's contributions, toggle "show hidden" on/off, verify canvas updates correctly

## 7. Risks & Assumptions

- **RISK-001**: SignalR WebSocket connections may be dropped by proxies/firewalls — mitigated by SignalR's automatic transport fallback (Server-Sent Events → Long Polling)
- **RISK-002**: Atlas network latency (~20-50ms per write) may feel slower than local MongoDB — mitigated by optimistic UI updates (render stroke locally before server confirms persistence)
- **RISK-003**: Large board histories may cause slow page loads — mitigated by pagination and potential future lazy-loading of older strokes
- **RISK-004**: If the HttpOnly cookie is stolen (e.g., via XSS on a related domain), an attacker could impersonate the user — mitigated by using `SameSite=Strict`, `Secure` flags, and no inline scripts
- **ASSUMPTION-001**: MongoDB Atlas M0 cluster is provisioned and accessible (IP allowlist configured)
- **ASSUMPTION-002**: Single-server deployment is sufficient (no SignalR backplane needed for demo scale)
- **ASSUMPTION-003**: Users are identified by a server-assigned UUID delivered via HttpOnly cookie on first request — no login flow, identity persists across sessions in the same browser
- **ASSUMPTION-004**: Browser supports HTML5 Canvas and WebSocket APIs (modern browsers only)
- **ASSUMPTION-005**: Board ownership is permanent and cannot be transferred (simplification for demo)

## 8. Related Specifications / Further Reading

- [ASP.NET Core SignalR documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction)
- [MongoDB.Driver .NET documentation](https://www.mongodb.com/docs/drivers/csharp/current/)
- [HTML5 Canvas API reference](https://developer.mozilla.org/en-US/docs/Web/API/Canvas_API)
- [Event Sourcing pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/event-sourcing)
- [SignalR JavaScript client](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client)
