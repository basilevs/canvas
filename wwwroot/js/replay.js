import { fetchAllHistory } from './history.js';
import { drawStrokePath } from './canvas.js';

const DEFAULT_COLOR = '#1e88e5';

// Animates a board's append-only history like a video: strokes play in
// chronological order, each stroke draws point-by-point using Point.TimeOffset,
// and long inactivity gaps are compressed so replays stay watchable.
export class ReplayEngine {
  constructor(context, config = {}) {
    this.context = context;
    this.canvas = context.canvas;
    this.gapThresholdMs = config.gapThresholdMs ?? 3000;
    this.speed = config.speed ?? 1;

    this.events = [];
    this.timeline = [];
    this.byCompletion = [];
    this.totalDurationMs = 0;

    // Completed strokes are baked once into this offscreen buffer; each frame we
    // blit the buffer and redraw only the in-progress tail, instead of redrawing
    // the entire history every animation frame (the cause of 100% CPU on replay).
    this.baseCanvas = null;
    this.baseContext = null;
    this.baseCursor = 0;
    this.baseElapsedMs = 0;
    this.tailStart = 0;
    this.baseAddsBuffer = [];

    this.elapsedMs = 0;
    this.playing = false;
    this.rafId = null;
    this.lastFrame = 0;

    this.onProgress = null;
    this.onStop = null;
    this.onEnd = null;

    this.tick = this.tick.bind(this);
  }

  async loadHistory(boardName) {
    const events = await fetchAllHistory(boardName);
    this.events = events.slice().sort(compareEvents);
    this.computeTimeline();
  }

  computeTimeline() {
    this.timeline = [];
    let cumulativeMs = 0;
    let previousWallMs = null;

    for (const event of this.events) {
      const type = event.type ?? event.Type;
      const stroke = event.stroke ?? event.Stroke;
      const strokeId = stroke.id ?? stroke.Id;
      const wallMs = Date.parse(event.timestamp ?? event.Timestamp);

      let gapMs = previousWallMs === null ? 0 : wallMs - previousWallMs;
      if (gapMs < 0) {
        gapMs = 0;
      }
      if (gapMs > this.gapThresholdMs) {
        gapMs = this.gapThresholdMs;
      }

      cumulativeMs += gapMs;
      const strokeDurationMs = type === 'Remove' ? 0 : strokeDuration(stroke);

      this.timeline.push({
        type,
        strokeId,
        stroke,
        startMs: cumulativeMs,
        strokeDurationMs,
        // The moment this entry's visual effect is final: a Remove takes effect at
        // its start; an Add is done once its last point has been drawn.
        completionMs: cumulativeMs + (type === 'Remove' ? 0 : strokeDurationMs),
        wallClock: event.timestamp ?? event.Timestamp
      });

      previousWallMs = wallMs;
    }

    const last = this.timeline[this.timeline.length - 1];
    this.totalDurationMs = last ? last.startMs + last.strokeDurationMs : 0;

    // Entries ordered by when they become final, used to advance the baked base
    // buffer. Ties keep timeline order so overlapping strokes layer consistently.
    this.byCompletion = this.timeline
      .map((entry, index) => ({ entry, index }))
      .sort((a, b) => a.entry.completionMs - b.entry.completionMs || a.index - b.index)
      .map(item => item.entry);

    this.resetBase();
  }

  play() {
    if (this.playing || this.timeline.length === 0) {
      return;
    }

    this.playing = true;
    this.lastFrame = performance.now();
    this.rafId = requestAnimationFrame(this.tick);
  }

  pause() {
    if (!this.playing) {
      return;
    }

    this.playing = false;
    this.cancelFrame();
  }

  resume() {
    if (this.playing || this.timeline.length === 0) {
      return;
    }

    // Replaying after reaching the end restarts from the beginning.
    if (this.elapsedMs >= this.totalDurationMs) {
      this.elapsedMs = 0;
    }

    this.playing = true;
    this.lastFrame = performance.now();
    this.rafId = requestAnimationFrame(this.tick);
  }

  seek(positionRatio) {
    const ratio = Math.min(1, Math.max(0, positionRatio));
    this.elapsedMs = ratio * this.totalDurationMs;
    this.lastFrame = performance.now();
    this.renderAt(this.elapsedMs);
    this.reportProgress();
  }

  setSpeed(multiplier) {
    this.speed = multiplier;
    // Re-baseline the frame clock so the change in scale does not produce a jump.
    this.lastFrame = performance.now();
  }

  stop() {
    this.playing = false;
    this.cancelFrame();
    this.elapsedMs = 0;
    this.clear();
    this.resetBase();
    this.onStop?.();
  }

  tick(now) {
    if (!this.playing) {
      return;
    }

    const delta = (now - this.lastFrame) * this.speed;
    this.lastFrame = now;
    this.elapsedMs += delta;

    if (this.elapsedMs >= this.totalDurationMs) {
      this.elapsedMs = this.totalDurationMs;
      this.renderAt(this.elapsedMs);
      this.reportProgress();
      this.playing = false;
      this.onEnd?.();
      return;
    }

    this.renderAt(this.elapsedMs);
    this.reportProgress();
    this.rafId = requestAnimationFrame(this.tick);
  }

  renderAt(elapsedMs) {
    this.ensureBaseCanvas();

    // Seeking backwards invalidates the forward-only base buffer; rebuild it.
    if (elapsedMs < this.baseElapsedMs) {
      this.resetBase();
    }

    this.advanceBase(elapsedMs);

    const timeline = this.timeline;
    const len = timeline.length;

    // Advance the tail watermark past every entry that is now fully complete
    // (and therefore baked into the base buffer).
    while (this.tailStart < len && timeline[this.tailStart].completionMs <= elapsedMs) {
      this.currentWallClock = timeline[this.tailStart].wallClock;
      this.tailStart++;
    }

    this.clear();
    this.context.drawImage(this.baseCanvas, 0, 0);

    // Only the tail (in-progress strokes, plus any complete strokes sitting behind
    // a still-animating one) is redrawn each frame.
    const visible = new Map();
    for (let k = this.tailStart; k < len; k++) {
      const entry = timeline[k];
      if (entry.startMs > elapsedMs) {
        break;
      }

      this.currentWallClock = entry.wallClock;

      if (entry.type === 'Remove') {
        visible.delete(entry.strokeId);
        continue;
      }

      const localMs = elapsedMs - entry.startMs;
      const upToMs = localMs >= entry.strokeDurationMs ? Infinity : localMs;
      visible.set(entry.strokeId, { stroke: entry.stroke, upToMs });
    }

    for (const { stroke, upToMs } of visible.values()) {
      this.drawStrokeTo(this.context, stroke, upToMs);
    }
  }

  // Lazily create (and size) the offscreen base buffer to match the live canvas.
  ensureBaseCanvas() {
    const width = this.canvas.width;
    const height = this.canvas.height;
    if (!this.baseCanvas) {
      this.baseCanvas = document.createElement('canvas');
      this.baseContext = this.baseCanvas.getContext('2d');
    }
    if (this.baseCanvas.width !== width || this.baseCanvas.height !== height) {
      this.baseCanvas.width = width;
      this.baseCanvas.height = height;
      this.resetBase();
    }
  }

  resetBase() {
    this.baseCursor = 0;
    this.baseElapsedMs = 0;
    this.tailStart = 0;
    this.baseAddsBuffer.length = 0;
    this.currentWallClock = null;
    if (this.baseContext && this.baseCanvas) {
      this.baseContext.clearRect(0, 0, this.baseCanvas.width, this.baseCanvas.height);
    }
  }

  // Bake newly-completed strokes into the base buffer. A completed Remove forces a
  // full rebuild (a flattened bitmap can't have a single stroke erased from it).
  advanceBase(elapsedMs) {
    let rebuild = false;
    while (this.baseCursor < this.byCompletion.length) {
      const entry = this.byCompletion[this.baseCursor];
      if (entry.completionMs > elapsedMs) {
        break;
      }
      if (entry.type === 'Remove') {
        rebuild = true;
      } else {
        this.baseAddsBuffer.push(entry);
      }
      this.baseCursor++;
    }

    if (rebuild) {
      this.baseAddsBuffer.length = 0;
      this.rebuildBase(elapsedMs);
    } else {
      for (const entry of this.baseAddsBuffer) {
        this.drawStrokeTo(this.baseContext, entry.stroke, Infinity);
      }
      this.baseAddsBuffer.length = 0;
    }

    this.baseElapsedMs = elapsedMs;
  }

  // Rebuild the base buffer from scratch by folding every completed entry in event
  // order (so Add/Remove visibility resolves correctly), then drawing the result.
  rebuildBase(elapsedMs) {
    this.baseContext.clearRect(0, 0, this.baseCanvas.width, this.baseCanvas.height);
    const visible = new Map();
    for (const entry of this.timeline) {
      if (entry.completionMs > elapsedMs) {
        continue;
      }
      if (entry.type === 'Remove') {
        visible.delete(entry.strokeId);
      } else {
        visible.set(entry.strokeId, entry.stroke);
      }
    }
    for (const stroke of visible.values()) {
      this.drawStrokeTo(this.baseContext, stroke, Infinity);
    }
  }

  reportProgress() {
    const ratio = this.totalDurationMs > 0 ? this.elapsedMs / this.totalDurationMs : 0;
    this.onProgress?.({ ratio, timestamp: this.currentWallClock });
  }

  drawStrokeTo(context, stroke, upToMs) {
    const points = stroke.points ?? stroke.Points ?? [];
    if (points.length === 0) {
      return;
    }

    drawStrokePath(context, points, {
      dpr: this.devicePixelRatio(),
      color: stroke.color ?? stroke.Color ?? DEFAULT_COLOR,
      baseWidth: stroke.width ?? stroke.Width ?? 4,
      upToMs
    });
  }

  clear() {
    const context = this.context;
    context.clearRect(0, 0, this.canvas.width, this.canvas.height);
    context.fillStyle = getComputedStyle(this.canvas).backgroundColor || '#ffffff';
    context.fillRect(0, 0, this.canvas.width, this.canvas.height);
  }

  cancelFrame() {
    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
      this.rafId = null;
    }
  }

  devicePixelRatio() {
    const rect = this.canvas.getBoundingClientRect();
    if (rect.width > 0) {
      return this.canvas.width / rect.width;
    }

    return Math.max(window.devicePixelRatio || 1, 1);
  }
}

function strokeDuration(stroke) {
  const points = stroke.points ?? stroke.Points ?? [];
  let max = 0;
  for (const point of points) {
    const offset = point.timeOffset ?? point.TimeOffset ?? 0;
    if (offset > max) {
      max = offset;
    }
  }

  return max;
}

function compareEvents(a, b) {
  const aTime = Date.parse(a.timestamp ?? a.Timestamp);
  const bTime = Date.parse(b.timestamp ?? b.Timestamp);
  if (aTime !== bTime) {
    return aTime - bTime;
  }

  const aId = (a.stroke ?? a.Stroke)?.id ?? (a.stroke ?? a.Stroke)?.Id ?? '';
  const bId = (b.stroke ?? b.Stroke)?.id ?? (b.stroke ?? b.Stroke)?.Id ?? '';
  return aId < bId ? -1 : aId > bId ? 1 : 0;
}
