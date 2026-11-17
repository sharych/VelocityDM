const DEFAULT_SETTINGS = {
  enabled: true,
  autoStart: false,
  minSizeBytes: 0,
  bridgePort: 17890
};

async function loadUi() {
  const stored = await chrome.storage.local.get(DEFAULT_SETTINGS);
  const enabledToggle = document.getElementById("enabledToggle");
  const autoStartToggle = document.getElementById("autoStartToggle");
  const minSizeSelect = document.getElementById("minSizeSelect");

  enabledToggle.checked = Boolean(stored.enabled);
  autoStartToggle.checked = Boolean(stored.autoStart);
  minSizeSelect.value = String(stored.minSizeBytes || 0);

  enabledToggle.addEventListener("change", () => {
    chrome.storage.local.set({ enabled: enabledToggle.checked });
  });
  autoStartToggle.addEventListener("change", () => {
    chrome.storage.local.set({ autoStart: autoStartToggle.checked });
  });
  minSizeSelect.addEventListener("change", () => {
    chrome.storage.local.set({ minSizeBytes: Number(minSizeSelect.value) || 0 });
  });

  document.getElementById("openAppBtn").addEventListener("click", async () => {
    await chrome.runtime.sendMessage({ type: "showApp" });
    window.close();
  });

  refreshStatus();
}

async function refreshStatus() {
  const dot = document.getElementById("statusDot");
  const text = document.getElementById("statusText");
  try {
    const resp = await chrome.runtime.sendMessage({ type: "ping" });
    if (resp && resp.ok) {
      dot.classList.add("online");
      text.textContent = "Connected to VelocityDM (running)";
    } else {
      dot.classList.remove("online");
      text.textContent = "Not running (will auto-launch via Native Host)";
    }
  } catch {
    dot.classList.remove("online");
    text.textContent = "Bridge offline";
  }
}

document.addEventListener("DOMContentLoaded", loadUi);
