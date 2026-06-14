const composerHandlers = new WeakMap();

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

  textarea.addEventListener("keydown", keydown);
  textarea.addEventListener("pointerdown", pointerdown);
  textarea.addEventListener("focus", focus);
  composerHandlers.set(textarea, { keydown, pointerdown, focus });
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
  composerHandlers.delete(textarea);
}

export function focusComposer(textarea) {
  if (!textarea) {
    return;
  }

  clearTextSelection();
  textarea.focus({ preventScroll: true });
}

function clearTextSelection() {
  const selection = window.getSelection?.();
  if (selection && !selection.isCollapsed) {
    selection.removeAllRanges();
  }
}
