(() => {
  const modalId = "components-reconnect-modal";
  const activeClasses = [
    "components-reconnect-show",
    "components-reconnect-retrying",
    "components-reconnect-failed",
    "components-reconnect-rejected"
  ];
  let startedAt = null;
  let intervalId = null;

  const formatElapsed = elapsedMs => {
    const totalSeconds = Math.max(0, Math.floor(elapsedMs / 1000));
    if (totalSeconds < 60) {
      return `${totalSeconds}s`;
    }

    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}m ${seconds.toString().padStart(2, "0")}s`;
  };

  const updateTimers = modal => {
    const elapsed = startedAt ? Date.now() - startedAt : 0;
    modal.querySelectorAll("[data-reconnect-elapsed]").forEach(timer => {
      timer.textContent = formatElapsed(elapsed);
    });
  };

  const isActive = modal => activeClasses.some(className => modal.classList.contains(className));

  const sync = modal => {
    if (isActive(modal)) {
      startedAt ??= Date.now();
      updateTimers(modal);
      intervalId ??= window.setInterval(() => updateTimers(modal), 1000);
      return;
    }

    startedAt = null;
    updateTimers(modal);
    if (intervalId) {
      window.clearInterval(intervalId);
      intervalId = null;
    }
  };

  const initialize = () => {
    const modal = document.getElementById(modalId);
    if (!modal) {
      return;
    }

    sync(modal);
    new MutationObserver(() => sync(modal)).observe(modal, {
      attributeFilter: ["class"],
      attributes: true
    });
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initialize, { once: true });
  } else {
    initialize();
  }
})();
