const MIN_LINE_SETTING_MINUTES = 5;

export { MIN_LINE_SETTING_MINUTES };

export const DEFAULT_RUNTIME_FEATURE_SETTINGS = {
  dispatchEnabled: true,
  bypassEnabled: true,
  broadcastEnabled: true,
  depotLockEnabled: true
};

export const DEFAULT_DEPOT_OPTIONS = [
  { id: "any-depot", labelKey: "nativeSchedule.data.depot.any" },
  { id: "north-depot", labelKey: "nativeSchedule.data.depot.north" }
];

export const DEFAULT_ORIGIN_OPTIONS = [
  { id: "origin-industrial", labelKey: "nativeSchedule.data.origin.industrial" }
];

export const DEFAULT_LINE_OPTIONS = [
  {
    id: "line-local",
    corridorId: "industrial-corridor",
    sourceLineId: "line-local",
    nameKey: "nativeSchedule.data.line.local",
    kind: "local",
    transportType: "",
    color: "#5ab4c5",
    depotId: "any-depot",
    originId: "origin-industrial",
    originStationId: "origin-industrial",
    hold: "15",
    dwell: "6"
  },
  {
    id: "line-express",
    corridorId: "industrial-corridor",
    sourceLineId: "line-express",
    nameKey: "nativeSchedule.data.line.express",
    kind: "express",
    transportType: "",
    color: "#c084fc",
    depotId: "north-depot",
    originId: "origin-industrial",
    originStationId: "origin-industrial",
    hold: "10",
    dwell: "4"
  }
];

export const DEPOT_OPTIONS = DEFAULT_DEPOT_OPTIONS.map((option) => ({ ...option }));
export const ORIGIN_OPTIONS = DEFAULT_ORIGIN_OPTIONS.map((option) => ({ ...option }));
export const LINE_OPTIONS = DEFAULT_LINE_OPTIONS.map((option) => ({ ...option }));

export function cloneOptions(options) {
  return (Array.isArray(options) ? options : []).map((option) => ({ ...option }));
}

export function replaceRuntimeOptions(target, nextOptions) {
  target.splice(0, target.length, ...cloneOptions(nextOptions));
}

export function replaceRuntimeCatalog({ lines, depots, origins }) {
  replaceRuntimeOptions(LINE_OPTIONS, lines);
  replaceRuntimeOptions(DEPOT_OPTIONS, depots);
  replaceRuntimeOptions(ORIGIN_OPTIONS, origins);
}

export function clampPositiveMinutes(value, fallbackValue) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric <= 0) {
    return fallbackValue;
  }

  return Math.max(MIN_LINE_SETTING_MINUTES, Math.min(120, Math.round(numeric)));
}

export function buildNativeDepotOptions(snapshotDepots = [], { allowDefaultFallback = true } = {}) {
  if (!Array.isArray(snapshotDepots) || snapshotDepots.length === 0) {
    return allowDefaultFallback ? cloneOptions(DEFAULT_DEPOT_OPTIONS) : [];
  }

  return snapshotDepots.map((depot, index) => ({
    id: depot?.id || `depot-${index + 1}`,
    label: depot?.name || depot?.id || `Depot ${index + 1}`,
    transportType: depot?.transportType || ""
  }));
}

export function buildNativeLineOptions(snapshotLines = [], t, { allowDefaultFallback = true } = {}) {
  if (!Array.isArray(snapshotLines) || snapshotLines.length === 0) {
    return allowDefaultFallback ? cloneOptions(DEFAULT_LINE_OPTIONS) : [];
  }

  return snapshotLines.map((line, index) => {
    const fallbackName = "--";
    const dispatchSupported = line?.dispatchSupported !== false;
    const originFallback = dispatchSupported ? `origin-${index + 1}` : "";
    const originNameFallback = dispatchSupported ? `Origin ${index + 1}` : "";

    return {
      id: line?.id || `line-${index + 1}`,
      corridorId: line?.sourceLineId || line?.id || `corridor-${index + 1}`,
      sourceLineId: line?.sourceLineId || line?.id || `source-line-${index + 1}`,
      name: line?.name || fallbackName,
      nameKey: "",
      kind: line?.kind === "express" ? "express" : "local",
      transportType: line?.transportType || "",
      color: line?.color || (line?.kind === "express" ? "#c084fc" : "#5ab4c5"),
      depotId: line?.allowedDepotId || "",
      originId: line?.originStationId || originFallback,
      originStationId: line?.originStationId || originFallback,
      originStationName: line?.originStationName || line?.originStationId || originNameFallback,
      hold: String(clampPositiveMinutes(line?.originHoldLimitMinutes, 20)),
      dwell: String(clampPositiveMinutes(line?.maxStationDwellMinutes, 10)),
      dispatchSupported,
      unsupportedReason: line?.unsupportedReason || "",
      originStatus: line?.originStatus || "",
      originMessageKey: line?.originMessageKey || ""
    };
  });
}

export function buildNativeOriginOptions(lineOptions = [], { allowDefaultFallback = true } = {}) {
  const seen = new Set();
  const origins = [];

  lineOptions.forEach((line, index) => {
    const dispatchSupported = line?.dispatchSupported !== false;
    const originId = line?.originId || line?.originStationId || (dispatchSupported ? `origin-${index + 1}` : "");
    if (!originId || seen.has(originId)) {
      return;
    }

    seen.add(originId);
    origins.push({
      id: originId,
      label: line?.originStationName || line?.originName || line?.originId || originId,
      dispatchSupported: line?.dispatchSupported !== false,
      originStatus: line?.originStatus || "",
      originMessageKey: line?.originMessageKey || ""
    });
  });

  return origins.length > 0 ? origins : (allowDefaultFallback ? cloneOptions(DEFAULT_ORIGIN_OPTIONS) : []);
}

export function overlayPersistedLineSettings(lineOptions = [], persistedLineSettings = []) {
  const settingsById = new Map(
    (Array.isArray(persistedLineSettings) ? persistedLineSettings : [])
      .filter((entry) => entry?.id)
      .map((entry) => [entry.id, entry])
  );

  return lineOptions.map((line) => {
    const persisted = settingsById.get(line.id);
    if (!persisted) {
      return line;
    }

    return {
      ...line,
      depotId: persisted.depotId || line.depotId,
      hold: String(clampPositiveMinutes(persisted.hold, Number(line.hold) || 20)),
      dwell: String(clampPositiveMinutes(persisted.dwell, Number(line.dwell) || 10))
    };
  });
}

export function buildRuntimeCatalog(snapshot, metadataSnapshot, persistedState, t) {
  const sourceSnapshot =
    Array.isArray(snapshot?.lines) && snapshot.lines.length > 0
      ? snapshot
      : metadataSnapshot;

  const lineOptions = overlayPersistedLineSettings(
    buildNativeLineOptions(sourceSnapshot?.lines, t),
    persistedState?.lineSettings
  );
  const depotOptions = buildNativeDepotOptions(sourceSnapshot?.depots);
  const originOptions = buildNativeOriginOptions(lineOptions);

  return {
    lineOptions,
    depotOptions,
    originOptions
  };
}

export function buildCatalog(metadataSnapshot, t, { allowDefaultFallback = true } = {}) {
  const lineOptions = buildNativeLineOptions(
    metadataSnapshot?.lines,
    t,
    { allowDefaultFallback }
  );
  const depotOptions = buildNativeDepotOptions(
    metadataSnapshot?.depots,
    { allowDefaultFallback }
  );
  const originOptions = buildNativeOriginOptions(
    lineOptions,
    { allowDefaultFallback }
  );

  return {
    lineOptions,
    depotOptions,
    originOptions
  };
}
export function normalizeRuntimeFeatureSettings(featureSettings) {
  if (!featureSettings || typeof featureSettings !== "object") {
    return { ...DEFAULT_RUNTIME_FEATURE_SETTINGS };
  }

  return {
    dispatchEnabled: featureSettings.dispatchEnabled !== false,
    bypassEnabled: featureSettings.bypassEnabled !== false,
    broadcastEnabled: featureSettings.broadcastEnabled !== false,
    depotLockEnabled: featureSettings.depotLockEnabled !== false
  };
}
export function normalizeKind(value) {
  if (value === "express") {
    return "express";
  }

  return "local";
}

export function getLocalizedLineType(kind, t, variant = "regular") {
  const normalizedKind = normalizeKind(kind);
  if (variant === "compact") {
    return t(`nativeSchedule.type.${normalizedKind}.compact`);
  }

  return t(`nativeSchedule.type.${normalizedKind}`);
}

export function getDepotOptionById(depotId) {
  return DEPOT_OPTIONS.find((depot) => depot.id === depotId) ?? null;
}

export function getOriginOptionById(originId) {
  return ORIGIN_OPTIONS.find((origin) => origin.id === originId) ?? ORIGIN_OPTIONS[0];
}

export function getLocalizedDepotLabel(depotId, t) {
  if (!depotId) {
    return t("nativeSchedule.data.depot.any");
  }

  const depot = getDepotOptionById(depotId);
  if (!depot) {
    return t("nativeSchedule.data.depot.any");
  }

  return depot.label || t(depot.labelKey);
}

export function getLocalizedOriginLabel(originId, t) {
  const origin = getOriginOptionById(originId);
  if (!origin) {
    return "";
  }

  return origin.label || t(origin.labelKey);
}

export function getLocalizedLineName(line, t) {
  if (line?.name) {
    return line.name;
  }

  return line?.nameKey ? t(line.nameKey) : "";
}

export function directionFromOffsetMode(offsetMode) {
  return offsetMode === "before" ? "early" : "late";
}

export function offsetModeFromDirection(direction) {
  return direction === "early" ? "before" : "after";
}

export function findLineOptionById(lineId) {
  return LINE_OPTIONS.find((line) => line.id === lineId) ?? null;
}

export function getLineOptionById(lineId) {
  return findLineOptionById(lineId) ?? LINE_OPTIONS[0];
}

export function getLineOptionByKind(kind, corridorId = "", fallbackToAny = true) {
  const normalizedKind = normalizeKind(kind);
  const exactMatch = LINE_OPTIONS.find((line) => line.kind === normalizedKind && (!corridorId || line.corridorId === corridorId));
  if (exactMatch) {
    return exactMatch;
  }

  if (!fallbackToAny) {
    return null;
  }

  return LINE_OPTIONS.find((line) => line.kind === normalizedKind) ?? LINE_OPTIONS[0];
}

export function getReferenceLineIdsForLine(lineOption, kind) {
  if (normalizeKind(kind) !== "express") {
    return [lineOption.id];
  }

  return LINE_OPTIONS
    .filter((line) => line.corridorId === lineOption.corridorId && line.kind === "local")
    .map((line) => line.id);
}
export function buildPlanLineOptions(lines = LINE_OPTIONS) {
  return lines
    .filter((line) => line && typeof line === "object")
    .map((line) => ({
      id: line.id || "",
      originStationId: line.originStationId || "",
      originStationName: line.originStationName || line.originId || "",
      dispatchSupported: line.dispatchSupported !== false
    }));
}

export function patchRuntimeLineOption(lineId, updates) {
  const nextIndex = LINE_OPTIONS.findIndex((line) => line?.id === lineId);
  if (nextIndex < 0) {
    return false;
  }

  LINE_OPTIONS[nextIndex] = {
    ...LINE_OPTIONS[nextIndex],
    ...updates
  };
  return true;
}

export function serializePersistedLineSettings(lines = LINE_OPTIONS) {
  return (Array.isArray(lines) ? lines : [])
    .filter((line) => line && typeof line === "object")
    .map((line) => ({
      id: line.id || "",
      depotId: line.depotId || "",
      hold: line.hold || "",
      dwell: line.dwell || ""
    }));
}
