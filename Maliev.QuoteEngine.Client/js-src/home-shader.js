/**
 * Metallic-fluid background shader — vanilla WebGL, no external dependencies.
 * Technique: domain-warped fractional Brownian motion (fBm) after Inigo Quilez.
 *
 * Public API (esbuild --global-name=homeShader → homeShader.*):
 *   init(isDark)   — attach to #home-shader-bg; isDark=true for dark mode
 *   setMode(isDark) — live-switch palette (smooth cross-fade)
 *   destroy()      — tear down all instances
 */

// ---------------------------------------------------------------------------
// GLSL source
// ---------------------------------------------------------------------------

const VERT = /* glsl */`
attribute vec2 a_pos;
void main() {
    gl_Position = vec4(a_pos, 0.0, 1.0);
}
`;

const FRAG = /* glsl */`
precision highp float;
uniform vec2  u_res;
uniform float u_time;
uniform float u_light;  /* 0 = dark mode, 1 = light mode — animates during theme switch */

vec2 hash2(vec2 p) {
    p = vec2(dot(p, vec2(127.1, 311.7)),
             dot(p, vec2(269.5, 183.3)));
    return -1.0 + 2.0 * fract(sin(p) * 43758.5453123);
}

float gnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = dot(hash2(i + vec2(0.0, 0.0)), f - vec2(0.0, 0.0));
    float b = dot(hash2(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(hash2(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
    float d = dot(hash2(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    vec2  sh  = vec2(100.0);
    mat2  rot = mat2(cos(0.5), sin(0.5), -sin(0.5), cos(0.5));
    for (int i = 0; i < 6; i++) {
        v += a * gnoise(p);
        p  = rot * p * 2.0 + sh;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_res;
    uv.x   *= u_res.x / u_res.y;

    float t = u_time * 0.06;

    /* Two-layer domain warp (Quilez "Warping" technique) */
    vec2 q = vec2(fbm(uv + t),
                  fbm(uv + vec2(1.0)));

    vec2 r = vec2(fbm(uv + 4.0 * q + vec2(1.7,  9.2) + 0.150 * t),
                  fbm(uv + 4.0 * q + vec2(8.3,  2.8) + 0.126 * t));

    float f = fbm(uv + 4.0 * r);
    float n = clamp(f * 0.5 + 0.5, 0.0, 1.0);

    /* ── Dark palette: near-black → deep navy → steel → silver → chrome ── */
    vec3 dk = mix(vec3(0.006, 0.008, 0.030),   /* near-black floor          */
                  vec3(0.045, 0.065, 0.180),    /* deep navy                 */
                  smoothstep(0.00, 0.32, n));
    dk = mix(dk, vec3(0.13,  0.21,  0.46),     /* steel blue                */
                  smoothstep(0.25, 0.60, n));
    dk = mix(dk, vec3(0.50,  0.60,  0.73),     /* silver                    */
                  smoothstep(0.52, 0.82, n));
    dk = mix(dk, vec3(0.83,  0.88,  0.94),     /* chrome specular tip       */
                  pow(smoothstep(0.78, 1.00, n), 1.8));

    /* ── Light palette: pale ice → light steel → mid blue → deep indigo ── */
    float m = 1.0 - n;                          /* invert: swirls are darker */
    vec3 lt = mix(vec3(0.92,  0.94,  0.98),    /* pale ice background       */
                  vec3(0.74,  0.81,  0.91),    /* light steel               */
                  smoothstep(0.00, 0.32, m));
    lt = mix(lt, vec3(0.49,  0.62,  0.80),     /* steel blue                */
                  smoothstep(0.25, 0.60, m));
    lt = mix(lt, vec3(0.24,  0.38,  0.65),     /* medium indigo             */
                  smoothstep(0.52, 0.82, m));
    lt = mix(lt, vec3(0.08,  0.16,  0.38),     /* deep navy accent          */
                  pow(smoothstep(0.78, 1.00, m), 1.8));

    vec3 col = mix(dk, lt, u_light);
    gl_FragColor = vec4(col, 1.0);
}
`;

// ---------------------------------------------------------------------------
// WebGL helpers
// ---------------------------------------------------------------------------

function compileShader(gl, type, src) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, src);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        const log = gl.getShaderInfoLog(shader);
        gl.deleteShader(shader);
        throw new Error('[homeShader] compile error: ' + log);
    }
    return shader;
}

function buildProgram(gl) {
    const vert = compileShader(gl, gl.VERTEX_SHADER,   VERT);
    const frag = compileShader(gl, gl.FRAGMENT_SHADER, FRAG);
    const prog = gl.createProgram();
    gl.attachShader(prog, vert);
    gl.attachShader(prog, frag);
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
        throw new Error('[homeShader] link error: ' + gl.getProgramInfoLog(prog));
    }
    gl.deleteShader(vert);
    gl.deleteShader(frag);
    return prog;
}

function uploadQuad(gl, prog) {
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER,
        new Float32Array([-1, -1,  1, -1,  -1, 1,  1, 1]),
        gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, 'a_pos');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    return buf;
}

// ---------------------------------------------------------------------------
// Per-canvas instance
// ---------------------------------------------------------------------------

function createInstance(canvas, isDark) {
    const dpr     = window.devicePixelRatio || 1;
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    const gl = canvas.getContext('webgl',              { alpha: false, antialias: false, powerPreference: 'low-power' })
            || canvas.getContext('experimental-webgl', { alpha: false, antialias: false });
    if (!gl) {
        console.warn('[homeShader] WebGL unavailable on', canvas.id);
        return null;
    }

    // u_light: 0 = dark mode, 1 = light mode.  Animated during theme switches.
    let currentLight = isDark ? 0.0 : 1.0;
    let targetLight  = currentLight;

    let prog, uRes, uTime, uLight, quadBuf;
    let rafId     = null;
    let paused    = false;
    let destroyed = false;
    let elapsed   = 0;       // accumulated seconds excluding paused time
    let wallStart = performance.now();

    function compile() {
        prog   = buildProgram(gl);
        uRes   = gl.getUniformLocation(prog, 'u_res');
        uTime  = gl.getUniformLocation(prog, 'u_time');
        uLight = gl.getUniformLocation(prog, 'u_light');
        quadBuf = uploadQuad(gl, prog);
        gl.useProgram(prog);
    }

    function syncSize() {
        const rect = canvas.getBoundingClientRect();
        const w = Math.round(rect.width  * dpr);
        const h = Math.round(rect.height * dpr);
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width  = w;
            canvas.height = h;
            gl.viewport(0, 0, w, h);
        }
    }

    function drawFrame(now) {
        // Animate light/dark transition (~600 ms cross-fade)
        if (currentLight !== targetLight) {
            const step = 0.036;  // ~600 ms at 60 fps
            currentLight = Math.abs(targetLight - currentLight) < step
                ? targetLight
                : currentLight + (targetLight > currentLight ? step : -step);
        }

        syncSize();
        const t = elapsed + (now - wallStart) * 0.001;
        gl.uniform2f(uRes,  canvas.width, canvas.height);
        gl.uniform1f(uTime, t);
        gl.uniform1f(uLight, currentLight);
        gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    }

    function tick(now) {
        if (destroyed || paused) return;
        drawFrame(now);
        rafId = requestAnimationFrame(tick);
    }

    function pause() {
        if (paused || destroyed) return;
        paused   = true;
        elapsed += (performance.now() - wallStart) * 0.001;
        if (rafId !== null) { cancelAnimationFrame(rafId); rafId = null; }
    }

    function resume() {
        if (!paused || destroyed) return;
        paused    = false;
        wallStart = performance.now();
        rafId     = requestAnimationFrame(tick);
    }

    // WebGL context loss / restore
    canvas.addEventListener('webglcontextlost', (e) => {
        e.preventDefault();
        if (rafId !== null) { cancelAnimationFrame(rafId); rafId = null; }
    });
    canvas.addEventListener('webglcontextrestored', () => {
        compile();
        syncSize();
        wallStart = performance.now();
        rafId = requestAnimationFrame(tick);
    });

    // Tab visibility — pause to save battery
    const onVis = () => (document.hidden ? pause : resume)();
    document.addEventListener('visibilitychange', onVis);

    // DPR-aware resize
    const ro = new ResizeObserver(() => { if (!paused && !destroyed) syncSize(); });
    ro.observe(canvas);

    // Boot
    try {
        compile();
        syncSize();
    } catch (err) {
        console.warn('[homeShader] init failed:', err);
        return null;
    }

    if (reduced) {
        drawFrame(performance.now());
    } else {
        wallStart = performance.now();
        rafId = requestAnimationFrame(tick);
    }

    return {
        setDarkMode(dark) {
            targetLight = dark ? 0.0 : 1.0;
            // Kick the RAF loop if it was somehow paused (e.g. reduced motion)
            if (reduced && !paused && !destroyed) {
                drawFrame(performance.now());
            }
        },
        destroy() {
            destroyed = true;
            if (rafId !== null) { cancelAnimationFrame(rafId); rafId = null; }
            ro.disconnect();
            document.removeEventListener('visibilitychange', onVis);
            if (prog)    gl.deleteProgram(prog);
            if (quadBuf) gl.deleteBuffer(quadBuf);
        }
    };
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

let _bg      = null;
let _overlay = null;

/**
 * Attach shader to #home-shader-bg.
 * @param {boolean} isDark  true = dark mode palette, false = light mode palette
 */
function init(isDark) {
    if (_bg) return;
    const dark = (isDark !== false);   // default to dark if called with no arg
    const bgEl = document.getElementById('home-shader-bg');
    if (bgEl) _bg = createInstance(bgEl, dark);
    // #home-shader-overlay is reserved for a second preset — activate when needed.
}

/**
 * Live-switch the colour palette with a smooth cross-fade.
 * @param {boolean} isDark  true = dark mode, false = light mode
 */
function setMode(isDark) {
    const dark = (isDark !== false);
    _bg?.setDarkMode(dark);
    _overlay?.setDarkMode(dark);
}

/** Free GPU resources. */
function destroy() {
    _bg?.destroy();
    _overlay?.destroy();
    _bg      = null;
    _overlay = null;
}

export { init, setMode, destroy };
