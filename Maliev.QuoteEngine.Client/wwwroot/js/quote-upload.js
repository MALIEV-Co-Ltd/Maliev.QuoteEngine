window.quoteEngineUploads = (() => {
  const fileMap = new Map();
  const objectUrlMap = new Map();
  const dropzoneMap = new Map();
  const clearTimerMap = new Map();
  const fileRetentionMs = 10 * 60 * 1000;

  function captureFiles(inputId, mappings) {
    const input = document.getElementById(inputId);
    if (!input || !input.files) {
      return;
    }

    for (const mapping of mappings) {
      const match = Array.from(input.files).find(file =>
        file.name === mapping.fileName && file.size === mapping.fileSizeBytes);
      if (match) {
        const existingTimer = clearTimerMap.get(mapping.clientFileId);
        if (existingTimer) clearTimeout(existingTimer);
        clearTimerMap.delete(mapping.clientFileId);
        fileMap.set(mapping.clientFileId, match);
      }
    }
  }

  function openFilePicker(inputId) {
    document.getElementById(inputId)?.click();
  }

  function registerDropzone(dropzoneId, inputId, dotNetRef) {
    const dropzone = document.getElementById(dropzoneId);
    const input = document.getElementById(inputId);
    if (!dropzone || !input || !dotNetRef) {
      return;
    }

    unregisterDropzone(dropzoneId);

    const openPicker = event => {
      event.preventDefault();
      input.click();
    };

    const dragOver = event => {
      event.preventDefault();
      dropzone.classList.add("is-dragover");
      event.dataTransfer.dropEffect = "copy";
    };

    const dragLeave = event => {
      if (!dropzone.contains(event.relatedTarget)) {
        dropzone.classList.remove("is-dragover");
      }
    };

    const drop = async event => {
      event.preventDefault();
      dropzone.classList.remove("is-dragover");
      if (!event.dataTransfer?.files?.length) {
        return;
      }

      const files = Array.from(event.dataTransfer.files).map(file => {
        const clientFileId = createClientFileId();
        fileMap.set(clientFileId, file);
        return {
          clientFileId,
          fileName: file.name,
          contentType: file.type || "application/octet-stream",
          fileSizeBytes: file.size
        };
      });

      await dotNetRef.invokeMethodAsync("HandleDroppedFilesAsync", files);
    };

    dropzone.addEventListener("click", openPicker);
    dropzone.addEventListener("dragover", dragOver);
    dropzone.addEventListener("dragleave", dragLeave);
    dropzone.addEventListener("drop", drop);

    dropzoneMap.set(dropzoneId, {
      dropzone,
      openPicker,
      dragOver,
      dragLeave,
      drop
    });
  }

  function unregisterDropzone(dropzoneId) {
    const registration = dropzoneMap.get(dropzoneId);
    if (!registration) {
      return;
    }

    registration.dropzone.removeEventListener("click", registration.openPicker);
    registration.dropzone.removeEventListener("dragover", registration.dragOver);
    registration.dropzone.removeEventListener("dragleave", registration.dragLeave);
    registration.dropzone.removeEventListener("drop", registration.drop);
    dropzoneMap.delete(dropzoneId);
  }

  function createClientFileId() {
    return crypto.randomUUID ? crypto.randomUUID().replace(/-/g, "") : `${Date.now()}${Math.random()}`;
  }

  async function uploadFile(clientFileId, uploadUrl, contentType) {
    const file = fileMap.get(clientFileId);
    if (!file) {
      throw new Error("The selected browser file is no longer available.");
    }

    const response = await fetch(uploadUrl, {
      method: "PUT",
      credentials: "include",
      headers: {
        "Content-Type": contentType || file.type || "application/octet-stream",
        "Content-Range": `bytes 0-${file.size - 1}/${file.size}`
      },
      body: file
    });

    if (!response.ok) {
      throw new Error(`Upload failed with HTTP ${response.status}.`);
    }

    scheduleClearFile(clientFileId);
  }

  function clearFile(clientFileId) {
    const existingTimer = clearTimerMap.get(clientFileId);
    if (existingTimer) clearTimeout(existingTimer);
    clearTimerMap.delete(clientFileId);
    const objectUrl = objectUrlMap.get(clientFileId);
    if (objectUrl && typeof URL !== "undefined" && typeof URL.revokeObjectURL === "function") {
      URL.revokeObjectURL(objectUrl);
    }
    objectUrlMap.delete(clientFileId);
    fileMap.delete(clientFileId);
  }

  function scheduleClearFile(clientFileId, delayMs) {
    if (!fileMap.has(clientFileId)) {
      return;
    }

    const existingTimer = clearTimerMap.get(clientFileId);
    if (existingTimer) clearTimeout(existingTimer);

    const timer = setTimeout(() => clearFile(clientFileId), Number(delayMs) > 0 ? Number(delayMs) : fileRetentionMs);
    clearTimerMap.set(clientFileId, timer);
  }

  async function getFileBytes(clientFileId) {
    const file = fileMap.get(clientFileId);
    if (!file || typeof file.arrayBuffer !== "function") {
      return null;
    }

    return new Uint8Array(await file.arrayBuffer());
  }

  function getObjectUrl(clientFileId) {
    const file = fileMap.get(clientFileId);
    if (!file || typeof URL === "undefined" || typeof URL.createObjectURL !== "function") {
      return null;
    }

    let objectUrl = objectUrlMap.get(clientFileId);
    if (!objectUrl) {
      objectUrl = URL.createObjectURL(file);
      objectUrlMap.set(clientFileId, objectUrl);
    }

    return objectUrl;
  }

  return {
    captureFiles,
    openFilePicker,
    registerDropzone,
    unregisterDropzone,
    uploadFile,
    clearFile,
    scheduleClearFile,
    getFileBytes,
    getObjectUrl
  };
})();
