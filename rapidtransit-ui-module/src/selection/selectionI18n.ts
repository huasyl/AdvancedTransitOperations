import * as l10n from "cs2/l10n";
import { useCallback } from "react";

const KEYS = {
  panelTitle: "RapidTransit.PanelTitle",
  vehicleTitle: "RapidTransit.VehicleTitle",
  lineTitle: "RapidTransit.LineTitle",
  emptyHint: "RapidTransit.EmptyHint",
  dispatch: "RapidTransit.Dispatch",
  retire: "RapidTransit.Retire",
  departNow: "RapidTransit.DepartNow",
  spawnOne: "RapidTransit.SpawnOne",
  dumpTrackModel: "RapidTransit.DumpTrackModel",
  dumpPlannerInput: "RapidTransit.DumpPlannerInput",
  dumpObservation: "RapidTransit.DumpObservation",
  dumpStationAnchorObservation: "RapidTransit.DumpStationAnchorObservation",
  topButtonTitle: "RapidTransit.TopButtonTitle",
  topButtonDescription: "RapidTransit.TopButtonDescription",
  none: "RapidTransit.None",
  vanillaControl: "RapidTransit.VanillaControl",
  officialDispatch: "RapidTransit.OfficialDispatch",
  state: "RapidTransit.State",
  lineName: "RapidTransit.LineName",
  nextSlot: "RapidTransit.NextSlot",
  routeDuration: "RapidTransit.RouteDuration",
  bypassStation: "RapidTransit.BypassStation",
  currentSlot: "RapidTransit.CurrentSlot",
  targetSlot: "RapidTransit.TargetSlot",
  currentStation: "RapidTransit.CurrentStation",
  arrival: "RapidTransit.Arrival",
  departure: "RapidTransit.Departure",
  stopped: "RapidTransit.Stopped",
  scheduled: "RapidTransit.Scheduled",
  actual: "RapidTransit.Actual",
  nextPass: "RapidTransit.NextPass",
  waitingForFastTrain: "RapidTransit.WaitingForFastTrain",
  nextStopStation: "RapidTransit.NextStopStation",
  actualArrival: "RapidTransit.ActualArrival",
  assigned: "RapidTransit.Assigned",
  yielding: "RapidTransit.Yielding",
  waitingDispatch: "RapidTransit.WaitingDispatch",
  headingToOrigin: "RapidTransit.HeadingToOrigin",
  returning: "RapidTransit.Returning",
  arriving: "RapidTransit.Arriving",
  boarding: "RapidTransit.Boarding",
  enRoute: "RapidTransit.EnRoute",
  launched: "RapidTransit.Launched",
  disabled: "RapidTransit.Disabled",
  usingNativeFallback: "RapidTransit.UsingNativeFallback",
  vehicleNotTracked: "RapidTransit.VehicleNotTracked",
  alertLineDisabled: "RapidTransit.AlertLineDisabled",
  alertNextSlotGap: "RapidTransit.AlertNextSlotGap",
  alertNoLapCache: "RapidTransit.AlertNoLapCache",
  alertNoDispatchCache: "RapidTransit.AlertNoDispatchCache",
  alertNearingTerminus: "RapidTransit.AlertNearingTerminus",
  alertLaunchCooldown: "RapidTransit.AlertLaunchCooldown",
  alertTargetExpired: "RapidTransit.AlertTargetExpired",
  alertYieldProtected: "RapidTransit.AlertYieldProtected",
  alertYieldingFor: "RapidTransit.AlertYieldingFor",
  alertSpawnPending: "RapidTransit.AlertSpawnPending",
  alertYieldGuard: "RapidTransit.AlertYieldGuard",
  etaHotTitle: "RapidTransit.EtaHotTitle",
  etaHotBuild: "RapidTransit.EtaHotBuild",
  etaHotGeneration: "RapidTransit.EtaHotGeneration",
  etaHotStatus: "RapidTransit.EtaHotStatus",
  etaHotSmokeValue: "RapidTransit.EtaHotSmokeValue",
  etaHotReloadLatest: "RapidTransit.EtaHotReloadLatest",
  etaHotRunSmoke: "RapidTransit.EtaHotRunSmoke",
  etaHotRollback: "RapidTransit.EtaHotRollback",
  etaHotBusy: "RapidTransit.EtaHotBusy",
  etaHotWorkerLost: "RapidTransit.EtaHotWorkerLost",
  etaWorkerLost: "RapidTransit.EtaWorkerLostRestartRequired",
  etaHotNone: "RapidTransit.EtaHotNone",
  etaSnapshotStatus: "RapidTransit.EtaSnapshotStatus",
  etaSnapshotRequest: "RapidTransit.EtaSnapshotRequest",
  etaComparisonTitle: "RapidTransit.EtaComparisonTitle",
  etaComparisonPredicted: "RapidTransit.EtaComparisonPredicted",
  etaComparisonActual: "RapidTransit.EtaComparisonActual",
  etaComparisonRemaining: "RapidTransit.EtaComparisonRemaining",
  etaComparisonPast: "RapidTransit.EtaComparisonPast",
  etaComparisonFinishDelta: "RapidTransit.EtaComparisonFinishDelta",
  etaComparisonPublishDelta: "RapidTransit.EtaComparisonPublishDelta",
  etaComparisonOriginDelta: "RapidTransit.EtaComparisonOriginDelta",
  etaComparisonPredictionDelta: "RapidTransit.EtaComparisonPredictionDelta"
} as const;

const PANEL_FALLBACKS: Record<string, string> = {
  vanillaControl: "原版系统控制",
  arrival: "到达",
  departure: "发出",
  stopped: "已停",
  scheduled: "图定",
  actual: "实际",
  nextPass: "下一通过",
  waitingForFastTrain: "等待快车"
};

function formatFallbackLabel(value: string) {
  const source = value.indexOf(".") >= 0 ? value.slice(value.lastIndexOf(".") + 1) : value;
  return source
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z])([A-Z][a-z])/g, "$1 $2")
    .replace(/[_-]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

export function useT() {
  const localization = typeof l10n.useLocalization === "function" ? l10n.useLocalization() : null;
  const translate = localization && typeof localization.translate === "function" ? localization.translate : null;

  return useCallback((key: string) => {
    const translationKey = (KEYS as Record<string, string>)[key] || key;
    const fallback = PANEL_FALLBACKS[key] || formatFallbackLabel(key);
    if (!translate) {
      return fallback;
    }

    const translated = translate(translationKey, fallback);
    return translated && translated !== translationKey ? translated : fallback;
  }, [translate]);
}
