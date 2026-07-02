// Runs in every page and reports title+URL to TabDesktop (via the background worker, whose fetch is exempt from the page's CSP) — the bare report doubles as a connectivity heartbeat and feeds the screenshot-domain toggle. While the tab is visible it also attaches the page's thumbnail (poster/og:image on any page — every tab is visible at least once, right when it's created); background tabs skip the image work. Running inside the page is the whole point: same-origin fetches carry the site's cookies/session, so auth-gated posters (Jellyfin, anything behind a login) work where no external fetch could.

const THUMB_HEIGHT = 180;
const JPEG_QUALITY = 0.8;
const REPORT_INTERVAL_MS = 15000;
const TITLE_DEBOUNCE_MS = 1000;

// Last-resort thumbnail for pages that publish no poster/og:image (e.g. Jellyfin playback); cross-origin media taints the canvas and throws.
function captureFrame() {
    const video = [...document.querySelectorAll("video")].find(v => v.videoWidth > 0 && v.readyState >= 2 && v.currentTime > 0);
    if (!video) {
        return undefined;
    }
    try {
        const canvas = document.createElement("canvas");
        canvas.height = THUMB_HEIGHT;
        canvas.width = Math.round(THUMB_HEIGHT * video.videoWidth / video.videoHeight);
        canvas.getContext("2d").drawImage(video, 0, 0, canvas.width, canvas.height);
        return canvas.toDataURL("image/jpeg", JPEG_QUALITY);
    } catch {
        return undefined;
    }
}

function findImageUrl() {
    const poster = document.querySelector("video[poster]")?.poster;
    if (poster) {
        return poster;
    }
    const og = document.querySelector('meta[property="og:image"], meta[name="twitter:image"]')?.content;
    if (!og) {
        return undefined;
    }
    try {
        return new URL(og, location.href).href;
    } catch {
        return undefined;
    }
}

async function toDataUrl(url) {
    const response = await fetch(url, { credentials: "include" });
    if (!response.ok) {
        throw new Error(`HTTP ${response.status} for ${url}`);
    }
    const blob = await response.blob();
    return await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(blob);
    });
}

async function report() {
    const message = { title: document.title, url: location.href };
    if (document.visibilityState === "visible") {
        // The site's own poster/og:image is the curated thumbnail and always beats a frame grab of the playing video.
        const imageUrl = findImageUrl();
        if (imageUrl?.startsWith("data:")) {
            // Some sites inline the image as a data URL — it already is the payload, so pass it through without fetching.
            message.imageDataUrl = imageUrl;
        } else if (imageUrl) {
            try {
                message.imageDataUrl = await toDataUrl(imageUrl);
            } catch {
                // Page CSP or a cross-origin fetch failure; hand the URL over and let TabDesktop try a plain fetch (works for public CDNs).
                message.imageUrl = imageUrl;
            }
        }
        if (!message.imageDataUrl && !message.imageUrl) {
            const frame = captureFrame();
            if (frame) {
                message.imageDataUrl = frame;
            }
        }
    }
    try {
        // Await so a rejection (extension reloaded/removed under this page) lands in the catch instead of as an unhandled rejection.
        await chrome.runtime.sendMessage(message);
        console.debug(`[TabDesktop] reported "${message.title}"`);
    } catch (err) {
        console.debug(`[TabDesktop] report failed:`, err?.stack ?? err);
    }
}

report();
setInterval(report, REPORT_INTERVAL_MS);

// Switching to this tab makes the document visible — report right away so TabDesktop shows the newly selected tab's thumbnail without waiting out the interval.
document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") {
        report();
    }
});

// SPA sites (YouTube, Jellyfin) navigate without page loads; the title change is the navigation signal.
let titleTimer;
const titleTarget = document.querySelector("title") ?? document.head ?? document.documentElement;
new MutationObserver(() => {
    clearTimeout(titleTimer);
    titleTimer = setTimeout(report, TITLE_DEBOUNCE_MS);
}).observe(titleTarget, { subtree: true, childList: true, characterData: true });
