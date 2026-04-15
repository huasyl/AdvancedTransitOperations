import React from "react";
import ReactDOM from "react-dom/client";
import DispatchWorkbenchNativeScheduleApp from "./DispatchWorkbenchNativeScheduleApp.jsx";
import "./styles/native-schedule-demo.css";

const GLOBAL_MOUNT_KEY = "RTDispatchWorkbenchNativeSchedule";
const mountedEntries = new WeakMap();

function NativeScheduleErrorFallback({ error }) {
  return (
    <div className="dw-native-schedule-root">
      <div className="dw-native-schedule-error">
        <div className="dw-native-schedule-error-title">RT Dispatch Workbench</div>
        <div className="dw-native-schedule-error-text">
          Native schedule mount failed.
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

function renderNativeSchedule(container) {
  if (!(container instanceof HTMLElement)) {
    throw new Error("Native schedule mount target is unavailable.");
  }

  let entry = mountedEntries.get(container);
  if (!entry) {
    entry = {
      root: ReactDOM.createRoot(container),
      hostActions: null
    };
    mountedEntries.set(container, entry);
  }

  entry.root.render(
    <NativeScheduleErrorBoundary>
      <DispatchWorkbenchNativeScheduleApp
        registerHostActions={(actions) => {
          entry.hostActions = actions || null;
        }}
      />
    </NativeScheduleErrorBoundary>
  );

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
  window[GLOBAL_MOUNT_KEY] = {
    mount: renderNativeSchedule,
    unmount: unmountNativeSchedule
  };
}

const rootElement = document.getElementById("root");
if (rootElement) {
  renderNativeSchedule(rootElement);
}
