import { fetchAllHistory } from './history.js';

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
    this.totalDurationMs = 0;

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
        wallClock: event.timestamp ?? event.Timestamp
      });

      previousWallMs = wallMs;
    }

    const last = this.timeline[this.timeline.length - 1];
    this.totalDurationMs = last ? last.startMs + last.strokeDurationMs : 0;
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
    this.clear();

    const visible = new Map();
    let currentWallClock = null;

    for (const entry of this.timeline) {
      if (entry.startMs > elapsedMs) {
        break;
      }

      currentWallClock = entry.wallClock;

      if (entry.type === 'Remove') {
        visible.delete(entry.strokeId);
        continue;
      }

      const localMs = elapsedMs - entry.startMs;
      const upToMs = localMs >= entry.strokeDurationMs ? Infinity : localMs;
      visible.set(entry.strokeId, { stroke: entry.stroke, upToMs });
    }

    for (const { stroke, upToMs } of visible.values()) {
      this.drawStroke(stroke, upToMs);
    }

    this.currentWallClock = currentWallClock;
  }

  reportProgress() {
    const ratio = this.totalDurationMs > 0 ? this.elapsedMs / this.totalDurationMs : 0;
    this.onProgress?.({ ratio, timestamp: this.currentWallClock });
  }

  drawStroke(stroke, upToMs) {
    const points = stroke.points ?? stroke.Points ?? [];
    if (points.length === 0) {
      return;
    }

    const dpr = this.devicePixelRatio();
    const context = this.context;
    context.strokeStyle = stroke.color ?? stroke.Color ?? DEFAULT_COLOR;
    context.lineWidth = (stroke.width ?? stroke.Width ?? 4) * dpr;
    context.lineCap = 'round';
    context.lineJoin = 'round';
    context.beginPath();

    let started = false;
    for (const point of points) {
      const offset = point.timeOffset ?? point.TimeOffset ?? 0;
      if (offset > upToMs) {
        break;
      }

      const x = (point.x ?? point.X) * dpr;
      const y = (point.y ?? point.Y) * dpr;
      if (!started) {
        context.moveTo(x, y);
        started = true;
      } else {
        context.lineTo(x, y);
      }
    }

    if (started) {
      context.stroke();
    }
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
