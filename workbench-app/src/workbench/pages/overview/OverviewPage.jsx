import { useEffect, useMemo, useRef, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { buildOverviewViewModel } from "./overview-view-model";
import { buildOverviewMetrics } from "./overview-metrics";
import OverviewHeaderStats from "./components/OverviewHeaderStats";
import OverviewModeRail from "./components/OverviewModeRail";
import OverviewNetworkDiagram from "./components/OverviewNetworkDiagram";
import OverviewSystemSwitches from "./components/OverviewSystemSwitches";
import useOverviewFeatureSettings from "./useOverviewFeatureSettings";
import { traceWorkbench } from "../../shared/workbench-trace";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";

const PASSENGER_CACHE_TTL_MS = 60000;

function toOverviewMode(mode) {
  const token = String(mode || "").toLowerCase();
  if (token === "subway") {
    return "Subway";
  }
  if (token === "tram") {
    return "Tram";
  }
  if (token === "bus") {
    return "Bus";
  }
  return "Train";
}

function toTransportMode(mode) {
  const token = String(mode || "").toLowerCase();
  if (token === "subway" || token === "tram" || token === "bus") {
    return token;
  }
  return "train";
}

function hasScopedLines(snapshot) {
  return Array.isArray(snapshot?.lines) && snapshot.lines.length > 0;
}

function snapshotForMode(snapshot, cachedSnapshot, mode) {
  const snapshotMode = String(snapshot?.mode || "").trim().toLowerCase();
  if (snapshotMode === mode) {
    return snapshot;
  }

  const cachedMode = String(cachedSnapshot?.mode || "").trim().toLowerCase();
  return cachedMode === mode ? cachedSnapshot : null;
}

function buildOverviewSystems(featureSettings, t, mode) {
  const systems = [
    { key: "dispatchEnabled", title: t("nativeWorkbench.overview.system.dispatch"), enabled: featureSettings?.dispatchEnabled !== false },
    { key: "broadcastEnabled", title: t("nativeWorkbench.overview.system.broadcast"), enabled: featureSettings?.broadcastEnabled !== false },
    { key: "depotLockEnabled", title: t("nativeWorkbench.overview.system.depotLock"), enabled: featureSettings?.depotLockEnabled !== false }
  ];

  if (mode !== "Tram" && mode !== "Bus") {
    systems.splice(1, 0, { key: "bypassEnabled", title: t("nativeWorkbench.overview.system.bypass"), enabled: featureSettings?.bypassEnabled !== false });
  }

  return systems;
}

function buildEmptyOverviewViewModel(featureSettings, t, activeMode) {
  return {
    generatedAtGameMinute: 0,
    modes: [
      { mode: "Subway", label: t("nativeWorkbench.overview.mode.subway"), lineCount: 0, appliedDepartureCount: 0 },
      { mode: "Train", label: t("nativeWorkbench.overview.mode.train"), lineCount: 0, appliedDepartureCount: 0 },
      { mode: "Tram", label: t("nativeWorkbench.overview.mode.tram"), lineCount: 0, appliedDepartureCount: 0 },
      { mode: "Bus", label: t("nativeWorkbench.overview.mode.bus"), lineCount: 0, appliedDepartureCount: 0 }
    ],
    activeMode,
    network: {
      lines: [],
      stations: [],
      vehicles: []
    },
    systems: buildOverviewSystems(featureSettings, t, activeMode),
    warnings: []
  };
}

export default function OverviewPage({ activeTransportMode = "train", isActive = false, registerHostActions, onTransportModeChange }) {
  const { t } = useNativeScheduleI18n();
  const activeMode = toTransportMode(activeTransportMode);
  const [snapshot, setSnapshot] = useState(null);
  const [metadataSnapshot, setMetadataSnapshot] = useState(null);
  const [passengerSnapshot, setPassengerSnapshot] = useState(null);
  const [error, setError] = useState("");
  const [refreshNonce, setRefreshNonce] = useState(0);
  const modeCacheRef = useRef({});
  const loadGenerationRef = useRef(0);
  const activeModeRef = useRef(activeMode);
  activeModeRef.current = activeMode;
  const hasActivatedRef = useRef(false);
  const activeCache = modeCacheRef.current[activeMode] || null;
  const scopedSnapshot = snapshotForMode(snapshot, activeCache?.snapshot, activeMode);
  const scopedMetadataSnapshot = snapshotForMode(metadataSnapshot, activeCache?.metadataSnapshot, activeMode);
  const scopedPassengerSnapshot = snapshotForMode(passengerSnapshot, activeCache?.passengerSnapshot, activeMode);

  useEffect(() => {
    const mode = activeMode;
    const generation = loadGenerationRef.current + 1;
    loadGenerationRef.current = generation;
    traceWorkbench("overview.mount", { mode });
    let cancelled = false;
    const api = getWorkbenchApi();
    const cached = modeCacheRef.current[mode] || null;
    const shouldForceRefresh = refreshNonce > 0;

    setSnapshot(cached?.snapshot || null);
    setMetadataSnapshot(cached?.metadataSnapshot || null);
    setPassengerSnapshot(cached?.passengerSnapshot || null);
    setError("");

    const shouldRefreshPassenger = shouldForceRefresh
      || !cached?.passengerLoadedAtMs
      || (isActive && hasActivatedRef.current && (Date.now() - cached.passengerLoadedAtMs) > PASSENGER_CACHE_TTL_MS);
    if (isActive) {
      hasActivatedRef.current = true;
    }

    if (cached && !shouldForceRefresh && !shouldRefreshPassenger) {
      traceWorkbench("overview.load.cache", { mode });
      return () => {
        cancelled = true;
        traceWorkbench("overview.unmount");
      };
    }

    const snapshotPromise = shouldForceRefresh || !cached?.snapshot ? api.loadOverviewSnapshot({ mode }) : Promise.resolve(cached.snapshot);
    const passengerPromise = shouldRefreshPassenger || !cached?.passengerSnapshot
      ? api.loadPassengerFlowSnapshot({ mode })
      : Promise.resolve(cached.passengerSnapshot);

    Promise.all([snapshotPromise, passengerPromise])
      .then(([nextSnapshot, nextPassengerSnapshot]) => {
        if (cancelled || loadGenerationRef.current !== generation || activeModeRef.current !== mode) {
          return;
        }
        modeCacheRef.current[mode] = {
          snapshot: nextSnapshot,
          metadataSnapshot: nextSnapshot,
          passengerSnapshot: nextPassengerSnapshot,
          passengerLoadedAtMs: Date.now()
        };
        setSnapshot(nextSnapshot);
        setMetadataSnapshot(nextSnapshot);
        setPassengerSnapshot(nextPassengerSnapshot);
        setError("");
        traceWorkbench("overview.load.done", {
          mode,
          lines: Array.isArray(nextSnapshot?.lines) ? nextSnapshot.lines.length : 0,
          stationVolumes: Array.isArray(nextPassengerSnapshot?.stationVolumes) ? nextPassengerSnapshot.stationVolumes.length : 0
        });
      })
      .catch((loadError) => {
        if (cancelled || loadGenerationRef.current !== generation || activeModeRef.current !== mode) {
          return;
        }
        if (!cached?.snapshot && !cached?.metadataSnapshot && !cached?.passengerSnapshot) {
          setError(loadError?.message || t("nativeWorkbench.overview.error.loadFailed"));
        }
        traceWorkbench("overview.load.error", { mode, message: loadError?.message || loadError });
      });

    return () => {
      cancelled = true;
      traceWorkbench("overview.unmount");
    };
  }, [activeMode, isActive, refreshNonce, t]);

  const snapshotFeatureSettings = scopedSnapshot?.featureSettings || null;

  const viewModel = useMemo(
    () => (
      hasScopedLines(scopedSnapshot) || hasScopedLines(scopedMetadataSnapshot)
        ? buildOverviewViewModel(scopedSnapshot || {}, scopedMetadataSnapshot || {}, t)
        : buildEmptyOverviewViewModel(snapshotFeatureSettings, t, toOverviewMode(activeMode))
    ),
    [activeMode, scopedMetadataSnapshot, scopedSnapshot, t]
  );

  const overviewMetrics = useMemo(
    () => buildOverviewMetrics(scopedPassengerSnapshot || {}),
    [scopedPassengerSnapshot]
  );

  const selectedMode = toOverviewMode(activeTransportMode || viewModel.activeMode);
  const modeRailModes = useMemo(() => {
    const summariesByMode = new Map(viewModel.modes.map((mode) => [mode.mode, mode]));
    Object.entries(modeCacheRef.current).forEach(([modeKey, cached]) => {
      const cachedMode = toOverviewMode(modeKey);
      if (cachedMode === selectedMode || (!hasScopedLines(cached?.snapshot) && !hasScopedLines(cached?.metadataSnapshot))) {
        return;
      }
      const cachedViewModel = buildOverviewViewModel(cached.snapshot || {}, cached.metadataSnapshot || {}, t);
      const cachedSummary = cachedViewModel.modes.find((mode) => mode.mode === cachedMode);
      if (cachedSummary) {
        summariesByMode.set(cachedMode, cachedSummary);
      }
    });
    return viewModel.modes.map((mode) => summariesByMode.get(mode.mode) || mode);
  }, [selectedMode, scopedSnapshot, scopedMetadataSnapshot, t, viewModel.modes]);
  const modeSummary = modeRailModes.find((mode) => mode.mode === selectedMode) || modeRailModes[0] || {
    lineCount: 0,
    appliedDepartureCount: 0
  };
  const summary = {
    ...modeSummary,
    totalBoardingsAlightings24h: overviewMetrics.totalBoardingsAlightings24h,
    busiestStationName: overviewMetrics.busiestStationName,
    peakSectionLoad: overviewMetrics.peakSectionLoad,
    peakQuarterHourFlow: overviewMetrics.peakQuarterHourFlow
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

  useEffect(() => {
    const api = getWorkbenchApi();
    const unsubscribe = api.onSnapshotChanged?.((nextSnapshot) => {
      if (!nextSnapshot || toTransportMode(nextSnapshot?.mode) !== activeModeRef.current) {
        return;
      }

      const mode = activeModeRef.current;
      const cached = modeCacheRef.current[mode] || {};
      modeCacheRef.current[mode] = {
        ...cached,
        snapshot: nextSnapshot
      };
      setSnapshot(nextSnapshot);
    });

    return () => {
      if (typeof unsubscribe === "function") {
        unsubscribe();
      }
    };
  }, []);

  useEffect(() => {
    if (!isActive || typeof registerHostActions !== "function") {
      return undefined;
    }

    registerHostActions({
      refreshData: async () => {
        setRefreshNonce((current) => current + 1);
      }
    });

    return () => {
      registerHostActions(null);
    };
  }, [isActive, registerHostActions]);

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
          <OverviewModeRail modes={modeRailModes} activeMode={selectedMode} onModeChange={handleModeChange} />
          <OverviewSystemSwitches systems={overviewFeatureSettings.systems} onSystemToggle={overviewFeatureSettings.toggleFeature} />
          <div className="rtw-overview-footer">
            <div className="rtw-overview-footer-tag">{t("nativeWorkbench.overview.footer.online")}</div>
            <div className="rtw-overview-footer-warning">{t("nativeWorkbench.overview.footer.tteWarning")}</div>
          </div>
        </aside>
        <main className="rtw-overview-main">
          <OverviewHeaderStats summary={summary} showSectionLoad={selectedMode !== "Bus"} />
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
