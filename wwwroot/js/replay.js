const DEFAULT_COLOR = '#1e88e5';

// Translates a board's append-only history into canvas commands, animating it
// like a video: strokes play in chronological order, each draws point-by-point
// using Point.TimeOffset, and long inactivity gaps are compressed. The
// WhiteboardCanvas owns all rendering and is the sole owner of the 2D context;
// the engine drives it purely through commands, marking which strokes are
// committed and which are mid-animation (and never draws directly).
export class ReplayEngine {
  constructor(board, config = {}) {
    this.board = board;
    this.gapThresholdMs = config.gapThresholdMs ?? 3000;
    this.speed = config.speed ?? 1;

    this.timeline = [];
    this.byCompletion = [];
    this.totalDurationMs = 0;

    // Watermark into byCompletion of the entries already pushed to the board's
    // committed layer, plus the elapsed time that layer reflects. The committed
    // strokes live in the canvas; the engine only tracks how far it has applied
    // so it can advance incrementally and rebuild on backward seek / Remove.
    this.committedCursor = 0;
    this.appliedMs = Number.POSITIVE_INFINITY;

    this.elapsedMs = 0;
    this.playing = false;
    this.rafId = null;
    this.lastFrame = 0;
    this.currentWallClock = null;

    this.onProgress = null;
    this.onStop = null;
    this.onEnd = null;

    this.tick = this.tick.bind(this);
  }

  // Builds the playback timeline from the raw event log. History is supplied by
  // the caller (the engine does not fetch it) and may arrive in any order, so it
  // is sorted chronologically here. The events are only read, so a sorted copy is
  // used rather than mutating or retaining the caller's array.
  computeTimeline(events) {
    events = events.slice().sort(compareEvents);
    this.timeline = [];
    let cumulativeMs = 0;
    let previousWallMs = null;

    for (const event of events) {
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

    // Entries ordered by when they become final, used to advance the committed
    // layer. Ties keep timeline order so overlapping strokes layer consistently.
    this.byCompletion = this.timeline
      .map((entry, index) => ({ entry, index }))
      .sort((a, b) => a.entry.completionMs - b.entry.completionMs || a.index - b.index)
      .map(item => item.entry);

    this.resetApply();
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
    this.resetApply();
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

  // Apply the board state for `elapsedMs` by issuing canvas commands: advance (or
  // rebuild) the committed layer, then hand the canvas the set of in-progress
  // stroke prefixes. The canvas owns all pixels, so this is resize-safe by
  // construction — a resize just repaints the same owned state.
  renderAt(elapsedMs) {
    if (elapsedMs < this.appliedMs) {
      this.#rebuildCommitted(elapsedMs);
    } else {
      this.#advanceCommitted(elapsedMs);
    }
    this.appliedMs = elapsedMs;
    this.#applyActive(elapsedMs);
  }

  resetApply() {
    this.committedCursor = 0;
    this.appliedMs = Number.POSITIVE_INFINITY;
    this.currentWallClock = null;
  }

  // Forward path: commit each stroke whose animation has just finished. A Remove
  // can target a stroke that isn't the most recent, so it falls back to a full
  // rebuild of the committed set (the event-ordered fold resolves visibility).
  #advanceCommitted(elapsedMs) {
    while (this.committedCursor < this.byCompletion.length) {
      const entry = this.byCompletion[this.committedCursor];
      if (entry.completionMs > elapsedMs) {
        break;
      }
      if (entry.type === 'Remove') {
        this.#rebuildCommitted(elapsedMs);
        return;
      }
      this.board.commitStroke(entry.stroke);
      this.committedCursor++;
    }
  }

  // Recompute the committed set from scratch (used on backward seek and after a
  // Remove) by folding every completed entry in event order so Add/Remove
  // visibility resolves correctly, then replace the board snapshot in one call.
  #rebuildCommitted(elapsedMs) {
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

    this.board.setSnapshot([...visible.values()]);

    this.committedCursor = 0;
    while (this.committedCursor < this.byCompletion.length &&
           this.byCompletion[this.committedCursor].completionMs <= elapsedMs) {
      this.committedCursor++;
    }
  }

  // The in-progress tail: every Add that has started but not finished, truncated
  // to its visible point prefix, with Removes applied. The canvas paints these on
  // the overlay above the committed layer, so only the overlay repaints per frame.
  #applyActive(elapsedMs) {
    const active = new Map();
    let wallClock = this.currentWallClock;

    for (const entry of this.timeline) {
      if (entry.startMs > elapsedMs) {
        break;
      }
      wallClock = entry.wallClock;

      if (entry.type === 'Remove') {
        active.delete(entry.strokeId);
        continue;
      }

      if (entry.completionMs <= elapsedMs) {
        // Finished animating: it now lives in the committed layer, not the tail.
        active.delete(entry.strokeId);
        continue;
      }

      const points = prefixPoints(entry.stroke, elapsedMs - entry.startMs);
      if (points.length === 0) {
        active.delete(entry.strokeId);
        continue;
      }

      active.set(entry.strokeId, {
        id: entry.strokeId,
        color: entry.stroke.color ?? entry.stroke.Color ?? DEFAULT_COLOR,
        width: entry.stroke.width ?? entry.stroke.Width ?? 4,
        points
      });
    }

    this.currentWallClock = wallClock;
    this.board.setActiveStrokes([...active.values()]);
  }

  reportProgress() {
    const ratio = this.totalDurationMs > 0 ? this.elapsedMs / this.totalDurationMs : 0;
    this.onProgress?.({ ratio, timestamp: this.currentWallClock });
  }

  cancelFrame() {
    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
      this.rafId = null;
    }
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

// The visible prefix of an in-progress stroke at local time `localMs`: the points
// whose timeOffset has been reached. Equivalent to drawStrokePath's `upToMs`
// truncation (which is point-granular), so handing this prefix to the canvas as a
// whole stroke reproduces the same frame without the canvas knowing about time.
function prefixPoints(stroke, localMs) {
  const points = stroke.points ?? stroke.Points ?? [];
  const prefix = [];
  for (const point of points) {
    if ((point.timeOffset ?? point.TimeOffset ?? 0) > localMs) {
      break;
    }
    prefix.push(point);
  }
  return prefix;
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
