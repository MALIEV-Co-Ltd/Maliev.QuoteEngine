(function () {
  const root = document.documentElement;
  const seenResources = new Set();
  const bootVersion = Date.now().toString(36);
  const staleBootRetryKey = "maliev.quote.boot.retry";
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

  function markRuntimeReady() {
    setProgress(100, true);
    setStatus(currentStrings.status.starting);
  }

  function markReady() {
    runtimeReady = true;
    setProgress(100, true);
    setStatus(currentStrings.status.ready);
    enableSkipStory();
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

  function prefersReducedMotion() {
    return Boolean(
      window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches);
  }

  function beginBoot(isWorkspaceHandoff) {
    const stage = document.getElementById("quote-startup");
    storyBeats = stage
      ? Array.prototype.slice.call(stage.querySelectorAll(".ms-beat"))
      : [];
    bootStartedAt = Date.now();

    // Tell the full story for every Studio boot while the WASM runtime loads.
    // Web → Studio handoff still affects the status copy, but direct Studio
    // visits should also see the narrative instead of a static finale.
    const wantsStory = true;
    storyMode = wantsStory && !prefersReducedMotion() ? "full" : "quiet";

    if (storyMode === "full") {
      minVisibleMs = STORY_BEAT_MS.reduce(function (sum, ms) { return sum + ms; }, 0) + FINALE_HOLD_MS;
      showBeat(0);
    } else {
      minVisibleMs = prefersReducedMotion() ? 0 : 500;
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

  function consumeWorkspaceHandoff() {
    try {
      const params = new URLSearchParams(window.location.search);
      if (params.has("handoff") || params.get("source") === "web") {
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
    setProgress(0, true);
    beginBoot(isWorkspaceHandoff);
    setStatus(isWorkspaceHandoff ? currentStrings.status.starting : currentStrings.status.preparing);

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

  window.quoteEngineLoader = {
    loadBootResource,
    markRuntimeReady,
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
    setTheme
  };
})();
