(function () {
  'use strict';

  const pendingBuilds = new Map();
  let buildCounter = 0;
  const scenes = new Map();

  function getWorker() {
    return new Promise(function (resolve) {
      if (window.replicadWorker) { resolve(window.replicadWorker); return; }
      const check = function () {
        if (window.replicadWorker) { resolve(window.replicadWorker); return; }
        setTimeout(check, 100);
      };
      check();
    });
  }

  function buildDefaultLighting(scene) {
    var hemi = new BABYLON.HemisphericLight('hemi', new BABYLON.Vector3(0, 1, 0.5), scene);
    hemi.intensity = 0.6;
    var dir = new BABYLON.DirectionalLight('dir', new BABYLON.Vector3(-1, -2, -1), scene);
    dir.intensity = 0.8;
    dir.position = new BABYLON.Vector3(5, 10, 5);
  }

  function buildMaterial(scene) {
    var mat = new BABYLON.StandardMaterial('inlineMat', scene);
    mat.diffuseColor = new BABYLON.Color3(0.55, 0.55, 0.55);
    mat.specularColor = new BABYLON.Color3(0.2, 0.2, 0.2);
    mat.ambientColor = new BABYLON.Color3(0.1, 0.1, 0.1);
    mat.backFaceCulling = true;
    return mat;
  }

  function createScene(container, meshData) {
    var scene = new BABYLON.Scene();
    scene.clearColor = new BABYLON.Color4(0, 0, 0, 0);

    buildDefaultLighting(scene);

    var vertices = new Float32Array(meshData.vertices);
    var tris = new Uint32Array(meshData.triangles);
    var normals = new Float32Array(meshData.normals);
    var indexArray = [];
    for (var i = 0; i < tris.length; i++) {
      indexArray.push(tris[i]);
    }

    var vertexData = new BABYLON.VertexData();
    vertexData.positions = vertices;
    vertexData.indices = indexArray;
    vertexData.normals = normals;

    var mesh = new BABYLON.Mesh('preview', scene);
    vertexData.applyToMesh(mesh);
    mesh.material = buildMaterial(scene);
    mesh.isPickable = false;

    // Compute bounding box from vertices
    var minX = Infinity, minY = Infinity, minZ = Infinity;
    var maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
    for (var j = 0; j < vertices.length; j += 3) {
      var vx = vertices[j], vy = vertices[j + 1], vz = vertices[j + 2];
      if (vx < minX) minX = vx;
      if (vy < minY) minY = vy;
      if (vz < minZ) minZ = vz;
      if (vx > maxX) maxX = vx;
      if (vy > maxY) maxY = vy;
      if (vz > maxZ) maxZ = vz;
    }
    var centerX = (minX + maxX) / 2;
    var centerY = (minY + maxY) / 2;
    var centerZ = (minZ + maxZ) / 2;
    var center = new BABYLON.Vector3(centerX, centerY, centerZ);
    var diag = Math.sqrt(
      (maxX - minX) * (maxX - minX) +
      (maxY - minY) * (maxY - minY) +
      (maxZ - minZ) * (maxZ - minZ)
    );
    var radius = Math.max(diag * 1.5, 3);

    var canvas = document.createElement('canvas');
    canvas.style.width = '100%';
    canvas.style.height = '100%';
    canvas.style.display = 'block';
    canvas.style.borderRadius = '8px';
    container.innerHTML = '';
    container.appendChild(canvas);

    var camera = new BABYLON.ArcRotateCamera(
      'cam',
      -Math.PI / 4,
      Math.acos(1 / Math.sqrt(3)),
      radius,
      center,
      scene
    );
    camera.lowerRadiusLimit = radius * 0.2;
    camera.upperRadiusLimit = radius * 5;
    camera.panningSensibility = 50;
    camera.wheelPrecision = 50;
    camera.attachControl(canvas, false);

    var engine = new BABYLON.Engine(canvas, true, {
      premultipliedAlpha: false,
      alpha: true,
      disableUniformBuffers: true,
    });

    engine.runRenderLoop(function () {
      if (scene.activeCamera) {
        scene.render();
      }
    });

    var ro = new ResizeObserver(function () {
      engine.resize();
    });
    ro.observe(container);

    return {
      scene: scene,
      engine: engine,
      canvas: canvas,
      camera: camera,
      center: center,
      radius: radius,
      resizeObserver: ro,
    };
  }

  function disposePreview(containerId) {
    var entry = scenes.get(containerId);
    if (!entry) return;
    if (entry.resizeObserver) {
      entry.resizeObserver.disconnect();
    }
    if (entry.engine) {
      entry.engine.stopRenderLoop();
      entry.engine.dispose();
    }
    if (entry.scene) {
      entry.scene.dispose();
    }
    if (entry.canvas && entry.canvas.parentNode) {
      entry.canvas.parentNode.removeChild(entry.canvas);
    }
    scenes.delete(containerId);
  }

  window.quoteInlineViewer = {
    createPreview: async function (containerId, commandsJson) {
      var container = document.getElementById(containerId);
      if (!container) return;

      var commands;
      try {
        commands = JSON.parse(commandsJson);
      } catch (e) {
        container.innerHTML = '<p style="padding:1rem;color:#888">Could not parse 3D commands</p>';
        return;
      }

      if (!Array.isArray(commands) || commands.length === 0) {
        container.innerHTML = '<p style="padding:1rem;color:#888">No shapes to display</p>';
        return;
      }

      var worker;
      try {
        worker = await getWorker();
      } catch (e) {
        container.innerHTML = '<p style="padding:1rem;color:#888">3D engine not available</p>';
        return;
      }

      container.innerHTML = '<div class="qe-inline-viewer-loading">Building 3D model…</div>';

      var buildId = 'bl_' + (++buildCounter);
      var result = await new Promise(function (resolve, reject) {
        pendingBuilds.set(buildId, { resolve: resolve, reject: reject });

        var handler = function (e) {
          var data = e.data;
          if (data.type === 'result' && data.id === buildId) {
            window.replicadWorker.removeEventListener('message', handler);
            pendingBuilds.delete(buildId);
            resolve(data);
          } else if (data.type === 'error' && data.id === buildId) {
            window.replicadWorker.removeEventListener('message', handler);
            pendingBuilds.delete(buildId);
            reject(new Error(data.message));
          }
        };
        window.replicadWorker.addEventListener('message', handler);

        worker.postMessage({ type: 'build', id: buildId, commands: commands });
      });

      // Clean up any previous scene for this container
      disposePreview(containerId);

      var entry = createScene(container, result);
      scenes.set(containerId, entry);
    },

    disposePreview: disposePreview,

    resetPreviewCamera: function (containerId) {
      var entry = scenes.get(containerId);
      if (!entry || !entry.camera) return;
      entry.camera.setPosition(
        new BABYLON.Vector3(
          -entry.radius * 0.7,
          entry.radius * 0.7,
          entry.radius * 0.7
        )
      );
      entry.camera.setTarget(entry.center);
    },

    resizePreviews: function () {
      for (var iter = scenes.values(), entry = iter.next(); !entry.done; entry = iter.next()) {
        if (entry.value.engine) {
          entry.value.engine.resize();
        }
      }
    },
  };
})();
