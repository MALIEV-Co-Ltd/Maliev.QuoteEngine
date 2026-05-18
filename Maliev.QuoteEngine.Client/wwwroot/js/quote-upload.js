window.quoteEngineUploads = (() => {
  const fileMap = new Map();
  const dropzoneMap = new Map();

  function captureFiles(inputId, mappings) {
    const input = document.getElementById(inputId);
    if (!input || !input.files) {
      return;
    }

    for (const mapping of mappings) {
      const match = Array.from(input.files).find(file =>
        file.name === mapping.fileName && file.size === mapping.fileSizeBytes);
      if (match) {
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
        const clientFileId = crypto.randomUUID ? crypto.randomUUID().replace(/-/g, "") : `${Date.now()}${Math.random()}`;
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
  }

  return { captureFiles, openFilePicker, registerDropzone, unregisterDropzone, uploadFile };
})();
