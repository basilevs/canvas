import { createWhiteboardCanvas } from './canvas.js';
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

const state = {
  boardName: getBoardNameFromPath(),
  currentUserId: null,
  knownNames: new Map(),
  connectedUsers: new Map(),
  replayEngine: null,
  replayPaused: false
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
    state.replayEngine?.seek(Number(replayScrubber.value));
  });

  replaySpeedSelect.addEventListener('change', () => {
    state.replayEngine?.setSpeed(Number(replaySpeedSelect.value));
  });
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

  state.replayEngine = engine;
  state.replayPaused = false;
  btnReplayPlayPause.textContent = '⏸ Pause';

  try {
    await engine.loadHistory(state.boardName);
  } catch (error) {
    state.replayEngine = null;
    updateStatus(error?.message ?? 'Failed to load replay');
    return;
  }

  setReplayUiVisible(true);
  whiteboardCanvas.setReplaying(true);
  engine.play();
}

async function exitReplay() {
  setReplayUiVisible(false);
  state.replayEngine = null;
  state.replayPaused = false;

  // Resync the live canvas from the authoritative log before handing control back.
  try {
    const history = await fetchAllHistory(state.boardName);
    whiteboardCanvas.setReplaying(false);
    whiteboardCanvas.setSnapshot(foldEvents(history));
  } catch (error) {
    whiteboardCanvas.setReplaying(false);
    updateStatus(error?.message ?? 'Failed to resync board');
  }
}

function setReplayUiVisible(visible) {
  replayControls.hidden = !visible;
  // Toggle each element's `hidden` attribute individually. A tidier approach —
  // injecting a `.replay-only { display: none }` <style> and flipping its
  // sheet.disabled — was reverted because the inline stylesheet violates our
  // strict CSP (style-src 'self'; no inline styles). See commit 542ed13.
  for (const control of replayOnlyControls) {
    control.hidden = !visible;
  }

  btnReplay.hidden = visible;
  btnUndo.disabled = visible;
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
    item.textContent = displayName;
    item.dataset.userId = userId;
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
    state.knownNames.set(state.currentUserId ?? 'self', displayName);
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
