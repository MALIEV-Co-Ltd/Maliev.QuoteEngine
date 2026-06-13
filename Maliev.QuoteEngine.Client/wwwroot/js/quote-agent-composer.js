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
    dotNetRef.invokeMethodAsync("SubmitComposerFromKeyboardAsync");
  };

  textarea.addEventListener("keydown", keydown);
  composerHandlers.set(textarea, keydown);
}

export function disposeComposer(textarea) {
  const keydown = composerHandlers.get(textarea);
  if (!textarea || !keydown) {
    return;
  }

  textarea.removeEventListener("keydown", keydown);
  composerHandlers.delete(textarea);
}
