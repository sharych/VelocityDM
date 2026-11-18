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
  await enqueueDispatch(() =>
    dispatchToVelocityDM(
      {
        action: "download",
        url: targetUrl,
        referrer: info.pageUrl || (tab && tab.url) || "",
        cookies,
        userAgent: navigator.userAgent,
        startImmediately: settings.autoStart
      },
      settings.bridgePort
    )
  );
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

    const handedOff = await enqueueDispatch(() => dispatchToVelocityDM(payload, settings.bridgePort));
    if (handedOff) {
      try {
        await chrome.downloads.cancel(downloadItem.id);
        await chrome.downloads.erase({ id: downloadItem.id });
      } catch {
        // Ignore cleanup failures
      }
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
      const ok = await enqueueDispatch(() => dispatchToVelocityDM({ action: "show" }, settings.bridgePort));
      sendResponse({ ok });
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
      if (data && data.ok !== false) return true;
    }
  } catch {
    // Bridge not listening; fall back to Native Messaging which launches VelocityDM
  }

  try {
    const nativeResp = await chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload);
    return Boolean(nativeResp && nativeResp.ok);
  } catch {
    return false;
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
