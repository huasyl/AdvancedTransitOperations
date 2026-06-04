import { useLayoutEffect, useMemo, useState } from "react";
import PlannerPage from "./pages/planner/PlannerPage";
import BroadcastPage from "./pages/broadcast/BroadcastPage";
import { useNativeScheduleI18n } from "./shared/workbench-i18n";
import OverviewPage from "./pages/overview/OverviewPage";
import SchedulePage from "./pages/schedule/SchedulePage";

const DEFAULT_NATIVE_WORKBENCH_PAGE = "schedule";
const WORKBENCH_PAGE_TRANSITION_MS = 220;

function requestWorkbenchClose() {
  if (typeof window === "undefined") {
    return;
  }

  const closeHandler = window.__RT_NATIVE_WORKBENCH_CLOSE__;
  if (typeof closeHandler === "function") {
    closeHandler();
  }
}

export default function WorkbenchApp({ registerHostActions }) {
  const { locale, script, t } = useNativeScheduleI18n();
  const [activePage, setActivePage] = useState(DEFAULT_NATIVE_WORKBENCH_PAGE);
  const [renderedPage, setRenderedPage] = useState(DEFAULT_NATIVE_WORKBENCH_PAGE);
  const [pageStage, setPageStage] = useState("entered");
  const [plannerEnterSequence, setPlannerEnterSequence] = useState(0);
  const [broadcastEnterSequence, setBroadcastEnterSequence] = useState(0);
  const pageTabs = useMemo(
    () => ([
      { key: "schedule", label: t("nativeWorkbench.tab.schedule") },
      { key: "planner", label: t("nativeWorkbench.tab.planner") },
      { key: "broadcast", label: t("nativeWorkbench.tab.broadcast") },
      { key: "overview", label: t("nativeWorkbench.tab.overview") }
    ]),
    [t]
  );
  const overviewLabels = useMemo(
    () => ({
      overviewKicker: t("nativeWorkbench.overview.kicker"),
      overviewTitle: t("nativeWorkbench.overview.title"),
      overviewCopy: t("nativeWorkbench.overview.copy")
    }),
    [t]
  );

  useLayoutEffect(() => {
    if (activePage === renderedPage) {
      return undefined;
    }

    if (activePage === "broadcast") {
      setRenderedPage(activePage);
      setBroadcastEnterSequence((current) => current + 1);
      setPageStage("entered");
      return undefined;
    }

    setPageStage("exiting");
    const timer = window.setTimeout(() => {
      setRenderedPage(activePage);
      if (activePage === "planner") {
        setPlannerEnterSequence((current) => current + 1);
      }
      if (activePage === "broadcast") {
        setBroadcastEnterSequence((current) => current + 1);
      }
      setPageStage("entering");
      const raf = window.requestAnimationFrame(() => {
        setPageStage("entered");
      });
      return () => window.cancelAnimationFrame(raf);
    }, WORKBENCH_PAGE_TRANSITION_MS);

    return () => window.clearTimeout(timer);
  }, [activePage, renderedPage]);

  return (
    <div
      className="dw-native-schedule-root dw-native-workbench-root"
      data-native-schedule-locale={locale}
      data-native-schedule-script={script}
      data-native-workbench-page={activePage}
      lang={locale}
    >
      <div className="dw-native-workbench-tabs-shell">
        <div className="dw-native-workbench-tabs-row">
          <div className="dw-native-workbench-tabs">
            {pageTabs.map((tab) => (
              <button
                key={tab.key}
                type="button"
                className={`dw-native-workbench-tab ${activePage === tab.key ? "is-active" : ""}`}
                onClick={() => setActivePage(tab.key)}
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
          <SchedulePage registerHostActions={registerHostActions} />
        </div>
        <div
          className={`dw-native-workbench-page ${renderedPage === "planner" ? "is-active" : "is-inactive"} is-${pageStage}`}
          data-workbench-page="planner"
        >
          <PlannerPage pageEnterSequence={plannerEnterSequence} />
        </div>
        <div
          className={`dw-native-workbench-page ${renderedPage === "broadcast" ? "is-active" : "is-inactive"} is-${pageStage}`}
          data-workbench-page="broadcast"
        >
          <BroadcastPage pageEnterSequence={broadcastEnterSequence} />
        </div>
        <div
          className={`dw-native-workbench-page ${renderedPage === "overview" ? "is-active" : "is-inactive"} is-${pageStage}`}
          data-workbench-page="overview"
        >
          <OverviewPage labels={overviewLabels} />
        </div>
      </div>
    </div>
  );
}
