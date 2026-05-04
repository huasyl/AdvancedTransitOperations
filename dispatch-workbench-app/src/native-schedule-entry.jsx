import React from "react";
import ReactDOM from "react-dom/client";
import DispatchWorkbenchNativeScheduleApp from "./DispatchWorkbenchNativeScheduleApp.jsx";
import { NativeScheduleI18nProvider } from "./native-schedule-i18n";
import "./styles/native-schedule-demo.css";
import "./styles/native-broadcast-page.css";
import "./styles/native-planner-page.css";

const GLOBAL_MOUNT_KEY = "RTDispatchWorkbenchNativeSchedule";
const mountedEntries = new WeakMap();

function NativeScheduleErrorFallback({ error, title = "RT Dispatch Workbench", body = "Native schedule mount failed." }) {

  return (
    <div className="dw-native-schedule-root">
      <div className="dw-native-schedule-error">
        <div className="dw-native-schedule-error-title">{title}</div>
        <div className="dw-native-schedule-error-text">
          {body}
        </div>
        <div className="dw-native-schedule-error-detail">
          {error?.message || "Unknown error"}
        </div>
      </div>
    </div>
  );
}

class NativeScheduleErrorBoundary extends React.Component {
  constructor(props) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error) {
    return { error };
  }

  componentDidCatch(error, errorInfo) {
    console.error("[RT Native Schedule] mount failed", error, errorInfo?.componentStack || "");
  }

  render() {
    if (this.state.error) {
      return <NativeScheduleErrorFallback error={this.state.error} />;
    }

    return this.props.children;
  }
}

function ensureEntry(container) {
  let entry = mountedEntries.get(container);
  if (!entry) {
    entry = {
      root: ReactDOM.createRoot(container),
      hostActions: null
    };
    mountedEntries.set(container, entry);
  }

  return entry;
}

function renderNativeScheduleStartupError(container, error) {
  const entry = ensureEntry(container);
  entry.hostActions = null;
  entry.root.render(<NativeScheduleErrorFallback error={error} />);
  return {
    refreshData: async () => undefined,
    unmount: () => unmountNativeSchedule(container)
  };
}

function renderNativeSchedule(container) {
  if (!(container instanceof HTMLElement)) {
    throw new Error("Native schedule mount target is unavailable.");
  }

  const entry = ensureEntry(container);

  try {
    entry.root.render(
      <NativeScheduleI18nProvider>
        <NativeScheduleErrorBoundary>
          <DispatchWorkbenchNativeScheduleApp
            registerHostActions={(actions) => {
              entry.hostActions = actions || null;
            }}
          />
        </NativeScheduleErrorBoundary>
      </NativeScheduleI18nProvider>
    );
  } catch (error) {
    console.error("[RT Native Schedule] startup render failed", error);
    return renderNativeScheduleStartupError(container, error);
  }

  return {
    refreshData: async () => entry.hostActions?.refreshData?.(),
    unmount: () => unmountNativeSchedule(container)
  };
}

function unmountNativeSchedule(container) {
  const entry = mountedEntries.get(container);
  if (!entry) {
    return;
  }

  entry.hostActions = null;
  entry.root.unmount();
  mountedEntries.delete(container);
}

if (typeof window !== "undefined") {
  const mountApi = {
    mount: renderNativeSchedule,
    unmount: unmountNativeSchedule
  };
  window[GLOBAL_MOUNT_KEY] = mountApi;
}

const rootElement = document.getElementById("root");
if (rootElement) {
  try {
    renderNativeSchedule(rootElement);
  } catch (error) {
    console.error("[RT Native Schedule] bootstrap render failed", error);
    renderNativeScheduleStartupError(rootElement, error);
  }
}
