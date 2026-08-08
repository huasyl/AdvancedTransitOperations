import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { buildPassengerFlowViewModel, filterPassengerFlow } from "./passenger-view-model";
import PassengerLineTabs from "./components/PassengerLineTabs";
import PassengerMetricCards from "./components/PassengerMetricCards";
import PassengerOdFlowDiagram from "./components/PassengerOdFlowDiagram";
import PassengerSectionRanking from "./components/PassengerSectionRanking";
import PassengerStationVolumeChart from "./components/PassengerStationVolumeChart";
import PassengerTrendChart from "./components/PassengerTrendChart";
import { traceWorkbench } from "../../shared/workbench-trace";
import WorkbenchScrollArea from "../../shared/WorkbenchScrollArea";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";

function ChartPanel({ title, children, large = false }) {
  return (
    <section className={`rtw-passenger-panel ${large ? "is-large" : ""}`}>
      <h3 className="rtw-passenger-panel-title">{title}</h3>
      {children}
    </section>
  );
}

function normalizePassengerMode(mode) {
  const token = String(mode || "").trim().toLowerCase();
  return token === "subway" || token === "tram" || token === "bus" ? token : "train";
}

const PASSENGER_FLOW_POLL_INTERVAL_MS = 5000;
const PASSENGER_CATALOG_RETRY_INTERVAL_MS = 30000;

function hasPassengerLineIds(snapshot) {
  const directRows = [snapshot?.stationVolumes, snapshot?.sectionVolumes];
  if (directRows.some((rows) => Array.isArray(rows) && rows.some((entry) => String(entry?.lineId || "").trim()))) {
    return true;
  }

  return Array.isArray(snapshot?.odFlows) && snapshot.odFlows.some((entry) => (
    String(entry?.lineId || entry?.firstLineId || entry?.lastLineId || "").trim()
  ));
}

function hasCatalogLines(snapshot) {
  return Array.isArray(snapshot?.lines)
    && snapshot.lines.some((line) => String(line?.id || "").trim());
}

function buildEmptyPassengerFlowViewModel() {
  return {
    lines: [],
    lineTrendById: {},
    systemTrend: [],
    stationVolumes: [],
    sectionVolumes: [],
    odFlows: [],
    warnings: []
  };
}

export default function PassengerFlowPage({ activeTransportMode = "train", isActive = false, registerHostActions }) {
  const { t } = useNativeScheduleI18n();
  const supportsSections = normalizePassengerMode(activeTransportMode) !== "bus";
  const [snapshot, setSnapshot] = useState(null);
  const [lineCatalogSnapshot, setLineCatalogSnapshot] = useState(null);
  const [selectedLineId, setSelectedLineId] = useState("ALL");
  const [error, setError] = useState("");
  const loadGenerationRef = useRef(0);
  const mountedRef = useRef(false);
  const refreshInFlightRef = useRef(false);
  const pollInFlightRef = useRef(false);
  const catalogReadyRef = useRef(false);
  const passengerHasLineRef = useRef(false);
  const catalogRetryAfterRef = useRef(0);
  const catalogRequestsRef = useRef(new Map());
  const activeModeRef = useRef(normalizePassengerMode(activeTransportMode));
  activeModeRef.current = normalizePassengerMode(activeTransportMode);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      loadGenerationRef.current += 1;
      traceWorkbench("passenger.unmount");
    };
  }, []);

  const loadLineCatalog = useCallback((api, mode) => {
    const existingRequest = catalogRequestsRef.current.get(mode);
    if (existingRequest) {
      return existingRequest;
    }

    const request = api.refreshTransitCatalog({ mode }).catch((metadataError) => {
      traceWorkbench("passenger.lineCatalog.error", { mode, message: metadataError?.message || metadataError });
      return null;
    });
    catalogRequestsRef.current.set(mode, request);
    request.finally(() => {
      if (catalogRequestsRef.current.get(mode) === request) {
        catalogRequestsRef.current.delete(mode);
      }
    });
    return request;
  }, []);

  const shouldLoadLineCatalog = useCallback((mode) => (
    catalogRequestsRef.current.has(mode)
      || (passengerHasLineRef.current
        && !catalogReadyRef.current
        && Date.now() >= catalogRetryAfterRef.current)
  ), []);

  const refreshPassengerFlow = useCallback(async ({ includeCatalog = false, reset = false, reason = "refresh" } = {}) => {
    const mode = normalizePassengerMode(activeTransportMode);
    if (reason === "poll" && (pollInFlightRef.current || refreshInFlightRef.current)) {
      return;
    }

    const generation = loadGenerationRef.current + 1;
    loadGenerationRef.current = generation;
    refreshInFlightRef.current = true;
    if (reason === "poll") {
      pollInFlightRef.current = true;
    }

    traceWorkbench("passenger.load.begin", { mode, reason, includeCatalog });
    const api = getWorkbenchApi();

    if (reset) {
      setSelectedLineId("ALL");
      setSnapshot(null);
      setLineCatalogSnapshot(null);
      catalogReadyRef.current = false;
      passengerHasLineRef.current = false;
      catalogRetryAfterRef.current = 0;
    }
    setError("");

    try {
      const [nextSnapshot, nextLineCatalogSnapshot] = await Promise.all([
        api.loadPassengerFlowSnapshot({ mode }),
        includeCatalog
          ? loadLineCatalog(api, mode)
          : Promise.resolve(null)
      ]);

      if (!mountedRef.current || loadGenerationRef.current !== generation || activeModeRef.current !== mode) {
        return;
      }

      const nextHasPassengerLines = hasPassengerLineIds(nextSnapshot);
      const nextHasCatalogLines = hasCatalogLines(nextLineCatalogSnapshot);
      passengerHasLineRef.current = nextHasPassengerLines;
      setSnapshot(nextSnapshot);
      if (nextLineCatalogSnapshot) {
        setLineCatalogSnapshot(nextLineCatalogSnapshot);
      }
      if (nextHasCatalogLines) {
        catalogReadyRef.current = true;
        catalogRetryAfterRef.current = 0;
      } else if (includeCatalog && nextHasPassengerLines) {
        catalogRetryAfterRef.current = Date.now() + PASSENGER_CATALOG_RETRY_INTERVAL_MS;
      }
      setError("");
      traceWorkbench("passenger.load.done", {
        mode,
        reason,
        stationVolumes: Array.isArray(nextSnapshot?.stationVolumes) ? nextSnapshot.stationVolumes.length : 0,
        sectionVolumes: Array.isArray(nextSnapshot?.sectionVolumes) ? nextSnapshot.sectionVolumes.length : 0,
        odFlows: Array.isArray(nextSnapshot?.odFlows) ? nextSnapshot.odFlows.length : 0,
        lines: Array.isArray(nextLineCatalogSnapshot?.lines) ? nextLineCatalogSnapshot.lines.length : 0
      });
    } catch (loadError) {
      if (!mountedRef.current || loadGenerationRef.current !== generation || activeModeRef.current !== mode) {
        return;
      }
      setError(loadError?.message || t("nativeWorkbench.passenger.error.loadFailed"));
      traceWorkbench("passenger.load.error", { mode, reason, message: loadError?.message || loadError });
    } finally {
      if (loadGenerationRef.current === generation) {
        refreshInFlightRef.current = false;
      }
      if (reason === "poll") {
        pollInFlightRef.current = false;
      }
    }
  }, [activeTransportMode, loadLineCatalog, t]);

  useEffect(() => {
    refreshPassengerFlow({ includeCatalog: true, reset: true, reason: "mode" });
  }, [refreshPassengerFlow]);

  useEffect(() => {
    if (!isActive) {
      return undefined;
    }

    refreshPassengerFlow({
      includeCatalog: shouldLoadLineCatalog(normalizePassengerMode(activeTransportMode)),
      reason: "active"
    });
    const intervalId = window.setInterval(() => {
      const includeCatalog = passengerHasLineRef.current
        && !catalogReadyRef.current
        && Date.now() >= catalogRetryAfterRef.current;
      refreshPassengerFlow({ includeCatalog, reason: "poll" });
    }, PASSENGER_FLOW_POLL_INTERVAL_MS);

    return () => {
      window.clearInterval(intervalId);
    };
  }, [activeTransportMode, isActive, refreshPassengerFlow, shouldLoadLineCatalog]);

  useEffect(() => {
    if (!isActive) {
      return undefined;
    }

    const mode = normalizePassengerMode(activeTransportMode);
    if (typeof window !== "undefined") {
      window.__RT_WORKBENCH_ACTIVE_PAGE__ = "passenger";
      window.__RT_WORKBENCH_SELECTED_LINE_ID__ = selectedLineId === "ALL" ? "" : selectedLineId;
      window.__RT_WORKBENCH_SELECTED_EDIT_LINE__ = "";
    }
    getWorkbenchApi().setHostState?.({
      mode,
      activePage: "passenger",
      selectedLineId: selectedLineId === "ALL" ? "" : selectedLineId,
      selectedEditLine: ""
    });
    return undefined;
  }, [activeTransportMode, isActive, selectedLineId]);

  useEffect(() => {
    if (!isActive || typeof registerHostActions !== "function") {
      return undefined;
    }

    registerHostActions({
      refreshData: async () => {
        await refreshPassengerFlow({
          includeCatalog: true,
          reason: "host"
        });
      }
    });

    return () => {
      registerHostActions(null);
    };
  }, [isActive, refreshPassengerFlow, registerHostActions]);

  const viewModel = useMemo(
    () => (snapshot ? buildPassengerFlowViewModel(snapshot || {}, lineCatalogSnapshot || {}) : buildEmptyPassengerFlowViewModel()),
    [lineCatalogSnapshot, snapshot]
  );
  const filteredData = useMemo(
    () => filterPassengerFlow(viewModel, selectedLineId),
    [selectedLineId, viewModel]
  );

  useEffect(() => {
    traceWorkbench("passenger.data.ready", {
      active: isActive,
      selectedLineId,
      lines: viewModel.lines.length,
      trend: filteredData.systemTrend.length,
      stations: filteredData.stationVolumes.length,
      od: filteredData.odFlows.length,
      sections: filteredData.sectionVolumes.length
    });
  }, [filteredData, isActive, selectedLineId, viewModel.lines.length]);

  function handleLineSelect(lineId) {
    traceWorkbench("passenger.line.select", { lineId, from: selectedLineId });
    setSelectedLineId(lineId);
  }

  if (error) {
    return (
      <div className="rtw-passenger-root">
        <div className="rtw-passenger-error">{error}</div>
      </div>
    );
  }

  return (
    <div className="rtw-passenger-root">
      <WorkbenchScrollArea className="rtw-passenger-body" metricsKey={`${selectedLineId}:${filteredData.stationVolumes.length}:${filteredData.sectionVolumes.length}:${filteredData.odFlows.length}`}>
        <div className="rtw-passenger-content">
          <div className="rtw-passenger-header">
            <h2 className="rtw-passenger-title">{t("nativeWorkbench.passenger.title")}</h2>
            <PassengerLineTabs lines={viewModel.lines} selectedLineId={selectedLineId} onSelect={handleLineSelect} />
          </div>
          <PassengerMetricCards data={filteredData} showSections={supportsSections} />
          <div className="rtw-passenger-panels">
            <ChartPanel title={selectedLineId === "ALL" ? t("nativeWorkbench.passenger.chart.systemTrend") : t("nativeWorkbench.passenger.chart.lineTrend")}>
              <div className="rtw-passenger-chart is-trend">
                <PassengerTrendChart points={filteredData.systemTrend} />
              </div>
            </ChartPanel>
            <ChartPanel title={t("nativeWorkbench.passenger.chart.stationVolumes")}>
              <div className="rtw-passenger-chart is-stations">
                <PassengerStationVolumeChart volumes={filteredData.stationVolumes} />
              </div>
            </ChartPanel>
            <ChartPanel title={t("nativeWorkbench.passenger.chart.odFlow")} large>
              <div className="rtw-passenger-chart is-od">
                <PassengerOdFlowDiagram flows={filteredData.odFlows} lines={viewModel.lines} isActive={isActive} />
              </div>
            </ChartPanel>
            {supportsSections ? (
              <ChartPanel title={t("nativeWorkbench.passenger.chart.sectionRanking")} large>
                <div className="rtw-passenger-chart is-ranking">
                  <PassengerSectionRanking sections={filteredData.sectionVolumes} lines={viewModel.lines} />
                </div>
              </ChartPanel>
            ) : null}
          </div>
        </div>
      </WorkbenchScrollArea>
    </div>
  );
}
