/**
 * Copyright (c) MALIEV Co., Ltd. All rights reserved.
 * www.maliev.com
 *
 * 3D Part Viewer for Maliev QuoteEngine
 * Powered by three.js (self-hosted ES modules, no bundler) — see wwwroot/lib/three/.
 * Provides JS interop functions for the QePartViewer Blazor component.
 *
 * Adapted from Maliev.Intranet's wwwroot/js/part-viewer-three.js (same architecture,
 * same per-canvas state-map pattern) — keep the two in sync when porting new features.
 *
 * "Realistic" PBR render mode has been removed entirely per product decision — only
 * solid/wireframe/transparent remain, and all renders use a flat professional-CAD-style
 * look. Material/color selection (setPartMaterial/setPartColor) has no remaining visual
 * effect for the same reason — see the no-op stubs at the bottom of this file.
 *
 * Supported formats: .glb, .gltf (GLTFLoader), .stl (STLLoader), .obj (OBJLoader),
 * .3mf (3MFLoader). Native CAD exchange formats (.step, .iges) are always converted
 * to GLB server-side before reaching this script.
 *
 * COORDINATE SYSTEM CONTRACT:
 *   Converted GLB/GLTF models follow the glTF spec: Y-up, mm.
 *   The viewer applies a +90deg X rotation only to GLB/GLTF at load time to display
 *   converted geometry in Z-up (CAD standard): X = right, Y = depth, Z = up.
 *   Native mesh uploads such as STL/OBJ/3MF keep their source orientation.
 *
 * EDGE RENDERING CONTRACT:
 *   Edge overlay defaults to ON for native CAD formats (.step/.stp/.iges/.igs —
 *   identified from the *original* fileExt hint, even though the actual geometry
 *   served is always a converted GLB) and OFF for mesh formats (.stl/.obj/.3mf/
 *   .glb/.gltf). The user can still toggle it either way from the toolbar.
 */

import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { STLLoader } from 'three/addons/loaders/STLLoader.js';
import { OBJLoader } from 'three/addons/loaders/OBJLoader.js';
import { ThreeMFLoader } from 'three/addons/loaders/3MFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';
import { LineSegments2 } from 'three/addons/lines/LineSegments2.js';
import { LineSegmentsGeometry } from 'three/addons/lines/LineSegmentsGeometry.js';

// ============================================================================
// USER CONFIGURATION SECTION
// ============================================================================

const CONFIG = {
    CAMERA_FOV_DEG: 0.8 * (180 / Math.PI),
    CAMERA_NEAR: 0.1,
    CAMERA_FAR_RADIUS_FACTOR: 500,
    CAMERA_LOWER_RADIUS_FACTOR: 0.00001,
    CAMERA_UPPER_RADIUS_FACTOR: 50,
    CAMERA_TRANSITION_MS: 500,
    ZOOM_SPEED: 1.0,

    AUTO_ROTATION_SPEED_IDLE: 0.001,
    AUTO_ROTATION_SPEED_HOVER: 0.0005,
    AUTO_ROTATION_SPEED_STOP: 0,
    AUTO_ROTATION_LERP_FACTOR: 0.04,
    AUTO_ROTATION_MIN_THRESHOLD: 0.00001,

    MULTI_BODY_PALETTE: [
        0x599cbf, 0xbf5959, 0x59bf59, 0xbfbf59, 0xbf59bf, 0x5959bf,
        0x997a4d, 0x73737f, 0xbf8c4d, 0xbf598c, 0x8c598c, 0x808080,
        0x668066, 0xa6664d, 0x4da680, 0xa6a64d, 0x6666a6, 0xa666a6,
        0x8c9959, 0x80598c, 0x998080,
    ],

    MATERIAL_CAD_SOLID: { color: 0xc7ccd1, metalness: 0.18, roughness: 0.55 },
    MATERIAL_XRAY: { color: 0x949ba3, opacity: 0.38 },
    MATERIAL_BODY_SELECTION_XRAY: { color: 0x949ba3, opacity: 0.15 },
    MATERIAL_MULTI_BODY: { metalness: 0.0, roughness: 0.7, opacity: 1.0 },

    STUDIO_LIGHT: {
        key: { dir: [-0.5, -1.1, -0.8], color: 0xfffaf2, intensity: 2.6 },
        fill: { dir: [0.85, -0.4, -0.2], color: 0xd9ebff, intensity: 1.5 },
        rim: { dir: [0.1, 0.7, -0.4], color: 0xfffcf7, intensity: 0.5 },
        ambient: { color: 0xe0e6f5, intensity: 0.55 },
        background: 0xf5f6f8,
    },
    STUDIO_DARK: {
        key: { dir: [-0.5, -1.1, -0.8], color: 0xffd6a6, intensity: 3.2 },
        rim: { dir: [-0.8, -0.3, 0.55], color: 0x6189ff, intensity: 1.6 },
        back: { dir: [0.1, 0.9, 0.8], color: 0x5270e0, intensity: 0.5 },
        ambient: { color: 0x1a2030, intensity: 0.3 },
        background: 0x0a0a0a,
    },

    EDGES: { color: 0x1f2226, colorDark: 0xcdd3de, widthPx: 1.6 },

    GRID: { divisions: 10 },

    /** Original-upload extensions that get edges ON by default (native BREP/CAD exchange formats). */
    CAD_NATIVE_EXTENSIONS: new Set(['.step', '.stp', '.iges', '.igs']),

    GLB_RETRY_DELAYS_MS: [2000, 5000, 10000, 20000],

    SCALE_TOLERANCE: 0.05,

    AXIS_GIZMO: {
        length: 0.65,
        coneHeight: 0.18,
        coneRadius: 0.045,
        colorX: 0xee4444, colorY: 0x22c750, colorZ: 0x3882f5,
        viewport: { x: 0.84, y: 0.76, width: 0.16, height: 0.24 },
        hoverRegion: { xMin: 0.83, yMax: 0.25 },
    },

    SECTION: { fillColor: 0xffc8e0, fillOpacity: 0.55, planeLiftMm: 0.1 },

    TURNING_AXIS: { color: 0xffa726, dashSize: 4, gapSize: 2 },

    DFM_OVERLAY_COLOR: 0xff5252,
    DFM_OVERLAY_OPACITY: 0.55,

    MEASURE: { color: 0xffd900, endpointRadiusRatio: 0.006 },
    THICKNESS: { safeThresholdMm: 2.0, criticalThresholdMm: 0.5, maxRayDistanceMm: 100 },
};

const PRESETS = {
    front: { dir: [0, -1, 0], up: [0, 0, 1] },
    back: { dir: [0, 1, 0], up: [0, 0, 1] },
    right: { dir: [-1, 0, 0], up: [0, 0, 1] },
    left: { dir: [1, 0, 0], up: [0, 0, 1] },
    top: { dir: [0, 0, 1], up: [0, 1, 0] },
    bottom: { dir: [0, 0, -1], up: [0, 1, 0] },
    iso: { dir: [-0.6408, -0.6408, 0.4226], up: [0, 0, 1] },
};

// LOD / performance constants
const LOD_FPS_SAMPLE_SIZE = 10;
const LOD_FPS_DOWN = 25;   // step down pixel ratio when rolling FPS falls below this
const LOD_FPS_UP = 50;     // restore pixel ratio when rolling FPS rises above this
const LOD_PIXEL_RATIOS = [2.0, 1.5, 0.75]; // per quality level (0=high, 1=mid, 2=low)

// ============================================================================
// PER-CANVAS STATE
// ============================================================================

const renderers = {};
const scenes = {};
const cameras = {};
const controlsMap = {};
const canvasEls = {};
const modelRoots = {};
const fitRadiusMap = {};
const orthoBaseMap = {};
const cameraProjection = {};
const darkModes = {};
const lightsMap = {};
const renderModes = {};
const edgesEnabled = {};
const bboxEnabled = {};
const gridEnabled = {};
const bboxHelpers = {};
const gridHelpers = {};
const edgeLinesByMesh = {};
const cadMeshes = {};
const bodyMaps = {};
const selectedBody = {};
const bodyPickHandlers = {};
const dotNetRefs = {};
const loadGenerations = {};
const animStates = {};
const autoSpeedCurrent = {};
const autoSpeedTarget = {};
const resizeObservers = {};
const panState = {};
const animationFrameHandles = {};

const gizmoScenes = {};
const gizmoCameras = {};
const gizmoLabelDivs = {};
const gizmoMouseHandlers = {};

const sectionStates = {};
const turningAxisObjects = {};
const dfmOverlays = {};
const measureStates = {};
const thicknessStates = {};
const flippedTriangleOriginalMaterials = {};

// LOD: on-demand rendering + adaptive pixel ratio
const renderLod = {};    // canvasId -> { frames, fpsIdx, fpsCnt, lastT, samples: Float32Array(10) }
const lodPixelLevel = {}; // canvasId -> 0=high, 1=mid, 2=low

const SPEED_IDLE = CONFIG.AUTO_ROTATION_SPEED_IDLE;
const SPEED_HOVER = CONFIG.AUTO_ROTATION_SPEED_HOVER;
const SPEED_STOP = CONFIG.AUTO_ROTATION_SPEED_STOP;

function debugLog(...args) {
    if (typeof window !== 'undefined' && window.__quoteViewerDebug) {
        console.debug('[ThreeViewer]', ...args);
    }
}

function warnNotImplemented(name) {
    console.warn(`[ThreeViewer] "${name}" is not implemented yet in the three.js migration.`);
}

// ============================================================================
// ON-DEMAND RENDERING + ADAPTIVE PIXEL RATIO (LOD)
// ============================================================================

/** Mark the viewer as needing at least 2 more rendered frames. */
function markDirty(canvasId) {
    const lod = renderLod[canvasId];
    if (lod && lod.frames < 2) lod.frames = 2;
}

/**
 * Called once per rendered frame. Measures rolling FPS from frame delta times and
 * steps the pixel ratio up/down to maintain ≥30 fps on weak GPUs. Only called during
 * actual render frames so the tracker never samples across idle gaps.
 */
function measureFpsAndAdaptPixelRatio(canvasId, timestamp) {
    const lod = renderLod[canvasId];
    const renderer = renderers[canvasId];
    if (!lod || !renderer) return;
    const dt = timestamp - lod.lastT;
    lod.lastT = timestamp;
    if (dt <= 0 || dt > 200) return;
    lod.samples[lod.fpsIdx] = dt;
    lod.fpsIdx = (lod.fpsIdx + 1) % LOD_FPS_SAMPLE_SIZE;
    if (lod.fpsCnt < LOD_FPS_SAMPLE_SIZE) lod.fpsCnt++;
    if (lod.fpsCnt < 5) return;
    let sum = 0;
    for (let i = 0; i < lod.fpsCnt; i++) sum += lod.samples[i];
    const fps = 1000 / (sum / lod.fpsCnt);
    const level = lodPixelLevel[canvasId] || 0;
    let next = level;
    if (fps < LOD_FPS_DOWN && level < 2) next = level + 1;
    else if (fps > LOD_FPS_UP && level > 0) next = level - 1;
    if (next !== level) {
        lodPixelLevel[canvasId] = next;
        renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, LOD_PIXEL_RATIOS[next]));
        lod.fpsCnt = 0;
    }
}

// ============================================================================
// FORMAT / LOADER RESOLUTION
// ============================================================================

const LOADABLE_EXTENSIONS = ['.glb', '.gltf', '.stl', '.obj', '.3mf'];

function resolveLoadableExtension(fileUrl, fileExt) {
    const lower = (fileExt || '').toLowerCase();
    if (LOADABLE_EXTENSIONS.includes(lower)) return lower;
    const path = (fileUrl || '').split('?')[0].toLowerCase();
    return LOADABLE_EXTENSIONS.find((e) => path.endsWith(e)) ?? '.glb';
}

function shouldApplyGltfUpAxisRotation(loadableExt) {
    return loadableExt === '.glb' || loadableExt === '.gltf';
}

function defaultEdgesForExtension(fileExt) {
    const lower = (fileExt || '').toLowerCase();
    return CONFIG.CAD_NATIVE_EXTENSIONS.has(lower);
}

// ============================================================================
// HELPERS
// ============================================================================

function getThemeBackground(isDark) {
    return isDark ? CONFIG.STUDIO_DARK.background : CONFIG.STUDIO_LIGHT.background;
}

function collectBodyRoots(root) {
    const children = root.children.filter((c) => !c.isCamera && !c.isLight);
    if (children.length === 0) return hasMeshDescendant(root) ? [root] : [];
    if (children.length === 1) return collectBodyRoots(children[0]);
    return children.filter(hasMeshDescendant);
}

function hasMeshDescendant(node) {
    let found = false;
    node.traverse((n) => { if (n.isMesh) found = true; });
    return found;
}

function collectMeshes(node) {
    const meshes = [];
    node.traverse((n) => { if (n.isMesh) meshes.push(n); });
    return meshes;
}

function computeBounds(object3d) {
    object3d.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(object3d);
    if (box.isEmpty()) return null;
    return box;
}

function disposeMaterial(material) {
    if (Array.isArray(material)) { material.forEach(disposeMaterial); return; }
    if (material && typeof material.dispose === 'function') material.dispose();
}

function disposeObject3D(root) {
    root.traverse((n) => {
        if (n.geometry) n.geometry.dispose();
        if (n.material) disposeMaterial(n.material);
    });
}

// ============================================================================
// CAMERA PRESETS (quaternion arc animation)
// ============================================================================

function easeInOutCubic(t) {
    return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
}

function currentZoomRatio(canvasId) {
    const cam = cameras[canvasId];
    const fit = fitRadiusMap[canvasId] || 1;
    if (cameraProjection[canvasId] === 'orthographic') return cam.zoom || 1;
    return (cam.position.distanceTo(controlsMap[canvasId].target) || fit) / fit;
}

function applyOrthoBounds(canvasId) {
    const cam = cameras[canvasId];
    const base = orthoBaseMap[canvasId];
    if (!cam || !base) return;
    cam.left = base.left;
    cam.right = base.right;
    cam.top = base.top;
    cam.bottom = base.bottom;
    cam.updateProjectionMatrix();
}

function animatePresetTransition(canvasId, dir, up, zoomRatio, durationMs) {
    return new Promise((resolve) => {
        const cam = cameras[canvasId];
        const controls = controlsMap[canvasId];
        const target = controls.target.clone();
        const fit = fitRadiusMap[canvasId] || 1;

        const startDir = cam.position.clone().sub(target).normalize();
        const endDir = new THREE.Vector3(dir[0], dir[1], dir[2]).normalize();
        const startUp = cam.up.clone();
        const endUp = new THREE.Vector3(up[0], up[1], up[2]).normalize();

        let dot = THREE.MathUtils.clamp(startDir.dot(endDir), -1, 1);
        let rotQuat;
        if (dot > 0.999999) {
            rotQuat = new THREE.Quaternion();
        } else if (dot < -0.999999) {
            let axis = new THREE.Vector3(0, 0, 1).cross(startDir);
            if (axis.lengthSq() < 1e-6) axis = new THREE.Vector3(0, 1, 0).cross(startDir);
            axis.normalize();
            rotQuat = new THREE.Quaternion().setFromAxisAngle(axis, Math.PI);
        } else {
            const axis = new THREE.Vector3().crossVectors(startDir, endDir).normalize();
            rotQuat = new THREE.Quaternion().setFromAxisAngle(axis, Math.acos(dot));
        }

        const isOrtho = cameraProjection[canvasId] === 'orthographic';
        const startRadius = isOrtho ? fit : cam.position.distanceTo(target);
        const endRadius = isOrtho ? fit : fit * zoomRatio;
        const startZoom = isOrtho ? (cam.zoom || 1) : 1;
        const endZoom = isOrtho ? zoomRatio : 1;

        const startTime = performance.now();
        cam._presetCancelled = false;

        const step = () => {
            if (cam._presetCancelled) { resolve(); return; }
            const elapsed = performance.now() - startTime;
            const progress = Math.min(elapsed / durationMs, 1);
            const eased = easeInOutCubic(progress);

            const slerped = new THREE.Quaternion().identity().slerp(rotQuat, eased);
            const dirNow = startDir.clone().applyQuaternion(slerped);
            const radiusNow = startRadius + (endRadius - startRadius) * eased;
            cam.position.copy(target).addScaledVector(dirNow, radiusNow);
            cam.up.copy(startUp).lerp(endUp, eased).normalize();

            if (isOrtho) {
                cam.zoom = startZoom + (endZoom - startZoom) * eased;
                applyOrthoBounds(canvasId);
            }

            cam.lookAt(target);
            controls.update();
            markDirty(canvasId);

            if (progress < 1) {
                requestAnimationFrame(step);
            } else {
                cam.up.copy(endUp);
                if (isOrtho) { cam.zoom = endZoom; applyOrthoBounds(canvasId); }
                cam.lookAt(target);
                controls.update();
                markDirty(canvasId);
                resolve();
            }
        };
        step();
    });
}

function applyPresetImmediate(canvasId, dir, up, zoomRatio) {
    const cam = cameras[canvasId];
    const controls = controlsMap[canvasId];
    const target = controls.target;
    const fit = fitRadiusMap[canvasId] || 1;
    const isOrtho = cameraProjection[canvasId] === 'orthographic';
    const radius = isOrtho ? fit : fit * zoomRatio;

    cam.position.copy(target).addScaledVector(new THREE.Vector3(dir[0], dir[1], dir[2]).normalize(), radius);
    cam.up.set(up[0], up[1], up[2]);
    if (isOrtho) { cam.zoom = zoomRatio; applyOrthoBounds(canvasId); }
    cam.lookAt(target);
    controls.update();
    markDirty(canvasId);
}

export function setCameraPreset(canvasId, presetName, smooth = true) {
    const preset = PRESETS[presetName];
    if (!preset || !cameras[canvasId]) return;
    const zoomRatio = Math.max(currentZoomRatio(canvasId), 1);
    if (smooth) {
        animatePresetTransition(canvasId, preset.dir, preset.up, zoomRatio, CONFIG.CAMERA_TRANSITION_MS);
    } else {
        applyPresetImmediate(canvasId, preset.dir, preset.up, zoomRatio);
    }
}

export function resetCamera(canvasId, smooth = true) {
    if (!cameras[canvasId]) return;
    if (smooth) {
        animatePresetTransition(canvasId, PRESETS.iso.dir, PRESETS.iso.up, 1, CONFIG.CAMERA_TRANSITION_MS);
    } else {
        applyPresetImmediate(canvasId, PRESETS.iso.dir, PRESETS.iso.up, 1);
    }
}

export function rotateModel(canvasId, degrees, smooth = true) {
    const root = modelRoots[canvasId];
    if (!root) return;
    root.rotateZ((degrees * Math.PI) / 180);
    markDirty(canvasId);
}

export function zoomTo(canvasId, zoomFactor, smooth = true) {
    const cam = cameras[canvasId];
    const controls = controlsMap[canvasId];
    if (!cam || !controls) return;
    const fit = fitRadiusMap[canvasId] || 1;
    const isOrtho = cameraProjection[canvasId] === 'orthographic';
    const dir = cam.position.clone().sub(controls.target).normalize();
    const targetRadius = isOrtho ? fit : fit * Math.max(zoomFactor, 0.001);

    if (smooth) {
        animatePresetTransition(canvasId, [dir.x, dir.y, dir.z], [cam.up.x, cam.up.y, cam.up.z], Math.max(zoomFactor, 0.001), CONFIG.CAMERA_TRANSITION_MS);
        return;
    }
    if (isOrtho) { cam.zoom = Math.max(zoomFactor, 0.001); applyOrthoBounds(canvasId); }
    else cam.position.copy(controls.target).addScaledVector(dir, targetRadius);
    cam.lookAt(controls.target);
    controls.update();
    markDirty(canvasId);
}

export function zoomToFit(canvasId, smooth = true) {
    zoomTo(canvasId, 1, smooth);
}

export function stopAutoRotation(canvasId) {
    animStates[canvasId] = 'interacting';
    autoSpeedTarget[canvasId] = SPEED_STOP;
}

export function setCameraProjection(canvasId, mode) {
    const cam = cameras[canvasId];
    const controls = controlsMap[canvasId];
    const renderer = renderers[canvasId];
    if (!cam || !controls || !renderer) return;
    const wantOrtho = mode === 'orthographic';
    if ((cameraProjection[canvasId] === 'orthographic') === wantOrtho) return;

    const fit = fitRadiusMap[canvasId] || 1;
    const target = controls.target.clone();
    const dir = cam.position.clone().sub(target).normalize();
    const zoomRatio = Math.max(currentZoomRatio(canvasId), 1);
    const up = cam.up.clone();

    const canvas = canvasEls[canvasId];
    const aspect = canvas.clientWidth / Math.max(canvas.clientHeight, 1);
    const newCam = wantOrtho
        ? new THREE.OrthographicCamera(-fit * aspect, fit * aspect, fit, -fit, CONFIG.CAMERA_NEAR, fit * CONFIG.CAMERA_FAR_RADIUS_FACTOR)
        : new THREE.PerspectiveCamera(CONFIG.CAMERA_FOV_DEG, aspect, CONFIG.CAMERA_NEAR, fit * CONFIG.CAMERA_FAR_RADIUS_FACTOR);

    if (wantOrtho) {
        orthoBaseMap[canvasId] = { left: -fit * aspect, right: fit * aspect, top: fit, bottom: -fit };
        newCam.zoom = zoomRatio;
    }

    newCam.position.copy(target).addScaledVector(dir, wantOrtho ? fit : fit * zoomRatio);
    newCam.up.copy(up);
    newCam.lookAt(target);
    newCam.updateProjectionMatrix();

    cameras[canvasId] = newCam;
    cameraProjection[canvasId] = wantOrtho ? 'orthographic' : 'perspective';

    controls.dispose();
    const newControls = createOrbitControls(canvasId, newCam, canvas, fit);
    newControls.target.copy(target);
    newControls.update();

    rebuildEdgeMaterialsResolution(canvasId);
    markDirty(canvasId);
}

// ============================================================================
// ORBIT CONTROLS + PICK-POINT PAN
// ============================================================================

function createOrbitControls(canvasId, camera, canvas, fitRadius) {
    const controls = new OrbitControls(camera, canvas);
    controls.enablePan = false;
    controls.mouseButtons = { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: null };
    controls.touches = { ONE: THREE.TOUCH.ROTATE, TWO: THREE.TOUCH.DOLLY_PAN };
    controls.minDistance = fitRadius * CONFIG.CAMERA_LOWER_RADIUS_FACTOR;
    controls.maxDistance = fitRadius * CONFIG.CAMERA_UPPER_RADIUS_FACTOR;
    controls.minZoom = 1 / CONFIG.CAMERA_UPPER_RADIUS_FACTOR;
    controls.maxZoom = 1 / CONFIG.CAMERA_LOWER_RADIUS_FACTOR;
    controls.zoomSpeed = CONFIG.ZOOM_SPEED;
    controls.rotateSpeed = 1.0;
    controls.minPolarAngle = 0;
    controls.maxPolarAngle = Math.PI;
    controls.enableDamping = false;
    controls.addEventListener('change', () => markDirty(canvasId));
    controls.addEventListener('start', () => {
        stopAutoRotation(canvasId);
        if (edgesEnabled[canvasId] && renderModes[canvasId] !== 'wireframe') {
            setEdgeLinesVisible(canvasId, false);
        }
    });
    controls.addEventListener('end', () => {
        if (edgesEnabled[canvasId] && renderModes[canvasId] !== 'wireframe') {
            setEdgeLinesVisible(canvasId, true);
        }
        markDirty(canvasId);
    });
    controlsMap[canvasId] = controls;
    attachPickPointPan(canvasId, canvas, camera, controls);
    return controls;
}

function attachPickPointPan(canvasId, canvas, camera, controls) {
    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    const plane = new THREE.Plane();
    const anchor = new THREE.Vector3();
    const hit = new THREE.Vector3();

    function rayFromEvent(evt) {
        const rect = canvas.getBoundingClientRect();
        pointer.x = ((evt.clientX - rect.left) / rect.width) * 2 - 1;
        pointer.y = -((evt.clientY - rect.top) / rect.height) * 2 + 1;
        raycaster.setFromCamera(pointer, camera);
        return raycaster.ray;
    }

    function onDown(evt) {
        if (evt.button !== 2) return;
        evt.preventDefault();
        const dir = new THREE.Vector3();
        camera.getWorldDirection(dir);
        plane.setFromNormalAndCoplanarPoint(dir, controls.target);
        if (rayFromEvent(evt).intersectPlane(plane, anchor)) {
            panState[canvasId] = { active: true };
        }
    }

    function onMove(evt) {
        const state = panState[canvasId];
        if (!state || !state.active) return;
        if (rayFromEvent(evt).intersectPlane(plane, hit)) {
            const delta = anchor.clone().sub(hit);
            camera.position.add(delta);
            controls.target.add(delta);
            controls.update();
        }
    }

    function onUp() {
        if (panState[canvasId]) panState[canvasId].active = false;
    }

    canvas.addEventListener('contextmenu', (e) => e.preventDefault());
    canvas.addEventListener('pointerdown', onDown);
    canvas.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    panHandlersRegistry[canvasId] = { onDown, onMove, onUp, canvas };
}

const panHandlersRegistry = {};

function detachPickPointPan(canvasId) {
    const h = panHandlersRegistry[canvasId];
    if (!h) return;
    h.canvas.removeEventListener('pointerdown', h.onDown);
    h.canvas.removeEventListener('pointermove', h.onMove);
    window.removeEventListener('pointerup', h.onUp);
    delete panHandlersRegistry[canvasId];
}

// ============================================================================
// LIGHTING
// ============================================================================

function buildLights(scene, isDark) {
    const cfg = isDark ? CONFIG.STUDIO_DARK : CONFIG.STUDIO_LIGHT;
    const ambient = new THREE.AmbientLight(cfg.ambient.color, cfg.ambient.intensity);
    const key = new THREE.DirectionalLight(cfg.key.color, cfg.key.intensity);
    key.position.set(...cfg.key.dir).negate();
    const result = { ambient, key };
    scene.add(ambient, key);

    if (cfg.fill) {
        const fill = new THREE.DirectionalLight(cfg.fill.color, cfg.fill.intensity);
        fill.position.set(...cfg.fill.dir).negate();
        scene.add(fill);
        result.fill = fill;
    }
    if (cfg.rim) {
        const rim = new THREE.DirectionalLight(cfg.rim.color, cfg.rim.intensity);
        rim.position.set(...cfg.rim.dir).negate();
        scene.add(rim);
        result.rim = rim;
    }
    if (cfg.back) {
        const back = new THREE.DirectionalLight(cfg.back.color, cfg.back.intensity);
        back.position.set(...cfg.back.dir).negate();
        scene.add(back);
        result.back = back;
    }
    return result;
}

function applyStudioLighting(canvasId, isDark) {
    const scene = scenes[canvasId];
    const lights = lightsMap[canvasId];
    if (!scene || !lights) return;
    darkModes[canvasId] = !!isDark;
    Object.values(lights).forEach((l) => scene.remove(l));
    lightsMap[canvasId] = buildLights(scene, isDark);
    scene.background = new THREE.Color(getThemeBackground(isDark));
    rebuildEdgeColors(canvasId);
}

// ============================================================================
// MATERIALS / RENDER MODES
// ============================================================================

function makeCadSolidMaterial() {
    const m = CONFIG.MATERIAL_CAD_SOLID;
    return new THREE.MeshStandardMaterial({ color: m.color, metalness: m.metalness, roughness: m.roughness });
}

function applyRenderMode(canvasId, mode) {
    const meshes = cadMeshes[canvasId] || [];
    const bodyMap = bodyMaps[canvasId];
    const isMultiBody = bodyMap && bodyMap.size > 1;

    meshes.forEach((mesh) => {
        mesh.material.wireframe = false;
        mesh.material.transparent = false;
        mesh.material.opacity = 1;
        mesh.material.depthWrite = true;

        if (mode === 'wireframe') {
            mesh.material.wireframe = true;
        } else if (mode === 'transparent') {
            mesh.material.transparent = true;
            mesh.material.opacity = isMultiBody ? CONFIG.MATERIAL_BODY_SELECTION_XRAY.opacity : CONFIG.MATERIAL_XRAY.opacity;
            mesh.material.depthWrite = false;
        }
        mesh.material.needsUpdate = true;
    });

    setEdgeLinesVisible(canvasId, edgesEnabled[canvasId] && mode !== 'wireframe');
}

export function setRenderMode(canvasId, mode) {
    const normalized = mode === 'wireframe' || mode === 'transparent' ? mode : 'solid';
    renderModes[canvasId] = normalized;
    applyRenderMode(canvasId, normalized);
    markDirty(canvasId);
}

// ============================================================================
// EDGE RENDERING
// ============================================================================

function buildEdgeLine(mesh, canvasId) {
    const edgesGeom = new THREE.EdgesGeometry(mesh.geometry, 30);
    const lineGeom = new LineSegmentsGeometry().fromEdgesGeometry(edgesGeom);
    edgesGeom.dispose();
    const isDark = darkModes[canvasId];
    const material = new LineMaterial({
        color: isDark ? CONFIG.EDGES.colorDark : CONFIG.EDGES.color,
        linewidth: CONFIG.EDGES.widthPx,
        worldUnits: false,
    });
    const renderer = renderers[canvasId];
    if (renderer) material.resolution.set(renderer.domElement.clientWidth, renderer.domElement.clientHeight);
    const line = new LineSegments2(lineGeom, material);
    line.computeLineDistances();
    line.renderOrder = 1;
    return line;
}

function rebuildEdgeLines(canvasId) {
    const meshes = cadMeshes[canvasId] || [];
    const map = new Map();
    meshes.forEach((mesh) => {
        const line = buildEdgeLine(mesh, canvasId);
        mesh.add(line);
        map.set(mesh.uuid, line);
    });
    edgeLinesByMesh[canvasId] = map;
    setEdgeLinesVisible(canvasId, edgesEnabled[canvasId] && renderModes[canvasId] !== 'wireframe');
}

function setEdgeLinesVisible(canvasId, visible) {
    const map = edgeLinesByMesh[canvasId];
    if (!map) return;
    map.forEach((line) => { line.visible = !!visible; });
}

function rebuildEdgeColors(canvasId) {
    const map = edgeLinesByMesh[canvasId];
    if (!map) return;
    const isDark = darkModes[canvasId];
    map.forEach((line) => line.material.color.set(isDark ? CONFIG.EDGES.colorDark : CONFIG.EDGES.color));
}

function rebuildEdgeMaterialsResolution(canvasId) {
    const map = edgeLinesByMesh[canvasId];
    const renderer = renderers[canvasId];
    if (!map || !renderer) return;
    map.forEach((line) => line.material.resolution.set(renderer.domElement.clientWidth, renderer.domElement.clientHeight));
}

export function toggleEdges(canvasId, enabled) {
    edgesEnabled[canvasId] = !!enabled;
    setEdgeLinesVisible(canvasId, !!enabled && renderModes[canvasId] !== 'wireframe');
    markDirty(canvasId);
}

// ============================================================================
// BOUNDING BOX / GRID
// ============================================================================

export function toggleBoundingBox(canvasId, enabled) {
    bboxEnabled[canvasId] = !!enabled;
    const scene = scenes[canvasId];
    const root = modelRoots[canvasId];
    if (!scene || !root) return;

    if (bboxHelpers[canvasId]) {
        scene.remove(bboxHelpers[canvasId]);
        bboxHelpers[canvasId].geometry.dispose();
        bboxHelpers[canvasId].material.dispose();
        delete bboxHelpers[canvasId];
    }
    if (enabled) {
        const box = computeBounds(root);
        if (box) {
            const helper = new THREE.Box3Helper(box, new THREE.Color(darkModes[canvasId] ? 0xcdd3de : 0x1f2226));
            scene.add(helper);
            bboxHelpers[canvasId] = helper;
        }
    }
    markDirty(canvasId);
}

export function showGrid(canvasId) {
    gridEnabled[canvasId] = true;
    const scene = scenes[canvasId];
    if (!scene) return;
    if (!gridHelpers[canvasId]) {
        const fit = fitRadiusMap[canvasId] || 10;
        const size = fit * 4;
        const helper = new THREE.GridHelper(size, CONFIG.GRID.divisions, 0x888d96, darkModes[canvasId] ? 0x33373f : 0xc9ccd2);
        helper.rotation.x = Math.PI / 2;
        gridHelpers[canvasId] = helper;
    }
    scene.add(gridHelpers[canvasId]);
    markDirty(canvasId);
}

export function hideGrid(canvasId) {
    gridEnabled[canvasId] = false;
    const scene = scenes[canvasId];
    if (scene && gridHelpers[canvasId]) scene.remove(gridHelpers[canvasId]);
    markDirty(canvasId);
}

// ============================================================================
// MULTI-BODY
// ============================================================================

function detectAndColorBodies(canvasId, root, scene) {
    const bodyRoots = collectBodyRoots(root);
    const map = new Map();
    bodyRoots.forEach((bodyRoot, index) => {
        const meshes = collectMeshes(bodyRoot);
        if (meshes.length === 0) return;
        map.set(index, { root: bodyRoot, meshes, color: null, name: bodyRoot.name || `Body_${index}` });
    });
    bodyMaps[canvasId] = map;

    if (map.size > 1) {
        const palette = CONFIG.MULTI_BODY_PALETTE;
        let i = 0;
        map.forEach((body) => {
            const color = palette[i % palette.length];
            i += 1;
            const material = new THREE.MeshStandardMaterial({
                color, metalness: CONFIG.MATERIAL_MULTI_BODY.metalness, roughness: CONFIG.MATERIAL_MULTI_BODY.roughness,
            });
            body.meshes.forEach((mesh) => { mesh.material = material; });
            body.color = color;
        });
    }
    return map;
}

export function setBodies(canvasId, _bodies) {
    // Coloring already applied at load time from the detected body map.
}

export function selectBody(canvasId, bodyIndex) {
    const map = bodyMaps[canvasId];
    if (!map) return;
    selectedBody[canvasId] = bodyIndex;
    map.forEach((body, idx) => {
        body.meshes.forEach((mesh) => {
            mesh.material.transparent = idx !== bodyIndex;
            mesh.material.opacity = idx !== bodyIndex ? CONFIG.MATERIAL_BODY_SELECTION_XRAY.opacity : 1;
            mesh.material.depthWrite = idx === bodyIndex;
            mesh.material.needsUpdate = true;
        });
    });
    markDirty(canvasId);
}

export function clearBodySelection(canvasId) {
    const map = bodyMaps[canvasId];
    if (!map) return;
    selectedBody[canvasId] = null;
    map.forEach((body) => {
        body.meshes.forEach((mesh) => {
            mesh.material.transparent = false;
            mesh.material.opacity = 1;
            mesh.material.depthWrite = true;
            mesh.material.needsUpdate = true;
        });
    });
    markDirty(canvasId);
}

export function enableBodyPicking(canvasId, dotNetRef) {
    const canvas = canvasEls[canvasId];
    const camera = cameras[canvasId];
    const scene = scenes[canvasId];
    if (!canvas || !camera || !scene) return;

    if (bodyPickHandlers[canvasId]) canvas.removeEventListener('click', bodyPickHandlers[canvasId]);

    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    const handler = (evt) => {
        const rect = canvas.getBoundingClientRect();
        pointer.x = ((evt.clientX - rect.left) / rect.width) * 2 - 1;
        pointer.y = -((evt.clientY - rect.top) / rect.height) * 2 + 1;
        raycaster.setFromCamera(pointer, camera);
        const hits = raycaster.intersectObjects(cadMeshes[canvasId] || [], false);
        const map = bodyMaps[canvasId];
        if (hits.length > 0 && map) {
            const hitMesh = hits[0].object;
            let foundIndex = null;
            map.forEach((body, idx) => { if (body.meshes.includes(hitMesh)) foundIndex = idx; });
            dotNetRef?.invokeMethodAsync('NotifyBodyPicked', foundIndex);
        } else {
            dotNetRef?.invokeMethodAsync('NotifyBodyPicked', null);
        }
    };
    canvas.addEventListener('click', handler);
    bodyPickHandlers[canvasId] = handler;
}

// ============================================================================
// LOADERS
// ============================================================================

async function loadModel(loadableExt, url) {
    if (loadableExt === '.stl') {
        const geometry = await new STLLoader().loadAsync(url);
        if (!geometry.attributes.normal) geometry.computeVertexNormals();
        const group = new THREE.Group();
        group.add(new THREE.Mesh(geometry, makeCadSolidMaterial()));
        return group;
    }
    if (loadableExt === '.obj') {
        return await new OBJLoader().loadAsync(url);
    }
    if (loadableExt === '.3mf') {
        return await new ThreeMFLoader().loadAsync(url);
    }
    const gltf = await new GLTFLoader().loadAsync(url);
    return gltf.scene;
}

async function fetchWithRetry(url, generationKey, canvasId) {
    const delays = CONFIG.GLB_RETRY_DELAYS_MS;
    for (let attempt = 0; ; attempt += 1) {
        if (loadGenerations[canvasId] !== generationKey) throw new Error('stale-load');
        const response = await fetch(url);
        if (response.ok) return response;
        if (response.status === 404 && attempt < delays.length) {
            await new Promise((r) => setTimeout(r, delays[attempt]));
            continue;
        }
        throw new Error(`Failed to fetch model (status ${response.status})`);
    }
}

// ============================================================================
// INITIALIZE
// ============================================================================

function normalizeViewerSettings(settings) {
    const s = settings || {};
    let renderMode = s.renderMode;
    if (renderMode !== 'wireframe' && renderMode !== 'transparent') renderMode = 'solid'; // 'realistic' (removed) falls back to solid
    return {
        renderMode,
        cameraPreset: typeof s.cameraPreset === 'string' && PRESETS[s.cameraPreset] ? s.cameraPreset : 'iso',
        cameraProjection: s.cameraProjection === 'orthographic' ? 'orthographic' : 'perspective',
        edgesEnabled: s.edgesEnabled,
        edgesEnabledExplicit: s.edgesEnabled !== undefined && s.edgesEnabled !== null,
        gridEnabled: !!s.gridEnabled,
        boundingBoxEnabled: !!s.boundingBoxEnabled,
    };
}

export async function initialize(canvasId, fileUrl, fileExt, isDark, knownDimsMm, dotNetRef, viewerSettings = {}) {
    const settings = normalizeViewerSettings(viewerSettings);
    debugLog('initialize', { canvasId, fileExt, settings });

    if (renderers[canvasId]) await dispose(canvasId);

    loadGenerations[canvasId] = (loadGenerations[canvasId] || 0) + 1;
    const generation = loadGenerations[canvasId];

    const canvas = document.getElementById(canvasId);
    if (!canvas) { console.error(`[ThreeViewer] Canvas #${canvasId} not found.`); return; }
    canvasEls[canvasId] = canvas;
    canvas.style.background = 'transparent';
    canvas.style.opacity = '0';
    canvas.style.transition = 'opacity 0.3s ease-in';

    if (dotNetRef) dotNetRefs[canvasId] = dotNetRef;
    darkModes[canvasId] = !!isDark;

    const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true, preserveDrawingBuffer: false });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderers[canvasId] = renderer;

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(getThemeBackground(isDark));
    scenes[canvasId] = scene;
    lightsMap[canvasId] = buildLights(scene, isDark);

    const aspect = canvas.clientWidth / Math.max(canvas.clientHeight, 1);
    const camera = new THREE.PerspectiveCamera(CONFIG.CAMERA_FOV_DEG, aspect || 1, CONFIG.CAMERA_NEAR, 1000);
    camera.up.set(0, 0, 1);
    cameras[canvasId] = camera;
    cameraProjection[canvasId] = 'perspective';

    createOrbitControls(canvasId, camera, canvas, 10);
    // Set the drawing-buffer size synchronously now — the canvas starts at the browser's
    // default 300x150 buffer until ResizeObserver's first (async) callback fires, which
    // would otherwise render the first frame(s) at the wrong resolution.
    doResize(canvasId);

    renderModes[canvasId] = settings.renderMode;
    edgesEnabled[canvasId] = settings.edgesEnabledExplicit ? !!settings.edgesEnabled : defaultEdgesForExtension(fileExt);
    bboxEnabled[canvasId] = false;
    gridEnabled[canvasId] = false;

    try {
        const loadableExt = resolveLoadableExtension(fileUrl, fileExt);
        const response = await fetchWithRetry(fileUrl, generation, canvasId);
        const blobUrl = URL.createObjectURL(await response.blob());
        let loaded;
        try {
            loaded = await loadModel(loadableExt, blobUrl);
        } finally {
            URL.revokeObjectURL(blobUrl);
        }
        if (loadGenerations[canvasId] !== generation) return;

        const root = new THREE.Group();
        root.add(loaded);
        scene.add(root);
        modelRoots[canvasId] = root;

        let rawBox = computeBounds(loaded);
        if (rawBox && knownDimsMm && knownDimsMm.x > 0 && knownDimsMm.y > 0 && knownDimsMm.z > 0) {
            const size = rawBox.getSize(new THREE.Vector3());
            const rawMax = Math.max(size.x, size.y, size.z);
            const knownMax = Math.max(knownDimsMm.x, knownDimsMm.y, knownDimsMm.z);
            if (rawMax > 1e-9) {
                const ratio = rawMax / knownMax;
                if (ratio < 1 - CONFIG.SCALE_TOLERANCE || ratio > 1 + CONFIG.SCALE_TOLERANCE) {
                    loaded.scale.setScalar(knownMax / rawMax);
                }
            }
        }

        if (shouldApplyGltfUpAxisRotation(loadableExt)) {
            loaded.quaternion.premultiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), Math.PI / 2));
        }
        root.updateMatrixWorld(true);

        const upBox = computeBounds(loaded);
        if (upBox) {
            const center = upBox.getCenter(new THREE.Vector3());
            loaded.position.x -= center.x;
            loaded.position.y -= center.y;
            loaded.position.z -= upBox.min.z;
        }
        root.updateMatrixWorld(true);

        const finalBox = computeBounds(root);
        if (!finalBox) { console.warn('[ThreeViewer] Loaded model has no visible geometry.'); return; }

        const bodyMap = detectAndColorBodies(canvasId, loaded, scene);
        const meshes = collectMeshes(loaded);
        cadMeshes[canvasId] = meshes;
        if (!(bodyMap.size > 1)) {
            const cadMat = makeCadSolidMaterial();
            meshes.forEach((mesh) => { mesh.material = cadMat; });
        }
        meshes.forEach((mesh) => { mesh.castShadow = false; mesh.receiveShadow = false; });

        rebuildEdgeLines(canvasId);

        const size = finalBox.getSize(new THREE.Vector3());
        const halfDiag = size.length() / 2;
        const fovRad = (camera.fov * Math.PI) / 180;
        const fitRadius = Math.max((halfDiag / Math.tan(fovRad / 2)) * 1.2, 1);
        fitRadiusMap[canvasId] = fitRadius;
        camera.near = CONFIG.CAMERA_NEAR;
        camera.far = fitRadius * CONFIG.CAMERA_FAR_RADIUS_FACTOR;
        camera.updateProjectionMatrix();
        const controls = controlsMap[canvasId];
        controls.target.copy(finalBox.getCenter(new THREE.Vector3()));
        controls.minDistance = fitRadius * CONFIG.CAMERA_LOWER_RADIUS_FACTOR;
        controls.maxDistance = fitRadius * CONFIG.CAMERA_UPPER_RADIUS_FACTOR;

        const initialPreset = PRESETS[settings.cameraPreset] || PRESETS.iso;
        applyPresetImmediate(canvasId, initialPreset.dir, initialPreset.up, 1);

        if (settings.cameraProjection === 'orthographic') setCameraProjection(canvasId, 'orthographic');

        createAxisGizmo(canvasId, canvas, cameras[canvasId]);

        setRenderMode(canvasId, settings.renderMode);
        toggleEdges(canvasId, edgesEnabled[canvasId]);
        toggleBoundingBox(canvasId, settings.boundingBoxEnabled);
        settings.gridEnabled ? showGrid(canvasId) : hideGrid(canvasId);

        animStates[canvasId] = 'idle';
        autoSpeedCurrent[canvasId] = 0;
        autoSpeedTarget[canvasId] = SPEED_IDLE;
        canvas.addEventListener('mouseenter', () => {
            if (animStates[canvasId] !== 'interacting') { animStates[canvasId] = 'hovering'; autoSpeedTarget[canvasId] = SPEED_HOVER; }
        });
        canvas.addEventListener('mouseleave', () => {
            if (animStates[canvasId] !== 'interacting') { animStates[canvasId] = 'idle'; autoSpeedTarget[canvasId] = SPEED_IDLE; }
        });

        requestAnimationFrame(() => { canvas.style.opacity = '1'; });
    } catch (err) {
        console.error('[ThreeViewer] Load failed:', err);
        canvas.style.opacity = '1';
        if (dotNetRef) dotNetRef.invokeMethodAsync('OnLoadError', err?.message || 'Failed to load 3D model');
    }

    markDirty(canvasId);
    startRenderLoop(canvasId, generation);
    attachResizeObserver(canvasId);
}

function startRenderLoop(canvasId, generation) {
    const renderer = renderers[canvasId];
    const scene = scenes[canvasId];
    const controls = controlsMap[canvasId];

    if (!renderLod[canvasId]) {
        renderLod[canvasId] = { frames: 2, fpsIdx: 0, fpsCnt: 0, lastT: 0, samples: new Float32Array(LOD_FPS_SAMPLE_SIZE) };
    }

    function frame(timestamp) {
        if (loadGenerations[canvasId] !== generation || renderers[canvasId] !== renderer) return;

        const curr = autoSpeedCurrent[canvasId] ?? 0;
        const tgt = autoSpeedTarget[canvasId] ?? 0;
        autoSpeedCurrent[canvasId] = curr + (tgt - curr) * CONFIG.AUTO_ROTATION_LERP_FACTOR;
        if (Math.abs(autoSpeedCurrent[canvasId]) > CONFIG.AUTO_ROTATION_MIN_THRESHOLD) {
            const cam = cameras[canvasId];
            const ctl = controlsMap[canvasId];
            if (cam && ctl) {
                const offset = cam.position.clone().sub(ctl.target);
                offset.applyAxisAngle(cam.up, autoSpeedCurrent[canvasId]);
                cam.position.copy(ctl.target).add(offset);
                cam.lookAt(ctl.target);
            }
            const lod = renderLod[canvasId];
            if (lod) lod.frames = 2;
        }

        controls.update();

        const lod = renderLod[canvasId];
        if (lod && lod.frames > 0) {
            lod.frames--;
            measureFpsAndAdaptPixelRatio(canvasId, timestamp);
            renderer.render(scene, cameras[canvasId]);
            renderAxisGizmo(canvasId);
            updateTurningAxisLabel(canvasId);
        }

        animationFrameHandles[canvasId] = requestAnimationFrame(frame);
    }
    animationFrameHandles[canvasId] = requestAnimationFrame(frame);
}

function attachResizeObserver(canvasId) {
    const canvas = canvasEls[canvasId];
    if (resizeObservers[canvasId]) resizeObservers[canvasId].disconnect();
    const ro = new ResizeObserver(() => doResize(canvasId));
    ro.observe(canvas);
    resizeObservers[canvasId] = ro;
}

function doResize(canvasId) {
    const canvas = canvasEls[canvasId];
    const renderer = renderers[canvasId];
    const camera = cameras[canvasId];
    if (!canvas || !renderer || !camera) return;
    const width = canvas.clientWidth || 1;
    const height = canvas.clientHeight || 1;
    renderer.setSize(width, height, false);
    if (camera.isPerspectiveCamera) {
        camera.aspect = width / height;
    } else {
        const base = orthoBaseMap[canvasId];
        if (base) {
            const halfHeight = base.top;
            camera.left = -halfHeight * (width / height);
            camera.right = halfHeight * (width / height);
            orthoBaseMap[canvasId] = { ...base, left: camera.left, right: camera.right };
        }
    }
    camera.updateProjectionMatrix();
    rebuildEdgeMaterialsResolution(canvasId);
    markDirty(canvasId);
}

export function notifyViewerVisible(canvasId) {
    doResize(canvasId);
}

// ============================================================================
// DISPOSE
// ============================================================================

export function dispose(canvasId) {
    if (animationFrameHandles[canvasId]) cancelAnimationFrame(animationFrameHandles[canvasId]);
    if (resizeObservers[canvasId]) { resizeObservers[canvasId].disconnect(); delete resizeObservers[canvasId]; }
    detachPickPointPan(canvasId);
    const canvas = canvasEls[canvasId];
    if (canvas && bodyPickHandlers[canvasId]) canvas.removeEventListener('click', bodyPickHandlers[canvasId]);

    disposeAxisGizmo(canvasId);
    clearTurningAxis(canvasId);
    clearDfmOverlays(canvasId, null);
    disableMeasureTool(canvasId);
    disableThicknessAnalysis(canvasId);
    if (sectionStates[canvasId]?.fillMesh) disposeObject3D(sectionStates[canvasId].fillMesh);

    if (controlsMap[canvasId]) controlsMap[canvasId].dispose();
    if (modelRoots[canvasId]) disposeObject3D(modelRoots[canvasId]);
    if (bboxHelpers[canvasId]) { bboxHelpers[canvasId].geometry.dispose(); bboxHelpers[canvasId].material.dispose(); }
    if (gridHelpers[canvasId]) { gridHelpers[canvasId].geometry.dispose(); gridHelpers[canvasId].material.dispose(); }
    if (renderers[canvasId]) renderers[canvasId].dispose();

    for (const map of [renderers, scenes, cameras, controlsMap, canvasEls, modelRoots, fitRadiusMap, orthoBaseMap,
        cameraProjection, lightsMap, renderModes, edgesEnabled, bboxEnabled, gridEnabled, bboxHelpers, gridHelpers,
        edgeLinesByMesh, cadMeshes, bodyMaps, selectedBody, bodyPickHandlers, animStates, autoSpeedCurrent,
        autoSpeedTarget, panState, panHandlersRegistry, animationFrameHandles, dotNetRefs, sectionStates,
        dfmOverlays, flippedTriangleOriginalMaterials, renderLod, lodPixelLevel]) {
        delete map[canvasId];
    }
}

// ============================================================================
// AXIS GIZMO (orientation cube, top-right corner)
// ============================================================================

function buildGizmoAxis(gizmoScene, dir, color) {
    const length = CONFIG.AXIS_GIZMO.length;
    const points = [new THREE.Vector3(0, 0, 0), new THREE.Vector3(dir[0] * length, dir[1] * length, dir[2] * length)];
    const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), new THREE.LineBasicMaterial({ color }));
    gizmoScene.add(line);

    const cone = new THREE.Mesh(
        new THREE.ConeGeometry(CONFIG.AXIS_GIZMO.coneRadius, CONFIG.AXIS_GIZMO.coneHeight, 12),
        new THREE.MeshBasicMaterial({ color }),
    );
    const dirVec = new THREE.Vector3(dir[0], dir[1], dir[2]);
    cone.position.copy(dirVec).multiplyScalar(length + CONFIG.AXIS_GIZMO.coneHeight / 2);
    cone.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dirVec);
    gizmoScene.add(cone);
}

function createAxisGizmo(canvasId, canvas, mainCamera) {
    disposeAxisGizmo(canvasId);

    const gizmoScene = new THREE.Scene();
    gizmoScene.add(new THREE.AmbientLight(0xffffff, 1.2));
    buildGizmoAxis(gizmoScene, [1, 0, 0], CONFIG.AXIS_GIZMO.colorX);
    buildGizmoAxis(gizmoScene, [0, 1, 0], CONFIG.AXIS_GIZMO.colorY);
    buildGizmoAxis(gizmoScene, [0, 0, 1], CONFIG.AXIS_GIZMO.colorZ);

    const hw = 1.1;
    const gizmoCamera = new THREE.OrthographicCamera(-hw, hw, hw, -hw, 0.01, 100);
    gizmoCamera.up.set(0, 0, 1);
    gizmoScenes[canvasId] = gizmoScene;
    gizmoCameras[canvasId] = gizmoCamera;

    const labelDefs = [
        { name: 'X', color: '#ee4444', pos: new THREE.Vector3(CONFIG.AXIS_GIZMO.length + 0.28, 0, 0) },
        { name: 'Y', color: '#22c750', pos: new THREE.Vector3(0, CONFIG.AXIS_GIZMO.length + 0.28, 0) },
        { name: 'Z', color: '#3882f5', pos: new THREE.Vector3(0, 0, CONFIG.AXIS_GIZMO.length + 0.28) },
    ];
    const labelDivs = labelDefs.map(({ name, color, pos }) => {
        const div = document.createElement('div');
        div.textContent = name;
        div.style.cssText = `position:fixed;transform:translate(-50%,-50%);color:${color};font-size:11px;font-weight:700;font-family:'Noto Sans','Noto Sans Thai',sans-serif;pointer-events:none;opacity:0;transition:opacity .18s ease;z-index:25;text-shadow:0 0 4px rgba(0,0,0,.8);`;
        document.body.appendChild(div);
        return { div, localPos: pos };
    });
    gizmoLabelDivs[canvasId] = labelDivs;

    const hover = CONFIG.AXIS_GIZMO.hoverRegion;
    const onMove = (evt) => {
        const rect = canvas.getBoundingClientRect();
        const relX = (evt.clientX - rect.left) / rect.width;
        const relY = (evt.clientY - rect.top) / rect.height;
        const inGizmo = relX >= hover.xMin && relY <= hover.yMax;
        labelDivs.forEach(({ div }) => { div.style.opacity = inGizmo ? '1' : '0'; });
    };
    const onLeave = () => labelDivs.forEach(({ div }) => { div.style.opacity = '0'; });
    canvas.addEventListener('mousemove', onMove);
    canvas.addEventListener('mouseleave', onLeave);
    gizmoMouseHandlers[canvasId] = { onMove, onLeave, canvas };
}

function renderAxisGizmo(canvasId) {
    const renderer = renderers[canvasId];
    const gizmoScene = gizmoScenes[canvasId];
    const gizmoCamera = gizmoCameras[canvasId];
    const mainCamera = cameras[canvasId];
    const controls = controlsMap[canvasId];
    const canvas = canvasEls[canvasId];
    if (!renderer || !gizmoScene || !gizmoCamera || !mainCamera || !controls) return;

    const dir = mainCamera.position.clone().sub(controls.target).normalize();
    gizmoCamera.position.copy(dir).multiplyScalar(3.5);
    gizmoCamera.up.copy(mainCamera.up);
    gizmoCamera.lookAt(0, 0, 0);

    const vp = CONFIG.AXIS_GIZMO.viewport;
    const w = renderer.domElement.clientWidth;
    const h = renderer.domElement.clientHeight;
    const dpr = renderer.getPixelRatio();
    const x = Math.floor(vp.x * w * dpr);
    const y = Math.floor(vp.y * h * dpr);
    const vw = Math.ceil(vp.width * w * dpr);
    const vh = Math.ceil(vp.height * h * dpr);

    renderer.setViewport(x, y, vw, vh);
    renderer.setScissor(x, y, vw, vh);
    renderer.setScissorTest(true);
    renderer.clearDepth();
    renderer.render(gizmoScene, gizmoCamera);
    renderer.setScissorTest(false);
    renderer.setViewport(0, 0, w * dpr, h * dpr);

    const labelDivs = gizmoLabelDivs[canvasId];
    if (labelDivs && canvas) {
        const rect = canvas.getBoundingClientRect();
        const gizmoRectW = rect.width * vp.width;
        const gizmoRectH = rect.height * vp.height;
        const gizmoRectLeft = rect.left + rect.width * vp.x;
        const gizmoRectTop = rect.top + rect.height * (1 - vp.y - vp.height);
        labelDivs.forEach(({ div, localPos }) => {
            const ndc = localPos.clone().project(gizmoCamera);
            if (ndc.z >= -1 && ndc.z <= 1) {
                div.style.left = (gizmoRectLeft + (ndc.x * 0.5 + 0.5) * gizmoRectW) + 'px';
                div.style.top = (gizmoRectTop + (1 - (ndc.y * 0.5 + 0.5)) * gizmoRectH) + 'px';
            }
        });
    }
}

function disposeAxisGizmo(canvasId) {
    if (gizmoMouseHandlers[canvasId]) {
        const { onMove, onLeave, canvas } = gizmoMouseHandlers[canvasId];
        canvas.removeEventListener('mousemove', onMove);
        canvas.removeEventListener('mouseleave', onLeave);
        delete gizmoMouseHandlers[canvasId];
    }
    if (gizmoLabelDivs[canvasId]) {
        gizmoLabelDivs[canvasId].forEach(({ div }) => div.remove());
        delete gizmoLabelDivs[canvasId];
    }
    if (gizmoScenes[canvasId]) { disposeObject3D(gizmoScenes[canvasId]); delete gizmoScenes[canvasId]; }
    delete gizmoCameras[canvasId];
}

// ============================================================================
// SECTION VIEW (GPU clipping plane — simplified: no cut-face hatch fill)
// ============================================================================

export function setSectionPlane(canvasId, enabled, axis, offsetMm, inverted) {
    const renderer = renderers[canvasId];
    const meshes = cadMeshes[canvasId] || [];
    if (!renderer) return;

    if (sectionStates[canvasId]?.fillMesh) {
        scenes[canvasId]?.remove(sectionStates[canvasId].fillMesh);
        disposeObject3D(sectionStates[canvasId].fillMesh);
    }
    meshes.forEach((mesh) => { mesh.material.clippingPlanes = null; });
    sectionStates[canvasId] = null;

    if (!enabled) return;

    renderer.localClippingEnabled = true;
    const box = computeBounds(modelRoots[canvasId]);
    const center = box ? box.getCenter(new THREE.Vector3()) : new THREE.Vector3();
    const axisCenter = axis === 'x' ? center.x : axis === 'y' ? center.y : center.z;
    const worldOffset = axisCenter + offsetMm;
    const defaultSign = axis === 'x' || axis === 'y' ? -1 : 1;
    const sign = inverted ? -defaultSign : defaultSign;
    const normal = new THREE.Vector3(
        axis === 'x' ? sign : 0,
        axis === 'y' ? sign : 0,
        axis === 'x' || axis === 'y' ? 0 : sign,
    );
    const plane = new THREE.Plane(normal, -(normal.x * worldOffset + normal.y * worldOffset + normal.z * worldOffset));
    meshes.forEach((mesh) => { mesh.material.clippingPlanes = [plane]; });

    if (box) {
        const size = box.getSize(new THREE.Vector3()).length();
        const fillGeom = new THREE.PlaneGeometry(size, size);
        const fillMat = new THREE.MeshBasicMaterial({
            color: CONFIG.SECTION.fillColor, transparent: true, opacity: CONFIG.SECTION.fillOpacity,
            side: THREE.DoubleSide, depthWrite: false,
        });
        const fillMesh = new THREE.Mesh(fillGeom, fillMat);
        fillMesh.position.copy(normal).multiplyScalar(-plane.constant + CONFIG.SECTION.planeLiftMm);
        fillMesh.quaternion.setFromUnitVectors(new THREE.Vector3(0, 0, 1), normal);
        fillMesh.renderOrder = 2;
        scenes[canvasId]?.add(fillMesh);
        sectionStates[canvasId] = { plane, fillMesh };
    }
    markDirty(canvasId);
}

export function enableSectionPanelDrag(panelId) {
    // no-op: the section panel is Blazor-rendered HTML, not part of the 3D viewer.
}

// ============================================================================
// TURNING AXIS OVERLAY (CNC) — line + label through the server-supplied axis
// ============================================================================

export function setTurningAxis(canvasId, primaryAxis, axisVector, axisPoint) {
    clearTurningAxis(canvasId);
    const scene = scenes[canvasId];
    const box = computeBounds(modelRoots[canvasId]);
    if (!scene || !box || !axisVector || !axisPoint) return;

    const dir = new THREE.Vector3(axisVector[0], axisVector[1], axisVector[2]).normalize();
    const point = new THREE.Vector3(axisPoint[0], axisPoint[1], axisPoint[2]);
    const span = box.getSize(new THREE.Vector3()).length();
    const a = point.clone().addScaledVector(dir, span);
    const b = point.clone().addScaledVector(dir, -span);

    const geometry = new THREE.BufferGeometry().setFromPoints([a, b]);
    const material = new THREE.LineDashedMaterial({
        color: CONFIG.TURNING_AXIS.color, dashSize: CONFIG.TURNING_AXIS.dashSize, gapSize: CONFIG.TURNING_AXIS.gapSize,
    });
    const line = new THREE.Line(geometry, material);
    line.computeLineDistances();
    scene.add(line);

    const labelDiv = document.createElement('div');
    labelDiv.textContent = `${primaryAxis || 'axis'} (turning)`;
    labelDiv.style.cssText = "position:fixed;transform:translate(-50%,-100%);color:#ffa726;font-size:11px;font-weight:600;font-family:'Noto Sans','Noto Sans Thai',sans-serif;pointer-events:none;z-index:20;text-shadow:0 0 4px rgba(0,0,0,.8);";
    document.body.appendChild(labelDiv);

    turningAxisObjects[canvasId] = { line, labelDiv, worldPoint: a };
    markDirty(canvasId);
}

export function clearTurningAxis(canvasId, _clearRequest = true) {
    const entry = turningAxisObjects[canvasId];
    if (!entry) return;
    scenes[canvasId]?.remove(entry.line);
    disposeObject3D(entry.line);
    entry.labelDiv.remove();
    delete turningAxisObjects[canvasId];
    markDirty(canvasId);
}

function updateTurningAxisLabel(canvasId) {
    const entry = turningAxisObjects[canvasId];
    const camera = cameras[canvasId];
    const canvas = canvasEls[canvasId];
    if (!entry || !camera || !canvas) return;
    const ndc = entry.worldPoint.clone().project(camera);
    const rect = canvas.getBoundingClientRect();
    entry.labelDiv.style.left = (rect.left + (ndc.x * 0.5 + 0.5) * rect.width) + 'px';
    entry.labelDiv.style.top = (rect.top + (1 - (ndc.y * 0.5 + 0.5)) * rect.height) + 'px';
}

// ============================================================================
// DFM OVERLAYS (server GLB only — QuoteEngine has no local-face-index variant)
// ============================================================================

function dfmOverlayKey(partKey, overlayKey) { return `${partKey || ''}|${overlayKey || ''}`; }

export async function toggleDfmOverlay(canvasId, partKey, overlayKey, glbUrl, visible) {
    if (!dfmOverlays[canvasId]) dfmOverlays[canvasId] = new Map();
    const key = dfmOverlayKey(partKey, overlayKey);
    const map = dfmOverlays[canvasId];
    const scene = scenes[canvasId];
    if (!scene) return;

    if (!visible) {
        const existing = map.get(key);
        if (existing) { scene.remove(existing); disposeObject3D(existing); map.delete(key); }
        return;
    }
    if (map.has(key)) { map.get(key).visible = true; return; }

    try {
        const gltf = await new GLTFLoader().loadAsync(glbUrl);
        const overlay = gltf.scene;
        overlay.quaternion.premultiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), Math.PI / 2));
        const offset = modelRoots[canvasId]?.children?.[0]?.position;
        if (offset) overlay.position.copy(offset);
        overlay.traverse((n) => {
            if (n.isMesh) {
                n.material = new THREE.MeshBasicMaterial({
                    color: CONFIG.DFM_OVERLAY_COLOR, transparent: true, opacity: CONFIG.DFM_OVERLAY_OPACITY,
                    depthWrite: false, side: THREE.DoubleSide,
                });
            }
        });
        scene.add(overlay);
        map.set(key, overlay);
        markDirty(canvasId);
    } catch (err) {
        console.error('[ThreeViewer] toggleDfmOverlay failed:', err);
    }
}

export function clearDfmOverlays(canvasId, partKey) {
    const map = dfmOverlays[canvasId];
    const scene = scenes[canvasId];
    if (!map || !scene) return;
    Array.from(map.entries()).forEach(([key, overlay]) => {
        if (partKey && !key.startsWith(`${partKey}|`)) return;
        scene.remove(overlay);
        disposeObject3D(overlay);
        map.delete(key);
    });
    markDirty(canvasId);
}

// ============================================================================
// FLIPPED-TRIANGLE VIEW (back-facing triangles highlighted red)
// ============================================================================

function makeFlippedTriangleMaterial() {
    return new THREE.ShaderMaterial({
        side: THREE.DoubleSide,
        uniforms: {},
        vertexShader: `
            void main() { gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }
        `,
        fragmentShader: `
            void main() {
                vec3 normalColor = vec3(0.78, 0.78, 0.80);
                vec3 flippedColor = vec3(1.0, 0.2, 0.2);
                gl_FragColor = vec4(gl_FrontFacing ? normalColor : flippedColor, 1.0);
            }
        `,
    });
}

export function enableFlippedTriangleView(canvasId, enabled) {
    const meshes = cadMeshes[canvasId] || [];
    if (enabled) {
        if (!flippedTriangleOriginalMaterials[canvasId]) flippedTriangleOriginalMaterials[canvasId] = new Map();
        const saved = flippedTriangleOriginalMaterials[canvasId];
        meshes.forEach((mesh) => {
            if (!saved.has(mesh.uuid)) saved.set(mesh.uuid, mesh.material);
            mesh.material = makeFlippedTriangleMaterial();
        });
    } else {
        const saved = flippedTriangleOriginalMaterials[canvasId];
        if (!saved) return;
        meshes.forEach((mesh) => {
            const original = saved.get(mesh.uuid);
            if (original) mesh.material = original;
        });
        saved.clear();
    }
    markDirty(canvasId);
}

// ============================================================================
// MEASURE TOOL (2-point distance + angle from horizontal)
// ============================================================================

function measureRaycast(canvasId, evt) {
    const canvas = canvasEls[canvasId];
    const camera = cameras[canvasId];
    const rect = canvas.getBoundingClientRect();
    const pointer = new THREE.Vector2(
        ((evt.clientX - rect.left) / rect.width) * 2 - 1,
        -((evt.clientY - rect.top) / rect.height) * 2 + 1,
    );
    const raycaster = new THREE.Raycaster();
    raycaster.setFromCamera(pointer, camera);
    const hits = raycaster.intersectObjects(cadMeshes[canvasId] || [], false);
    return hits.length > 0 ? hits[0].point : null;
}

function makeMeasureMarker(point) {
    const marker = new THREE.Mesh(
        new THREE.SphereGeometry(Math.max(CONFIG.MEASURE.endpointRadiusRatio, 0.3), 12, 12),
        new THREE.MeshBasicMaterial({ color: CONFIG.MEASURE.color }),
    );
    marker.position.copy(point);
    return marker;
}

export function enableMeasureTool(canvasId, dotNetRef) {
    disableMeasureTool(canvasId);
    const canvas = canvasEls[canvasId];
    const scene = scenes[canvasId];
    if (!canvas || !scene) return;

    const state = { points: [], line: null, markers: [], dotNetRef };
    const clickHandler = (evt) => {
        const point = measureRaycast(canvasId, evt);
        if (!point) return;
        state.points.push(point);
        const marker = makeMeasureMarker(point);
        scene.add(marker);
        state.markers.push(marker);

        if (state.points.length === 2) {
            const [a, b] = state.points;
            if (state.line) { scene.remove(state.line); disposeObject3D(state.line); }
            const geometry = new THREE.BufferGeometry().setFromPoints([a, b]);
            const line = new THREE.Line(geometry, new THREE.LineBasicMaterial({ color: CONFIG.MEASURE.color }));
            scene.add(line);
            state.line = line;

            const distanceMm = a.distanceTo(b);
            const horizontal = new THREE.Vector2(b.x - a.x, b.y - a.y).length();
            const vertical = Math.abs(b.z - a.z);
            const angleDeg = (Math.atan2(vertical, horizontal) * 180) / Math.PI;
            dotNetRef?.invokeMethodAsync('NotifyMeasureResult', distanceMm.toFixed(2), angleDeg.toFixed(1));

            state.markers.forEach((m) => scene.remove(m));
            state.markers.forEach((m) => disposeObject3D(m));
            state.markers = [];
            state.points = [];
        }
        markDirty(canvasId);
    };
    canvas.addEventListener('click', clickHandler);
    state.clickHandler = clickHandler;
    measureStates[canvasId] = state;
}

export function disableMeasureTool(canvasId) {
    const state = measureStates[canvasId];
    if (!state) return;
    const canvas = canvasEls[canvasId];
    const scene = scenes[canvasId];
    if (canvas && state.clickHandler) canvas.removeEventListener('click', state.clickHandler);
    if (scene) {
        if (state.line) { scene.remove(state.line); disposeObject3D(state.line); }
        state.markers.forEach((m) => { scene.remove(m); disposeObject3D(m); });
    }
    delete measureStates[canvasId];
    markDirty(canvasId);
}

// ============================================================================
// THICKNESS ANALYSIS (entry/exit ray probe along the camera view direction)
// ============================================================================

function thicknessMarkerColor(thicknessMm) {
    if (thicknessMm >= CONFIG.THICKNESS.safeThresholdMm) return 0x4caf50;
    if (thicknessMm <= CONFIG.THICKNESS.criticalThresholdMm) return 0xff5252;
    return 0xffa726;
}

export function enableThicknessAnalysis(canvasId) {
    disableThicknessAnalysis(canvasId);
    const canvas = canvasEls[canvasId];
    const camera = cameras[canvasId];
    const scene = scenes[canvasId];
    if (!canvas || !camera || !scene) return;

    const state = { markers: [] };
    const clickHandler = (evt) => {
        const rect = canvas.getBoundingClientRect();
        const pointer = new THREE.Vector2(
            ((evt.clientX - rect.left) / rect.width) * 2 - 1,
            -((evt.clientY - rect.top) / rect.height) * 2 + 1,
        );
        const raycaster = new THREE.Raycaster();
        raycaster.far = CONFIG.THICKNESS.maxRayDistanceMm * 10;
        raycaster.setFromCamera(pointer, camera);
        const meshes = cadMeshes[canvasId] || [];
        const hits = raycaster.intersectObjects(meshes, false);
        if (hits.length < 2) return;
        const entry = hits[0];
        const exit = hits[hits.length - 1];
        const thicknessMm = entry.point.distanceTo(exit.point);
        if (thicknessMm > CONFIG.THICKNESS.maxRayDistanceMm) return;

        const marker = new THREE.Mesh(
            new THREE.SphereGeometry(0.4, 10, 10),
            new THREE.MeshBasicMaterial({ color: thicknessMarkerColor(thicknessMm) }),
        );
        marker.position.copy(entry.point);
        scene.add(marker);
        state.markers.push(marker);
        markDirty(canvasId);
        debugLog(`thickness probe: ${thicknessMm.toFixed(2)}mm`);
    };
    canvas.addEventListener('click', clickHandler);
    state.clickHandler = clickHandler;
    thicknessStates[canvasId] = state;
}

export function disableThicknessAnalysis(canvasId) {
    const state = thicknessStates[canvasId];
    if (!state) return;
    const canvas = canvasEls[canvasId];
    const scene = scenes[canvasId];
    if (canvas && state.clickHandler) canvas.removeEventListener('click', state.clickHandler);
    if (scene) state.markers.forEach((m) => { scene.remove(m); disposeObject3D(m); });
    delete thicknessStates[canvasId];
    markDirty(canvasId);
}

// ============================================================================
// MATERIAL / COLOR SELECTION — no rendering effect (Realistic mode removed)
// ============================================================================

export function setPartMaterial(canvasId, processId, finishCode, roughnessCode, cssColor, materialId = null) {
    // Realistic PBR materials were removed; solid/wireframe/transparent modes always
    // show the uniform CAD-gray material, matching the former "solid" mode behaviour.
}
export function setPartColor(canvasId, cssColor) { /* no visual effect — see setPartMaterial */ }
// ============================================================================
// LOCAL ADVISORY GEOMETRY RUNTIME (browser-first DFM)
// ----------------------------------------------------------------------------
// Bridges the loaded viewer geometry (or the original uploaded bytes) into the
// GeometryService-owned browser runtime worker served by the BFF at
// /geometry/client-runtime/*. The worker computes advisory mesh metrics and DFM
// issues locally; the server remains authoritative. Results are delivered back
// to Blazor via the QePartViewer dotNet callbacks.
// ============================================================================

const GEOMETRY_RUNTIME_MANIFEST_URL = '/geometry/client-runtime/manifest.json';
let advisoryRuntimePromise = null;
let advisoryWorker = null;
let advisoryWorkerKey = null;
const advisoryPending = new Map();
let advisoryRequestSeq = 0;

async function loadAdvisoryRuntimeManifest() {
    if (advisoryRuntimePromise) return advisoryRuntimePromise;
    advisoryRuntimePromise = (async () => {
        const response = await fetch(GEOMETRY_RUNTIME_MANIFEST_URL, { headers: { Accept: 'application/json' } });
        if (!response || !response.ok) {
            throw new Error(`Geometry runtime manifest request failed (${response ? response.status : 'no response'}).`);
        }
        const manifest = await response.json();
        const workerAsset = manifest?.assets?.worker;
        const wasmAsset = manifest?.assets?.wasm;
        if (!workerAsset) throw new Error('Geometry runtime manifest did not include a worker asset.');
        const origin = (typeof location !== 'undefined' && location.origin) ? location.origin : GEOMETRY_RUNTIME_MANIFEST_URL;
        return {
            workerUrl: new URL(workerAsset, origin).href,
            wasmUrl: wasmAsset ? new URL(wasmAsset, origin).href : null
        };
    })().catch((error) => { advisoryRuntimePromise = null; throw error; });
    return advisoryRuntimePromise;
}

async function ensureAdvisoryWorker(workerUrl) {
    if (advisoryWorker && advisoryWorkerKey === workerUrl) return advisoryWorker;
    if (advisoryWorker) { try { advisoryWorker.terminate(); } catch { /* ignore */ } advisoryWorker = null; }

    // Load through a same-origin blob so the worker constructs even when the
    // manifest points at a cross-origin GeometryService asset URL.
    const response = await fetch(workerUrl);
    if (!response || !response.ok) {
        throw new Error(`Geometry runtime worker request failed (${response ? response.status : 'no response'}).`);
    }
    const source = await response.text();
    const blobUrl = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
    let worker;
    try {
        worker = new Worker(blobUrl);
    } finally {
        URL.revokeObjectURL(blobUrl);
    }

    const failAll = (error) => {
        for (const pending of advisoryPending.values()) pending.reject(error);
        advisoryPending.clear();
    };
    worker.onmessage = (event) => {
        const message = event.data || {};
        const pending = advisoryPending.get(message.id);
        if (!pending) return;
        advisoryPending.delete(message.id);
        if (message.ok) pending.resolve(message.result);
        else pending.reject(new Error(message.error || 'Local geometry runtime reported a failure.'));
    };
    worker.onerror = (event) => { failAll(new Error(event?.message || 'Local geometry runtime worker crashed.')); };
    worker.onmessageerror = () => { failAll(new Error('Local geometry runtime returned an unreadable message.')); };

    advisoryWorker = worker;
    advisoryWorkerKey = workerUrl;
    return worker;
}

function runAdvisoryOperation(worker, payload) {
    const id = `dfm-${advisoryRequestSeq += 1}`;
    return new Promise((resolve, reject) => {
        advisoryPending.set(id, { resolve, reject });
        try { worker.postMessage({ id, ...payload }); }
        catch (error) { advisoryPending.delete(id); reject(error); }
    });
}

async function waitForViewerMesh(canvasId, timeoutMs = 6000) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const meshes = cadMeshes[canvasId];
        if (Array.isArray(meshes) && meshes.some((mesh) => mesh?.geometry?.attributes?.position)) return true;
        if (Date.now() >= deadline) return false;
        await new Promise((resolve) => setTimeout(resolve, 100));
    }
}

// Collects world-space (millimetre) triangle buffers from the loaded viewer mesh.
export function collectAdvisoryMeshBuffers(canvasId) {
    const meshes = cadMeshes[canvasId] || [];
    const positions = [];
    const indices = [];
    for (const mesh of meshes) {
        const geometry = mesh?.geometry;
        const positionAttr = geometry?.attributes?.position;
        if (!positionAttr) continue;
        mesh.updateWorldMatrix(true, false);
        const worldGeometry = geometry.clone().applyMatrix4(mesh.matrixWorld);
        const worldPositions = worldGeometry.attributes.position.array;
        const baseVertex = positions.length / 3;
        for (let i = 0; i < worldPositions.length; i += 1) positions.push(worldPositions[i]);
        const indexAttr = worldGeometry.getIndex();
        if (indexAttr) {
            const source = indexAttr.array;
            for (let i = 0; i < source.length; i += 1) indices.push(baseVertex + source[i]);
        } else {
            const vertexCount = worldPositions.length / 3;
            for (let i = 0; i < vertexCount; i += 1) indices.push(baseVertex + i);
        }
        worldGeometry.dispose();
    }
    return { positions, indices };
}

async function buildAdvisoryInput(canvasId, options) {
    // Prefer the original uploaded bytes (most faithful to the customer file).
    const uploads = (typeof window !== 'undefined') ? window.quoteEngineUploads : null;
    if (options.fileBytesProvider === 'quoteEngineUploads' && options.clientUploadId && uploads?.getFileBytes) {
        try {
            const bytes = await uploads.getFileBytes(options.clientUploadId);
            if (bytes && bytes.length > 0) {
                return { input: { fileBytes: bytes, fileName: options.fileName || '' }, source: 'upload_bytes' };
            }
        } catch (error) {
            console.warn('[ThreeViewer] Upload bytes unavailable; falling back to viewer mesh.', error);
        }
    }

    // Fall back to the loaded viewer mesh (always available once rendered).
    await waitForViewerMesh(canvasId);
    const meshBuffers = collectAdvisoryMeshBuffers(canvasId);
    if (meshBuffers.positions.length >= 9 && meshBuffers.indices.length >= 3) {
        return { input: { meshBuffers }, source: 'viewer_mesh' };
    }
    return null;
}

function mapAdvisoryResult(result, processCode) {
    const metrics = result?.metrics || {};
    const box = metrics.boundingBox || null;
    const issues = Array.isArray(result?.issues) ? result.issues : [];
    return {
        processCode: result?.processCode ?? processCode ?? null,
        runtimeVersion: result?.runtimeVersion ?? null,
        algorithmVersion: result?.algorithmVersion ?? null,
        authority: result?.authority ?? null,
        executionMode: result?.executionMode ?? null,
        isAuthoritative: result?.isAuthoritative ?? false,
        inputHash: result?.inputHash ?? null,
        metrics: {
            vertexCount: metrics.vertexCount ?? null,
            faceCount: metrics.faceCount ?? null,
            volumeMm3: metrics.volumeMm3 ?? null,
            surfaceAreaMm2: metrics.surfaceAreaMm2 ?? null,
            boundingBoxMm: box ? { x: box.x, y: box.y, z: box.z } : null,
            isManifold: metrics.isManifold ?? null,
            nonManifoldEdgeCount: metrics.nonManifoldEdgeCount ?? null,
            complexity: metrics.complexity ?? null
        },
        issues: issues.map((issue) => ({
            category: issue?.category ?? null,
            severity: issue?.severity ?? null,
            title: issue?.title ?? null,
            description: issue?.description ?? null,
            value: issue?.value ?? null,
            threshold: issue?.threshold ?? null,
            faceIndices: Array.isArray(issue?.faceIndices) ? issue.faceIndices : [],
            centroid: Array.isArray(issue?.centroid) ? issue.centroid : []
        }))
    };
}

export async function runLocalAdvisoryGeometry(canvasId, options) {
    const opts = options || {};
    const processCode = opts.processCode || null;
    const dotNetRef = opts.dotNetRef || dotNetRefs[canvasId] || null;

    const notifyUnavailable = async (reason) => {
        if (!dotNetRef) return;
        try { await dotNetRef.invokeMethodAsync('NotifyLocalGeometryRuntimeUnavailable', { processCode, reason }); }
        catch (error) { console.warn('[ThreeViewer] Local DFM unavailable callback failed.', error); }
    };

    try {
        if (dotNetRef) {
            try { await dotNetRef.invokeMethodAsync('NotifyLocalGeometryRuntimeStarted', { processCode }); }
            catch { /* non-fatal: proceed with analysis */ }
        }

        const prepared = await buildAdvisoryInput(canvasId, opts);
        if (!prepared) { await notifyUnavailable('no_local_geometry_input'); return; }

        const runtime = await loadAdvisoryRuntimeManifest();
        const worker = await ensureAdvisoryWorker(runtime.workerUrl);
        const result = await runAdvisoryOperation(worker, {
            operation: 'analyze',
            input: prepared.input,
            processCode: processCode || 'FDM',
            wasmUrl: runtime.wasmUrl
        });

        if (!dotNetRef) return;
        await dotNetRef.invokeMethodAsync('NotifyLocalGeometryRuntimeComplete', mapAdvisoryResult(result, processCode));
    } catch (error) {
        console.warn('[ThreeViewer] Local advisory geometry runtime failed.', error);
        await notifyUnavailable('local_runtime_error');
    }
}
