(function () {
  const root = document.documentElement;
  const seenResources = new Set();
  const bootVersion = Date.now().toString(36);
  const staleBootRetryKey = "maliev.quote.boot.retry";
  const firstWasmLoadKey = "maliev.makestudio.first-wasm-loaded";
  const motionPreferenceKey = "maliev.quote.motion-reduced";
  let loadedResources = 0;
  let totalResources = 0;
  let displayedProgress = 0;

  // ── Localized copy (runs before Blazor, so it can't come from .resx) ──
  const STRINGS = {
    "en-US": {
      beats: [
        "For years, getting a custom part made meant one thing.",
        "Upload files. Send emails. Wait days for a quote.",
        "That ends here.",
        "Describe what you want to make. Make Studio quotes it, reviews it, and orders it in minutes."
      ],
      tagline: "Agentic manufacturing",
      status: {
        preparing: "Preparing Make Studio",
        loading: "Loading Make Studio",
        loadingEngine: "Loading 3D engine",
        starting: "Starting your studio",
        ready: "Make Studio ready",
        failed: "Make Studio failed to start"
      }
    },
    "th-TH": {
      beats: [
        "หลายปีที่ผ่านมา การสั่งทำชิ้นงานมีอยู่ทางเดียว",
        "อัปโหลดไฟล์ ส่งอีเมลไปมา รอใบเสนอราคาหลายวัน",
        "ยุคนั้นจบลงแล้ว",
        "แค่บอกว่าคุณอยากผลิตอะไร แล้ว Make Studio จะเสนอราคา ตรวจ DFM และสั่งผลิตให้ ภายในไม่กี่นาที"
      ],
      tagline: "การผลิตยุคเอเจนต์",
      status: {
        preparing: "กำลังเตรียม Make Studio",
        loading: "กำลังโหลด Make Studio",
        loadingEngine: "กำลังโหลด 3D engine",
        starting: "กำลังเปิดสตูดิโอของคุณ",
        ready: "Make Studio พร้อมแล้ว",
        failed: "เริ่ม Make Studio ไม่สำเร็จ"
      }
    }
  };
  let currentStrings = STRINGS["en-US"];

  // ── Make Studio story / dismissal state ───────────────────────────────
  const STORY_BEAT_MS = [3200, 4200, 2600, 5200];
  const FINALE_HOLD_MS = 3000;
  let storyBeats = [];
  let storyMode = "full"; // "full" plays the narrative; "quiet" shows the finale only
  let bootTick = null;
  let bootStartedAt = 0;
  let minVisibleMs = FINALE_HOLD_MS;
  let runtimeReady = false;
  let canSkipStory = false;
  let firstWasmStoryPending = false;

  function clamp(value) {
    return Math.max(0, Math.min(100, value));
  }

  function setStatus(text) {
    const status = document.getElementById("startup-status");
    if (status) {
      status.textContent = text;
    }
  }

  function setProgress(value, allowDecrease) {
    const progress = clamp(Number.isFinite(value) ? value : 0);
    displayedProgress = allowDecrease
      ? progress
      : Math.max(displayedProgress, progress);

    const roundedProgress = Math.floor(displayedProgress);

    root.style.setProperty("--ms-progress", `${displayedProgress}%`);

    const progressBar = document.querySelector(".ms-progress");
    if (progressBar) {
      progressBar.setAttribute("aria-valuenow", roundedProgress.toString());
    }

    const percent = document.getElementById("startup-percent");
    if (percent) {
      percent.textContent = `${roundedProgress}%`;
    }
  }

  function updateResourceProgress() {
    const estimatedTotal = Math.max(totalResources + 3, loadedResources + 1);
    setProgress(Math.min((loadedResources / estimatedTotal) * 100, 95));
  }

  function resolveStaticAsset(defaultUri) {
    const map = window.malievStaticAssetMap || {};
    return map[defaultUri] || defaultUri;
  }

  function withBootCacheBust(uri) {
    if (!uri || !uri.startsWith("_framework/")) {
      return uri;
    }

    const separator = uri.includes("?") ? "&" : "?";
    return `${uri}${separator}v=${bootVersion}`;
  }

  function loadBootResource(type, name, defaultUri, integrity) {
    if (type === "dotnetjs") {
      return withBootCacheBust(resolveStaticAsset(defaultUri));
    }

    const key = `${type}:${name}:${defaultUri}`;
    if (!seenResources.has(key)) {
      seenResources.add(key);
      totalResources += 1;
      setStatus(currentStrings.status.loading);
      updateResourceProgress();
    }

    const requestInit = {
      cache: "no-store",
      ...(integrity ? { integrity } : {})
    };

    return fetch(withBootCacheBust(defaultUri), requestInit).then(
      function (response) {
        loadedResources += 1;
        updateResourceProgress();
        return response;
      },
      function (error) {
        markFailed(error);
        throw error;
      });
  }

  let replicadReady = false;
  let blazorReady = false;
  let loadingProgress = 0;

  function createInlineViewerBridge() {
    const timeoutMs = 12000;
    let runtime = null;
    const pending = [];

    function flushPending() {
      while (pending.length > 0) {
        const item = pending.shift();
        invoke(item.methodName, item.args).then(item.resolve, item.reject);
      }
    }

    function invoke(methodName, args) {
      if (runtime && typeof runtime[methodName] === "function") {
        return Promise.resolve(runtime[methodName].apply(runtime, args));
      }

      return new Promise(function (resolve, reject) {
        const timeout = window.setTimeout(function () {
          const index = pending.findIndex(function (item) { return item.resolve === resolve; });
          if (index >= 0) {
            pending.splice(index, 1);
          }
          reject(new Error("3D preview runtime is still loading."));
        }, timeoutMs);

        pending.push({
          methodName,
          args,
          resolve: function (value) {
            window.clearTimeout(timeout);
            resolve(value);
          },
          reject: function (error) {
            window.clearTimeout(timeout);
            reject(error);
          }
        });
      });
    }

    return {
      registerRuntime: function (nextRuntime) {
        runtime = nextRuntime || null;
        flushPending();
      },
      createPreview: function () {
        return invoke("createPreview", Array.prototype.slice.call(arguments));
      },
      disposePreview: function () {
        return invoke("disposePreview", Array.prototype.slice.call(arguments));
      },
      resetPreviewCamera: function () {
        return invoke("resetPreviewCamera", Array.prototype.slice.call(arguments));
      },
      resizePreviews: function () {
        return invoke("resizePreviews", Array.prototype.slice.call(arguments));
      }
    };
  }

  window.quoteInlineViewer = window.quoteInlineViewer || createInlineViewerBridge();

  function updateLoadingProgress() {
    const blazorProgress = blazorReady ? 50 : (loadedResources / Math.max(totalResources + 2, 1)) * 50;
    const replicadProgress = replicadReady ? 50 : 0;
    loadingProgress = Math.max(loadingProgress, blazorProgress + replicadProgress);
    setProgress(loadingProgress);
  }

  function markRuntimeReady() {
    blazorReady = true;
    setStatus(currentStrings.status.starting);
    updateLoadingProgress();
    checkAllReady();
  }

  function markReplicadReady() {
    replicadReady = true;
    updateLoadingProgress();
    checkAllReady();
  }

  function checkAllReady() {
    if (blazorReady && replicadReady) {
      runtimeReady = true;
      if (firstWasmStoryPending) {
        rememberFirstWasmLoaded();
        firstWasmStoryPending = false;
      }
      setProgress(100, true);
      setStatus(currentStrings.status.ready);
      enableSkipStory();
      maybeFinish();
    }
  }

  function startLoadingReplicad() {
    setStatus(currentStrings.status.loadingEngine);
    updateResourceProgress();
    try {
      const worker = new Worker('js/replicad-worker.bundle.js');
      worker.onmessage = function (e) {
        if (e.data.type === 'ready') {
          window.replicadWorker = worker;
          window.quoteEngineLoader.markReplicadReady();
        }
      };
      worker.onerror = function () {
        window.quoteEngineLoader.markReplicadReady();
      };
    } catch (e) {
      window.quoteEngineLoader.markReplicadReady();
    }
  }

  function markReady() {
    maybeFinish();
  }

  function markFailed(error) {
    console.error("MALIEV Make Studio startup failed", error);
    setStatus(currentStrings.status.failed);
    document.body.classList.add("quote-loading-failed");
    stopBootTick();
  }

  function isStaleBootError(error) {
    const message = String(error && (error.message || error) || "");
    return message.includes("Failed to fetch dynamically imported module")
      || message.includes("/_framework/dotnet.")
      || message.includes("_framework/dotnet.");
  }

  function recoverFromStaleBoot(error) {
    if (!isStaleBootError(error)) {
      return false;
    }

    try {
      if (window.sessionStorage.getItem(staleBootRetryKey) === "1") {
        return false;
      }

      window.sessionStorage.setItem(staleBootRetryKey, "1");
    } catch {
      // If sessionStorage is unavailable, do one reload anyway.
    }

    const url = new URL(window.location.href);
    url.searchParams.set("msboot", Date.now().toString());
    window.location.replace(url.toString());
    return true;
  }

  // ── story timeline ────────────────────────────────────────────────────
  function stopBootTick() {
    if (bootTick) {
      window.clearInterval(bootTick);
      bootTick = null;
    }
  }

  function applyStoryStrings() {
    const stage = document.getElementById("quote-startup");
    if (!stage) {
      return;
    }
    const setText = function (selector, text) {
      const el = stage.querySelector(selector);
      if (el && text) {
        el.textContent = text;
      }
    };
    setText('[data-ms-beat="0"]', currentStrings.beats[0]);
    setText('[data-ms-beat="1"] .ms-kill', currentStrings.beats[1]);
    setText('[data-ms-beat="2"]', currentStrings.beats[2]);
    setText('[data-ms-beat="3"]', currentStrings.beats[3]);
    setText(".ms-tagline", currentStrings.tagline);
  }

  function showBeat(index) {
    storyBeats.forEach(function (beat, i) {
      beat.classList.toggle("ms-beat--on", i === index);
    });
  }

  function activeBeatIndex(elapsed) {
    let accumulated = 0;
    for (let i = 0; i < STORY_BEAT_MS.length; i++) {
      if (elapsed < accumulated + STORY_BEAT_MS[i]) {
        return i;
      }
      accumulated += STORY_BEAT_MS[i];
    }
    return Math.max(0, storyBeats.length - 1); // finale, held until dismissal
  }

  function maybeFinish() {
    if (!runtimeReady) {
      return;
    }
    if (Date.now() - bootStartedAt >= minVisibleMs) {
      finishStartupStory();
    }
  }

  function finishStartupStory() {
    stopBootTick();
    document.body.classList.add("quote-ready");
  }

  function enableSkipStory() {
    canSkipStory = true;
    document.body.classList.add("quote-skip-ready");

    const skip = document.getElementById("startup-skip");
    if (skip) {
      skip.hidden = false;
      skip.disabled = false;
      skip.setAttribute("aria-disabled", "false");
    }
  }

  function skipStory() {
    if (!canSkipStory) {
      return;
    }

    finishStartupStory();
  }

  function resolveMotion(fallback) {
    const stored = getPreference(motionPreferenceKey);
    if (stored === "1" || stored === "true") {
      return true;
    }

    if (stored === "0" || stored === "false") {
      return false;
    }

    return Boolean(fallback);
  }

  function setMotion(isReduced) {
    const reduced = Boolean(isReduced);
    root.setAttribute("data-motion-reduced", reduced ? "true" : "false");
    setPreference(motionPreferenceKey, reduced ? "true" : "false");
  }

  function beginBoot(wantsStory) {
    const stage = document.getElementById("quote-startup");
    storyBeats = stage
      ? Array.prototype.slice.call(stage.querySelectorAll(".ms-beat"))
      : [];
    bootStartedAt = Date.now();

    // Play the full narrative only for Maliev.Web handoffs or the first
    // browser boot. Direct return visits should reach the Studio quickly.
    if (!wantsStory) {
      finishStartupStory();
      return;
    }

    const isMotionReduced = resolveMotion(false);
    storyMode = !isMotionReduced ? "full" : "quiet";

    if (storyMode === "full") {
      minVisibleMs = STORY_BEAT_MS.reduce(function (sum, ms) { return sum + ms; }, 0) + FINALE_HOLD_MS;
      showBeat(0);
    } else {
      minVisibleMs = isMotionReduced ? 0 : 500;
      showBeat(storyBeats.length - 1);
    }

    stopBootTick();
    bootTick = window.setInterval(function () {
      if (storyMode === "full") {
        showBeat(activeBeatIndex(Date.now() - bootStartedAt));
      }
      maybeFinish();
    }, 90);
  }

  function getPreference(key) {
    try {
      return window.localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  function setPreference(key, value) {
    try {
      window.localStorage.setItem(key, value);
    } catch {
      // localStorage can be unavailable in strict privacy modes.
    }
  }

  function getCookie(name) {
    const prefix = `${name}=`;
    const parts = document.cookie ? document.cookie.split(";") : [];
    for (const part of parts) {
      const trimmed = part.trim();
      if (trimmed.startsWith(prefix)) {
        return decodeURIComponent(trimmed.substring(prefix.length));
      }
    }

    return null;
  }

  function setCookie(name, value, maxAgeSeconds) {
    const secure = window.location.protocol === "https:" ? "; Secure" : "";
    document.cookie = `${name}=${encodeURIComponent(value)}; Path=/; Max-Age=${maxAgeSeconds}; SameSite=Lax${secure}`;
  }

  function consumeQueryWorkspaceHandoff(params) {
    if (!params.has("handoff") && params.get("source") !== "web") {
      return false;
    }

    try {
      const url = new URL(window.location.href);
      url.searchParams.delete("handoff");
      url.searchParams.delete("source");
      window.history.replaceState(window.history.state, document.title, url.toString());
    } catch {
      // History may be unavailable in constrained browser contexts. The handoff
      // still counts for this page load; future loads will fall back to storage.
    }

    return true;
  }

  function consumeWorkspaceHandoff() {
    try {
      const params = new URLSearchParams(window.location.search);
      if (consumeQueryWorkspaceHandoff(params)) {
        return true;
      }
    } catch {
      // URLSearchParams can fail in very old or constrained browser contexts.
    }

    try {
      const handoff = window.sessionStorage.getItem("maliev.quote.workspace.handoff");
      if (handoff) {
        window.sessionStorage.removeItem("maliev.quote.workspace.handoff");
        return true;
      }
    } catch {
      // sessionStorage can be unavailable in strict privacy modes.
    }

    return false;
  }

  function shouldPlayFirstWasmStory() {
    try {
      return window.localStorage.getItem(firstWasmLoadKey) !== "1";
    } catch {
      // If localStorage is blocked, treat this boot as first-load so the
      // customer still sees the narrative while the runtime starts.
      return true;
    }
  }

  function rememberFirstWasmLoaded() {
    try {
      window.localStorage.setItem(firstWasmLoadKey, "1");
    } catch {
      // localStorage can be unavailable in strict privacy modes.
    }
  }

  function getQueryCulture() {
    try {
      return new URLSearchParams(window.location.search).get("culture");
    } catch {
      return null;
    }
  }

  function normalizeCulture(culture) {
    return culture && culture.toLowerCase().startsWith("th") ? "th-TH" : "en-US";
  }

  function applyDocumentCulture(culture) {
    const normalizedCulture = normalizeCulture(culture);
    root.lang = normalizedCulture === "th-TH" ? "th" : "en";
    root.setAttribute("data-culture", normalizedCulture);
    return normalizedCulture;
  }

  function resolveCulture(fallback) {
    // Query string wins — it is how Maliev.Web hands the chosen language across
    // the subdomain hop, where its host-scoped cookie/localStorage can't reach.
    const storedCulture =
      getQueryCulture() ||
      getPreference("maliev.quote.culture") ||
      getPreference("maliev.culture") ||
      getCookie("maliev.culture") ||
      (navigator.languages && navigator.languages.length > 0 ? navigator.languages[0] : navigator.language) ||
      fallback;

    return applyDocumentCulture(storedCulture);
  }

  function setCulture(culture) {
    const normalizedCulture = applyDocumentCulture(culture);
    setPreference("maliev.quote.culture", normalizedCulture);
    setPreference("maliev.culture", normalizedCulture);
    setCookie("maliev.culture", normalizedCulture, 60 * 60 * 24 * 365);
    currentStrings = STRINGS[normalizedCulture] || STRINGS["en-US"];
    applyStoryStrings();
    return normalizedCulture;
  }

  function resolveTheme(fallback) {
    return getPreference("maliev.quote.theme") || getPreference("maliev.theme") || fallback || "light";
  }

  function setTheme(theme) {
    const normalizedTheme = theme === "dark" ? "dark" : "light";
    root.setAttribute("data-maliev-theme", normalizedTheme);
    root.style.colorScheme = normalizedTheme;
    setPreference("maliev.quote.theme", normalizedTheme);
    setPreference("maliev.theme", normalizedTheme);
  }

  function startBlazor() {
    const isWorkspaceHandoff = consumeWorkspaceHandoff();
    const isFirstWasmLoad = shouldPlayFirstWasmStory();
    firstWasmStoryPending = isFirstWasmLoad;
    setProgress(0, true);
    beginBoot(isWorkspaceHandoff || isFirstWasmLoad);
    setStatus(isWorkspaceHandoff ? currentStrings.status.starting : currentStrings.status.preparing);

    startLoadingReplicad();

    if (!window.Blazor || typeof window.Blazor.start !== "function") {
      markFailed(new Error("Blazor startup script is not available."));
      return Promise.resolve();
    }

    return window.Blazor.start({
      loadBootResource: window.quoteEngineLoader.loadBootResource
    }).then(function () {
      try {
        window.sessionStorage.removeItem(staleBootRetryKey);
      } catch {
        // sessionStorage can be unavailable in strict privacy modes.
      }
      window.quoteEngineLoader.markRuntimeReady();
    }).catch(function (error) {
      if (recoverFromStaleBoot(error)) {
        return;
      }

      window.quoteEngineLoader.markFailed(error);
    });
  }

  setProgress(0, true);
  setCulture(resolveCulture("en-US"));
  setStatus(currentStrings.status.preparing);
  setTheme(resolveTheme("light"));
  setMotion(resolveMotion(false));

  window.quoteEngineLoader = {
    loadBootResource,
    markRuntimeReady,
    markReplicadReady,
    markReady,
    markFailed,
    setProgress,
    startBlazor,
    skipStory
  };

  window.quoteEnginePreferences = {
    getPreference,
    setPreference,
    resolveCulture,
    setCulture,
    resolveTheme,
    setTheme,
    resolveMotion,
    setMotion
  };
})();
