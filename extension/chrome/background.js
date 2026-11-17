const NATIVE_HOST_NAME = "com.velocitydm.host";
const DEFAULT_SETTINGS = {
  enabled: true,
  autoStart: false,
  minSizeBytes: 0,
  bridgePort: 17890
};

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
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== "velocitydm-download-link") return;
  const targetUrl = info.linkUrl || info.srcUrl;
  if (!targetUrl || !isHttpUrl(targetUrl)) return;

  const settings = await getSettings();
  const cookies = await getCookieHeader(targetUrl);
  await dispatchToVelocityDM(
    {
      action: "download",
      url: targetUrl,
      referrer: info.pageUrl || (tab && tab.url) || "",
      cookies,
      userAgent: navigator.userAgent,
      startImmediately: settings.autoStart
    },
    settings.bridgePort
  );
});

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  const url = downloadItem.finalUrl || downloadItem.url;
  if (!isHttpUrl(url)) return;

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

  // Pause Chrome's download briefly while VelocityDM acknowledges handoff
  try {
    await chrome.downloads.pause(downloadItem.id);
  } catch {
    // Ignore if already completing or cannot pause
  }

  const cookies = await getCookieHeader(url);
  const payload = {
    action: "download",
    url,
    filename: downloadItem.filename ? downloadItem.filename.split(/[\\/]/).pop() : "",
    referrer: downloadItem.referrer || "",
    cookies,
    userAgent: navigator.userAgent,
    startImmediately: settings.autoStart
  };

  const handedOff = await dispatchToVelocityDM(payload, settings.bridgePort);
  if (handedOff) {
    try {
      await chrome.downloads.cancel(downloadItem.id);
      await chrome.downloads.erase({ id: downloadItem.id });
    } catch {
      // Ignore cleanup failures
    }
  } else {
    try {
      await chrome.downloads.resume(downloadItem.id);
    } catch {
      // Ignore resume failures
    }
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
      const ok = await dispatchToVelocityDM({ action: "show" }, settings.bridgePort);
      sendResponse({ ok });
    });
    return true;
  }

  return false;
});

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
