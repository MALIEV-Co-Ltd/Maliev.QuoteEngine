import opencascade from 'replicad-opencascadejs';
import {
  setOC,
  makeBox as replicadMakeBox,
  makeBaseBox,
  makeCylinder,
  makeSphere,
  draw,
  drawRectangle,
  drawCircle,
  Sketcher,
} from 'replicad';

let initialized = false;
let initPromise = null;
const shapes = new Map();

async function ensureInit() {
  if (initialized) return;
  if (initPromise) return initPromise;
  initPromise = (async () => {
    const OC = await opencascade({
      locateFile: () => '/lib/replicad/replicad_single.wasm',
    });
    setOC(OC);
    initialized = true;
    self.postMessage({ type: 'ready' });
  })();
  return initPromise;
}

function resolve(ref) {
  if (typeof ref === 'string') {
    const key = ref.trim();
    const s = key ? shapes.get(key) : null;
    if (!s) throw new Error(`Shape not found: ${ref}`);
    return s;
  }
  return ref || null;
}

function resolveOperationTarget(cmd, fallback) {
  const target = cmd.targetId == null || cmd.targetId === ''
    ? fallback
    : resolve(cmd.targetId);
  if (!target) throw new Error(`CAD operation ${cmd.op} requires a target shape`);
  return target;
}

function tryApplyEdgeOperation(target, operation, radius) {
  const value = Number(radius);
  if (!Number.isFinite(value) || value <= 0 || typeof target[operation] !== 'function') {
    return target;
  }

  try {
    return target[operation](value);
  } catch {
    return target;
  }
}

function requireSegmentParams(seg, values, requiredLength) {
  if (
    !Array.isArray(values) ||
    values.length < requiredLength ||
    values.slice(0, requiredLength).some((value) => !Number.isFinite(Number(value)))
  ) {
    throw new Error(`Profile segment ${seg.type} requires ${requiredLength} finite parameter(s)`);
  }
}

function requireCommandParams(cmd, values, requiredLength, options = {}) {
  if (
    !Array.isArray(values) ||
    values.length < requiredLength ||
    values.slice(0, requiredLength).some((value, index) => {
      const numeric = Number(value);
      return !Number.isFinite(numeric) || numeric < 0 || (numeric <= 0 && (!options.allowZeroAfterFirst || index === 0));
    })
  ) {
    throw new Error(`CAD operation ${cmd.op} requires ${requiredLength} valid parameter(s)`);
  }
}

function requireConeParams(cmd, values) {
  if (!Array.isArray(values) || values.length < 3) {
    throw new Error(`CAD operation ${cmd.op} requires a positive bottom radius, non-negative top radius, and positive height`);
  }

  const bottomRadius = Number(values[0]);
  const topRadius = Number(values[1]);
  const height = Number(values[2]);
  if (
    !Number.isFinite(bottomRadius) ||
    !Number.isFinite(topRadius) ||
    !Number.isFinite(height) ||
    bottomRadius <= 0 ||
    topRadius < 0 ||
    height <= 0
  ) {
    throw new Error(`CAD operation ${cmd.op} requires a positive bottom radius, non-negative top radius, and positive height`);
  }
}

function requireOptionalFiniteNumber(cmd, value, fieldName) {
  if (value == null) {
    return undefined;
  }

  const numeric = Number(value);
  if (!Number.isFinite(numeric)) {
    throw new Error(`CAD operation ${cmd.op} ${fieldName} must be finite`);
  }

  return numeric;
}

function requireOptionalNonZeroVector(cmd, values, fieldName) {
  if (values == null) {
    return null;
  }

  if (!Array.isArray(values) || values.length < 3) {
    throw new Error(`CAD operation ${cmd.op} ${fieldName} must be a non-zero finite vector`);
  }

  const vector = values.slice(0, 3).map((value) => Number(value));
  if (vector.some((value) => !Number.isFinite(value)) || vector.every((value) => value === 0)) {
    throw new Error(`CAD operation ${cmd.op} ${fieldName} must be a non-zero finite vector`);
  }

  return vector;
}

function requireOptionalFiniteVector(cmd, values, fieldName) {
  if (values == null) {
    return null;
  }

  if (!Array.isArray(values) || values.length < 3) {
    throw new Error(`CAD operation ${cmd.op} ${fieldName} must be a finite vector`);
  }

  const vector = values.slice(0, 3).map((value) => Number(value));
  if (vector.some((value) => !Number.isFinite(value))) {
    throw new Error(`CAD operation ${cmd.op} ${fieldName} must be a finite vector`);
  }

  return vector;
}

function requirePositiveProfileNumber(profile, fieldName) {
  const value = Number(profile[fieldName]);
  if (!Number.isFinite(value) || value <= 0) {
    throw new Error(`Profile ${fieldName} must be a positive finite number`);
  }

  return value;
}

function resolveProfilePlane(profile) {
  const plane = profile.plane || 'XY';
  if (plane !== 'XY' && plane !== 'XZ' && plane !== 'YZ') {
    throw new Error(`Profile plane must be XY, XZ, or YZ`);
  }

  return plane;
}

function buildProfile(profile) {
  const plane = resolveProfilePlane(profile);
  const sketch = new Sketcher(plane);
  for (const seg of profile.segments || []) {
    const p = seg.params || [];
    switch (seg.type) {
      case 'move':
        requireSegmentParams(seg, p, 2);
        sketch.movePointerTo(p);
        break;
      case 'line':
        if (p.length >= 4) {
          requireSegmentParams(seg, p, 4);
          sketch.movePointerTo([p[0], p[1]]);
          sketch.lineTo(p[2], p[3]);
        } else {
          requireSegmentParams(seg, p, 2);
          sketch.lineTo(p[0], p[1]);
        }
        break;
      case 'hLine':
        requireSegmentParams(seg, p, 1);
        sketch.hLine(p[0]);
        break;
      case 'vLine':
        requireSegmentParams(seg, p, 1);
        sketch.vLine(p[0]);
        break;
      case 'arc':
        requireSegmentParams(seg, p, 4);
        sketch.threePointsArc(p[0], p[1], p[2], p[3]);
        break;
      case 'bezier':
        requireSegmentParams(seg, p, 4);
        sketch.quadraticBezierCurveTo([p[0], p[1]], [p[2], p[3]]);
        break;
      default:
        throw new Error(`Unsupported profile segment: ${seg.type}`);
    }
  }
  if (profile.close !== false) {
    return sketch.close();
  }
  return sketch;
}

function buildFaceFromProfile(profile, params) {
  const plane = resolveProfilePlane(profile);
  const profileType = typeof profile.type === 'string' ? profile.type.toLowerCase() : '';
  if (profileType === 'circle' || Number(profile.radius) > 0) {
    const radius = requirePositiveProfileNumber(profile, 'radius');
    return drawCircle(radius).sketchOnPlane(plane);
  }

  if (
    profileType === 'rect' ||
    profileType === 'rectangle' ||
    (Number(profile.width) > 0 && Number(profile.height) > 0)
  ) {
    const width = requirePositiveProfileNumber(profile, 'width');
    const height = requirePositiveProfileNumber(profile, 'height');
    return drawRectangle(width, height).sketchOnPlane(plane);
  }

  return buildProfile(profile);
}

function makeCone(radiusBottom, radiusTop, height) {
  const sketch = new Sketcher('XZ')
    .movePointerTo([0, 0])
    .vLine(height)
    .hLine(radiusTop)
    .lineTo(radiusBottom, 0)
    .close();
  return sketch.revolve([0, 0, 1]);
}

function packedMeshData(meshResult) {
  return [
    meshResult.vertices.buffer,
    meshResult.triangles.buffer,
    meshResult.normals.buffer,
  ];
}

function processCommands(commands) {
  shapes.clear();
  let result;

  for (const cmd of commands) {
    let shape;
    const p = cmd.params || [];
    const offset = cmd.offset;

    switch (cmd.op) {
      case 'box': {
        requireCommandParams(cmd, p, 3);
        shape = replicadMakeBox([-p[0] / 2, -p[1] / 2, 0], [p[0] / 2, p[1] / 2, p[2]]);
        break;
      }
      case 'cylinder': {
        requireCommandParams(cmd, p, 2);
        shape = makeCylinder(p[0], p[1]);
        break;
      }
      case 'sphere': {
        requireCommandParams(cmd, p, 1);
        shape = makeSphere(p[0]);
        break;
      }
      case 'cone': {
        requireConeParams(cmd, p);
        shape = makeCone(p[0], p[1], p[2]);
        break;
      }
      case 'extrude': {
        const profile = cmd.profile;
        requireCommandParams(cmd, p, 1);
        const face = buildFaceFromProfile(profile, p);
        shape = face.extrude(p[0]);
        break;
      }
      case 'revolve': {
        const profile = cmd.profile;
        const axis = requireOptionalNonZeroVector(cmd, cmd.axis, 'axis') || [0, 0, 1];
        const angle = requireOptionalFiniteNumber(cmd, cmd.angle, 'angle');
        const face = buildFaceFromProfile(profile, p);
        shape = face.revolve(axis, angle);
        break;
      }
      case 'fuse': {
        const target = resolveOperationTarget(cmd, result);
        shape = target.fuse(resolve(cmd.toolId));
        break;
      }
      case 'cut': {
        const target = resolveOperationTarget(cmd, result);
        shape = target.cut(resolve(cmd.toolId));
        break;
      }
      case 'intersect': {
        const target = resolveOperationTarget(cmd, result);
        shape = target.intersect(resolve(cmd.toolId));
        break;
      }
      case 'fillet': {
        const target = resolveOperationTarget(cmd, result);
        shape = tryApplyEdgeOperation(target, 'fillet', cmd.radius || p[0]);
        break;
      }
      case 'chamfer': {
        const target = resolveOperationTarget(cmd, result);
        shape = tryApplyEdgeOperation(target, 'chamfer', cmd.radius || p[0]);
        break;
      }
      case 'loft': {
        const a = resolve(cmd.targetId);
        const b = resolve(cmd.toolId);
        shape = a.loftWith(b);
        break;
      }
      case 'translate': {
        shape = resolveOperationTarget(cmd, result);
        const translation = offset == null
          ? (p.length > 0 ? requireOptionalFiniteVector(cmd, p, 'params') : null)
          : requireOptionalFiniteVector(cmd, offset, 'offset');
        if (translation) shape = shape.translate(translation[0], translation[1], translation[2]);
        break;
      }
      case 'rotate': {
        const target = resolveOperationTarget(cmd, result);
        const axis = requireOptionalNonZeroVector(cmd, cmd.axis, 'axis') || [0, 0, 1];
        const angle = requireOptionalFiniteNumber(cmd, cmd.angle ?? (p.length > 0 ? p[0] : 0), 'angle') ?? 0;
        shape = target.rotate(axis, angle);
        break;
      }
      default:
        throw new Error(`Unknown CAD operation: ${cmd.op}`);
    }

    if (cmd.id) shapes.set(cmd.id, shape);
    if (cmd.resultId) shapes.set(cmd.resultId, shape);
    result = shape;
  }

  return result;
}

self.onmessage = async (event) => {
  const msg = event.data;
  try {
    switch (msg.type) {
      case 'init':
        await ensureInit();
        break;

      case 'build':
        await ensureInit();
        const shape = processCommands(msg.commands);
        const rawMesh = shape.mesh({ tolerance: 0.05, angularTolerance: 0.1 });
        // replicad's mesh() returns plain number[] arrays (no .buffer), but postMessage's
        // transfer list requires actual ArrayBuffers -- convert to typed arrays first.
        const mesh = {
          vertices: Float32Array.from(rawMesh.vertices),
          triangles: Uint32Array.from(rawMesh.triangles),
          normals: Float32Array.from(rawMesh.normals),
        };

        self.postMessage(
          {
            type: 'result',
            id: msg.id,
            vertices: mesh.vertices.buffer,
            triangles: mesh.triangles.buffer,
            normals: mesh.normals.buffer,
            vertexCount: mesh.vertices.length / 3,
            triangleCount: mesh.triangles.length / 3,
          },
          packedMeshData(mesh),
        );
        break;
    }
  } catch (err) {
    self.postMessage({ type: 'error', id: msg.id, message: err.message });
  }
};

ensureInit();
