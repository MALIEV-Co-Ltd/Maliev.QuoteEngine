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
      if (textarea.form?.requestSubmit) {
        textarea.form.requestSubmit();
        return;
      }

      dotNetRef.invokeMethodAsync("SubmitComposerFromKeyboardAsync");
    });
  };

  const pointerdown = () => clearTextSelection();
  const focus = () => clearTextSelection();
  const windowFocus = () => focusComposer(textarea);

  textarea.addEventListener("keydown", keydown);
  textarea.addEventListener("pointerdown", pointerdown);
  textarea.addEventListener("focus", focus);
  window.addEventListener("focus", windowFocus);
  composerHandlers.set(textarea, { keydown, pointerdown, focus, windowFocus });
  focusComposer(textarea);
}

export function disposeComposer(textarea) {
  const handlers = composerHandlers.get(textarea);
  if (!textarea || !handlers) {
    return;
  }

  textarea.removeEventListener("keydown", handlers.keydown);
  textarea.removeEventListener("pointerdown", handlers.pointerdown);
  textarea.removeEventListener("focus", handlers.focus);
  window.removeEventListener("focus", handlers.windowFocus);
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

function clearTextSelection() {
  const selection = window.getSelection?.();
  if (selection && selection.rangeCount > 0) {
    selection.removeAllRanges();
  }
}

function wait(milliseconds) {
  return new Promise(resolve => window.setTimeout(resolve, milliseconds));
}
