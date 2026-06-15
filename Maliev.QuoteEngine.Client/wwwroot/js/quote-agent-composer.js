const composerHandlers = new WeakMap();
const typingAnimations = new WeakMap();
const dictationSessions = new WeakMap();
const dictationButtonHandlers = new WeakMap();
const composerDotNetRefs = new WeakMap();

export function initComposer(textarea, dotNetRef, dictationButton) {
  if (!textarea || !dotNetRef) {
    return;
  }

  disposeComposer(textarea);
  composerDotNetRefs.set(textarea, dotNetRef);

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

  const input = () => {
    normalizeCaretAfterInput(textarea);
    updateComposerShape(textarea);
  };

  let holdTimer = 0;
  let holdStarted = false;
  let suppressClick = false;
  const pointerDown = event => {
    if (event.button !== undefined && event.button !== 0) {
      return;
    }

    holdStarted = false;
    window.clearTimeout(holdTimer);
    holdTimer = window.setTimeout(async () => {
      holdStarted = true;
      suppressClick = true;
      dictationButton?.setPointerCapture?.(event.pointerId);
      await dotNetRef.invokeMethodAsync("BeginDictationFromHoldAsync");
    }, 220);
  };
  const pointerUp = async event => {
    window.clearTimeout(holdTimer);
    if (!holdStarted) {
      return;
    }

    dictationButton?.releasePointerCapture?.(event.pointerId);
    await dotNetRef.invokeMethodAsync("StopDictationAsync");
  };
  const pointerCancel = async event => {
    window.clearTimeout(holdTimer);
    if (!holdStarted) {
      return;
    }

    dictationButton?.releasePointerCapture?.(event.pointerId);
    await dotNetRef.invokeMethodAsync("StopDictationAsync");
  };
  const click = async event => {
    if (suppressClick) {
      suppressClick = false;
      event.preventDefault();
      return;
    }

    await dotNetRef.invokeMethodAsync("ToggleDictationAsync");
  };

  textarea.addEventListener("keydown", keydown);
  textarea.addEventListener("input", input);
  if (dictationButton) {
    dictationButton.addEventListener("pointerdown", pointerDown);
    dictationButton.addEventListener("pointerup", pointerUp);
    dictationButton.addEventListener("pointerleave", pointerCancel);
    dictationButton.addEventListener("pointercancel", pointerCancel);
    dictationButton.addEventListener("click", click);
    dictationButtonHandlers.set(textarea, { dictationButton, pointerDown, pointerUp, pointerCancel, click });
  }

  composerHandlers.set(textarea, { keydown, input });
  updateComposerShape(textarea);
  focusComposer(textarea);
}

export function disposeComposer(textarea) {
  const handlers = composerHandlers.get(textarea);
  if (!textarea || !handlers) {
    return;
  }

  textarea.removeEventListener("keydown", handlers.keydown);
  textarea.removeEventListener("input", handlers.input);
  const dictationHandlers = dictationButtonHandlers.get(textarea);
  if (dictationHandlers?.dictationButton) {
    dictationHandlers.dictationButton.removeEventListener("pointerdown", dictationHandlers.pointerDown);
    dictationHandlers.dictationButton.removeEventListener("pointerup", dictationHandlers.pointerUp);
    dictationHandlers.dictationButton.removeEventListener("pointerleave", dictationHandlers.pointerCancel);
    dictationHandlers.dictationButton.removeEventListener("pointercancel", dictationHandlers.pointerCancel);
    dictationHandlers.dictationButton.removeEventListener("click", dictationHandlers.click);
    dictationButtonHandlers.delete(textarea);
  }

  cancelDictation(textarea);
  composerDotNetRefs.delete(textarea);
  composerHandlers.delete(textarea);
}

export function focusComposer(textarea) {
  if (!textarea) {
    return;
  }

  window.requestAnimationFrame(() => {
    textarea.focus({ preventScroll: true });
    if (document.activeElement === textarea) {
      moveCaretToEnd(textarea);
    }

    updateComposerShape(textarea);
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
  updateComposerShape(textarea);

  for (let index = 0; index < value.length; index += step) {
    if (typingAnimations.get(textarea) !== token) {
      return;
    }

    textarea.value = value.slice(0, Math.min(value.length, index + step));
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
    updateComposerShape(textarea);
    await wait(12);
  }

  textarea.value = value;
  textarea.dispatchEvent(new Event("input", { bubbles: true }));
  updateComposerShape(textarea);
  typingAnimations.delete(textarea);
}

export async function dictateComposerText(textarea) {
  await beginDictation(textarea);
  return endDictation(textarea);
}

export async function beginDictation(textarea, dictationButton) {
  const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SpeechRecognition) {
    throw new Error("Speech recognition is not supported in this browser.");
  }

  if (!textarea) {
    return;
  }

  cancelDictation(textarea);

  const rawSelectionStart = typeof textarea.selectionStart === "number"
    ? textarea.selectionStart
    : textarea.value.length;
  const rawSelectionEnd = typeof textarea.selectionEnd === "number"
    ? textarea.selectionEnd
    : rawSelectionStart;
  const insertion = resolveDictationInsertion(textarea.value, rawSelectionStart, rawSelectionEnd);
  const session = {
    after: textarea.value.slice(insertion.end),
    before: textarea.value.slice(0, insertion.start),
    button: dictationButton || dictationButtonHandlers.get(textarea)?.dictationButton || null,
    dotNetStopRequested: false,
    finalTranscript: "",
    interimTranscript: "",
    recognition: new SpeechRecognition(),
    settled: false,
    active: true,
    dotNetRef: composerDotNetRefs.get(textarea)
  };
  dictationSessions.set(textarea, session);
  prepareDictationPreview(textarea, session);
  startDictationMeter(session);

  session.recognition.continuous = true;
  session.recognition.interimResults = true;
  session.recognition.maxAlternatives = 1;
  session.recognition.lang = resolveSpeechRecognitionLanguage();

  session.recognition.onresult = event => {
    let finalText = "";
    let interimText = "";
    for (let index = event.resultIndex || 0; index < event.results.length; index += 1) {
      const result = event.results[index];
      const text = result[0]?.transcript || "";
      if (result.isFinal) {
        finalText += ` ${text}`;
      } else {
        interimText += ` ${text}`;
      }
    }

    session.finalTranscript = summarizeDictation(`${session.finalTranscript} ${finalText}`);
    session.interimTranscript = summarizeDictation(interimText);
    updateDictationPreview(textarea, session, false);
  };

  session.recognition.onerror = event => {
    if (event.error === "no-speech") {
      session.interimTranscript = "";
      updateDictationPreview(textarea, session, false);
      return;
    }

    session.error = event.error || "Dictation failed.";
    try {
      session.recognition.stop();
    } catch {
      // Browser recognition may already be stopped.
    }
  };

  session.recognition.onend = () => {
    if (session.settled || session.dotNetStopRequested) {
      return;
    }

    session.settled = true;
    finishDictation(textarea, session);
    session.dotNetRef?.invokeMethodAsync("StopDictationAsync");
  };

  try {
    session.recognition.start();
  } catch (error) {
    cancelDictation(textarea);
    throw error;
  }
}

export async function endDictation(textarea) {
  const session = dictationSessions.get(textarea);
  if (!textarea || !session) {
    return textarea?.value || "";
  }

  session.dotNetStopRequested = true;
  try {
    session.recognition.stop();
  } catch {
    // Browser recognition may already be stopped.
  }

  return finishDictation(textarea, session);
}

function cancelDictation(textarea) {
  const session = dictationSessions.get(textarea);
  if (!session) {
    return;
  }

  try {
    session.recognition.abort();
  } catch {
    // Ignore aborted or unavailable sessions.
  }

  stopDictationMeter(session);
  session.active = false;
  removeDictationPreview(textarea);
  dictationSessions.delete(textarea);
}

function prepareDictationPreview(textarea, session) {
  clearTextSelection();
  textarea.focus({ preventScroll: true });
  setTextareaSelection(textarea, session.before.length, session.before.length);
  textarea.classList.add("qe-agent-dictation-source");
  updateDictationPreview(textarea, session, false);
}

function updateDictationPreview(textarea, session, finalizing) {
  const rawSpeech = summarizeDictation(`${session.finalTranscript} ${session.interimTranscript}`);
  const speechPrefix = rawSpeech && session.before && !/\s$/.test(session.before) ? " " : "";
  const speechSuffix = rawSpeech && session.after && !/^\s/.test(session.after) ? " " : "";
  const previewValue = `${session.before}${speechPrefix}${rawSpeech}${speechSuffix}${session.after}`;
  textarea.value = previewValue;
  textarea.dispatchEvent(new Event("input", { bubbles: true }));
  setTextareaSelection(textarea, session.before.length + speechPrefix.length + rawSpeech.length, session.before.length + speechPrefix.length + rawSpeech.length);
  updateComposerShape(textarea);

  const overlay = ensureDictationOverlay(textarea);
  overlay.innerHTML = "";
  overlay.append(createDictationSpan("qe-agent-dictation-preview-before", session.before));
  overlay.append(createDictationSpan("qe-agent-dictation-preview-spacer", speechPrefix));
  overlay.append(createDictationSpan(finalizing ? "qe-agent-dictation-preview-final" : "qe-agent-dictation-preview-live", rawSpeech));
  if (!rawSpeech) {
    overlay.append(createDictationSpan("qe-agent-dictation-preview-caret", ""));
  }
  overlay.append(createDictationSpan("qe-agent-dictation-preview-spacer", speechSuffix));
  overlay.append(createDictationSpan("qe-agent-dictation-preview-after", session.after));
}

function finishDictation(textarea, session) {
  stopDictationMeter(session);
  session.active = false;
  if (session.error) {
    removeDictationPreview(textarea);
    dictationSessions.delete(textarea);
    return textarea.value || "";
  }

  const speech = summarizeDictation(`${session.finalTranscript} ${session.interimTranscript}`);
  const refinedSpeech = summarizeDictation(speech);
  const speechPrefix = refinedSpeech && session.before && !/\s$/.test(session.before) ? " " : "";
  const speechSuffix = refinedSpeech && session.after && !/^\s/.test(session.after) ? " " : "";
  const nextValue = `${session.before}${speechPrefix}${refinedSpeech}${speechSuffix}${session.after}`;
  session.settled = true;
  textarea.value = nextValue;
  textarea.dispatchEvent(new Event("input", { bubbles: true }));
  setTextareaSelection(textarea, session.before.length + speechPrefix.length + refinedSpeech.length, session.before.length + speechPrefix.length + refinedSpeech.length);
  updateComposerShape(textarea);
  removeDictationPreview(textarea);
  dictationSessions.delete(textarea);
  return nextValue;
}

function ensureDictationOverlay(textarea) {
  const composer = textarea.closest?.(".qe-agent-composer");
  let overlay = composer?.querySelector(".qe-agent-dictation-preview");
  if (!overlay && composer) {
    overlay = document.createElement("div");
    overlay.className = "qe-agent-dictation-preview";
    overlay.setAttribute("aria-hidden", "true");
    textarea.insertAdjacentElement("afterend", overlay);
  }

  return overlay;
}

function createDictationSpan(className, text) {
  const span = document.createElement("span");
  span.className = className;
  span.textContent = text || "";
  return span;
}

function removeDictationPreview(textarea) {
  textarea?.classList.remove("qe-agent-dictation-source");
  const composer = textarea?.closest?.(".qe-agent-composer");
  composer?.querySelector(".qe-agent-dictation-preview")?.remove();
}

function resolveDictationInsertion(value, selectionStart, selectionEnd) {
  const text = String(value ?? "");
  if (selectionStart !== selectionEnd) {
    return { start: selectionStart, end: selectionEnd };
  }

  let index = Math.max(0, Math.min(selectionEnd, text.length));
  while (index < text.length && !/\s/.test(text[index])) {
    index += 1;
  }

  return { start: index, end: index };
}

function setTextareaSelection(textarea, start, end) {
  if (typeof textarea?.setSelectionRange !== "function") {
    return;
  }

  try {
    textarea.setSelectionRange(start, end);
  } catch {
    // Some input modes may temporarily reject selection changes.
  }
}

function resolveSpeechRecognitionLanguage() {
  const language = document.documentElement.lang || navigator.language || "en-US";
  if (language.toLowerCase() === "th") {
    return "th-TH";
  }

  if (language.toLowerCase() === "en") {
    return navigator.language?.startsWith("en-") ? navigator.language : "en-US";
  }

  return language;
}

async function startDictationMeter(session) {
  const button = session.button;
  if (!button || !navigator.mediaDevices?.getUserMedia || !window.AudioContext && !window.webkitAudioContext) {
    setDictationLevel(button, 0, "quiet");
    return;
  }

  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const AudioContext = window.AudioContext || window.webkitAudioContext;
    const audioContext = new AudioContext();
    const analyser = audioContext.createAnalyser();
    analyser.fftSize = 1024;
    const source = audioContext.createMediaStreamSource(stream);
    const samples = new Uint8Array(analyser.fftSize);

    source.connect(analyser);
    session.audioContext = audioContext;
    session.audioStream = stream;
    session.audioAnalyser = analyser;
    session.audioSource = source;

    if (!session.active) {
      stopDictationMeter(session);
      return;
    }

    const tick = () => {
      if (!session.active) {
        return;
      }

      analyser.getByteTimeDomainData(samples);
      let sum = 0;
      for (let index = 0; index < samples.length; index += 1) {
        const centered = (samples[index] - 128) / 128;
        sum += centered * centered;
      }

      const rms = Math.sqrt(sum / samples.length);
      const level = Math.min(1, rms * 7);
      const state = level > 0.82 ? "loud" : level < 0.12 ? "quiet" : "good";
      setDictationLevel(button, level, state);
      session.audioFrame = window.requestAnimationFrame(tick);
    };

    tick();
  } catch {
    setDictationLevel(button, 0, "quiet");
  }
}

function setDictationLevel(button, level, state) {
  if (!button) {
    return;
  }

  const normalized = Math.max(0, Math.min(1, level));
  button.style.setProperty("--qe-dictation-level", normalized.toFixed(2));
  button.style.setProperty("--qe-dictation-ring", `${Math.round(4 + normalized * 16)}px`);
  button.style.setProperty("--qe-dictation-glow", `${Math.round(10 + normalized * 22)}px`);
  button.style.setProperty("--qe-dictation-scale", (1 + normalized * 0.12).toFixed(3));
  button.dataset.dictationLevel = state;
}

function stopDictationMeter(session) {
  if (session.audioFrame) {
    window.cancelAnimationFrame(session.audioFrame);
  }

  session.audioStream?.getTracks?.().forEach(track => track.stop());
  session.audioContext?.close?.();
  if (session.button) {
    session.button.style.removeProperty("--qe-dictation-level");
    session.button.style.removeProperty("--qe-dictation-ring");
    session.button.style.removeProperty("--qe-dictation-glow");
    session.button.style.removeProperty("--qe-dictation-scale");
    delete session.button.dataset.dictationLevel;
  }
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

function updateComposerShape(textarea) {
  const composer = textarea.closest?.(".qe-agent-composer");
  if (!composer) {
    return;
  }

  window.requestAnimationFrame(() => {
    const lineHeight = Number.parseFloat(getComputedStyle(textarea).lineHeight) || 24;
    const value = textarea.value || "";
    const isMultiline = value.includes("\n") || value.length > 68 || value.length > 0 && textarea.scrollHeight > lineHeight * 1.55;
    composer.classList.toggle("qe-agent-composer--multiline", isMultiline);
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
