(function () {
  const root = document.documentElement;
  const seenResources = new Set();
  let loadedResources = 0;
  let totalResources = 0;
  let displayedProgress = 0;

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
    const progressText = `${displayedProgress}%`;
    const roundedProgressText = `${roundedProgress}%`;

    root.style.setProperty("--blazor-load-percentage", `${progress}%`);
    root.style.setProperty("--blazor-load-percentage-text", `"${roundedProgressText}"`);
    root.style.setProperty("--wasm-logo-progress", progressText);
    root.style.setProperty("--wasm-loader-progress", progressText);

    document.querySelectorAll(".maliev-logo-loader").forEach(function (loader) {
      loader.style.setProperty("--wasm-logo-progress", progressText);
    });

    const progressBar = document.querySelector(".startup-progress");
    if (progressBar) {
      progressBar.setAttribute("aria-valuenow", roundedProgress.toString());
    }

    const percent = document.getElementById("startup-percent");
    if (percent) {
      percent.textContent = roundedProgressText;
    }
  }

  function updateResourceProgress() {
    const estimatedTotal = Math.max(totalResources + 3, loadedResources + 1);
    setProgress(Math.min((loadedResources / estimatedTotal) * 100, 95));
  }

  function loadBootResource(type, name, defaultUri, integrity) {
    if (type === "dotnetjs") {
      return null;
    }

    const key = `${type}:${name}:${defaultUri}`;
    if (!seenResources.has(key)) {
      seenResources.add(key);
      totalResources += 1;
      setStatus("Loading quote engine resources");
      updateResourceProgress();
    }

    const requestInit = integrity ? { integrity } : undefined;
    return fetch(defaultUri, requestInit).then(
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
    setStatus("Starting quote workspace");
  }

  function markReady() {
    markRuntimeReady();
    setStatus("Quote engine ready");
    document.body.classList.add("quote-ready");
  }

  function markFailed(error) {
    console.error("MALIEV Quote Engine startup failed", error);
    setStatus("Quote engine failed to start");
    document.body.classList.add("quote-loading-failed");
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

  function setTheme(theme) {
    const normalizedTheme = theme === "dark" ? "dark" : "light";
    root.setAttribute("data-maliev-theme", normalizedTheme);
    root.style.colorScheme = normalizedTheme;
    setPreference("maliev.quote.theme", normalizedTheme);
  }

  function startBlazor() {
    setProgress(0, true);
    setStatus("Preparing quote engine");

    if (!window.Blazor || typeof window.Blazor.start !== "function") {
      markFailed(new Error("Blazor startup script is not available."));
      return Promise.resolve();
    }

    return window.Blazor.start({
      loadBootResource: window.quoteEngineLoader.loadBootResource
    }).then(function () {
      window.quoteEngineLoader.markRuntimeReady();
    }).catch(function (error) {
      window.quoteEngineLoader.markFailed(error);
    });
  }

  setProgress(0, true);
  setTheme(getPreference("maliev.quote.theme") || "light");

  window.quoteEngineLoader = {
    loadBootResource,
    markRuntimeReady,
    markReady,
    markFailed,
    setProgress,
    startBlazor
  };

  window.quoteEnginePreferences = {
    getPreference,
    setPreference,
    setTheme
  };
})();
