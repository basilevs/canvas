import { createWhiteboardCanvas, colorFromUserId } from './canvas.js';
import { createWhiteboardConnection } from './connection.js';
import { fetchAllHistory, foldEvents, lastEventTimestamp } from './history.js';
import { ReplayEngine } from './replay.js';

const boardForm = document.getElementById('board-form');
const boardInput = document.getElementById('board-input');
const displayNameForm = document.getElementById('display-name-form');
const displayNameInput = document.getElementById('display-name-input');
const colorInput = document.getElementById('color-input');
const widthInput = document.getElementById('width-input');
const canvasElement = document.getElementById('whiteboard-canvas');
const usersList = document.getElementById('users-list');
const statusText = document.getElementById('status-text');
const btnUndo = document.getElementById('btn-undo');
const btnReplay = document.getElementById('btn-replay');
const btnReplayPlayPause = document.getElementById('btn-replay-playpause');
const btnReplayStop = document.getElementById('btn-replay-stop');
const replaySpeedSelect = document.getElementById('replay-speed');
const replayScrubber = document.getElementById('replay-scrubber');
const replayTimestamp = document.getElementById('replay-timestamp');
const replayControls = document.getElementById('replay-controls');
const replayOnlyControls = document.querySelectorAll('.replay-only');
const liveOnlyControls = document.querySelectorAll('.live-only');

const state = {
  boardName: getBoardNameFromPath(),
  currentUserId: null,
  knownNames: new Map(),
  connectedUsers: new Map(),
  replayEngine: null,
  replayPaused: false,
  // True once playback has reached the end: the engine is parked at the final
  // frame and still selectable via the controls, but the canvas has been handed
  // back to the live board so remote strokes resume showing.
  replayEnded: false
};

const whiteboardCanvas = createWhiteboardCanvas(canvasElement, {
  onStrokeCompleted: stroke => {
    if (state.boardName) {
      connection.sendStroke(state.boardName, stroke);
    }
  },
  onCursorMoved: (x, y) => {
    if (state.boardName) {
      connection.moveCursor(state.boardName, x, y);
    }
  }
});

const connection = createWhiteboardConnection({
  onJoined: profile => {
    state.currentUserId = profile.userId ?? profile.UserId;
    // Overwrite the input with the caller's last known name on every join,
    // including reconnects. Today the name is shared across all boards; once
    // moderation lands it becomes board-local and a moderator can override it.
    displayNameInput.value = profile.displayName ?? profile.DisplayName ?? '';
  },
  onConnectedUsers: users => {
    syncUsers(users);
    updateStatus(`Joined ${state.boardName}`);
  },
  onStrokeReceived: stroke => {
    whiteboardCanvas.commitStroke(stroke);
    registerKnownUser(stroke.userId ?? stroke.UserId, null);
  },
  onStrokeRemoved: strokeId => {
    whiteboardCanvas.removeStroke(strokeId);
  },
  onUserJoined: (userId, displayName) => {
    registerKnownUser(userId, displayName);
    renderUsers();
  },
  onUserLeft: userId => {
    state.connectedUsers.delete(userId);
    renderUsers();
  },
  onUserRenamed: (userId, name) => {
    registerKnownUser(userId, name);
    renderUsers();
  },
  onCursorMoved: (userId, x, y) => {
    if (!state.connectedUsers.has(userId)) {
      state.connectedUsers.set(userId, state.knownNames.get(userId) ?? 'Anonymous');
      renderUsers();
    }

    whiteboardCanvas.setRemoteCursor(userId, { x, y });
  },
  onStateChanged: message => updateStatus(message)
});

configureBoardSwitching();
configureDisplayNameEditing();
configureDrawingTools();
configureUndo();
configureReplay();

if (!state.boardName) {
  updateStatus('Open a board URL such as /boards/example');
} else {
  boardInput.value = state.boardName;
  await startBoard();
}

function configureBoardSwitching() {
  boardForm.addEventListener('submit', event => {
    event.preventDefault();
    const nextBoard = boardInput.value.trim();
    if (!nextBoard) {
      return;
    }

    window.location.assign(`/boards/${encodeURIComponent(nextBoard)}`);
  });
}

function configureDisplayNameEditing() {
  displayNameForm.addEventListener('submit', event => {
    event.preventDefault();
    commitDisplayName();
  });

  displayNameInput.addEventListener('blur', commitDisplayName);
  displayNameInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      commitDisplayName();
    }
  });
}

function configureDrawingTools() {
  colorInput.addEventListener('input', () => {
    whiteboardCanvas.setDrawingStyle(colorInput.value, Number(widthInput.value));
  });

  widthInput.addEventListener('input', () => {
    whiteboardCanvas.setDrawingStyle(colorInput.value, Number(widthInput.value));
  });

  whiteboardCanvas.setDrawingStyle(colorInput.value, Number(widthInput.value));
}

function configureUndo() {
  btnUndo.addEventListener('click', async () => {
    if (!state.boardName || state.replayEngine) {
      return;
    }

    try {
      await connection.undoLastStroke(state.boardName);
    } catch (error) {
      updateStatus(error?.message ?? 'Failed to undo');
    }
  });
}

function configureReplay() {
  btnReplay.addEventListener('click', () => startReplay());

  btnReplayPlayPause.addEventListener('click', () => {
    if (!state.replayEngine) {
      return;
    }

    if (state.replayPaused) {
      reenterReplayIfEnded();
      state.replayEngine.resume();
      state.replayPaused = false;
      btnReplayPlayPause.textContent = '⏸ Pause';
    } else {
      state.replayEngine.pause();
      state.replayPaused = true;
      btnReplayPlayPause.textContent = '▶ Play';
    }
  });

  btnReplayStop.addEventListener('click', () => {
    state.replayEngine?.stop();
  });

  replayScrubber.addEventListener('input', () => {
    if (!state.replayEngine) {
      return;
    }

    reenterReplayIfEnded();
    state.replayEngine.seek(Number(replayScrubber.value));
  });

  replaySpeedSelect.addEventListener('change', () => {
    state.replayEngine?.setSpeed(Number(replaySpeedSelect.value));
  });
}

// After playback reached the end the canvas went live (replayEnded). Playing or
// scrubbing again must hand the canvas back to the ReplayEngine before it draws,
// otherwise live rendering and the engine would fight over the shared context.
function reenterReplayIfEnded() {
  if (!state.replayEnded) {
    return;
  }

  state.replayEnded = false;
  whiteboardCanvas.setReplaying(true);
}

async function startReplay() {
  if (state.replayEngine || !state.boardName) {
    return;
  }

  const context = canvasElement.getContext('2d');
  const engine = new ReplayEngine(context, {
    gapThresholdMs: 3000,
    speed: Number(replaySpeedSelect.value)
  });

  engine.onProgress = ({ ratio, timestamp }) => {
    replayScrubber.value = String(ratio);
    replayTimestamp.textContent = formatTimestamp(timestamp);
  };
  engine.onStop = () => exitReplay();
  // Reaching the end of the history means we have caught up to "now": hand the
  // canvas back to the live board so remote strokes resume showing and the board
  // updates incrementally again, while keeping the replay controls visible so the
  // user can still scrub back or replay. The engine stays parked at the final
  // frame; playing or scrubbing again re-enters replay mode (see configureReplay).
  engine.onEnd = () => {
    state.replayPaused = true;
    state.replayEnded = true;
    btnReplayPlayPause.textContent = '▶ Play';
    resyncLiveCanvas();
  };

  state.replayEngine = engine;
  state.replayPaused = false;
  btnReplayPlayPause.textContent = '⏸ Pause';

  try {
    const history = await fetchAllHistory(state.boardName);
    engine.computeTimeline(history);
  } catch (error) {
    state.replayEngine = null;
    updateStatus(error?.message ?? 'Failed to load replay');
    return;
  }

  refreshToolbar();
  whiteboardCanvas.setReplaying(true);
  engine.play();
}

async function exitReplay() {
  state.replayEngine = null;
  state.replayPaused = false;
  state.replayEnded = false;
  refreshToolbar();

  try {
    await resyncLiveCanvas();
  } finally {
    whiteboardCanvas.setReplaying(false);
  }
}

// Hands the canvas back to the live board: resync from the authoritative log,
// then re-enable live rendering and input. Shared by Stop (exitReplay) and the
// end-of-playback transition (engine.onEnd).
async function resyncLiveCanvas() {
  try {
    const history = await fetchAllHistory(state.boardName);
    whiteboardCanvas.setSnapshot(foldEvents(history));
  } catch (error) {
    updateStatus(error?.message ?? 'Failed to resync board');
  }
}

// Reflects the current replay state in the toolbar. There are three modes:
//   - live:           no engine; drawing tools shown, replay controls hidden.
//   - actively replaying / ended-but-live: an engine is present, so the replay
//                     controls are shown and the live-only drawing tools (color,
//                     width, undo) are hidden until the user fully exits replay.
function refreshToolbar() {
  const replayMode = state.replayEngine != null;

  replayControls.hidden = !replayMode;
  // Toggle each element's `hidden` attribute individually. A tidier approach —
  // injecting a `.replay-only { display: none }` <style> and flipping its
  // sheet.disabled — was reverted because the inline stylesheet violates our
  // strict CSP (style-src 'self'; no inline styles). See commit 542ed13.
  for (const control of replayOnlyControls) {
    control.hidden = !replayMode;
  }

  // Live-only tools (color, width, undo) are meaningless whenever a replay is
  // active — the local drawing they configure is unavailable until replay exits.
  for (const control of liveOnlyControls) {
    control.hidden = replayMode;
  }

  btnReplay.hidden = replayMode;
}

function formatTimestamp(timestamp) {
  if (!timestamp) {
    return '';
  }

  const date = new Date(timestamp);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
}

async function startBoard() {
  try {
    // Load the full, cacheable history first, render it, then atomically join:
    // the server replays only the newer tail through the same live channel,
    // de-duplicated by stroke id, so nothing is missed or duplicated.
    const history = await fetchAllHistory(state.boardName);
    whiteboardCanvas.setSnapshot(foldEvents(history));
    const sinceTimestamp = lastEventTimestamp(history);

    await connection.start();
    await connection.joinBoard(state.boardName, sinceTimestamp);
  } catch (error) {
    updateStatus(error?.message ?? 'Failed to connect');
  }
}

function syncUsers(users) {
  state.connectedUsers.clear();
  for (const user of users ?? []) {
    const userId = user.userId ?? user.UserId;
    const displayName = user.displayName ?? user.DisplayName ?? 'Anonymous';
    state.connectedUsers.set(userId, displayName);
    state.knownNames.set(userId, displayName);
  }

  renderUsers();
}

function registerKnownUser(userId, displayName) {
  if (!userId) {
    return;
  }

  if (displayName) {
    state.knownNames.set(userId, displayName);
  }

  state.connectedUsers.set(userId, displayName ?? state.knownNames.get(userId) ?? 'Anonymous');
}

function renderUsers() {
  usersList.replaceChildren();
  for (const [userId, displayName] of state.connectedUsers.entries()) {
    const item = document.createElement('li');
    item.dataset.userId = userId;

    const swatch = document.createElement('span');
    swatch.className = 'user-swatch';
    // Color is per-user and must match the cursor color drawn on the canvas,
    // so it is set via the CSSOM rather than a static stylesheet rule.
    swatch.style.backgroundColor = colorFromUserId(userId);
    item.appendChild(swatch);

    item.appendChild(document.createTextNode(displayName));
    usersList.appendChild(item);
  }
}

async function commitDisplayName() {
  const displayName = displayNameInput.value.trim();
  if (!displayName) {
    return;
  }

  try {
    await connection.setDisplayName(displayName);
  } catch (error) {
    updateStatus(error?.message ?? 'Failed to update display name');
  }
}

function updateStatus(message) {
  statusText.textContent = message;
}

function getBoardNameFromPath() {
  const prefix = '/boards/';
  if (!window.location.pathname.startsWith(prefix)) {
    return '';
  }

  return decodeURIComponent(window.location.pathname.slice(prefix.length));
}
