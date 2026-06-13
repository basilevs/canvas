---
goal: Build a collaborative web whiteboard with a persistent state snapshot using ASP.NET Core and MongoDB
version: 1.0
date_created: 2026-06-13
last_updated: 2026-06-13
owner: basilevs
status: 'Planned'
tags: [feature, architecture, asp.net-core, mongodb, signalr, real-time, collaboration]
---

# Introduction

![Status: Planned](https://img.shields.io/badge/status-Planned-blue)

Technology demonstrator of ASP.NET Core and MongoDB implementing a collaborative web whiteboard. Multiple users can simultaneously draw on a shared canvas with a persistent current-state snapshot and real-time synchronization via SignalR. The append-only event log that powers video-like replay and undo is post-MVP — it is introduced by [feature-replay-history-1.md](./feature-replay-history-1.md); the MVP persists only the materialized snapshot.

## 1. Requirements & Constraints

- **REQ-001**: Users can draw freehand strokes on an HTML5 Canvas element in a web browser
- **REQ-002**: Multiple users can collaborate on the same whiteboard simultaneously with live cursor/stroke visibility
- **REQ-003**: All drawing operations are persisted to MongoDB with timestamps and user identity
- **REQ-004**: The current-state snapshot is served via a REST endpoint for editing/late-join. Note: the separate history endpoint (for replay/undo) and the append-only event log are not part of the MVP — they are introduced by [feature-replay-history-1.md](./feature-replay-history-1.md), and owner visibility filtering of the served data is layered on by [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md).
- **REQ-005**: Users can create and join named whiteboard sessions via URL
- **REQ-006**: The whiteboard state is reconstructed from snapshot on page load (late joiners see current state)
- **REQ-015**: Users can choose a pseudonym (display name) shown to other connected users on the board; stored per-user globally and editable at any time. Note: board ownership is not part of the MVP — it is introduced by [feature-board-administration-1.md](./feature-board-administration-1.md)
- **SEC-001**: User identity is bound to the browser instance via a server-issued session token (HttpOnly cookie or SignalR connection token), automatically established on first access — no login or name entry required
- **SEC-002**: Users cannot impersonate another user; the server assigns and validates identity — client-supplied user IDs are never trusted
- **SEC-003**: Abuse controls (IP-range and per-user rate limiting) are layered on by [feature-abuse-controls-1.md](./feature-abuse-controls-1.md) and are not part of the MVP, which targets a trusted environment
- **CON-001**: Target framework is .NET 10.0 (already configured in canvas.csproj)
- **CON-002**: MongoDB Atlas (free tier M0, replica set) is the sole persistence layer — no local MongoDB daemon required
- **CON-003**: Real-time communication must use ASP.NET Core SignalR (WebSocket transport preferred)
- **CON-004**: Frontend must be vanilla JavaScript with HTML5 Canvas (no SPA framework required for demo); use the classless **Pico.css** framework via CDN for default styling so no design system needs to be authored — only app-specific layout (full-viewport canvas, toolbar overlay) is hand-written CSS
- **CON-005**: MongoDB connection string must be stored in `dotnet user-secrets` (never committed to git)
- **GUD-001**: Follow ASP.NET Core minimal API patterns where applicable; the project already uses minimal APIs + built-in OpenAPI (`AddOpenApi`/`MapOpenApi`) — do not introduce controllers or Swashbuckle
- **GUD-002**: Use MongoDB.Driver official .NET driver (not Entity Framework)
- **GUD-003**: REST endpoints must never expose persistence/domain models (`Board`, `Stroke`, `StrokeEvent`) directly; define `sealed record` response DTOs (e.g. `BoardSnapshotResponse`, `StrokeResponse`) with `<summary>` XML doc comments, and use `DateTimeOffset` (not `DateTime`) for any date/time fields in DTOs
- **GUD-004**: Every service has an interface (`IMongoDbContext`, `IBoardService`, `IUserProfileService`) registered with the interface in DI (`AddSingleton<IBoardService, BoardService>()` etc.); enables mocking in unit tests
- **GUD-005**: Every REST endpoint accepts a `CancellationToken` and forwards it through all async service/driver calls; service method signatures take a trailing `CancellationToken`
- **GUD-006**: REST endpoints follow standard HTTP semantics — `GET` → `200 OK`/`404 Not Found`, `DELETE` → `204 No Content`/`404 Not Found` — and chain OpenAPI metadata (`.WithName().WithSummary().Produces<T>()`); enum values serialize as strings (`JsonStringEnumConverter`)
- **GUD-007**: Use a global exception handler (`IExceptionHandler` in `Middleware/`) with `AddProblemDetails()` + `UseExceptionHandler()`; error responses use RFC 7807 Problem Details, never leaking exception messages
- **GUD-008**: Use MSTest for the automated test projects (aligns with the installed dotnet-test skill set); name tests by what they verify, and label tests that touch MongoDB Atlas as integration tests (not unit tests)
- **GUD-009**: Use **strongly typed SignalR hubs** — the hub derives from `Hub<IWhiteboardClient>`, where `IWhiteboardClient` is an interface declaring every server→client callback (`LoadSnapshot`, `StrokeReceived`, `UserJoined`, `UserLeft`, `UserRenamed`, …) with `Task`-returning methods. All broadcasts use the typed proxy (`Clients.Group(name).StrokeReceived(...)`), never the stringly-typed `Clients.Group(name).SendAsync("StrokeReceived", ...)`, so client method names and signatures are checked at compile time. `IWhiteboardClient` is the single typed client contract; the replay-history, board-administration, and visibility-moderation sub-plans extend it with their additional events
- **PAT-001**: Maintain a materialized snapshot of the current board state (active strokes only) as the sole MVP persistence, for fast client loading. Note: the append-only event-sourcing log (each stroke an immutable event) and the temporal metadata needed for video-like replay are introduced by [feature-replay-history-1.md](./feature-replay-history-1.md)

## 2. Implementation Steps

### Implementation Phase 1

- GOAL-001: Set up project infrastructure — MongoDB connection, SignalR hub, and static file serving

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-001 | Add NuGet packages: `MongoDB.Driver` (latest stable) to `canvas.csproj`. SignalR is included in the framework. | | |
| TASK-002 | Initialize user-secrets: run `dotnet user-secrets init` then `dotnet user-secrets set "MongoDB:ConnectionString" "mongodb+srv://<user>:<pass>@<cluster>.mongodb.net/"`. Add `"MongoDB": { "DatabaseName": "canvas" }` to `appsettings.json` (no secrets in this file). | | |
| TASK-003 | Create `Services/MongoDbContext.cs` (and `IMongoDbContext` interface) — singleton service that reads `MongoDB:ConnectionString` from configuration (user-secrets in dev), exposes `IMongoDatabase` and typed collection accessors for `Boards` and `Users`. Note: the `StrokeEvents` collection accessor is added by [feature-replay-history-1.md](./feature-replay-history-1.md) and the `Invites` accessor by [feature-board-administration-1.md](./feature-board-administration-1.md) | | |
| TASK-004 | Register services in `Program.cs` via their interfaces: `builder.Services.AddSingleton<IMongoDbContext, MongoDbContext>()`, and likewise `IUserProfileService`/`IBoardService` (per GUD-004). Also add `builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()))` so enums serialize as strings (GUD-006). | | |
| TASK-005 | Add `builder.Services.AddSignalR()` and map hub endpoint `app.MapHub<WhiteboardHub>("/hub/whiteboard")` in `Program.cs` | | |
| TASK-006 | Create identity middleware in `Middleware/UserIdentityMiddleware.cs`: on each request, check for a `uid` HttpOnly cookie. If absent, generate a new UUID, set it as an HttpOnly, `SameSite=Strict` cookie, and attach to `HttpContext.Items["UserId"]`. Set the `Secure` flag only when the request is HTTPS (`context.Request.IsHttps`) so identity still works over `http://localhost` in development — a `Secure` cookie is never returned over plain HTTP. SignalR hub reads userId from `Context.GetHttpContext().Items["UserId"]` — client never supplies its own identity. | | |
| TASK-010 | Register middleware in `Program.cs`: `app.UseMiddleware<UserIdentityMiddleware>()` before `UseStaticFiles`. Note: rate limiting middleware (`UseRateLimiter`) is added by [feature-abuse-controls-1.md](./feature-abuse-controls-1.md) and is not part of the MVP. | | |
| TASK-011 | Add `app.UseStaticFiles()` in `Program.cs` and create `wwwroot/` directory for frontend assets | | |
| TASK-012 | Remove the default weather forecast endpoint (and `WeatherForecast` record) from `Program.cs` | | |
| TASK-053 | Add global error handling (GUD-007): create `Middleware/ApiExceptionHandler.cs` implementing `IExceptionHandler` that maps domain exceptions to RFC 7807 Problem Details (without leaking exception messages); register `builder.Services.AddExceptionHandler<ApiExceptionHandler>()` + `builder.Services.AddProblemDetails()` and `app.UseExceptionHandler()` in `Program.cs`. | | |

### Implementation Phase 2

- GOAL-002: Implement domain models and MongoDB persistence layer for boards and strokes

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-013 | Create `Models/Board.cs` — properties: `Id` (ObjectId), `Name` (string), `CreatedAt` (UTC `DateTime` — native BSON date), `LastActivityAt` (UTC `DateTime` — native BSON date, drives the TTL index), `ActiveStrokes` (List<Stroke>, embedded snapshot of all active strokes; the MVP serves them unfiltered — owner visibility filtering is layered on at serve time by [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md)). Store date/time as UTC `DateTime` in models (clean BSON date, queryable/TTL-able); DTOs expose `DateTimeOffset` per GUD-003. Note: ownership/membership fields (`OwnerId`, `IsPublic`, `MembersCanInvite`, `Members`) are added by [feature-board-administration-1.md](./feature-board-administration-1.md); `HiddenRanges` is added by [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) | | |
| TASK-020 | Create `Models/UserProfile.cs` — properties: `Id` (ObjectId), `UserId` (string, unique, server-assigned UUID), `DisplayName` (string, user-chosen pseudonym, default "Anonymous"), `CreatedAt` (UTC `DateTime`). Stored in a `Users` collection. | | |
| TASK-015 | Create `Models/Stroke.cs` — properties: `Id` (ObjectId), `BoardId` (string), `UserId` (string), `Points` (List<Point>), `Color` (string), `Width` (float), `Timestamp` (UTC `DateTime`, server-assigned when the stroke was received), `Duration` (long, milliseconds from first point to last point in the stroke as measured by client), `SequenceNumber` (long). | | |
| TASK-017 | Create `Models/Point.cs` — properties: `X` (double), `Y` (double), `Pressure` (double, optional), `TimeOffset` (long, milliseconds since stroke start — enables point-by-point animated replay within a single stroke) | | |
| TASK-018 | Create `Services/UserProfileService.cs` (and `IUserProfileService` interface) — methods (each taking a trailing `CancellationToken`, GUD-005): `GetOrCreateProfileAsync(userId, ct)` (creates with default "Anonymous" name on first access), `SetDisplayNameAsync(userId, name, ct)`, `GetDisplayNameAsync(userId, ct)`, `GetDisplayNamesAsync(userIds, ct)` (batch lookup) | | |
| TASK-019 | Create `Services/BoardService.cs` (and `IBoardService` interface) — methods (each taking a trailing `CancellationToken`, GUD-005): `CreateBoardAsync(name)`, `GetBoardAsync(id)`, `GetOrCreateBoardAsync(name)` (atomic upsert via `FindOneAndUpdate` with `IsUpsert=true` + `ReturnDocument.After` so concurrent first-joiners don't trip the unique `Name` index — returns the `Board` including its `Id`), `UpdateLastActivityAsync(boardId)`, `GetSnapshotAsync(boardName)`, `AddStrokeToSnapshotAsync(boardId, stroke)` (atomic `UpdateOne` with `$push` to `ActiveStrokes` — never load-modify-save, to avoid lost updates when users draw concurrently). Methods that mutate a known board take `boardId` (resolved once in `JoinBoard` and cached in the connection map); read/create-by-URL methods take `boardName`. Note: board ownership + membership (owner establishment in `GetOrCreateBoardAsync`, `AddMemberAsync`/`IsMemberAsync`/`GetMembersWithNamesAsync`) plus administration methods `SetPublicAsync`/`SetMembersCanInviteAsync`/`RemoveMemberAsync` and forced-name overrides `SetForcedNameAsync`/`ClearForcedNameAsync` (with `ForcedName ?? DisplayName` resolution) are in [feature-board-administration-1.md](./feature-board-administration-1.md); identity-based snapshot filtering (`GetSnapshotAsync` per-caller filtering, the owner-only unfiltered `GetFullSnapshotAsync`) plus `HideContributionsAsync`/`RestoreContributionsAsync`/`GetHiddenRangesAsync` are in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) | | |
| TASK-022 | Create MongoDB indexes: unique index on `Boards` for `{ Name: 1 }`, unique index on `Users` for `{ UserId: 1 }`, and an optional 30-day TTL index on `Boards.LastActivityAt` (TTL requires a BSON `Date` field — hence `LastActivityAt` is a UTC `DateTime`, not `DateTimeOffset`, which the driver would serialize as an array and never expire). Note: the `StrokeEvents` indexes are specified in [feature-replay-history-1.md](./feature-replay-history-1.md) and the `Invites` collection indexes in [feature-board-administration-1.md](./feature-board-administration-1.md) | | |

### Implementation Phase 3

- GOAL-003: Implement SignalR hub for real-time collaboration with access control enforcement

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-023 | Define the strongly typed client interface `Hubs/IWhiteboardClient.cs` (GUD-009) declaring all server→client callbacks as `Task`-returning methods with concrete signatures: `LoadSnapshot(BoardSnapshotResponse board, IReadOnlyList<ConnectedUserResponse> users)`, `StrokeReceived(StrokeResponse stroke)`, `UserJoined(string userId, string displayName)`, `UserLeft(string userId)`, `UserRenamed(string userId, string name)`, `CursorMoved(string userId, double x, double y)` (ephemeral live-cursor position, never persisted). Create `Hubs/WhiteboardHub.cs` as `Hub<IWhiteboardClient>` — all methods resolve userId from `Context.GetHttpContext().Items["UserId"]` (server-assigned, never from client) and all broadcasts use the typed proxy (e.g. `Clients.Group(name).StrokeReceived(...)`), never `SendAsync`. Hub methods: `JoinBoard(string boardName)`, `LeaveBoard(string boardName)`, `SendStroke(string boardName, StrokeInput stroke)` (the inbound `StrokeInput` DTO carries only client-supplied data — points, color, width, duration; the server assigns `Id`, `UserId`, and `Timestamp`, never trusting the client for them), `SetDisplayName(string name)`, `MoveCursor(string boardName, double x, double y)` (broadcast-only live cursor; not persisted to the snapshot). Note: undo (`UndoLastStroke`/`StrokeRemoved`) is not part of the MVP — it is owned by [feature-replay-history-1.md](./feature-replay-history-1.md) (it depends on that plan's post-MVP event log); administration methods `JoinBoardWithInvite`/`CreateInvite`/`SetPublic`/`SetMembersCanInvite`/`RemoveMember` plus forced-name overrides `SetForcedName`/`ClearForcedName` (and their client events) are in [feature-board-administration-1.md](./feature-board-administration-1.md); `HideContributions`/`RestoreContributions`/`ToggleShowHidden` (and their client events) are in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) — these sub-plans extend `IWhiteboardClient` with the events they add | | |
| TASK-024 | Implement `JoinBoard`: read userId from context, call `UserProfileService.GetOrCreateProfileAsync(userId)`, call `GetOrCreateBoardAsync(name)` (creates the board if absent), join the board's SignalR group and record the connection (including the resolved `boardId`) in the in-memory tracking map (TASK-039), send the snapshot + the list of currently connected users with display names (derived from the connection map, resolved via `UserProfileService.GetDisplayNamesAsync`) to the caller via `LoadSnapshot`, broadcast `UserJoined(userId, displayName)` to *others* in the group (`Clients.OthersInGroup`) — the caller already has the full user list from `LoadSnapshot`. Note: board ownership establishment (first caller becomes owner) and persisted membership are not part of the MVP — they are introduced by [feature-board-administration-1.md](./feature-board-administration-1.md), which also layers private-board access enforcement (reject non-members with `AccessDenied`) onto this flow. In the MVP all boards are public and the "members" shown are simply the live connected users. | | |
| TASK-025 | Implement `LeaveBoard`: remove the connection from its SignalR group and the tracking map, and broadcast `UserLeft(userId)` to the group — the explicit counterpart to `OnDisconnectedAsync` for users navigating away without dropping the connection. | | |
| TASK-026 | Implement `SendStroke`: validate the inbound `StrokeInput`, build a server-authoritative `Stroke` (assign `Id`/`UserId`/`Timestamp`), append it via `AddStrokeToSnapshotAsync(boardId, stroke)` (atomic `$push`, using the `boardId` cached in the connection map — no per-stroke board lookup), and broadcast `StrokeReceived(stroke)` to the group. Note: member-access enforcement (verify the caller is a member, reject non-members with `AccessDenied`) is layered onto this method by [feature-board-administration-1.md](./feature-board-administration-1.md), and append-only event persistence (write an `Add` StrokeEvent) is layered on by [feature-replay-history-1.md](./feature-replay-history-1.md); the MVP has no membership concept and persists only the snapshot, so any connected user may draw. | | |
| TASK-027 | Implement `MoveCursor`: broadcast `CursorMoved(userId, x, y)` to *others* in the board group (`Clients.OthersInGroup`). Cursor positions are ephemeral presence data — never validated, persisted, or read back from the snapshot. | | |
| TASK-035 | Implement `SetDisplayName(name)`: any user can call. Validate name (non-empty, max 30 chars). Call `UserProfileService.SetDisplayNameAsync(userId, name)`. Broadcast `UserRenamed(userId, name)` to all groups the user is in. | | |
| TASK-038 | Implement `OnDisconnectedAsync`: remove user from tracking, broadcast `UserLeft` event to relevant groups | | |
| TASK-039 | Add in-memory connection tracking: `ConcurrentDictionary<string, UserConnection>` mapping connectionId to (boardName, boardId, userId) for disconnect cleanup, forced removal, and `boardId`-keyed snapshot mutations | | |

### Implementation Phase 4

- GOAL-004: Build the frontend HTML5 Canvas whiteboard with SignalR client integration

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-040 | Create `wwwroot/index.html` — page with: board name input (or auto-join via URL path), canvas element (full viewport), color picker, stroke width slider, display name input (editable), connected users list (showing display names). Use semantic, class-light markup (`<nav>`, `<button>`, `<input>`, `<aside>`, `<label>`) so Pico.css styles controls automatically; wrap chrome in `<main class="container">` and set `data-theme`. Note: the undo control is post-MVP (depends on the event log, [feature-replay-history-1.md](./feature-replay-history-1.md)); the owner settings panel and invite-token landing UI are added by [feature-board-administration-1.md](./feature-board-administration-1.md) | | |
| TASK-041 | Add CDN `<link>`/`<script>` tags in `index.html`: Pico.css `<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@picocss/pico@2/css/pico.min.css">` and SignalR client `<script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js"></script>` | | |
| TASK-042 | Create `wwwroot/js/canvas.js` — Canvas drawing module: capture mousedown/mousemove/mouseup and touch events, collect points into stroke objects with `TimeOffset` (ms since stroke start via `performance.now()`), compute `Duration`, render strokes on canvas using `CanvasRenderingContext2D` path API; also render a lightweight remote-cursor overlay (one marker per userId, updated on `CursorMoved`, removed on `UserLeft`) drawn above the strokes | | |
| TASK-043 | Create `wwwroot/js/connection.js` — SignalR connection module: establish connection to `/hub/whiteboard` (identity via cookie, no client-supplied userId), implement `JoinBoard`, send throttled `MoveCursor` (≈20–30 Hz) on pointer move, and handle `LoadSnapshot`/`StrokeReceived`/`UserJoined`/`UserLeft`/`UserRenamed`/`CursorMoved` events. Note: there is no `StrokeRemoved` handler — undo is not part of the MVP (it is introduced by [feature-replay-history-1.md](./feature-replay-history-1.md)); administration events `UserRemoved`/`BoardSettingsChanged`/`Kicked`/`AccessDenied`/`InvalidInvite` and `joinBoardWithInvite` per [feature-board-administration-1.md](./feature-board-administration-1.md); visibility moderation events `StrokesHidden`/`StrokesRestored` per [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md) | | |
| TASK-044 | The owner panel module `wwwroot/js/admin.js` (show/hide based on ownership, administration controls) is introduced by [feature-board-administration-1.md](./feature-board-administration-1.md) and is not part of the MVP — the MVP has no ownership/admin UI. The live connected-users list is updated by `app.js` (TASK-045) on `UserJoined`/`UserLeft`/`UserRenamed`. | | |
| TASK-045 | Create `wwwroot/js/app.js` — Application orchestrator: route join method, wire display name input to `SetDisplayName` on blur/enter, manage board state, handle reconnection, maintain the connected-users list on `UserJoined`/`UserLeft`/`UserRenamed`. Note: invite-URL detection and `Kicked` handling per [feature-board-administration-1.md](./feature-board-administration-1.md) | | |
| TASK-046 | Create `wwwroot/css/style.css` — only app-specific layout that Pico.css does not cover: full-viewport `<canvas>`, the toolbar overlay positioned over the canvas, and the connected-users sidebar placement. Override Pico design tokens (`--pico-*`) instead of restyling components; do not hand-author button/form/typography styles. | | |
| TASK-047 | Implement `LoadSnapshot` handler: on receiving active strokes array + connected-users list with display names, clear canvas and render all strokes immediately, populate the connected-users panel | | |

### Implementation Phase 5

- GOAL-005: Implement the board snapshot REST endpoint (board discovery/deletion extracted to [feature-board-management-1.md](./feature-board-management-1.md); replay functionality extracted to [feature-replay-history-1.md](./feature-replay-history-1.md))

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-054 | Create response DTOs as `sealed record` types in `Dtos/` (GUD-003), each with `<summary>` XML doc comments and `DateTimeOffset` for date/time fields: `BoardSnapshotResponse` (board name, `IReadOnlyList<StrokeResponse>`), `StrokeResponse` (id, userId, color, width, `IReadOnlyList<PointResponse>`, `Timestamp`), `PointResponse` (x, y, pressure, timeOffset), `ConnectedUserResponse` (userId, displayName). Also define inbound (client→server) hub DTOs: `StrokeInput` (color, width, duration, `IReadOnlyList<PointInput>`) and `PointInput` (x, y, pressure, timeOffset) — these carry only client-supplied fields; the server assigns `Id`/`UserId`/`Timestamp`. Map domain models → DTOs (and `StrokeInput` → `Stroke`) in the service/endpoint/hub layer; never serialize `Board`/`Stroke`/`StrokeEvent` directly. When mapping a UTC `DateTime` model field to a `DateTimeOffset` DTO field, treat it as UTC (`new DateTimeOffset(value, TimeSpan.Zero)`). Note: the board-listing DTO `BoardSummaryResponse` is defined by [feature-board-management-1.md](./feature-board-management-1.md). | | |
| TASK-037 | Add minimal-API endpoint `GET /api/boards/{name}/snapshot` — returns `200 OK` with `BoardSnapshotResponse`, or `404 Not Found` if the board does not exist; open access in the MVP (all boards public). Accept `CancellationToken`; use `TypedResults` with an explicit `Results<Ok<BoardSnapshotResponse>, NotFound>` return type; chain `.WithName("GetBoardSnapshot").WithSummary(...).Produces<BoardSnapshotResponse>(200).Produces(404)`. Note: private-board access enforcement (require userId + membership check, return `403`) is layered onto this endpoint by [feature-board-administration-1.md](./feature-board-administration-1.md). | | |
| TASK-055 | Add a request for `GET /api/boards/{name}/snapshot` to `canvas.http`, including an error path (non-existent board → 404), matching the port in `Properties/launchSettings.json` (GUD-006). Note: requests for the board list/delete endpoints are added by [feature-board-management-1.md](./feature-board-management-1.md). | | |

### Implementation Phase 6

- GOAL-006: Testing, documentation, and deployment setup

| Task | Description | Completed | Date |
|------|-------------|-----------|------|
| TASK-049 | Create `README.md` with: project overview, architecture diagram (text), prerequisites (dotnet 10 SDK, Atlas account), setup instructions (user-secrets configuration), and usage guide | | |
| TASK-050 | Add `.gitignore` entries to ensure user-secrets and `obj/`/`bin/` are excluded | | |
| TASK-051 | Add MSTest integration test (GUD-008): `Tests/WhiteboardHubTests.cs` — verify JoinBoard returns the snapshot and broadcasts `UserJoined`, and SendStroke adds the stroke to the board snapshot and broadcasts `StrokeReceived`. (Private-board access-denial tests are in [feature-board-administration-1.md](./feature-board-administration-1.md); undo and event-log tests are in [feature-replay-history-1.md](./feature-replay-history-1.md).) | | |
| TASK-056 | Scaffold the test project before adding tests: `dotnet new mstest -o Tests` (`Tests/Canvas.Tests.csproj`, net10.0), add a project reference to `canvas.csproj`, and create a solution (`dotnet new sln` + `dotnet sln add` for both projects). Atlas-touching integration tests read a **separate** test-database connection string from user-secrets/env (never the dev database); document the test-secret setup in the README (TASK-049). | | |

## 3. Alternatives

- **ALT-001**: Use Entity Framework Core with MongoDB provider instead of raw MongoDB.Driver — rejected because EF Core MongoDB support is less mature and this demo aims to showcase direct MongoDB.Driver usage patterns
- **ALT-002**: Use Blazor WebAssembly for the frontend — rejected to keep frontend simple and framework-agnostic, showcasing SignalR interop with vanilla JS
- **ALT-003**: Use Redis Pub/Sub for real-time messaging instead of SignalR — rejected because SignalR is the idiomatic ASP.NET Core solution and supports WebSocket fallback automatically
- **ALT-004**: Use CRDT (Conflict-free Replicated Data Types) for conflict resolution — rejected as overengineering for a demo; server-authoritative ordering (strokes applied in the order the hub receives them) is sufficient
- **ALT-005**: Use Canvas API library (Fabric.js, Konva) — rejected to minimize dependencies and demonstrate raw Canvas 2D API usage

## 4. Dependencies

- **DEP-001**: MongoDB.Driver NuGet package (latest stable, currently ~3.x) — .NET driver for MongoDB
- **DEP-002**: MongoDB Atlas M0 free tier cluster (replica set, shared infrastructure)
- **DEP-003**: .NET 10.0 SDK (already configured)
- **DEP-004**: SignalR JavaScript client library (CDN-hosted, ~8.0.0)

## 5. Files

- **FILE-001**: `Program.cs` — Application entry point; configure services (MongoDB, SignalR, static files, identity middleware), map endpoints and hub
- **FILE-002**: `canvas.csproj` — Add MongoDB.Driver package reference
- **FILE-003**: `appsettings.json` — Add MongoDB database name (no secrets). Note: rate limit configuration is added by [feature-abuse-controls-1.md](./feature-abuse-controls-1.md)
- **FILE-004**: `Middleware/UserIdentityMiddleware.cs` — Assigns and validates server-side user identity via HttpOnly cookie
- **FILE-033**: `Middleware/ApiExceptionHandler.cs` — Global `IExceptionHandler` mapping exceptions to RFC 7807 Problem Details
- **FILE-034**: `Dtos/` — `sealed record` response DTOs (`BoardSnapshotResponse`, `StrokeResponse`, `PointResponse`) with XML doc comments and `DateTimeOffset` date/time fields (`BoardSummaryResponse` is defined by [feature-board-management-1.md](./feature-board-management-1.md))
- **FILE-035**: `canvas.http` — Add a request for the board snapshot REST endpoint, including a 404 error path (list/delete requests in [feature-board-management-1.md](./feature-board-management-1.md))
- **FILE-005**: `Models/Board.cs` — Board document model with embedded `ActiveStrokes` snapshot (ownership/membership fields added by the board administration plan; `HiddenRanges` added by the visibility moderation plan)
- **FILE-006**: `Models/BoardMember.cs` — (Defined in [feature-board-administration-1.md](./feature-board-administration-1.md); persisted membership is not part of the MVP)
- **FILE-007**: `Models/UserProfile.cs` — User document with self-chosen DisplayName (global, stored in Users collection)
- **FILE-008**: `Models/HiddenRange.cs` — (Defined in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md))
- **FILE-009**: `Models/Invite.cs` — (Defined in [feature-board-administration-1.md](./feature-board-administration-1.md))
- **FILE-010**: `Models/Stroke.cs` — Stroke value object (embedded in the Board snapshot)
- **FILE-011**: `Models/StrokeEvent.cs` — (Defined in [feature-replay-history-1.md](./feature-replay-history-1.md); the append-only event log is not part of the MVP)
- **FILE-012**: `Models/Point.cs` — Point value object with TimeOffset
- **FILE-013**: `Services/MongoDbContext.cs` — MongoDB connection and collection accessors (Boards, Users; `StrokeEvents` added by the replay-history plan, `Invites` by the board-administration plan)
- **FILE-014**: `Services/UserProfileService.cs` — User profile CRUD (display name management)
- **FILE-015**: `Services/BoardService.cs` — Board CRUD and snapshot mutations (ownership, membership, administration, forced-name, and visibility-moderation methods specified in their respective plans)
- **FILE-016**: `Services/InviteService.cs` — (Defined in [feature-board-administration-1.md](./feature-board-administration-1.md))
- **FILE-017**: `Services/StrokeEventService.cs` — (Defined in [feature-replay-history-1.md](./feature-replay-history-1.md); append-only event log persistence is not part of the MVP)
- **FILE-018**: `Hubs/WhiteboardHub.cs` — Strongly typed SignalR hub (`Hub<IWhiteboardClient>`) for real-time collaboration and name management
- **FILE-039**: `Hubs/IWhiteboardClient.cs` — Strongly typed client contract declaring all server→client callbacks (extended by the board-administration and visibility-moderation sub-plans)
- **FILE-019**: `wwwroot/index.html` — Main whiteboard page (Pico.css + SignalR via CDN) with display name editing
- **FILE-020**: `wwwroot/js/canvas.js` — Canvas drawing engine
- **FILE-021**: `wwwroot/js/connection.js` — SignalR client wrapper with identity via cookie
- **FILE-022**: `wwwroot/js/admin.js` — (Defined in [feature-board-administration-1.md](./feature-board-administration-1.md); no admin UI in the MVP)
- **FILE-023**: `wwwroot/js/app.js` — Application orchestrator with connected-users list and name editing
- **FILE-024**: `plan/feature-replay-history-1.md` — Extracted replay feature plan (history endpoint, animation engine, UI controls)
- **FILE-037**: `plan/feature-board-management-1.md` — Extracted board management plan (board discovery listing, board deletion)
- **FILE-038**: `plan/feature-abuse-controls-1.md` — Extracted abuse controls plan (IP-range and per-user rate limiting)
- **FILE-025**: `wwwroot/css/style.css` — App-specific layout only (canvas/overlay/sidebar); Pico.css (CDN) provides the base styling
- **FILE-026**: `.gitignore` — Exclude bin/, obj/, user-secrets, IDE files
- **FILE-027**: `README.md` — Project documentation with Atlas setup and name management
- **FILE-028**: `Tests/WhiteboardHubTests.cs` — Hub integration tests (MSTest)
- **FILE-040**: `Tests/Canvas.Tests.csproj` — MSTest test project (net10.0) referencing `canvas.csproj`
- **FILE-041**: `canvas.sln` — Solution referencing the web project and the test project
- **FILE-029**: `Tests/InviteFlowTests.cs` — (Specified in [feature-board-administration-1.md](./feature-board-administration-1.md))
- **FILE-030**: `Tests/MemberRemovalTests.cs` — (Specified in [feature-board-administration-1.md](./feature-board-administration-1.md))
- **FILE-031**: `Tests/DisplayNameTests.cs` — Pseudonym (self-chosen display name) integration tests (MSTest)
- **FILE-032**: `Tests/StrokeEventServiceTests.cs` — (Specified in [feature-replay-history-1.md](./feature-replay-history-1.md))

## 6. Testing

- **TEST-001**: Integration test — Joining a public board returns empty snapshot for new boards and populated snapshot for existing boards
- **TEST-002**: Integration test — Sending a stroke adds it to the board snapshot and broadcasts `StrokeReceived` to the group
- **TEST-004**: Board administration tests (invites, public/private access, member removal, owner-only enforcement) are specified in [feature-board-administration-1.md](./feature-board-administration-1.md)
- **TEST-011**: Unit test (MSTest) — BoardService snapshot logic returns the board's active strokes
- **TEST-012**: Visibility moderation tests (hide/restore/show-hidden) are specified in [feature-visibility-moderation-1.md](./feature-visibility-moderation-1.md)
- **TEST-016**: Manual test — Open two browser tabs on the same board URL, draw in one, verify stroke appears in the other within 100ms
- **TEST-017**: Manual test — Refresh page after drawing; verify all active strokes load instantly from snapshot
- **TEST-018**: Manual test — Open two tabs on the same board; move the pointer in one and verify the other shows a live remote cursor that tracks it; close a tab and verify its cursor disappears
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
- **ASSUMPTION-005**: Board ownership is not modeled in the MVP; it (permanent, non-transferable) is introduced by [feature-board-administration-1.md](./feature-board-administration-1.md)

## 8. Related Specifications / Further Reading

- [Replay & History View feature plan](./feature-replay-history-1.md) — extracted sub-plan for video-like replay functionality
- [Board Management feature plan](./feature-board-management-1.md) — extracted sub-plan for board discovery listing and board deletion endpoints
- [Board Administration feature plan](./feature-board-administration-1.md) — extracted sub-plan for ownership, membership, invites, public/private visibility, and member removal
- [Visibility Moderation feature plan](./feature-visibility-moderation-1.md) — extracted sub-plan for owner hide/restore of member contributions
- [Abuse Controls feature plan](./feature-abuse-controls-1.md) — extracted sub-plan for IP-range and per-user rate limiting
- [ASP.NET Core SignalR documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction)
- [MongoDB.Driver .NET documentation](https://www.mongodb.com/docs/drivers/csharp/current/)
- [HTML5 Canvas API reference](https://developer.mozilla.org/en-US/docs/Web/API/Canvas_API)
- [Event Sourcing pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/event-sourcing)
- [SignalR JavaScript client](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client)
- [Pico.css documentation](https://picocss.com/docs) — classless CSS framework used for default frontend styling
