import { useEffect, useMemo, useRef, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { buildPassengerFlowViewModel, filterPassengerFlow } from "./passenger-view-model";
import PassengerLineTabs from "./components/PassengerLineTabs";
import PassengerMetricCards from "./components/PassengerMetricCards";
import PassengerOdFlowDiagram from "./components/PassengerOdFlowDiagram";
import PassengerSectionRanking from "./components/PassengerSectionRanking";
import PassengerStationVolumeChart from "./components/PassengerStationVolumeChart";
import PassengerTrendChart from "./components/PassengerTrendChart";
import { traceWorkbench } from "../../shared/workbench-trace";

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
  return token === "subway" ? "subway" : "train";
}

function hasScopedLines(snapshot) {
  return Array.isArray(snapshot?.lines) && snapshot.lines.length > 0;
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

export default function PassengerFlowPage({ activeTransportMode = "train", isActive = false }) {
  const [snapshot, setSnapshot] = useState(null);
  const [metadataSnapshot, setMetadataSnapshot] = useState(null);
  const [selectedLineId, setSelectedLineId] = useState("ALL");
  const [error, setError] = useState("");
  const modeCacheRef = useRef({});
  const loadGenerationRef = useRef(0);
  const activeModeRef = useRef(normalizePassengerMode(activeTransportMode));
  activeModeRef.current = normalizePassengerMode(activeTransportMode);

  useEffect(() => {
    const mode = normalizePassengerMode(activeTransportMode);
    const generation = loadGenerationRef.current + 1;
    loadGenerationRef.current = generation;
    traceWorkbench("passenger.mount", { mode });
    let cancelled = false;
    const api = getWorkbenchApi();
    const cached = modeCacheRef.current[mode] || null;

    setSelectedLineId("ALL");
    setSnapshot(cached?.snapshot || null);
    setMetadataSnapshot(cached?.metadataSnapshot || null);
    setError("");

    if (cached) {
      traceWorkbench("passenger.load.cache", { mode });
      return () => {
        cancelled = true;
        traceWorkbench("passenger.unmount");
      };
    }

    Promise.all([api.loadSnapshot({ mode }), api.refreshMetadata({ mode })])
      .then(([nextSnapshot, nextMetadata]) => {
        if (cancelled || loadGenerationRef.current !== generation || activeModeRef.current !== mode) {
          return;
        }
        modeCacheRef.current[mode] = {
          snapshot: nextSnapshot,
          metadataSnapshot: nextMetadata
        };
        setSnapshot(nextSnapshot);
        setMetadataSnapshot(nextMetadata);
        setError("");
        traceWorkbench("passenger.load.done", {
          mode,
          lines: Array.isArray(nextMetadata?.lines) ? nextMetadata.lines.length : 0
        });
      })
      .catch((loadError) => {
        if (cancelled || loadGenerationRef.current !== generation || activeModeRef.current !== mode) {
          return;
        }
        setError(loadError?.message || "Unable to load passenger flow data.");
        traceWorkbench("passenger.load.error", { mode, message: loadError?.message || loadError });
      });

    return () => {
      cancelled = true;
      traceWorkbench("passenger.unmount");
    };
  }, [activeTransportMode]);

  const viewModel = useMemo(
    () => (
      hasScopedLines(snapshot) || hasScopedLines(metadataSnapshot)
        ? buildPassengerFlowViewModel(snapshot || {}, metadataSnapshot || {})
        : buildEmptyPassengerFlowViewModel()
    ),
    [metadataSnapshot, snapshot]
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
      <div className="rtw-passenger-body">
        <div className="rtw-passenger-content">
          <div className="rtw-passenger-header">
            <h2 className="rtw-passenger-title">全息客流数据中心 / ANALYTICS</h2>
            <PassengerLineTabs lines={viewModel.lines} selectedLineId={selectedLineId} onSelect={handleLineSelect} />
          </div>
          <PassengerMetricCards data={filteredData} />
          <div className="rtw-passenger-panels">
            <ChartPanel title={selectedLineId === "ALL" ? "全网分时客流走势 / SYSTEM TREND" : "单线分时客流走势 / LINE TREND"}>
              <div className="rtw-passenger-chart is-trend">
                <PassengerTrendChart points={filteredData.systemTrend} />
              </div>
            </ChartPanel>
            <ChartPanel title="各站进出站量 / STATION VOLUMES">
              <div className="rtw-passenger-chart is-stations">
                <PassengerStationVolumeChart volumes={filteredData.stationVolumes} />
              </div>
            </ChartPanel>
            <ChartPanel title="站间客流 OD 矩阵 / O-D FLOW" large>
              <div className="rtw-passenger-chart is-od">
                <PassengerOdFlowDiagram flows={filteredData.odFlows} lines={viewModel.lines} isActive={isActive} />
              </div>
            </ChartPanel>
            <ChartPanel title="最高压断面管段排行 (Top 10) / SECTION VOLUME RANKING" large>
              <div className="rtw-passenger-chart is-ranking">
                <PassengerSectionRanking sections={filteredData.sectionVolumes} />
              </div>
            </ChartPanel>
          </div>
        </div>
      </div>
    </div>
  );
}
