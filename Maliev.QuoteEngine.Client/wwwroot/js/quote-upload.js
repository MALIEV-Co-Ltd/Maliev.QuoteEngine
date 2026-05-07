window.quoteEngineUploads = (() => {
  const fileMap = new Map();

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

  return { captureFiles, openFilePicker, uploadFile };
})();
