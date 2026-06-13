import { useEffect, useMemo, useRef, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { buildOverviewViewModel } from "./overview-view-model";
import OverviewHeaderStats from "./components/OverviewHeaderStats";
import OverviewModeRail from "./components/OverviewModeRail";
import OverviewNetworkDiagram from "./components/OverviewNetworkDiagram";
import OverviewSystemSwitches from "./components/OverviewSystemSwitches";
import useOverviewFeatureSettings from "./useOverviewFeatureSettings";
import { traceWorkbench } from "../../shared/workbench-trace";

function toOverviewMode(mode) {
  const token = String(mode || "").toLowerCase();
  if (token === "subway") {
    return "Subway";
  }
  return "Train";
}

function toTransportMode(mode) {
  const token = String(mode || "").toLowerCase();
  return token === "subway" ? "subway" : "train";
}

function hasScopedLines(snapshot) {
  return Array.isArray(snapshot?.lines) && snapshot.lines.length > 0;
}

function buildOverviewSystems(featureSettings) {
  return [
    { key: "dispatchEnabled", title: "发车控制", enabled: featureSettings?.dispatchEnabled !== false },
    { key: "bypassEnabled", title: "智能待避", enabled: featureSettings?.bypassEnabled !== false },
    { key: "broadcastEnabled", title: "自动广播", enabled: featureSettings?.broadcastEnabled !== false },
    { key: "depotLockEnabled", title: "车库锁定", enabled: featureSettings?.depotLockEnabled !== false }
  ];
}

function buildEmptyOverviewViewModel(featureSettings) {
  return {
    generatedAtGameMinute: 0,
    modes: [
      { mode: "Subway", label: "城市地铁", lineCount: 0, activeVehicleCount: 0, scheduledVehicleCount: 0, estimatedPassengerLoad: 0, healthPercent: 0 },
      { mode: "Train", label: "城际铁路", lineCount: 0, activeVehicleCount: 0, scheduledVehicleCount: 0, estimatedPassengerLoad: 0, healthPercent: 0 }
    ],
    activeMode: "Train",
    network: {
      lines: [],
      stations: [],
      vehicles: []
    },
    systems: buildOverviewSystems(featureSettings),
    warnings: []
  };
}

export default function OverviewPage({ activeTransportMode = "train", onTransportModeChange }) {
  const [snapshot, setSnapshot] = useState(null);
  const [metadataSnapshot, setMetadataSnapshot] = useState(null);
  const [error, setError] = useState("");
  const modeCacheRef = useRef({});
  const loadGenerationRef = useRef(0);
  const activeModeRef = useRef(toTransportMode(activeTransportMode));
  activeModeRef.current = toTransportMode(activeTransportMode);

  useEffect(() => {
    const mode = toTransportMode(activeTransportMode);
    const generation = loadGenerationRef.current + 1;
    loadGenerationRef.current = generation;
    traceWorkbench("overview.mount", { mode });
    let cancelled = false;
    const api = getWorkbenchApi();
    const cached = modeCacheRef.current[mode] || null;

    setSnapshot(cached?.snapshot || null);
    setMetadataSnapshot(cached?.metadataSnapshot || null);
    setError("");

    if (cached) {
      traceWorkbench("overview.load.cache", { mode });
      return () => {
        cancelled = true;
        traceWorkbench("overview.unmount");
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
        traceWorkbench("overview.load.done", {
          mode,
          lines: Array.isArray(nextMetadata?.lines) ? nextMetadata.lines.length : 0
        });
      })
      .catch((loadError) => {
        if (cancelled || loadGenerationRef.current !== generation || activeModeRef.current !== mode) {
          return;
        }
        setError(loadError?.message || "Unable to load overview data.");
        traceWorkbench("overview.load.error", { mode, message: loadError?.message || loadError });
      });

    return () => {
      cancelled = true;
      traceWorkbench("overview.unmount");
    };
  }, [activeTransportMode]);

  const snapshotFeatureSettings = snapshot?.featureSettings || null;

  const viewModel = useMemo(
    () => (
      hasScopedLines(snapshot) || hasScopedLines(metadataSnapshot)
        ? buildOverviewViewModel(snapshot || {}, metadataSnapshot || {})
        : buildEmptyOverviewViewModel(snapshotFeatureSettings)
    ),
    [metadataSnapshot, snapshot, snapshotFeatureSettings]
  );

  const selectedMode = toOverviewMode(activeTransportMode || viewModel.activeMode);
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

  function handleFeatureSettingsSaved(nextFeatureSettings) {
    setSnapshot((current) => (
      current
        ? {
            ...current,
            featureSettings: nextFeatureSettings
          }
        : current
    ));

    Object.keys(modeCacheRef.current).forEach((modeKey) => {
      const cached = modeCacheRef.current[modeKey];
      if (!cached?.snapshot) {
        return;
      }

      modeCacheRef.current[modeKey] = {
        ...cached,
        snapshot: {
          ...cached.snapshot,
          featureSettings: nextFeatureSettings
        }
      };
    });
  }

  const overviewFeatureSettings = useOverviewFeatureSettings({
    featureSettings: snapshotFeatureSettings,
    systems: viewModel.systems,
    canEdit: Boolean(snapshotFeatureSettings),
    onSaved: handleFeatureSettingsSaved
  });

  function handleModeChange(mode) {
    const nextTransportMode = toTransportMode(mode);
    traceWorkbench("overview.mode.change", { mode: nextTransportMode, from: activeTransportMode });
    if (typeof onTransportModeChange === "function") {
      onTransportModeChange(nextTransportMode);
    }
  }

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
          <OverviewModeRail modes={viewModel.modes} activeMode={selectedMode} onModeChange={handleModeChange} />
          <OverviewSystemSwitches systems={overviewFeatureSettings.systems} onSystemToggle={overviewFeatureSettings.toggleFeature} />
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
