import { getPrototypeFallbackLines } from "../shared/prototype-fallback-data";

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function getLineName(line, index) {
  return line?.name || line?.displayName || line?.id || `Line ${index + 1}`;
}

function getLineCode(line, index) {
  return line?.code || line?.number || String(index + 1).padStart(2, "0");
}

function getLineShortName(line, index) {
  const name = getLineName(line, index);
  const splitName = name.split(" - ");
  return splitName.length > 1 ? splitName[1] : name;
}

function getLineStations(line) {
  const stations = asArray(line?.stations);
  if (stations.length > 0) {
    return stations;
  }

  return [
    { id: line?.originStationId || `${line?.id || "line"}-origin`, name: line?.originStationName || "起点站" },
    { id: line?.terminalStationId || `${line?.id || "line"}-terminal`, name: line?.terminalStationName || "终点站" }
  ];
}

function deterministicValue(seed, min, max) {
  let hash = 0;
  const text = String(seed);
  for (let index = 0; index < text.length; index += 1) {
    hash = (hash * 31 + text.charCodeAt(index)) % 100000;
  }
  return Math.round(min + (hash / 100000) * (max - min));
}

function buildTrendForLine(line, index) {
  const base = Number(line?.ridershipHour || 1200 + index * 900);
  return Array.from({ length: 24 }).map((_, hour) => {
    const morningPeak = hour >= 7 && hour <= 9 ? 1.85 : 1;
    const eveningPeak = hour >= 17 && hour <= 19 ? 2.05 : 1;
    const offPeak = hour >= 0 && hour <= 5 ? 0.36 : 0.72;
    const shape = Math.max(morningPeak, eveningPeak, offPeak);
    const noise = deterministicValue(`${line?.id || index}-${hour}`, 86, 116) / 100;
    return {
      hour: `${hour}:00`,
      lineId: line?.id || `line-${index + 1}`,
      passengers: Math.round(base * shape * noise)
    };
  });
}

function aggregateTrend(lines) {
  const trendsByLine = lines.map(buildTrendForLine);
  return Array.from({ length: 24 }).map((_, hour) => ({
    hour: `${hour}:00`,
    passengers: trendsByLine.reduce((sum, points) => sum + Number(points[hour]?.passengers || 0), 0)
  }));
}

function buildFallbackPassengerData(lines) {
  const normalizedLines = getPrototypeFallbackLines(lines);
  const lineTrendById = {};
  const stationVolumes = [];
  const sectionVolumes = [];
  const odFlows = [];

  normalizedLines.forEach((line, lineIndex) => {
    const lineId = line?.id || `line-${lineIndex + 1}`;
    const stations = getLineStations(line);
    const ridershipHour = Number(line?.ridershipHour || 1200 + lineIndex * 900);
    lineTrendById[lineId] = buildTrendForLine(line, lineIndex);

    stations.forEach((station, stationIndex) => {
      const base = Math.max(420, ridershipHour / Math.max(2, stations.length));
      stationVolumes.push({
        lineId,
        stationId: station?.id || `${lineId}-station-${stationIndex + 1}`,
        stationName: station?.name || `Station ${stationIndex + 1}`,
        inflow: deterministicValue(`${lineId}-${stationIndex}-in`, base * 0.7, base * 2.1),
        outflow: deterministicValue(`${lineId}-${stationIndex}-out`, base * 0.6, base * 2)
      });
    });

    for (let stationIndex = 0; stationIndex < stations.length - 1; stationIndex += 1) {
      const left = stations[stationIndex];
      const right = stations[stationIndex + 1];
      sectionVolumes.push({
        lineId,
        label: `${left?.name || left?.id}-${right?.name || right?.id}`,
        fromStationId: left?.id || "",
        toStationId: right?.id || "",
        volume: deterministicValue(`${lineId}-${stationIndex}-section`, ridershipHour * 2.8, ridershipHour * 9.6)
      });
    }

    stations.forEach((origin, originIndex) => {
      stations.forEach((destination, destinationIndex) => {
        if (originIndex === destinationIndex || Math.abs(originIndex - destinationIndex) > 3) {
          return;
        }
        const distanceFactor = Math.abs(originIndex - destinationIndex) + 1;
        odFlows.push({
          lineId,
          originStationId: origin?.id || "",
          destinationStationId: destination?.id || "",
          originName: origin?.name || "",
          destinationName: destination?.name || "",
          volume: deterministicValue(`${lineId}-${originIndex}-${destinationIndex}-od`, ridershipHour / distanceFactor, ridershipHour * 3.2 / distanceFactor)
        });
      });
    });
  });

  return {
    lines: normalizedLines,
    lineTrendById,
    systemTrend: aggregateTrend(normalizedLines),
    stationVolumes,
    sectionVolumes: sectionVolumes.sort((left, right) => Number(right.volume || 0) - Number(left.volume || 0)).slice(0, 10),
    odFlows: odFlows.sort((left, right) => Number(right.volume || 0) - Number(left.volume || 0)).slice(0, 16)
  };
}

export function buildPassengerFlowViewModel(snapshot, metadataSnapshot) {
  const source = asArray(snapshot?.lines).length > 0 ? snapshot : metadataSnapshot;
  const fallback = buildFallbackPassengerData(asArray(source?.lines));
  const sourceLines = asArray(source?.lines).length > 0 ? asArray(source?.lines) : fallback.lines;
  const lines = sourceLines.map((line, index) => ({
    id: line?.id || `line-${index + 1}`,
    code: getLineCode(line, index),
    name: getLineName(line, index),
    shortName: getLineShortName(line, index),
    color: line?.color || (line?.kind === "express" ? "#c084fc" : "#5ab4c5")
  }));

  return {
    lines,
    lineTrendById: fallback.lineTrendById,
    systemTrend: asArray(source?.systemTrend).length > 0 ? asArray(source.systemTrend) : fallback.systemTrend,
    stationVolumes: asArray(source?.stationVolumes).length > 0 ? asArray(source.stationVolumes) : fallback.stationVolumes,
    sectionVolumes: asArray(source?.sectionVolumes).length > 0 ? asArray(source.sectionVolumes) : fallback.sectionVolumes,
    odFlows: asArray(source?.odFlows).length > 0 ? asArray(source.odFlows) : fallback.odFlows,
    warnings: asArray(source?.passengerFlowWarnings)
  };
}

export function filterPassengerFlow(viewModel, selectedLineId) {
  if (!selectedLineId || selectedLineId === "ALL") {
    return viewModel;
  }

  return {
    ...viewModel,
    systemTrend: viewModel.lineTrendById?.[selectedLineId] || viewModel.systemTrend.filter((entry) => entry?.lineId === selectedLineId),
    stationVolumes: viewModel.stationVolumes.filter((entry) => entry?.lineId === selectedLineId),
    sectionVolumes: viewModel.sectionVolumes.filter((entry) => entry?.lineId === selectedLineId),
    odFlows: viewModel.odFlows.filter((entry) => entry?.lineId === selectedLineId)
  };
}
