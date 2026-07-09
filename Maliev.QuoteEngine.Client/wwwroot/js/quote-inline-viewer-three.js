/**
 * Copyright (c) MALIEV Co., Ltd. All rights reserved.
 * www.maliev.com
 *
 * Lightweight inline 3D preview for procedurally-built (replicad) CAD shapes
 * shown inline in the QuoteEngine agent chat. Builds a mesh directly from the
 * vertex/triangle/normal buffers produced by the replicad web worker — no file
 * loading involved. Powered by three.js (self-hosted ES module, no bundler).
 *
 * Renders Y-up (matches the worker's coordinate convention) — unlike the main
 * CAD viewers (quote-part-viewer-three.js / part-viewer-three.js) this preview does not
 * apply a Z-up CAD rotation (the worker outputs Y-up, matching three.js defaults).
 */

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const pendingBuilds = new Map();
let buildCounter = 0;
const scenes = new Map();

function getWorker() {
    return new Promise((resolve, reject) => {
        if (window.replicadWorker) { resolve(window.replicadWorker); return; }
        const timeout = setTimeout(() => reject(new Error('3D worker not ready')), 10000);
        const check = () => {
            if (window.replicadWorker) { clearTimeout(timeout); resolve(window.replicadWorker); return; }
            setTimeout(check, 100);
        };
        check();
    });
}

function showPreviewError(container, message) {
    const p = document.createElement('p');
    p.className = 'qe-inline-viewer-error';
    p.textContent = message || '3D preview could not be loaded';
    container.replaceChildren(p);
}

function showLoadingMessage(container, message) {
    const div = document.createElement('div');
    div.className = 'qe-inline-viewer-loading';
    div.textContent = message;
    container.replaceChildren(div);
}

function failPreview(container, message) {
    showPreviewError(container, message);
    throw new Error(message || '3D preview could not be loaded');
}

function parseCommandsPayload(commandsPayload) {
    if (typeof commandsPayload === 'string') return unwrapCommandsPayload(JSON.parse(commandsPayload));
    return unwrapCommandsPayload(commandsPayload);
}

function unwrapCommandsPayload(value) {
    if (!value || Array.isArray(value) || typeof value !== 'object') return value;
    for (const key of ['commands', 'cadCommands', 'cad_commands', 'model', 'preview', 'cad', 'geometry']) {
        if (Array.isArray(value[key])) return value[key];
    }
    return value;
}

function normalizeNumericArray(value) {
    if (value == null) return value;
    if (Array.isArray(value)) return value;

    if (typeof value === 'object') {
        const ordered = [];
        const used = new Set();
        const add = (...names) => {
            for (const name of names) {
                if (Object.prototype.hasOwnProperty.call(value, name)) {
                    const numeric = Number(value[name]);
                    if (Number.isFinite(numeric)) { ordered.push(numeric); used.add(name); return; }
                }
            }
        };
        add('radiusBottom', 'bottomRadius');
        add('radiusTop', 'topRadius');
        add('radius', 'r');
        add('width', 'w', 'x');
        add('depth', 'length', 'd', 'y');
        add('height', 'h', 'z');
        for (const [key, raw] of Object.entries(value)) {
            if (used.has(key)) continue;
            const numeric = Number(raw);
            if (Number.isFinite(numeric)) ordered.push(numeric);
        }
        return ordered;
    }

    const numeric = Number(value);
    return Number.isFinite(numeric) ? [numeric] : value;
}

function normalizeCommandsForWorker(commands) {
    return commands.map((source) => {
        const command = { ...source };
        command.params = normalizeNumericArray(command.params);
        command.parameters = normalizeNumericArray(command.parameters);
        command.size = normalizeNumericArray(command.size);

        if (command.profile && typeof command.profile === 'object') {
            command.profile = { ...command.profile };
            command.profile.params = normalizeNumericArray(command.profile.params);
            command.profile.parameters = normalizeNumericArray(command.profile.parameters);
            command.profile.size = normalizeNumericArray(command.profile.size);
            if (Array.isArray(command.profile.segments)) {
                command.profile.segments = command.profile.segments.map((sourceSegment) => {
                    const segment = { ...sourceSegment };
                    segment.params = normalizeNumericArray(segment.params);
                    segment.parameters = normalizeNumericArray(segment.parameters);
                    return segment;
                });
            }
        }
        return command;
    });
}

function validateMeshData(meshData) {
    if (!meshData || !meshData.vertices || !meshData.triangles || !meshData.normals
        || meshData.vertices.byteLength === 0 || meshData.triangles.byteLength === 0) {
        throw new Error('3D preview mesh is empty');
    }
}

function createScene(container, meshData) {
    validateMeshData(meshData);

    const vertices = new Float32Array(meshData.vertices);
    const triangles = new Uint32Array(meshData.triangles);
    const normals = new Float32Array(meshData.normals);
    if (vertices.length < 3 || triangles.length < 3 || normals.length < 3) {
        throw new Error('3D preview mesh is empty');
    }

    let minX = Infinity, minY = Infinity, minZ = Infinity;
    let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
    for (let j = 0; j < vertices.length; j += 3) {
        const vx = vertices[j], vy = vertices[j + 1], vz = vertices[j + 2];
        if (!Number.isFinite(vx) || !Number.isFinite(vy) || !Number.isFinite(vz)) {
            throw new Error('3D preview mesh contains invalid coordinates');
        }
        if (vx < minX) minX = vx; if (vy < minY) minY = vy; if (vz < minZ) minZ = vz;
        if (vx > maxX) maxX = vx; if (vy > maxY) maxY = vy; if (vz > maxZ) maxZ = vz;
    }

    const center = new THREE.Vector3((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
    const diag = Math.sqrt((maxX - minX) ** 2 + (maxY - minY) ** 2 + (maxZ - minZ) ** 2);
    if (!Number.isFinite(diag)) throw new Error('3D preview mesh contains invalid coordinates');
    const radius = Math.max(diag * 1.5, 3);

    const canvas = document.createElement('canvas');
    canvas.style.width = '100%';
    canvas.style.height = '100%';
    canvas.style.display = 'block';
    canvas.style.borderRadius = '8px';

    let renderer = null;
    let resizeObserver = null;

    try {
        container.replaceChildren(canvas);

        renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true });
        renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));

        // Professional CAD studio rig, matching quote-part-viewer-three.js.
        // The canvas stays transparent (the chat card supplies the backdrop);
        // only the lighting and edge colors switch with the app theme.
        const scene = new THREE.Scene();
        const lights = { current: [] };
        const isDarkTheme = () =>
            document.documentElement.getAttribute('data-maliev-theme') === 'dark';
        const applyLights = () => {
            for (const light of lights.current) scene.remove(light);
            lights.current = [];
            const dark = isDarkTheme();
            const rig = dark
                ? [
                    new THREE.DirectionalLight(0xffffff, 2.7),
                    new THREE.DirectionalLight(0xdce6f5, 1.5),
                    new THREE.DirectionalLight(0xf2f6ff, 0.7),
                    new THREE.HemisphereLight(0x9fb2cc, 0x2c313a, 0.9),
                    new THREE.AmbientLight(0x545e6d, 0.5),
                ]
                : [
                    new THREE.DirectionalLight(0xfffaf2, 2.6),
                    new THREE.DirectionalLight(0xd9ebff, 1.5),
                    new THREE.DirectionalLight(0xfffcf7, 0.5),
                    new THREE.AmbientLight(0xe0e6f5, 0.55),
                ];
            const dirs = [[0.5, 1.1, 0.8], [-0.85, 0.4, 0.2], [-0.1, -0.7, 0.4]];
            let dirIndex = 0;
            for (const light of rig) {
                if (light.isDirectionalLight) {
                    const d = dirs[Math.min(dirIndex, dirs.length - 1)];
                    light.position.set(d[0], d[1], d[2]).multiplyScalar(radius * 2);
                    dirIndex += 1;
                }
                scene.add(light);
                lights.current.push(light);
            }
        };
        applyLights();

        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(vertices, 3));
        geometry.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
        geometry.setIndex(new THREE.BufferAttribute(triangles, 1));
        const material = new THREE.MeshStandardMaterial({ color: 0xc7ccd1, metalness: 0.18, roughness: 0.55 });
        const mesh = new THREE.Mesh(geometry, material);
        scene.add(mesh);

        // CAD-style feature edges.
        const edgeGeometry = new THREE.EdgesGeometry(geometry, 30);
        const edgeMaterial = new THREE.LineBasicMaterial({
            color: isDarkTheme() ? 0xcdd3de : 0x1f2226,
            transparent: true,
            opacity: 0.55,
        });
        const edgeLines = new THREE.LineSegments(edgeGeometry, edgeMaterial);
        scene.add(edgeLines);

        const aspect = (canvas.clientWidth || 1) / (canvas.clientHeight || 1);
        const camera = new THREE.PerspectiveCamera(45, aspect, radius * 0.01, radius * 50);
        camera.position.set(
            center.x - radius * 0.7,
            center.y + radius * 0.7,
            center.z + radius * 0.7,
        );
        camera.lookAt(center);

        const controls = new OrbitControls(camera, canvas);
        controls.target.copy(center);
        controls.minDistance = radius * 0.2;
        controls.maxDistance = radius * 5;

        // Render on demand only: chat threads can hold several previews at
        // once, and a continuous rAF loop per card burns mobile GPU/battery.
        const renderFrame = () => renderer.render(scene, camera);
        const resizeAndRender = () => {
            const width = container.clientWidth || 1;
            const height = container.clientHeight || 1;
            renderer.setSize(width, height, false);
            camera.aspect = width / height;
            camera.updateProjectionMatrix();
            renderFrame();
        };
        controls.addEventListener('change', renderFrame);
        controls.update();
        resizeAndRender();

        resizeObserver = new ResizeObserver(resizeAndRender);
        resizeObserver.observe(container);

        // Follow live theme switches (lighting + edge color).
        const themeObserver = new MutationObserver(() => {
            applyLights();
            edgeMaterial.color.set(isDarkTheme() ? 0xcdd3de : 0x1f2226);
            renderFrame();
        });
        themeObserver.observe(document.documentElement, {
            attributes: true,
            attributeFilter: ['data-maliev-theme'],
        });

        return { renderer, scene, canvas, camera, controls, center, radius, resizeObserver, themeObserver, renderFrame };
    } catch (creationError) {
        if (resizeObserver) resizeObserver.disconnect();
        if (renderer) renderer.dispose();
        if (canvas.parentNode) canvas.parentNode.removeChild(canvas);
        throw creationError;
    }
}

function disposePreview(containerId) {
    const entry = scenes.get(containerId);
    if (!entry) return;
    if (entry.frameHandle) cancelAnimationFrame(entry.frameHandle);
    if (entry.themeObserver) entry.themeObserver.disconnect();
    if (entry.resizeObserver) entry.resizeObserver.disconnect();
    if (entry.controls) entry.controls.dispose();
    if (entry.scene) {
        entry.scene.traverse((n) => {
            if (n.geometry) n.geometry.dispose();
            if (n.material) n.material.dispose();
        });
    }
    if (entry.renderer) entry.renderer.dispose();
    if (entry.canvas && entry.canvas.parentNode) entry.canvas.parentNode.removeChild(entry.canvas);
    scenes.delete(containerId);
}

async function createPreview(containerId, commandsPayload) {
    const container = document.getElementById(containerId);
    if (!container) return;

    let commands;
    try {
        commands = parseCommandsPayload(commandsPayload);
    } catch (e) {
        failPreview(container, 'Could not parse 3D commands');
    }

    if (!Array.isArray(commands) || commands.length === 0) {
        failPreview(container, 'No shapes to display');
    }
    commands = normalizeCommandsForWorker(commands);

    let worker;
    try {
        worker = await getWorker();
    } catch (e) {
        failPreview(container, '3D engine not available');
    }

    showLoadingMessage(container, 'Building 3D model…');

    try {
        const buildId = `bl_${++buildCounter}`;
        const result = await new Promise((resolve, reject) => {
            pendingBuilds.set(buildId, { resolve, reject });

            const cleanup = () => {
                worker.removeEventListener('message', handler);
                worker.removeEventListener('error', errorHandler);
                pendingBuilds.delete(buildId);
                clearTimeout(timeout);
            };
            const timeout = setTimeout(() => { cleanup(); reject(new Error('3D model build timed out')); }, 20000);
            const handler = (e) => {
                const data = e.data;
                if (data.type === 'result' && data.id === buildId) { cleanup(); resolve(data); }
                else if (data.type === 'error' && data.id === buildId) { cleanup(); reject(new Error(data.message)); }
            };
            const errorHandler = (event) => { cleanup(); reject(new Error(event.message || '3D worker failed while building the model')); };
            worker.addEventListener('message', handler);
            worker.addEventListener('error', errorHandler);

            try {
                worker.postMessage({ type: 'build', id: buildId, commands });
            } catch (postError) {
                cleanup();
                reject(postError instanceof Error ? postError : new Error('3D worker could not receive the model commands'));
            }
        });

        disposePreview(containerId);
        scenes.set(containerId, createScene(container, result));
    } catch (e) {
        disposePreview(containerId);
        const message = e && e.message ? e.message : '3D preview could not be loaded';
        showPreviewError(container, message);
        throw e instanceof Error ? e : new Error(message);
    }
}

function resetPreviewCamera(containerId) {
    const entry = scenes.get(containerId);
    if (!entry || !entry.camera) return;
    entry.camera.position.set(
        entry.center.x - entry.radius * 0.7,
        entry.center.y + entry.radius * 0.7,
        entry.center.z + entry.radius * 0.7,
    );
    entry.controls.target.copy(entry.center);
    entry.camera.lookAt(entry.center);
    entry.controls.update();
}

function resizePreviews() {
    for (const entry of scenes.values()) {
        if (entry.renderer && entry.canvas) {
            const width = entry.canvas.parentElement?.clientWidth || entry.canvas.clientWidth || 1;
            const height = entry.canvas.parentElement?.clientHeight || entry.canvas.clientHeight || 1;
            entry.renderer.setSize(width, height, false);
            entry.camera.aspect = width / height;
            entry.camera.updateProjectionMatrix();
        }
    }
}

const runtime = {
    createPreview,
    disposePreview,
    resetPreviewCamera,
    resizePreviews,
};

if (window.quoteInlineViewer && typeof window.quoteInlineViewer.registerRuntime === 'function') {
    window.quoteInlineViewer.registerRuntime(runtime);
} else {
    window.quoteInlineViewer = runtime;
}

export { createPreview, disposePreview, resetPreviewCamera, resizePreviews };
