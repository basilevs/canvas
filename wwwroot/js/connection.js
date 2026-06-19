export function createWhiteboardConnection(handlers = {}) {
  return new WhiteboardConnection(handlers);
}

class WhiteboardConnection {
  constructor(handlers) {
    this.handlers = handlers;
    this.boardName = null;
    this.sinceTimestamp = new Date(0).toISOString();
    this.pendingStrokes = new Map();
    this.cursorMoveInFlight = false;
    this.pendingCursorMove = null;
    this.started = false;
    this.restartPromise = null;
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/hub/whiteboard')
      .withAutomaticReconnect()
      .build();

    this.registerHandlers();
  }

  registerHandlers() {
    this.connection.on('ConnectedUsers', users => {
      this.handlers.onConnectedUsers?.(users);
    });

    this.connection.on('StrokeReceived', stroke => {
      const strokeId = stroke.id ?? stroke.Id;
      this.pendingStrokes.delete(strokeId);
      this.trackTimestamp(stroke.timestamp ?? stroke.Timestamp);
      this.handlers.onStrokeReceived?.(stroke);
    });

    this.connection.on('StrokeRemoved', strokeId => {
      this.handlers.onStrokeRemoved?.(strokeId);
    });

    this.connection.on('UserJoined', (userId, displayName) => {
      this.handlers.onUserJoined?.(userId, displayName);
    });

    this.connection.on('UserLeft', userId => {
      this.handlers.onUserLeft?.(userId);
    });

    this.connection.on('UserRenamed', (userId, name) => {
      this.handlers.onUserRenamed?.(userId, name);
    });

    this.connection.on('CursorMoved', (userId, x, y) => {
      this.handlers.onCursorMoved?.(userId, x, y);
    });

    this.connection.onreconnecting(() => {
      this.handlers.onStateChanged?.('Reconnecting…');
    });

    this.connection.onreconnected(async () => {
      this.handlers.onStateChanged?.('Reconnected');
      if (this.boardName) {
        // Re-subscribe and catch up only the tail missed while disconnected,
        // using the latest event timestamp we have already applied.
        await this.joinBoard(this.boardName, this.sinceTimestamp);
      }

      await this.resendPendingStrokes();
    });

    this.connection.onclose(() => {
      this.handlers.onStateChanged?.('Disconnected');
    });
  }

  async start() {
    await this.connection.start();
    this.started = true;
    this.handlers.onStateChanged?.('Connected');
  }

  async joinBoard(boardName, sinceTimestamp) {
    this.boardName = boardName;
    if (sinceTimestamp) {
      this.trackTimestamp(sinceTimestamp);
    }

    await this.connection.invoke('JoinBoard', boardName, this.sinceTimestamp);
  }

  trackTimestamp(timestamp) {
    if (timestamp && timestamp > this.sinceTimestamp) {
      this.sinceTimestamp = timestamp;
    }
  }

  async leaveBoard(boardName = this.boardName) {
    if (!boardName) {
      return;
    }

    await this.connection.invoke('LeaveBoard', boardName);
  }

  async setDisplayName(name) {
    await this.connection.invoke('SetDisplayName', name);
  }

  async sendStroke(boardName, stroke) {
    if (!this.started) {
      return;
    }

    this.pendingStrokes.set(stroke.id, stroke);
    try {
      await this.connection.invoke('SendStroke', boardName, stroke);
    } catch (error) {
      this.pendingStrokes.delete(stroke.id);
      throw error;
    }
  }

  async moveCursor(boardName, x, y) {
    if (!this.started) {
      return;
    }

    this.pendingCursorMove = { boardName, x, y };

    // Cursor movement is frequent user activity, so use it as a trigger to
    // recover the connection once SignalR's automatic reconnect has given up and
    // gone fully Disconnected. Skip sending this sample; movement resumes on the
    // next move once reconnected.
    if (this.connection.state !== signalR.HubConnectionState.Connected) {
      await this.ensureConnected();
      return;
    }

    if (this.cursorMoveInFlight) {
      return;
    }

    this.cursorMoveInFlight = true;
    try {
      // While one invoke is in flight, keep only the latest sampled position.
      while (this.pendingCursorMove) {
        const nextCursorMove = this.pendingCursorMove;
        this.pendingCursorMove = null;

        try {
          await this.connection.invoke('MoveCursor', nextCursorMove.boardName, nextCursorMove.x, nextCursorMove.y);
        } catch {
          // Cursor updates are best-effort; a transient invoke failure is ignored and
          // the next move will detect the dropped connection and recover.
        }
      }
    } finally {
      this.cursorMoveInFlight = false;
    }
  }

  // Coalesces concurrent recovery attempts (cursor moves fire rapidly) into a
  // single in-flight restart. Only restarts from a fully Disconnected state;
  // while automatic reconnect is still Connecting/Reconnecting we let it finish.
  async ensureConnected() {
    if (this.connection.state === signalR.HubConnectionState.Connected) {
      return true;
    }
    if (this.connection.state !== signalR.HubConnectionState.Disconnected) {
      return false;
    }

    if (!this.restartPromise) {
      this.restartPromise = this.restart().finally(() => {
        this.restartPromise = null;
      });
    }

    try {
      await this.restartPromise;
    } catch {
      // Surfaced via onStateChanged below; the next cursor move retries.
    }

    return this.connection.state === signalR.HubConnectionState.Connected;
  }

  async restart() {
    this.handlers.onStateChanged?.('Reconnecting…');
    try {
      await this.connection.start();
      this.started = true;
      this.handlers.onStateChanged?.('Reconnected');
      if (this.boardName) {
        // Re-subscribe and catch up only the tail missed while disconnected.
        await this.joinBoard(this.boardName, this.sinceTimestamp);
      }
      await this.resendPendingStrokes();
    } catch (error) {
      this.handlers.onStateChanged?.('Disconnected');
      throw error;
    }
  }

  async undoLastStroke(boardName = this.boardName) {
    if (!this.started || !boardName) {
      return;
    }

    await this.connection.invoke('UndoLastStroke', boardName);
  }

  async resendPendingStrokes() {
    if (!this.boardName || this.pendingStrokes.size === 0) {
      return;
    }

    for (const stroke of this.pendingStrokes.values()) {
      await this.connection.invoke('SendStroke', this.boardName, stroke);
    }
  }
}
