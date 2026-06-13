# PRD: Collaborative Whiteboard MVP

## 1. Product overview

### 1.1 Document title and version

- PRD: Collaborative Whiteboard MVP
- Version: 1.0

### 1.2 Product summary

The collaborative whiteboard is a lightweight, browser-based drawing surface where several people can sketch together in real time. Anyone who opens a board's URL can immediately draw with a mouse, trackpad, or touch screen, and every stroke appears live on everyone else's screen within a fraction of a second. There is no sign-up, no password, and no name entry barrier: a visitor is given a stable anonymous identity automatically on first access and can optionally pick a display name to be recognized by.

The MVP targets casual, ad-hoc collaboration — a throwaway space for brainstorming, quick diagrams, or doodling together. Boards are created on demand simply by visiting a named URL, and the current drawing is persisted so that someone joining late sees everything already on the canvas. Live cursors show where each collaborator is pointing, making the session feel shared and present.

This document covers the MVP only: anonymous drawing, real-time stroke and cursor synchronization, late-join snapshot reconstruction, and self-chosen display names. Board ownership, private boards, invites, undo/replay history, moderation, and rate limiting are explicitly deferred to later, on-hold plans and are out of scope here.

## 2. Goals

### 2.1 Business goals

- Demonstrate a credible real-time collaboration experience with a minimal, easy-to-run stack.
- Validate that frictionless, no-login entry drives immediate engagement.
- Keep operating cost and complexity low (single server, managed database free tier).
- Provide a clean foundation that later features (ownership, history, moderation) can layer onto without rework.

### 2.2 User goals

- Start or join a shared drawing in seconds, with no account or setup.
- See collaborators' strokes and cursors update live, so the session feels co-present.
- Have the current drawing preserved so late joiners and reconnecting users see the full picture.
- Be recognizable to others via an optional, freely chosen display name.
- Draw reliably from either a desktop or a touch device.

### 2.3 Non-goals

- Authentication, accounts, or login of any kind (identity is anonymous and automatic).
- Board ownership, membership, private boards, or invite links.
- Undo/redo, time-travel replay, or per-stroke history browsing.
- Moderation, content hiding, or abuse/rate-limit controls.
- Rich drawing tools beyond freehand strokes (no shapes, text, images, or layers).
- Cross-device identity continuity (identity is per-browser).

## 3. User personas

### 3.1 Key user types

- Anonymous collaborator (the only actor in the MVP): a person who opens a board URL to draw together with others.

### 3.2 Basic persona details

- **Anonymous collaborator**: An unauthenticated visitor who reaches a board by URL. They want to draw immediately and see others' contributions live. They may set a display name so others can tell who is who, but they never create an account. Their identity persists within the same browser via a server-issued cookie.

### 3.3 Role-based access

- **Anonymous collaborator**: May create or join any board by visiting its URL, draw strokes, move their cursor, set or change their own display name, and view the live snapshot and the list of currently connected users. All boards are open to all collaborators in the MVP; there are no elevated roles.

## 4. Functional requirements

- **Automatic anonymous identity** (Priority: high)

  - On first access, the server assigns a stable, unique identity and stores it in an HttpOnly cookie. The client never supplies or chooses its own identity.
  - The same browser retains the identity across page reloads and reconnects.

- **Board creation and joining by URL** (Priority: high)

  - Visiting a named board URL joins that board, creating it on first visit.
  - All boards are public and joinable by anyone with the URL.

- **Freehand drawing** (Priority: high)

  - A collaborator can draw freehand strokes with a selectable color and line width using mouse, trackpad, or touch input.

- **Real-time stroke synchronization** (Priority: high)

  - A completed stroke is broadcast to all other collaborators on the same board and rendered on their canvases in near real time.

- **Live cursor presence** (Priority: medium)

  - Each collaborator's pointer position is shown to others as a labeled remote cursor, updated continuously and removed when they leave.

- **Late-join snapshot reconstruction** (Priority: high)

  - A collaborator joining an existing board immediately receives and renders the current drawing (all active strokes).

- **Self-chosen display name** (Priority: medium)

  - A collaborator can set or change a display name at any time; the name is shown to other collaborators and updates live.

- **Connected-users list** (Priority: medium)

  - Collaborators can see who is currently connected to the board, by display name.

- **Persistence of the current drawing** (Priority: high)

  - Strokes are persisted so the drawing survives server restarts and is available to late joiners and reconnecting clients.

- **Resilient reconnection** (Priority: medium)

  - On a dropped connection, the client transparently reconnects and re-synchronizes; any unconfirmed strokes are re-sent without producing duplicates.

## 5. User experience

### 5.1 Entry points & first-time user flow

- A collaborator arrives via a shared board URL (e.g., from a chat message or pasted link).
- On first access the server silently issues an anonymous identity cookie; no prompt or sign-up appears.
- The board loads with the current drawing already rendered, and the collaborator can draw right away.
- An optional display-name field invites them to identify themselves, defaulting to an anonymous label if skipped.

### 5.2 Core experience

- **Open a board**: The collaborator visits a board URL and the canvas appears immediately.

  - Instant, login-free entry removes friction and gets them drawing in seconds.

- **Draw a stroke**: They press and drag to draw; the stroke renders locally as they move.

  - Local rendering makes drawing feel instant even before the server confirms persistence.

- **See it sync**: The finished stroke appears on every other collaborator's canvas almost immediately.

  - Fast propagation makes the board feel genuinely shared and live.

- **See others**: Remote cursors and incoming strokes from others appear continuously, each tied to a display name.

  - Visible presence and attribution make collaboration feel social and coordinated.

- **Identify yourself**: They type a display name, which others see update live next to their cursor and in the users list.

  - Lightweight, optional identity lets people coordinate without accounts.

### 5.3 Advanced features & edge cases

- A collaborator joining a busy board mid-session sees the complete current drawing, not a blank canvas.
- A brief network drop reconnects automatically and re-synchronizes without losing or duplicating strokes.
- Two collaborators drawing simultaneously both have their strokes preserved (no lost updates).
- A collaborator who closes the tab is removed from the connected-users list and their cursor disappears for others.
- Touch and mouse input produce equivalent strokes.

### 5.4 UI/UX highlights

- Full-viewport canvas with a minimal toolbar overlay (color, line width) and an unobtrusive connected-users sidebar.
- Default styling via a classless CSS framework so the focus stays on the drawing surface.
- No modal sign-in, no account management, no setup screens.
- Remote cursors are labeled and lightweight so they aid presence without cluttering the canvas.

## 6. Narrative

A collaborator receives a board link in a group chat and clicks it. The whiteboard opens instantly, already showing the diagram their teammates started, and without any prompt to log in they begin sketching — their strokes appearing on everyone's screen as fast as they can draw. They see their teammates' cursors moving and their names beside each contribution, type in a display name so others know who they are, and keep drawing together. When their wifi blips, the board reconnects on its own and nothing they drew is lost. The whole experience feels like sharing a physical whiteboard: immediate, present, and effortless.

## 7. Success metrics

### 7.1 User-centric metrics

- Time from opening a board URL to being able to draw (target: under two seconds on a typical connection).
- Perceived stroke-sync latency between collaborators (target: sub-second under normal conditions).
- Share of sessions in which a late joiner successfully sees the existing drawing.
- Share of sessions that experience a reconnect yet lose or duplicate no strokes.

### 7.2 Business metrics

- Number of distinct boards created.
- Number of multi-collaborator sessions (two or more concurrent participants on a board).
- Average concurrent collaborators per active board.

### 7.3 Technical metrics

- Real-time message round-trip latency under load.
- Snapshot fetch time for boards of varying stroke counts.
- Server stability (uptime, error rate) during concurrent multi-board use.
- Successful automatic-reconnect rate after transient disconnects.

## 8. Technical considerations

### 8.1 Integration points

- Real-time transport over a SignalR hub for stroke, cursor, join, and presence events.
- A REST endpoint serving the current board snapshot for initial load and reconnection.
- A managed MongoDB Atlas cluster as the sole persistence layer.
- A static, framework-free frontend (HTML5 Canvas plus vanilla JavaScript).

### 8.2 Data storage & privacy

- Stored data is limited to drawings, anonymous identity identifiers, and self-chosen display names; no personal data, emails, or credentials are collected.
- Identity is an opaque server-assigned value in an HttpOnly cookie, treated as a secret and never exposed in URLs or logs.
- The database connection string is kept outside source control (in user secrets for development).

### 8.3 Scalability & performance

- The MVP targets single-server deployment at demo scale; no real-time backplane is assumed.
- The current drawing is kept as a materialized snapshot for fast late-join loading.
- Stroke persistence uses atomic appends to avoid lost updates under concurrent drawing.

### 8.4 Potential challenges

- Real-time connections may be disrupted by proxies or flaky networks; handled by automatic reconnect and idempotent stroke re-sending.
- Very large drawings could slow initial load; acceptable at MVP scale but a future concern.
- Anonymous, open boards mean anyone with a URL can draw; abuse controls are deferred to a later plan.

## 9. Milestones & sequencing

### 9.1 Project estimate

- Small: roughly 2–3 weeks for a working MVP.

### 9.2 Team size & composition

- 1–2 people: one full-stack developer, optionally supported by a designer for canvas/toolbar polish.

### 9.3 Suggested phases

- **1**: Infrastructure and identity — project setup, database connection, real-time hub, and automatic anonymous identity (about 1 week).

  - Key deliverables: running server, persistence wired up, identity cookie issued on first access.

- **2**: Core drawing and sync — freehand drawing, real-time stroke broadcast, persistence, and snapshot load (about 1 week).

  - Key deliverables: collaborators can draw and see each other's strokes live; late joiners see the current drawing.

- **3**: Presence and resilience — live cursors, display names, connected-users list, and reconnect with idempotent re-send (about 0.5 week).

  - Key deliverables: labeled remote cursors, editable display names, robust reconnection.

## 10. User stories

### 10.1. Obtain an anonymous identity automatically

- **ID**: GH-001
- **Description**: As an anonymous collaborator, I want to be given a stable identity automatically on first access so that I can participate without signing up or logging in.
- **Acceptance criteria**:

  - On first access, the server issues a unique identity stored in an HttpOnly cookie.
  - The same browser retains the same identity across reloads and reconnects.
  - The client cannot set, spoof, or override its own identity; the server assigns and validates it.
  - No login, registration, or name-entry step is required to begin.

### 10.2. Create or join a board by URL

- **ID**: GH-002
- **Description**: As an anonymous collaborator, I want to open a board by visiting its named URL so that I can start or continue a shared drawing.
- **Acceptance criteria**:

  - Visiting a board URL that does not yet exist creates the board and joins it.
  - Visiting a board URL that already exists joins the existing board.
  - All boards are joinable by anyone with the URL.
  - Concurrent first-time visitors to the same new board URL all join one and the same board.

### 10.3. Draw freehand strokes

- **ID**: GH-003
- **Description**: As an anonymous collaborator, I want to draw freehand strokes with a chosen color and width so that I can contribute to the drawing.
- **Acceptance criteria**:

  - The collaborator can select a color and a line width before or between strokes.
  - Pressing and dragging produces a stroke rendered on the local canvas as it is drawn.
  - Both mouse/trackpad and touch input produce equivalent strokes.

### 10.4. See others' strokes in real time

- **ID**: GH-004
- **Description**: As an anonymous collaborator, I want other collaborators' completed strokes to appear on my canvas live so that we can draw together.
- **Acceptance criteria**:

  - A stroke completed by one collaborator is broadcast to all others on the same board.
  - Received strokes render on each other collaborator's canvas in near real time.
  - A collaborator does not receive a duplicate of a stroke they just drew.

### 10.5. See late-join snapshot of the current drawing

- **ID**: GH-005
- **Description**: As an anonymous collaborator joining an existing board, I want to immediately see the drawing already in progress so that I have full context.
- **Acceptance criteria**:

  - On joining, the collaborator receives and renders all active strokes currently on the board.
  - The rendered order of strokes matches the order in which they were originally added.
  - Joining a board with no strokes yet shows an empty canvas without error.

### 10.6. See collaborators' live cursors

- **ID**: GH-006
- **Description**: As an anonymous collaborator, I want to see where other collaborators are pointing so that the session feels co-present.
- **Acceptance criteria**:

  - Each other collaborator's pointer position is shown as a remote cursor, updated continuously as they move.
  - A remote cursor is associated with that collaborator's display name.
  - A collaborator's remote cursor disappears for others when they leave the board.
  - Cursor positions are ephemeral and not persisted.

### 10.7. Set and change a display name

- **ID**: GH-007
- **Description**: As an anonymous collaborator, I want to choose and change a display name so that others can recognize me.
- **Acceptance criteria**:

  - The collaborator can set a display name at any time, and it is shown to other collaborators.
  - Changing the display name updates it live for other collaborators (in the users list and beside their cursor).
  - If no name is chosen, a default anonymous label is shown.

### 10.8. See who is currently connected

- **ID**: GH-008
- **Description**: As an anonymous collaborator, I want to see the list of collaborators currently on the board so that I know who I am drawing with.
- **Acceptance criteria**:

  - The board shows the display names of all currently connected collaborators.
  - The list updates live as collaborators join and leave.

### 10.9. Have the drawing persist

- **ID**: GH-009
- **Description**: As an anonymous collaborator, I want the drawing to be saved so that it is not lost between visits or server restarts.
- **Acceptance criteria**:

  - Each completed stroke is persisted to the database.
  - The persisted drawing is available to late joiners and after a server restart.
  - Two collaborators drawing at the same time both have their strokes preserved (no lost updates).

### 10.10. Reconnect without losing or duplicating work

- **ID**: GH-010
- **Description**: As an anonymous collaborator, I want a dropped connection to recover automatically without losing or duplicating my strokes so that brief network issues do not disrupt me.
- **Acceptance criteria**:

  - On a dropped connection, the client reconnects automatically and re-synchronizes the current drawing.
  - Any strokes not yet confirmed are re-sent on reconnect.
  - A re-sent stroke is stored and broadcast at most once (no duplicates appear for anyone).

### 10.11. Trust that identity cannot be impersonated

- **ID**: GH-011
- **Description**: As an anonymous collaborator, I want the server to control identity so that no one can impersonate me or act as another collaborator.
- **Acceptance criteria**:

  - Strokes and presence are attributed using the server-assigned identity, never a client-supplied one.
  - A client cannot cause its contributions to be attributed to another collaborator's identity.
  - The identity cookie is HttpOnly and is not exposed to client scripts, URLs, or logs.
