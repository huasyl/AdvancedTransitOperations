import { useEffect, useRef, useState } from "react";
import { timeToMinutes } from "../lib/time";
import { SafeControlText } from "./ChoiceButtons";
import { useI18n } from "../lib/i18n";

const SVG_UNITS = 100;
const TARGET_LINE_PX = 3;
const TARGET_SELECTED_LINE_PX = 3.7;
const TARGET_NODE_STROKE_PX = 1.5;
const TARGET_NODE_RADIUS_PX = 6.5;
const TARGET_TIME_LABEL_GAP_PX = 120;
const MAX_SAME_STATION_DWELL_MINUTES = 90;

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function getDiagramMetrics(width = 1678, height = 602, lineWidthScale = 1) {
  const safeHeight = Number.isFinite(height) && height > 0 ? height : 602;
  const uniformScale = safeHeight / SVG_UNITS;
  const nodeStrokeWidth = clamp(TARGET_NODE_STROKE_PX / uniformScale, 0.18, 0.42);
  const realtimeRadiusPx = TARGET_NODE_RADIUS_PX * 0.72;
  const widthScale =
    Number.isFinite(Number(lineWidthScale)) && Number(lineWidthScale) > 0 ? Number(lineWidthScale) : 1;
  return {
    lineWidth: clamp((TARGET_LINE_PX / uniformScale) * widthScale, 0.28, 1.45),
    selectedLineWidth: clamp((TARGET_SELECTED_LINE_PX / uniformScale) * widthScale, 0.34, 1.72),
    nodeStrokeWidth,
    nodeRx: clamp(TARGET_NODE_RADIUS_PX / uniformScale, 0.5, 1.3),
    nodeRy: clamp(TARGET_NODE_RADIUS_PX / uniformScale, 0.5, 1.3),
    realtimeRx: clamp(realtimeRadiusPx / uniformScale, 0.32, 0.9),
    realtimeRy: clamp(realtimeRadiusPx / uniformScale, 0.32, 0.9),
    realtimeLabelFontSize: clamp(10 / uniformScale, 0.9, 1.7),
    realtimeLabelOffsetX: clamp(8 / uniformScale, 0.8, 1.6),
    realtimeLabelOffsetY: clamp(10 / uniformScale, 1.0, 1.9)
  };
}

function getTimeLabelStepMinutes(width, startMinutes, endMinutes) {
  const safeWidth = Number.isFinite(width) && width > 0 ? width : 1678;
  const rangeMinutes = endMinutes - startMinutes;
  if (!Number.isFinite(rangeMinutes) || rangeMinutes <= 0) {
    return 10;
  }

  const pixelsPerMinute = safeWidth / rangeMinutes;
  const preferredSteps = [10, 15, 20, 30, 40, 50, 60];
  return preferredSteps.find((step) => step * pixelsPerMinute >= TARGET_TIME_LABEL_GAP_PX) ?? 60;
}

function buildTimeLabelTicks(startMinutes, endMinutes, stepMinutes) {
  const ticks = [];
  for (let tick = startMinutes; tick <= endMinutes; tick += 5) {
    if ((tick - startMinutes) % stepMinutes === 0) {
      ticks.push(tick);
    }
  }

  if (ticks.length === 0 || ticks[0] !== startMinutes) {
    ticks.unshift(startMinutes);
  }

  if (ticks[ticks.length - 1] !== endMinutes) {
    ticks.push(endMinutes);
  }

  return [...new Set(ticks)].sort((a, b) => a - b);
}

function getStopArrivalMinutes(stop) {
  return timeToMinutes(stop.arrivalTime ?? stop.time ?? "");
}

function getStopDepartureMinutes(stop) {
  return timeToMinutes(stop.departureTime ?? stop.arrivalTime ?? stop.time ?? "");
}

function getStationDistanceRange(stations) {
  const distances = stations
    .map((station) => station.distance)
    .filter((distance) => Number.isFinite(distance));

  if (distances.length === 0) {
    return { min: 0, max: 1 };
  }

  const min = Math.min(...distances);
  const max = Math.max(...distances);
  return { min, max: max > min ? max : min + 1 };
}

const PLOT_TOP_PERCENT = 4;
const PLOT_BOTTOM_PERCENT = 96;

function getY(plotOrder, maxPlotOrder) {
  const range = Number.isFinite(maxPlotOrder) && maxPlotOrder > 0 ? maxPlotOrder : 1;
  if (!Number.isFinite(plotOrder)) {
    return PLOT_TOP_PERCENT;
  }

  const normalized = plotOrder / range;
  return PLOT_TOP_PERCENT + normalized * (PLOT_BOTTOM_PERCENT - PLOT_TOP_PERCENT);
}

function getViewBoxWidth(width, height) {
  const safeWidth = Number.isFinite(width) && width > 0 ? width : 1678;
  const safeHeight = Number.isFinite(height) && height > 0 ? height : 602;
  return (safeWidth / safeHeight) * SVG_UNITS;
}

function getX(minutes, startMinutes, endMinutes, viewBoxWidth = 100) {
  const range = endMinutes - startMinutes;
  if (!Number.isFinite(minutes) || range <= 0) {
    return null;
  }

  return ((minutes - startMinutes) / range) * viewBoxWidth;
}

function formatMinutes(minutes) {
  if (!Number.isFinite(minutes)) {
    return "--:--";
  }

  const normalized = ((Math.round(minutes) % 1440) + 1440) % 1440;
  const hours = String(Math.floor(normalized / 60)).padStart(2, "0");
  const mins = String(normalized % 60).padStart(2, "0");
  return `${hours}:${mins}`;
}

function formatStopTooltip(stop, station, t) {
  const stationName = station.rawName ?? station.name;
  const arrival = stop.arrivalTime ?? stop.time ?? "--:--";
  const departure = stop.departureTime ?? stop.arrivalTime ?? stop.time ?? "--:--";
  const parts = [stationName];

  if (arrival !== departure) {
    parts.push(`${t("diagram.tooltip.arr")} ${arrival}`);
    parts.push(`${t("diagram.tooltip.dep")} ${departure}`);
  } else {
    parts.push(`${t("diagram.tooltip.time")} ${departure}`);
  }

  if (stop.waitMinutes) {
    parts.push(`${t("diagram.tooltip.wait")} ${stop.waitMinutes} ${t("diagram.tooltip.minutes")}`);
  }

  return parts.join(" | ");
}

function splitStationLabel(label) {
  const normalized = String(label || "").trim();
  if (!normalized) {
    return [""];
  }

  if (normalized.length <= 22) {
    return [normalized];
  }

  if (!/\s/.test(normalized)) {
    const compactLines = [];
    const chunkSize = normalized.length > 18 ? 10 : 12;
    for (let index = 0; index < normalized.length && compactLines.length < 2; index += chunkSize) {
      compactLines.push(normalized.slice(index, index + chunkSize));
    }
    return compactLines;
  }

  const words = normalized.split(/\s+/).filter(Boolean);
  if (words.length < 2) {
    return [normalized];
  }

  const lines = [];
  let currentLine = "";
  for (let index = 0; index < words.length; index += 1) {
    const word = words[index];
    const nextLine = currentLine ? `${currentLine} ${word}` : word;
    if (nextLine.length <= 22 || currentLine.length === 0) {
      currentLine = nextLine;
      continue;
    }

    lines.push(currentLine);
    currentLine = word;
    if (lines.length === 1) {
      continue;
    }

    return [lines[0], `${currentLine} ${words.slice(index + 1).join(" ")}`.trim()];
  }

  if (currentLine) {
    lines.push(currentLine);
  }

  return lines.slice(0, 2);
}

function buildTripTooltip(trip, stationById, t) {
  const tripStops = Array.isArray(trip.stops) ? trip.stops : [];
  const stopLines = tripStops.map((stop) => {
    const station = stationById.get(stop.stationId);
    const stationName = station?.rawName ?? station?.name ?? stop.stationId;
    const arrival = stop.arrivalTime ?? stop.time ?? "--:--";
    const departure = stop.departureTime ?? stop.arrivalTime ?? stop.time ?? "--:--";

    if (arrival !== departure) {
      return `${stationName}: ${t("diagram.tooltip.arr")} ${arrival} / ${t("diagram.tooltip.dep")} ${departure}`;
    }

    return `${stationName}: ${t("diagram.tooltip.time")} ${departure}`;
  });

  return [
    `${trip.id} | ${trip.kind === "local" ? t("diagram.trip.local") : t("diagram.trip.express")}`,
    ...stopLines
  ].join("\n");
}

function buildDisplayStations(stations, mergedView) {
  const baseStations = Array.isArray(stations) ? stations : [];
  if (baseStations.length === 0) {
    return [];
  }

  if (mergedView?.isLoop) {
    const displayStations = baseStations.map((station, index) => ({
      ...station,
      plotId: station.id,
      plotOrder: index,
      sourceStationId: station.id
    }));
    const firstStation = baseStations[0];
    displayStations.push({
      ...firstStation,
      plotId: `${firstStation.id}__loop_end`,
      plotOrder: baseStations.length,
      sourceStationId: firstStation.id,
      isLoopDuplicate: true
    });
    return displayStations;
  }

  const turnbackIndex = baseStations.findIndex((station) => station.id === mergedView?.turnbackStationId);
  if (turnbackIndex >= 0) {
    let ordered;
    if (mergedView?.direction === "down") {
      ordered = [...baseStations.slice(turnbackIndex)];
      if (ordered[ordered.length - 1]?.id !== baseStations[0]?.id) {
        ordered.push(baseStations[0]);
      }
    } else {
      ordered = baseStations.slice(0, turnbackIndex + 1);
    }
    return ordered.map((station, index) => ({
      ...station,
      plotId: station.id,
      plotOrder: index,
      sourceStationId: station.id
    }));
  }

  return baseStations.map((station, index) => ({
    ...station,
    plotId: station.id,
    plotOrder: index,
    sourceStationId: station.id
  }));
}

function transformTripsForDisplay(trip, stations, displayStations, mergedView) {
  const tripStops = Array.isArray(trip.stops) ? trip.stops : [];
  const stationOrderById = new Map(stations.map((station, index) => [station.id, index]));
  const displayStationsBySourceId = new Map();
  displayStations.forEach((station) => {
    const key = station.sourceStationId ?? station.id;
    if (!displayStationsBySourceId.has(key)) {
      displayStationsBySourceId.set(key, []);
    }
    displayStationsBySourceId.get(key).push(station);
  });

  const originId = stations[0]?.id ?? "";
  const turnbackId = mergedView?.turnbackStationId ?? "";
  const turnbackIndex = stations.findIndex((station) => station.id === turnbackId);

  if (mergedView?.isLoop) {
    const mappedStops = [];
    let previousMinutes = null;
    let wrapped = false;
    let previousOrder = null;

    for (const stop of tripStops) {
      const order = stationOrderById.get(stop.stationId);
      if (!Number.isFinite(order)) {
        continue;
      }

      const stopMinutes = getStopDepartureMinutes(stop) ?? getStopArrivalMinutes(stop);
      if (previousMinutes !== null && stopMinutes !== null && stopMinutes + 5 < previousMinutes) {
        break;
      }

      if (previousOrder !== null && order < previousOrder) {
        wrapped = true;
      }

      if (wrapped && stop.stationId !== originId) {
        break;
      }

      const displayVariants = displayStationsBySourceId.get(stop.stationId) ?? [];
      const displayStation =
        wrapped && stop.stationId === originId && displayVariants.length > 1
          ? displayVariants[displayVariants.length - 1]
          : displayVariants[0];
      if (!displayStation) {
        continue;
      }

      mappedStops.push({ ...stop, stationId: displayStation.plotId });
      previousOrder = order;
      previousMinutes = stopMinutes;
    }

    let realtimeFromStationId = trip.realtimeFromStationId;
    let realtimeToStationId = trip.realtimeToStationId;
    if (trip.realtimeFromStationId && trip.realtimeToStationId) {
      const fromOrder = stationOrderById.get(trip.realtimeFromStationId);
      const toOrder = stationOrderById.get(trip.realtimeToStationId);
      if (Number.isFinite(fromOrder) && Number.isFinite(toOrder) && toOrder < fromOrder) {
        if (trip.realtimeToStationId === originId) {
          realtimeToStationId = `${originId}__loop_end`;
        } else {
          realtimeFromStationId = null;
          realtimeToStationId = null;
        }
      }
    }

    return [{
      ...trip,
      stops: mappedStops,
      realtimeFromStationId,
      realtimeToStationId
    }];
  }

  if (turnbackIndex >= 0) {
    const normalizedStops = [];
    let previousMinutes = null;
    for (const stop of tripStops) {
      const order = stationOrderById.get(stop.stationId);
      if (!Number.isFinite(order)) {
        continue;
      }

      const stopMinutes = getStopDepartureMinutes(stop) ?? getStopArrivalMinutes(stop);
      if (previousMinutes !== null && stopMinutes !== null && stopMinutes + 5 < previousMinutes) {
        break;
      }

      normalizedStops.push({ ...stop, __order: order });
      previousMinutes = stopMinutes;
    }

    if (normalizedStops.length === 0) {
      return [];
    }

    const firstTurnbackIndex = normalizedStops.findIndex((stop) => stop.stationId === turnbackId);
    let sourceStops;
    if (mergedView?.direction === "down") {
      if (firstTurnbackIndex < 0) {
        sourceStops = [];
      } else {
        let firstOriginAfterTurnbackIndex = -1;
        for (let index = firstTurnbackIndex + 1; index < normalizedStops.length; index += 1) {
          if (normalizedStops[index].stationId === originId) {
            firstOriginAfterTurnbackIndex = index;
            break;
          }
        }

        sourceStops = firstOriginAfterTurnbackIndex >= 0
          ? normalizedStops.slice(firstTurnbackIndex, firstOriginAfterTurnbackIndex + 1)
          : normalizedStops.slice(firstTurnbackIndex);
      }
    } else {
      sourceStops = (firstTurnbackIndex >= 0
        ? normalizedStops.slice(0, firstTurnbackIndex + 1)
        : normalizedStops).filter((stop) => stop.__order <= turnbackIndex);
    }

    if (sourceStops.length === 0) {
      return [];
    }

    const mappedStops = sourceStops
      .map((stop) => {
        const displayStation = (displayStationsBySourceId.get(stop.stationId) ?? [])[0];
        return displayStation ? { ...stop, stationId: displayStation.plotId } : null;
      })
      .filter(Boolean);

    if (mappedStops.length === 0) {
      return [];
    }

    let realtimeFromStationId = null;
    let realtimeToStationId = null;
    if (trip.realtimeFromStationId && trip.realtimeToStationId) {
      let hasAdjacentRealtimePair = false;
      for (let index = 0; index < sourceStops.length - 1; index += 1) {
        const currentStop = sourceStops[index];
        const nextStop = sourceStops[index + 1];
        if (currentStop.stationId === trip.realtimeFromStationId && nextStop.stationId === trip.realtimeToStationId) {
          hasAdjacentRealtimePair = true;
          break;
        }
      }

      if (hasAdjacentRealtimePair) {
        realtimeFromStationId = (displayStationsBySourceId.get(trip.realtimeFromStationId) ?? [])[0]?.plotId ?? null;
        realtimeToStationId = (displayStationsBySourceId.get(trip.realtimeToStationId) ?? [])[0]?.plotId ?? null;
      }
    }

    return [{
      ...trip,
      id: `${trip.id}-${mergedView?.direction === "down" ? "down" : "up"}`,
      stops: mappedStops,
      realtimeFromStationId,
      realtimeToStationId
    }];
  }

  return [trip];
}

function buildTripGeometry(trip, stationById, startMinutes, endMinutes, maxPlotOrder, viewBoxWidth) {
  const orderedPoints = [];
  const anchors = [];
  const tripStops = Array.isArray(trip.stops) ? trip.stops : [];

  tripStops.forEach((stop) => {
    const station = stationById.get(stop.stationId);
    if (!station) {
      return;
    }

    const y = getY(station.plotOrder, maxPlotOrder);
    const arrivalMinutes = getStopArrivalMinutes(stop);
    const departureMinutes = getStopDepartureMinutes(stop);

    if (arrivalMinutes !== null) {
      const x = getX(arrivalMinutes, startMinutes, endMinutes, viewBoxWidth);
      if (x !== null) {
        orderedPoints.push({
          x,
          y,
          stationId: stop.stationId,
          minutes: arrivalMinutes
        });
        anchors.push({
          key: `${trip.id}-${stop.stationId}-arrival-anchor`,
          x,
          y,
          stop,
          station
        });
      }
    }

    if (departureMinutes !== null) {
      const x = getX(departureMinutes, startMinutes, endMinutes, viewBoxWidth);
      if (x !== null) {
        orderedPoints.push({
          x,
          y,
          stationId: stop.stationId,
          minutes: departureMinutes
        });
        if (arrivalMinutes === null || departureMinutes !== arrivalMinutes) {
          anchors.push({
            key: `${trip.id}-${stop.stationId}-departure-anchor`,
            x,
            y,
            stop,
            station
          });
        }
      }
    }
  });

  const filteredPoints = [];
  for (const point of orderedPoints) {
    const lastPoint = filteredPoints[filteredPoints.length - 1];
    if (!lastPoint) {
      filteredPoints.push(point);
      continue;
    }

    if (lastPoint.x === point.x && lastPoint.y === point.y) {
      continue;
    }

    const isSameStation = lastPoint.stationId === point.stationId;
    const isHorizontal = lastPoint.y === point.y;
    const dwellMinutes =
      Number.isFinite(lastPoint.minutes) && Number.isFinite(point.minutes)
        ? point.minutes - lastPoint.minutes
        : 0;

    if (isSameStation && isHorizontal && dwellMinutes > MAX_SAME_STATION_DWELL_MINUTES) {
      continue;
    }

    filteredPoints.push(point);
  }

  return {
    points: filteredPoints.length >= 2
      ? filteredPoints.map((point) => `${point.x},${point.y}`).join(" ")
      : null,
    anchors: anchors.filter((anchor) =>
      filteredPoints.some((point) => point.x === anchor.x && point.y === anchor.y)
    ),
    startPoint: filteredPoints[0] ?? null
  };
}

function getRealtimePosition(trip, stationById, startMinutes, endMinutes, viewBoxWidth) {
  if (trip.realtimeFromStationId && trip.realtimeToStationId) {
    const currentStation = stationById.get(trip.realtimeFromStationId);
    const nextStation = stationById.get(trip.realtimeToStationId);
    if (!currentStation || !nextStation) {
      return null;
    }

    const maxPlotOrder = Array.from(stationById.values()).reduce(
      (max, station) => Math.max(max, Number.isFinite(station.plotOrder) ? station.plotOrder : 0),
      0
    );
    const yA = getY(currentStation.plotOrder, maxPlotOrder);
    const yB = getY(nextStation.plotOrder, maxPlotOrder);
    const timeMinutes = timeToMinutes(trip.realtimeTime ?? "");
    const x = getX(timeMinutes, startMinutes, endMinutes, viewBoxWidth);
    if (x === null) {
      return null;
    }

    return {
      x,
      y: yA + (yB - yA) * (Number.isFinite(trip.realtimeProgress) ? trip.realtimeProgress : 0)
    };
  }
  return null;
}

function getRealtimeVehicleLabel(trip) {
  const matched = String(trip.id || "").match(/^RT-(\d+)/);
  return matched ? matched[1] : String(trip.id || "");
}

function buildRealtimeLabelPlacements(realtimeItems, diagramMetrics) {
  const sorted = [...realtimeItems].sort((left, right) => {
    if (left.x !== right.x) {
      return left.x - right.x;
    }
    return left.y - right.y;
  });
  const placements = [];

  for (const item of sorted) {
    let collisionIndex = 0;
    for (let index = placements.length - 1; index >= 0; index -= 1) {
      const placed = placements[index];
      if (item.x - placed.x > diagramMetrics.realtimeLabelOffsetX * 8) {
        break;
      }
      if (Math.abs(item.y - placed.y) < diagramMetrics.realtimeLabelOffsetY * 2.2) {
        collisionIndex += 1;
      }
    }

    const direction = collisionIndex % 2 === 0 ? -1 : 1;
    const lane = Math.floor(collisionIndex / 2);
    placements.push({
      ...item,
      labelX: item.x + diagramMetrics.realtimeLabelOffsetX,
      labelY: item.y + direction * (diagramMetrics.realtimeLabelOffsetY + lane * diagramMetrics.realtimeLabelOffsetY * 0.85)
    });
  }

  return placements;
}

export default function DiagramPanel({
  lines = [],
  stations = [],
  trips = [],
  mergedView,
  selectedTripId,
  setSelectedTripId
}) {
  const { t } = useI18n();
  const canvasRef = useRef(null);
  const [canvasWidth, setCanvasWidth] = useState(1678);
  const [canvasHeight, setCanvasHeight] = useState(602);
  const [diagramMetrics, setDiagramMetrics] = useState(() => getDiagramMetrics());
  const startMinutes = timeToMinutes(mergedView.windowStart) ?? 360;
  const endMinutes = timeToMinutes(mergedView.windowEnd) ?? 390;
  const displayStations = buildDisplayStations(stations, mergedView);
  const stationById = new Map(displayStations.map((station) => [station.plotId, station]));
  const lineColorById = new Map(lines.map((line) => [line.id, line.color]).filter((entry) => entry[1]));
  const maxPlotOrder = displayStations.reduce(
    (max, station) => Math.max(max, Number.isFinite(station.plotOrder) ? station.plotOrder : 0),
    0
  );
  const viewBoxWidth = getViewBoxWidth(canvasWidth, canvasHeight);
  const showStopAnchors = mergedView?.showStopAnchors !== false;
  const displayTrips = trips
    .flatMap((trip) => transformTripsForDisplay(trip, stations, displayStations, mergedView))
    .filter((trip) => Array.isArray(trip.stops) && trip.stops.length > 0);
  const minorTimeTicks = [];

  for (let tick = startMinutes; tick <= endMinutes; tick += 5) {
    minorTimeTicks.push(tick);
  }

  const timeLabelStepMinutes = getTimeLabelStepMinutes(canvasWidth, startMinutes, endMinutes);
  const majorTimeTicks = buildTimeLabelTicks(startMinutes, endMinutes, timeLabelStepMinutes);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) {
      return undefined;
    }

    const updateMetrics = () => {
      const width = canvas.clientWidth;
      const height = canvas.clientHeight;
      if (width <= 0 || height <= 0) {
        return;
      }
      setCanvasWidth(width);
      setCanvasHeight(height);
      setDiagramMetrics(getDiagramMetrics(width, height, mergedView?.lineWidthScale));
    };

    updateMetrics();

    if (typeof ResizeObserver !== "function") {
      return undefined;
    }

    const observer = new ResizeObserver(() => {
      updateMetrics();
    });
    observer.observe(canvas);

    return () => {
      observer.disconnect();
    };
  }, [mergedView?.lineWidthScale]);

  const realtimeLabelPlacements = buildRealtimeLabelPlacements(
    displayTrips
      .map((trip) => {
        const realtime = getRealtimePosition(trip, stationById, startMinutes, endMinutes, viewBoxWidth);
        if (!realtime) {
          return null;
        }
        return {
          tripId: trip.id,
          label: getRealtimeVehicleLabel(trip),
          x: realtime.x,
          y: realtime.y
        };
      })
      .filter(Boolean),
    diagramMetrics
  );

  return (
    <section className="dw-panel">
      <div className="dw-panel-header">
        <SafeControlText>{t("overview.panel.diagram")}</SafeControlText>
      </div>
      <div className="dw-diagram-panel">
        <div className="dw-diagram-layout">
          <div className="dw-diagram-corner" />

          <div className="dw-time-axis">
            {majorTimeTicks.map((tick, index) => {
              const isFirst = index === 0;
              const isLast = index === majorTimeTicks.length - 1;
              return (
                <div
                  key={tick}
                  className="dw-time-axis-label"
                  style={{
                    position: "absolute",
                    left: `${(getX(tick, startMinutes, endMinutes, viewBoxWidth) / viewBoxWidth) * 100}%`,
                    transform: isFirst ? "translateX(0)" : isLast ? "translateX(-100%)" : "translateX(-50%)"
                  }}
                >
                  {formatMinutes(tick)}
                </div>
              );
            })}
          </div>

          <div className="dw-station-axis">
            {displayStations.map((station) => {
              const stationLabel = station.rawName || station.name || t("diagram.station.unnamed");
              const labelLines = splitStationLabel(stationLabel);
              const y = getY(station.plotOrder, maxPlotOrder);

              return (
                <div
                  key={station.plotId}
                  className="dw-station-axis-label"
                  style={{
                    position: "absolute",
                    top: `${y}%`,
                    transform: "translateY(-50%)"
                  }}
                  title={stationLabel}
                >
                  {labelLines.map((line, index) => (
                    <span key={`${station.id}-${index}`} className="dw-station-axis-line">
                      {line}
                    </span>
                  ))}
                </div>
              );
            })}
          </div>

          <div className="dw-diagram-canvas" ref={canvasRef}>
            <svg viewBox={`0 0 ${viewBoxWidth} 100`} preserveAspectRatio="none" className="dw-diagram-svg">
              {displayStations.map((station) => {
                const stationName = station.rawName || station.name || t("diagram.station.unnamed");
                const y = getY(station.plotOrder, maxPlotOrder);
                return (
                  <g key={station.plotId}>
                    {station.hasSiding ? (
                      <rect
                        x="0"
                        y={Math.max(0, y - 1.2)}
                        width={viewBoxWidth}
                        height="2.4"
                        className="dw-siding-band"
                      />
                    ) : null}
                      <line
                        x1="0"
                        y1={y}
                        x2={viewBoxWidth}
                        y2={y}
                        className="dw-grid-line is-station"
                      >
                      <title>{`${stationName} | Distance ${station.distance}`}</title>
                    </line>
                  </g>
                );
              })}

              {minorTimeTicks.map((tick) => {
                const x = getX(tick, startMinutes, endMinutes, viewBoxWidth);
                return (
                    <line
                      key={tick}
                      x1={x}
                      y1="0"
                      x2={x}
                      y2="100"
                      className={`dw-grid-line is-time ${majorTimeTicks.includes(tick) ? "is-major" : "is-minor"}`}
                    >
                    <title>{formatMinutes(tick)}</title>
                  </line>
                );
              })}

              {displayTrips.map((trip) => {
                const isSelected = trip.id === selectedTripId;
                const geometry = buildTripGeometry(
                  trip,
                  stationById,
                  startMinutes,
                  endMinutes,
                  maxPlotOrder,
                  viewBoxWidth
                );
                const realtime = getRealtimePosition(
                  trip,
                  stationById,
                  startMinutes,
                  endMinutes,
                  viewBoxWidth
                );

                if (!geometry || (!geometry.points && geometry.anchors.length === 0 && !realtime)) {
                  return null;
                }

                const strokeColor = lineColorById.get(trip.lineId) || undefined;

                return (
                    <g key={trip.id} onClick={() => setSelectedTripId(trip.id)}>
                      {geometry.points ? (
                        <polyline
                          points={geometry.points}
                          fill="none"
                          strokeLinejoin="round"
                          strokeLinecap="butt"
                          className={`dw-trip-line ${trip.kind === "local" ? "is-local" : "is-express"} ${isSelected ? "is-selected" : ""}`}
                          style={{
                            stroke: strokeColor,
                            strokeWidth: isSelected ? diagramMetrics.selectedLineWidth : diagramMetrics.lineWidth
                          }}
                        >
                          <title>{buildTripTooltip(trip, stationById, t)}</title>
                        </polyline>
                      ) : null}

                      {showStopAnchors ? (geometry.points ? geometry.anchors : []).map((anchor) => (
                        <ellipse
                          key={anchor.key}
                          cx={anchor.x}
                          cy={anchor.y}
                          rx={diagramMetrics.nodeRx}
                          ry={diagramMetrics.nodeRy}
                          className="dw-stop-anchor"
                          style={{
                            strokeWidth: diagramMetrics.nodeStrokeWidth
                          }}
                        >
                        <title>{formatStopTooltip(anchor.stop, anchor.station, t)}</title>
                      </ellipse>
                    )) : null}

                      {realtime ? (
                      <ellipse
                        cx={realtime.x}
                        cy={realtime.y}
                        rx={diagramMetrics.realtimeRx}
                        ry={diagramMetrics.realtimeRy}
                        className={`dw-realtime-dot ${trip.kind === "local" ? "is-local" : "is-express"}`}
                        style={{ fill: strokeColor }}
                      >
                        <title>{`${trip.id} realtime position`}</title>
                      </ellipse>
                    ) : null}
                  </g>
                );
              })}

              {realtimeLabelPlacements.map((item) => (
                <text
                  key={`${item.tripId}-realtime-label`}
                  x={item.labelX}
                  y={item.labelY}
                  className="dw-realtime-label"
                  style={{ fontSize: `${diagramMetrics.realtimeLabelFontSize}px` }}
                >
                  {item.label}
                </text>
              ))}
            </svg>
          </div>
        </div>
      </div>
    </section>
  );
}
