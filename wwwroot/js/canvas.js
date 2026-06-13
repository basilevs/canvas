const DEFAULT_COLOR = '#1e88e5';

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
    this.devicePixelRatio = Math.max(window.devicePixelRatio || 1, 1);

    this.handlePointerDown = this.handlePointerDown.bind(this);
    this.handlePointerMove = this.handlePointerMove.bind(this);
    this.handlePointerUp = this.handlePointerUp.bind(this);
    this.handleResize = this.handleResize.bind(this);

    this.canvas.addEventListener('pointerdown', this.handlePointerDown);
    this.canvas.addEventListener('pointermove', this.handlePointerMove);
    this.canvas.addEventListener('pointerup', this.handlePointerUp);
    this.canvas.addEventListener('pointercancel', this.handlePointerUp);
    window.addEventListener('resize', this.handleResize);
    this.handleResize();
  }

  setDrawingStyle(color, width) {
    this.currentColor = color || DEFAULT_COLOR;
    this.currentWidth = Number.isFinite(width) ? width : 4;
  }

  setSnapshot(strokes) {
    this.confirmedStrokes = Array.isArray(strokes) ? [...strokes] : [];
    this.previewStroke = null;
    this.render();
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

    this.render();
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

    this.render();
  }

  removeRemoteCursor(userId) {
    this.remoteCursors.delete(userId);
    this.render();
  }

  handleResize() {
    const rect = this.canvas.getBoundingClientRect();
    const width = Math.max(Math.floor(rect.width * this.devicePixelRatio), 1);
    const height = Math.max(Math.floor(rect.height * this.devicePixelRatio), 1);

    if (this.canvas.width !== width || this.canvas.height !== height) {
      this.canvas.width = width;
      this.canvas.height = height;
      this.render();
    }
  }

  handlePointerDown(event) {
    if (event.button !== 0) {
      return;
    }

    this.canvas.setPointerCapture(event.pointerId);
    this.currentStroke = {
      id: crypto.randomUUID(),
      points: [],
      color: this.currentColor,
      width: this.currentWidth,
      duration: 0,
      startTime: performance.now()
    };

    this.appendPoint(event);
    this.previewStroke = this.currentStroke;
    this.render();
  }

  handlePointerMove(event) {
    if (!this.currentStroke) {
      this.updateCursor(event);
      return;
    }

    this.appendPoint(event);
    this.previewStroke = this.currentStroke;
    this.render();
  }

  handlePointerUp(event) {
    if (!this.currentStroke) {
      return;
    }

    this.appendPoint(event);
    this.currentStroke.duration = Math.max(0, Math.round(performance.now() - this.currentStroke.startTime));
    const completedStroke = this.currentStroke;
    this.currentStroke = null;
    this.previewStroke = completedStroke;
    this.render();
    this.handlers.onStrokeCompleted?.(completedStroke);
  }

  appendPoint(event) {
    if (!this.currentStroke) {
      return;
    }

    const point = this.toCanvasPoint(event);
    const points = this.currentStroke.points;
    const lastPoint = points[points.length - 1];
    if (lastPoint && lastPoint.x === point.x && lastPoint.y === point.y) {
      return;
    }

    points.push({
      x: point.x,
      y: point.y,
      pressure: event.pressure > 0 ? event.pressure : null,
      timeOffset: Math.max(0, Math.round(performance.now() - this.currentStroke.startTime))
    });
  }

  updateCursor(event) {
    const cursor = this.toCanvasPoint(event);
    this.handlers.onCursorMoved?.(cursor.x, cursor.y);
  }

  toCanvasPoint(event) {
    const rect = this.canvas.getBoundingClientRect();
    return {
      x: (event.clientX - rect.left) * this.devicePixelRatio,
      y: (event.clientY - rect.top) * this.devicePixelRatio
    };
  }

  render() {
    const context = this.context;
    if (!context) {
      return;
    }

    const width = this.canvas.width;
    const height = this.canvas.height;
    context.clearRect(0, 0, width, height);
    context.fillStyle = getComputedStyle(this.canvas).backgroundColor || '#ffffff';
    context.fillRect(0, 0, width, height);

    for (const stroke of this.confirmedStrokes) {
      this.drawStroke(stroke);
    }

    if (this.previewStroke) {
      context.save();
      context.globalAlpha = 0.85;
      this.drawStroke(this.previewStroke);
      context.restore();
    }

    for (const [userId, cursor] of this.remoteCursors.entries()) {
      this.drawCursor(userId, cursor);
    }
  }

  drawStroke(stroke) {
    const points = stroke.points ?? stroke.Points ?? [];
    if (points.length === 0) {
      return;
    }

    const context = this.context;
    context.strokeStyle = stroke.color ?? stroke.Color ?? DEFAULT_COLOR;
    context.lineWidth = (stroke.width ?? stroke.Width ?? 4) * this.devicePixelRatio;
    context.lineCap = 'round';
    context.lineJoin = 'round';
    context.beginPath();

    context.moveTo(points[0].x * this.devicePixelRatio, points[0].y * this.devicePixelRatio);
    for (let i = 1; i < points.length; i += 1) {
      context.lineTo(points[i].x * this.devicePixelRatio, points[i].y * this.devicePixelRatio);
    }

    context.stroke();
  }

  drawCursor(userId, cursor) {
    const context = this.context;
    const color = colorFromUserId(userId);
    const x = cursor.x;
    const y = cursor.y;

    context.save();
    context.fillStyle = color;
    context.strokeStyle = '#ffffff';
    context.lineWidth = 2 * this.devicePixelRatio;
    context.beginPath();
    context.arc(x, y, 5 * this.devicePixelRatio, 0, Math.PI * 2);
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

function colorFromUserId(userId) {
  let hash = 0;
  for (let i = 0; i < userId.length; i += 1) {
    hash = (hash * 31 + userId.charCodeAt(i)) | 0;
  }

  const hue = Math.abs(hash) % 360;
  return `hsl(${hue} 75% 45%)`;
}
