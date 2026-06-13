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
        drawing: false,
        lastPoint: null,
        pointerId: null
    };

    drawPaper(ctx, canvas);

    const start = (event) => {
        event.preventDefault();
        state.drawing = true;
        state.pointerId = event.pointerId;
        state.lastPoint = canvasPoint(canvas, event);
        canvas.setPointerCapture(event.pointerId);
    };

    const move = (event) => {
        if (!state.drawing || state.pointerId !== event.pointerId || !state.lastPoint) {
            return;
        }

        event.preventDefault();
        const point = canvasPoint(canvas, event);
        const pressure = event.pressure && event.pressure > 0 ? event.pressure : 0.55;

        ctx.save();
        ctx.strokeStyle = "#161616";
        ctx.lineCap = "round";
        ctx.lineJoin = "round";
        ctx.lineWidth = Math.max(2, pressure * 5);
        ctx.beginPath();
        ctx.moveTo(state.lastPoint.x, state.lastPoint.y);
        ctx.lineTo(point.x, point.y);
        ctx.stroke();
        ctx.restore();

        state.lastPoint = point;
    };

    const stop = (event) => {
        if (state.pointerId === event.pointerId) {
            state.drawing = false;
            state.pointerId = null;
            state.lastPoint = null;
        }
    };

    canvas.addEventListener("pointerdown", start);
    canvas.addEventListener("pointermove", move);
    canvas.addEventListener("pointerup", stop);
    canvas.addEventListener("pointercancel", stop);
    canvas.addEventListener("lostpointercapture", stop);
    canvases.set(canvasId, { canvas, ctx, start, move, stop });
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
