const composerHandlers = new WeakMap();
const typingAnimations = new WeakMap();
const dictationSessions = new WeakMap();
const dictationButtonHandlers = new WeakMap();
const composerDotNetRefs = new WeakMap();
const KeyboardDictationHoldDelayMs = 300;

// The textarea whose document-level listeners are currently attached. Tracked
// separately from the composerHandlers WeakMap (which is keyed by element) so a
// re-init on a *recreated* textarea can still tear down the *previous* element's
// listeners. See isUploadMenuInteraction for why leaking them breaks uploads.
let activeComposerTextarea = null;

// A pointerdown/click that lands inside the composer upload-picker menu (or on
// any upload trigger) must NEVER be treated as an "outside" interaction that
// closes the menu. If it is, the menu -- and the <label> that opens the native
// file picker -- is removed before the click is delivered, so "+ -> 3D files"
// closes silently and no picker opens. Deciding by structure/attribute (not by
// matching a captured composer element) keeps this correct even if a stale
// composer listener leaks across an in-place composer re-render. Exported for
// unit testing. See composer-upload-menu.test.mjs.
export function isUploadMenuInteraction(target) {
  if (!target || typeof target.closest !== "function") {
    return false;
  }

  return Boolean(
    target.closest(".qe-agent-upload-picker-menu") ||
    target.closest("[data-upload-input-id]")
  );
}

export function initComposer(textarea, dotNetRef, dictationButton) {
  if (!textarea || !dotNetRef) {
    return;
  }

  // Dispose the previously-registered composer even when the textarea element
  // was recreated in-place (e.g. returning from the Plugins/Projects/Settings
  // view). disposeComposer is keyed by the textarea element, so
  // disposeComposer(textarea) alone would orphan the old element's
  // document-level listeners with a stale composer reference.
  if (activeComposerTextarea && activeComposerTextarea !== textarea) {
    disposeComposer(activeComposerTextarea);
  }

  disposeComposer(textarea);
  composerDotNetRefs.set(textarea, dotNetRef);

  const keydown = event => {
    if (event.key !== "Enter" || event.isComposing) {
      return;
    }

    if ((event.ctrlKey || event.metaKey) && !event.altKey) {
      event.preventDefault();
      insertTextAtSelection(textarea, "\n");
      return;
    }

    if (!event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
      event.preventDefault();
      textarea.dispatchEvent(new Event("input", { bubbles: true }));
      window.requestAnimationFrame(() => {
        dotNetRef.invokeMethodAsync("SubmitComposerFromKeyboardAsync");
      });
    }
  };

  const input = () => {
    normalizeCaretAfterInput(textarea);
    updateComposerShape(textarea);
  };

  let holdTimer = 0;
  let holdStarted = false;
  let keyboardHoldStarted = false;
  let keyboardHoldPending = false;
  let keyboardHoldTimer = 0;
  let pressStartedActive = false;
  let suppressClick = false;
  const clearKeyboardHold = () => {
    window.clearTimeout(keyboardHoldTimer);
    keyboardHoldTimer = 0;
    keyboardHoldPending = false;
  };
  const beginKeyboardDictationHoldAsync = async () => {
    if (dictationButton?.disabled || isDictationActive(textarea)) {
      return;
    }

    keyboardHoldStarted = true;
    suppressClick = true;
    await startDictationFromUserAction(textarea, dictationButton, dotNetRef);
  };
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
  const documentKeydown = async event => {
    if (!isSpaceKey(event) ||
        event.altKey ||
        event.ctrlKey ||
        event.metaKey ||
        dictationButton?.disabled ||
        !shouldHandleComposerShortcut(event, textarea)) {
      return;
    }

    const isComposerTextInput = event.target === textarea;
    if (!isComposerTextInput) {
      event.preventDefault();
    }

    if (event.repeat) {
      if (keyboardHoldPending || keyboardHoldStarted) {
        event.preventDefault();
      }
      return;
    }

    if (isDictationActive(textarea) || keyboardHoldPending || keyboardHoldStarted) {
      return;
    }

    keyboardHoldPending = true;
    window.clearTimeout(keyboardHoldTimer);
    keyboardHoldTimer = window.setTimeout(async () => {
      keyboardHoldTimer = 0;
      if (!keyboardHoldPending) {
        return;
      }

      keyboardHoldPending = false;
      await beginKeyboardDictationHoldAsync();
    }, KeyboardDictationHoldDelayMs);
  };
  const documentKeyup = async event => {
    if (!isSpaceKey(event)) {
      return;
    }

    if (keyboardHoldPending) {
      clearKeyboardHold();
      return;
    }

    if (!keyboardHoldStarted) {
      return;
    }

    event.preventDefault();
    keyboardHoldStarted = false;
    await finishDictationFromUserAction(textarea, dotNetRef);
  };
  const composer = textarea.closest?.(".qe-agent-composer");
  const outsideQuickActionsPointerDown = event => {
    if (isUploadMenuInteraction(event.target)) {
      return;
    }

    dotNetRef.invokeMethodAsync("CloseQuickActionsMenuFromOutsideAsync");
  };
  // Ctrl/Cmd+K opens the command palette. Capture phase so the loader's
  // focus-the-search fallback (a window bubble listener) never also fires.
  const commandPaletteKeydown = event => {
    if (!(event.ctrlKey || event.metaKey) || event.shiftKey || event.altKey) {
      return;
    }
    if (event.key !== "k" && event.key !== "K") {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    dotNetRef.invokeMethodAsync("OpenCommandPaletteAsync");
  };

  textarea.addEventListener("keydown", keydown);
  textarea.addEventListener("input", input);
  document.addEventListener("keydown", commandPaletteKeydown, true);
  document.addEventListener("keydown", documentKeydown, true);
  document.addEventListener("keyup", documentKeyup, true);
  document.addEventListener("pointerdown", outsideQuickActionsPointerDown, true);
  if (dictationButton) {
    dictationButton.addEventListener("pointerdown", pointerDown);
    dictationButton.addEventListener("pointerup", pointerUp);
    dictationButton.addEventListener("pointerleave", pointerCancel);
    dictationButton.addEventListener("pointercancel", pointerCancel);
    dictationButton.addEventListener("click", click);
    dictationButtonHandlers.set(textarea, { dictationButton, pointerDown, pointerUp, pointerCancel, click });
  }

  const resizeObserver = composer && window.ResizeObserver
    ? new ResizeObserver(() => {
      updateComposerExpansionOffset(textarea);
      updateComposerTooltipPlacements(composer);
    })
    : null;
  resizeObserver?.observe(composer);

  const mutationObserver = composer
    ? new MutationObserver(() => {
      updateComposerExpansionOffset(textarea);
      updateComposerTooltipPlacements(composer);
    })
    : null;
  mutationObserver?.observe(composer, { attributes: true, attributeFilter: ["class"] });

  const updateTooltipPlacement = event => {
    const wrapper = event.target?.closest?.(".qe-agent-composer-tooltip");
    if (wrapper && composer?.contains(wrapper)) {
      updateComposerTooltipPlacement(wrapper);
    }
  };
  const updateAllTooltipPlacements = () => updateComposerTooltipPlacements(composer);
  if (composer) {
    composer.addEventListener("pointerenter", updateTooltipPlacement, true);
    composer.addEventListener("focusin", updateTooltipPlacement);
    window.addEventListener("resize", updateAllTooltipPlacements);
    updateComposerTooltipPlacements(composer);
  }

  composerHandlers.set(textarea, {
    keydown,
    input,
    commandPaletteKeydown,
    documentKeydown,
    documentKeyup,
    outsideQuickActionsPointerDown,
    resizeObserver,
    mutationObserver,
    composer,
    updateTooltipPlacement,
    updateAllTooltipPlacements,
    clearKeyboardHold
  });
  activeComposerTextarea = textarea;
  updateComposerShape(textarea);
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
  document.removeEventListener("keydown", handlers.commandPaletteKeydown, true);
  document.removeEventListener("keydown", handlers.documentKeydown, true);
  document.removeEventListener("keyup", handlers.documentKeyup, true);
  document.removeEventListener("pointerdown", handlers.outsideQuickActionsPointerDown, true);
  handlers.clearKeyboardHold?.();
  handlers.resizeObserver?.disconnect?.();
  handlers.mutationObserver?.disconnect?.();
  if (handlers.composer) {
    handlers.composer.removeEventListener("pointerenter", handlers.updateTooltipPlacement, true);
    handlers.composer.removeEventListener("focusin", handlers.updateTooltipPlacement);
  }
  window.removeEventListener("resize", handlers.updateAllTooltipPlacements);
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
  if (activeComposerTextarea === textarea) {
    activeComposerTextarea = null;
  }
}

function isSpaceKey(event) {
  return event.code === "Space" || event.key === " " || event.key === "Spacebar";
}

function shouldHandleComposerShortcut(event, textarea) {
  const target = event.target;
  if (!target || target === document.body || target === document.documentElement) {
    return true;
  }

  if (target === textarea) {
    return true;
  }

  return !isEditableTarget(target);
}

function isEditableTarget(target) {
  if (!target) {
    return false;
  }

  if (target.isContentEditable) {
    return true;
  }

  const tag = target.tagName?.toLowerCase?.();
  if (tag === "textarea" || tag === "select") {
    return true;
  }

  if (tag !== "input") {
    return false;
  }

  const type = (target.getAttribute("type") || "text").toLowerCase();
  return !["button", "checkbox", "color", "file", "hidden", "image", "radio", "range", "reset", "submit"].includes(type);
}

function insertTextAtSelection(textarea, text) {
  const start = textarea.selectionStart ?? textarea.value.length;
  const end = textarea.selectionEnd ?? start;
  textarea.value = `${textarea.value.slice(0, start)}${text}${textarea.value.slice(end)}`;
  const next = start + text.length;
  setTextareaSelection(textarea, next, next);
  textarea.dispatchEvent(new Event("input", { bubbles: true }));
  normalizeCaretAfterInput(textarea);
  updateComposerShape(textarea);
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

  const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SpeechRecognition) {
    throw new Error("Speech recognition is not supported in this browser.");
  }

  const languages = resolveSpeechRecognitionLanguages(textarea);
  const session = {
    after: textarea.value.slice(insertion.end),
    before: textarea.value.slice(0, insertion.start),
    button: dictationButton || dictationButtonHandlers.get(textarea)?.dictationButton || null,
    dotNetStopRequested: false,
    finalTranscript: "",
    interimTranscript: "",
    languageIndex: 0,
    languages,
    noSpeechError: false,
    recognition: new SpeechRecognition(),
    restartTimer: 0,
    settled: false,
    active: true,
    thaiRetryCount: 0,
    dotNetRef
  };
  dictationSessions.set(textarea, session);
  prepareDictationPreview(textarea, session);

  session.recognition.continuous = true;
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

function updateComposerTooltipPlacements(composer) {
  if (!composer) {
    return;
  }

  composer
    .querySelectorAll(".qe-agent-composer-tooltip")
    .forEach(updateComposerTooltipPlacement);
}

function updateComposerTooltipPlacement(wrapper) {
  const panel = wrapper?.querySelector?.(".qe-agent-tooltip-panel");
  if (!wrapper || !panel) {
    return;
  }

  const wrapperRect = wrapper.getBoundingClientRect();
  const panelRect = panel.getBoundingClientRect();
  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 0;
  const panelHeight = Math.max(panelRect.height, 28);
  const margin = 14;
  const spaceBelow = viewportHeight - wrapperRect.bottom;

  wrapper.dataset.tooltipPlacement = spaceBelow >= panelHeight + margin ? "bottom" : "top";
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


export function isSpeechRecognitionSupported() {
  return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
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
