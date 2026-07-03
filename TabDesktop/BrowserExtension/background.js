// Relays thumbnail reports from content scripts to the local TabDesktop app (fetching here, in the extension origin with host_permissions, so page CSP can't block it), keeps TabDesktop's per-window tab list current, and holds a WebSocket over which TabDesktop sends commands like "activate tab". TabDesktop pings that socket every 20 s, which also keeps this MV3 service worker alive — incoming socket messages reset its idle timer. Relay failures are expected whenever TabDesktop isn't running.

const ENDPOINT = "http://127.0.0.1:38472/thumbnail";
const TABS_ENDPOINT = "http://127.0.0.1:38472/tabs";
const COMMAND_SOCKET_URL = "ws://127.0.0.1:38472/ws";
const SOCKET_RETRY_MS = 15000;
const TABS_DEBOUNCE_MS = 300;

let socket;

function ensureSocket() {
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) {
        return;
    }
    socket = new WebSocket(COMMAND_SOCKET_URL);
    socket.onopen = () => {
        console.log("[TabDesktop] command socket connected");
        reportTabsSoon();
    };
    socket.onmessage = (event) => {
        try {
            handleCommand(JSON.parse(event.data));
        } catch (err) {
            console.error("[TabDesktop] bad command:", err?.stack ?? err);
        }
    };
    socket.onclose = () => {
        socket = undefined;
        setTimeout(ensureSocket, SOCKET_RETRY_MS);
    };
}

function handleCommand(command) {
    if (command.type === "activateTab") {
        chrome.tabs.update(command.tabId, { active: true }).catch(err => console.error("[TabDesktop] activateTab failed:", err?.stack ?? err));
        if (command.windowId !== undefined) {
            chrome.windows.update(command.windowId, { focused: true }).catch(() => {});
        }
    } else if (command.type === "moveTab") {
        chrome.tabs.move(command.tabId, { index: command.index }).catch(err => console.error("[TabDesktop] moveTab failed:", err?.stack ?? err));
    }
}

let tabsTimer;
function reportTabsSoon() {
    clearTimeout(tabsTimer);
    tabsTimer = setTimeout(reportTabs, TABS_DEBOUNCE_MS);
}

async function reportTabs() {
    try {
        const tabs = await chrome.tabs.query({});
        const windows = new Map();
        for (const tab of tabs) {
            if (!windows.has(tab.windowId)) {
                windows.set(tab.windowId, { windowId: tab.windowId, tabs: [] });
            }
            windows.get(tab.windowId).tabs.push({ id: tab.id, title: tab.title ?? "", url: tab.url ?? "", active: tab.active, index: tab.index });
        }
        await fetch(TABS_ENDPOINT, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ windows: [...windows.values()] }),
        });
    } catch (err) {
        console.error("[TabDesktop] tab report failed (is TabDesktop running?):", err?.stack ?? err);
    }
}

chrome.tabs.onActivated.addListener(reportTabsSoon);
chrome.tabs.onUpdated.addListener((tabId, info) => {
    if (info.title !== undefined || info.url !== undefined || info.status === "complete") {
        reportTabsSoon();
    }
});
chrome.tabs.onCreated.addListener(reportTabsSoon);
chrome.tabs.onRemoved.addListener(reportTabsSoon);
chrome.tabs.onMoved.addListener(reportTabsSoon);

chrome.runtime.onMessage.addListener((message) => {
    ensureSocket();
    const kind = message.imageDataUrl ? "thumbnail" : message.imageUrl ? "image-url" : "heartbeat";
    fetch(ENDPOINT, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(message),
    })
        .then(response => console.log(`[TabDesktop] relayed ${kind} for "${message.title}" — HTTP ${response.status}`))
        .catch(err => console.error(`[TabDesktop] relay of ${kind} for "${message.title}" failed (is TabDesktop running?):`, err?.stack ?? err));
});

ensureSocket();
reportTabsSoon();
console.log(`[TabDesktop] background worker started, relaying to ${ENDPOINT}`);
