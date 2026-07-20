// @ts-nocheck
import { trigger } from "cs2/api";
import React from "react";
import { COLORS } from "../selection/selectionStyles";

declare const __RT_UI_MODULE_DEV__: boolean;

export const NATIVE_WORKBENCH_PANEL_ID = "RapidTransitMod.DispatchWorkbenchNativePanel";
const NATIVE_WORKBENCH_UI_BUILD = "0.1.0-native-r4e";
const NATIVE_WORKBENCH_SCHEDULE_GLOBAL = "RTDispatchWorkbenchNativeSchedule";
const NATIVE_WORKBENCH_PAGE_GLOBAL = "__RT_NATIVE_WORKBENCH_PAGE__";
const NATIVE_WORKBENCH_PAGE_EVENT = "rt-native-workbench-pagechange";
const NATIVE_WORKBENCH_DEBUG_FLAGS_EVENT = "rt-native-workbench-debug-flags";

let nativeWorkbenchTraceSeq = 0;
let nativeWorkbenchTraceLifecycleRegistered = false;
let nativeWorkbenchScheduleLoader = null;
let nativeWorkbenchSchedulePrewarmStarted = false;
let nativeWorkbenchPersistentHostElement = null;
let nativeWorkbenchPersistentBundleHandle = null;
let nativeWorkbenchPersistentBundlePromise = null;
let nativeWorkbenchParkingLotElement = null;
let nativeWorkbenchParkingHideTimer = 0;
let nativeWorkbenchParkingShowRaf = 0;
let nativeWorkbenchSlotSyncRaf = 0;
let nativeWorkbenchSlotSyncUntil = 0;
let nativeWorkbenchTrackedSlotElement = null;
let nativeWorkbenchSlotResizeObserver = null;
let nativeWorkbenchLastSyncedRect = null;
let nativeWorkbenchParkingBaseScale = 1;
let nativeWorkbenchParkingCurrentScale = 1;
let nativeWorkbenchParkingCurrentOpacity = 1;
let nativeWorkbenchParkingCurrentInteractive = false;
let nativeWorkbenchParkingCurrentShellRect = null;
let nativeWorkbenchOpenToken = 0;

function getNativeWorkbenchDebugToolsEnabled() {
  return typeof window !== "undefined" && window.__RT_DEBUG_TOOLS__ === true;
}

function getNativeWorkbenchRescueToolsVisible() {
  try {
    return __RT_UI_MODULE_DEV__ === true;
  } catch {
    return false;
  }
}

function getNativeWorkbenchDebugToolsVisible() {
  return getNativeWorkbenchDebugToolsEnabled() || getNativeWorkbenchRescueToolsVisible();
}

function getNativeWorkbenchVerboseLogsEnabled() {
  return typeof window !== "undefined" && window.__RT_VERBOSE_LOGS__ === true;
}

function isNativeWorkbenchTraceEnabled() {
  return getNativeWorkbenchDebugToolsEnabled() || getNativeWorkbenchVerboseLogsEnabled();
}

function handleNativeWorkbenchPageHide() {
  traceNativeWorkbench("window.pagehide");
}

function handleNativeWorkbenchBeforeUnload() {
  traceNativeWorkbench("window.beforeunload");
}

function ensureNativeWorkbenchTraceHooks() {
  if (typeof window === "undefined" || !isNativeWorkbenchTraceEnabled() || nativeWorkbenchTraceLifecycleRegistered) {
    return;
  }

  window.__RTWB_TRACE__ = traceNativeWorkbench;
  window.addEventListener("pagehide", handleNativeWorkbenchPageHide);
  window.addEventListener("beforeunload", handleNativeWorkbenchBeforeUnload);
  nativeWorkbenchTraceLifecycleRegistered = true;
}

function updateNativeWorkbenchDebugFlags(debugTools, verboseLogs) {
  if (typeof window === "undefined") {
    return;
  }

  window.__RT_DEBUG_TOOLS__ = debugTools === true;
  window.__RT_VERBOSE_LOGS__ = verboseLogs === true;

  if (isNativeWorkbenchTraceEnabled()) {
    ensureNativeWorkbenchTraceHooks();
  } else {
    try {
      delete window.__RTWB_TRACE__;
    } catch (error) {
      window.__RTWB_TRACE__ = undefined;
    }
  }

  window.dispatchEvent(new CustomEvent(NATIVE_WORKBENCH_DEBUG_FLAGS_EVENT, {
    detail: {
      debugTools: window.__RT_DEBUG_TOOLS__ === true,
      verboseLogs: window.__RT_VERBOSE_LOGS__ === true
    }
  }));
}

function parseNativeWorkbenchBuildFlavor(payload) {
  if (!payload) {
    return null;
  }

  if (typeof payload === "string") {
    try {
      return JSON.parse(payload);
    } catch (error) {
      return null;
    }
  }

  return typeof payload === "object" ? payload : null;
}

function loadNativeWorkbenchBuildFlavor() {
  if (typeof window === "undefined") {
    return Promise.resolve();
  }

  updateNativeWorkbenchDebugFlags(false, false);

  if (!window.engine || typeof window.engine.call !== "function") {
    return Promise.resolve();
  }

  return window.engine.call("suhua::rt.workbench.getBuildFlavor").then((payload) => {
    const buildFlavor = parseNativeWorkbenchBuildFlavor(payload);
    if (!buildFlavor) {
      return;
    }

    updateNativeWorkbenchDebugFlags(buildFlavor.debugTools === true, buildFlavor.verboseLogs === true);
  }).catch(() => {});
}

function traceNativeWorkbench(eventName, details) {
  return;
}

if (typeof window !== "undefined") {
  window.__RT_DEBUG_TOOLS__ = false;
  window.__RT_VERBOSE_LOGS__ = false;
  loadNativeWorkbenchBuildFlavor();
}

function getNativeWorkbenchScheduleScriptUrl(cacheBustToken) {
  const token = cacheBustToken || Date.now().toString();
  const localeCode = typeof window.__RT_NATIVE_SCHEDULE_LOCALE__ === "string" ? window.__RT_NATIVE_SCHEDULE_LOCALE__ : "";
  return "coui://rapidtransitmod/UI/workbench/workbench.js?rt_live=1&rt_build=" + encodeURIComponent(NATIVE_WORKBENCH_UI_BUILD) + "&rt_ts=" + encodeURIComponent(token) + "&rt_locale=" + encodeURIComponent(localeCode || "");
}

function getNativeWorkbenchScheduleCssUrl(cacheBustToken) {
  const token = cacheBustToken || Date.now().toString();
  const localeCode = typeof window.__RT_NATIVE_SCHEDULE_LOCALE__ === "string" ? window.__RT_NATIVE_SCHEDULE_LOCALE__ : "";
  return "coui://rapidtransitmod/UI/workbench/workbench.css?rt_live=1&rt_build=" + encodeURIComponent(NATIVE_WORKBENCH_UI_BUILD) + "&rt_ts=" + encodeURIComponent(token) + "&rt_locale=" + encodeURIComponent(localeCode || "");
}

export function setNativeWorkbenchPage(nextPage) {
  if (typeof window === "undefined") {
    return;
  }

  const resolvedPage = nextPage === "broadcast"
    ? "broadcast"
    : nextPage === "overview"
      ? "overview"
      : "schedule";
  traceNativeWorkbench("host.page.set", { next: resolvedPage });
  window[NATIVE_WORKBENCH_PAGE_GLOBAL] = resolvedPage;
  window.dispatchEvent(new CustomEvent(NATIVE_WORKBENCH_PAGE_EVENT, {
    detail: {
      page: resolvedPage
    }
  }));
}

function setNativeWorkbenchCloseHandler(handler) {
  if (typeof window === "undefined") {
    return;
  }

  if (typeof handler === "function") {
    window.__RT_NATIVE_WORKBENCH_CLOSE__ = handler;
    return;
  }

  try {
    delete window.__RT_NATIVE_WORKBENCH_CLOSE__;
  } catch (error) {
    window.__RT_NATIVE_WORKBENCH_CLOSE__ = undefined;
  }
}

function resetNativeWorkbenchScheduleLoader() {
  traceNativeWorkbench("loader.reset");
  nativeWorkbenchScheduleLoader = null;
  const scripts = document.querySelectorAll('script[data-rt-native-schedule-script="true"]');
  scripts.forEach((script) => {
    if (script && script.parentNode) {
      script.parentNode.removeChild(script);
    }
  });

  try {
    delete window[NATIVE_WORKBENCH_SCHEDULE_GLOBAL];
  } catch (error) {
    window[NATIVE_WORKBENCH_SCHEDULE_GLOBAL] = undefined;
  }
}

function ensureNativeWorkbenchScheduleShadowCss(shadowRoot, cacheBustToken) {
  return new Promise((resolve, reject) => {
    if (!shadowRoot) {
      reject(new Error("Native workbench shadow root is unavailable."));
      return;
    }

    const href = getNativeWorkbenchScheduleCssUrl(cacheBustToken);
    const existing = shadowRoot.querySelector('link[data-rt-native-shadow-css-link="true"]');
    if (existing) {
      if (String(existing.getAttribute("href") || "") === href) {
        traceNativeWorkbench("shadow.css.reuse");
        resolve(existing);
        return;
      }
      traceNativeWorkbench("shadow.css.replace");
      shadowRoot.removeChild(existing);
    }

    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = href;
    link.setAttribute("data-rt-native-shadow-css-link", "true");
    link.onload = () => {
      traceNativeWorkbench("shadow.css.loaded");
      resolve(link);
    };
    link.onerror = () => {
      traceNativeWorkbench("shadow.css.error");
      reject(new Error("Native schedule CSS link failed to load."));
    };
    traceNativeWorkbench("shadow.css.append");
    shadowRoot.appendChild(link);
  });
}

function ensureNativeWorkbenchScheduleLoader(forceReload, cacheBustToken) {
  if (forceReload) {
    resetNativeWorkbenchScheduleLoader();
  }

  const existingApi = window[NATIVE_WORKBENCH_SCHEDULE_GLOBAL];
  if (existingApi && typeof existingApi.mount === "function") {
    traceNativeWorkbench("loader.reuse.global");
    return Promise.resolve(existingApi);
  }

  if (nativeWorkbenchScheduleLoader) {
    traceNativeWorkbench("loader.reuse.promise");
    return nativeWorkbenchScheduleLoader;
  }

  traceNativeWorkbench("loader.create", { forceReload: forceReload === true });
  nativeWorkbenchScheduleLoader = new Promise((resolve, reject) => {
    if (typeof window.__RT_NATIVE_SCHEDULE_LOCALE__ !== "string") {
      window.__RT_NATIVE_SCHEDULE_LOCALE__ = "";
    }

    const script = document.createElement("script");
    script.src = getNativeWorkbenchScheduleScriptUrl(cacheBustToken);
    script.async = true;
    script.setAttribute("data-rt-native-schedule-script", "true");
    script.onload = () => {
      traceNativeWorkbench("loader.script.loaded");
      const mountedApi = window[NATIVE_WORKBENCH_SCHEDULE_GLOBAL];
      if (mountedApi && typeof mountedApi.mount === "function") {
        traceNativeWorkbench("loader.api.ready");
        resolve(mountedApi);
        return;
      }

      nativeWorkbenchScheduleLoader = null;
      traceNativeWorkbench("loader.api.missing");
      reject(new Error("Native schedule loader did not register a mount API."));
    };
    script.onerror = () => {
      nativeWorkbenchScheduleLoader = null;
      traceNativeWorkbench("loader.script.error");
      reject(new Error("Native schedule script failed to load."));
    };
    document.body.appendChild(script);
  });

  return nativeWorkbenchScheduleLoader;
}

function ensureNativeWorkbenchParkingLot() {
  if (nativeWorkbenchParkingLotElement instanceof HTMLElement) {
    return nativeWorkbenchParkingLotElement;
  }

  const parkingLot = document.createElement("div");
  parkingLot.setAttribute("data-rt-native-workbench-parking", "true");
  parkingLot.style.position = "fixed";
  parkingLot.style.left = "-99999px";
  parkingLot.style.top = "0";
  parkingLot.style.width = "1520px";
  parkingLot.style.height = "980px";
  parkingLot.style.overflow = "hidden";
  parkingLot.style.pointerEvents = "none";
  parkingLot.style.opacity = "0";
  parkingLot.style.visibility = "hidden";
  parkingLot.style.zIndex = "100";
  parkingLot.style.transitionProperty = "opacity, transform";
  parkingLot.style.transitionTimingFunction = "ease";
  parkingLot.style.transitionDuration = "150ms";
  parkingLot.style.transformOrigin = "top left";
  parkingLot.style.transform = "translate(0px, 0px) scale(1)";
  document.body.appendChild(parkingLot);
  nativeWorkbenchParkingLotElement = parkingLot;
  traceNativeWorkbench("parking.create");
  return parkingLot;
}

function ensureNativeWorkbenchPersistentHostElement() {
  if (nativeWorkbenchPersistentHostElement instanceof HTMLElement) {
    return nativeWorkbenchPersistentHostElement;
  }

  const hostElement = document.createElement("div");
  hostElement.setAttribute("data-rt-native-workbench-host", "true");
  hostElement.style.display = "block";
  hostElement.style.width = "100%";
  hostElement.style.height = "100%";
  hostElement.style.minHeight = "0";
  ensureNativeWorkbenchParkingLot().appendChild(hostElement);
  nativeWorkbenchPersistentHostElement = hostElement;
  traceNativeWorkbench("host.create");
  return hostElement;
}

function syncNativeWorkbenchParkingLotToTarget(targetElement) {
  if (!(targetElement instanceof HTMLElement)) {
    return false;
  }

  const rect = targetElement.getBoundingClientRect();
  if (!(rect.width > 0) || !(rect.height > 0)) {
    return false;
  }

  const nextRect = {
    left: rect.left,
    top: rect.top,
    width: rect.width,
    height: rect.height
  };

  if (nativeWorkbenchLastSyncedRect) {
    const threshold = 0.75;
    const rectChangedEnough = Math.abs(nativeWorkbenchLastSyncedRect.left - nextRect.left) >= threshold
      || Math.abs(nativeWorkbenchLastSyncedRect.top - nextRect.top) >= threshold
      || Math.abs(nativeWorkbenchLastSyncedRect.width - nextRect.width) >= threshold
      || Math.abs(nativeWorkbenchLastSyncedRect.height - nextRect.height) >= threshold;

    if (!rectChangedEnough) {
      return true;
    }
  }

  const parkingLot = ensureNativeWorkbenchParkingLot();
  parkingLot.style.left = nextRect.left.toString() + "px";
  parkingLot.style.top = nextRect.top.toString() + "px";
  parkingLot.style.width = nextRect.width.toString() + "px";
  parkingLot.style.height = nextRect.height.toString() + "px";
  nativeWorkbenchLastSyncedRect = nextRect;
  nativeWorkbenchParkingBaseScale = nativeWorkbenchParkingCurrentScale > 0
    ? nativeWorkbenchParkingCurrentScale
    : 1;
  applyNativeWorkbenchPersistentHostVisualState({
    durationMs: 0
  });
  return true;
}

function stopNativeWorkbenchSlotTracking(options) {
  const settings = options || {};
  if (nativeWorkbenchSlotSyncRaf) {
    window.cancelAnimationFrame(nativeWorkbenchSlotSyncRaf);
    nativeWorkbenchSlotSyncRaf = 0;
  }
  nativeWorkbenchSlotSyncUntil = 0;
  nativeWorkbenchTrackedSlotElement = null;
  if (settings.preserveRect !== true) {
    nativeWorkbenchLastSyncedRect = null;
  }
  if (nativeWorkbenchSlotResizeObserver) {
    nativeWorkbenchSlotResizeObserver.disconnect();
    nativeWorkbenchSlotResizeObserver = null;
  }
}

function scheduleNativeWorkbenchSlotSync() {
  if (nativeWorkbenchSlotSyncRaf || !(nativeWorkbenchTrackedSlotElement instanceof HTMLElement)) {
    return;
  }

  nativeWorkbenchSlotSyncRaf = window.requestAnimationFrame(() => {
    nativeWorkbenchSlotSyncRaf = 0;
    syncNativeWorkbenchParkingLotToTarget(nativeWorkbenchTrackedSlotElement);
    const now = typeof performance !== "undefined" && typeof performance.now === "function"
      ? performance.now()
      : Date.now();
    if (now < nativeWorkbenchSlotSyncUntil) {
      scheduleNativeWorkbenchSlotSync();
    }
  });
}

function startNativeWorkbenchSlotTracking(targetElement) {
  if (!(targetElement instanceof HTMLElement)) {
    return;
  }

  nativeWorkbenchTrackedSlotElement = targetElement;
  nativeWorkbenchSlotSyncUntil = (typeof performance !== "undefined" && typeof performance.now === "function"
    ? performance.now()
    : Date.now()) + 280;
  scheduleNativeWorkbenchSlotSync();
}

function applyNativeWorkbenchPersistentHostVisualState(options) {
  const settings = options || {};
  const parkingLot = ensureNativeWorkbenchParkingLot();
  const nextScale = typeof settings.scale === "number" && settings.scale > 0
    ? settings.scale
    : nativeWorkbenchParkingCurrentScale;
  const nextOpacity = typeof settings.opacity === "number"
    ? settings.opacity
    : nativeWorkbenchParkingCurrentOpacity;
  const nextInteractive = typeof settings.interactive === "boolean"
    ? settings.interactive
    : nativeWorkbenchParkingCurrentInteractive;
  const nextShellRect = settings.shellRect && typeof settings.shellRect === "object"
    ? {
        left: settings.shellRect.left,
        top: settings.shellRect.top,
        width: settings.shellRect.width,
        height: settings.shellRect.height
      }
    : nativeWorkbenchParkingCurrentShellRect;

  nativeWorkbenchParkingCurrentScale = nextScale;
  nativeWorkbenchParkingCurrentOpacity = nextOpacity;
  nativeWorkbenchParkingCurrentInteractive = nextInteractive;
  nativeWorkbenchParkingCurrentShellRect = nextShellRect;

  if (typeof settings.durationMs === "number") {
    parkingLot.style.transitionDuration = settings.durationMs.toString() + "ms";
  }
  if (typeof settings.timingFunction === "string" && settings.timingFunction.length > 0) {
    parkingLot.style.transitionTimingFunction = settings.timingFunction;
  }

  parkingLot.style.visibility = "visible";
  parkingLot.style.opacity = nextOpacity.toString();
  parkingLot.style.pointerEvents = nextInteractive ? "auto" : "none";

  if (!nativeWorkbenchLastSyncedRect || !nextShellRect) {
    parkingLot.style.transform = "translate(0px, 0px) scale(1)";
    return;
  }

  const baseScale = nativeWorkbenchParkingBaseScale > 0 ? nativeWorkbenchParkingBaseScale : 1;
  const relativeScale = nextScale / baseScale;
  const shellCenterX = nextShellRect.left + nextShellRect.width / 2;
  const shellCenterY = nextShellRect.top + nextShellRect.height / 2;
  const dx = (nativeWorkbenchLastSyncedRect.left - shellCenterX) * (relativeScale - 1);
  const dy = (nativeWorkbenchLastSyncedRect.top - shellCenterY) * (relativeScale - 1);

  parkingLot.style.transform = "translate(" + dx.toFixed(3) + "px, " + dy.toFixed(3) + "px) scale(" + relativeScale.toFixed(5) + ")";
}

function showNativeWorkbenchPersistentHostAnimated(options) {
  if (nativeWorkbenchParkingShowRaf) {
    window.cancelAnimationFrame(nativeWorkbenchParkingShowRaf);
    nativeWorkbenchParkingShowRaf = 0;
  }
  if (nativeWorkbenchParkingHideTimer) {
    window.clearTimeout(nativeWorkbenchParkingHideTimer);
    nativeWorkbenchParkingHideTimer = 0;
  }
  const settings = options || {};
  const parkingLot = ensureNativeWorkbenchParkingLot();
  parkingLot.style.visibility = "visible";
  if (typeof settings.durationMs === "number") {
    parkingLot.style.transitionDuration = settings.durationMs.toString() + "ms";
  }
  nativeWorkbenchParkingShowRaf = window.requestAnimationFrame(() => {
    nativeWorkbenchParkingShowRaf = 0;
    applyNativeWorkbenchPersistentHostVisualState({
      durationMs: 0,
      opacity: typeof settings.opacity === "number" ? settings.opacity : nativeWorkbenchParkingCurrentOpacity,
      scale: typeof settings.scale === "number" ? settings.scale : nativeWorkbenchParkingCurrentScale,
      interactive: typeof settings.interactive === "boolean" ? settings.interactive : nativeWorkbenchParkingCurrentInteractive
    });
  });
}

function resetNativeWorkbenchParkingLotBounds(parkingLot) {
  if (!(parkingLot instanceof HTMLElement)) {
    return;
  }

  parkingLot.style.left = "-99999px";
  parkingLot.style.top = "0";
  parkingLot.style.width = "1520px";
  parkingLot.style.height = "980px";
  parkingLot.style.transform = "translate(0px, 0px) scale(1)";
}

function parkNativeWorkbenchPersistentHost() {
  nativeWorkbenchOpenToken += 1;
  traceNativeWorkbench("parking.park", { openToken: nativeWorkbenchOpenToken });
  reportNativeWorkbenchHostState("parked");
  stopNativeWorkbenchSlotTracking();
  const parkingLot = ensureNativeWorkbenchParkingLot();
  if (nativeWorkbenchParkingShowRaf) {
    window.cancelAnimationFrame(nativeWorkbenchParkingShowRaf);
    nativeWorkbenchParkingShowRaf = 0;
  }
  if (nativeWorkbenchParkingHideTimer) {
    window.clearTimeout(nativeWorkbenchParkingHideTimer);
    nativeWorkbenchParkingHideTimer = 0;
  }

  nativeWorkbenchParkingCurrentInteractive = false;
  nativeWorkbenchParkingCurrentOpacity = 0;
  nativeWorkbenchParkingCurrentScale = 1;
  nativeWorkbenchParkingCurrentShellRect = null;
  nativeWorkbenchParkingBaseScale = 1;
  parkingLot.style.pointerEvents = "none";
  parkingLot.style.transitionDuration = "0ms";
  parkingLot.style.opacity = "0";
  parkingLot.style.visibility = "hidden";
  resetNativeWorkbenchParkingLotBounds(parkingLot);
}

function attachNativeWorkbenchPersistentHost(targetElement, options) {
  if (!(targetElement instanceof HTMLElement)) {
    throw new Error("Native workbench host slot is unavailable.");
  }

  const settings = options || {};
  traceNativeWorkbench("parking.attach", {
    deferShow: settings.deferShow === true,
    track: settings.track !== false,
    interactive: settings.interactive === true
  });
  const hostElement = ensureNativeWorkbenchPersistentHostElement();
  const parkingLot = ensureNativeWorkbenchParkingLot();
  if (nativeWorkbenchParkingHideTimer) {
    window.clearTimeout(nativeWorkbenchParkingHideTimer);
    nativeWorkbenchParkingHideTimer = 0;
  }
  if (nativeWorkbenchParkingShowRaf) {
    window.cancelAnimationFrame(nativeWorkbenchParkingShowRaf);
    nativeWorkbenchParkingShowRaf = 0;
  }
  syncNativeWorkbenchParkingLotToTarget(targetElement);
  if (settings.track !== false) {
    startNativeWorkbenchSlotTracking(targetElement);
  } else {
    stopNativeWorkbenchSlotTracking({
      preserveRect: true
    });
  }
  parkingLot.style.visibility = "visible";
  parkingLot.style.transitionDuration = "0ms";
  parkingLot.style.opacity = "0";
  parkingLot.style.pointerEvents = settings.interactive === true ? "auto" : "none";
  if (settings.deferShow !== true) {
    showNativeWorkbenchPersistentHostAnimated({
      durationMs: typeof settings.durationMs === "number" ? settings.durationMs : 0,
      interactive: settings.interactive === true,
      opacity: typeof settings.opacity === "number" ? settings.opacity : nativeWorkbenchParkingCurrentOpacity,
      scale: typeof settings.scale === "number" ? settings.scale : nativeWorkbenchParkingCurrentScale
    });
  }
  return hostElement;
}

async function ensureNativeWorkbenchPersistentBundle(options) {
  const settings = options || {};
  const forceReload = settings.forceReload === true;
  const forceRemount = settings.forceRemount === true;
  traceNativeWorkbench("bundle.ensure", { forceReload, forceRemount });

  if (!forceReload && !forceRemount && nativeWorkbenchPersistentBundlePromise) {
    traceNativeWorkbench("bundle.reuse.promise");
    return nativeWorkbenchPersistentBundlePromise;
  }

  const nextPromise = (async () => {
    const hostElement = ensureNativeWorkbenchPersistentHostElement();
    const shadowSurface = ensureNativeWorkbenchShadowSurface(hostElement);
    const cacheBustToken = Date.now().toString();

    if ((forceReload || forceRemount) && nativeWorkbenchPersistentBundleHandle && typeof nativeWorkbenchPersistentBundleHandle.unmount === "function") {
      traceNativeWorkbench("bundle.unmount.before-remount", { forceReload, forceRemount });
      nativeWorkbenchPersistentBundleHandle.unmount();
      nativeWorkbenchPersistentBundleHandle = null;
    }

    if (forceReload || !shadowSurface.shadowRoot.querySelector('link[data-rt-native-shadow-css-link="true"]')) {
      await ensureNativeWorkbenchScheduleShadowCss(shadowSurface.shadowRoot, cacheBustToken);
    }

    const bundleApi = await ensureNativeWorkbenchScheduleLoader(forceReload, cacheBustToken);
    if (!nativeWorkbenchPersistentBundleHandle) {
      traceNativeWorkbench("bundle.mount.begin");
      const mountedHandle = bundleApi.mount(shadowSurface.mountNode);
      nativeWorkbenchPersistentBundleHandle = mountedHandle && typeof mountedHandle === "object"
        ? mountedHandle
        : {
            refreshData: async () => {},
            unmount: () => {
              if (typeof bundleApi.unmount === "function") {
                bundleApi.unmount(shadowSurface.mountNode);
              }
            }
          };
      traceNativeWorkbench("bundle.mount.done");
    }

    return {
      hostElement,
      handle: nativeWorkbenchPersistentBundleHandle
    };
  })();

  nativeWorkbenchPersistentBundlePromise = nextPromise;
  try {
    const result = await nextPromise;
    traceNativeWorkbench("bundle.ensure.done");
    return result;
  } catch (error) {
    if (nativeWorkbenchPersistentBundlePromise === nextPromise) {
      nativeWorkbenchPersistentBundlePromise = null;
    }
    traceNativeWorkbench("bundle.ensure.error", { message: error && error.message ? error.message : error });
    throw error;
  }
}

export function prewarmNativeWorkbenchScheduleLoader() {
  if (nativeWorkbenchSchedulePrewarmStarted) {
    return;
  }

  nativeWorkbenchSchedulePrewarmStarted = true;
  const launchPrewarm = () => {
    traceNativeWorkbench("prewarm.begin");
    ensureNativeWorkbenchPersistentBundle({ forceReload: false, forceRemount: false }).catch(() => {
      nativeWorkbenchSchedulePrewarmStarted = false;
      traceNativeWorkbench("prewarm.error");
    });
  };

  if (typeof window.requestAnimationFrame === "function") {
    window.requestAnimationFrame(() => {
      window.setTimeout(launchPrewarm, 200);
    });
    return;
  }

  window.setTimeout(launchPrewarm, 200);
}

function reportNativeWorkbenchHostState(phase) {
  if (!window.engine || typeof window.engine.call !== "function") {
    return;
  }

  window.engine.call("suhua::rt.workbench.setWorkbenchHostState", JSON.stringify({
    phase: phase || "unknown",
    mode: window.__RT_WORKBENCH_ACTIVE_TRANSPORT_MODE__ || "train",
    activePage: window.__RT_WORKBENCH_ACTIVE_PAGE__ || "",
    selectedLineId: window.__RT_WORKBENCH_SELECTED_LINE_ID__ || "",
    selectedEditLine: window.__RT_WORKBENCH_SELECTED_EDIT_LINE__ || ""
  })).catch(() => {});
}

function formatProbeError(error) {
  if (!error) {
    return "unknown error";
  }

  if (typeof error.message === "string" && error.message.length > 0) {
    return error.message;
  }

  return String(error);
}

function getNativeWorkbenchGameFontFamily() {
  try {
    const rootStyle = window.getComputedStyle(document.documentElement);
    const rootFont = rootStyle?.getPropertyValue("--fontFamily")?.trim();
    if (rootFont) {
      return rootFont;
    }

    const bodyFont = document.body ? window.getComputedStyle(document.body).fontFamily : "";
    if (bodyFont) {
      return bodyFont;
    }
  } catch {}

  return "var(--fontFamily)";
}

function ensureNativeWorkbenchShadowSurface(hostElement) {
  if (!(hostElement instanceof HTMLElement)) {
    throw new Error("Native workbench host element is unavailable.");
  }

  const gameFontFamily = getNativeWorkbenchGameFontFamily();
  hostElement.style.setProperty("--rt-game-font-family", gameFontFamily);
  hostElement.style.setProperty("--dw-native-font-family", gameFontFamily);

  let shadowRoot = hostElement.shadowRoot;
  if (!shadowRoot) {
    if (typeof hostElement.attachShadow !== "function") {
      throw new Error("attachShadow is unavailable on the native workbench host.");
    }
    shadowRoot = hostElement.attachShadow({ mode: "open" });
  }

  let baseStyle = shadowRoot.querySelector('style[data-rt-native-shadow-base="true"]');
  if (!baseStyle) {
    baseStyle = document.createElement("style");
    baseStyle.setAttribute("data-rt-native-shadow-base", "true");
    baseStyle.textContent = [
      ":host {",
      "  display: block;",
      "  width: 100%;",
      "  height: 100%;",
      "  min-height: 100%;",
      "  --bg: #142332;",
      "  --bg-2: #182a3b;",
      "  --panel: rgba(18, 31, 44, 0.94);",
      "  --panel-2: rgba(24, 39, 55, 0.97);",
      "  --line: rgba(145, 190, 210, 0.18);",
      "  --line-strong: rgba(145, 190, 210, 0.28);",
      "  --text: #edf5f9;",
      "  --muted: #aebfca;",
      "  --accent: #4f90a8;",
      "  --accent-2: #5ea4bf;",
      "  --warn: #d48353;",
      "  --local: #65d8ef;",
      "  --express: #f0b165;",
      "  --bad: #d37272;",
      "  --good: #88c08f;",
      "  color: #edf5f9;",
      "  font-family: var(--rt-game-font-family);",
      "}",
      ":host *, :host input, :host button, :host select, :host textarea {",
      "  font-family: inherit;",
      "}",
      ".rt-native-schedule-surface {",
      "  width: 100%;",
      "  height: 100%;",
      "  min-height: 100%;",
      "  overflow: hidden;",
      "}",
      ".rt-native-schedule-mount {",
      "  width: 100%;",
      "  height: 100%;",
      "  min-height: 100%;",
      "}",
      ".rt-native-schedule-mount > .dw-native-schedule-root {",
      "  height: 100%;",
      "}"
    ].join("\n");
    shadowRoot.appendChild(baseStyle);
  }

  let surface = shadowRoot.querySelector(".rt-native-schedule-surface");
  if (!(surface instanceof HTMLElement)) {
    surface = document.createElement("div");
    surface.className = "rt-native-schedule-surface";
    shadowRoot.appendChild(surface);
  }

  let mountNode = shadowRoot.querySelector(".rt-native-schedule-mount");
  if (!(mountNode instanceof HTMLElement)) {
    mountNode = document.createElement("div");
    mountNode.className = "rt-native-schedule-mount";
    surface.appendChild(mountNode);
  } else if (mountNode.parentNode !== surface) {
    surface.appendChild(mountNode);
  }

  return {
    shadowRoot,
    mountNode
  };
}

const NATIVE_WORKBENCH_OPEN_SCALE = 1;
const NATIVE_WORKBENCH_CLOSED_SCALE = 0.95;
const NATIVE_WORKBENCH_OPEN_DURATION_MS = 250;
const NATIVE_WORKBENCH_CLOSE_DURATION_MS = 160;
const NATIVE_WORKBENCH_OPEN_SETTLE_DELAY_MS = NATIVE_WORKBENCH_OPEN_DURATION_MS + 32;

const RapidTransitWorkbenchBundleHost = (props) => {
  const hostSlotRef = props.hostSlotRef;
  const busyState = React.useState("");
  const busyAction = busyState[0];
  const setBusyAction = busyState[1];
  const debugToolsVisibleState = React.useState(getNativeWorkbenchDebugToolsVisible());
  const debugToolsVisible = debugToolsVisibleState[0];
  const setDebugToolsVisible = debugToolsVisibleState[1];
  const statusState = React.useState({
    tone: "info",
    text: "Loading native schedule bundle into the workbench shell..."
  });
  const status = statusState[0];
  const setStatus = statusState[1];
  const mountError = typeof props.mountError === "string" ? props.mountError : "";

  React.useEffect(() => {
    const syncDebugToolsVisible = () => {
      setDebugToolsVisible(getNativeWorkbenchDebugToolsVisible());
    };

    syncDebugToolsVisible();
    window.addEventListener(NATIVE_WORKBENCH_DEBUG_FLAGS_EVENT, syncDebugToolsVisible);
    return () => {
      window.removeEventListener(NATIVE_WORKBENCH_DEBUG_FLAGS_EVENT, syncDebugToolsVisible);
    };
  }, []);

  async function mountNativeWorkbenchBundle(forceReload, forceRemount) {
    const slotElement = hostSlotRef.current;
    const openToken = nativeWorkbenchOpenToken + 1;
    nativeWorkbenchOpenToken = openToken;
    traceNativeWorkbench("bundle.host.mount-request", { forceReload: forceReload === true, forceRemount: forceRemount === true, openToken });
    attachNativeWorkbenchPersistentHost(slotElement, {
      deferShow: true,
      interactive: nativeWorkbenchParkingCurrentInteractive
    });
    await ensureNativeWorkbenchPersistentBundle({
      forceReload: forceReload === true,
      forceRemount: forceRemount === true
    });
    if (nativeWorkbenchOpenToken !== openToken) {
      return;
    }
    syncNativeWorkbenchParkingLotToTarget(slotElement);
    applyNativeWorkbenchPersistentHostVisualState({
      durationMs: 0
    });
  }

  async function runBundleAction(action) {
    if (busyAction) {
      return;
    }

    traceNativeWorkbench("bundle.action.begin", { action });
    const nextBusyLabel = action === "reload"
      ? "Reload Bundle"
      : action === "remount"
        ? "Remount"
        : action === "rebind"
          ? "Rebind API"
          : "Refresh Data";
    setBusyAction(nextBusyLabel);

    try {
      if (action === "rebind") {
        trigger("RapidTransitPanel", "requestWorkbenchApiRebind");
        setStatus({
          tone: "success",
          text: "Rebind API requested."
        });
        traceNativeWorkbench("bundle.action.done", { action });
        return;
      }

      if (action === "refresh") {
        const bundleState = await ensureNativeWorkbenchPersistentBundle({
          forceReload: false,
          forceRemount: false
        });
        attachNativeWorkbenchPersistentHost(hostSlotRef.current, {
          interactive: nativeWorkbenchParkingCurrentInteractive
        });
        if (bundleState.handle && typeof bundleState.handle.refreshData === "function") {
          await bundleState.handle.refreshData();
        } else {
          await mountNativeWorkbenchBundle(false, false);
        }
        syncNativeWorkbenchParkingLotToTarget(hostSlotRef.current);
        applyNativeWorkbenchPersistentHostVisualState({
          durationMs: 0
        });
        setStatus({
          tone: "success",
          text: "Refresh Data completed."
        });
        traceNativeWorkbench("bundle.action.done", { action });
        return;
      }

      const isReload = action === "reload";
      const isRemount = action === "remount";
      await mountNativeWorkbenchBundle(isReload, isRemount);
      setStatus({
        tone: "success",
        text: isReload ? "Reload Bundle completed." : "Remount completed."
      });
      traceNativeWorkbench("bundle.action.done", { action });
    } catch (error) {
      traceNativeWorkbench("bundle.action.error", { action, message: error && error.message ? error.message : error });
      setStatus({
        tone: "error",
        text: nextBusyLabel + " failed: " + formatProbeError(error)
      });
    } finally {
      setBusyAction("");
    }
  }

  const buttonBaseStyle = {
    height: "34px",
    borderRadius: "8px",
    padding: "0 12px",
    fontSize: "13px",
    lineHeight: "18px",
    fontWeight: 700,
    cursor: busyAction ? "default" : "pointer",
    opacity: busyAction ? "0.70" : "1"
  };

  return React.createElement(
    "div",
    {
      style: {
        width: "100%",
        height: "100%",
        minHeight: "0",
        display: "flex",
        flexDirection: "column"
      }
    },
    debugToolsVisible ? React.createElement(
      "div",
      {
        style: {
          marginBottom: "8px",
          padding: "8px 10px",
          borderRadius: "8px",
          background: "rgba(24,42,66,0.42)",
          border: "1px solid rgba(182,214,238,0.20)"
        }
      },
      React.createElement(
        "div",
        {
          style: {
            display: "flex",
            alignItems: "center"
          }
        },
        React.createElement(
          "div",
          {
            style: {
              flex: "1 1 auto",
              minWidth: 0,
              paddingRight: "8px"
            }
          },
          React.createElement(
            "div",
            {
              style: {
                fontSize: "18px",
                lineHeight: "24px",
                fontWeight: 700,
                color: COLORS.title
              }
            },
            "Dispatch Workbench"
        )
      ),
        React.createElement(
          "button",
          {
            type: "button",
            onClick: () => runBundleAction("rebind"),
            disabled: !!busyAction,
            style: Object.assign({}, buttonBaseStyle, {
              marginRight: "6px",
              border: "1px solid rgba(238,204,134,0.34)",
              background: "rgba(157,113,46,0.52)",
              color: "#fff2cf"
            })
          },
          busyAction === "Rebind API" ? "Rebinding..." : "Rebind API"
        ),
        React.createElement(
          "button",
          {
            type: "button",
            onClick: () => runBundleAction("refresh"),
            disabled: !!busyAction,
            style: Object.assign({}, buttonBaseStyle, {
              marginRight: "6px",
              border: "0",
              background: "rgba(79,144,168,0.90)",
              color: "#f4fbff"
            })
          },
          "Refresh"
        ),
        React.createElement(
          "button",
          {
            type: "button",
            onClick: () => runBundleAction("remount"),
            disabled: !!busyAction,
            style: Object.assign({}, buttonBaseStyle, {
              marginRight: "6px",
              background: "rgba(8,14,20,0.72)",
              color: COLORS.text,
              border: "1px solid rgba(182,214,238,0.18)"
            })
          },
          "Remount"
        ),
        React.createElement(
          "button",
          {
            type: "button",
            onClick: () => runBundleAction("reload"),
            disabled: !!busyAction,
            style: Object.assign({}, buttonBaseStyle, {
              marginRight: "8px",
              background: "rgba(95,147,200,0.28)",
              color: COLORS.title,
              border: "1px solid rgba(182,214,238,0.24)"
            })
          },
          busyAction === "Reload Bundle" ? "Reloading..." : "Reload"
        ),
        React.createElement(
          "div",
          {
            style: {
              flex: "0 0 auto",
              padding: "4px 8px",
              borderRadius: "8px",
              background: "rgba(8,14,20,0.72)",
              border: "1px solid rgba(182,214,238,0.18)",
              color: COLORS.titleAccent,
              fontSize: "12px",
              lineHeight: "16px",
              fontWeight: 700
            }
          },
          "Build " + NATIVE_WORKBENCH_UI_BUILD
        )
      )
    ) : null,
    mountError
      ? React.createElement(
        "div",
        {
          style: {
            marginBottom: "8px",
            padding: "10px 12px",
            borderRadius: "8px",
            background: "rgba(114,57,70,0.32)",
            border: "1px solid rgba(207,125,115,0.26)",
            color: "#ffd6d1",
            fontSize: "13px",
            lineHeight: "18px"
          }
        },
        mountError
      )
      : status.tone === "error"
      ? React.createElement(
        "div",
        {
          style: {
            marginBottom: "8px",
            padding: "10px 12px",
            borderRadius: "8px",
            background: "rgba(114,57,70,0.32)",
            border: "1px solid rgba(207,125,115,0.26)",
            color: "#ffd6d1",
            fontSize: "13px",
            lineHeight: "18px"
          }
        },
        status.text
      )
      : null,
    React.createElement("div", {
      ref: hostSlotRef,
      style: {
        width: "100%",
        height: "100%",
        flex: "1 1 auto",
        minHeight: 0,
        background: "transparent"
      }
    })
  );
};

export const RapidTransitWorkbenchGamePanel = (props) => {
  const shellFrameRef = React.useRef(null);
  const hostSlotRef = React.useRef(null);
  const mountTimerRef = React.useRef(0);
  const openRafRef = React.useRef(0);
  const settleTimerRef = React.useRef(0);
  const closeTimerRef = React.useRef(0);
  const disposedRef = React.useRef(false);
  const closingRef = React.useRef(false);
  const propsRef = React.useRef(props);
  const shellOpenState = React.useState(false);
  const shellOpen = shellOpenState[0];
  const setShellOpen = shellOpenState[1];
  const shellInteractiveState = React.useState(false);
  const shellInteractive = shellInteractiveState[0];
  const setShellInteractive = shellInteractiveState[1];
  const mountErrorState = React.useState("");
  const mountError = mountErrorState[0];
  const setMountError = mountErrorState[1];
  propsRef.current = props;

  function syncParkingVisualState(nextOpen, nextInteractive, durationMs) {
    const shellElement = shellFrameRef.current;
    const shellRect = shellElement instanceof HTMLElement
      ? shellElement.getBoundingClientRect()
      : null;
    applyNativeWorkbenchPersistentHostVisualState({
      shellRect,
      scale: nextOpen ? NATIVE_WORKBENCH_OPEN_SCALE : NATIVE_WORKBENCH_CLOSED_SCALE,
      opacity: nextOpen ? 1 : 0,
      interactive: nextInteractive === true,
      durationMs,
      timingFunction: "ease"
    });
  }

  function clearWorkbenchShellTimers() {
    if (mountTimerRef.current) {
      window.clearTimeout(mountTimerRef.current);
      mountTimerRef.current = 0;
    }
    if (openRafRef.current) {
      window.cancelAnimationFrame(openRafRef.current);
      openRafRef.current = 0;
    }
    if (settleTimerRef.current) {
      window.clearTimeout(settleTimerRef.current);
      settleTimerRef.current = 0;
    }
    if (closeTimerRef.current) {
      window.clearTimeout(closeTimerRef.current);
      closeTimerRef.current = 0;
    }
  }

  function finalizeWorkbenchOpen() {
    const slotElement = hostSlotRef.current;
    if (!(slotElement instanceof HTMLElement)) {
      return;
    }

    traceNativeWorkbench("panel.open.finalize");
    reportNativeWorkbenchHostState("visible");
    syncNativeWorkbenchParkingLotToTarget(slotElement);
    startNativeWorkbenchSlotTracking(slotElement);
    syncParkingVisualState(true, true, 0);
  }

  const closeWorkbenchPanel = () => {
    if (closingRef.current) {
      return;
    }

    traceNativeWorkbench("panel.close.begin");
    reportNativeWorkbenchHostState("closing");
    closingRef.current = true;
    setNativeWorkbenchCloseHandler(null);
    stopNativeWorkbenchSlotTracking({
      preserveRect: true
    });
    clearWorkbenchShellTimers();
    setShellInteractive(false);
    setShellOpen(false);
    closeTimerRef.current = window.setTimeout(() => {
      closeTimerRef.current = 0;
      try {
        const currentProps = propsRef.current;
        if (currentProps && typeof currentProps.onClose === "function") {
          currentProps.onClose();
        } else {
          trigger("game", "togglePanel", NATIVE_WORKBENCH_PANEL_ID);
        }
        traceNativeWorkbench("panel.close.triggered");
      } catch (error) {
        closingRef.current = false;
        setNativeWorkbenchCloseHandler(closeWorkbenchPanel);
        setShellOpen(true);
        setShellInteractive(true);
        settleTimerRef.current = window.setTimeout(() => {
          settleTimerRef.current = 0;
          if (!disposedRef.current && !closingRef.current) {
            finalizeWorkbenchOpen();
          }
        }, NATIVE_WORKBENCH_OPEN_SETTLE_DELAY_MS);
        console.error("RapidTransit native workbench close failed", error);
      }
    }, NATIVE_WORKBENCH_CLOSE_DURATION_MS);
  };

  React.useEffect(() => {
    syncParkingVisualState(
      shellOpen,
      shellOpen && shellInteractive && !closingRef.current,
      shellOpen ? NATIVE_WORKBENCH_OPEN_DURATION_MS : NATIVE_WORKBENCH_CLOSE_DURATION_MS
    );
  }, [shellInteractive, shellOpen]);

  React.useEffect(() => {
    traceNativeWorkbench("panel.effect.mount");
    disposedRef.current = false;
    closingRef.current = false;
    setMountError("");
    setShellOpen(false);
    setShellInteractive(false);
    setNativeWorkbenchCloseHandler(closeWorkbenchPanel);
    setNativeWorkbenchPage("schedule");
    const handleKeyDown = (event) => {
      if (!event || event.defaultPrevented) {
        return;
      }
      if (event.key === "Escape" || event.key === "Esc") {
        event.preventDefault();
        closeWorkbenchPanel();
      }
    };
    window.addEventListener("keydown", handleKeyDown);
    const launchMount = () => {
      traceNativeWorkbench("panel.launch-mount");
      mountTimerRef.current = 0;
      const slotElement = hostSlotRef.current;
      if (!(slotElement instanceof HTMLElement)) {
        setMountError("Initial mount failed: Native workbench host slot is unavailable.");
        return;
      }

      applyNativeWorkbenchPersistentHostVisualState({
        scale: NATIVE_WORKBENCH_CLOSED_SCALE,
        opacity: 0,
        interactive: false,
        durationMs: 0
      });

      try {
        attachNativeWorkbenchPersistentHost(slotElement, {
          deferShow: true,
          track: false,
          interactive: false,
          durationMs: 0
        });
      } catch (error) {
        setMountError("Initial mount failed: " + formatProbeError(error));
        console.error("RapidTransit native workbench initial attach failed", error);
        return;
      }

      const openToken = nativeWorkbenchOpenToken + 1;
      nativeWorkbenchOpenToken = openToken;
      reportNativeWorkbenchHostState("opening");
      ensureNativeWorkbenchPersistentBundle({
        forceReload: false,
        forceRemount: false
      }).then(() => {
        traceNativeWorkbench("panel.bundle.ready");
        if (disposedRef.current || closingRef.current || nativeWorkbenchOpenToken !== openToken) {
          return;
        }

        syncParkingVisualState(false, false, 0);
        openRafRef.current = window.requestAnimationFrame(() => {
          openRafRef.current = 0;
          if (disposedRef.current || closingRef.current) {
            return;
          }

          setShellOpen(true);
          setShellInteractive(true);
          settleTimerRef.current = window.setTimeout(() => {
            settleTimerRef.current = 0;
            if (!disposedRef.current && !closingRef.current) {
              finalizeWorkbenchOpen();
            }
          }, NATIVE_WORKBENCH_OPEN_SETTLE_DELAY_MS);
        });
      }).catch((error) => {
        if (!disposedRef.current) {
          traceNativeWorkbench("panel.bundle.error", { message: error && error.message ? error.message : error });
          setMountError("Initial mount failed: " + formatProbeError(error));
          console.error("RapidTransit native workbench initial mount failed", error);
        }
      });
    };

    if (nativeWorkbenchPersistentBundleHandle || nativeWorkbenchPersistentBundlePromise) {
      launchMount();
    } else {
      mountTimerRef.current = window.setTimeout(launchMount, 120);
    }

    return () => {
      traceNativeWorkbench("panel.effect.unmount");
      window.removeEventListener("keydown", handleKeyDown);
      disposedRef.current = true;
      clearWorkbenchShellTimers();
      stopNativeWorkbenchSlotTracking();
      closingRef.current = false;
      setNativeWorkbenchCloseHandler(null);
      parkNativeWorkbenchPersistentHost();
    };
  }, []);

  return React.createElement(
    "div",
    {
      style: {
        display: "flex",
        alignItems: "flex-end",
        justifyContent: "center",
        width: "100%",
        height: "100%",
        minHeight: "calc(100vh - 42rem)",
        paddingTop: "6rem",
        paddingRight: "68rem",
        paddingBottom: "102rem",
        paddingLeft: "68rem",
        boxSizing: "border-box",
        pointerEvents: "none"
      }
    },
    React.createElement(
      "div",
      {
        ref: shellFrameRef,
        style: {
          width: "100%",
          maxWidth: "1140rem",
          minWidth: "1035rem",
          height: "100%",
          maxHeight: "846rem",
          minHeight: "0",
          display: "flex",
          flexDirection: "column",
          opacity: shellOpen ? "1" : "0",
          transform: shellOpen ? "scale(1)" : "scale(0.95)",
          transformOrigin: "center center",
          transitionProperty: "opacity, transform",
          transitionDuration: shellOpen
            ? NATIVE_WORKBENCH_OPEN_DURATION_MS.toString() + "ms"
            : NATIVE_WORKBENCH_CLOSE_DURATION_MS.toString() + "ms",
          transitionTimingFunction: "ease",
          pointerEvents: shellInteractive ? "auto" : "none"
        }
      },
      React.createElement(
        "div",
        {
          style: {
            width: "100%",
            height: "100%",
            maxWidth: "100%",
            minWidth: "0",
            maxHeight: "100%",
            minHeight: "0",
            display: "flex",
            flexDirection: "column",
            backgroundColor: "transparent"
          }
        },
        React.createElement(
          "div",
          {
            className: "child-opacity-transition_nkS",
            style: {
              display: "flex",
              flexDirection: "column",
              padding: "0",
              width: "100%",
              height: "100%",
              minHeight: "0",
              "--childOpacityTransitionDuration": shellOpen
                ? NATIVE_WORKBENCH_OPEN_DURATION_MS.toString() + "ms"
                : NATIVE_WORKBENCH_CLOSE_DURATION_MS.toString() + "ms",
              "--childOpacityTransitionDelay": "0ms",
              "--childOpacityTransitionTimingFunction": "ease"
            }
          },
          React.createElement(RapidTransitWorkbenchBundleHost, {
            hostSlotRef,
            mountError
          })
        )
      )
    )
  );
};

export function registerRapidTransitWorkbenchPanelType(input) {
  input.RAPID_TRANSIT_WORKBENCH = NATIVE_WORKBENCH_PANEL_ID;
  return input;
}

export function registerRapidTransitWorkbenchPanel(input) {
  input[NATIVE_WORKBENCH_PANEL_ID] = RapidTransitWorkbenchGamePanel;
  return input;
}
