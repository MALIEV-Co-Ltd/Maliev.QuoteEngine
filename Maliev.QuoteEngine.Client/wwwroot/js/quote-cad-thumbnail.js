// Browser-local CAD thumbnail generation for Make Studio.
// STL and 3MF mesh extraction stays owned by the GeometryService browser runtime;
// three.js only frames and renders the extracted mesh into a transient PNG.

import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OBJLoader } from 'three/addons/loaders/OBJLoader.js';

const THUMBNAIL_SIZE = 256;
const DEFAULT_TIMEOUT_MS = 12000;
const MAX_SOURCE_BYTES = 50 * 1024 * 1024;
const DIRECT_LOADER_EXTENSIONS = new Set(['.obj', '.glb', '.gltf']);
const RUNTIME_EXTRACTION_EXTENSIONS = new Set(['.stl', '.3mf']);
const SUPPORTED_EXTENSIONS = new Set([
    ...DIRECT_LOADER_EXTENSIONS,
    ...RUNTIME_EXTRACTION_EXTENSIONS
]);
const GEOMETRY_RUNTIME_MANIFEST_URL = '/quote/v1/geometry/runtime/manifest';
const GEOMETRY_RUNTIME_ASSET_BASE_URL = '/quote/v1/geometry/runtime/assets/';

function normalizedExtension(fileUrl, explicitExtension) {
    if (typeof explicitExtension === 'string' && explicitExtension.trim()) {
        const extension = explicitExtension.trim().toLowerCase();
        return extension.startsWith('.') ? extension : `.${extension}`;
    }

    try {
        const pathname = new URL(fileUrl, window.location.href).pathname;
        const dotIndex = pathname.lastIndexOf('.');
        return dotIndex >= 0 ? pathname.slice(dotIndex).toLowerCase() : '';
    } catch {
        return '';
    }
}

function resolveRuntimeAssetUrl(assetPath) {
    const value = String(assetPath || '');
    if (value.includes('..') || value.includes('\\')) {
        return null;
    }

    const assetName = value.split('?')[0].split('/').pop();
    if (!assetName || assetName.includes('/') || assetName.includes('\\')) {
        return null;
    }

    return `${GEOMETRY_RUNTIME_ASSET_BASE_URL}${encodeURIComponent(assetName)}`;
}

function neutralMaterial() {
    return new THREE.MeshStandardMaterial({
        color: new THREE.Color(0.72, 0.77, 0.82),
        emissive: new THREE.Color(0.055, 0.06, 0.07),
        roughness: 0.62,
        metalness: 0.08,
        side: THREE.DoubleSide
    });
}

async function extractMeshWithGeometryRuntime(buffer, fileName, signal, timeoutMs) {
    const manifestResponse = await fetch(GEOMETRY_RUNTIME_MANIFEST_URL, { signal });
    if (!manifestResponse.ok) {
        throw new Error(`Geometry runtime manifest unavailable: ${manifestResponse.status}`);
    }

    const manifest = await manifestResponse.json();
    const workerUrl = resolveRuntimeAssetUrl(manifest.assets && manifest.assets.worker);
    const wasmUrl = resolveRuntimeAssetUrl(manifest.assets && manifest.assets.wasm);
    if (!workerUrl) {
        throw new Error('Geometry runtime worker asset unavailable');
    }

    return await new Promise((resolve, reject) => {
        const worker = new Worker(workerUrl, { name: 'maliev-quote-thumbnail' });
        const requestId = `thumbnail:${fileName}:${crypto.randomUUID?.() || Math.random().toString(36).slice(2)}`;
        let settled = false;

        const finish = (callback, value) => {
            if (settled) {
                return;
            }

            settled = true;
            clearTimeout(timeoutId);
            signal.removeEventListener('abort', abort);
            try { worker.terminate(); } catch { /* best-effort cleanup */ }
            callback(value);
        };
        const abort = () => finish(reject, new Error('Local thumbnail generation cancelled'));
        const timeoutId = setTimeout(
            () => finish(reject, new Error('Local thumbnail generation timed out')),
            timeoutMs);

        signal.addEventListener('abort', abort, { once: true });
        worker.onmessage = event => {
            const message = event.data || {};
            if (message.id !== requestId) {
                return;
            }

            if (!message.ok || !message.result?.meshBuffers) {
                finish(reject, new Error(message.error || 'Geometry runtime mesh extraction failed'));
                return;
            }

            finish(resolve, message.result.meshBuffers);
        };
        worker.onerror = event => finish(
            reject,
            new Error(event?.message || 'Geometry runtime worker failed'));
        worker.postMessage({
            id: requestId,
            operation: 'extract_mesh',
            input: {
                fileBytes: new Uint8Array(buffer),
                fileName
            },
            wasmUrl
        });
    });
}

function buildRuntimeMesh(meshBuffers) {
    const positions = Float32Array.from(meshBuffers.positions || []);
    const indices = Array.from(meshBuffers.indices || [], Number);
    if (positions.length < 9 || indices.length < 3) {
        return null;
    }

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geometry.setIndex(indices);
    geometry.computeVertexNormals();
    return new THREE.Mesh(geometry, neutralMaterial());
}

function disposeMaterial(material) {
    if (!material) {
        return;
    }

    for (const value of Object.values(material)) {
        if (value?.isTexture && typeof value.dispose === 'function') {
            value.dispose();
        }
    }
    material.dispose?.();
}

function disposeObject(root) {
    root?.traverse?.(node => {
        node.geometry?.dispose?.();
        if (Array.isArray(node.material)) {
            node.material.forEach(disposeMaterial);
        } else {
            disposeMaterial(node.material);
        }
    });
}

function prepareCamera(root) {
    root.updateMatrixWorld(true);
    const bounds = new THREE.Box3().setFromObject(root);
    if (bounds.isEmpty()) {
        throw new Error('Mesh has no geometry to render');
    }

    const center = bounds.getCenter(new THREE.Vector3());
    const radius = Math.max(bounds.getSize(new THREE.Vector3()).length() / 2, 1e-6);
    const camera = new THREE.PerspectiveCamera(36, 1, 0.01, Math.max(1000, radius * 12));
    camera.up.set(0, 0, 1);

    const direction = new THREE.Vector3(1, -1, 0.82).normalize();
    const fitDistance = (radius / Math.tan(THREE.MathUtils.degToRad(camera.fov / 2))) * 1.14;
    camera.position.copy(center).addScaledVector(direction, fitDistance);
    camera.near = Math.max(0.01, fitDistance - radius * 2.1);
    camera.far = fitDistance + radius * 4.5;
    camera.lookAt(center);
    camera.updateProjectionMatrix();
    return camera;
}

async function loadModel(blob, extension, fileName, signal, timeoutMs) {
    if (RUNTIME_EXTRACTION_EXTENSIONS.has(extension)) {
        const buffers = await extractMeshWithGeometryRuntime(
            await blob.arrayBuffer(),
            fileName || `thumbnail-source${extension}`,
            signal,
            timeoutMs);
        const mesh = buildRuntimeMesh(buffers);
        if (!mesh) {
            throw new Error('Geometry runtime returned no renderable mesh');
        }
        return { root: mesh, temporaryUrl: null };
    }

    const temporaryUrl = URL.createObjectURL(blob);
    try {
        if (extension === '.obj') {
            return { root: await new OBJLoader().loadAsync(temporaryUrl), temporaryUrl };
        }

        const gltf = await new GLTFLoader().loadAsync(temporaryUrl);
        const root = gltf.scene;
        // glTF is Y-up; the Make Studio viewer and GeometryService use Z-up.
        root.quaternion.premultiply(
            new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), Math.PI / 2));
        return { root, temporaryUrl };
    } catch (error) {
        URL.revokeObjectURL(temporaryUrl);
        throw error;
    }
}

export async function generateThumbnail(fileUrl, options = {}) {
    const extension = normalizedExtension(fileUrl, options.fileExtension);
    if (!SUPPORTED_EXTENSIONS.has(extension)) {
        return null;
    }

    const timeoutMs = Number(options.timeoutMs) > 0
        ? Number(options.timeoutMs)
        : DEFAULT_TIMEOUT_MS;
    const abortController = new AbortController();
    const timeoutId = setTimeout(() => abortController.abort('timeout'), timeoutMs);
    let renderer = null;
    let root = null;
    let temporaryUrl = null;

    try {
        const response = await fetch(fileUrl, { signal: abortController.signal });
        if (!response.ok) {
            throw new Error(`CAD source unavailable: ${response.status}`);
        }

        const sourceBlob = await response.blob();
        if (sourceBlob.size > MAX_SOURCE_BYTES) {
            return null;
        }

        const loaded = await loadModel(
            sourceBlob,
            extension,
            options.fileName,
            abortController.signal,
            timeoutMs);
        root = loaded.root;
        temporaryUrl = loaded.temporaryUrl;

        const scene = new THREE.Scene();
        scene.background = null;
        scene.add(root);
        scene.add(new THREE.HemisphereLight(0xffffff, 0x334155, Math.PI * 1.15));
        const keyLight = new THREE.DirectionalLight(0xffffff, Math.PI * 0.9);
        keyLight.position.set(1, -1, 1.2);
        scene.add(keyLight);

        const canvas = document.createElement('canvas');
        canvas.width = THUMBNAIL_SIZE;
        canvas.height = THUMBNAIL_SIZE;
        renderer = new THREE.WebGLRenderer({
            canvas,
            alpha: true,
            antialias: true,
            preserveDrawingBuffer: true
        });
        renderer.setPixelRatio(1);
        renderer.setSize(THUMBNAIL_SIZE, THUMBNAIL_SIZE, false);
        renderer.outputColorSpace = THREE.SRGBColorSpace;
        renderer.render(scene, prepareCamera(root));
        return canvas.toDataURL('image/png');
    } finally {
        clearTimeout(timeoutId);
        if (temporaryUrl) {
            URL.revokeObjectURL(temporaryUrl);
        }
        disposeObject(root);
        renderer?.dispose?.();
        renderer?.forceContextLoss?.();
    }
}
