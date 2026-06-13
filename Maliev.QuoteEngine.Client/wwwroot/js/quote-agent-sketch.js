const canvases = new Map();

function getCanvas(canvasId) {
    const canvas = document.getElementById(canvasId);
    if (!(canvas instanceof HTMLCanvasElement)) {
        return null;
    }

    return canvas;
}

function canvasPoint(canvas, event) {
    const rect = canvas.getBoundingClientRect();
    const scaleX = canvas.width / Math.max(rect.width, 1);
    const scaleY = canvas.height / Math.max(rect.height, 1);

    return {
        x: (event.clientX - rect.left) * scaleX,
        y: (event.clientY - rect.top) * scaleY
    };
}

function pressureFor(event) {
    if (event.pressure && event.pressure > 0) {
        return Math.min(1, Math.max(0.08, event.pressure));
    }

    return event.pointerType === "mouse" ? 0.42 : 0.55;
}

function midpoint(a, b) {
    return {
        x: (a.x + b.x) / 2,
        y: (a.y + b.y) / 2,
        pressure: (a.pressure + b.pressure) / 2
    };
}

function drawSoftSegment(ctx, from, control, to, color) {
    const pressure = Math.max(0.08, Math.min(1, control.pressure));
    const width = 1.2 + pressure * 7.2;

    ctx.save();
    ctx.strokeStyle = color;
    ctx.globalAlpha = 0.62 + pressure * 0.38;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.lineWidth = width;
    ctx.shadowColor = color;
    ctx.shadowBlur = Math.max(0.4, width * 0.22);
    ctx.beginPath();
    ctx.moveTo(from.x, from.y);
    ctx.quadraticCurveTo(control.x, control.y, to.x, to.y);
    ctx.stroke();
    ctx.restore();
}

function drawPaper(ctx, canvas) {
    ctx.save();
    ctx.fillStyle = "#ffffff";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.strokeStyle = "#e7e7e7";
    ctx.lineWidth = 1;

    for (let x = 40; x < canvas.width; x += 40) {
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, canvas.height);
        ctx.stroke();
    }

    for (let y = 40; y < canvas.height; y += 40) {
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(canvas.width, y);
        ctx.stroke();
    }

    ctx.restore();
}

export function initSketchCanvas(canvasId) {
    const canvas = getCanvas(canvasId);
    if (!canvas || canvases.has(canvasId)) {
        return;
    }

    const ctx = canvas.getContext("2d");
    if (!ctx) {
        return;
    }

    const state = {
        color: "#161616",
        drawing: false,
        lastPoint: null,
        lastMidpoint: null,
        pointerId: null
    };

    drawPaper(ctx, canvas);

    const start = (event) => {
        event.preventDefault();
        state.drawing = true;
        state.pointerId = event.pointerId;
        state.lastPoint = { ...canvasPoint(canvas, event), pressure: pressureFor(event) };
        state.lastMidpoint = state.lastPoint;
        canvas.setPointerCapture(event.pointerId);
    };

    const move = (event) => {
        if (!state.drawing || state.pointerId !== event.pointerId || !state.lastPoint) {
            return;
        }

        event.preventDefault();
        const point = { ...canvasPoint(canvas, event), pressure: pressureFor(event) };
        const nextMidpoint = midpoint(state.lastPoint, point);
        drawSoftSegment(ctx, state.lastMidpoint, state.lastPoint, nextMidpoint, state.color);

        state.lastPoint = point;
        state.lastMidpoint = nextMidpoint;
    };

    const stop = (event) => {
        if (state.pointerId === event.pointerId) {
            state.drawing = false;
            state.pointerId = null;
            state.lastPoint = null;
            state.lastMidpoint = null;
        }
    };

    canvas.addEventListener("pointerdown", start);
    canvas.addEventListener("pointermove", move);
    canvas.addEventListener("pointerup", stop);
    canvas.addEventListener("pointercancel", stop);
    canvas.addEventListener("lostpointercapture", stop);
    canvases.set(canvasId, { canvas, ctx, state, start, move, stop });
}

export function setSketchBrushColor(canvasId, color) {
    const entry = canvases.get(canvasId);
    if (!entry) {
        return;
    }

    entry.state.color = color || "#161616";
}

export function clearSketchCanvas(canvasId) {
    const entry = canvases.get(canvasId);
    if (!entry) {
        const canvas = getCanvas(canvasId);
        const ctx = canvas?.getContext("2d");
        if (canvas && ctx) {
            drawPaper(ctx, canvas);
        }

        return;
    }

    drawPaper(entry.ctx, entry.canvas);
}

export function exportSketchCanvas(canvasId) {
    const canvas = getCanvas(canvasId);
    return canvas ? canvas.toDataURL("image/png") : "";
}
