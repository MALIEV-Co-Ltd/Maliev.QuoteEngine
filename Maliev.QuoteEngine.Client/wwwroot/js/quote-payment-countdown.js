// Live countdown for the in-chat PromptPay QR. Updates the countdown text every second directly in
// the DOM (no per-second Blazor re-render of the large shell component). Idempotent: start() only
// installs a single interval; it scans for [data-qr-expires-at] elements on each tick, so elements
// that Blazor renders later are picked up automatically.

let started = false;

export function start() {
    if (started) {
        return;
    }
    started = true;
    tick();
    setInterval(tick, 1000);
}

function tick() {
    const now = Date.now();
    const elements = document.querySelectorAll("[data-qr-expires-at]");
    for (const el of elements) {
        const expiresAt = Date.parse(el.getAttribute("data-qr-expires-at") || "");
        if (Number.isNaN(expiresAt)) {
            continue;
        }

        const remainingMs = expiresAt - now;
        if (remainingMs <= 0) {
            el.textContent = el.getAttribute("data-qr-expired-label") || "Expired";
            el.classList.add("is-expired");
            continue;
        }

        const totalSeconds = Math.floor(remainingMs / 1000);
        const minutes = Math.floor(totalSeconds / 60);
        const seconds = totalSeconds % 60;
        const prefix = el.getAttribute("data-qr-countdown-prefix") || "";
        el.textContent = `${prefix}${minutes}:${seconds.toString().padStart(2, "0")}`;
        el.classList.remove("is-expired");
    }
}
