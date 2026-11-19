import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const BACKGROUND_JS_PATH = path.resolve(__dirname, "../../extension/chrome/background.js");
const BACKGROUND_SOURCE = fs.readFileSync(BACKGROUND_JS_PATH, "utf8");

function createChromeHarness(options = {}) {
  let currentNow = options.initialNow ?? 1_700_000_000_000;
  const localStore = { ...(options.initialLocalStore ?? {}) };
  const sessionStore = { ...(options.initialSessionStore ?? {}) };
  const downloadItems = [...(options.initialDownloads ?? [])];

  const listeners = {
    onInstalled: [],
    onStartup: [],
    contextClicked: [],
    downloadCreated: [],
    runtimeMessage: []
  };

  const calls = {
    fetch: [],
    nativeMessages: [],
    paused: [],
    resumed: [],
    cancelled: [],
    erased: [],
    tabScripts: [],
    notifications: []
  };

  class MockDate extends Date {
    constructor(...args) {
      if (args.length === 0) {
        super(currentNow);
      } else {
        super(...args);
      }
    }
    static now() {
      return currentNow;
    }
    static parse(str) {
      return Date.parse(str);
    }
  }

  function loadWorker() {
    listeners.onInstalled = [];
    listeners.onStartup = [];
    listeners.contextClicked = [];
    listeners.downloadCreated = [];
    listeners.runtimeMessage = [];

    const chromeMock = {
      runtime: {
        id: "pcegcihecngonbfglmjgobmlafofmchk",
        lastError: null,
        onInstalled: { addListener: (fn) => listeners.onInstalled.push(fn) },
        onStartup: { addListener: (fn) => listeners.onStartup.push(fn) },
        onMessage: { addListener: (fn) => listeners.runtimeMessage.push(fn) },
        sendNativeMessage: async (host, payload) => {
          calls.nativeMessages.push({ host, payload });
          if (options.nativeHandler) {
            return options.nativeHandler(host, payload);
          }
          return { ok: options.nativeOk ?? true };
        }
      },
      contextMenus: {
        removeAll: (cb) => cb && cb(),
        create: () => {},
        onClicked: { addListener: (fn) => listeners.contextClicked.push(fn) }
      },
      storage: {
        local: {
          get: (defaults, cb) => cb({ ...defaults, ...localStore }),
          set: (values, cb) => {
            Object.assign(localStore, values);
            if (cb) cb();
          }
        },
        session: options.disableSessionStorage
          ? undefined
          : {
              get: async (key) => {
                if (typeof key === "string") {
                  return key in sessionStore ? { [key]: structuredClone(sessionStore[key]) } : {};
                }
                return structuredClone(sessionStore);
              },
              set: async (values) => {
                for (const [k, v] of Object.entries(values)) {
                  sessionStore[k] = structuredClone(v);
                }
              }
            }
      },
      cookies: {
        getAll: async () => []
      },
      tabs: {
        query: async () => options.activeTabs ?? [{ id: 1 }]
      },
      scripting: {
        executeScript: async (details) => {
          calls.tabScripts.push(details);
          if (options.failScripting) {
            throw new Error("Cannot access chrome:// URL");
          }
          return [{ result: true }];
        }
      },
      notifications: {
        create: (id, notifOptions) => {
          calls.notifications.push({ id, ...notifOptions });
        },
        clear: () => {}
      },
      downloads: {
        onCreated: { addListener: (fn) => listeners.downloadCreated.push(fn) },
        search: (query, cb) => {
          let results = [...downloadItems];
          if (typeof query?.id === "number") {
            results = results.filter((d) => d.id === query.id);
          }
          if (query?.state) {
            results = results.filter((d) => d.state === query.state);
          }
          if (typeof query?.limit === "number") {
            results = results.slice(0, query.limit);
          }
          cb(results);
        },
        pause: async (id) => {
          calls.paused.push(id);
          const item = downloadItems.find((d) => d.id === id);
          if (!item || item.state !== "in_progress") {
            throw new Error("Download must be in progress");
          }
          item.paused = true;
        },
        resume: async (id) => {
          calls.resumed.push(id);
        },
        cancel: async (id) => {
          calls.cancelled.push(id);
          const item = downloadItems.find((d) => d.id === id);
          if (item) item.state = "interrupted";
        },
        erase: async ({ id }) => {
          calls.erased.push(id);
        }
      }
    };

    const sandbox = {
      chrome: chromeMock,
      Date: MockDate,
      AbortController,
      setTimeout,
      clearTimeout,
      navigator: { userAgent: "ChromeTest/1.0" },
      fetch: async (url, init) => {
        calls.fetch.push({ url, init });
        if (options.fetchHandler) {
          return options.fetchHandler(url, init);
        }
        if (options.bridgeOnline === false) {
          throw new Error("ECONNREFUSED");
        }
        return {
          ok: true,
          json: async () => ({
            ok: true,
            app: "VelocityDM",
            mode: "popup",
            fileName: "sample.zip",
            saveDirectory: "/Downloads/VelocityDM/Compressed",
            connections: 32
          })
        };
      }
    };

    vm.createContext(sandbox);
    vm.runInContext(BACKGROUND_SOURCE, sandbox, { filename: "background.js" });
  }

  loadWorker();

  return {
    get now() {
      return currentNow;
    },
    advanceTime(ms) {
      currentNow += ms;
    },
    addExistingDownload(item) {
      downloadItems.push(item);
    },
    async triggerStartup() {
      for (const fn of listeners.onStartup) {
        await fn();
      }
    },
    async triggerInstalled() {
      for (const fn of listeners.onInstalled) {
        await fn();
      }
    },
    async triggerDownloadCreated(item, { addToStore = true } = {}) {
      if (addToStore && !downloadItems.some((d) => d.id === item.id)) {
        downloadItems.push({ ...item });
      }
      await Promise.all(listeners.downloadCreated.map((fn) => fn(item)));
    },
    async triggerContextMenu(url) {
      await Promise.all(
        listeners.contextClicked.map((fn) =>
          fn({ menuItemId: "velocitydm-download-link", linkUrl: url, pageUrl: "https://example.org" }, { url: "https://example.org" })
        )
      );
    },
    restartServiceWorker() {
      loadWorker();
    },
    calls
  };
}

test("ignores massive flood of historical completed, interrupted, paused, and old downloads on Chrome startup", async () => {
  const baseNow = 1_700_000_000_000;
  const historicalItems = [];
  for (let i = 1; i <= 150; i++) {
    historicalItems.push({
      id: i,
      url: `https://cdn.example.com/archive-${i}.zip`,
      state: i % 3 === 0 ? "interrupted" : "complete",
      paused: i % 5 === 0,
      startTime: new Date(baseNow - 86_400_000 * i).toISOString(),
      endTime: i % 3 === 0 ? undefined : new Date(baseNow - 86_400_000 * i + 5000).toISOString()
    });
  }
  // Also include a previous-session download that Chrome auto-resumed on startup
  historicalItems.push({
    id: 999,
    url: "https://cdn.example.com/auto-resumed.iso",
    state: "in_progress",
    paused: false,
    startTime: new Date(baseNow - 3_600_000).toISOString()
  });

  const h = createChromeHarness({
    initialNow: baseNow,
    initialDownloads: historicalItems
  });

  await h.triggerStartup();
  await Promise.all(historicalItems.map((item) => h.triggerDownloadCreated(item, { addToStore: false })));

  assert.equal(h.calls.fetch.length, 0);
  assert.equal(h.calls.nativeMessages.length, 0);
  assert.equal(h.calls.paused.length, 0);
  assert.equal(h.calls.resumed.length, 0);
});

test("handles race when onCreated fires concurrently with onStartup and blocks startup tab-restore burst", async () => {
  const baseNow = 1_700_000_000_000;
  const oldRestored = {
    id: 10,
    url: "https://cdn.example.com/old.exe",
    state: "in_progress",
    paused: false,
    startTime: new Date(baseNow - 20_000).toISOString()
  };
  const instantTabRestore = {
    id: 11,
    url: "https://cdn.example.com/restored-tab.zip",
    state: "in_progress",
    paused: false,
    startTime: new Date(baseNow + 200).toISOString()
  };

  const h = createChromeHarness({
    initialNow: baseNow,
    initialDownloads: [oldRestored]
  });

  // Trigger startup and an instant startup download within grace window
  const startupPromise = h.triggerStartup();
  h.advanceTime(200);
  const createdOld = h.triggerDownloadCreated(oldRestored, { addToStore: false });
  const createdRestoredTab = h.triggerDownloadCreated(instantTabRestore);

  await Promise.all([startupPromise, createdOld, createdRestoredTab]);

  assert.equal(h.calls.fetch.length, 0);
  assert.equal(h.calls.nativeMessages.length, 0);
});

test("intercepts genuine user download click after Chrome startup and survives MV3 service worker wake-up", async () => {
  const baseNow = 1_700_000_000_000;
  const h = createChromeHarness({ initialNow: baseNow });

  await h.triggerStartup();
  h.advanceTime(5_000);

  // Simulate MV3 service worker going idle and waking up later
  h.restartServiceWorker();

  const clickedItem = {
    id: 501,
    url: "https://cdn.example.com/game-installer.exe",
    filename: "C:\\Users\\test\\Downloads\\game-installer.exe",
    referrer: "https://cdn.example.com/downloads",
    state: "in_progress",
    paused: false,
    startTime: new Date(h.now - 100).toISOString()
  };

  await h.triggerDownloadCreated(clickedItem);
  // Duplicate event for same ID must be ignored
  await h.triggerDownloadCreated(clickedItem);

  assert.equal(h.calls.fetch.length, 1);
  const body = JSON.parse(h.calls.fetch[0].init.body);
  assert.equal(body.action, "download");
  assert.equal(body.url, "https://cdn.example.com/game-installer.exe");
  assert.equal(body.filename, "game-installer.exe");
  assert.deepEqual(h.calls.paused, [501]);
  assert.deepEqual(h.calls.cancelled, [501]);
  assert.deepEqual(h.calls.erased, [501]);
});

test("serializes native messaging fallback when bridge is offline and resumes Chrome download if handoff fails", async () => {
  const baseNow = 1_700_000_000_000;
  let activeNativeCalls = 0;
  let maxConcurrentNativeCalls = 0;

  const h = createChromeHarness({
    initialNow: baseNow,
    bridgeOnline: false,
    nativeHandler: async (_host, payload) => {
      activeNativeCalls++;
      maxConcurrentNativeCalls = Math.max(maxConcurrentNativeCalls, activeNativeCalls);
      await new Promise((r) => setTimeout(r, 15));
      activeNativeCalls--;
      return { ok: !payload.url.includes("fail") };
    }
  });

  await h.triggerStartup();
  h.advanceTime(4_000);

  await Promise.all([
    h.triggerDownloadCreated({
      id: 601,
      url: "https://cdn.example.com/ok1.zip",
      state: "in_progress",
      paused: false,
      startTime: new Date(h.now).toISOString()
    }),
    h.triggerDownloadCreated({
      id: 602,
      url: "https://cdn.example.com/fail2.zip",
      state: "in_progress",
      paused: false,
      startTime: new Date(h.now).toISOString()
    })
  ]);

  assert.equal(maxConcurrentNativeCalls, 1);
  assert.equal(h.calls.nativeMessages.length, 2);
  assert.deepEqual(h.calls.cancelled, [601]);
  assert.deepEqual(h.calls.resumed, [602]);
});

test("ignores non-HTTP URLs and extension-originated downloads, while context menu always works", async () => {
  const baseNow = 1_700_000_000_000;
  const h = createChromeHarness({ initialNow: baseNow });
  await h.triggerStartup();
  h.advanceTime(4_000);

  await h.triggerDownloadCreated({
    id: 701,
    url: "blob:https://example.com/1234",
    state: "in_progress",
    paused: false,
    startTime: new Date(h.now).toISOString()
  });

  await h.triggerDownloadCreated({
    id: 702,
    url: "https://example.com/self.zip",
    state: "in_progress",
    paused: false,
    byExtensionId: "pcegcihecngonbfglmjgobmlafofmchk",
    startTime: new Date(h.now).toISOString()
  });

  assert.equal(h.calls.fetch.length, 0);

  await h.triggerContextMenu("https://example.com/manual.zip");
  assert.equal(h.calls.fetch.length, 1);
});

test("blocks startup downloads even if onCreated fires right before onStartup or without chrome.storage.session", async () => {
  const baseNow = 1_700_000_000_000;
  const h = createChromeHarness({
    initialNow: baseNow,
    disableSessionStorage: true,
    initialDownloads: [
      {
        id: 801,
        url: "https://example.com/stale-in-memory.zip",
        state: "complete",
        startTime: new Date(baseNow - 60_000).toISOString(),
        endTime: new Date(baseNow - 50_000).toISOString()
      }
    ]
  });

  // onCreated fires first, then onStartup fires immediately in the same event batch
  const p1 = h.triggerDownloadCreated({
    id: 802,
    url: "https://example.com/instant-startup.zip",
    state: "in_progress",
    paused: false,
    startTime: new Date(baseNow).toISOString()
  });
  const p2 = h.triggerStartup();
  await Promise.all([p1, p2]);

  assert.equal(h.calls.fetch.length, 0);

  // After startup grace window passes, a genuine user click still succeeds without storage.session
  h.advanceTime(4_000);
  await h.triggerDownloadCreated({
    id: 803,
    url: "https://example.com/user-click.zip",
    state: "in_progress",
    paused: false,
    startTime: new Date(h.now).toISOString()
  });

  assert.equal(h.calls.fetch.length, 1);
});

test("shows in-tab Chrome popup on user click and falls back to Chrome notification on restricted pages", async () => {
  const baseNow = 1_700_000_000_000;

  const hTab = createChromeHarness({ initialNow: baseNow });
  await hTab.triggerStartup();
  hTab.advanceTime(4_000);

  await hTab.triggerDownloadCreated({
    id: 901,
    url: "https://example.com/movie.mp4",
    state: "in_progress",
    paused: false,
    startTime: new Date(hTab.now).toISOString()
  });

  assert.equal(hTab.calls.tabScripts.length, 1);
  assert.equal(hTab.calls.tabScripts[0].args[0].saveDirectory, "/Downloads/VelocityDM/Compressed");
  assert.equal(hTab.calls.tabScripts[0].args[0].connections, 32);
  assert.equal(hTab.calls.notifications.length, 0);

  const hNotif = createChromeHarness({ initialNow: baseNow, failScripting: true });
  await hNotif.triggerStartup();
  hNotif.advanceTime(4_000);

  await hNotif.triggerDownloadCreated({
    id: 902,
    url: "https://example.com/archive.zip",
    state: "in_progress",
    paused: false,
    startTime: new Date(hNotif.now).toISOString()
  });

  assert.equal(hNotif.calls.notifications.length, 1);
  assert.match(hNotif.calls.notifications[0].message, /Save to: \/Downloads\/VelocityDM\/Compressed/);
});
