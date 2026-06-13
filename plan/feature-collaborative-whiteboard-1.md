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
- **REQ-004**: History and current-state snapshot are served via separate endpoints (snapshot for editing, history for replay/undo). Note: owner visibility filtering of these endpoints is layered on by [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) and is not part of the MVP.
- **REQ-005**: Users can create and join named whiteboard sessions via URL
- **REQ-006**: The whiteboard state is reconstructed from snapshot on page load (late joiners see current state)
- **REQ-007**: The first user to access a board becomes its owner; ownership is permanent
- **REQ-015**: Users can choose a pseudonym (display name) shown to other members on the board; stored per-user globally and editable at any time
- **SEC-001**: User identity is bound to the browser instance via a server-issued session token (HttpOnly cookie or SignalR connection token), automatically established on first access — no login or name entry required
- **SEC-002**: Users cannot impersonate another user; the server assigns and validates identity — client-supplied user IDs are never trusted
- **SEC-003**: IP-based rate limiting: configurable request cap per IP range (e.g., /24 subnet) to mitigate abuse from shared networks or botnets
- **SEC-004**: User-based rate limiting: configurable cap on actions (strokes, invites, joins) per user identity per time window to prevent spam and resource exhaustion
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
| TASK-007 | Add rate limiting via `Microsoft.AspNetCore.RateLimiting` (built-in). Configure two policies in `Program.cs`: (1) `"ip-range"` — sliding window limiter keyed by client IP /24 subnet (configurable in appsettings: `RateLimits:IpWindowSeconds`, `RateLimits:IpMaxRequests`); (2) `"user"` — sliding window limiter keyed by userId from cookie (configurable: `RateLimits:UserWindowSeconds`, `RateLimits:UserMaxRequests`). Apply both to hub and API endpoints. | | |
| TASK-008 | Create `Middleware/RateLimitKeyProviders.cs` — helper methods: `GetSubnetKey(HttpContext)` (extracts client IP, masks to /24), `GetUserKey(HttpContext)` (reads userId from Items). | | |
| TASK-009 | Add rate limit configuration section to `appsettings.json`: `"RateLimits": { "IpWindowSeconds": 60, "IpMaxRequests": 200, "UserWindowSeconds": 10, "UserMaxRequests": 30 }` (defaults; tunable without redeployment via config reload). | | |
| TASK-010 | Register middleware in `Program.cs`: `app.UseRateLimiter()` before `app.UseMiddleware<UserIdentityMiddleware>()`; identity middleware before `UseStaticFiles`. | | |
| TASK-011 | Add `app.UseStaticFiles()` in `Program.cs` and create `wwwroot/` directory for frontend assets | | |
| TASK-012 | Remove the default weather forecast endpoint from `Program.cs` | | |

### Implementation Phase 2

- GOAL-002: Implement domain models and MongoDB persistence layer for boards and strokes

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-013 | Create `Models/Board.cs` — properties: `Id` (ObjectId), `Name` (string), `OwnerId` (string, UUID of first user to access), `IsPublic` (bool, default true), `MembersCanInvite` (bool, default false), `Members` (List<BoardMember>), `CreatedAt` (DateTime), `LastActivityAt` (DateTime), `ActiveStrokes` (List<Stroke>, embedded snapshot — includes ALL strokes, filtering applied at serve time). Note: `HiddenRanges` is added by [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) | | |
| TASK-014 | Create `Models/BoardMember.cs` — properties: `UserId` (string). Embedded in Board.Members. Note: owner-forced `ForcedName` override is added by [feature-board-administration-1.md](./feature-board-administration-1.md) | | |
| TASK-020 | Create `Models/UserProfile.cs` — properties: `Id` (ObjectId), `UserId` (string, unique, server-assigned UUID), `DisplayName` (string, user-chosen pseudonym, default "Anonymous"), `CreatedAt` (DateTime). Stored in a `Users` collection. | | |
| TASK-015 | Create `Models/Stroke.cs` — properties: `Id` (ObjectId), `BoardId` (string), `UserId` (string), `Points` (List<Point>), `Color` (string), `Width` (float), `Timestamp` (DateTime, server-assigned UTC time when stroke was received), `Duration` (long, milliseconds from first point to last point in the stroke as measured by client), `SequenceNumber` (long). | | |
| TASK-016 | Create `Models/StrokeEvent.cs` — properties: `Id` (ObjectId), `BoardId` (string), `Type` (enum: `Add`/`Remove`), `Stroke` (Stroke, the stroke added or removed), `UserId` (string), `Timestamp` (DateTime), `SequenceNumber` (long). Append-only event log for history replay and undo. | | |
| TASK-017 | Create `Models/Point.cs` — properties: `X` (double), `Y` (double), `Pressure` (double, optional), `TimeOffset` (long, milliseconds since stroke start — enables point-by-point animated replay within a single stroke) | | |
| TASK-018 | Create `Services/UserProfileService.cs` — methods: `GetOrCreateProfileAsync(userId)` (creates with default "Anonymous" name on first access), `SetDisplayNameAsync(userId, name)`, `GetDisplayNameAsync(userId)`, `GetDisplayNamesAsync(userIds)` (batch lookup) | | |
| TASK-019 | Create `Services/BoardService.cs` — methods: `CreateBoardAsync(name, ownerId)`, `GetBoardAsync(id)`, `GetOrCreateBoardAsync(name, userId)` (first caller becomes owner and member), `UpdateLastActivityAsync(boardId)`, `GetSnapshotAsync(boardName, requestingUserId)`, `GetFullSnapshotAsync(boardName)` (unfiltered, owner-only), `AddStrokeToSnapshotAsync(boardId, stroke)`, `RemoveStrokeFromSnapshotAsync(boardId, strokeId)`, `AddMemberAsync(boardId, userId)`, `IsMemberAsync(boardId, userId)`, `GetMembersWithNamesAsync(boardId)` (returns members with their self-chosen `UserProfile.DisplayName`). Note: administration methods `SetPublicAsync`/`SetMembersCanInviteAsync`/`RemoveMemberAsync` plus forced-name overrides `SetForcedNameAsync`/`ClearForcedNameAsync` (and the `ForcedName ?? DisplayName` resolution) are in [feature-board-administration-1.md](./feature-board-administration-1.md); HiddenRange-based filtering plus `HideContributionsAsync`/`RestoreContributionsAsync`/`GetHiddenRangesAsync` are in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) | | |
| TASK-021 | Create `Services/StrokeEventService.cs` — methods: `AppendEventAsync(strokeEvent)`, `GetEventsAsync(boardId)` (full history for replay), `GetRecentEventsAsync(boardId, count)` (for undo lookup), `GetEventsSinceAsync(boardId, sequenceNumber)` | | |
| TASK-022 | Create MongoDB indexes: compound index on `StrokeEvents` for `{ BoardId: 1, SequenceNumber: 1 }`, index on `{ BoardId: 1, Timestamp: 1 }` (replay), unique index on `Boards` for `{ Name: 1 }`, unique index on `Users` for `{ UserId: 1 }`, and TTL index on `Boards.LastActivityAt` (optional, 30 days). Note: `Invites` collection indexes are specified in [feature-board-administration-1.md](./feature-board-administration-1.md) | | |

### Implementation Phase 3

- GOAL-003: Implement SignalR hub for real-time collaboration with access control enforcement

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-023 | Create `Hubs/WhiteboardHub.cs` implementing `Hub` — all methods resolve userId from `Context.GetHttpContext().Items["UserId"]` (server-assigned, never from client). Methods: `JoinBoard(boardName)`, `LeaveBoard(boardName)`, `SendStroke(boardName, strokeData)`, `UndoLastStroke(boardName)`, `SetDisplayName(name)`. Note: administration methods `JoinBoardWithInvite`/`CreateInvite`/`SetPublic`/`SetMembersCanInvite`/`RemoveMember` plus forced-name overrides `SetForcedName`/`ClearForcedName` are in [feature-board-administration-1.md](./feature-board-administration-1.md); `HideContributions`/`RestoreContributions`/`ToggleShowHidden` are in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) | | |
| TASK-024 | Implement `JoinBoard`: read userId from context, call `UserProfileService.GetOrCreateProfileAsync(userId)`, call `GetOrCreateBoardAsync(name, userId)` (creates the board if absent; first caller is recorded as owner per REQ-007), add the caller as a member and join the board's SignalR group, send the snapshot + member list with display names to the caller, broadcast `UserJoined(userId, displayName)` to the group. Note: private-board access enforcement (reject non-members with `AccessDenied`) is layered onto this flow by [feature-board-administration-1.md](./feature-board-administration-1.md) and does not apply in the MVP, where all boards are public. | | |
| TASK-026 | Implement `SendStroke`: validate stroke data, add to snapshot, append event, broadcast to group. Note: member-access enforcement (verify the caller is a member, reject non-members with `AccessDenied`) is layered onto this method by [feature-board-administration-1.md](./feature-board-administration-1.md); it is a no-op in the MVP, where every joiner is auto-added as a member. | | |
| TASK-027 | Implement `UndoLastStroke`: query recent events for last `Add` by caller, remove from snapshot, append `Remove` event, broadcast `StrokeRemoved(strokeId)`. Note: member-access enforcement (verify the caller is a member, reject non-members with `AccessDenied`) is layered onto this method by [feature-board-administration-1.md](./feature-board-administration-1.md); it is a no-op in the MVP. | | |
| TASK-035 | Implement `SetDisplayName(name)`: any user can call. Validate name (non-empty, max 30 chars). Call `UserProfileService.SetDisplayNameAsync(userId, name)`. Broadcast `UserRenamed(userId, name)` to all groups the user is in. | | |
| TASK-038 | Implement `OnDisconnectedAsync`: remove user from tracking, broadcast `UserLeft` event to relevant groups | | |
| TASK-039 | Add in-memory connection tracking: `ConcurrentDictionary<string, UserConnection>` mapping connectionId to (boardName, userId) for disconnect cleanup and forced removal | | |

### Implementation Phase 4

- GOAL-004: Build the frontend HTML5 Canvas whiteboard with SignalR client integration

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-040 | Create `wwwroot/index.html` — page with: board name input (or auto-join via URL path), canvas element (full viewport), color picker, stroke width slider, undo button, display name input (editable), connected users list (showing display names), owner settings panel (hidden for non-owners). Note: invite-token landing UI per [feature-board-administration-1.md](./feature-board-administration-1.md) | | |
| TASK-041 | Add SignalR JavaScript client via CDN: `<script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js"></script>` | | |
| TASK-042 | Create `wwwroot/js/canvas.js` — Canvas drawing module: capture mousedown/mousemove/mouseup and touch events, collect points into stroke objects with `TimeOffset` (ms since stroke start via `performance.now()`), compute `Duration`, render strokes on canvas using `CanvasRenderingContext2D` path API | | |
| TASK-043 | Create `wwwroot/js/connection.js` — SignalR connection module: establish connection to `/hub/whiteboard` (identity via cookie, no client-supplied userId), implement `JoinBoard`, handle `LoadSnapshot`/`StrokeReceived`/`StrokeRemoved`/`UserJoined`/`UserLeft`/`UserRenamed` events. Note: administration events `UserRemoved`/`BoardSettingsChanged`/`Kicked`/`AccessDenied`/`InvalidInvite` and `joinBoardWithInvite` per [feature-board-administration-1.md](./feature-board-administration-1.md); visibility moderation events `StrokesHidden`/`StrokesRestored` per [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) | | |
| TASK-044 | Create `wwwroot/js/admin.js` — Owner panel module: show/hide based on ownership. Handle `UserRenamed` to update the members list UI. Note: board administration controls (invites, public/private, member removal, forced names) per [feature-board-administration-1.md](./feature-board-administration-1.md); visibility moderation controls (hide/restore, show-hidden) per [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) | | |
| TASK-045 | Create `wwwroot/js/app.js` — Application orchestrator: route join method, wire display name input to `SetDisplayName` on blur/enter, manage board state, handle reconnection, `UserRenamed` (update members list). Note: invite-URL detection and `Kicked` handling per [feature-board-administration-1.md](./feature-board-administration-1.md) | | |
| TASK-046 | Create `wwwroot/css/style.css` — Minimal styling: full-viewport canvas, toolbar overlay, admin panel sidebar, name input, responsive layout | | |
| TASK-047 | Implement `LoadSnapshot` handler: on receiving active strokes array + member list with display names, clear canvas and render all strokes immediately, populate members panel | | |

### Implementation Phase 5

- GOAL-005: Implement REST API endpoints for board management (replay functionality extracted to [feature-replay-history-1.md](./feature-replay-history-1.md))

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-036 | Add REST endpoint `GET /api/boards` — returns list of public boards with active stroke counts (requires no auth) | | |
| TASK-037 | Add REST endpoint `GET /api/boards/{name}/snapshot` — returns the current board snapshot; open access in the MVP, where all boards are public. Note: private-board access enforcement (require userId + membership check, return 403 for non-members) is layered onto this endpoint by [feature-board-administration-1.md](./feature-board-administration-1.md). | | |
| TASK-048 | Add REST endpoint `DELETE /api/boards/{name}` — owner-only; clears snapshot and all history for a board | | |

### Implementation Phase 6

- GOAL-006: Testing, documentation, and deployment setup

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-049 | Create `README.md` with: project overview, architecture diagram (text), prerequisites (dotnet 10 SDK, Atlas account), setup instructions (user-secrets configuration), and usage guide | | |
| TASK-050 | Add `.gitignore` entries to ensure user-secrets and `obj/`/`bin/` are excluded | | |
| TASK-051 | Add integration test: `Tests/WhiteboardHubTests.cs` — verify JoinBoard returns the snapshot and broadcasts `UserJoined`, SendStroke persists and broadcasts, UndoLastStroke removes the caller's last stroke. (Private-board access-denial tests are in [feature-board-administration-1.md](./feature-board-administration-1.md).) | | |
| TASK-052 | Add unit test: `Tests/StrokeEventServiceTests.cs` — verify event append and query operations against Atlas (test database) | | |

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

- **FILE-001**: `Program.cs` — Application entry point; configure services (MongoDB, SignalR, static files, rate limiting, identity middleware), map endpoints and hub
- **FILE-002**: `canvas.csproj` — Add MongoDB.Driver package reference
- **FILE-003**: `appsettings.json` — Add MongoDB database name and rate limit configuration (no secrets)
- **FILE-004**: `Middleware/UserIdentityMiddleware.cs` — Assigns and validates server-side user identity via HttpOnly cookie
- **FILE-005**: `Middleware/RateLimitKeyProviders.cs` — IP subnet and user-based rate limit key extraction helpers
- **FILE-005**: `Models/Board.cs` — Board document model with ownership, membership (`BoardMember`), embedded `ActiveStrokes` snapshot (`BoardMember.ForcedName` added by the board administration plan; `HiddenRanges` added by the visibility moderation plan)
- **FILE-006**: `Models/BoardMember.cs` — Embedded member value object (UserId; optional `ForcedName` override added by the board administration plan)
- **FILE-007**: `Models/UserProfile.cs` — User document with self-chosen DisplayName (global, stored in Users collection)
- **FILE-008**: `Models/HiddenRange.cs` — (Defined in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md))
- **FILE-009**: `Models/Invite.cs` — (Defined in [feature-board-administration-1.md](./feature-board-administration-1.md))
- **FILE-010**: `Models/Stroke.cs` — Stroke value object (embedded in Board and StrokeEvent)
- **FILE-011**: `Models/StrokeEvent.cs` — Append-only event log document (Add/Remove events for replay & undo)
- **FILE-012**: `Models/Point.cs` — Point value object with TimeOffset
- **FILE-013**: `Services/MongoDbContext.cs` — MongoDB connection and collection accessor (Boards, StrokeEvents, Invites, Users)
- **FILE-014**: `Services/UserProfileService.cs` — User profile CRUD (display name management)
- **FILE-015**: `Services/BoardService.cs` — Board CRUD, snapshot mutations, core membership, member name resolution (administration, forced-name, and visibility-moderation methods specified in their respective plans)
- **FILE-016**: `Services/InviteService.cs` — (Defined in [feature-board-administration-1.md](./feature-board-administration-1.md))
- **FILE-017**: `Services/StrokeEventService.cs` — Append-only event log persistence and querying
- **FILE-018**: `Hubs/WhiteboardHub.cs` — SignalR hub for real-time collaboration with access control and name management
- **FILE-019**: `wwwroot/index.html` — Main whiteboard page with admin panel and name editing
- **FILE-020**: `wwwroot/js/canvas.js` — Canvas drawing engine
- **FILE-021**: `wwwroot/js/connection.js` — SignalR client wrapper with identity via cookie
- **FILE-022**: `wwwroot/js/admin.js` — Owner settings panel container (board administration, forced-name, and visibility-moderation controls specified in their respective plans)
- **FILE-023**: `wwwroot/js/app.js` — Application orchestrator with invite URL detection and name editing
- **FILE-024**: `plan/feature-replay-history-1.md` — Extracted replay feature plan (history endpoint, animation engine, UI controls)
- **FILE-025**: `wwwroot/css/style.css` — Stylesheet
- **FILE-026**: `.gitignore` — Exclude bin/, obj/, user-secrets, IDE files
- **FILE-027**: `README.md` — Project documentation with Atlas setup, invite flow, and name management
- **FILE-028**: `Tests/WhiteboardHubTests.cs` — Hub integration tests
- **FILE-029**: `Tests/InviteFlowTests.cs` — (Specified in [feature-board-administration-1.md](./feature-board-administration-1.md))
- **FILE-030**: `Tests/MemberRemovalTests.cs` — (Specified in [feature-board-administration-1.md](./feature-board-administration-1.md))
- **FILE-031**: `Tests/DisplayNameTests.cs` — Pseudonym (self-chosen display name) integration tests
- **FILE-032**: `Tests/StrokeEventServiceTests.cs` — Event service unit tests

## 6. Testing

- **TEST-001**: Integration test — Joining a public board returns empty snapshot for new boards and populated snapshot for existing boards; first joiner becomes owner
- **TEST-002**: Integration test — Sending a stroke adds it to the board snapshot AND appends an Add event to the event log
- **TEST-003**: Integration test — Undo removes only the current user's last stroke from snapshot, appends Remove event, and broadcasts removal to group
- **TEST-004**: Board administration tests (invites, public/private access, member removal, owner-only enforcement) are specified in [feature-board-administration-1.md](./feature-board-administration-1.md)
- **TEST-009**: Unit test — StrokeEventService.AppendEventAsync assigns incrementing sequence numbers per board
- **TEST-010**: Unit test — StrokeEventService.GetEventsAsync returns events ordered by SequenceNumber ascending
- **TEST-011**: Unit test — BoardService.GetSnapshotAsync returns only active strokes (excludes undone strokes)
- **TEST-012**: Visibility moderation tests (hide/restore/show-hidden) are specified in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md)
- **TEST-016**: Manual test — Open two browser tabs on the same board URL, draw in one, verify stroke appears in the other within 100ms
- **TEST-017**: Manual test — Refresh page after drawing; verify all active strokes load instantly from snapshot
- **TEST-018**: Manual test — Click replay button; verify strokes animate in chronological order with inactivity gaps compressed (detailed tests in [feature-replay-history-1.md](./feature-replay-history-1.md))
- **TEST-019**: Manual test — Board administration manual checks (invite link redemption, member removal) are in [feature-board-administration-1.md](./feature-board-administration-1.md)
- **TEST-020**: Manual test — Visibility moderation manual checks are in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md)

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

- [Replay & History View feature plan](./feature-replay-history-1.md) — extracted sub-plan for video-like replay functionality
- [Board Administration feature plan](./feature-board-administration-1.md) — extracted sub-plan for invites, public/private visibility, and member removal
- [Visibility Moderation feature plan](./feature-visibility-moderation-1.md) — extracted sub-plan for owner hide/restore of member contributions
- [ASP.NET Core SignalR documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction)
- [MongoDB.Driver .NET documentation](https://www.mongodb.com/docs/drivers/csharp/current/)
- [HTML5 Canvas API reference](https://developer.mozilla.org/en-US/docs/Web/API/Canvas_API)
- [Event Sourcing pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/event-sourcing)
- [SignalR JavaScript client](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client)
