// Unit tests for the client-side ReplayEngine (TASK-034). The engine no longer
// draws; it translates a board's history timeline into canvas commands
// (setSnapshot / commitStroke / removeStroke / setActiveStrokes). These tests
// drive a fake board that records those commands and assert the resulting
// committed + in-progress (active) stroke sets. drawStrokePath rendering is
// covered separately at the bottom.
//
// Run: node --test Tests/ReplayEngineTests.mjs

import test from 'node:test';
import assert from 'node:assert/strict';

const { ReplayEngine } = await import('../wwwroot/js/replay.js');
const { drawStrokePath } = await import('../wwwroot/js/canvas.js');

function strokeId(stroke) {
  return stroke.id ?? stroke.Id;
}

// A fake WhiteboardCanvas that records the commands the engine issues, exposing
// the committed set, the current in-progress (active) set, and call logs so a
// test can assert that completed strokes are committed exactly once.
function makeBoard() {
  const committed = new Map();
  let active = [];
  return {
    commits: [],
    removes: [],
    snapshots: [],
    setSnapshot(strokes) {
      committed.clear();
      for (const stroke of strokes) {
        committed.set(strokeId(stroke), stroke);
      }
      active = [];
      this.snapshots.push([...committed.keys()]);
    },
    commitStroke(stroke) {
      committed.set(strokeId(stroke), stroke);
      this.commits.push(strokeId(stroke));
    },
    removeStroke(id) {
      committed.delete(id);
      this.removes.push(id);
    },
    setActiveStrokes(strokes) {
      active = strokes.slice();
    },
    committedIds() {
      return [...committed.keys()];
    },
    activeIds() {
      return active.map(strokeId);
    },
    activePoints(id) {
      const found = active.find(stroke => strokeId(stroke) === id);
      return found ? found.points : null;
    }
  };
}

function addEvent(id, timestamp, lastOffsetMs) {
  return {
    type: 'Add',
    timestamp,
    stroke: {
      id,
      color: '#000000',
      width: 4,
      points: [
        { x: 0, y: 0, timeOffset: 0 },
        { x: 10, y: 10, timeOffset: lastOffsetMs }
      ]
    }
  };
}

function newEngine() {
  const board = makeBoard();
  const engine = new ReplayEngine(board, { gapThresholdMs: 3000, speed: 1 });
  return { engine, board };
}

test('computeTimeline compresses inactivity gaps longer than the threshold', () => {
  const { engine } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    // 100s real gap -> must be clamped to the 3000ms threshold.
    addEvent('s2', '2025-01-01T00:01:40.000Z', 1000)
  ];

  engine.computeTimeline(events);

  const [first, second] = engine.timeline;
  assert.equal(first.startMs, 0);
  assert.equal(second.startMs, 3000, 'gap should be clamped to gapThresholdMs');
});

test('computeTimeline preserves short gaps below the threshold', () => {
  const { engine } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    // 1.5s real gap -> kept as-is (under the 3000ms threshold).
    addEvent('s2', '2025-01-01T00:00:01.500Z', 1000)
  ];

  engine.computeTimeline(events);

  assert.equal(engine.timeline[1].startMs, 1500);
});

test('totalDurationMs accounts for the final stroke duration', () => {
  const { engine } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    addEvent('s2', '2025-01-01T00:01:40.000Z', 1000)
  ];

  engine.computeTimeline(events);

  // s2 starts at 3000 (clamped gap) and lasts 1000 -> total 4000.
  assert.equal(engine.totalDurationMs, 4000);
});

test('seek to the midpoint commits past strokes and shows no in-progress stroke', () => {
  const { engine, board } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    addEvent('s2', '2025-01-01T00:01:40.000Z', 1000)
  ];
  engine.computeTimeline(events);

  let lastProgress = null;
  engine.onProgress = (progress) => {
    lastProgress = progress;
  };

  // totalDurationMs = 4000, so 2000ms is the midpoint: after s1 finishes (1000)
  // and before s2 begins (3000).
  engine.seekTo(engine.totalDurationMs * 0.5);

  assert.deepEqual(board.committedIds(), ['s1'], 'the finished stroke is committed');
  assert.deepEqual(board.activeIds(), [], 'nothing is mid-animation at the midpoint');
  assert.equal(lastProgress.ratio, 0.5);
  assert.equal(lastProgress.timestamp, '2025-01-01T00:00:00.000Z');
});

test('a Remove hides a stroke that is undone before its draw finishes', () => {
  const { engine, board } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    { type: 'Remove', timestamp: '2025-01-01T00:00:00.500Z', stroke: { id: 's1', points: [] } }
  ];
  engine.computeTimeline(events);

  engine.seekTo(engine.totalDurationMs);

  assert.deepEqual(board.committedIds(), [], 'removed stroke is not committed');
  assert.deepEqual(board.activeIds(), [], 'and is not left in the in-progress tail');
});

test('completed strokes are committed once; in-progress strokes go to the active set', () => {
  const { engine, board } = newEngine();
  // s2 has three points so it has a visible prefix while in progress.
  const s2 = {
    type: 'Add',
    timestamp: '2025-01-01T00:00:00.200Z',
    stroke: {
      id: 's2',
      color: '#000000',
      width: 4,
      points: [
        { x: 0, y: 0, timeOffset: 0 },
        { x: 5, y: 5, timeOffset: 50 },
        { x: 10, y: 10, timeOffset: 100 }
      ]
    }
  };
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 100),
    // 200ms gap (< threshold) -> s2 starts at 200ms, lasts 100ms (200..300).
    s2
  ];
  engine.computeTimeline(events);

  // Frame 0: replay starts — nothing has finished yet, s1 begins animating. The
  // first frame rebuilds the committed set (entering replay), so commitStroke is
  // exercised only once playback advances past a completion.
  engine.seekTo(0);
  assert.deepEqual(board.committedIds(), [], 'nothing committed at the start');
  assert.deepEqual(board.activeIds(), ['s1'], 's1 is animating from the start');

  // Frame 1: s1 (0..100) is now complete and committed; s2 has not started.
  engine.seekTo(150);
  assert.deepEqual(board.committedIds(), ['s1'], 's1 committed');
  assert.deepEqual(board.activeIds(), [], 'nothing in progress at t=150');
  assert.deepEqual(board.commits, ['s1']);

  // Frame 2: s2 is now in progress; s1 must NOT be committed again.
  engine.seekTo(250);
  assert.deepEqual(board.commits, ['s1'], 's1 is committed exactly once');
  assert.deepEqual(board.activeIds(), ['s2'], 'only the in-progress stroke is active');
  assert.equal(board.activePoints('s2').length, 2, 's2 prefix is its first two points at +50ms');

  // Frame 3: still mid-s2. No new commits; history is not re-emitted.
  engine.seekTo(260);
  assert.deepEqual(board.commits, ['s1']);
  assert.deepEqual(board.activeIds(), ['s2']);
});

test('seeking backwards rebuilds the committed set via a snapshot', () => {
  const { engine, board } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 100),
    addEvent('s2', '2025-01-01T00:00:00.200Z', 100)
  ];
  engine.computeTimeline(events);

  // Play forward past both strokes: both committed.
  engine.seekTo(350);
  assert.deepEqual(board.committedIds(), ['s1', 's2']);

  // Seek back before s2 started: the committed set is rebuilt to only s1.
  engine.seekTo(150);
  assert.deepEqual(board.committedIds(), ['s1'], 's2 is in the future after seeking back');
  assert.deepEqual(board.activeIds(), []);
});

test('a Remove rebuilds the committed set, dropping the removed stroke', () => {
  const { engine, board } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 100),
    addEvent('s2', '2025-01-01T00:00:00.200Z', 100),
    // s1 is undone after both strokes have finished drawing.
    { type: 'Remove', timestamp: '2025-01-01T00:00:00.400Z', stroke: { id: 's1', points: [] } }
  ];
  engine.computeTimeline(events);

  // Before the Remove: both strokes committed.
  engine.seekTo(350);
  assert.deepEqual(board.committedIds(), ['s1', 's2']);

  // After the Remove takes effect: rebuilt to s2 only.
  engine.seekTo(450);
  assert.deepEqual(board.committedIds(), ['s2']);
  assert.deepEqual(board.activeIds(), []);
});

// Regression: a single click/tap produces a one-point stroke (the pointerup at
// the same coordinates is de-duplicated). drawStrokePath must render it as a
// filled dot, otherwise single taps leave no mark on the canvas.
function recordingContext() {
  const calls = [];
  return {
    calls,
    strokeStyle: '',
    fillStyle: '',
    lineWidth: 0,
    lineCap: '',
    lineJoin: '',
    beginPath() {},
    moveTo() {},
    lineTo() {},
    stroke() { calls.push('stroke'); },
    arc() { calls.push('arc'); },
    fill() { calls.push('fill'); }
  };
}

test('a single-point stroke renders as a filled dot', () => {
  const context = recordingContext();
  drawStrokePath(context, [{ x: 5, y: 5, timeOffset: 0 }], { scale: 1, color: '#000', baseWidth: 4 });

  assert.ok(context.calls.includes('fill'), 'an isolated point must be filled as a dot');
  assert.ok(!context.calls.includes('stroke'), 'a single point has no segment to stroke');
});

test('a multi-point stroke strokes segments and draws no dot', () => {
  const context = recordingContext();
  drawStrokePath(
    context,
    [{ x: 0, y: 0, timeOffset: 0 }, { x: 10, y: 10, timeOffset: 10 }],
    { scale: 1, color: '#000', baseWidth: 4 }
  );

  assert.ok(context.calls.includes('stroke'), 'consecutive points are stroked as a segment');
  assert.ok(!context.calls.includes('fill'), 'a stroked path must not also draw a dot');
});
