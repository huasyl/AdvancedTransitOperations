// @ts-nocheck
import { trigger } from "cs2/api";
import { ActiveFocusDiv, InputActionConsumer } from "cs2/input";
import React from "react";
import { COLORS } from "../selection/selectionStyles";

declare const __RT_UI_MODULE_DEV__: boolean;

export const NATIVE_WORKBENCH_PANEL_ID = "RapidTransitMod.DispatchWorkbenchNativePanel";
const NATIVE_WORKBENCH_UI_BUILD = "0.1.0-native-r4e";
const NATIVE_WORKBENCH_SCHEDULE_GLOBAL = "RTDispatchWorkbenchNativeSchedule";
const NATIVE_WORKBENCH_PAGE_GLOBAL = "__RT_NATIVE_WORKBENCH_PAGE__";
const NATIVE_WORKBENCH_PAGE_EVENT = "rt-native-workbench-pagechange";
const NATIVE_WORKBENCH_DEBUG_FLAGS_EVENT = "rt-native-workbench-debug-flags";
const NATIVE_WORKBENCH_OPEN_DURATION_MS = 250;
const NATIVE_WORKBENCH_CLOSE_DURATION_MS = 150;
const NATIVE_WORKBENCH_NAVIGATE_EVENT = "rt-native-workbench-navigate";

let nativeWorkbenchTraceSeq = 0;
let nativeWorkbenchTraceLifecycleRegistered = false;
let nativeWorkbenchScheduleLoader = null;
let nativeWorkbenchSchedulePrewarmStarted = false;
let nativeWorkbenchPersistentHostElement = null;
let nativeWorkbenchPersistentBundleHandle = null;
let nativeWorkbenchPersistentBundlePromise = null;
let nativeWorkbenchParkingLotElement = null;
let nativeWorkbenchShellFrameElement = null;
let nativeWorkbenchShellFrameListener = null;
let nativeWorkbenchContentShown = false;

function setNativeWorkbenchShellFrame(element) {
  nativeWorkbenchShellFrameElement = element;
  if (nativeWorkbenchShellFrameListener) {
    nativeWorkbenchShellFrameListener(element);
  }
}

export function requestNativeWorkbenchNavigation() {
  if (nativeWorkbenchContentShown) {
    window.dispatchEvent(new CustomEvent(NATIVE_WORKBENCH_NAVIGATE_EVENT));
  }
}

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
  window.__RT_NATIVE_WORKBENCH_CLOSE__ = () => trigger("game", "closePanel", NATIVE_WORKBENCH_PANEL_ID);
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
  parkingLot.style.visibility = "hidden";
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
      "}",
      ".rt-native-schedule-mount > .dw-native-schedule-root > * {",
      "  opacity: 0;",
      "  transition-property: opacity;",
      "  transition-timing-function: ease;",
      "  transition-duration: " + NATIVE_WORKBENCH_CLOSE_DURATION_MS + "ms;",
      "}",
      ".rt-native-schedule-surface[data-rt-shown=\"true\"] > .rt-native-schedule-mount > .dw-native-schedule-root > * {",
      "  opacity: 1;",
      "  transition-duration: " + NATIVE_WORKBENCH_OPEN_DURATION_MS + "ms;",
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

  async function runBundleAction(action) {
    if (busyAction) {
      return;
    }

    traceNativeWorkbench("bundle.action.begin", { action });
    const nextBusyLabel = action === "reload"
      ? "Reload Bundle"
      : action === "remount"
        ? "Remount"
        : "Refresh Data";
    setBusyAction(nextBusyLabel);

    try {
      if (action === "refresh") {
        const bundleState = await ensureNativeWorkbenchPersistentBundle({
          forceReload: false,
          forceRemount: false
        });
        await bundleState.handle.refreshData();
        setStatus({
          tone: "success",
          text: "Refresh Data completed."
        });
        traceNativeWorkbench("bundle.action.done", { action });
        return;
      }

      const isReload = action === "reload";
      const isRemount = action === "remount";
      await ensureNativeWorkbenchPersistentBundle({
        forceReload: isReload,
        forceRemount: isRemount
      });
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
  const frameRef = React.useRef(null);
  const actions = React.useMemo(() => ({ Close: props.onClose }), [props.onClose]);

  React.useLayoutEffect(() => {
    setNativeWorkbenchPage("schedule");
    setNativeWorkbenchShellFrame(frameRef.current);
    return () => {
      setNativeWorkbenchShellFrame(null);
    };
  }, []);

  return React.createElement(
    InputActionConsumer,
    { actions },
    React.createElement(
      ActiveFocusDiv,
      {
        style: {
          position: "absolute",
          top: "10rem",
          right: "68rem",
          bottom: "6rem",
          left: "68rem",
          display: "flex",
          alignItems: "flex-end",
          justifyContent: "center",
          pointerEvents: "none"
        }
      },
      React.createElement("div", {
        ref: frameRef,
        style: {
          width: "100%",
          maxWidth: "1206rem",
          height: "100%",
          maxHeight: "900rem"
        }
      })
    )
  );
};

export const RapidTransitWorkbenchContentHost = () => {
  const frameState = React.useState(null);
  const frame = frameState[0];
  const setFrame = frameState[1];
  const phaseState = React.useState("hidden");
  const phase = phaseState[0];
  const setPhase = phaseState[1];
  const rectState = React.useState(null);
  const rect = rectState[0];
  const setRect = rectState[1];
  const mountErrorState = React.useState("");
  const mountError = mountErrorState[0];
  const setMountError = mountErrorState[1];
  const hostSlotRef = React.useRef(null);
  const exitTimerRef = React.useRef(0);

  React.useEffect(() => {
    if (!frame) {
      return undefined;
    }

    const hostElement = ensureNativeWorkbenchPersistentHostElement();
    let entered = false;
    let outerRaf = 0;
    let innerRaf = 0;
    const measure = () => {
      const next = frame.getBoundingClientRect();
      if (!(next.width > 0 && next.height > 0)) {
        return;
      }

      setRect((current) => (
        current
          && Math.abs(current.left - next.left) < 0.5
          && Math.abs(current.top - next.top) < 0.5
          && Math.abs(current.width - next.width) < 0.5
          && Math.abs(current.height - next.height) < 0.5
          ? current
          : { left: next.left, top: next.top, width: next.width, height: next.height }
      ));
      if (entered) {
        return;
      }

      entered = true;
      setPhase((current) => (current === "exiting" ? "shown" : "entering"));
      outerRaf = window.requestAnimationFrame(() => {
        innerRaf = window.requestAnimationFrame(() => setPhase("shown"));
      });
    };

    window.clearTimeout(exitTimerRef.current);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(frame);
    window.addEventListener("resize", measure);
    reportNativeWorkbenchHostState("visible");

    return () => {
      window.cancelAnimationFrame(outerRaf);
      window.cancelAnimationFrame(innerRaf);
      observer.disconnect();
      window.removeEventListener("resize", measure);
      const focused = hostElement.shadowRoot.activeElement;
      if (focused) {
        focused.blur();
      }
      reportNativeWorkbenchHostState("parked");
      setPhase("exiting");
      exitTimerRef.current = window.setTimeout(() => setPhase("hidden"), NATIVE_WORKBENCH_CLOSE_DURATION_MS);
    };
  }, [frame]);

  React.useEffect(() => {
    nativeWorkbenchShellFrameListener = setFrame;
    setFrame(nativeWorkbenchShellFrameElement);
    const hostElement = ensureNativeWorkbenchPersistentHostElement();
    hostSlotRef.current.appendChild(hostElement);
    ensureNativeWorkbenchPersistentBundle({
      forceReload: false,
      forceRemount: false
    }).catch((error) => {
      setMountError("Initial mount failed: " + formatProbeError(error));
      console.error("RapidTransit native workbench initial mount failed", error);
    });

    return () => {
      nativeWorkbenchShellFrameListener = null;
      window.clearTimeout(exitTimerRef.current);
      ensureNativeWorkbenchParkingLot().appendChild(hostElement);
    };
  }, []);

  React.useEffect(() => {
    nativeWorkbenchContentShown = phase === "shown";
    const shownRaf = window.requestAnimationFrame(() => {
      ensureNativeWorkbenchPersistentHostElement().shadowRoot
        .querySelector(".rt-native-schedule-surface")
        .setAttribute("data-rt-shown", phase === "shown" ? "true" : "false");
    });
    if (phase === "shown") {
      window.dispatchEvent(new CustomEvent(NATIVE_WORKBENCH_NAVIGATE_EVENT));
    }
    return () => {
      window.cancelAnimationFrame(shownRaf);
      nativeWorkbenchContentShown = false;
    };
  }, [phase]);

  return React.createElement(
    "div",
    {
      style: {
        position: "fixed",
        left: phase === "hidden" ? "-99999px" : rect.left.toString() + "px",
        top: rect ? rect.top.toString() + "px" : "0px",
        width: rect ? rect.width.toString() + "px" : "1520px",
        height: rect ? rect.height.toString() + "px" : "980px",
        display: "flex",
        flexDirection: "column",
        visibility: phase === "hidden" ? "hidden" : "visible",
        transform: phase === "shown" ? "scale(1)" : "scale(0.95)",
        transformOrigin: "center center",
        transitionProperty: "transform",
        transitionTimingFunction: "ease",
        transitionDuration: phase === "entering" || phase === "shown"
          ? NATIVE_WORKBENCH_OPEN_DURATION_MS.toString() + "ms"
          : NATIVE_WORKBENCH_CLOSE_DURATION_MS.toString() + "ms",
        pointerEvents: phase === "shown" ? "auto" : "none"
      }
    },
    React.createElement(RapidTransitWorkbenchBundleHost, {
      hostSlotRef,
      mountError
    })
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
