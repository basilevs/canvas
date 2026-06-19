const DEFAULT_COLOR = '#1e88e5';
const SIMPLIFY_THRESHOLD_CSS_PX = 5;

// On-screen debug log for diagnosing input on mobile, where there is no easy
// access to the dev-tools console. Flip DEBUG to true to surface a live panel
// of pointer events; left in place because it is handy for future input bugs.
const DEBUG = false;
let debugPanel = null;
function debugLog(...args) {
  if (!DEBUG) {
    return;
  }
  console.log(...args);
  if (!debugPanel) {
    debugPanel = document.createElement('div');
    const s = debugPanel.style;
    s.position = 'fixed';
    s.left = '0';
    s.right = '0';
    s.bottom = '0';
    s.maxHeight = '40%';
    s.overflowY = 'auto';
    s.zIndex = '99999';
    s.background = 'rgba(0, 0, 0, 0.8)';
    s.color = '#0f0';
    s.font = '11px/1.3 monospace';
    s.padding = '4px';
    s.whiteSpace = 'pre-wrap';
    document.body.appendChild(debugPanel);
  }
  const line = args
    .map(arg => (typeof arg === 'string' ? arg : JSON.stringify(arg)))
    .join(' ');
  debugPanel.textContent = `${line}\n${debugPanel.textContent}`.slice(0, 4000);
}

export function createWhiteboardCanvas(canvasElement, handlers = {}) {
  return new WhiteboardCanvas(canvasElement, handlers);
}

class WhiteboardCanvas {
  constructor(canvasElement, handlers) {
    this.canvas = canvasElement;
    this.context = canvasElement.getContext('2d');
    this.handlers = handlers;
    this.confirmedStrokes = [];
    this.previewStroke = null;
    this.remoteCursors = new Map();
    this.currentStroke = null;
    this.currentColor = DEFAULT_COLOR;
    this.currentWidth = 4;
    this.replaying = false;
    this.devicePixelRatio = Math.max(window.devicePixelRatio || 1, 1);

    // Remote cursors are painted on a separate transparent overlay stacked on
    // top of the strokes canvas. This keeps frequent cursor movement from
    // forcing a full redraw of the entire stroke history (expensive on mobile),
    // since the two layers repaint independently. pointer-events: none lets
    // input pass through to the strokes canvas below. Explicit z-index values
    // give the two exactly-overlapping canvases a deterministic stacking order,
    // avoiding the compositor z-fighting that ambiguous (auto) z-index allows.
    this.canvas.style.zIndex = '0';
    this.cursorCanvas = document.createElement('canvas');
    const cursorStyle = this.cursorCanvas.style;
    cursorStyle.position = 'absolute';
    cursorStyle.top = '0';
    cursorStyle.left = '0';
    cursorStyle.width = '100%';
    cursorStyle.height = '100%';
    cursorStyle.zIndex = '1';
    cursorStyle.pointerEvents = 'none';
    this.canvas.parentNode.appendChild(this.cursorCanvas);
    this.cursorContext = this.cursorCanvas.getContext('2d');

    this.canvas.addEventListener('pointerdown', this.#handlePointerDown);
    this.canvas.addEventListener('pointermove', this.#handlePointerMove);
    this.canvas.addEventListener('pointerup', this.#handlePointerUp);
    this.canvas.addEventListener('pointercancel', this.#handlePointerUp);
    window.addEventListener('resize', this.#handleResize);
    this.#handleResize();
  }

  setDrawingStyle(color, width) {
    this.currentColor = color || DEFAULT_COLOR;
    this.currentWidth = Number.isFinite(width) ? width : 4;
  }

  // While replaying, the ReplayEngine owns the canvas: suppress live rendering and
  // input so animation frames are not clobbered. Leaving replay repaints current state.
  setReplaying(value) {
    this.replaying = value;
    if (value) {
      // Hide remote cursors while the replay engine animates the strokes layer.
      this.#clearCursors();
    } else {
      this.#render();
      this.#renderCursors();
    }
  }

  setSnapshot(strokes) {
    this.confirmedStrokes = Array.isArray(strokes) ? [...strokes] : [];
    this.previewStroke = null;
    this.#render();
  }

  commitStroke(stroke) {
    if (!stroke?.id && !stroke?.Id) {
      return;
    }

    const strokeId = stroke.id ?? stroke.Id;
    this.previewStroke = this.previewStroke?.id === strokeId ? null : this.previewStroke;

    if (!this.confirmedStrokes.some(existing => getStrokeId(existing) === strokeId)) {
      this.confirmedStrokes.push(normalizeStroke(stroke));
    }

    this.#render();
  }

  removeStroke(strokeId) {
    if (!strokeId) {
      return;
    }

    const index = this.confirmedStrokes.findIndex(existing => getStrokeId(existing) === strokeId);
    if (index !== -1) {
      this.confirmedStrokes.splice(index, 1);
      this.#render();
    }
  }

  setRemoteCursor(userId, cursor) {
    if (!userId) {
      return;
    }

    if (cursor === null) {
      this.remoteCursors.delete(userId);
    } else {
      this.remoteCursors.set(userId, cursor);
    }

    this.#renderCursors();
  }

  removeRemoteCursor(userId) {
    this.remoteCursors.delete(userId);
    this.#renderCursors();
  }

  #handleResize = () => {
    const rect = this.canvas.getBoundingClientRect();
    const width = Math.max(Math.floor(rect.width * this.devicePixelRatio), 1);
    const height = Math.max(Math.floor(rect.height * this.devicePixelRatio), 1);

    if (this.canvas.width !== width || this.canvas.height !== height ||
        this.cursorCanvas.width !== width || this.cursorCanvas.height !== height) {
      this.canvas.width = width;
      this.canvas.height = height;
      this.cursorCanvas.width = width;
      this.cursorCanvas.height = height;
      this.#render();
      this.#renderCursors();
    }
  };

  #handlePointerDown = (event) => {
    if (event.button !== 0 || this.replaying) {
      return;
    }

    debugLog('[ptr] down start', {
      type: event.pointerType,
      pressure: Number(event.pressure?.toFixed?.(2) ?? event.pressure),
      button: event.button
    });
    this.canvas.setPointerCapture(event.pointerId);
    this.currentStroke = {
      id: generateStrokeId(),
      points: [],
      color: this.currentColor,
      width: this.currentWidth,
      startTime: performance.now(),
      pointerType: event.pointerType,
      startPressure: event.pressure
    };

    this.#appendPoint(event);
    this.previewStroke = this.currentStroke;
    this.#render();
  };

  #handlePointerMove = (event) => {
    if (this.currentStroke && event.pointerType !== this.currentStroke.pointerType) {
      debugLog('[ptr] DROP move type', { type: event.pointerType, active: this.currentStroke.pointerType });
      return;
    }
    this.#updateCursor(event);
    this.#appendPoint(event);
    this.previewStroke = this.currentStroke;
    this.#render();
  };

  #handlePointerUp = (event) => {
    if (!this.currentStroke) {
      return;
    }
    if (event.pointerType !== this.currentStroke.pointerType) {
      debugLog('[ptr] DROP up type', { type: event.pointerType, active: this.currentStroke.pointerType });
      return;
    }

    this.#appendPoint(event);
    const completedStroke = this.currentStroke;
    this.currentStroke = null;
    completedStroke.points = simplifyPointsVisvalingamWhyatt(
      completedStroke.points,
      SIMPLIFY_THRESHOLD_CSS_PX,
      completedStroke.width ?? completedStroke.Width ?? 4
    );
    this.previewStroke = completedStroke;
    this.#render();
    this.handlers.onStrokeCompleted?.(completedStroke);
  };

  #appendPoint(event) {
    if (!this.currentStroke) {
      return;
    }

    const point = this.#toCanvasPoint(event);
    const points = this.currentStroke.points;
    const lastPoint = points[points.length - 1];
    if (lastPoint && lastPoint.x === point.x && lastPoint.y === point.y) {
      return;
    }

    // Ignore any sample whose pointer type differs from the one that started
    // the stroke (e.g. the OS synthesising stray mouse moves during a pen or
    // touch stroke), which would otherwise pollute the stroke with bogus
    // positions and pressures.
    if (event.pointerType !== this.currentStroke.pointerType) {
      debugLog('[ptr] DROP point type', { type: event.pointerType, active: this.currentStroke.pointerType });
      return;
    }

    const pressure = event.pressure;

    debugLog('[ptr]', {
      type: event.pointerType,
      p: Number(pressure?.toFixed?.(3) ?? pressure),
      n: points.length
    });

    // A pen on Android intermittently spikes to a spurious pressure of exactly
    // 1.0 mid-stroke. Touch, by contrast, legitimately reports a constant 1.0,
    // so only drop the spike when the stroke started at a different pressure.
    if (pressure === 1 && this.currentStroke.startPressure !== 1) {
      debugLog('[ptr] DROP pressure==1', { type: event.pointerType, start: this.currentStroke.startPressure });
      return;
    }

    // The pen reports a spurious high pressure on the very first contact
    // point. Record it with no pressure, then back-fill it from the first
    // subsequent point so the stroke doesn't start with a fat blob.
    if (points.length === 0) {
      this.currentStroke.backfillFirstPressure = true;
      points.push({
        x: point.x,
        y: point.y,
        pressure: null,
        timeOffset: 0
      });
      return;
    }

    if (this.currentStroke.backfillFirstPressure) {
      points[0].pressure = pressure;
      this.currentStroke.backfillFirstPressure = false;
    }

    points.push({
      x: point.x,
      y: point.y,
      pressure,
      timeOffset: Math.min(65535, Math.max(0, Math.round(performance.now() - this.currentStroke.startTime)))
    });
  }

  #updateCursor(event) {
    const cursor = this.#toCanvasPoint(event);
    this.handlers.onCursorMoved?.(cursor.x, cursor.y);
  }

  #toCanvasPoint(event) {
    const rect = this.canvas.getBoundingClientRect();
    return {
      x: event.clientX - rect.left,
      y: event.clientY - rect.top
    };
  }

  #render() {
    const context = this.context;
    if (!context || this.replaying) {
      return;
    }

    const width = this.canvas.width;
    const height = this.canvas.height;
    context.clearRect(0, 0, width, height);
    context.fillStyle = getComputedStyle(this.canvas).backgroundColor || '#ffffff';
    context.fillRect(0, 0, width, height);

    for (const stroke of this.confirmedStrokes) {
      this.#drawStroke(stroke);
    }

    if (this.previewStroke) {
      context.save();
      context.globalAlpha = 0.85;
      this.#drawStroke(this.previewStroke);
      context.restore();
    }
  }

  // Repaints only the cursor overlay, leaving the strokes layer untouched so a
  // moving remote cursor doesn't trigger a full history redraw.
  #renderCursors() {
    this.#clearCursors();
    if (this.replaying) {
      return;
    }

    for (const [userId, cursor] of this.remoteCursors.entries()) {
      this.#drawCursor(userId, cursor);
    }
  }

  #clearCursors() {
    const context = this.cursorContext;
    if (!context) {
      return;
    }
    context.clearRect(0, 0, this.cursorCanvas.width, this.cursorCanvas.height);
  }

  #drawStroke(stroke) {
    const points = stroke.points ?? stroke.Points ?? [];
    if (points.length === 0) {
      return;
    }

    drawStrokePath(this.context, points, {
      dpr: this.devicePixelRatio,
      color: stroke.color ?? stroke.Color ?? DEFAULT_COLOR,
      baseWidth: stroke.width ?? stroke.Width ?? 4
    });
  }

  #drawCursor(userId, cursor) {
    const context = this.cursorContext;
    const color = colorFromUserId(userId);
    const x = cursor.x;
    const y = cursor.y;

    context.save();
    context.fillStyle = color;
    context.strokeStyle = '#ffffff';
    context.lineWidth = 2 * this.devicePixelRatio;
    context.beginPath();
    context.arc(x * this.devicePixelRatio, y * this.devicePixelRatio, 5 * this.devicePixelRatio, 0, Math.PI * 2);
    context.fill();
    context.stroke();
    context.restore();
  }
}

function normalizeStroke(stroke) {
  return {
    id: stroke.id ?? stroke.Id,
    color: stroke.color ?? stroke.Color ?? DEFAULT_COLOR,
    width: stroke.width ?? stroke.Width ?? 4,
    points: (stroke.points ?? stroke.Points ?? []).map(point => ({
      x: point.x ?? point.X,
      y: point.y ?? point.Y,
      pressure: point.pressure ?? point.Pressure ?? null,
      timeOffset: point.timeOffset ?? point.TimeOffset ?? 0
    }))
  };
}

function getStrokeId(stroke) {
  return stroke.id ?? stroke.Id;
}

function simplifyPointsVisvalingamWhyatt(points, thresholdCssPx, baseWidthCssPx = 4) {
  if (!Array.isArray(points) || points.length <= 2) {
    return points;
  }

  // Keep the public threshold in CSS pixels and convert to an area-like scale
  // used by the VW triangle score.
  const t = Math.max(0, thresholdCssPx);
  const areaThreshold = 0.5 * t * t;
  const kept = points.map(point => ({ point, removed: false }));

  while (true) {
    let minScore = Infinity;
    let minIndex = -1;

    for (let i = 1; i < kept.length - 1; i += 1) {
      if (kept[i].removed) {
        continue;
      }

      const prev = findPreviousKept(kept, i);
      const next = findNextKept(kept, i);
      if (prev === -1 || next === -1) {
        continue;
      }

      const score = triangleScoreWithPressure(
        kept[prev].point,
        kept[i].point,
        kept[next].point,
        baseWidthCssPx
      );
      if (score < minScore) {
        minScore = score;
        minIndex = i;
      }
    }

    if (minIndex === -1 || minScore >= areaThreshold) {
      break;
    }

    kept[minIndex].removed = true;
  }

  return kept.filter(entry => !entry.removed).map(entry => entry.point);
}

function findPreviousKept(entries, index) {
  for (let i = index - 1; i >= 0; i -= 1) {
    if (!entries[i].removed) {
      return i;
    }
  }

  return -1;
}

function findNextKept(entries, index) {
  for (let i = index + 1; i < entries.length; i += 1) {
    if (!entries[i].removed) {
      return i;
    }
  }

  return -1;
}

function triangleArea(a, b, c) {
  const ax = a.x ?? a.X;
  const ay = a.y ?? a.Y;
  const bx = b.x ?? b.X;
  const by = b.y ?? b.Y;
  const cx = c.x ?? c.X;
  const cy = c.y ?? c.Y;

  return Math.abs((ax * (by - cy) + bx * (cy - ay) + cx * (ay - by)) / 2);
}

function triangleScoreWithPressure(a, b, c, baseWidthCssPx) {
  const geometryArea = triangleArea(a, b, c);
  const chordLength = pointDistance(a, c);
  const wa = momentaryWidthCssPx(a, baseWidthCssPx);
  const wb = momentaryWidthCssPx(b, baseWidthCssPx);
  const wc = momentaryWidthCssPx(c, baseWidthCssPx);
  const widthCurvature = Math.abs(wa - (2 * wb) + wc);
  const widthArea = 0.5 * chordLength * widthCurvature;
  return geometryArea + widthArea;
}

function momentaryWidthCssPx(point, baseWidthCssPx) {
  return Math.max(0, baseWidthCssPx) * pointPressureFactor(point);
}

function pointPressureFactor(point) {
  const pressure = point.pressure ?? point.Pressure;
  if (pressure == null) {
    return 1;
  }

  return Math.min(1, Math.max(0, pressure));
}

function pointDistance(a, b) {
  const ax = a.x ?? a.X;
  const ay = a.y ?? a.Y;
  const bx = b.x ?? b.X;
  const by = b.y ?? b.Y;
  return Math.hypot(ax - bx, ay - by);
}

// crypto.randomUUID() is only available in secure contexts (HTTPS or
// localhost). When the app is served over plain HTTP on a LAN address it is an
// insecure context and randomUUID is undefined, so fall back to building a
// RFC 4122 v4 UUID from crypto.getRandomValues, which is available everywhere.
function generateStrokeId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;

  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0'));
  return `${hex[0]}${hex[1]}${hex[2]}${hex[3]}-${hex[4]}${hex[5]}-` +
    `${hex[6]}${hex[7]}-${hex[8]}${hex[9]}-` +
    `${hex[10]}${hex[11]}${hex[12]}${hex[13]}${hex[14]}${hex[15]}`;
}

export function colorFromUserId(userId) {
  let hash = 0;
  for (let i = 0; i < userId.length; i += 1) {
    hash = (hash * 31 + userId.charCodeAt(i)) | 0;
  }

  const hue = Math.abs(hash) % 360;
  return `hsl(${hue} 75% 45%)`;
}

// Draws a stroke as a sequence of per-segment paths so line width can follow
// pen pressure. Each point's pressure (0..1, or null when the input device
// reports none, e.g. mouse without force) scales baseWidth, with null treated
// as full pressure so non-pressure input keeps the configured width. Canvas 2D
// cannot vary lineWidth within a single path, hence one stroke() per segment.
// upToMs limits drawing to points at or before a given time offset (replay).
export function drawStrokePath(context, points, { dpr, color, baseWidth, upToMs = Infinity }) {
  if (points.length === 0) {
    return;
  }

  context.strokeStyle = color;
  context.lineCap = 'round';
  context.lineJoin = 'round';

  const pressureFactor = point => {
    const pressure = point.pressure ?? point.Pressure;
    return pressure == null ? 1 : pressure;
  };

  let previous = null;
  let drewSegment = false;
  let lastVisible = null;
  for (const point of points) {
    const offset = point.timeOffset ?? point.TimeOffset ?? 0;
    if (offset > upToMs) {
      break;
    }

    const current = {
      x: (point.x ?? point.X) * dpr,
      y: (point.y ?? point.Y) * dpr,
      factor: pressureFactor(point)
    };

    if (previous) {
      context.beginPath();
      context.lineWidth = baseWidth * ((previous.factor + current.factor) / 2) * dpr;
      context.moveTo(previous.x, previous.y);
      context.lineTo(current.x, current.y);
      context.stroke();
      drewSegment = true;
    }

    previous = current;
    lastVisible = current;
  }

  // A single click/tap (or the first frame of a replayed stroke) yields one
  // isolated point with no segment to stroke, so render it as a filled dot the
  // size of the line at that point. Without this, single taps leave no mark.
  if (!drewSegment && lastVisible) {
    const radius = Math.max(baseWidth * lastVisible.factor * dpr, dpr) / 2;
    context.beginPath();
    context.fillStyle = color;
    context.arc(lastVisible.x, lastVisible.y, radius, 0, Math.PI * 2);
    context.fill();
  }
}
