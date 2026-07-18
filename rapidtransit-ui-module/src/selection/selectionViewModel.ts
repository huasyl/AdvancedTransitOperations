export interface DetailRowData {
  label?: string;
  value?: string;
}

export interface DevSightData {
  source?: string;
  summaryText?: string;
}

export interface EtaHotStatusData {
  busy?: boolean;
  currentSource?: string;
  currentBuildId?: string;
  generation?: number;
  lastAction?: string;
  status?: string;
  lastSmokeValue?: number;
  lastError?: string;
  hotBackendWorkerLost?: boolean;
  etaWorkerLost?: boolean;
  workerLost?: boolean;
}

export interface EtaSnapshotStatusData {
  ticket?: string;
  state?: string;
  failure?: string;
  detail?: string;
  predictorSource?: string;
  predictorBuildId?: string;
  predictorGeneration?: number;
  arrival?: number;
  comparisonSummary?: string;
  etaGameMinutes?: number;
  comparisonOriginFrame?: number;
  comparisonState?: string;
  comparisonValid?: boolean;
  comparisonInvalidReason?: string;
  comparisonVehicleId?: string;
  comparisonVehicleIndex?: number;
  comparisonPredictedArrival?: number;
  comparisonActualArrival?: number;
  comparisonFinishDelta?: number;
  comparisonPublishDelta?: number;
  comparisonOriginDelta?: number;
  comparisonPredictionDelta?: number;
  comparisonFramesToOrPastPrediction?: number;
}

export interface PanelData {
  entityId?: string | number;
  mode?: string;
  primaryLabelKey?: string;
  primaryValue?: string;
  primaryValueKind?: string;
  detail1LabelKey?: string;
  detail1Value?: string;
  detail2LabelKey?: string;
  detail2Value?: string;
  detail3LabelKey?: string;
  detail3Value?: string;
  detail4LabelKey?: string;
  detail4Value?: string;
  detail5LabelKey?: string;
  detail5Value?: string;
  detail6LabelKey?: string;
  detail6Value?: string;
  detail7LabelKey?: string;
  detail7Value?: string;
  detail8LabelKey?: string;
  detail8Value?: string;
  alertText?: string;
  showAlerts?: boolean;
  showBypassStationToggle?: boolean;
  bypassStationChecked?: boolean;
  showActions?: boolean;
  showRetireAction?: boolean;
  showForceDepartAction?: boolean;
  showLineSpawnAction?: boolean;
  showDumpTrackModelAction?: boolean;
  showDumpPlannerInputAction?: boolean;
  showDumpObservationAction?: boolean;
  showDumpStationAnchorObservationAction?: boolean;
}

const VEHICLE_DETAIL_KEYS = [
  "control",
  "currentStation",
  "nextStopStation",
  "nextStation",
  "currentSlot",
  "targetSlot",
  "stopDwell"
];

const DETAIL_HIDE_KEYS: Record<string, boolean> = {
  waypoint: true,
  lapCache: true,
  lapCooldown: true,
  time: true,
  "当前时间": true,
  Time: true
};

const ALERT_CODES: Record<string, string> = {
  "line-disabled": "alertLineDisabled",
  "next-slot-gap": "alertNextSlotGap",
  "no-lap-cache": "alertNoLapCache",
  "no-dispatch-cache": "alertNoDispatchCache",
  "bv-misfire": "alertBvMisfire",
  "nearing-terminus": "alertNearingTerminus",
  "launch-cooldown": "alertLaunchCooldown",
  "target-expired": "alertTargetExpired",
  "yield-protected": "alertYieldProtected"
};

export function buildDetailRows(panelData: PanelData | null, mode: string) {
  if (!panelData) {
    return [];
  }

  const rows = [
    { label: panelData.detail1LabelKey, value: panelData.detail1Value },
    { label: panelData.detail2LabelKey, value: panelData.detail2Value },
    { label: panelData.detail3LabelKey, value: panelData.detail3Value },
    { label: panelData.detail4LabelKey, value: panelData.detail4Value },
    { label: panelData.detail5LabelKey, value: panelData.detail5Value },
    { label: panelData.detail6LabelKey, value: panelData.detail6Value },
    { label: panelData.detail7LabelKey, value: panelData.detail7Value },
    { label: panelData.detail8LabelKey, value: panelData.detail8Value }
  ].filter((row) => row.label);

  if (mode === "vehicle") {
    const rowMap: Record<string, DetailRowData> = {};
    for (let i = 0; i < rows.length; i += 1) {
      const row = rows[i];
      if (row && row.label && !rowMap[row.label]) {
        rowMap[row.label] = row;
      }
    }

    const ordered: DetailRowData[] = [];
    const usedLabels: Record<string, boolean> = {};
    for (let i = 0; i < VEHICLE_DETAIL_KEYS.length; i += 1) {
      const key = VEHICLE_DETAIL_KEYS[i];
      const row = rowMap[key];
      if (row && !DETAIL_HIDE_KEYS[key]) {
        ordered.push(row);
        usedLabels[key] = true;
      }
    }

    for (let i = 0; i < rows.length; i += 1) {
      const row = rows[i];
      if (!row || !row.label) {
        continue;
      }
      if (usedLabels[row.label] || DETAIL_HIDE_KEYS[row.label]) {
        continue;
      }
      ordered.push(row);
    }

    return ordered;
  }

  return rows.filter((row) => row.label && !DETAIL_HIDE_KEYS[row.label]);
}

export function formatValue(value: string | undefined, valueKind: string | undefined, t: (key: string) => string) {
  if (!value || value === "-") {
    return "-";
  }

  if (valueKind === "key") {
    return t(value);
  }

  if (valueKind === "state") {
    const stateMap: Record<string, string> = {
      Running: "enRoute",
      Holding: "assigned",
      Yielding: "yielding",
      Preparing: "headingToOrigin",
      Idle: "waitingDispatch",
      Retiring: "returning",
      Disabled: "disabled",
      Returning: "returning",
      Arriving: "arriving",
      Boarding: "boarding",
      EnRoute: "enRoute",
      Launched: "launched",
      Assigned: "assigned"
    };
    return t(stateMap[value] || value);
  }

  if (value === "yes") {
    return t("yes");
  }

  if (value === "no") {
    return t("no");
  }

  return value;
}

export function formatAlertText(text: string | undefined, t: (key: string) => string) {
  if (!text || text === "None") {
    return t("none");
  }

  if (text === "using-native-fallback") {
    return t("usingNativeFallback");
  }

  if (text === "vehicle-not-tracked") {
    return t("vehicleNotTracked");
  }

  if (text === "official-dispatch") {
    return t("officialDispatch");
  }

  return text.split(", ").map((part) => {
    if (part.startsWith("yielding-for:")) {
      return t("alertYieldingFor") + ": #" + part.split(":")[1];
    }
    if (part.startsWith("spawn-pending:")) {
      return t("alertSpawnPending") + ": " + part.split(":")[1];
    }
    if (part.startsWith("yield-guard:")) {
      return t("alertYieldGuard") + ": " + part.split(":")[1];
    }
    return ALERT_CODES[part] ? t(ALERT_CODES[part]) : part;
  }).join(" / ");
}
