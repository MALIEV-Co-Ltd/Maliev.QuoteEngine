window.quoteEngineUploads = (() => {
  const fileMap = new Map();
  const objectUrlMap = new Map();
  const dropzoneMap = new Map();
  const pasteTargetMap = new Map();
  const clearTimerMap = new Map();
  const pendingUploadIds = new Set();
  const activeUploadIds = new Set();
  let cadThumbnailModulePromise = null;
  const fileRetentionMs = 10 * 60 * 1000;
  let lastCaptureDiagnostics = null;
  let lastClearDiagnostics = null;
  let openPickerPending = false;

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

  function syncNativeUploadPickerTrigger(event) {
    const trigger = event.target?.closest?.("[data-upload-input-id][data-upload-accept]");
    if (!trigger) {
      return;
    }

    const input = document.getElementById(trigger.dataset.uploadInputId || "");
    if (!input || input.disabled) {
      return;
    }

    event.preventDefault();
    const accept = trigger.dataset.uploadAccept || "";
    if (accept) {
      input.setAttribute("accept", accept);
    } else {
      input.removeAttribute("accept");
    }

    input.click();
  }

  document.addEventListener("click", syncNativeUploadPickerTrigger, true);

  async function openFilePicker(inputId, options, dotNetRef) {
    if (openPickerPending) {
      return;
    }

    const input = document.getElementById(inputId);
    if (!input || input.disabled) {
      return;
    }

    openPickerPending = true;
    const pickerOptions = normalizePickerOptions(options);
    try {
      if (pickerOptions?.types?.length && dotNetRef && window.showOpenFilePicker) {
        const handled = await openNamedFilePicker(pickerOptions, dotNetRef);
        if (handled) {
          return;
        }
      }

      if (pickerOptions?.accept) {
        input.setAttribute("accept", pickerOptions.accept);
      }

      input.click();
    } finally {
      setTimeout(() => {
        openPickerPending = false;
      }, 600);
    }
  }

  function normalizePickerOptions(options) {
    if (!options || typeof options !== "object") {
      return null;
    }

    const types = Array.isArray(options.types)
      ? options.types
        .filter(type => type?.description && type?.accept && typeof type.accept === "object")
        .map(type => ({
          description: String(type.description),
          accept: type.accept
        }))
      : [];

    return {
      accept: typeof options.accept === "string" ? options.accept : "",
      types
    };
  }

  async function openNamedFilePicker(options, dotNetRef) {
    try {
      const handles = await window.showOpenFilePicker({
        multiple: true,
        excludeAcceptAllOption: false,
        types: options.types
      });
      const files = [];
      for (const handle of handles) {
        files.push(await handle.getFile());
      }

      const droppedFiles = files.map(file => storeBrowserFile(file));
      if (droppedFiles.length > 0) {
        await dotNetRef.invokeMethodAsync("HandleDroppedFilesAsync", droppedFiles);
      }

      return true;
    } catch (error) {
      if (error?.name === "AbortError") {
        return true;
      }

      console.warn("Named file picker failed; falling back to input picker.", error);
      return false;
    }
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

  function registerPasteTarget(targetId, dotNetRef) {
    const target = document.getElementById(targetId) || document;
    if (!target || !dotNetRef) {
      return;
    }

    unregisterPasteTarget(targetId);

    const paste = async event => {
      if (shouldIgnorePasteEvent(event)) {
        return;
      }

      const files = filesFromClipboard(event.clipboardData);
      if (files.length === 0) {
        return;
      }

      event.preventDefault();
      const droppedFiles = files.map(file => storeBrowserFile(file));
      await dotNetRef.invokeMethodAsync("HandleDroppedFilesAsync", droppedFiles);
    };

    target.addEventListener("paste", paste);
    pasteTargetMap.set(targetId, { target, paste });
  }

  function shouldIgnorePasteEvent(event) {
    return event.defaultPrevented ||
      !!event.target?.closest?.(".qe-agent-sketch-dialog");
  }

  function unregisterPasteTarget(targetId) {
    const registration = pasteTargetMap.get(targetId);
    if (!registration) {
      return;
    }

    registration.target.removeEventListener("paste", registration.paste);
    pasteTargetMap.delete(targetId);
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

  function storeBrowserFile(file) {
    const clientFileId = createClientFileId();
    const storedFile = normalizeFile(file);
    fileMap.set(clientFileId, storedFile);
    pendingUploadIds.add(clientFileId);
    return {
      clientFileId,
      fileName: storedFile.name || createPastedFileName(storedFile.type),
      contentType: storedFile.type || "application/octet-stream",
      fileSizeBytes: storedFile.size
    };
  }

  function filesFromClipboard(clipboardData) {
    if (!clipboardData) {
      return [];
    }

    const files = Array.from(clipboardData.files || []);
    if (files.length > 0) {
      return files.map(ensurePastedFileName);
    }

    return Array.from(clipboardData.items || [])
      .filter(item => item.kind === "file")
      .map(item => item.getAsFile())
      .filter(Boolean)
      .map(ensurePastedFileName);
  }

  function ensurePastedFileName(file) {
    if (file.name) {
      return file;
    }

    const name = createPastedFileName(file.type);
    try {
      return new File([file], name, { type: file.type || "application/octet-stream", lastModified: Date.now() });
    } catch {
      file.name = name;
      return file;
    }
  }

  function createPastedFileName(contentType) {
    const normalizedType = (contentType || "").toLowerCase();
    const extension = normalizedType.includes("png") ? "png"
      : normalizedType.includes("jpeg") || normalizedType.includes("jpg") ? "jpg"
      : normalizedType.includes("webp") ? "webp"
      : normalizedType.includes("pdf") ? "pdf"
      : "bin";
    const stamp = new Date().toISOString().replace(/[-:]/g, "").replace(/\..+/, "").replace("T", "-");
    return `pasted-quote-file-${stamp}.${extension}`;
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

  const MAX_LOCAL_BYTES = 50 * 1024 * 1024; // Skip local geometry for files > 50 MB

  async function getFileBytes(clientFileId) {
    const file = fileMap.get(clientFileId);
    if (!file?.blob || typeof file.blob.arrayBuffer !== "function") {
      return null;
    }

    if (file.size > MAX_LOCAL_BYTES) {
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

  function getCadThumbnailModule() {
    cadThumbnailModulePromise ??= import('/js/quote-cad-thumbnail.js?v=geometry-runtime-v1');
    return cadThumbnailModulePromise;
  }

  async function generateCadThumbnail(clientFileId, fileName, timeoutMs) {
    const file = fileMap.get(clientFileId);
    if (!file?.blob || file.size > MAX_LOCAL_BYTES) {
      return null;
    }

    const objectUrl = getObjectUrl(clientFileId);
    if (!objectUrl) {
      return null;
    }

    const thumbnailModule = await getCadThumbnailModule();
    return await thumbnailModule.generateThumbnail(objectUrl, {
      fileName,
      fileExtension: String(fileName || '').split('.').pop() || '',
      timeoutMs
    });
  }

  async function generateCadThumbnailFromUrl(fileUrl, fileName, timeoutMs) {
    if (!fileUrl) {
      return null;
    }

    const thumbnailModule = await getCadThumbnailModule();
    return await thumbnailModule.generateThumbnail(fileUrl, {
      fileName,
      fileExtension: String(fileName || '').split('.').pop() || '',
      timeoutMs
    });
  }

  return {
    captureFiles,
    openFilePicker,
    registerDropzone,
    unregisterDropzone,
    registerPasteTarget,
    unregisterPasteTarget,
    uploadFile,
    clearFile,
    scheduleClearFile,
    getFileBytes,
    getObjectUrl,
    generateCadThumbnail,
    generateCadThumbnailFromUrl
  };
})();
