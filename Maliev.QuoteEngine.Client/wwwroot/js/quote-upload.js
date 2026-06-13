window.quoteEngineUploads = (() => {
  const fileMap = new Map();
  const objectUrlMap = new Map();
  const dropzoneMap = new Map();
  const clearTimerMap = new Map();
  const pendingUploadIds = new Set();
  const activeUploadIds = new Set();
  const fileRetentionMs = 10 * 60 * 1000;
  let lastCaptureDiagnostics = null;
  let lastClearDiagnostics = null;

  function normalizeFile(fileLike) {
    if (!fileLike) {
      return null;
    }

    const blob = fileLike instanceof Blob ? fileLike : fileLike.blob || fileLike.file;
    if (!blob) {
      return null;
    }

    return {
      blob,
      name: fileLike.name || blob.name || "",
      size: Number(fileLike.size ?? blob.size ?? 0),
      type: fileLike.type || fileLike.contentType || blob.type || ""
    };
  }

  function getCapturedInputFiles(input) {
    const inputFiles = Array.from(input.files || [])
      .map(normalizeFile)
      .filter(Boolean);
    if (inputFiles.length > 0) {
      return inputFiles;
    }

    const blazorFiles = input._blazorFilesById ? Object.values(input._blazorFilesById) : [];
    return blazorFiles
      .map(normalizeFile)
      .filter(Boolean);
  }

  function captureFiles(inputId, mappings) {
    const input = document.getElementById(inputId);
    if (!input) {
      lastCaptureDiagnostics = { inputId, inputFound: false };
      return;
    }

    const capturedFiles = getCapturedInputFiles(input);
    const mappingList = Array.from(mappings || []);
    const matchedClientFileIds = [];
    for (const mapping of mappingList) {
      const match = capturedFiles.find(file =>
        file.name === mapping.fileName && file.size === Number(mapping.fileSizeBytes));
      if (match) {
        const existingTimer = clearTimerMap.get(mapping.clientFileId);
        if (existingTimer) clearTimeout(existingTimer);
        clearTimerMap.delete(mapping.clientFileId);
        fileMap.set(mapping.clientFileId, match);
        pendingUploadIds.add(mapping.clientFileId);
        matchedClientFileIds.push(mapping.clientFileId);
      }
    }

    lastCaptureDiagnostics = {
      inputId,
      inputFound: true,
      inputFileCount: input.files?.length || 0,
      inputFiles: Array.from(input.files || []).map(file => ({ name: file.name, size: file.size, type: file.type || "" })),
      blazorFileCount: input._blazorFilesById ? Object.keys(input._blazorFilesById).length : 0,
      blazorFileKeys: input._blazorFilesById ? Object.keys(input._blazorFilesById) : [],
      capturedFiles: capturedFiles.map(file => ({ name: file.name, size: file.size, type: file.type || "" })),
      pendingUploadIds: Array.from(pendingUploadIds),
      activeUploadIds: Array.from(activeUploadIds),
      mappings: mappingList.map(mapping => ({
        clientFileId: mapping.clientFileId,
        fileName: mapping.fileName,
        fileSizeBytes: mapping.fileSizeBytes
      })),
      matchedClientFileIds,
      knownClientFileIds: Array.from(fileMap.keys())
    };
  }

  function openFilePicker(inputId) {
    document.getElementById(inputId)?.click();
  }

  function registerDropzone(dropzoneId, inputId, dotNetRef, options) {
    const dropzone = document.getElementById(dropzoneId);
    const input = document.getElementById(inputId);
    if (!dropzone || !input || !dotNetRef) {
      return;
    }

    unregisterDropzone(dropzoneId);
    const registrationOptions = options || {};
    const clickToOpen = registrationOptions.clickToOpen !== false;

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
      event.stopPropagation();
      dropzone.classList.remove("is-dragover");
      if (!event.dataTransfer?.files?.length) {
        return;
      }

      const files = Array.from(event.dataTransfer.files).map(file => {
        const clientFileId = createClientFileId();
        const storedFile = normalizeFile(file);
        fileMap.set(clientFileId, storedFile);
        pendingUploadIds.add(clientFileId);
        return {
          clientFileId,
          fileName: file.name,
          contentType: file.type || "application/octet-stream",
          fileSizeBytes: file.size
        };
      });

      await dotNetRef.invokeMethodAsync("HandleDroppedFilesAsync", files);
    };

    if (clickToOpen) {
      dropzone.addEventListener("click", openPicker);
    }

    dropzone.addEventListener("dragover", dragOver);
    dropzone.addEventListener("dragleave", dragLeave);
    dropzone.addEventListener("drop", drop);

    dropzoneMap.set(dropzoneId, {
      dropzone,
      openPicker,
      dragOver,
      dragLeave,
      drop,
      clickToOpen
    });
  }

  function unregisterDropzone(dropzoneId) {
    const registration = dropzoneMap.get(dropzoneId);
    if (!registration) {
      return;
    }

    if (registration.clickToOpen) {
      registration.dropzone.removeEventListener("click", registration.openPicker);
    }

    registration.dropzone.removeEventListener("dragover", registration.dragOver);
    registration.dropzone.removeEventListener("dragleave", registration.dragLeave);
    registration.dropzone.removeEventListener("drop", registration.drop);
    dropzoneMap.delete(dropzoneId);
  }

  function createClientFileId() {
    return crypto.randomUUID ? crypto.randomUUID().replace(/-/g, "") : `${Date.now()}${Math.random()}`;
  }

  function uploadBlob(uploadUrl, contentType, file, uploadBody) {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open("PUT", uploadUrl, true);
      xhr.withCredentials = true;
      xhr.setRequestHeader("Content-Type", contentType || file.type || "application/octet-stream");
      xhr.setRequestHeader("Content-Range", `bytes 0-${file.size - 1}/${file.size}`);
      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          resolve();
          return;
        }

        reject(new Error(`Upload failed with HTTP ${xhr.status}.`));
      };
      xhr.onerror = () => reject(new Error("Upload failed before the server returned a response."));
      xhr.onabort = () => reject(new Error("Upload was aborted before completion."));
      xhr.send(uploadBody);
    });
  }

  async function uploadFile(clientFileId, uploadUrl, contentType) {
    const file = fileMap.get(clientFileId);
    if (!file || !file.blob) {
      throw new Error(`The selected browser file is no longer available. ${JSON.stringify({
        clientFileId,
        knownClientFileIds: Array.from(fileMap.keys()),
        pendingUploadIds: Array.from(pendingUploadIds),
        activeUploadIds: Array.from(activeUploadIds),
        lastCapture: lastCaptureDiagnostics,
        lastClear: lastClearDiagnostics
      })}`);
    }

    pendingUploadIds.delete(clientFileId);
    activeUploadIds.add(clientFileId);
    try {
      const uploadBody = typeof file.blob.slice === "function"
        ? file.blob.slice(0, file.size, contentType || file.type || "application/octet-stream")
        : file.blob;
      await uploadBlob(uploadUrl, contentType, file, uploadBody);
    } finally {
      activeUploadIds.delete(clientFileId);
    }
  }

  function clearFile(clientFileId, reason = "explicit") {
    const isUploadRetained = pendingUploadIds.has(clientFileId) || activeUploadIds.has(clientFileId);
    if (isUploadRetained) {
      lastClearDiagnostics = {
        clientFileId,
        reason,
        skipped: true,
        pendingUploadIds: Array.from(pendingUploadIds),
        activeUploadIds: Array.from(activeUploadIds),
        knownClientFileIds: Array.from(fileMap.keys()),
        stack: new Error().stack
      };
      return;
    }

    const existingTimer = clearTimerMap.get(clientFileId);
    if (existingTimer) clearTimeout(existingTimer);
    clearTimerMap.delete(clientFileId);
    const objectUrl = objectUrlMap.get(clientFileId);
    if (objectUrl && typeof URL !== "undefined" && typeof URL.revokeObjectURL === "function") {
      URL.revokeObjectURL(objectUrl);
    }
    objectUrlMap.delete(clientFileId);
    pendingUploadIds.delete(clientFileId);
    activeUploadIds.delete(clientFileId);
    fileMap.delete(clientFileId);
    lastClearDiagnostics = {
      clientFileId,
      reason,
      skipped: false,
      pendingUploadIds: Array.from(pendingUploadIds),
      activeUploadIds: Array.from(activeUploadIds),
      knownClientFileIds: Array.from(fileMap.keys()),
      stack: new Error().stack
    };
  }

  function scheduleClearFile(clientFileId, delayMs) {
    if (!fileMap.has(clientFileId)) {
      return;
    }

    const existingTimer = clearTimerMap.get(clientFileId);
    if (existingTimer) clearTimeout(existingTimer);

    const timer = setTimeout(() => clearFile(clientFileId, "timer"), Number(delayMs) > 0 ? Number(delayMs) : fileRetentionMs);
    clearTimerMap.set(clientFileId, timer);
  }

  async function getFileBytes(clientFileId) {
    const file = fileMap.get(clientFileId);
    if (!file?.blob || typeof file.blob.arrayBuffer !== "function") {
      return null;
    }

    return new Uint8Array(await file.blob.arrayBuffer());
  }

  function getObjectUrl(clientFileId) {
    const file = fileMap.get(clientFileId);
    if (!file?.blob || typeof URL === "undefined" || typeof URL.createObjectURL !== "function") {
      return null;
    }

    let objectUrl = objectUrlMap.get(clientFileId);
    if (!objectUrl) {
      objectUrl = URL.createObjectURL(file.blob);
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
