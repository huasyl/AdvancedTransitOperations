import { getPrototypeFallbackLines } from "../shared/prototype-fallback-data";

const MODE_LABELS = {
  Train: "城际铁路",
  Subway: "城市地铁",
  Tram: "现代电车",
  Bus: "常规公交",
  Unknown: "其他交通"
};

const MODE_ORDER = ["Subway", "Train"];
const OVERVIEW_VISIBLE_MODES = new Set(MODE_ORDER);

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function normalizeMode(value) {
  const text = String(value || "").toLowerCase();
  if (text.includes("subway") || text.includes("metro")) {
    return "Subway";
  }
  if (text.includes("train") || text.includes("rail")) {
    return "Train";
  }
  if (text.includes("tram")) {
    return "Tram";
  }
  if (text.includes("bus")) {
    return "Bus";
  }
  return "Unknown";
}

function clamp01(value) {
  const numberValue = Number(value);
  if (!Number.isFinite(numberValue)) {
    return 0;
  }
  return Math.max(0, Math.min(1, numberValue));
}

function getLineName(line, index) {
  return line?.name || line?.displayName || line?.id || `Line ${index + 1}`;
}

function getLineCode(line, index) {
  return line?.code || line?.number || String(index + 1).padStart(2, "0");
}

function fallbackStationPosition(index, total) {
  const count = Math.max(1, total);
  const angle = count === 1 ? -Math.PI / 2 : (-Math.PI / 2) + (Math.PI * 1.6 * index) / Math.max(1, count - 1);
  const radius = 260;
  return {
    x: Math.round(400 + Math.cos(angle) * radius),
    y: Math.round(400 + Math.sin(angle) * radius)
  };
}

function buildStationsForLine(line, lineIndex, allStations) {
  const inlineStations = asArray(line?.stations);
  const sourceStations = (inlineStations.length > 0 ? inlineStations : asArray(allStations)
      .filter((station) => !station?.lineId || station.lineId === line.id || station.lineId === line.sourceLineId))
    .sort((left, right) => Number(left?.order ?? left?.index ?? 0) - Number(right?.order ?? right?.index ?? 0));

  if (sourceStations.length > 0) {
    return sourceStations.map((station, stationIndex) => {
      const fallback = fallbackStationPosition(stationIndex, sourceStations.length);
      return {
        id: station?.id || station?.stationId || `${line.id}-station-${stationIndex + 1}`,
        name: station?.name || station?.stationName || station?.id || `Station ${stationIndex + 1}`,
        x: Number.isFinite(Number(station?.x)) ? Number(station.x) : fallback.x,
        y: Number.isFinite(Number(station?.y)) ? Number(station.y) : fallback.y,
        transferLineCodes: asArray(station?.transferLineCodes),
        hasBypass: station?.hasBypass === true
      };
    });
  }

  const originId = line?.originStationId || line?.originId || `${line.id}-origin`;
  const terminalId = line?.terminalStationId || `${line.id}-terminal`;
  return [originId, terminalId].map((stationId, stationIndex) => {
    const fallback = fallbackStationPosition(stationIndex + lineIndex, 2 + lineIndex);
    return {
      id: stationId,
      name: stationIndex === 0 ? (line?.originStationName || stationId) : terminalId,
      x: fallback.x,
      y: fallback.y,
      transferLineCodes: [],
      hasBypass: false
    };
  });
}

export function buildOverviewViewModel(snapshot, metadataSnapshot) {
  const source = asArray(snapshot?.lines).length > 0 ? snapshot : metadataSnapshot;
  const lines = getPrototypeFallbackLines(asArray(source?.lines));
  const stations = asArray(source?.stations);
  const trips = asArray(snapshot?.trips);
  const appliedRows = asArray(snapshot?.appliedRows);

  const networkLines = lines.map((line, index) => {
    const mode = normalizeMode(line?.transportType);
    const lineStations = buildStationsForLine(line, index, stations);
    return {
      id: line?.id || `line-${index + 1}`,
      mode,
      code: getLineCode(line, index),
      name: getLineName(line, index),
      color: line?.color || (line?.kind === "express" ? "#c084fc" : "#5ab4c5"),
      stationIds: lineStations.map((station) => station.id),
      stations: lineStations,
      ridershipHour: Number(line?.ridershipHour || 0),
      scheduledVehicleCount: asArray(line?.vehicleIds).length || asArray(line?.vehicles).length || Number(line?.totalVehicles || 0) || trips.filter((trip) => trip?.lineId === line?.id).length || appliedRows.filter((row) => row?.lineId === line?.id).length
    };
  });

  const modes = MODE_ORDER
    .map((mode) => {
      const modeLines = networkLines.filter((line) => line.mode === mode);
      if (!OVERVIEW_VISIBLE_MODES.has(mode)) {
        return null;
      }
      const scheduledVehicleCount = modeLines.reduce((sum, line) => sum + line.scheduledVehicleCount, 0);
      const estimatedPassengerLoad = modeLines.reduce((sum, line) => sum + Number(line.ridershipHour || 0), 0);
      return {
        mode,
        label: MODE_LABELS[mode],
        lineCount: modeLines.length,
        activeVehicleCount: Math.max(0, Math.round(scheduledVehicleCount * 0.86)),
        scheduledVehicleCount,
        estimatedPassengerLoad,
        healthPercent: modeLines.length > 0 ? 100 : 0
      };
    })
    .filter(Boolean);

  const activeMode = modes.find((mode) => mode.lineCount > 0)?.mode || modes[0]?.mode || "Unknown";
  const networkStationsById = new Map();
  networkLines.forEach((line) => {
    line.stations.forEach((station) => {
      if (!networkStationsById.has(station.id)) {
        networkStationsById.set(station.id, { ...station, transferLineCodes: [line.code] });
      } else {
        const current = networkStationsById.get(station.id);
        networkStationsById.set(station.id, {
          ...current,
          transferLineCodes: [...new Set([...current.transferLineCodes, line.code])]
        });
      }
    });
  });
  const fallbackVehicles = [];
  networkLines.forEach((line) => {
    const vehicleCount = Math.min(3, Math.max(0, line.stations.length - 1));
    for (let index = 0; index < vehicleCount; index += 1) {
      const station = line.stations[index];
      fallbackVehicles.push({
        id: `${line.id}-vehicle-${index + 1}`,
        lineId: line.id,
        currentStationId: station.id,
        nextStationId: line.stations[index + 1]?.id || station.id,
        progress: (index + 1) / 4,
        passengers: Math.round((line.ridershipHour || 1200) / 24),
        capacity: Math.max(80, Math.round((line.ridershipHour || 1200) / 10)),
        speedKmh: 35 + index * 8
      });
    }
  });

  return {
    generatedAtGameMinute: Number(snapshot?.generatedAtGameMinute || metadataSnapshot?.generatedAtGameMinute || 0),
    modes,
    activeMode,
    network: {
      lines: networkLines,
      stations: [...networkStationsById.values()],
      vehicles: (asArray(source?.vehicles).length > 0 ? asArray(source?.vehicles) : fallbackVehicles).map((vehicle, index) => ({
        id: vehicle?.id || `vehicle-${index + 1}`,
        lineId: vehicle?.lineId || "",
        currentStationId: vehicle?.currentStationId || "",
        nextStationId: vehicle?.nextStationId || "",
        progress: clamp01(vehicle?.progress),
        passengers: Number(vehicle?.passengers || 0),
        capacity: Number(vehicle?.capacity || 0),
        speedKmh: Number(vehicle?.speedKmh || 0)
      }))
    },
    systems: [
      { key: "dispatchEnabled", title: "发车控制", enabled: snapshot?.featureSettings?.dispatchEnabled !== false },
      { key: "bypassEnabled", title: "智能待避", enabled: snapshot?.featureSettings?.bypassEnabled !== false },
      { key: "broadcastEnabled", title: "自动广播", enabled: snapshot?.featureSettings?.broadcastEnabled !== false },
      { key: "depotLockEnabled", title: "车库锁定", enabled: snapshot?.featureSettings?.depotLockEnabled !== false }
    ],
    warnings: []
  };
}
