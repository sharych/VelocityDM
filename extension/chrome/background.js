const NATIVE_HOST_NAME = "com.velocitydm.host";
const DEFAULT_SETTINGS = {
  enabled: true,
  autoStart: false,
  minSizeBytes: 0,
  bridgePort: 17890
};

// Chrome can fire downloads.onCreated during browser startup for historical,
// interrupted, paused, or session-restored items. Only intercept fresh,
// actively-started downloads initiated during the current session.
const MAX_NEW_DOWNLOAD_AGE_MS = 15_000;
const STARTUP_RESTORE_GRACE_MS = 2_500;
const MAX_TRACKED_IDS = 2000;
const SESSION_STORAGE_KEY = "velocitydmSessionState";

const inFlightDownloadIds = new Set();
let memorySessionState = createEmptySessionState();
let startupSyncPromise = null;
let stateMutationChain = Promise.resolve();
let dispatchChain = Promise.resolve();

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "velocitydm-download-link",
      title: "Download with VelocityDM",
      contexts: ["link", "video", "audio", "image"]
    });
  });

  chrome.storage.local.get(DEFAULT_SETTINGS, (current) => {
    chrome.storage.local.set({ ...DEFAULT_SETTINGS, ...current });
  });

  startupSyncPromise = enqueueStateMutation(() => initializeSessionOnInstalled());
});

chrome.runtime.onStartup.addListener(() => {
  const startupAt = Date.now();
  memorySessionState.browserStartupAt = startupAt;
  startupSyncPromise = enqueueStateMutation(() => initializeSessionOnStartup(startupAt));
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== "velocitydm-download-link") return;
  const targetUrl = info.linkUrl || info.srcUrl;
  if (!targetUrl || !isHttpUrl(targetUrl)) return;

  const settings = await getSettings();
  const cookies = await getCookieHeader(targetUrl);
  const payload = {
    action: "download",
    url: targetUrl,
    referrer: info.pageUrl || (tab && tab.url) || "",
    cookies,
    userAgent: navigator.userAgent,
    startImmediately: settings.autoStart
  };
  const handoff = await enqueueDispatch(() => dispatchToVelocityDM(payload, settings.bridgePort));
  if (handoff.ok) {
    await showChromeDownloadPopup(handoff, payload, tab && typeof tab.id === "number" ? tab.id : undefined);
  }
});

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  if (!downloadItem || typeof downloadItem.id !== "number") return;
  if (inFlightDownloadIds.has(downloadItem.id)) return;

  inFlightDownloadIds.add(downloadItem.id);
  try {
    const now = Date.now();
    if (!isLiveDownloadCandidate(downloadItem, now)) {
      return;
    }

    const sessionState = await enqueueStateMutation(() => ensureSessionState());
    if (startupSyncPromise) {
      await startupSyncPromise;
    }

    const effectiveState = (await readSessionState()) || sessionState;
    if (!isEligibleForSession(downloadItem, effectiveState, Date.now())) {
      return;
    }

    const settings = await getSettings();
    if (!settings.enabled) return;

    if (
      settings.minSizeBytes > 0 &&
      typeof downloadItem.totalBytes === "number" &&
      downloadItem.totalBytes > 0 &&
      downloadItem.totalBytes < settings.minSizeBytes
    ) {
      return;
    }

    // Re-check current item state in case it completed/errored while awaiting storage
    const latestItems = await searchDownloads({ id: downloadItem.id });
    if (latestItems.length > 0 && !isLiveDownloadCandidate(latestItems[0], Date.now())) {
      return;
    }

    const activeItem = latestItems.length > 0 ? latestItems[0] : downloadItem;
    const url = activeItem.finalUrl || activeItem.url;
    if (!isHttpUrl(url)) return;

    await enqueueStateMutation(() => markHandledDownloadId(downloadItem.id));

    // Pause Chrome's download briefly while VelocityDM acknowledges handoff
    let pausedByUs = false;
    try {
      await chrome.downloads.pause(downloadItem.id);
      pausedByUs = true;
    } catch {
      // Ignore if already completing or cannot pause
    }

    const cookies = await getCookieHeader(url);
    const payload = {
      action: "download",
      url,
      filename: activeItem.filename ? activeItem.filename.split(/[\\/]/).pop() : "",
      referrer: activeItem.referrer || "",
      cookies,
      userAgent: navigator.userAgent,
      startImmediately: settings.autoStart
    };

    const handoff = await enqueueDispatch(() => dispatchToVelocityDM(payload, settings.bridgePort));
    if (handoff.ok) {
      try {
        await chrome.downloads.cancel(downloadItem.id);
        await chrome.downloads.erase({ id: downloadItem.id });
      } catch {
        // Ignore cleanup failures
      }
      await showChromeDownloadPopup(handoff, payload);
    } else if (pausedByUs) {
      try {
        await chrome.downloads.resume(downloadItem.id);
      } catch {
        // Ignore resume failures
      }
    }
  } finally {
    inFlightDownloadIds.delete(downloadItem.id);
  }
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message && message.type === "ping") {
    getSettings().then(async (settings) => {
      try {
        const res = await fetchWithTimeout(`http://127.0.0.1:${settings.bridgePort}/ping`, { method: "GET" }, 900);
        sendResponse({ ok: res.ok });
      } catch {
        sendResponse({ ok: false });
      }
    });
    return true;
  }

  if (message && message.type === "showApp") {
    getSettings().then(async (settings) => {
      const resp = await enqueueDispatch(() => dispatchToVelocityDM({ action: "show" }, settings.bridgePort));
      sendResponse({ ok: Boolean(resp && resp.ok) });
    });
    return true;
  }

  return false;
});

function createEmptySessionState() {
  return {
    initialized: false,
    browserStartupAt: 0,
    ignoredIds: [],
    handledIds: []
  };
}

function isLiveDownloadCandidate(downloadItem, now) {
  if (!downloadItem || typeof downloadItem !== "object") return false;

  const url = downloadItem.finalUrl || downloadItem.url;
  if (!isHttpUrl(url)) return false;

  if (downloadItem.state !== "in_progress") return false;
  if (downloadItem.paused === true) return false;
  if (downloadItem.endTime) return false;
  if (downloadItem.error) return false;

  const runtimeId = chrome.runtime && chrome.runtime.id;
  if (runtimeId && downloadItem.byExtensionId === runtimeId) return false;

  const startMs = parseTimestampMs(downloadItem.startTime);
  if (!Number.isFinite(startMs)) return false;
  if (now - startMs > MAX_NEW_DOWNLOAD_AGE_MS) return false;
  if (startMs > now + 5_000) return false;

  return true;
}

function isEligibleForSession(downloadItem, sessionState, now) {
  if (!sessionState) return true;
  if (sessionState.ignoredIds.includes(downloadItem.id)) return false;
  if (sessionState.handledIds.includes(downloadItem.id)) return false;

  if (sessionState.browserStartupAt > 0) {
    const startMs = parseTimestampMs(downloadItem.startTime);
    if (!Number.isFinite(startMs)) return false;
    if (startMs < sessionState.browserStartupAt - 500) return false;
    if (now - sessionState.browserStartupAt < STARTUP_RESTORE_GRACE_MS) return false;
  }

  return true;
}

async function initializeSessionOnStartup(startupAt) {
  const existing = await collectExistingDownloadIds();
  const nextState = {
    initialized: true,
    browserStartupAt: startupAt,
    ignoredIds: trimTrackedIds(existing),
    handledIds: []
  };
  await writeSessionState(nextState);
  return nextState;
}

async function initializeSessionOnInstalled() {
  const existing = await collectExistingDownloadIds();
  const current = (await readSessionState()) || createEmptySessionState();
  const mergedIgnored = trimTrackedIds([...current.ignoredIds, ...existing]);
  const nextState = {
    initialized: true,
    browserStartupAt: current.browserStartupAt || 0,
    ignoredIds: mergedIgnored,
    handledIds: trimTrackedIds(current.handledIds)
  };
  await writeSessionState(nextState);
  return nextState;
}

async function ensureSessionState() {
  const stored = await readSessionState();
  if (stored && stored.initialized) {
    return stored;
  }

  const now = Date.now();
  const snapshot = await collectDownloadSnapshot();
  const ignored = [...memorySessionState.ignoredIds];
  for (const item of snapshot) {
    if (typeof item.id !== "number") continue;
    if (!isLiveDownloadCandidate(item, now) && !ignored.includes(item.id)) {
      ignored.push(item.id);
    }
  }

  const nextState = {
    initialized: true,
    browserStartupAt: memorySessionState.browserStartupAt || 0,
    ignoredIds: trimTrackedIds(ignored),
    handledIds: trimTrackedIds(memorySessionState.handledIds)
  };
  await writeSessionState(nextState);
  return nextState;
}

async function markHandledDownloadId(id) {
  const state = (await readSessionState()) || memorySessionState;
  if (!state.handledIds.includes(id)) {
    state.handledIds = trimTrackedIds([...state.handledIds, id]);
    await writeSessionState(state);
  }
}

async function collectDownloadSnapshot() {
  const [recent, active] = await Promise.all([
    searchDownloads({ orderBy: ["-startTime"], limit: 1000 }),
    searchDownloads({ state: "in_progress" })
  ]);

  const byId = new Map();
  for (const item of [...recent, ...active]) {
    if (item && typeof item.id === "number") {
      byId.set(item.id, item);
    }
  }
  return Array.from(byId.values());
}

async function collectExistingDownloadIds() {
  const snapshot = await collectDownloadSnapshot();
  return snapshot.map((item) => item.id);
}

function searchDownloads(query) {
  return new Promise((resolve) => {
    try {
      chrome.downloads.search(query, (items) => {
        if (chrome.runtime && chrome.runtime.lastError) {
          resolve([]);
          return;
        }
        resolve(Array.isArray(items) ? items : []);
      });
    } catch {
      resolve([]);
    }
  });
}

async function readSessionState() {
  const sessionArea = chrome.storage && chrome.storage.session;
  if (!sessionArea) {
    return memorySessionState;
  }

  try {
    const result = await sessionArea.get(SESSION_STORAGE_KEY);
    const stored = result && result[SESSION_STORAGE_KEY];
    if (stored && typeof stored === "object") {
      memorySessionState = {
        initialized: Boolean(stored.initialized),
        browserStartupAt: typeof stored.browserStartupAt === "number" ? stored.browserStartupAt : 0,
        ignoredIds: Array.isArray(stored.ignoredIds) ? stored.ignoredIds : [],
        handledIds: Array.isArray(stored.handledIds) ? stored.handledIds : []
      };
      return memorySessionState;
    }
  } catch {
    // Fall back to in-memory session state
  }

  return memorySessionState;
}

async function writeSessionState(state) {
  memorySessionState = {
    initialized: Boolean(state.initialized),
    browserStartupAt: typeof state.browserStartupAt === "number" ? state.browserStartupAt : 0,
    ignoredIds: trimTrackedIds(state.ignoredIds),
    handledIds: trimTrackedIds(state.handledIds)
  };

  const sessionArea = chrome.storage && chrome.storage.session;
  if (!sessionArea) return;

  try {
    await sessionArea.set({ [SESSION_STORAGE_KEY]: memorySessionState });
  } catch {
    // Ignore storage errors and keep in-memory state
  }
}

function trimTrackedIds(ids) {
  if (!Array.isArray(ids)) return [];
  const unique = Array.from(new Set(ids.filter((id) => typeof id === "number")));
  return unique.length > MAX_TRACKED_IDS ? unique.slice(-MAX_TRACKED_IDS) : unique;
}

function parseTimestampMs(value) {
  if (typeof value === "number") {
    return Number.isFinite(value) ? value : NaN;
  }
  if (typeof value === "string" && value.trim().length > 0) {
    return Date.parse(value);
  }
  return NaN;
}

function enqueueStateMutation(task) {
  const next = stateMutationChain.then(task, task);
  stateMutationChain = next.catch(() => null);
  return next;
}

function enqueueDispatch(task) {
  const next = dispatchChain.then(task, task);
  dispatchChain = next.catch(() => false);
  return next;
}

async function dispatchToVelocityDM(payload, bridgePort) {
  try {
    const res = await fetchWithTimeout(
      `http://127.0.0.1:${bridgePort}/download`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      },
      1500
    );
    if (res.ok) {
      const data = await res.json().catch(() => ({ ok: true }));
      if (data && data.ok !== false) {
        return { ok: true, ...data };
      }
    }
  } catch {
    // Bridge not listening; fall back to Native Messaging which launches VelocityDM
  }

  try {
    const nativeResp = await chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload);
    if (nativeResp && nativeResp.ok) {
      return { ok: true, ...nativeResp };
    }
    return { ok: false };
  } catch {
    return { ok: false };
  }
}

async function showChromeDownloadPopup(ack, payload, preferredTabId) {
  const fileName = ack.fileName || payload.filename || extractFileNameFromUrl(payload.url);
  const saveDirectory = ack.saveDirectory || "VelocityDM Default Folder";
  const connections = typeof ack.connections === "number" && ack.connections > 0 ? ack.connections : 32;
  const modeLabel = ack.mode === "started"
    ? "Downloading by VelocityDM"
    : "VelocityDM Download Popup";

  const toastInfo = {
    app: ack.app || "VelocityDM",
    modeLabel,
    fileName,
    saveDirectory,
    connections
  };

  const injected = await tryShowInTabToast(toastInfo, preferredTabId);
  if (injected) return;

  if (chrome.notifications && typeof chrome.notifications.create === "function") {
    try {
      const notifId = `velocitydm-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
      chrome.notifications.create(notifId, {
        type: "basic",
        iconUrl: "icon128.png",
        title: `${toastInfo.modeLabel} (${toastInfo.connections} connections)`,
        message: `${toastInfo.fileName}\nSave to: ${toastInfo.saveDirectory}`
      });
      setTimeout(() => {
        try {
          chrome.notifications.clear(notifId);
        } catch {
          // Ignore clear errors
        }
      }, 6500);
    } catch {
      // Ignore notification errors
    }
  }
}

async function tryShowInTabToast(toastInfo, preferredTabId) {
  if (!chrome.scripting || typeof chrome.scripting.executeScript !== "function") {
    return false;
  }

  try {
    let targetTabId = typeof preferredTabId === "number" ? preferredTabId : null;
    if (targetTabId === null && chrome.tabs && typeof chrome.tabs.query === "function") {
      const tabs = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      if (Array.isArray(tabs) && tabs.length > 0 && typeof tabs[0].id === "number") {
        targetTabId = tabs[0].id;
      }
    }
    if (targetTabId === null) return false;

    await chrome.scripting.executeScript({
      target: { tabId: targetTabId },
      func: renderVelocityDmChromeToast,
      args: [toastInfo]
    });
    return true;
  } catch {
    return false;
  }
}

function renderVelocityDmChromeToast(info) {
  try {
    const hostId = "__velocitydm_chrome_toast_host__";
    let host = document.getElementById(hostId);
    if (!host) {
      host = document.createElement("div");
      host.id = hostId;
      host.style.cssText = "position:fixed;top:16px;right:16px;z-index:2147483647;pointer-events:none;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;";
      const shadow = host.attachShadow({ mode: "open" });
      const style = document.createElement("style");
      style.textContent = `
        .vdm-stack { display: flex; flex-direction: column; gap: 10px; max-width: 380px; }
        .vdm-card {
          pointer-events: auto;
          background: #0f172a;
          color: #f8fafc;
          border: 1px solid #38bdf8;
          border-left: 4px solid #38bdf8;
          border-radius: 10px;
          box-shadow: 0 14px 34px rgba(2, 6, 23, 0.55);
          padding: 12px 14px;
          font-size: 12.5px;
          line-height: 1.45;
          animation: vdmSlideIn 0.18s ease-out;
        }
        .vdm-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 6px; }
        .vdm-title { font-weight: 700; color: #38bdf8; font-size: 13px; }
        .vdm-close {
          background: transparent; border: none; color: #94a3b8; cursor: pointer;
          font-size: 15px; line-height: 1; padding: 0 4px;
        }
        .vdm-close:hover { color: #f8fafc; }
        .vdm-file { font-weight: 600; color: #f8fafc; word-break: break-word; margin-bottom: 4px; }
        .vdm-meta { color: #cbd5e1; font-size: 11.5px; word-break: break-word; }
        .vdm-by { color: #10b981; font-weight: 600; margin-top: 4px; font-size: 11.5px; }
        @keyframes vdmSlideIn {
          from { opacity: 0; transform: translateY(-8px); }
          to { opacity: 1; transform: translateY(0); }
        }
      `;
      const stack = document.createElement("div");
      stack.className = "vdm-stack";
      shadow.appendChild(style);
      shadow.appendChild(stack);
      (document.body || document.documentElement).appendChild(host);
    }

    const stack = host.shadowRoot && host.shadowRoot.querySelector(".vdm-stack");
    if (!stack) return;

    const card = document.createElement("div");
    card.className = "vdm-card";

    const head = document.createElement("div");
    head.className = "vdm-head";

    const title = document.createElement("div");
    title.className = "vdm-title";
    title.textContent = info.modeLabel || "VelocityDM Download";

    const closeBtn = document.createElement("button");
    closeBtn.className = "vdm-close";
    closeBtn.textContent = "×";
    closeBtn.title = "Dismiss";
    closeBtn.addEventListener("click", () => card.remove());

    head.appendChild(title);
    head.appendChild(closeBtn);

    const fileEl = document.createElement("div");
    fileEl.className = "vdm-file";
    fileEl.textContent = info.fileName || "Download";

    const pathEl = document.createElement("div");
    pathEl.className = "vdm-meta";
    pathEl.textContent = `Save to: ${info.saveDirectory}`;

    const byEl = document.createElement("div");
    byEl.className = "vdm-by";
    byEl.textContent = `Handled by ${info.app || "VelocityDM"} • ${info.connections || 32} parallel connections`;

    card.appendChild(head);
    card.appendChild(fileEl);
    card.appendChild(pathEl);
    card.appendChild(byEl);
    stack.appendChild(card);

    setTimeout(() => {
      card.remove();
      if (stack.childElementCount === 0 && host.isConnected) {
        host.remove();
      }
    }, 6000);
  } catch {
    // Ignore DOM injection failures
  }
}

function extractFileNameFromUrl(url) {
  try {
    const parsed = new URL(url);
    const seg = parsed.pathname.split("/").filter(Boolean).pop();
    return seg ? decodeURIComponent(seg) : "download";
  } catch {
    return "download";
  }
}

async function getCookieHeader(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    if (!list || list.length === 0) return "";
    return list.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch {
    return "";
  }
}

async function fetchWithTimeout(url, options, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...options, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

function getSettings() {
  return new Promise((resolve) => {
    chrome.storage.local.get(DEFAULT_SETTINGS, (items) => resolve({ ...DEFAULT_SETTINGS, ...items }));
  });
}

function isHttpUrl(value) {
  return typeof value === "string" && (value.startsWith("http://") || value.startsWith("https://"));
}
