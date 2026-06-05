import { useEffect, useMemo, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { buildPassengerFlowViewModel, filterPassengerFlow } from "./passenger-view-model";
import PassengerLineTabs from "./components/PassengerLineTabs";
import PassengerMetricCards from "./components/PassengerMetricCards";
import PassengerOdFlowDiagram from "./components/PassengerOdFlowDiagram";
import PassengerSectionRanking from "./components/PassengerSectionRanking";
import PassengerStationVolumeChart from "./components/PassengerStationVolumeChart";
import PassengerTrendChart from "./components/PassengerTrendChart";

function ChartPanel({ title, children, large = false }) {
  return (
    <section className={`rtw-passenger-panel ${large ? "is-large" : ""}`}>
      <h3 className="rtw-passenger-panel-title">{title}</h3>
      {children}
    </section>
  );
}

export default function PassengerFlowPage() {
  const [snapshot, setSnapshot] = useState(null);
  const [metadataSnapshot, setMetadataSnapshot] = useState(null);
  const [selectedLineId, setSelectedLineId] = useState("ALL");
  const [error, setError] = useState("");

  useEffect(() => {
    let cancelled = false;
    const api = getWorkbenchApi();

    Promise.all([api.loadSnapshot(), api.refreshMetadata()])
      .then(([nextSnapshot, nextMetadata]) => {
        if (cancelled) {
          return;
        }
        setSnapshot(nextSnapshot);
        setMetadataSnapshot(nextMetadata);
        setError("");
      })
      .catch((loadError) => {
        if (cancelled) {
          return;
        }
        setError(loadError?.message || "Unable to load passenger flow data.");
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const viewModel = useMemo(
    () => buildPassengerFlowViewModel(snapshot || {}, metadataSnapshot || {}),
    [metadataSnapshot, snapshot]
  );
  const filteredData = useMemo(
    () => filterPassengerFlow(viewModel, selectedLineId),
    [selectedLineId, viewModel]
  );

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
        <div className="rtw-passenger-header">
          <h2 className="rtw-passenger-title">全息客流数据中心 / ANALYTICS</h2>
          <PassengerLineTabs lines={viewModel.lines} selectedLineId={selectedLineId} onSelect={setSelectedLineId} />
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
              <PassengerOdFlowDiagram flows={filteredData.odFlows} />
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
  );
}
