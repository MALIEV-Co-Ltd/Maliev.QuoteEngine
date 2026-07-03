(function () {
    const gapiScriptUrl = "https://apis.google.com/js/api.js";
    const identityScriptUrl = "https://accounts.google.com/gsi/client";

    let pickerLoadPromise = null;
    let identityLoadPromise = null;

    function loadScript(src, isReady) {
        if (isReady()) {
            return Promise.resolve();
        }

        return new Promise((resolve, reject) => {
            const existing = Array.from(document.scripts).find(script => script.src === src);
            const script = existing || document.createElement("script");

            const onLoad = () => {
                script.removeEventListener("error", onError);
                resolve();
            };
            const onError = () => {
                script.removeEventListener("load", onLoad);
                reject(new Error(`Failed to load ${src}.`));
            };

            script.addEventListener("load", onLoad, { once: true });
            script.addEventListener("error", onError, { once: true });

            if (!existing) {
                script.src = src;
                script.async = true;
                script.defer = true;
                document.head.appendChild(script);
            }
            else if (isReady()) {
                onLoad();
            }
        });
    }

    function loadPicker() {
        if (pickerLoadPromise) {
            return pickerLoadPromise;
        }

        pickerLoadPromise = loadScript(gapiScriptUrl, () => Boolean(window.gapi?.load))
            .then(() => new Promise((resolve, reject) => {
                window.gapi.load("picker", {
                    callback: resolve,
                    onerror: () => reject(new Error("Google Picker could not be loaded."))
                });
            }));
        return pickerLoadPromise;
    }

    function loadIdentity() {
        if (identityLoadPromise) {
            return identityLoadPromise;
        }

        identityLoadPromise = loadScript(
            identityScriptUrl,
            () => Boolean(window.google?.accounts?.oauth2));
        return identityLoadPromise;
    }

    function requestTokenWithPrompt(config, prompt) {
        return new Promise((resolve, reject) => {
            const tokenClient = google.accounts.oauth2.initTokenClient({
                client_id: config.clientId,
                scope: config.scope,
                callback: response => {
                    if (response?.error) {
                        reject(new Error(response.error));
                        return;
                    }

                    if (!response?.access_token) {
                        reject(new Error("Google did not return an access token."));
                        return;
                    }

                    resolve(response.access_token);
                }
            });

            tokenClient.requestAccessToken({ prompt });
        });
    }

    async function requestAccessToken(config) {
        try {
            return await requestTokenWithPrompt(config, "");
        }
        catch {
            return await requestTokenWithPrompt(config, "consent");
        }
    }

    function readPickerValue(doc, key, fallbackName) {
        const pickerDocument = google.picker.Document;
        return doc[pickerDocument?.[key]] ?? doc[fallbackName] ?? null;
    }

    function mapPickerDocument(doc) {
        const rawSize = readPickerValue(doc, "SIZE_BYTES", "sizeBytes");
        const sizeBytes = Number.parseInt(rawSize, 10);
        return {
            id: readPickerValue(doc, "ID", "id") ?? "",
            name: readPickerValue(doc, "NAME", "name"),
            mimeType: readPickerValue(doc, "MIME_TYPE", "mimeType"),
            sizeBytes: Number.isFinite(sizeBytes) ? sizeBytes : null,
            webViewLink: readPickerValue(doc, "URL", "url") ?? doc.webViewLink ?? null
        };
    }

    function showPicker(config, accessToken) {
        return new Promise((resolve, reject) => {
            try {
                const docsView = new google.picker.DocsView(google.picker.ViewId.DOCS)
                    .setIncludeFolders(false)
                    .setSelectFolderEnabled(false);

                const picker = new google.picker.PickerBuilder()
                    .enableFeature(google.picker.Feature.MULTISELECT_ENABLED)
                    .setAppId(config.appId)
                    .setDeveloperKey(config.developerKey)
                    .setOAuthToken(accessToken)
                    .setOrigin(`${window.location.protocol}//${window.location.host}`)
                    .addView(docsView)
                    .setCallback(data => {
                        const action = data[google.picker.Response.ACTION];
                        if (action === google.picker.Action.PICKED) {
                            const documents = data[google.picker.Response.DOCUMENTS] ?? [];
                            resolve(documents.map(mapPickerDocument).filter(file => file.id));
                            return;
                        }

                        if (action === google.picker.Action.CANCEL) {
                            resolve([]);
                        }
                    })
                    .build();

                picker.setVisible(true);
            }
            catch (error) {
                reject(error);
            }
        });
    }

    async function openPicker(config) {
        if (!config?.clientId || !config?.developerKey || !config?.appId || !config?.scope) {
            throw new Error("Google Drive Picker configuration is incomplete.");
        }

        await Promise.all([loadPicker(), loadIdentity()]);
        const accessToken = await requestAccessToken(config);
        return await showPicker(config, accessToken);
    }

    window.quoteGoogleDrivePicker = {
        openPicker
    };
})();
