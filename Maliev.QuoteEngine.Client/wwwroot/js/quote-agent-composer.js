const composerHandlers = new WeakMap();
const typingAnimations = new WeakMap();

export function initComposer(textarea, dotNetRef) {
  if (!textarea || !dotNetRef) {
    return;
  }

  disposeComposer(textarea);

  const keydown = event => {
    if (event.key !== "Enter" || event.shiftKey || event.altKey || event.ctrlKey || event.metaKey || event.isComposing) {
      return;
    }

    event.preventDefault();
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
    window.requestAnimationFrame(() => {
      dotNetRef.invokeMethodAsync("SubmitComposerFromKeyboardAsync");
    });
  };

  const pointerdown = () => clearTextSelection();
  const focus = () => clearTextSelection();
  const windowFocus = () => focusComposer(textarea);
  const input = () => normalizeCaretAfterInput(textarea);
  const selectionchange = () => {
    if (document.activeElement === textarea) {
      clearTextSelection();
    }
  };

  textarea.addEventListener("keydown", keydown);
  textarea.addEventListener("input", input);
  textarea.addEventListener("pointerdown", pointerdown);
  textarea.addEventListener("focus", focus);
  window.addEventListener("focus", windowFocus);
  document.addEventListener("selectionchange", selectionchange);
  composerHandlers.set(textarea, { keydown, input, pointerdown, focus, windowFocus, selectionchange });
  focusComposer(textarea);
}

export function disposeComposer(textarea) {
  const handlers = composerHandlers.get(textarea);
  if (!textarea || !handlers) {
    return;
  }

  textarea.removeEventListener("keydown", handlers.keydown);
  textarea.removeEventListener("input", handlers.input);
  textarea.removeEventListener("pointerdown", handlers.pointerdown);
  textarea.removeEventListener("focus", handlers.focus);
  window.removeEventListener("focus", handlers.windowFocus);
  document.removeEventListener("selectionchange", handlers.selectionchange);
  composerHandlers.delete(textarea);
}

export function focusComposer(textarea) {
  if (!textarea) {
    return;
  }

  clearTextSelection();
  window.requestAnimationFrame(() => {
    clearTextSelection();
    textarea.focus({ preventScroll: true });
    moveCaretToEnd(textarea);
  });
}

export async function typeComposerText(textarea, text) {
  if (!textarea) {
    return;
  }

  const token = {};
  const value = String(text ?? "");
  const step = Math.max(1, Math.ceil(value.length / 90));
  typingAnimations.set(textarea, token);
  clearTextSelection();
  textarea.focus({ preventScroll: true });
  textarea.value = "";
  textarea.dispatchEvent(new Event("input", { bubbles: true }));

  for (let index = 0; index < value.length; index += step) {
    if (typingAnimations.get(textarea) !== token) {
      return;
    }

    textarea.value = value.slice(0, Math.min(value.length, index + step));
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
    await wait(12);
  }

  textarea.value = value;
  textarea.dispatchEvent(new Event("input", { bubbles: true }));
  typingAnimations.delete(textarea);
}

export async function dictateComposerText(textarea) {
  const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SpeechRecognition) {
    throw new Error("Speech recognition is not supported in this browser.");
  }

  const transcript = await captureSpeech(SpeechRecognition);
  const summarizedText = summarizeDictation(transcript);
  if (summarizedText) {
    await typeComposerText(textarea, summarizedText);
  }

  return summarizedText;
}

function clearTextSelection() {
  const selection = window.getSelection?.();
  if (selection && selection.rangeCount > 0) {
    selection.removeAllRanges();
  }
}

function moveCaretToEnd(textarea) {
  if (typeof textarea.setSelectionRange !== "function") {
    return;
  }

  const end = textarea.value?.length ?? 0;
  try {
    textarea.setSelectionRange(end, end);
  } catch {
    // Some input modes may temporarily reject selection changes.
  }
}

function normalizeCaretAfterInput(textarea) {
  window.requestAnimationFrame(() => {
    if (document.activeElement !== textarea || !textarea.value) {
      return;
    }

    if (textarea.selectionStart === 0 && textarea.selectionEnd === 0) {
      moveCaretToEnd(textarea);
    }
  });
}

function wait(milliseconds) {
  return new Promise(resolve => window.setTimeout(resolve, milliseconds));
}

function captureSpeech(SpeechRecognition) {
  return new Promise((resolve, reject) => {
    const recognition = new SpeechRecognition();
    let transcript = "";
    let settled = false;

    recognition.continuous = false;
    recognition.interimResults = false;
    recognition.maxAlternatives = 1;
    recognition.lang = document.documentElement.lang || navigator.language || "en-US";

    recognition.onresult = event => {
      transcript = Array.from(event.results)
        .map(result => result[0]?.transcript || "")
        .join(" ")
        .trim();
    };

    recognition.onerror = event => {
      if (settled) {
        return;
      }

      settled = true;
      reject(new Error(event.error || "Dictation failed."));
    };

    recognition.onend = () => {
      if (settled) {
        return;
      }

      settled = true;
      resolve(transcript);
    };

    try {
      recognition.start();
    } catch (error) {
      settled = true;
      reject(error);
    }
  });
}

function summarizeDictation(text) {
  return String(text ?? "")
    .replace(/\s+/g, " ")
    .trim();
}
