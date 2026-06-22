// Unit tests for the client-side ReplayEngine timeline logic (TASK-034).
//
// There is no browser test runner in this repo, so these run on Node's built-in
// test runner with the handful of browser globals the engine touches stubbed
// out. Only pure timeline behaviour is exercised here (gap compression, total
// duration, seek-to-midpoint state); point-by-point animation timing is covered
// by the manual tests TASK-035..037.
//
// Run: node --test Tests/ReplayEngineTests.mjs

import test from 'node:test';
import assert from 'node:assert/strict';

// The engine reads the canvas background colour via getComputedStyle when
// clearing each frame; a constant stub is sufficient for headless testing.
globalThis.getComputedStyle = () => ({ backgroundColor: '#ffffff' });

const { ReplayEngine } = await import('../wwwroot/js/replay.js');
const { drawStrokePath } = await import('../wwwroot/js/canvas.js');

// Builds a recording 2D-context stub. Every stroke() is captured into the shared
// `drawnStrokes` array tagged with its role, so a test can tell strokes painted
// onto the visible canvas ('live') apart from those baked into the offscreen base
// buffer ('base'). Both are real visible output (the base is blitted each frame).
function buildContext(canvas, role, drawnStrokes) {
  let current = null;
  return {
    canvas,
    strokeStyle: '',
    lineWidth: 0,
    lineCap: '',
    lineJoin: '',
    fillStyle: '',
    clearRect() {},
    fillRect() {},
    drawImage() {},
    beginPath() {
      current = { points: [], role };
    },
    moveTo(x, y) {
      current.points.push([x, y]);
    },
    lineTo(x, y) {
      current.points.push([x, y]);
    },
    arc(x, y) {
      current.points.push([x, y]);
    },
    stroke() {
      drawnStrokes.push(current);
    },
    fill() {
      drawnStrokes.push(current);
    }
  };
}

function makeContext() {
  const drawnStrokes = [];
  const canvas = {
    width: 100,
    height: 100,
    getBoundingClientRect: () => ({ width: 100 })
  };
  const context = buildContext(canvas, 'live', drawnStrokes);

  // The engine lazily creates an offscreen base canvas via document.createElement.
  // Hand back a canvas whose context records into the same array (tagged 'base').
  globalThis.document = {
    createElement() {
      const baseCanvas = { width: 0, height: 0 };
      baseCanvas.getContext = () => buildContext(baseCanvas, 'base', drawnStrokes);
      return baseCanvas;
    }
  };

  return { context, drawnStrokes };
}

function countByRole(drawnStrokes) {
  return {
    base: drawnStrokes.filter(entry => entry.role === 'base').length,
    live: drawnStrokes.filter(entry => entry.role === 'live').length
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
  const { context, drawnStrokes } = makeContext();
  const engine = new ReplayEngine(context, { gapThresholdMs: 3000, speed: 1 });
  return { engine, drawnStrokes };
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

test('seek to the midpoint renders only the strokes visible at that time', () => {
  const { engine, drawnStrokes } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    addEvent('s2', '2025-01-01T00:01:40.000Z', 1000)
  ];
  engine.computeTimeline(events);

  let lastProgress = null;
  engine.onProgress = (progress) => {
    lastProgress = progress;
  };

  // totalDurationMs = 4000, so 0.5 -> elapsedMs 2000, before s2 begins at 3000.
  engine.seek(0.5);

  assert.equal(drawnStrokes.length, 1, 'only the first stroke should be visible at the midpoint');
  assert.equal(lastProgress.ratio, 0.5);
  assert.equal(lastProgress.timestamp, '2025-01-01T00:00:00.000Z');
});

test('a Remove event hides a previously added stroke during replay', () => {
  const { engine, drawnStrokes } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    { type: 'Remove', timestamp: '2025-01-01T00:00:00.500Z', stroke: { id: 's1', points: [] } }
  ];
  engine.computeTimeline(events);

  engine.seek(1);

  assert.equal(drawnStrokes.length, 0, 'removed stroke must not be drawn');
});

test('completed strokes are baked once and not redrawn on every frame', () => {
  const { engine, drawnStrokes } = newEngine();
  // s2 has three points so it draws a partial segment while in progress (a
  // two-point stroke renders nothing until its final point's time is reached).
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

  // Frame 1: s1 (0..100) is complete, s2 has not started. s1 is baked into base.
  engine.renderAt(150);
  const frame1 = countByRole(drawnStrokes);
  assert.equal(frame1.base, 1, 's1 should be baked into the base buffer once');
  assert.equal(frame1.live, 0, 'nothing is in progress at t=150');

  // Frame 2: s2 is now in progress (one segment drawn). s1 is already baked, so
  // it must NOT be redrawn -- only the in-progress s2 is painted live.
  drawnStrokes.length = 0;
  engine.renderAt(250);
  const frame2 = countByRole(drawnStrokes);
  assert.equal(frame2.base, 0, 's1 must not be re-baked once complete');
  assert.equal(frame2.live, 1, 'only the in-progress stroke is redrawn');

  // Frame 3: still mid-s2. The completed history is never redrawn again.
  drawnStrokes.length = 0;
  engine.renderAt(260);
  const frame3 = countByRole(drawnStrokes);
  assert.equal(frame3.base, 0, 'completed history is not redrawn frame-to-frame');
  assert.equal(frame3.live, 1, 'only the in-progress stroke is redrawn');
});

test('seeking backwards rebuilds the base buffer from scratch', () => {
  const { engine, drawnStrokes } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 100),
    addEvent('s2', '2025-01-01T00:00:00.200Z', 100)
  ];
  engine.computeTimeline(events);

  // Play forward past both strokes: both end up baked into the base buffer.
  engine.renderAt(350);
  drawnStrokes.length = 0;

  // Seek back before s2 started: the forward-only base must be rebuilt to contain
  // only s1, so the now-future s2 is not shown.
  engine.renderAt(150);
  const frame = countByRole(drawnStrokes);
  assert.equal(frame.base, 1, 'base buffer is rebuilt with only the completed s1');
  assert.equal(frame.live, 0, 's2 is in the future after seeking back to t=150');
});

test('a Remove forces a base rebuild that drops the removed stroke', () => {
  const { engine, drawnStrokes } = newEngine();
  const events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 100),
    addEvent('s2', '2025-01-01T00:00:00.200Z', 100),
    // s1 is undone after both strokes have finished drawing (at ~400ms).
    { type: 'Remove', timestamp: '2025-01-01T00:00:00.400Z', stroke: { id: 's1', points: [] } }
  ];
  engine.computeTimeline(events);

  // Before the Remove: both strokes are baked.
  engine.renderAt(350);

  // After the Remove completes: the base must be rebuilt so only s2 remains.
  drawnStrokes.length = 0;
  engine.renderAt(450);
  const frame = countByRole(drawnStrokes);
  assert.equal(frame.live, 0, 'no stroke is in progress at t=450');
  assert.equal(frame.base, 1, 'base is rebuilt with s2 only after s1 is removed');
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
