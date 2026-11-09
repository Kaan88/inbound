// A stand-in for the extension APIs, for scripts/store-screens.mjs (EXT.R8 §7): the real extension pages run against
// fixed data (screens-data.json) with no native host, no network and no browser profile. Installed with Playwright's
// addInitScript before any page script, after window.__inboundScreens = { data, scenario } is set.
//
// scenario: paired, serverDown, localError ("missing", "forbidden", "stopped"), autoOff, paused, ask, left, target,
// empty, startMode, theme, linksError ("notWeb").
(() => {
  const config = window.__inboundScreens;
  if (!config) return;
  const { data, scenario = {} } = config;
  const now = () => Date.now();
  const settings = { ...data.settings, startMode: scenario.startMode ?? data.settings.startMode,
    theme: scenario.theme ?? data.settings.theme, defaultTarget: scenario.target ?? data.settings.defaultTarget };
  if (scenario.autoOff) settings.autoIntercept = false;
  const server = scenario.paired ? { ...data.server, connected: !scenario.serverDown, lastError: scenario.serverDown ? "No answer from home-server:8080." : "" } : null;
  const localErrors = {
    missing: "Specified native messaging host not found.",
    forbidden: "Access to the specified native messaging host is forbidden.",
    stopped: "Native host has exited.",
  };
  const localError = scenario.localError ? localErrors[scenario.localError] : "";
  const jobs = scenario.empty ? [] : data.jobs
    .filter((job) => scenario.paired || job.target !== "server")
    .map((job) => scenario.paired ? job : { ...job, target: undefined });
  const targets = scenario.paired
    ? data.targets.map((target) => target.id === "server" && scenario.serverDown ? { ...target, error: "No answer from home-server:8080." } : target)
    : [data.targets[0]];

  function status() {
    return {
      ok: true, extensionVersion: "0.6.0", enabled: settings.enabled, autoIntercept: settings.autoIntercept,
      automaticSending: settings.enabled && settings.autoIntercept, connected: !localError, lastError: localError,
      localMissing: !!server && scenario.localError === "missing", server,
      automaticTarget: server && settings.defaultTarget === "server" ? "server" : "local",
      pauseUntil: scenario.paused ? now() + 4 * 60 * 1000 - 1000 : 0, settings,
      leftInBrowser: scenario.left ? data.leftInBrowser.map((note, index) => ({ ...note, at: now() - (index + 1) * 60 * 1000 })) : [],
      jobs,
    };
  }

  const handlers = {
    "dm.status": status,
    "dm.jobs": () => ({
      ok: true,
      jobs: scenario.localError && !server ? [] : jobs.filter((job) => !(scenario.serverDown && job.target === "server")),
      groups: server ? [{ target: "local", ok: !localError }, { target: "server", ok: !scenario.serverDown }] : undefined,
    }),
    "dm.askQueue": () => ({ ok: true, items: scenario.ask ? data.ask.slice(0, scenario.askCount ?? 1) : [] }),
    "dm.getHandoff": () => ({
      ok: true, handoff: data.handoff, held: true,
      defaults: { destination: targets[0].defaultFolder, favorites: targets[0].favorites, startMode: settings.startMode },
      targets, target: server && settings.defaultTarget === "server" ? "server" : "local",
    }),
    "dm.targets": () => ({ ok: true, targets, target: server && settings.defaultTarget === "server" ? "server" : "local",
      startMode: settings.startMode, theme: settings.theme }),
    "dm.pageLinks": () => scenario.linksError === "notWeb"
      ? { ok: false, notWebPage: true, error: "Download all links works on web pages. This page has no links to collect." }
      : scenario.linksPending ? new Promise(() => undefined)
      : { ok: true, tabId: 5, pageUrl: data.page.pageUrl, pageTitle: data.page.pageTitle, links: scenario.noLinks ? [] : data.page.links },
    "dm.browseServerFolders": (message) => data.serverFolders[message.path ?? ""] ?? data.serverFolders["/downloads"],
    "dm.ping": () => (localError ? { ok: false, error: localError } : { ok: true }),
  };

  const listeners = [];
  const store = { settings };
  window.chrome = {
    runtime: {
      id: "pknddmneejadnnngffojnoecojhdfalc",
      getURL: (path) => new URL(path, location.origin + "/").href,
      getManifest: () => ({ version: "0.6.0" }),
      sendMessage: async (message) => {
        const handler = handlers[message?.type];
        return handler ? handler(message) : { ok: true };
      },
      connect: () => ({ postMessage() {}, disconnect() {}, onMessage: { addListener() {} }, onDisconnect: { addListener() {} } }),
      openOptionsPage: async () => undefined,
    },
    storage: {
      local: { get: async () => ({ ...store }), set: async (values) => Object.assign(store, values) },
      onChanged: { addListener: (listener) => listeners.push(listener) },
    },
    permissions: { contains: async () => !scenario.noSiteAccess, request: async () => true },
    tabs: { create: async () => ({}), getCurrent: async () => undefined, remove: async () => undefined },
    windows: { getCurrent: async () => ({ id: 1, type: "normal" }), update: async () => undefined },
  };
})();
