import * as l10n from "cs2/l10n";
import { useCallback } from "react";

const KEYS = {
  panelTitle: "RapidTransit.PanelTitle",
  vehicleTitle: "RapidTransit.VehicleTitle",
  lineTitle: "RapidTransit.LineTitle",
  emptyHint: "RapidTransit.EmptyHint",
  overview: "RapidTransit.Overview",
  runtime: "RapidTransit.Runtime",
  alerts: "RapidTransit.Alerts",
  actions: "RapidTransit.Actions",
  close: "RapidTransit.Close",
  dispatch: "RapidTransit.Dispatch",
  retire: "RapidTransit.Retire",
  departNow: "RapidTransit.DepartNow",
  reevaluate: "RapidTransit.Reevaluate",
  spawnOne: "RapidTransit.SpawnOne",
  dumpTrackModel: "RapidTransit.DumpTrackModel",
  dumpPlannerInput: "RapidTransit.DumpPlannerInput",
  dumpObservation: "RapidTransit.DumpObservation",
  dumpStationAnchorObservation: "RapidTransit.DumpStationAnchorObservation",
  topButtonTitle: "RapidTransit.TopButtonTitle",
  topButtonDescription: "RapidTransit.TopButtonDescription",
  none: "RapidTransit.None",
  officialDispatch: "RapidTransit.OfficialDispatch",
  vehiclePrefix: "RapidTransit.VehiclePrefix",
  linePrefix: "RapidTransit.LinePrefix",
  state: "RapidTransit.State",
  line: "RapidTransit.Line",
  lineName: "RapidTransit.LineName",
  nextSlot: "RapidTransit.NextSlot",
  time: "RapidTransit.Time",
  fleet: "RapidTransit.Fleet",
  states: "RapidTransit.States",
  lapCache: "RapidTransit.LapCache",
  dispatchCache: "RapidTransit.DispatchCache",
  routeDuration: "RapidTransit.RouteDuration",
  spawnTarget: "RapidTransit.SpawnTarget",
  stationBindings: "RapidTransit.StationBindings",
  bypassStation: "RapidTransit.BypassStation",
  managed: "RapidTransit.Managed",
  progress: "RapidTransit.Progress",
  currentSlot: "RapidTransit.CurrentSlot",
  targetSlot: "RapidTransit.TargetSlot",
  currentStation: "RapidTransit.CurrentStation",
  nextStation: "RapidTransit.NextStation",
  nextStopStation: "RapidTransit.NextStopStation",
  event: "RapidTransit.Event",
  nextSliceCut: "RapidTransit.NextSliceCut",
  stopDwell: "RapidTransit.StopDwell",
  inboundTime: "RapidTransit.InboundTime",
  waypoint: "RapidTransit.Waypoint",
  lapCooldown: "RapidTransit.LapCooldown",
  yes: "RapidTransit.Yes",
  no: "RapidTransit.No",
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
  alertBvMisfire: "RapidTransit.AlertBvMisfire",
  alertNearingTerminus: "RapidTransit.AlertNearingTerminus",
  alertLaunchCooldown: "RapidTransit.AlertLaunchCooldown",
  alertTargetExpired: "RapidTransit.AlertTargetExpired",
  alertYieldProtected: "RapidTransit.AlertYieldProtected",
  alertYieldingFor: "RapidTransit.AlertYieldingFor",
  alertSpawnPending: "RapidTransit.AlertSpawnPending",
  alertYieldGuard: "RapidTransit.AlertYieldGuard"
} as const;

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
    const fallback = formatFallbackLabel(key);
    if (!translate) {
      return fallback;
    }

    const translated = translate(translationKey, fallback);
    return translated && translated !== translationKey ? translated : fallback;
  }, [translate]);
}
