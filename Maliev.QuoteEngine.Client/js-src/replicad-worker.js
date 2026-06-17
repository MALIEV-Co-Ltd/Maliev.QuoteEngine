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
    const s = shapes.get(ref);
    if (!s) throw new Error(`Shape not found: ${ref}`);
    return s;
  }
  return ref;
}

function buildProfile(profile) {
  const plane = profile.plane || 'XY';
  const sketch = new Sketcher(plane);
  for (const seg of profile.segments || []) {
    const p = seg.params || [];
    switch (seg.type) {
      case 'move':
        sketch.movePointerTo(p);
        break;
      case 'line':
        sketch.lineTo(p[0], p[1]);
        break;
      case 'hLine':
        sketch.hLine(p[0]);
        break;
      case 'vLine':
        sketch.vLine(p[0]);
        break;
      case 'arc':
        sketch.threePointsArc(p[0], p[1], p[2], p[3]);
        break;
      case 'bezier':
        sketch.quadraticBezierCurveTo([p[0], p[1]], [p[2], p[3]]);
        break;
    }
  }
  if (profile.close !== false) {
    return sketch.close();
  }
  return sketch;
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
        shape = replicadMakeBox([-p[0] / 2, -p[1] / 2, 0], [p[0] / 2, p[1] / 2, p[2]]);
        break;
      }
      case 'cylinder': {
        shape = makeCylinder(p[0], p[1]);
        break;
      }
      case 'sphere': {
        shape = makeSphere(p[0]);
        break;
      }
      case 'cone': {
        shape = makeCone(p[0], p[1] || 0, p[2]);
        break;
      }
      case 'extrude': {
        const profile = cmd.profile;
        let face;
        if (profile.type === 'circle') {
          face = drawCircle(profile.radius || p[1] || 10).sketchOnPlane('XY');
        } else if (profile.type === 'rect') {
          face = drawRectangle(profile.width || p[1], profile.height || p[2]).sketchOnPlane('XY');
        } else {
          face = buildProfile(profile);
        }
        shape = face.extrude(p[0]);
        break;
      }
      case 'revolve': {
        const profile = cmd.profile;
        let face;
        if (profile.type === 'circle') {
          face = drawCircle(profile.radius || p[1] || 10).sketchOnPlane('XY');
        } else if (profile.type === 'rect') {
          face = drawRectangle(profile.width || p[1], profile.height || p[2]).sketchOnPlane('XY');
        } else {
          face = buildProfile(profile);
        }
        shape = face.revolve(cmd.axis || [0, 0, 1], cmd.angle);
        break;
      }
      case 'fuse': {
        shape = resolve(cmd.targetId).fuse(resolve(cmd.toolId));
        break;
      }
      case 'cut': {
        shape = resolve(cmd.targetId).cut(resolve(cmd.toolId));
        break;
      }
      case 'intersect': {
        shape = resolve(cmd.targetId).intersect(resolve(cmd.toolId));
        break;
      }
      case 'fillet': {
        shape = resolve(cmd.targetId).fillet(cmd.radius || p[0]);
        break;
      }
      case 'chamfer': {
        shape = resolve(cmd.targetId).chamfer(cmd.radius || p[0]);
        break;
      }
      case 'loft': {
        const a = resolve(cmd.targetId);
        const b = resolve(cmd.toolId);
        shape = a.loftWith(b);
        break;
      }
      case 'translate': {
        shape = resolve(cmd.targetId);
        if (offset) shape = shape.translate(offset[0], offset[1], offset[2]);
        else if (p.length >= 3) shape = shape.translate(p[0], p[1], p[2]);
        break;
      }
      case 'rotate': {
        shape = resolve(cmd.targetId).rotate(cmd.axis || [0, 0, 1], cmd.angle || p[0] || 0);
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
        const mesh = shape.mesh({ tolerance: 0.05, angularTolerance: 0.1 });

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
