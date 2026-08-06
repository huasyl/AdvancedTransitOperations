import { useLayoutEffect, useMemo, useState } from "react";
import PlannerPage from "./pages/planner/PlannerPage";
import BroadcastPage from "./pages/broadcast/BroadcastPage";
import { useNativeScheduleI18n } from "./shared/workbench-i18n";
import OverviewPage from "./pages/overview/OverviewPage";
import PassengerFlowPage from "./pages/passenger/PassengerFlowPage";
import SchedulePage from "./pages/schedule/SchedulePage";
import { setWorkbenchApiTransportMode } from "../lib/workbench-api";
import { traceWorkbench } from "./shared/workbench-trace";

const DEFAULT_NATIVE_WORKBENCH_PAGE = "overview";
const WORKBENCH_PAGE_TRANSITION_MS = 220;
const DEFAULT_TRANSPORT_MODE = "train";
const WORKBENCH_DEBUG_FLAGS_EVENT = "rt-native-workbench-debug-flags";

function getWorkbenchDebugToolsEnabled() {
  return typeof window !== "undefined" && window.__RT_DEBUG_TOOLS__ === true;
}

function requestWorkbenchClose() {
  if (typeof window === "undefined") {
    return;
  }

  traceWorkbench("app.close.click");
  const closeHandler = window.__RT_NATIVE_WORKBENCH_CLOSE__;
  if (typeof closeHandler === "function") {
    closeHandler();
  }
}

export default function WorkbenchApp({ registerHostActions }) {
  const { locale, script, t } = useNativeScheduleI18n();
  const [activePage, setActivePage] = useState(DEFAULT_NATIVE_WORKBENCH_PAGE);
  const [renderedPage, setRenderedPage] = useState(DEFAULT_NATIVE_WORKBENCH_PAGE);
  const [previousPage, setPreviousPage] = useState("");
  const [activeTransportMode, setActiveTransportMode] = useState(DEFAULT_TRANSPORT_MODE);
  const [pageTransportModes, setPageTransportModes] = useState({
    schedule: DEFAULT_TRANSPORT_MODE,
    planner: DEFAULT_TRANSPORT_MODE,
    broadcast: DEFAULT_TRANSPORT_MODE,
    overview: DEFAULT_TRANSPORT_MODE,
    passenger: DEFAULT_TRANSPORT_MODE
  });
  const [pageStage, setPageStage] = useState("entered");
  const [plannerEnterSequence, setPlannerEnterSequence] = useState(0);
  const [broadcastEnterSequence, setBroadcastEnterSequence] = useState(0);
  const [debugToolsEnabled, setDebugToolsEnabled] = useState(getWorkbenchDebugToolsEnabled);
  const [stickyPages, setStickyPages] = useState({
    overview: false,
    passenger: false
  });
  const pageTabs = useMemo(
    () => {
      const tabs = [
        { key: "overview", label: t("nativeWorkbench.tab.overview") },
        { key: "schedule", label: t("nativeWorkbench.tab.schedule") }
      ];
      if (debugToolsEnabled) {
        tabs.push({ key: "planner", label: t("nativeWorkbench.tab.planner") });
      }
      tabs.push(
        { key: "broadcast", label: t("nativeWorkbench.tab.broadcast") },
        { key: "passenger", label: t("nativeWorkbench.tab.passenger") }
      );
      return tabs;
    },
    [debugToolsEnabled, t]
  );

  useLayoutEffect(() => {
    if (typeof window === "undefined") {
      return undefined;
    }

    const syncDebugToolsEnabled = () => {
      setDebugToolsEnabled(getWorkbenchDebugToolsEnabled());
    };

    syncDebugToolsEnabled();
    window.addEventListener(WORKBENCH_DEBUG_FLAGS_EVENT, syncDebugToolsEnabled);
    return () => {
      window.removeEventListener(WORKBENCH_DEBUG_FLAGS_EVENT, syncDebugToolsEnabled);
    };
  }, []);

  useLayoutEffect(() => {
    if (debugToolsEnabled || activePage !== "planner") {
      return;
    }

    traceWorkbench("app.planner.hidden.redirect", { from: activePage });
    setActivePage(DEFAULT_NATIVE_WORKBENCH_PAGE);
  }, [activePage, debugToolsEnabled]);

  useLayoutEffect(() => {
    traceWorkbench("app.page.state", { activePage, renderedPage, pageStage });
  }, [activePage, pageStage, renderedPage]);

  useLayoutEffect(() => {
    setWorkbenchApiTransportMode(activeTransportMode);
    traceWorkbench("app.transportMode.state", { mode: activeTransportMode });
  }, [activeTransportMode]);

  useLayoutEffect(() => {
    setPageTransportModes((current) => (
      current[renderedPage] === activeTransportMode
        ? current
        : {
            ...current,
            [renderedPage]: activeTransportMode
          }
    ));
  }, [activeTransportMode, renderedPage]);

  useLayoutEffect(() => {
    if (activePage === renderedPage) {
      return undefined;
    }

    traceWorkbench("app.page.transition.begin", { activePage, renderedPage });
    traceWorkbench("app.page.transition.swap", { next: activePage, from: renderedPage });
    setPreviousPage(renderedPage === "broadcast" ? "" : renderedPage);
    setRenderedPage(activePage);
    if (activePage === "planner") {
      setPlannerEnterSequence((current) => current + 1);
    }
    if (activePage === "broadcast") {
      setBroadcastEnterSequence((current) => current + 1);
    }
    setPageStage("armed");
    return undefined;
  }, [activePage, renderedPage]);

  useLayoutEffect(() => {
    if (pageStage !== "armed") {
      return undefined;
    }

    let innerRaf = 0;
    const outerRaf = window.requestAnimationFrame(() => {
      innerRaf = window.requestAnimationFrame(() => {
        setPageStage("running");
      });
    });
    return () => {
      window.cancelAnimationFrame(outerRaf);
      if (innerRaf) {
        window.cancelAnimationFrame(innerRaf);
      }
    };
  }, [pageStage, renderedPage]);

  useLayoutEffect(() => {
    if (pageStage !== "running") {
      return undefined;
    }

    const timer = window.setTimeout(() => {
      traceWorkbench("app.page.transition.entered", { page: renderedPage });
      setPreviousPage("");
      setPageStage("entered");
    }, WORKBENCH_PAGE_TRANSITION_MS);
    return () => window.clearTimeout(timer);
  }, [pageStage, renderedPage]);

  useLayoutEffect(() => {
    if (renderedPage !== "overview" && renderedPage !== "passenger") {
      return;
    }

    setStickyPages((current) => (
      current[renderedPage]
        ? current
        : {
            ...current,
            [renderedPage]: true
          }
    ));
  }, [renderedPage]);

  const shouldMountOverview = stickyPages.overview || renderedPage === "overview";
  const shouldMountPassenger = stickyPages.passenger || renderedPage === "passenger";
  const modeForPage = (pageKey) => (
    activePage === pageKey || renderedPage === pageKey
      ? activeTransportMode
      : pageTransportModes[pageKey] || DEFAULT_TRANSPORT_MODE
  );

  function pageClassName(pageKey) {
    if (renderedPage === pageKey) {
      const stageClass = pageStage === "armed" ? "is-entering" : "is-entered";
      return `dw-native-workbench-page is-active ${stageClass}`;
    }

    if (previousPage === pageKey) {
      const stageClass = pageStage === "armed" ? "is-entered" : "is-exiting";
      return `dw-native-workbench-page is-leaving ${stageClass}`;
    }

    return "dw-native-workbench-page is-inactive is-entered";
  }

  function handleTabClick(tabKey) {
    if (tabKey === "planner" && !debugToolsEnabled) {
      return;
    }

    traceWorkbench("app.tab.click", { tab: tabKey, from: activePage });
    setActivePage(tabKey);
  }

  return (
    <div
      className="dw-native-schedule-root dw-native-workbench-root"
      data-native-schedule-locale={locale}
      data-native-schedule-script={script}
      data-native-workbench-page={activePage}
      lang={locale}
    >
      <div className="dw-native-workbench-base" />

      <div className="dw-native-workbench-tabs-shell">
        <div className="dw-native-workbench-tabs-row">
          <div className="dw-native-workbench-tabs">
            {pageTabs.map((tab) => (
              <button
                key={tab.key}
                type="button"
                className={`dw-native-workbench-tab ${activePage === tab.key ? "is-active" : ""}`}
                onClick={() => handleTabClick(tab.key)}
              >
                {tab.label}
              </button>
            ))}
          </div>
          <button type="button" className="dw-native-workbench-close" onClick={requestWorkbenchClose}>
            ×
          </button>
        </div>
      </div>

      <div className="dw-native-workbench-pages">
        <div
          className={pageClassName("schedule")}
          data-workbench-page="schedule"
        >
          <SchedulePage
            registerHostActions={registerHostActions}
            activeTransportMode={modeForPage("schedule")}
            isActive={renderedPage === "schedule"}
          />
        </div>
        <div
          className={pageClassName("planner")}
          data-workbench-page="planner"
        >
          {debugToolsEnabled ? (
            <PlannerPage pageEnterSequence={plannerEnterSequence} activeTransportMode={modeForPage("planner")} />
          ) : null}
        </div>
        <div
          className={pageClassName("broadcast")}
          data-workbench-page="broadcast"
        >
          <BroadcastPage pageEnterSequence={broadcastEnterSequence} activeTransportMode={modeForPage("broadcast")} />
        </div>
        <div
          className={pageClassName("overview")}
          data-workbench-page="overview"
        >
          {shouldMountOverview ? (
            <OverviewPage
              activeTransportMode={modeForPage("overview")}
              isActive={renderedPage === "overview"}
              registerHostActions={registerHostActions}
              onTransportModeChange={setActiveTransportMode}
            />
          ) : null}
        </div>
        <div
          className={pageClassName("passenger")}
          data-workbench-page="passenger"
        >
          {shouldMountPassenger ? (
            <PassengerFlowPage
              activeTransportMode={modeForPage("passenger")}
              isActive={renderedPage === "passenger"}
              registerHostActions={registerHostActions}
            />
          ) : null}
        </div>
      </div>
    </div>
  );
}
