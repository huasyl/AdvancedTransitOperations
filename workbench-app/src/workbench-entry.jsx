import ReactDOM from "react-dom/client";
import WorkbenchApp from "./workbench/WorkbenchApp.jsx";
import WorkbenchEntryError, { WorkbenchEntryErrorBoundary } from "./workbench/WorkbenchEntryError.jsx";
import { NativeScheduleI18nProvider } from "./workbench-i18n";
import "./styles/workbench.css";
import "./styles/native-broadcast-page.css";
import "./styles/native-planner-page.css";

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
