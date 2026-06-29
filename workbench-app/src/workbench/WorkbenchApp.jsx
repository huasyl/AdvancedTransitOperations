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
    if (activePage === "broadcast") {
      setRenderedPage(activePage);
      setBroadcastEnterSequence((current) => current + 1);
      setPageStage("entered");
      traceWorkbench("app.page.transition.direct", { activePage });
      return undefined;
    }

    setPageStage("exiting");
    const timer = window.setTimeout(() => {
      traceWorkbench("app.page.transition.swap", { next: activePage, from: renderedPage });
      setRenderedPage(activePage);
      if (activePage === "planner") {
        setPlannerEnterSequence((current) => current + 1);
      }
      if (activePage === "broadcast") {
        setBroadcastEnterSequence((current) => current + 1);
      }
      setPageStage("entering");
      const raf = window.requestAnimationFrame(() => {
        traceWorkbench("app.page.transition.entered", { page: activePage });
        setPageStage("entered");
      });
      return () => window.cancelAnimationFrame(raf);
    }, WORKBENCH_PAGE_TRANSITION_MS);

    return () => window.clearTimeout(timer);
  }, [activePage, renderedPage]);

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
          className={`dw-native-workbench-page ${renderedPage === "schedule" ? "is-active" : "is-inactive"} is-${pageStage}`}
          data-workbench-page="schedule"
        >
          <SchedulePage
            registerHostActions={registerHostActions}
            activeTransportMode={modeForPage("schedule")}
            isActive={renderedPage === "schedule"}
          />
        </div>
        <div
          className={`dw-native-workbench-page ${renderedPage === "planner" ? "is-active" : "is-inactive"} is-${pageStage}`}
          data-workbench-page="planner"
        >
          {debugToolsEnabled ? (
            <PlannerPage pageEnterSequence={plannerEnterSequence} activeTransportMode={modeForPage("planner")} />
          ) : null}
        </div>
        <div
          className={`dw-native-workbench-page ${renderedPage === "broadcast" ? "is-active" : "is-inactive"} is-${pageStage}`}
          data-workbench-page="broadcast"
        >
          <BroadcastPage pageEnterSequence={broadcastEnterSequence} activeTransportMode={modeForPage("broadcast")} />
        </div>
        <div
          className={`dw-native-workbench-page ${renderedPage === "overview" ? "is-active" : "is-inactive"} is-${pageStage}`}
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
          className={`dw-native-workbench-page ${renderedPage === "passenger" ? "is-active" : "is-inactive"} is-${pageStage}`}
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
