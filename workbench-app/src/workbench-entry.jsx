import ReactDOM from "react-dom/client";
import WorkbenchApp from "./workbench/WorkbenchApp.jsx";
import WorkbenchEntryError, { WorkbenchEntryErrorBoundary } from "./workbench/WorkbenchEntryError.jsx";
import { NativeScheduleI18nProvider } from "./workbench-i18n";
import "./styles/workbench.css";
import "./styles/native-broadcast-page.css";
import "./styles/native-planner-page.css";
import { traceWorkbench } from "./workbench/shared/workbench-trace";

const GLOBAL_MOUNT_KEY = "RTDispatchWorkbenchNativeSchedule";
const mountedEntries = new WeakMap();

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
  entry.root.render(<WorkbenchEntryError error={error} />);
  return {
    refreshData: async () => undefined,
    unmount: () => unmountNativeSchedule(container)
  };
}

function renderNativeSchedule(container) {
  if (!(container instanceof HTMLElement)) {
    throw new Error("Native schedule mount target is unavailable.");
  }

  traceWorkbench("entry.mount.begin");
  const entry = ensureEntry(container);

  try {
    entry.root.render(
      <NativeScheduleI18nProvider>
        <WorkbenchEntryErrorBoundary>
          <WorkbenchApp
            registerHostActions={(actions) => {
              entry.hostActions = actions || null;
            }}
          />
        </WorkbenchEntryErrorBoundary>
      </NativeScheduleI18nProvider>
    );
  } catch (error) {
    traceWorkbench("entry.mount.error", { message: error?.message || error });
    console.error("[RT Native Schedule] startup render failed", error);
    return renderNativeScheduleStartupError(container, error);
  }

  traceWorkbench("entry.mount.done");

  return {
    refreshData: async () => entry.hostActions?.refreshData?.(),
    unmount: () => unmountNativeSchedule(container)
  };
}

function unmountNativeSchedule(container) {
  const entry = mountedEntries.get(container);
  if (!entry) {
    traceWorkbench("entry.unmount.skip");
    return;
  }

  traceWorkbench("entry.unmount.begin");
  entry.hostActions = null;
  entry.root.unmount();
  mountedEntries.delete(container);
  traceWorkbench("entry.unmount.done");
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
