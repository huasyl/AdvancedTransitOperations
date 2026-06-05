import { useEffect, useMemo, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { buildOverviewViewModel } from "./overview-view-model";
import OverviewHeaderStats from "./components/OverviewHeaderStats";
import OverviewModeRail from "./components/OverviewModeRail";
import OverviewNetworkDiagram from "./components/OverviewNetworkDiagram";
import OverviewSystemSwitches from "./components/OverviewSystemSwitches";

export default function OverviewPage() {
  const [snapshot, setSnapshot] = useState(null);
  const [metadataSnapshot, setMetadataSnapshot] = useState(null);
  const [activeMode, setActiveMode] = useState("");
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
        setError(loadError?.message || "Unable to load overview data.");
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const viewModel = useMemo(
    () => buildOverviewViewModel(snapshot || {}, metadataSnapshot || {}),
    [metadataSnapshot, snapshot]
  );

  useEffect(() => {
    if (!activeMode && viewModel.activeMode) {
      setActiveMode(viewModel.activeMode);
    }
  }, [activeMode, viewModel.activeMode]);

  const selectedMode = activeMode || viewModel.activeMode;
  const modeSummary = viewModel.modes.find((mode) => mode.mode === selectedMode) || viewModel.modes[0] || {
    estimatedPassengerLoad: 0,
    activeVehicleCount: 0,
    scheduledVehicleCount: 0,
    healthPercent: 0
  };
  const firstStation = viewModel.network.stations[0];
  const summary = {
    ...modeSummary,
    peakStationName: firstStation?.name || ""
  };

  if (error) {
    return (
      <div className="rtw-overview-root">
        <div className="rtw-overview-error">{error}</div>
      </div>
    );
  }

  return (
    <div className="rtw-overview-root">
      <div className="rtw-overview-body">
        <aside className="rtw-overview-sidebar">
          <OverviewModeRail modes={viewModel.modes} activeMode={selectedMode} onModeChange={setActiveMode} />
          <OverviewSystemSwitches systems={viewModel.systems} />
          <div className="rtw-overview-footer-tag">CS2-BUS-SUB-V0.90 / ONLINE</div>
        </aside>
        <main className="rtw-overview-main">
          <OverviewHeaderStats summary={summary} />
          <OverviewNetworkDiagram
            lines={viewModel.network.lines}
            stations={viewModel.network.stations}
            vehicles={viewModel.network.vehicles}
            activeMode={selectedMode}
          />
        </main>
      </div>
    </div>
  );
}
