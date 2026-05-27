(() => {
  const sleep = milliseconds => new Promise(resolve => window.setTimeout(resolve, milliseconds));

  const typeText = async (target, value, speed, token) => {
    target.textContent = "";

    for (let index = 0; index < value.length; index += 1) {
      if (token.cancelled) {
        return false;
      }

      target.textContent = value.slice(0, index + 1);
      await sleep(speed);
    }

    return true;
  };

  const eraseText = async (target, value, speed, token) => {
    for (let index = value.length; index >= 0; index -= 1) {
      if (token.cancelled) {
        return false;
      }

      target.textContent = value.slice(0, index);
      await sleep(speed);
    }

    return true;
  };

  const initializeTypewriter = root => {
    const target = root.querySelector("[data-typewriter-text]");
    const options = Array.from(root.querySelectorAll("[data-typewriter-option]"))
      .map(option => option.textContent?.trim() ?? "")
      .filter(Boolean);

    if (!target || options.length === 0) {
      return;
    }

    const uniqueOptions = [...new Set(options)];
    const signature = uniqueOptions.join("");
    if (root.dataset.typewriterReady === "true" && root.dataset.typewriterSignature === signature) {
      return;
    }

    if (root._malievTypewriterToken) {
      root._malievTypewriterToken.cancelled = true;
    }

    root.dataset.typewriterReady = "true";
    root.dataset.typewriterSignature = signature;
    target.textContent = uniqueOptions[0];

    if (uniqueOptions.length === 1) {
      return;
    }

    const interval = Number.parseInt(root.dataset.typewriterInterval ?? "5000", 10);
    const typeSpeed = Number.parseInt(root.dataset.typewriterSpeed ?? "26", 10);
    const eraseSpeed = Number.parseInt(root.dataset.typewriterEraseSpeed ?? "16", 10);
    const cycleInterval = Number.isFinite(interval) && interval > 1200 ? interval : 5000;
    const token = { cancelled: false };
    let optionIndex = 0;

    root._malievTypewriterToken = token;

    const run = async () => {
      while (!token.cancelled) {
        const text = uniqueOptions[optionIndex % uniqueOptions.length];
        const typeDuration = text.length * typeSpeed;
        const eraseDuration = text.length * eraseSpeed;
        const holdDuration = Math.max(650, cycleInterval - typeDuration - eraseDuration);

        if (!await typeText(target, text, typeSpeed, token)) {
          return;
        }

        await sleep(holdDuration);

        if (!await eraseText(target, text, eraseSpeed, token)) {
          return;
        }

        optionIndex += 1;
      }
    };

    target.textContent = "";
    void run();
  };

  const initializeAll = () => {
    document.querySelectorAll("[data-maliev-typewriter]").forEach(initializeTypewriter);
  };

  const scheduleInitialize = delay => {
    window.setTimeout(initializeAll, delay);
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initializeAll, { once: true });
  } else {
    initializeAll();
  }

  window.addEventListener("load", initializeAll, { once: true });
  scheduleInitialize(250);
  scheduleInitialize(1000);
  scheduleInitialize(2500);

  const observer = new MutationObserver(mutations => {
    if (mutations.some(mutation => mutation.addedNodes.length > 0)) {
      initializeAll();
    }
  });

  observer.observe(document.documentElement, { childList: true, subtree: true });
})();
