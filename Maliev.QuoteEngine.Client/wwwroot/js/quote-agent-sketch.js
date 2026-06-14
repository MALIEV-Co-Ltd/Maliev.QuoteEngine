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

function drawSoftSegment(ctx, from, control, to, state) {
    const pressure = Math.max(0.08, Math.min(1, control.pressure));
    const width = state.mode === "eraser" ? 14 + pressure * 18 : 1.2 + pressure * 7.2;

    ctx.save();
    ctx.strokeStyle = state.mode === "eraser" ? "#ffffff" : state.color;
    ctx.globalAlpha = state.mode === "eraser" ? 1 : 0.62 + pressure * 0.38;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.lineWidth = width;
    ctx.shadowColor = state.mode === "eraser" ? "transparent" : state.color;
    ctx.shadowBlur = state.mode === "eraser" ? 0 : Math.max(0.4, width * 0.22);
    ctx.beginPath();
    ctx.moveTo(from.x, from.y);
    ctx.quadraticCurveTo(control.x, control.y, to.x, to.y);
    ctx.stroke();
    ctx.restore();
}

function insertImageData(entry, dataUrl) {
    return new Promise((resolve) => {
        const image = new Image();
        image.onload = () => {
            const { canvas, ctx } = entry;
            const padding = 56;
            const availableWidth = Math.max(canvas.width - padding * 2, 1);
            const availableHeight = Math.max(canvas.height - padding * 2, 1);
            const scale = Math.min(availableWidth / image.naturalWidth, availableHeight / image.naturalHeight, 1);
            const width = image.naturalWidth * scale;
            const height = image.naturalHeight * scale;
            const x = (canvas.width - width) / 2;
            const y = (canvas.height - height) / 2;

            ctx.save();
            ctx.globalAlpha = 0.98;
            ctx.drawImage(image, x, y, width, height);
            ctx.restore();
            resolve(true);
        };
        image.onerror = () => resolve(false);
        image.src = dataUrl;
    });
}

function fileToDataUrl(file) {
    return new Promise((resolve) => {
        const reader = new FileReader();
        reader.onload = () => resolve(typeof reader.result === "string" ? reader.result : "");
        reader.onerror = () => resolve("");
        reader.readAsDataURL(file);
    });
}

async function insertClipboardFile(entry, file) {
    const dataUrl = await fileToDataUrl(file);
    if (!dataUrl) {
        return false;
    }

    const inserted = await insertImageData(entry, dataUrl);
    if (inserted && entry.dotNetRef) {
        await entry.dotNetRef.invokeMethodAsync("SetSketchImageNameAsync", file.name || "Clipboard image");
    }

    return inserted;
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

export function initSketchCanvas(canvasId, dotNetRef) {
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
        mode: "ink",
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
        drawSoftSegment(ctx, state.lastMidpoint, state.lastPoint, nextMidpoint, state);

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
    const paste = async (event) => {
        const files = Array.from(event.clipboardData?.files || []);
        const image = files.find(file => file.type.startsWith("image/"));
        if (!image) {
            return;
        }

        event.preventDefault();
        const entry = canvases.get(canvasId);
        if (entry) {
            await insertClipboardFile(entry, image);
        }
    };

    window.addEventListener("paste", paste);
    canvases.set(canvasId, { canvas, ctx, state, start, move, stop, paste, dotNetRef });
}

export function setSketchBrushColor(canvasId, color) {
    const entry = canvases.get(canvasId);
    if (!entry) {
        return;
    }

    entry.state.color = color || "#161616";
    entry.state.mode = "ink";
}

export function setSketchEraser(canvasId) {
    const entry = canvases.get(canvasId);
    if (!entry) {
        return;
    }

    entry.state.mode = "eraser";
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

export async function insertSketchImage(canvasId, dataUrl) {
    const entry = canvases.get(canvasId);
    if (!entry || !dataUrl) {
        return false;
    }

    return await insertImageData(entry, dataUrl);
}

export async function pasteSketchImage(canvasId) {
    const entry = canvases.get(canvasId);
    if (!entry) {
        return false;
    }

    if (!navigator.clipboard?.read) {
        return false;
    }

    try {
        const items = await navigator.clipboard.read();
        for (const item of items) {
            const imageType = item.types.find(type => type.startsWith("image/"));
            if (!imageType) {
                continue;
            }

            const blob = await item.getType(imageType);
            return await insertClipboardFile(entry, new File([blob], "Clipboard image", { type: imageType }));
        }
    } catch {
        return false;
    }

    return false;
}

export function exportSketchCanvas(canvasId) {
    const canvas = getCanvas(canvasId);
    return canvas ? canvas.toDataURL("image/png") : "";
}

export function disposeSketchCanvas(canvasId) {
    const entry = canvases.get(canvasId);
    if (!entry) {
        return;
    }

    entry.canvas.removeEventListener("pointerdown", entry.start);
    entry.canvas.removeEventListener("pointermove", entry.move);
    entry.canvas.removeEventListener("pointerup", entry.stop);
    entry.canvas.removeEventListener("pointercancel", entry.stop);
    entry.canvas.removeEventListener("lostpointercapture", entry.stop);
    window.removeEventListener("paste", entry.paste);
    canvases.delete(canvasId);
}
