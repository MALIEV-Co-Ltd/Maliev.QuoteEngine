// Behavioral regression tests for the composer "+" upload picker.
//
// Guards the fix for the recurring bug where "+ -> 3D files" opened no file
// picker and the menu just closed silently after visiting a management view
// (Plugins/Projects/Settings). Root cause: initComposer leaked the previous
// textarea's document-level pointerdown listener (with a stale composer
// reference) across an in-place composer re-render; that stale listener then
// treated an in-menu click as "outside" and closed the menu before the click
// could open the native file picker.
//
// These tests execute the REAL initComposer/disposeComposer/isUploadMenuInteraction
// against a tiny hand-rolled DOM (no jsdom / no npm deps) so they run under
// `node --test` from the xUnit suite. They must stay BEHAVIORAL: assert what
// happens on a click, never just that a source string exists.

import test from "node:test";
import assert from "node:assert/strict";

import {
  initComposer,
  disposeComposer,
  isUploadMenuInteraction
} from "../wwwroot/js/quote-agent-composer.js";

// --- Minimal fake DOM -------------------------------------------------------
// initComposer only touches document/window listeners plus updateComposerShape,
// which early-returns when the textarea has no .qe-agent-composer ancestor. So a
// textarea whose closest() returns null keeps the surface tiny.

function makeFakeDocument() {
  const listeners = [];
  return {
    addEventListener(type, fn, opts) {
      listeners.push({ type, fn, capture: opts === true || (opts && opts.capture) || false });
    },
    removeEventListener(type, fn, opts) {
      const capture = opts === true || (opts && opts.capture) || false;
      const i = listeners.findIndex(l => l.type === type && l.fn === fn && l.capture === capture);
      if (i >= 0) listeners.splice(i, 1);
    },
    getElementById() { return null; },
    dispatchCapture(type, event) {
      for (const l of listeners.filter(l => l.type === type && l.capture)) {
        l.fn(event);
      }
    },
    count(type) {
      return listeners.filter(l => l.type === type && l.capture).length;
    }
  };
}

const fakeWindow = {
  addEventListener() {},
  removeEventListener() {},
  clearTimeout() {},
  setTimeout() { return 0; },
  requestAnimationFrame() { return 0; }
};

function makeTextarea() {
  return {
    closest() { return null; }, // no .qe-agent-composer ancestor -> shape update is a no-op
    addEventListener() {},
    removeEventListener() {},
    value: ""
  };
}

// A click target whose closest() matches the given selector fragments.
function makeTarget(...matchFragments) {
  return { closest: sel => (matchFragments.some(m => sel.includes(m)) ? {} : null) };
}

function makeDotNetRef() {
  const calls = [];
  return {
    calls,
    invokeMethodAsync(name) { calls.push(name); return Promise.resolve(); }
  };
}

function makeClassList() {
  const values = new Set();
  return {
    add(...names) { names.forEach(name => values.add(name)); },
    remove(...names) { names.forEach(name => values.delete(name)); },
    contains(name) { return values.has(name); },
    toggle(name, force) {
      const enabled = force === undefined ? !values.has(name) : Boolean(force);
      if (enabled) values.add(name);
      else values.delete(name);
      return enabled;
    }
  };
}

function makeStyle() {
  const values = new Map();
  return {
    setProperty(name, value) { values.set(name, value); },
    removeProperty(name) { values.delete(name); },
    getPropertyValue(name) { return values.get(name) || ""; }
  };
}

function makeVoiceComposer() {
  let overlay = null;
  return {
    classList: makeClassList(),
    dataset: {},
    style: makeStyle(),
    addEventListener() {},
    removeEventListener() {},
    contains() { return false; },
    getBoundingClientRect() { return { height: 58 }; },
    querySelector(selector) {
      return selector === ".qe-agent-dictation-preview" && !overlay?.removed ? overlay : null;
    },
    querySelectorAll() { return []; },
    setAttribute() {},
    removeAttribute() {},
    setOverlay(element) {
      overlay = element;
      element.remove = () => { element.removed = true; };
    }
  };
}

function makeVoiceTextarea(composer) {
  const textarea = {
    classList: makeClassList(),
    dataset: { speechLanguages: "en-US" },
    lang: "en-US",
    selectionEnd: 0,
    selectionStart: 0,
    value: "",
    closest(selector) { return selector === ".qe-agent-composer" ? composer : null; },
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent() {},
    focus() { document.activeElement = textarea; },
    insertAdjacentElement(_position, element) { composer.setOverlay(element); },
    setSelectionRange(start, end) {
      textarea.selectionStart = start;
      textarea.selectionEnd = end;
    }
  };
  return textarea;
}

function makeVoiceTrigger(composer) {
  const listeners = new Map();
  const trigger = {
    capturedPointers: [],
    classList: makeClassList(),
    closestCalls: 0,
    disabled: false,
    releasedPointers: [],
    addEventListener(type, listener) {
      const entries = listeners.get(type) || [];
      entries.push(listener);
      listeners.set(type, entries);
    },
    removeEventListener(type, listener) {
      const entries = listeners.get(type) || [];
      listeners.set(type, entries.filter(entry => entry !== listener));
    },
    listenerCount(type) { return (listeners.get(type) || []).length; },
    closest(selector) {
      trigger.closestCalls += 1;
      return selector === ".qe-agent-composer" ? composer : null;
    },
    setPointerCapture(pointerId) { trigger.capturedPointers.push(pointerId); },
    releasePointerCapture(pointerId) { trigger.releasedPointers.push(pointerId); },
    async dispatch(type, event = {}) {
      event.currentTarget = trigger;
      event.target ??= trigger;
      event.preventDefault ??= () => { event.defaultPrevented = true; };
      for (const listener of [...(listeners.get(type) || [])]) {
        await listener(event);
      }
      return event;
    }
  };
  return trigger;
}

function makeVoiceWindow() {
  let nextTimerId = 1;
  const timers = new Map();
  class FakeSpeechRecognition {
    abort() { this.aborted = true; }
    start() { this.started = true; }
    stop() { this.stopped = true; }
  }

  return {
    SpeechRecognition: FakeSpeechRecognition,
    addEventListener() {},
    removeEventListener() {},
    cancelAnimationFrame() {},
    clearTimeout(id) { timers.delete(id); },
    getSelection() { return null; },
    requestAnimationFrame() { return 0; },
    setTimeout(callback) {
      const id = nextTimerId++;
      timers.set(id, callback);
      return id;
    },
    async runTimers() {
      const callbacks = [...timers.values()];
      timers.clear();
      for (const callback of callbacks) await callback();
    }
  };
}

function installDom() {
  const doc = makeFakeDocument();
  globalThis.document = doc;
  globalThis.window = fakeWindow;
  return doc;
}

function teardownDom() {
  delete globalThis.document;
  delete globalThis.window;
}

function installVoiceDom() {
  const doc = makeFakeDocument();
  doc.activeElement = null;
  doc.body = { appendChild() {} };
  doc.documentElement = { lang: "en-US" };
  doc.createElement = tagName => ({
    append(...children) { this.children.push(...children); },
    children: [],
    className: "",
    innerHTML: "",
    removed: false,
    setAttribute() {},
    style: makeStyle(),
    tagName,
    textContent: ""
  });

  const voiceWindow = makeVoiceWindow();
  globalThis.document = doc;
  globalThis.window = voiceWindow;
  globalThis.MutationObserver = class {
    disconnect() {}
    observe() {}
  };
  globalThis.getComputedStyle = () => ({
    getPropertyValue(name) { return name === "--qe-composer-collapsed-height" ? "58" : ""; },
    gridTemplateColumns: "",
    lineHeight: "24px"
  });
  return { doc, voiceWindow };
}

function teardownVoiceDom() {
  teardownDom();
  delete globalThis.MutationObserver;
  delete globalThis.getComputedStyle;
}

// --- Tests ------------------------------------------------------------------

test("isUploadMenuInteraction: a click inside the menu or on a trigger is never 'outside'", () => {
  assert.equal(isUploadMenuInteraction(makeTarget(".qe-agent-upload-picker-menu")), true);
  assert.equal(isUploadMenuInteraction(makeTarget("[data-upload-input-id]")), true);
  assert.equal(isUploadMenuInteraction(makeTarget(".something-else")), false);
  assert.equal(isUploadMenuInteraction(null), false);
  assert.equal(isUploadMenuInteraction({}), false);
});

test("initComposer does not leak document listeners when the textarea is recreated in place", () => {
  const doc = installDom();
  try {
    const dotNet = makeDotNetRef();

    const first = makeTextarea();
    initComposer(first, dotNet, null);
    assert.equal(doc.count("pointerdown"), 1, "one composer -> one pointerdown listener");
    assert.equal(doc.count("keydown"), 2, "one composer -> two keydown listeners");

    // Simulate an in-place recreation (returning from the Plugins/Projects view):
    // a brand-new textarea element, same as Blazor's else-if re-init path.
    const recreated = makeTextarea();
    initComposer(recreated, dotNet, null);
    assert.equal(doc.count("pointerdown"), 1, "recreation must not leak a stale pointerdown listener");
    assert.equal(doc.count("keydown"), 2, "recreation must not leak stale keydown listeners");

    disposeComposer(recreated);
    assert.equal(doc.count("pointerdown"), 0, "dispose removes the remaining listener");
  } finally {
    teardownDom();
  }
});

test("a pointerdown inside the upload menu keeps the menu open (so the picker click survives), even after recreation", () => {
  const doc = installDom();
  try {
    const dotNet = makeDotNetRef();

    // Recreate once so any stale listener from before the fix would still be attached.
    initComposer(makeTextarea(), dotNet, null);
    const current = makeTextarea();
    initComposer(current, dotNet, null);

    const closeCalls = () => dotNet.calls.filter(c => c === "CloseQuickActionsMenuFromOutsideAsync").length;

    // Clicking a menu item must NOT close the menu -- otherwise the <label> is
    // removed before the click reaches the native file-picker trigger.
    doc.dispatchCapture("pointerdown", { target: makeTarget(".qe-agent-upload-picker-menu") });
    assert.equal(closeCalls(), 0, "in-menu pointerdown must not close the upload menu");

    // A genuine click outside the menu still closes it.
    doc.dispatchCapture("pointerdown", { target: makeTarget(".qe-agent-workspace") });
    assert.equal(closeCalls(), 1, "outside pointerdown still closes the menu");

    disposeComposer(current);
  } finally {
    teardownDom();
  }
});

test("primary dictation and optional voice triggers both toggle one canonical dictation session", async () => {
  const { voiceWindow } = installVoiceDom();
  const composer = makeVoiceComposer();
  const textarea = makeVoiceTextarea(composer);
  const primary = makeVoiceTrigger(composer);
  const voice = makeVoiceTrigger(composer);
  const dotNet = makeDotNetRef();

  try {
    initComposer(textarea, dotNet, primary, voice);
    assert.equal(primary.listenerCount("click"), 1, "primary trigger is wired");
    assert.equal(voice.listenerCount("click"), 1, "optional voice trigger is wired");

    await primary.dispatch("click");
    assert.equal(dotNet.calls.filter(call => call === "DictationStartedAsync").length, 1);
    await primary.dispatch("click");
    assert.equal(dotNet.calls.filter(call => call === "BeginDictationProcessingAsync").length, 1);

    await voice.dispatch("click");
    assert.equal(dotNet.calls.filter(call => call === "DictationStartedAsync").length, 2);
    assert.ok(primary.closestCalls > 0, "meter/state stays anchored to the canonical dictation button");
    assert.equal(voice.closestCalls, 0, "secondary trigger is not used as the meter/state owner");
    await voice.dispatch("click");
    assert.equal(dotNet.calls.filter(call => call === "BeginDictationProcessingAsync").length, 2);

    await voice.dispatch("pointerdown", { button: 0, pointerId: 41 });
    await voice.dispatch("pointerup", { pointerId: 41 });
    await voice.dispatch("click");

    assert.deepEqual(voice.capturedPointers, [41], "the pressed voice trigger owns pointer capture");
    assert.deepEqual(voice.releasedPointers, [41], "the pressed voice trigger releases pointer capture");
    assert.equal(primary.capturedPointers.length, 0, "the canonical button must not capture the voice trigger pointer");
    assert.equal(dotNet.calls.filter(call => call === "DictationStartedAsync").length, 3);
    await voice.dispatch("click");
    assert.equal(dotNet.calls.filter(call => call === "BeginDictationProcessingAsync").length, 3);
    assert.equal(dotNet.calls.filter(call => call === "CompleteDictationAsync").length, 3);
    await voiceWindow.runTimers();
  } finally {
    disposeComposer(textarea);
    teardownVoiceDom();
  }
});

test("holding either voice trigger dictates only while the pointer is held", async () => {
  const { voiceWindow } = installVoiceDom();
  const composer = makeVoiceComposer();
  const textarea = makeVoiceTextarea(composer);
  const primary = makeVoiceTrigger(composer);
  const voice = makeVoiceTrigger(composer);
  const dotNet = makeDotNetRef();

  try {
    initComposer(textarea, dotNet, primary, voice);

    for (const [trigger, pointerId] of [[voice, 61], [primary, 62]]) {
      await trigger.dispatch("pointerdown", { button: 0, pointerId });
      await voiceWindow.runTimers();
      await trigger.dispatch("pointerup", { pointerId });
      await trigger.dispatch("click");
    }

    assert.equal(dotNet.calls.filter(call => call === "DictationStartedAsync").length, 2);
    assert.equal(dotNet.calls.filter(call => call === "BeginDictationProcessingAsync").length, 2);
    assert.equal(dotNet.calls.filter(call => call === "CompleteDictationAsync").length, 2);
    assert.deepEqual(voice.capturedPointers, [61]);
    assert.deepEqual(primary.capturedPointers, [62]);
  } finally {
    disposeComposer(textarea);
    teardownVoiceDom();
  }
});

test("disposeComposer removes voice handlers from every registered trigger", async () => {
  installDom();
  const textarea = makeTextarea();
  const primary = makeVoiceTrigger(null);
  const voice = makeVoiceTrigger(null);
  const dotNet = makeDotNetRef();

  try {
    initComposer(textarea, dotNet, primary, voice);
    for (const type of ["pointerdown", "pointerup", "pointerleave", "pointercancel", "click"]) {
      assert.equal(primary.listenerCount(type), 1, `primary ${type} handler is registered`);
      assert.equal(voice.listenerCount(type), 1, `voice ${type} handler is registered`);
    }

    disposeComposer(textarea);

    for (const type of ["pointerdown", "pointerup", "pointerleave", "pointercancel", "click"]) {
      assert.equal(primary.listenerCount(type), 0, `primary ${type} handler is disposed`);
      assert.equal(voice.listenerCount(type), 0, `voice ${type} handler is disposed`);
    }

    await primary.dispatch("click");
    await voice.dispatch("click");
    assert.deepEqual(dotNet.calls, [], "disposed triggers cannot call .NET");
  } finally {
    disposeComposer(textarea);
    teardownDom();
  }
});

test("initComposer remains backward-compatible when the optional voice trigger is absent", () => {
  installDom();
  const textarea = makeTextarea();
  const primary = makeVoiceTrigger(null);

  try {
    initComposer(textarea, makeDotNetRef(), primary);
    assert.equal(primary.listenerCount("click"), 1);
    disposeComposer(textarea);
    assert.equal(primary.listenerCount("click"), 0);
  } finally {
    disposeComposer(textarea);
    teardownDom();
  }
});
