import React from "react";
import ReactDOM from "react-dom";
import DispatchWorkbenchEuisApp from "./DispatchWorkbenchEuisApp.jsx";
import { SafeControlText } from "./components/ChoiceButtons";
import { WorkbenchI18nProvider } from "./lib/i18n";
import "./styles/dispatch-workbench.css";

// Official EUIS mount entry.
// If the workbench disappears from the host, check this file before touching page components.
// Host container geometry and hit-testing belong here, not in normal component CSS.

const DEFAULT_APP_NAME = "dispatch-workbench";
const ROOT_ATTRIBUTE = "data-dw-euis-root";
const HOST_STYLE_KEYS = [
  "position",
  "pointerEvents",
  "overflow",
  "backgroundColor",
  "top",
  "left",
  "right",
  "bottom",
];
const HOST_STYLE_PROP_KEYS = ["backdrop-filter", "-webkit-backdrop-filter"];

class EuisErrorBoundary extends React.Component {
  constructor(props) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error) {
    return { error };
  }

  componentDidCatch(error) {
    console.error("[RT Workbench] mount failed", error);
  }

  render() {
    if (this.state.error) {
      return (
        <div className="dw-euis-shell">
          <div className="dw-euis-frame">
            <header className="dw-euis-header">
              <div className="dw-title-block">
                <h1>
                  <SafeControlText>RT Dispatch Workbench</SafeControlText>
                </h1>
                <p>
                  <SafeControlText>Frontend mount failed. Check UI.log for the latest error.</SafeControlText>
                </p>
              </div>
            </header>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

function resolveHostTarget(props) {
  if (props?.domElement instanceof HTMLElement) {
    return props.domElement;
  }

  const appName = props?.name || DEFAULT_APP_NAME;
  const containerId = `single-spa-application:${appName}`;
  const existing = document.getElementById(containerId);
  if (existing) {
    return existing;
  }

  const container = document.createElement("div");
  container.id = containerId;
  document.body.appendChild(container);
  return container;
}

function prepareHostTarget(hostTarget) {
  hostTarget.classList.add("dw-euis-host-target");

  // EUIS rewrites app CSS under data-safe-name, so selectors in our stylesheet
  // do not reliably reach the single-spa host container itself. Host container
  // geometry and hit-testing must therefore be controlled inline here.
  HOST_STYLE_KEYS.forEach((key) => {
    const snapshotKey = `dwPrev${key.charAt(0).toUpperCase()}${key.slice(1)}`;
    if (!Object.prototype.hasOwnProperty.call(hostTarget.dataset, snapshotKey)) {
      hostTarget.dataset[snapshotKey] = hostTarget.style[key] || "";
    }
  });
  HOST_STYLE_PROP_KEYS.forEach((key) => {
    const snapshotKey = `dwPrevProp${key.replace(/[^a-z0-9]+/gi, "")}`;
    if (!Object.prototype.hasOwnProperty.call(hostTarget.dataset, snapshotKey)) {
      hostTarget.dataset[snapshotKey] = hostTarget.style.getPropertyValue(key) || "";
    }
  });

  // Force absolute positioning to prevent covering bottom EUIS UI
  hostTarget.style.position = "absolute";
  hostTarget.style.top = "0";
  hostTarget.style.left = "0";
  hostTarget.style.right = "0";
  hostTarget.style.bottom = "56px";
  hostTarget.style.pointerEvents = "none";
  hostTarget.style.overflow = "visible";
  hostTarget.style.backgroundColor = "rgba(12, 26, 37, 0.28)";
  hostTarget.style.setProperty("backdrop-filter", "var(--menuBlur, blur(10px))");
  hostTarget.style.setProperty("-webkit-backdrop-filter", "var(--menuBlur, blur(10px))");
  document.documentElement.classList.add("dw-hosted-surface");
  document.body.classList.add("dw-hosted-surface");
  const rootEl = document.getElementById("root");
  if (rootEl) {
    rootEl.classList.add("dw-hosted-surface");
  }
}

function ensureMountRoot(hostTarget) {
  const existingRoot = hostTarget.querySelector(`[${ROOT_ATTRIBUTE}="true"]`);
  if (existingRoot instanceof HTMLElement) {
    return existingRoot;
  }

  const root = document.createElement("div");
  root.setAttribute(ROOT_ATTRIBUTE, "true");
  root.className = "dw-euis-root";
  hostTarget.appendChild(root);
  return root;
}

function restoreHostTarget(hostTarget) {
  HOST_STYLE_KEYS.forEach((key) => {
    const snapshotKey = `dwPrev${key.charAt(0).toUpperCase()}${key.slice(1)}`;
    if (Object.prototype.hasOwnProperty.call(hostTarget.dataset, snapshotKey)) {
      hostTarget.style[key] = hostTarget.dataset[snapshotKey];
      delete hostTarget.dataset[snapshotKey];
    }
  });
  HOST_STYLE_PROP_KEYS.forEach((key) => {
    const snapshotKey = `dwPrevProp${key.replace(/[^a-z0-9]+/gi, "")}`;
    if (Object.prototype.hasOwnProperty.call(hostTarget.dataset, snapshotKey)) {
      hostTarget.style.setProperty(key, hostTarget.dataset[snapshotKey]);
      delete hostTarget.dataset[snapshotKey];
    }
  });
}

export function bootstrap() {
  return Promise.resolve();
}

export function mount(props) {
  const hostTarget = resolveHostTarget(props);
  if (!hostTarget) {
    return Promise.reject(new Error("EUIS host mount target is unavailable."));
  }

  prepareHostTarget(hostTarget);
  const mountRoot = ensureMountRoot(hostTarget);

  return new Promise((resolve) => {
    ReactDOM.render(
      <WorkbenchI18nProvider>
        <EuisErrorBoundary>
          <DispatchWorkbenchEuisApp {...props} />
        </EuisErrorBoundary>
      </WorkbenchI18nProvider>,
      mountRoot,
      resolve
    );
  });
}

export function unmount(props) {
  const hostTarget = resolveHostTarget(props);
  if (!hostTarget) {
    return Promise.resolve();
  }

  const mountRoot = ensureMountRoot(hostTarget);
  return new Promise((resolve) => {
    ReactDOM.unmountComponentAtNode(mountRoot);
    restoreHostTarget(hostTarget);
    resolve();
  });
}
