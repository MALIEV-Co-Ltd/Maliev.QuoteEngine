const composerHandlers = new WeakMap();
const typingAnimations = new WeakMap();
const dictationSessions = new WeakMap();
const dictationButtonHandlers = new WeakMap();
const composerDotNetRefs = new WeakMap();

let transformersPipeline = null;
let transformersLoadPromise = null;
let transformersReady = false;
let transformersFailed = false;

async function loadWhisperModel(dotNetRef) {
  if (transformersReady) return true;
  if (transformersFailed) return false;
  if (transformersLoadPromise) return transformersLoadPromise;

  transformersLoadPromise = (async () => {
    dotNetRef?.invokeMethodAsync("ReportDictationModelProgressAsync", 0.01, "Loading speech engine...");
    try {
      const module = await import("https://cdn.jsdelivr.net/npm/@huggingface/transformers@3.4/dist/transformers.min.js");
      const { pipeline } = module;

      dotNetRef?.invokeMethodAsync("ReportDictationModelProgressAsync", 0.1, "Loading voice model (39MB)...");

      transformersPipeline = await pipeline(
        "automatic-speech-recognition",
        "onnx-community/whisper-tiny",
        {
          progress_callback: progress => {
            if (progress?.status === "progress") {
              const pct = 0.1 + (progress.progress / 100) * 0.9;
              const rounded = Math.round(progress.progress);
              if (rounded % 5 !== 0 && rounded < 100) return;
              if (rounded === loadWhisperModel._lastReportedPct) return;
              loadWhisperModel._lastReportedPct = rounded;
              dotNetRef?.invokeMethodAsync("ReportDictationModelProgressAsync", pct,
                `Loading voice model${progress.file ? " (" + progress.file.split("/").pop() + ")" : ""}... ${rounded}%`);
            }
          }
        }
      );

      transformersReady = true;
      dotNetRef?.invokeMethodAsync("ReportDictationModelProgressAsync", 1, "Ready");
      return true;
    } catch (error) {
      console.warn("Whisper model unavailable, falling back to browser native:", error);
      transformersFailed = true;
      dotNetRef?.invokeMethodAsync("ReportDictationModelProgressAsync", 0, "fallback");
      return false;
    }
  })();

  return transformersLoadPromise;
}

async function startWhisperCapture(session) {
  try {
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: true, noiseSuppression: true }
    });

    const AudioContext = window.AudioContext || window.webkitAudioContext;
    const audioContext = new AudioContext({ sampleRate: 16000 });

    if (audioContext.state === "suspended") {
      await audioContext.resume().catch(() => {});
    }

    const source = audioContext.createMediaStreamSource(stream);
    const processor = audioContext.createScriptProcessor(4096, 1, 1);

    const chunks = [];
    processor.onaudioprocess = event => {
      if (!session?.active) return;
      chunks.push(new Float32Array(event.inputBuffer.getChannelData(0)));
    };

    source.connect(processor);
    processor.connect(audioContext.destination);

    session.whisperChunks = chunks;
    session.whisperStream = stream;
    session.whisperAudioContext = audioContext;
    session.whisperProcessor = processor;
    session.whisperSource = source;

    return stream;
  } catch (error) {
    console.warn("Microphone capture failed:", error);
    throw error;
  }
}

function stopWhisperCapture(session) {
  if (session.whisperProcessor) {
    try { session.whisperProcessor.disconnect(); } catch {}
  }
  if (session.whisperSource) {
    try { session.whisperSource.disconnect(); } catch {}
  }
  if (session.whisperAudioContext) {
    try { session.whisperAudioContext.close(); } catch {}
  }
  if (session.whisperStream) {
    session.whisperStream.getTracks().forEach(t => t.stop());
  }
}

function getWhisperAudioBuffer(session) {
  const chunks = session.whisperChunks;
  if (!chunks || chunks.length === 0) return null;
  const totalLength = chunks.reduce((s, c) => s + c.length, 0);
  const result = new Float32Array(totalLength);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  return result;
}


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
  let pressStartedActive = false;
  let suppressClick = false;
  const pointerDown = async event => {
    if (event.button !== undefined && event.button !== 0) {
      return;
    }

    pressStartedActive = isDictationActive(textarea);
    holdStarted = false;
    window.clearTimeout(holdTimer);
    if (pressStartedActive) {
      await finishDictationFromUserAction(textarea, dotNetRef);
      return;
    }

    suppressClick = true;
    dictationButton?.setPointerCapture?.(event.pointerId);
    await startDictationFromUserAction(textarea, dictationButton, dotNetRef);
    holdTimer = window.setTimeout(async () => {
      holdStarted = true;
    }, 220);
  };
  const pointerUp = async event => {
    window.clearTimeout(holdTimer);
    dictationButton?.releasePointerCapture?.(event.pointerId);
    if (pressStartedActive) {
      return;
    }

    if (!holdStarted) {
      return;
    }

    await finishDictationFromUserAction(textarea, dotNetRef);
  };
  const pointerCancel = async event => {
    window.clearTimeout(holdTimer);
    dictationButton?.releasePointerCapture?.(event.pointerId);
    if (pressStartedActive) {
      return;
    }

    if (!holdStarted) {
      return;
    }

    await finishDictationFromUserAction(textarea, dotNetRef);
  };
  const click = async event => {
    if (suppressClick) {
      suppressClick = false;
      event.preventDefault();
      return;
    }

    const session = dictationSessions.get(textarea);
    if (!session || session.settled) {
      event.preventDefault();
      return;
    }

    if (isDictationActive(textarea) || dictationButton?.classList.contains("active")) {
      await finishDictationFromUserAction(textarea, dotNetRef);
      return;
    }

    await startDictationFromUserAction(textarea, dictationButton, dotNetRef);
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

  const composer = textarea.closest?.(".qe-agent-composer");
  const resizeObserver = composer && window.ResizeObserver
    ? new ResizeObserver(() => updateComposerExpansionOffset(textarea))
    : null;
  resizeObserver?.observe(composer);

  const mutationObserver = composer
    ? new MutationObserver(() => updateComposerExpansionOffset(textarea))
    : null;
  mutationObserver?.observe(composer, { attributes: true, attributeFilter: ["class"] });

  composerHandlers.set(textarea, { keydown, input, resizeObserver, mutationObserver });
  updateComposerShape(textarea);
  focusComposer(textarea);
}

export function isComposerInitialized(textarea) {
  return composerHandlers.has(textarea);
}

export function disposeComposer(textarea) {
  const handlers = composerHandlers.get(textarea);
  if (!textarea || !handlers) {
    return;
  }

  textarea.removeEventListener("keydown", handlers.keydown);
  textarea.removeEventListener("input", handlers.input);
  handlers.resizeObserver?.disconnect?.();
  handlers.mutationObserver?.disconnect?.();
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

function isDictationActive(textarea) {
  const session = dictationSessions.get(textarea);
  return !!session?.active && !session.settled && !session.dotNetStopRequested;
}

async function startDictationFromUserAction(textarea, dictationButton, dotNetRef) {
  try {
    await beginDictation(textarea, dictationButton);
    await dotNetRef.invokeMethodAsync("DictationStartedAsync");
  } catch (error) {
    await dotNetRef.invokeMethodAsync(
      "ReportDictationErrorAsync",
      error?.name || error?.message || "dictation-failed");
  }
}

async function finishDictationFromUserAction(textarea, dotNetRef) {
  try {
    await dotNetRef.invokeMethodAsync("BeginDictationProcessingAsync");
    const session = dictationSessions.get(textarea);
    if (!textarea || !session) {
      await dotNetRef.invokeMethodAsync("CompleteDictationAsync", "", "", "");
      return;
    }

    session.dotNetStopRequested = true;

    if (session.useWhisper) {
      stopWhisperCapture(session);
      stopDictationMeter(session);
      session.active = false;

      const audioData = getWhisperAudioBuffer(session);
      if (audioData && transformersPipeline) {
        try {
          const result = await transformersPipeline(audioData, {
            task: "transcribe",
            return_timestamps: false
          });
          const text = String(result?.text || "").trim();

          const speechPrefix = text && session.before && !/\s$/.test(session.before) ? " " : "";
          const speechSuffix = text && session.after && !/^\s/.test(session.after) ? " " : "";
          const fullText = `${session.before}${speechPrefix}${text}${speechSuffix}${session.after}`;
          session.settled = true;
          textarea.value = fullText;
          textarea.dispatchEvent(new Event("input", { bubbles: true }));
          setTextareaSelection(textarea, fullText.length, fullText.length);
          updateComposerShape(textarea);

          await dotNetRef.invokeMethodAsync("CompleteDictationAsync", session.before + speechPrefix, text, speechSuffix + session.after);
        } catch (error) {
          console.warn("Whisper inference failed:", error);
          await dotNetRef.invokeMethodAsync("CompleteDictationAsync", "", "", "");
        }
      } else {
        await dotNetRef.invokeMethodAsync("CompleteDictationAsync", "", "", "");
      }
      dictationSessions.delete(textarea);
      return;
    }

    clearDictationRestart(session);
    try {
      session.recognition.stop();
    } catch {
    }

    const result = finishDictation(textarea, session);
    if (typeof result === "object") {
      await dotNetRef.invokeMethodAsync("CompleteDictationAsync", result.contextBefore || "", result.speech || "", result.contextAfter || "");
    } else if (!session.error) {
      await dotNetRef.invokeMethodAsync("CompleteDictationAsync", "", result || "", "");
    }
  } catch (error) {
    await dotNetRef.invokeMethodAsync(
      "ReportDictationErrorAsync",
      error?.name || error?.message || "dictation-failed");
  }
}

export function focusComposer(textarea) {
  if (!textarea) {
    return;
  }

  clearTextSelection();
  window.requestAnimationFrame(() => {
    clearTextSelection();
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

export function setComposerText(textarea, text) {
  if (!textarea) {
    return;
  }

  removeDictationPreview(textarea);
  typingAnimations.delete(textarea);
  clearTextSelection();
  const value = String(text ?? "");
  textarea.value = value;
  textarea.dispatchEvent(new Event("input", { bubbles: true }));
  setTextareaSelection(textarea, value.length, value.length);
  textarea.focus({ preventScroll: true });
  updateComposerShape(textarea);
}

export async function dictateComposerText(textarea) {
  await beginDictation(textarea);
  return endDictation(textarea);
}

export async function loadDictationModel(dotNetRef) {
  return loadWhisperModel(dotNetRef);
}

export async function beginDictation(textarea, dictationButton) {
  if (!textarea) {
    return;
  }

  cancelDictation(textarea);

  if (!dictationButton && textarea) {
    dictationButton = dictationButtonHandlers.get(textarea)?.dictationButton || null;
  }

  const rawSelectionStart = typeof textarea.selectionStart === "number"
    ? textarea.selectionStart
    : textarea.value.length;
  const rawSelectionEnd = typeof textarea.selectionEnd === "number"
    ? textarea.selectionEnd
    : rawSelectionStart;
  const insertion = resolveDictationInsertion(textarea.value, rawSelectionStart, rawSelectionEnd);

  const dotNetRef = composerDotNetRefs.get(textarea);
  const useWhisper = await loadWhisperModel(dotNetRef);

  if (useWhisper) {
    const session = {
      after: textarea.value.slice(insertion.end),
      before: textarea.value.slice(0, insertion.start),
      button: dictationButton,
      dotNetStopRequested: false,
      finalTranscript: "",
      interimTranscript: "",
      settled: false,
      active: true,
      useWhisper: true,
      dotNetRef
    };
    dictationSessions.set(textarea, session);
    prepareDictationPreview(textarea, session);

    await startWhisperCapture(session);
    void startDictationMeter(session, session.whisperStream);
    return;
  }

  const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SpeechRecognition) {
    throw new Error("Speech recognition is not supported in this browser.");
  }

  const session = {
    after: textarea.value.slice(insertion.end),
    before: textarea.value.slice(0, insertion.start),
    button: dictationButton || dictationButtonHandlers.get(textarea)?.dictationButton || null,
    dotNetStopRequested: false,
    finalTranscript: "",
    interimTranscript: "",
    languageIndex: 0,
    languages: resolveSpeechRecognitionLanguages(textarea),
    noSpeechError: false,
    recognition: new SpeechRecognition(),
    restartTimer: 0,
    settled: false,
    active: true,
    thaiRetryCount: 0,
    dotNetRef: composerDotNetRefs.get(textarea),
    useWhisper: false
  };
  dictationSessions.set(textarea, session);
  prepareDictationPreview(textarea, session);

  const primaryLanguage = session.languages[0] || "en-US";
  const isThaiPrimary = primaryLanguage.toLowerCase() === "th-th" || primaryLanguage.toLowerCase().startsWith("th-");
  session.recognition.continuous = !isThaiPrimary;
  session.recognition.interimResults = true;
  session.recognition.maxAlternatives = 1;
  session.recognition.onaudiostart = () => {
    boostDictationLevel(session, 0, "quiet", 0);
  };
  session.recognition.onsoundstart = () => {
    boostDictationLevel(session, 0.34, "good", 900);
  };
  session.recognition.onspeechstart = () => {
    boostDictationLevel(session, 0.5, "good", 1200);
  };
  session.recognition.onspeechend = () => {
    boostDictationLevel(session, 0, "quiet", 0);
  };
  session.recognition.onaudioend = () => {
    boostDictationLevel(session, 0, "quiet", 0);
  };

  session.recognition.onresult = event => {
    session.noSpeechError = false;
    boostDictationLevel(session, 0.45, "good", 1400);
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
    if (event.error === "no-speech" || event.error === "no-match") {
      session.noSpeechError = true;
      session.interimTranscript = "";
      updateDictationPreview(textarea, session, false);
      return;
    }

    if (event.error === "aborted") {
      return;
    }

    reportDictationError(session, event.error || "dictation-failed");
    session.error = event.error || "Dictation failed.";
    session.settled = true;
    finishDictation(textarea, session);
    try {
      session.recognition.stop();
    } catch {
    }
  };

  session.recognition.onend = () => {
    if (session.settled || session.dotNetStopRequested) {
      return;
    }

    if (session.error) {
      session.settled = true;
      finishDictation(textarea, session);
      return;
    }

    scheduleDictationRestart(textarea, session);
  };

  try {
    startRecognitionSession(session);
    void startDictationMeter(session);
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

  if (session.useWhisper) {
    stopWhisperCapture(session);
    stopDictationMeter(session);
    session.active = false;

    const audioData = getWhisperAudioBuffer(session);
    if (audioData && transformersPipeline) {
      try {
        const result = await transformersPipeline(audioData, {
          task: "transcribe",
          return_timestamps: false
        });
        const text = String(result?.text || "").trim();
        const speechPrefix = text && session.before && !/\s$/.test(session.before) ? " " : "";
        const speechSuffix = text && session.after && !/^\s/.test(session.after) ? " " : "";
        session.settled = true;
        dictationSessions.delete(textarea);
        return `${session.before}${speechPrefix}${text}${speechSuffix}${session.after}`;
      } catch {
        session.settled = true;
        dictationSessions.delete(textarea);
        return textarea.value || "";
      }
    }
    session.settled = true;
    dictationSessions.delete(textarea);
    return textarea.value || "";
  }

  clearDictationRestart(session);
  try {
    session.recognition.stop();
  } catch {
  }

  const result = finishDictation(textarea, session);
  return typeof result === "object" ? result.fullText : result;
}

function cancelDictation(textarea) {
  const session = dictationSessions.get(textarea);
  if (!session) {
    return;
  }

  if (session.useWhisper) {
    stopWhisperCapture(session);
    stopDictationMeter(session);
    session.active = false;
    removeDictationPreview(textarea);
    dictationSessions.delete(textarea);
    return;
  }

  clearDictationRestart(session);
  try {
    session.recognition.abort();
  } catch {
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
  // Show the overlay immediately so speech is rendered in blue from the first word.
  // The textarea text becomes transparent via qe-agent-dictation-source while the
  // overlay mirrors it. This state persists through the finalizing phase and is
  // cleared only when setComposerText is called with the LLM-cleaned result.
  const composer = textarea.closest?.(".qe-agent-composer");
  if (composer) {
    composer.setAttribute("data-dictation-active", "");
  }
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
  if (rawSpeech) {
    overlay.append(createDictationSpan(finalizing ? "qe-agent-dictation-preview-final" : "qe-agent-dictation-preview-live", rawSpeech));
  } else {
    overlay.append(createDictationSpan("qe-agent-dictation-preview-caret", ""));
  }
  overlay.append(createDictationSpan("qe-agent-dictation-preview-spacer", speechSuffix));
  overlay.append(createDictationSpan("qe-agent-dictation-preview-after", session.after));
}

function finishDictation(textarea, session) {
  clearDictationRestart(session);
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
  // Overlay is already visible via data-dictation-active (set in prepareDictationPreview).
  // Flip to final (solid blue) state while the LLM cleans the text. The attribute and
  // class are cleared when setComposerText is called with the cleaned result.
  updateDictationPreview(textarea, session, true);
  dictationSessions.delete(textarea);
  return {
    contextBefore: session.before + speechPrefix,
    speech: refinedSpeech,
    contextAfter: speechSuffix + session.after,
    fullText: nextValue
  };
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
  const composer = textarea?.closest?.(".qe-agent-composer");
  composer?.removeAttribute("data-dictation-active");
  textarea?.classList.remove("qe-agent-dictation-source");
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

function resolveSpeechRecognitionLanguage(textarea) {
  return resolveSpeechRecognitionLanguages(textarea)[0] || "en-US";
}

function resolveSpeechRecognitionLanguages(textarea) {
  const configuredLanguages = String(textarea?.dataset?.speechLanguages || "")
    .split(",")
    .map(language => language.trim())
    .filter(Boolean);
  const candidates = [
    ...configuredLanguages,
    textarea?.lang,
    document.documentElement.lang,
    ...(Array.isArray(navigator.languages) ? navigator.languages : []),
    navigator.language,
    "th-TH",
    "en-US"
  ];
  const languages = [];
  for (const candidate of candidates) {
    const language = normalizeSpeechRecognitionLanguage(candidate);
    if (language && !languages.some(existing => existing.toLowerCase() === language.toLowerCase())) {
      languages.push(language);
    }
  }

  return languages.length > 0 ? languages : ["en-US"];
}

function normalizeSpeechRecognitionLanguage(language) {
  const value = String(language || "").trim();
  if (!value) {
    return "";
  }

  const lower = value.toLowerCase();
  if (lower === "th" || lower.startsWith("th-")) {
    return "th-TH";
  }

  if (lower === "en") {
    return navigator.language?.startsWith("en-") ? navigator.language : "en-US";
  }

  return value;
}

function startRecognitionSession(session) {
  if (!session?.active || session.dotNetStopRequested || session.settled) {
    return;
  }

  session.recognition.lang = session.languages[session.languageIndex] || resolveSpeechRecognitionLanguage();
  session.noSpeechError = false;
  session.recognition.start();
}

function scheduleDictationRestart(textarea, session) {
  if (!session?.active || session.dotNetStopRequested || session.settled || session.restartTimer) {
    return;
  }

  if (session.noSpeechError && !session.finalTranscript) {
    const currentLanguage = session.languages[session.languageIndex] || "";
    const isThaiCurrent = currentLanguage.toLowerCase() === "th-th" || currentLanguage.toLowerCase().startsWith("th-");
    if (isThaiCurrent && session.thaiRetryCount < 2) {
      session.thaiRetryCount += 1;
    } else if (session.languages.length > 1) {
      session.languageIndex = (session.languageIndex + 1) % session.languages.length;
      session.thaiRetryCount = 0;
    }
  }

  session.restartTimer = window.setTimeout(() => {
    session.restartTimer = 0;
    if (!session.active || session.dotNetStopRequested || session.settled) {
      return;
    }

    try {
      startRecognitionSession(session);
    } catch (error) {
      if (error?.name === "InvalidStateError") {
        scheduleDictationRestart(textarea, session);
        return;
      }

      reportDictationError(session, error?.name || error?.message || "dictation-failed");
      session.error = error?.name || error?.message || "Dictation failed.";
      session.settled = true;
      finishDictation(textarea, session);
    }
  }, 140);
}

function clearDictationRestart(session) {
  if (!session?.restartTimer) {
    return;
  }

  window.clearTimeout(session.restartTimer);
  session.restartTimer = 0;
}

async function startDictationMeter(session, sharedStream) {
  const button = session.button;
  if (!button || !navigator.mediaDevices?.getUserMedia || !window.AudioContext && !window.webkitAudioContext) {
    setDictationLevel(button, 0, "quiet");
    return;
  }

  try {
    const stream = sharedStream || await navigator.mediaDevices.getUserMedia({ audio: true });
    const AudioContext = window.AudioContext || window.webkitAudioContext;
    const audioContext = new AudioContext();
    if (audioContext.state === "suspended") {
      await audioContext.resume().catch(() => {});
    }

    const analyser = audioContext.createAnalyser();
    analyser.fftSize = 1024;
    analyser.smoothingTimeConstant = 0.72;
    const source = audioContext.createMediaStreamSource(stream);
    const samples = new Uint8Array(analyser.fftSize);

    source.connect(analyser);
    session.audioContext = audioContext;
    session.audioStream = stream;
    session.audioAnalyser = analyser;
    session.audioSource = source;
    initializeDictationMeterHistory(session);

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
      let peak = 0;
      for (let index = 0; index < samples.length; index += 1) {
        const centered = (samples[index] - 128) / 128;
        sum += centered * centered;
        const abs = Math.abs(centered);
        if (abs > peak) {
          peak = abs;
        }
      }

      const rms = Math.sqrt(sum / samples.length);
      const noiseFloor = session.noiseFloor ?? 0.018;
      if (rms < noiseFloor * 1.45) {
        session.noiseFloor = noiseFloor * 0.94 + rms * 0.06;
      }

      const activeSignal = Math.max(0, rms - (session.noiseFloor ?? noiseFloor));
      const gate = Math.max(0.008, (session.noiseFloor ?? noiseFloor) * 0.55);
      let level = activeSignal <= gate ? 0 : Math.min(1, (activeSignal - gate) * 44);
      const peakBoost = Math.min(1, peak * 1.6);
      level = Math.max(level, peakBoost * 0.6);
      if (session.speechBoostUntil && Date.now() < session.speechBoostUntil) {
        level = Math.max(level, session.speechBoostLevel || 0);
      }

      const previousLevel = session.smoothedMeterLevel ?? level;
      const smoothedLevel = previousLevel * 0.55 + level * 0.45;
      session.smoothedMeterLevel = smoothedLevel;

      const state = smoothedLevel > 0.82 ? "loud" : smoothedLevel < 0.08 ? "quiet" : "good";
      const now = Date.now();
      if (!session.lastVolumeHistoryAt || now - session.lastVolumeHistoryAt >= 86) {
        appendDictationMeterLevel(session, smoothedLevel);
        session.lastVolumeHistoryAt = now;
      }

      setDictationLevel(button, smoothedLevel, state);
      session.audioFrame = window.requestAnimationFrame(tick);
    };

    tick();
  } catch (error) {
    setDictationLevel(button, 0, "quiet");
    renderDictationMeterHistory(session?.button, []);
    session.meterUnavailable = error?.name || error?.message || "audio-capture";
  }
}

function initializeDictationMeterHistory(session) {
  const bars = getDictationMeterBars(session?.button);
  const count = Math.max(1, bars.length || 16);
  session.lastVolumeHistoryAt = 0;
  session.smoothedMeterLevel = 0;
  session.volumeHistory = Array(count).fill(0);
  renderDictationMeterHistory(session.button, session.volumeHistory);
}

function appendDictationMeterLevel(session, level) {
  if (!session?.active) {
    return;
  }

  const bars = getDictationMeterBars(session.button);
  const count = Math.max(1, bars.length || session.volumeHistory?.length || 16);
  if (!Array.isArray(session.volumeHistory) || session.volumeHistory.length !== count) {
    session.volumeHistory = Array(count).fill(0);
  }

  session.volumeHistory.push(Math.max(0, Math.min(1, level)));
  while (session.volumeHistory.length > count) {
    session.volumeHistory.shift();
  }

  renderDictationMeterHistory(session.button, session.volumeHistory);
}

function getDictationMeterBars(button) {
  const composer = button?.closest?.(".qe-agent-composer");
  return Array.from(composer?.querySelectorAll?.(".qe-agent-dictation-meter-bars i") || []);
}

function renderDictationMeterHistory(button, history) {
  const bars = getDictationMeterBars(button);
  for (let index = 0; index < bars.length; index += 1) {
    const value = Math.max(0, Math.min(1, history?.[index] || 0));
    bars[index].style.setProperty("--qe-bar-level", value.toFixed(2));
    bars[index].style.opacity = (0.40 + value * 0.60).toFixed(2);
  }
}

function reportDictationError(session, code) {
  if (!session || session.reported) {
    return;
  }

  session.reported = true;
  session.dotNetRef?.invokeMethodAsync("ReportDictationErrorAsync", String(code || "dictation-failed"));
}

function boostDictationLevel(session, level, state, milliseconds) {
  if (!session?.button || !session.active) {
    return;
  }

  const normalized = Math.max(0, Math.min(1, level));
  session.speechBoostLevel = normalized;
  session.speechBoostUntil = milliseconds > 0 ? Date.now() + milliseconds : 0;
  setDictationLevel(session.button, normalized, state);
}

function setDictationLevel(button, level, state) {
  const composer = button?.closest?.(".qe-agent-composer");
  if (!composer) {
    return;
  }

  const normalized = Math.max(0, Math.min(1, level));
  // The composer's box-shadow widens with the level; quiet collapses it back
  // to the resting shadow so the composer itself is the volume indicator.
  composer.style.setProperty("--qe-dictation-level", normalized.toFixed(2));
  composer.dataset.dictationLevel = state;
}

function clearDictationLevel(button) {
  const composer = button?.closest?.(".qe-agent-composer");
  if (!composer) {
    return;
  }

  composer.style.removeProperty("--qe-dictation-level");
  delete composer.dataset.dictationLevel;
  renderDictationMeterHistory(button, []);
}

function stopDictationMeter(session) {
  if (session.audioFrame) {
    window.cancelAnimationFrame(session.audioFrame);
  }

  session.audioStream?.getTracks?.().forEach(track => track.stop());
  session.audioContext?.close?.();
  clearDictationLevel(session.button);
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

let composerShapeMirror = null;

function ensureComposerShapeMirror() {
  if (composerShapeMirror && composerShapeMirror.isConnected) {
    return composerShapeMirror;
  }

  const mirror = document.createElement("div");
  mirror.setAttribute("aria-hidden", "true");
  mirror.style.position = "absolute";
  mirror.style.top = "0";
  mirror.style.left = "-9999px";
  mirror.style.visibility = "hidden";
  mirror.style.pointerEvents = "none";
  mirror.style.boxSizing = "content-box";
  mirror.style.margin = "0";
  mirror.style.border = "0";
  mirror.style.padding = "0";
  document.body.appendChild(mirror);
  composerShapeMirror = mirror;
  return mirror;
}

function isComposerTextWrapped(textarea, value) {
  if (!value) {
    return false;
  }

  const composer = textarea.closest?.(".qe-agent-composer");
  if (!composer) {
    return false;
  }

  const textareaStyles = getComputedStyle(textarea);
  const lineHeight = Number.parseFloat(textareaStyles.lineHeight) || 24;

  // The composer grid keeps the same four column tracks in both the pill and the
  // multiline layouts, so the second (text) track is a stable reference for the
  // single-line text width. Measuring wrap against that fixed width — instead of
  // the textarea's live width, which changes when the layout switches — is what
  // stops the pill <-> rectangle shape from oscillating frame to frame.
  const columns = getComputedStyle(composer).gridTemplateColumns.split(" ");
  const pillTextTrack = columns.length >= 2 ? Number.parseFloat(columns[1]) : Number.NaN;
  if (!Number.isFinite(pillTextTrack) || pillTextTrack <= 0) {
    // Fallback for engines that do not resolve grid tracks to pixels.
    return textarea.scrollHeight > lineHeight * 1.8;
  }

  const horizontalInset = (Number.parseFloat(textareaStyles.paddingLeft) || 0)
    + (Number.parseFloat(textareaStyles.paddingRight) || 0)
    + (Number.parseFloat(textareaStyles.borderLeftWidth) || 0)
    + (Number.parseFloat(textareaStyles.borderRightWidth) || 0);
  const contentWidth = Math.max(0, pillTextTrack - horizontalInset);

  const mirror = ensureComposerShapeMirror();
  mirror.style.width = `${contentWidth}px`;
  mirror.style.fontFamily = textareaStyles.fontFamily;
  mirror.style.fontSize = textareaStyles.fontSize;
  mirror.style.fontWeight = textareaStyles.fontWeight;
  mirror.style.fontStyle = textareaStyles.fontStyle;
  mirror.style.lineHeight = textareaStyles.lineHeight;
  mirror.style.letterSpacing = textareaStyles.letterSpacing;
  mirror.style.textTransform = textareaStyles.textTransform;
  mirror.style.tabSize = textareaStyles.tabSize;
  // Mirror the textarea's real wrapping rules so the measured line count matches.
  mirror.style.whiteSpace = "pre-wrap";
  mirror.style.overflowWrap = textareaStyles.overflowWrap;
  mirror.style.wordBreak = textareaStyles.wordBreak;
  mirror.textContent = value;

  return mirror.scrollHeight > lineHeight * 1.5;
}

function updateComposerShape(textarea) {
  const composer = textarea.closest?.(".qe-agent-composer");
  if (!composer) {
    return;
  }

  const value = textarea.value || "";
  const isMultiline = value.includes("\n") || isComposerTextWrapped(textarea, value);
  composer.classList.toggle("qe-agent-composer--multiline", isMultiline);
  updateComposerExpansionOffset(textarea);
}

function updateComposerExpansionOffset(textarea) {
  const composer = textarea.closest?.(".qe-agent-composer");
  if (!composer) {
    return;
  }

  const styles = getComputedStyle(composer);
  const collapsedHeight = Number.parseFloat(styles.getPropertyValue("--qe-composer-collapsed-height")) || 58;
  const growth = Math.max(0, composer.getBoundingClientRect().height - collapsedHeight);
  composer.style.setProperty("--qe-composer-expansion-offset", `${Math.round(growth)}px`);
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


export function listenForAuthComplete(dotNetRef) {
  const handler = event => {
    if (event.origin !== location.origin || !event.data || event.data.type !== 'maliev.chatbot.authenticated') {
      return;
    }
    window.removeEventListener('message', handler);
    dotNetRef.invokeMethodAsync('OnAuthPopupCompleted');
  };
  window.addEventListener('message', handler);
}