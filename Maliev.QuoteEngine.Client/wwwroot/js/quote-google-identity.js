const gisSource = "https://accounts.google.com/gsi/client";
let gisLoadPromise;

function loadGoogleIdentityServices() {
    if (globalThis.google?.accounts?.id) {
        return Promise.resolve(globalThis.google.accounts.id);
    }

    if (gisLoadPromise) {
        return gisLoadPromise;
    }

    gisLoadPromise = new Promise((resolve, reject) => {
        const existing = document.querySelector(`script[src="${gisSource}"]`);
        const script = existing ?? document.createElement("script");
        const onReady = () => {
            if (globalThis.google?.accounts?.id) {
                resolve(globalThis.google.accounts.id);
                return;
            }

            reject(new Error("Google Identity Services did not initialize."));
        };

        script.addEventListener("load", onReady, { once: true });
        script.addEventListener("error", () => reject(new Error("Google Identity Services could not be loaded.")), { once: true });
        if (!existing) {
            script.src = gisSource;
            script.async = true;
            script.defer = true;
            document.head.appendChild(script);
        } else if (globalThis.google?.accounts?.id) {
            onReady();
        }
    });

    return gisLoadPromise;
}

export async function renderButton(host, dotNetReference, config, language) {
    if (!host || !config?.clientId || !config?.nonce) {
        throw new Error("Google sign-in configuration is incomplete.");
    }

    const identity = await loadGoogleIdentityServices();
    host.replaceChildren();
    identity.initialize({
        client_id: config.clientId,
        nonce: config.nonce,
        auto_select: false,
        cancel_on_tap_outside: true,
        use_fedcm_for_prompt: true,
        callback: async response => {
            if (!response?.credential) {
                await dotNetReference.invokeMethodAsync(
                    "OnGoogleIdentityError",
                    "Google did not return a sign-in credential.");
                return;
            }

            await dotNetReference.invokeMethodAsync(
                "OnGoogleCredentialReceived",
                response.credential,
                config.nonce);
        }
    });
    identity.renderButton(host, {
        type: "standard",
        theme: "outline",
        size: "large",
        text: "continue_with",
        shape: "rectangular",
        logo_alignment: "left",
        locale: language === "th" ? "th" : "en",
        width: Math.max(240, Math.min(360, Math.round(host.getBoundingClientRect().width || 320)))
    });
}

export function clearButton(host) {
    host?.replaceChildren();
}
