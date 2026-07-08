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
