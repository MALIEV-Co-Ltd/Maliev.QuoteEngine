const canvases = new Map();
const resizeHandleSize = 30;

function getCanvas(canvasId) {
    const canvas = document.getElementById(canvasId);
    return canvas instanceof HTMLCanvasElement ? canvas : null;
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

function drawSoftSegment(ctx, from, control, to, stroke) {
    const pressure = Math.max(0.08, Math.min(1, control.pressure));
    const width = stroke.mode === "eraser" ? 14 + pressure * 18 : 1.2 + pressure * 7.2;

    ctx.save();
    ctx.globalCompositeOperation = stroke.mode === "eraser" ? "destination-out" : "source-over";
    ctx.strokeStyle = stroke.mode === "eraser" ? "rgba(0,0,0,1)" : stroke.color;
    ctx.globalAlpha = stroke.mode === "eraser" ? 1 : 0.62 + pressure * 0.38;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.lineWidth = width;
    ctx.shadowColor = stroke.mode === "eraser" ? "transparent" : stroke.color;
    ctx.shadowBlur = stroke.mode === "eraser" ? 0 : Math.max(0.4, width * 0.22);
    ctx.beginPath();
    ctx.moveTo(from.x, from.y);
    ctx.quadraticCurveTo(control.x, control.y, to.x, to.y);
    ctx.stroke();
    ctx.restore();
}

function renderStroke(ctx, stroke) {
    if (!stroke.points || stroke.points.length === 0) {
        return;
    }

    if (stroke.points.length === 1) {
        const point = stroke.points[0];
        drawSoftSegment(ctx, point, point, point, stroke);
        return;
    }

    let lastPoint = stroke.points[0];
    let lastMidpoint = lastPoint;
    for (let index = 1; index < stroke.points.length; index++) {
        const point = stroke.points[index];
        const nextMidpoint = midpoint(lastPoint, point);
        drawSoftSegment(ctx, lastMidpoint, lastPoint, nextMidpoint, stroke);
        lastPoint = point;
        lastMidpoint = nextMidpoint;
    }
}

function renderImage(ctx, item, includeEditor) {
    ctx.save();
    ctx.globalAlpha = 0.98;
    ctx.drawImage(item.image, item.x, item.y, item.width, item.height);
    ctx.restore();

    if (!includeEditor || !item.selected) {
        return;
    }

    ctx.save();
    ctx.strokeStyle = "#0a72ef";
    ctx.lineWidth = 3;
    ctx.setLineDash([10, 7]);
    ctx.strokeRect(item.x, item.y, item.width, item.height);
    ctx.setLineDash([]);
    ctx.fillStyle = "#0a72ef";
    ctx.strokeStyle = "#ffffff";
    ctx.lineWidth = 2;
    const handle = imageResizeHandle(item);
    ctx.beginPath();
    ctx.roundRect(handle.x, handle.y, handle.size, handle.size, 7);
    ctx.fill();
    ctx.stroke();
    ctx.restore();
}

function renderEraserIndicator(ctx, state) {
    if (state.mode !== "eraser" || !state.cursorPoint) {
        return;
    }

    ctx.save();
    ctx.strokeStyle = "#111827";
    ctx.fillStyle = "rgba(255,255,255,.56)";
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(state.cursorPoint.x, state.cursorPoint.y, state.eraserRadius, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
    ctx.restore();
}

function renderScene(entry, includeEditor = true) {
    const { canvas, ctx, state } = entry;
    drawPaper(ctx, canvas);
    for (const item of state.images) {
        renderImage(ctx, item, includeEditor);
    }
    for (const stroke of state.strokes) {
        renderStroke(ctx, stroke);
    }
    if (includeEditor) {
        renderEraserIndicator(ctx, state);
    }
}

function imageResizeHandle(item) {
    return {
        x: item.x + item.width - resizeHandleSize / 2,
        y: item.y + item.height - resizeHandleSize / 2,
        size: resizeHandleSize
    };
}

function hitTestHandle(item, point) {
    const handle = imageResizeHandle(item);
    return point.x >= handle.x &&
        point.x <= handle.x + handle.size &&
        point.y >= handle.y &&
        point.y <= handle.y + handle.size;
}

function hitTestImage(state, point) {
    for (let index = state.images.length - 1; index >= 0; index--) {
        const item = state.images[index];
        const inside = point.x >= item.x &&
            point.x <= item.x + item.width &&
            point.y >= item.y &&
            point.y <= item.y + item.height;
        if (inside) {
            return {
                item,
                mode: hitTestHandle(item, point) ? "resize" : "move"
            };
        }
    }

    return null;
}

function selectImage(state, selected) {
    for (const item of state.images) {
        item.selected = item === selected;
    }
}

function bringImageToFront(state, selected) {
    const index = state.images.indexOf(selected);
    if (index < 0 || index === state.images.length - 1) {
        return;
    }

    state.images.splice(index, 1);
    state.images.push(selected);
}

function insertImageData(entry, dataUrl) {
    return new Promise((resolve) => {
        const image = new Image();
        image.onload = () => {
            const { canvas, state } = entry;
            const padding = 72;
            const availableWidth = Math.max(canvas.width - padding * 2, 1);
            const availableHeight = Math.max(canvas.height - padding * 2, 1);
            const scale = Math.min(availableWidth / image.naturalWidth, availableHeight / image.naturalHeight, 1);
            const width = image.naturalWidth * scale;
            const height = image.naturalHeight * scale;
            const item = {
                image,
                dataUrl,
                x: (canvas.width - width) / 2,
                y: (canvas.height - height) / 2,
                width,
                height,
                aspectRatio: width / Math.max(height, 1),
                selected: true
            };

            selectImage(state, null);
            state.images.push(item);
            renderScene(entry);
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

function updateCanvasMode(canvas, state) {
    canvas.classList.toggle("is-eraser", state.mode === "eraser");
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
        transform: null,
        currentStroke: null,
        pointerId: null,
        cursorPoint: null,
        eraserRadius: 18,
        strokes: [],
        images: []
    };

    const entry = { canvas, ctx, state, start: null, move: null, stop: null, leave: null, paste: null, dotNetRef };

    const start = (event) => {
        event.preventDefault();
        const point = { ...canvasPoint(canvas, event), pressure: pressureFor(event) };
        const hit = hitTestImage(state, point);

        state.pointerId = event.pointerId;
        canvas.setPointerCapture(event.pointerId);

        if (hit) {
            selectImage(state, hit.item);
            bringImageToFront(state, hit.item);
            state.transform = {
                item: hit.item,
                mode: hit.mode,
                startPoint: point,
                startX: hit.item.x,
                startY: hit.item.y,
                startWidth: hit.item.width,
                startHeight: hit.item.height
            };
            renderScene(entry);
            return;
        }

        selectImage(state, null);
        state.drawing = true;
        state.currentStroke = {
            color: state.color,
            mode: state.mode,
            points: [point]
        };
        state.strokes.push(state.currentStroke);
        state.cursorPoint = point;
        renderScene(entry);
    };

    const move = (event) => {
        const point = { ...canvasPoint(canvas, event), pressure: pressureFor(event) };
        state.cursorPoint = point;

        if (state.pointerId !== event.pointerId) {
            if (state.mode === "eraser") {
                renderScene(entry);
            }
            return;
        }

        event.preventDefault();

        if (state.transform) {
            const { item, mode, startPoint, startX, startY, startWidth, startHeight } = state.transform;
            const dx = point.x - startPoint.x;
            const dy = point.y - startPoint.y;

            if (mode === "move") {
                item.x = Math.max(0, Math.min(canvas.width - item.width, startX + dx));
                item.y = Math.max(0, Math.min(canvas.height - item.height, startY + dy));
            } else {
                const nextWidth = Math.max(40, Math.min(canvas.width - item.x, startWidth + dx));
                const nextHeight = Math.max(40, nextWidth / item.aspectRatio);
                item.width = nextWidth;
                item.height = Math.min(nextHeight, canvas.height - item.y);
                item.width = item.height * item.aspectRatio;
            }

            renderScene(entry);
            return;
        }

        if (!state.drawing || !state.currentStroke) {
            if (state.mode === "eraser") {
                renderScene(entry);
            }
            return;
        }

        state.currentStroke.points.push(point);
        renderScene(entry);
    };

    const stop = (event) => {
        if (state.pointerId !== event.pointerId) {
            return;
        }

        state.drawing = false;
        state.transform = null;
        state.currentStroke = null;
        state.pointerId = null;
        renderScene(entry);
    };

    const leave = () => {
        state.cursorPoint = null;
        renderScene(entry);
    };

    const paste = async (event) => {
        const files = Array.from(event.clipboardData?.files || []);
        const image = files.find(file => file.type.startsWith("image/"));
        if (!image) {
            return;
        }

        event.preventDefault();
        await insertClipboardFile(entry, image);
    };

    entry.start = start;
    entry.move = move;
    entry.stop = stop;
    entry.leave = leave;
    entry.paste = paste;
    canvas.addEventListener("pointerdown", start);
    canvas.addEventListener("pointermove", move);
    canvas.addEventListener("pointerup", stop);
    canvas.addEventListener("pointercancel", stop);
    canvas.addEventListener("lostpointercapture", stop);
    canvas.addEventListener("pointerleave", leave);
    window.addEventListener("paste", paste);
    canvases.set(canvasId, entry);
    updateCanvasMode(canvas, state);
    renderScene(entry);
}

export function setSketchBrushColor(canvasId, color) {
    const entry = canvases.get(canvasId);
    if (!entry) {
        return;
    }

    entry.state.color = color || "#161616";
    entry.state.mode = "ink";
    updateCanvasMode(entry.canvas, entry.state);
    renderScene(entry);
}

export function setSketchEraser(canvasId) {
    const entry = canvases.get(canvasId);
    if (!entry) {
        return;
    }

    entry.state.mode = "eraser";
    updateCanvasMode(entry.canvas, entry.state);
    renderScene(entry);
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

    entry.state.strokes = [];
    entry.state.images = [];
    entry.state.transform = null;
    entry.state.currentStroke = null;
    renderScene(entry);
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
    const entry = canvases.get(canvasId);
    if (!entry) {
        const canvas = getCanvas(canvasId);
        return canvas ? canvas.toDataURL("image/png") : "";
    }

    renderScene(entry, false);
    const dataUrl = entry.canvas.toDataURL("image/png");
    renderScene(entry, true);
    return dataUrl;
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
    entry.canvas.removeEventListener("pointerleave", entry.leave);
    window.removeEventListener("paste", entry.paste);
    canvases.delete(canvasId);
}
