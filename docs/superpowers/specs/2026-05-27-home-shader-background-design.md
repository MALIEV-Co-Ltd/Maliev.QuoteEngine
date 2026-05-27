# Home Page Shader Background — Design Spec
_Date: 2026-05-27_

## Summary

Replace the static landing page hero (floating metal-components image + grid decoration) with a full-page animated GLSL shader background powered by the `shaders` npm package. Two stacked canvas layers sit beneath all page content. All home-page copy switches to light (white) text. The topbar gets a dark/translucent tint when on the home page.

---

## Goals

- Animated shader fills the entire `.home-host` area (below topbar)
- Two canvas layers: background shader + overlay canvas (overlay preset TBD by developer, architecture ready)
- Remove `.home-visual` section (floating image, grid decoration) entirely
- All home-page text switches to light colors — no frosted panel, no scrim
- Topbar darkens when on home page so logo/links stay legible against the dark shader
- `shaders` package integrated via esbuild build step — no CDN dependency

## Non-Goals

- Shader on any page other than `/`
- Dark-mode toggling of the shader (shader runs regardless of theme)
- Changing the rest of the app shell

---

## Architecture

### New files

| File | Purpose |
|------|---------|
| `Maliev.QuoteEngine.Client/package.json` | npm manifest: `shaders` dep, `esbuild` devDep |
| `Maliev.QuoteEngine.Client/js-src/home-shader.js` | Entry point; exports `init` / `destroy`; two preset constants |
| `Maliev.QuoteEngine.Client/wwwroot/js/home-shader.bundle.js` | esbuild IIFE output — gitignored, generated on `dotnet build` |

### Modified files

| File | Change |
|------|--------|
| `Maliev.QuoteEngine.Client.csproj` | Add `NpmInstall` + `BuildHomeShaderJs` MSBuild targets |
| `wwwroot/index.html` | Add `<script src="js/home-shader.bundle.js">` before Blazor bootstrap |
| `Pages/Home.razor` | Add two `<canvas>` elements; remove `.home-visual`; add JS interop + `IAsyncDisposable` |
| `Layout/MainLayout.razor` | Add `is-home` CSS class to `.app-shell` when `IsHomePage` is true |
| `wwwroot/css/app.css` | Canvas positioning; light-text treatment for home; dark topbar for `.is-home` |

---

## Build Integration

### `package.json`

```json
{
  "name": "maliev-quote-engine-client",
  "private": true,
  "scripts": {
    "build-js": "esbuild js-src/home-shader.js --bundle --format=iife --global-name=homeShader --outfile=wwwroot/js/home-shader.bundle.js"
  },
  "dependencies": {
    "shaders": "latest"
  },
  "devDependencies": {
    "esbuild": "latest"
  }
}
```

### MSBuild targets (in `.csproj`)

Two incremental targets:
- `NpmInstall` — runs `npm ci` only when `package.json` is newer than `node_modules/.package-lock.json`
- `BuildHomeShaderJs` — runs esbuild only when source or lock file is newer than the bundle output

```xml
<Target Name="NpmInstall"
        Inputs="$(MSBuildProjectDirectory)/package.json"
        Outputs="$(MSBuildProjectDirectory)/node_modules/.package-lock.json"
        BeforeTargets="BuildHomeShaderJs">
  <Exec Command="npm ci" WorkingDirectory="$(MSBuildProjectDirectory)" />
</Target>

<Target Name="BuildHomeShaderJs"
        Inputs="$(MSBuildProjectDirectory)/js-src/home-shader.js;$(MSBuildProjectDirectory)/node_modules/.package-lock.json"
        Outputs="$(MSBuildProjectDirectory)/wwwroot/js/home-shader.bundle.js"
        BeforeTargets="Build;Publish">
  <Exec Command="npm run build-js" WorkingDirectory="$(MSBuildProjectDirectory)" />
</Target>
```

---

## JS Module (`js-src/home-shader.js`)

```js
import { createPreview } from 'shaders/js';

const BG_PRESET_ID      = 'd67f08f2-25b0-4579-8733-1a85b4893d93';
const OVERLAY_PRESET_ID = null; // Set to a preset UUID when the overlay shader is ready

let _bg      = null;
let _overlay = null;

async function init() {
    const bgEl      = document.getElementById('home-shader-bg');
    const overlayEl = document.getElementById('home-shader-overlay');

    if (bgEl && BG_PRESET_ID) {
        try { _bg = await createPreview(bgEl, { presetId: BG_PRESET_ID }); }
        catch (e) { console.warn('[homeShader] background init failed', e); }
    }
    if (overlayEl && OVERLAY_PRESET_ID) {
        try { _overlay = await createPreview(overlayEl, { presetId: OVERLAY_PRESET_ID }); }
        catch (e) { console.warn('[homeShader] overlay init failed', e); }
    }
}

function destroy() {
    _bg?.destroy?.();
    _overlay?.destroy?.();
    _bg = null;
    _overlay = null;
}

export { init, destroy };
```

`--global-name=homeShader` in esbuild means `homeShader.init()` / `homeShader.destroy()` are available globally — same convention as `quoteEngineLoader.startBlazor()`.

---

## Home.razor Changes

- Implement `IAsyncDisposable`; inject `IJSRuntime`
- Remove `<div class="home-visual">…</div>` entirely
- Add before `.home-root`:
  ```html
  <canvas id="home-shader-bg"      class="home-shader-canvas"                     aria-hidden="true"></canvas>
  <canvas id="home-shader-overlay" class="home-shader-canvas home-shader-canvas--overlay" aria-hidden="true"></canvas>
  ```
- `OnAfterRenderAsync(firstRender)` → `await Js.InvokeVoidAsync("homeShader.init")`
- `DisposeAsync()` → `await Js.InvokeVoidAsync("homeShader.destroy")` (wrapped in `JSDisconnectedException` catch)

---

## MainLayout.razor Changes

Change `AppShellClass` to include `is-home` when `IsHomePage` is true:

```csharp
private string AppShellClass =>
    $"app-shell{(_chatDrawerOpen ? " chatbot-open" : "")}{(IsHomePage ? " is-home" : "")}";
```

---

## CSS Changes (`app.css`)

### Canvas layers
```css
/* home-shader-canvas — covers the full home-host area */
.home-shader-canvas {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    display: block;
    z-index: 0;
}

.home-shader-canvas--overlay {
    z-index: 1;
    mix-blend-mode: screen;
    opacity: 0.75;
    pointer-events: none;
}
```

### `.home-host` — add relative positioning, remove background
```css
.home-host {
    position: relative;
    overflow: hidden;
    /* remove: background: var(--maliev-bg) */
}
```

### `.home-root` — single-column, sits above canvases
```css
.home-root {
    position: relative;
    z-index: 2;
    /* change grid to flex column, center vertically */
    display: flex;
    flex-direction: column;
    justify-content: center;
    /* remove: grid-template-columns */
}
```

### Light-text treatment inside `.home-root`
```css
.home-root .home-h1                { color: #ffffff; }
.home-root .home-h1-accent         { color: #a78bfa; }  /* violet — visible against dark */
.home-root .home-sub               { color: rgba(255,255,255,0.65); }
.home-root .home-start-btn         { background: #ffffff; color: #171717; }
.home-root .home-start-btn:hover   { filter: brightness(0.93); }
.home-root .home-demo-btn          { background: rgba(255,255,255,0.08); color: #ffffff;
                                     box-shadow: 0 0 0 1px rgba(255,255,255,0.16); }
.home-root .home-demo-btn:hover    { background: rgba(255,255,255,0.14); }
.home-root .home-benefits          { border-top-color: rgba(255,255,255,0.14); }
.home-root .home-benefits li       { color: rgba(255,255,255,0.5); }
.home-root .home-benefits li.is-highlighted { color: #ffffff; }
.home-root .home-typewriter-cursor { background: #a78bfa; }
```

### Dark topbar on `.is-home`
```css
.app-shell.is-home .quote-topbar {
    background: rgba(5, 5, 14, 0.55);
    box-shadow: rgba(255,255,255,0.07) 0 1px 0 0;
}
.app-shell.is-home .quote-brand-logo              { filter: invert(1) brightness(1.08); }
.app-shell.is-home .quote-topnav a                { color: rgba(255,255,255,0.55); }
.app-shell.is-home .quote-topnav a:hover          { color: #ffffff; background: rgba(255,255,255,0.07); }
.app-shell.is-home .quote-topnav a.active         { color: #111111; }
.app-shell.is-home .quote-theme-toggle            { color: rgba(255,255,255,0.55); }
.app-shell.is-home .quote-theme-toggle:hover      { color: #ffffff; }
.app-shell.is-home .quote-language-button         { color: rgba(255,255,255,0.55); }
.app-shell.is-home .quote-language-button:hover   { color: #ffffff; }
.app-shell.is-home .quote-language-toggle         { background: rgba(255,255,255,0.10); }
.app-shell.is-home .quote-language-button.is-active { color: #111111; background: rgba(255,255,255,0.9); }
.app-shell.is-home .quote-signin-btn              { /* already colored — no change needed */ }
```

---

## Error Handling

- Both `createPreview` calls are wrapped in `try/catch` — failures log a warning but do not throw
- If the bundle fails to load or WebGL is unavailable, the page renders with its default background (transparent → dark body background set on `.home-host` as a fallback)
- Add `background: #08080f` to `.home-host` as a CSS fallback color so the page never shows plain white behind light text

---

## Gitignore

Add to `.gitignore` (or create if absent):
```
Maliev.QuoteEngine/Maliev.QuoteEngine.Client/node_modules/
Maliev.QuoteEngine/Maliev.QuoteEngine.Client/wwwroot/js/home-shader.bundle.js
```

---

## Testing

- `dotnet build` must succeed with zero errors and produce `home-shader.bundle.js`
- All existing `dotnet test` tests must pass (no regressions)
- No new automated tests required — this is a pure visual/build-integration feature
