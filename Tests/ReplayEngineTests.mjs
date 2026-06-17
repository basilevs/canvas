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

function makeContext() {
  const drawnStrokes = [];
  let current = null;
  const canvas = {
    width: 100,
    height: 100,
    getBoundingClientRect: () => ({ width: 100 })
  };

  const context = {
    canvas,
    strokeStyle: '',
    lineWidth: 0,
    lineCap: '',
    lineJoin: '',
    fillStyle: '',
    clearRect() {},
    fillRect() {},
    beginPath() {
      current = { points: [] };
    },
    moveTo(x, y) {
      current.points.push([x, y]);
    },
    lineTo(x, y) {
      current.points.push([x, y]);
    },
    stroke() {
      drawnStrokes.push(current);
    }
  };

  return { context, drawnStrokes };
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
  engine.events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    // 100s real gap -> must be clamped to the 3000ms threshold.
    addEvent('s2', '2025-01-01T00:01:40.000Z', 1000)
  ];

  engine.computeTimeline();

  const [first, second] = engine.timeline;
  assert.equal(first.startMs, 0);
  assert.equal(second.startMs, 3000, 'gap should be clamped to gapThresholdMs');
});

test('computeTimeline preserves short gaps below the threshold', () => {
  const { engine } = newEngine();
  engine.events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    // 1.5s real gap -> kept as-is (under the 3000ms threshold).
    addEvent('s2', '2025-01-01T00:00:01.500Z', 1000)
  ];

  engine.computeTimeline();

  assert.equal(engine.timeline[1].startMs, 1500);
});

test('totalDurationMs accounts for the final stroke duration', () => {
  const { engine } = newEngine();
  engine.events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    addEvent('s2', '2025-01-01T00:01:40.000Z', 1000)
  ];

  engine.computeTimeline();

  // s2 starts at 3000 (clamped gap) and lasts 1000 -> total 4000.
  assert.equal(engine.totalDurationMs, 4000);
});

test('seek to the midpoint renders only the strokes visible at that time', () => {
  const { engine, drawnStrokes } = newEngine();
  engine.events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    addEvent('s2', '2025-01-01T00:01:40.000Z', 1000)
  ];
  engine.computeTimeline();

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
  engine.events = [
    addEvent('s1', '2025-01-01T00:00:00.000Z', 1000),
    { type: 'Remove', timestamp: '2025-01-01T00:00:00.500Z', stroke: { id: 's1', points: [] } }
  ];
  engine.computeTimeline();

  engine.seek(1);

  assert.equal(drawnStrokes.length, 0, 'removed stroke must not be drawn');
});
