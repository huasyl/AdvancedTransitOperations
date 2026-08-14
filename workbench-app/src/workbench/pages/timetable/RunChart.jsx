import { useMemo } from "react";
import { minutesToTime } from "./timetable-data";

const WIDTH = 960;
const HEIGHT = 300;
const LEFT = 118;
const RIGHT = 24;
const TOP = 18;
const BOTTOM = 34;
const TICK_STEPS = [15, 30, 60, 120];
const MIN_TICK_GAP = 64;

function isChartPoint(point) {
  return (Number.isFinite(point?.arrivalTime)
    || Number.isFinite(point?.departureTime))
    && Number.isFinite(point?.distance);
}

function buildTimeTicks(minTime, maxTime) {
  const timeRange = maxTime - minTime;
  const plotWidth = WIDTH - LEFT - RIGHT;
  const maxIntervals = Math.max(1, Math.floor(plotWidth / MIN_TICK_GAP));
  const step = TICK_STEPS.find((value) => Math.ceil(timeRange / value) <= maxIntervals)
    || TICK_STEPS[TICK_STEPS.length - 1];
  const minimumGapMinutes = timeRange * MIN_TICK_GAP / plotWidth;
  const ticks = [minTime];
  const firstAlignedTick = Math.ceil(minTime / step) * step;

  for (let minute = firstAlignedTick; minute < maxTime; minute += step) {
    if (minute - minTime < minimumGapMinutes || maxTime - minute < minimumGapMinutes) {
      continue;
    }
    ticks.push(minute);
  }

  if (maxTime !== minTime) {
    ticks.push(maxTime);
  }
  return ticks;
}

export default function RunChart({ stations, series, startMinute, endMinute, emptyText }) {
  const model = useMemo(() => {
    if (stations.length === 0
      || !Number.isFinite(startMinute)
      || !Number.isFinite(endMinute)
      || startMinute >= endMinute) {
      return null;
    }

    const minTime = startMinute;
    const maxTime = endMinute;
    const minDistance = Math.min(...stations.map((station) => station.distance));
    const maxDistance = Math.max(...stations.map((station) => station.distance));
    const distanceRange = Math.max(1, maxDistance - minDistance);
    const timeRange = Math.max(1, maxTime - minTime);
    const x = (value) => LEFT + ((value - minTime) / timeRange) * (WIDTH - LEFT - RIGHT);
    const y = (value) => TOP + ((value - minDistance) / distanceRange) * (HEIGHT - TOP - BOTTOM);
    const ticks = buildTimeTicks(minTime, maxTime);
    const lines = series.flatMap((item) => {
      const points = item.points.filter(isChartPoint);
      let firstMinute = Infinity;
      let lastMinute = -Infinity;
      points.forEach((point) => {
        if (Number.isFinite(point.arrivalTime)) {
          firstMinute = Math.min(firstMinute, point.arrivalTime);
          lastMinute = Math.max(lastMinute, point.arrivalTime);
        }
        if (Number.isFinite(point.departureTime)) {
          firstMinute = Math.min(firstMinute, point.departureTime);
          lastMinute = Math.max(lastMinute, point.departureTime);
        }
      });
      if (lastMinute < minTime || firstMinute > maxTime) {
        return [];
      }

      const pathPoints = [];
      const markerPaths = [];
      points.forEach((point) => {
        if (Number.isFinite(point.arrivalTime)) {
          pathPoints.push(`${x(point.arrivalTime)},${y(point.distance)}`);
        }
        if (Number.isFinite(point.departureTime)
          && point.departureTime !== point.arrivalTime) {
          pathPoints.push(`${x(point.departureTime)},${y(point.distance)}`);
        }
        const markerMinute = Number.isFinite(point.arrivalTime)
          ? point.arrivalTime
          : point.departureTime;
        if (markerMinute >= minTime && markerMinute <= maxTime) {
          const markerX = x(markerMinute);
          const markerY = y(point.distance);
          markerPaths.push(`M${markerX - 3},${markerY}a3,3 0 1,0 6,0a3,3 0 1,0 -6,0`);
        }
      });
      return [{
        key: `${item.lineId}-${item.trainId}-${item.source || ""}`,
        color: item.color,
        pathPoints: pathPoints.join(" "),
        markerPath: markerPaths.join(" ")
      }];
    });
    if (lines.length === 0) {
      return null;
    }

    return { minTime, maxTime, x, y, ticks, lines };
  }, [endMinute, series, startMinute, stations]);

  if (!model) {
    return emptyText ? <div className="rtw-timetable-chart-empty">{emptyText}</div> : null;
  }

  return (
    <svg className="rtw-timetable-chart-svg" viewBox={`0 0 ${WIDTH} ${HEIGHT}`} preserveAspectRatio="xMidYMid meet" aria-hidden="true">
      <defs>
        <clipPath id="rtw-run-chart-plot-clip">
          <rect x={LEFT} y={TOP} width={WIDTH - LEFT - RIGHT} height={HEIGHT - TOP - BOTTOM} />
        </clipPath>
      </defs>
      {model.ticks.map((tick, index) => (
        <g key={`time-${tick}`}>
          <line className="rtw-timetable-chart-grid is-time" x1={model.x(tick)} x2={model.x(tick)} y1={TOP} y2={HEIGHT - BOTTOM} />
          <text
            className="rtw-timetable-chart-time"
            x={model.x(tick)}
            y={HEIGHT - 10}
            textAnchor={index === 0 ? "start" : index === model.ticks.length - 1 ? "end" : "middle"}
          >
            {minutesToTime(tick)}
          </text>
        </g>
      ))}
      {stations.map((station, index) => (
        <g key={`${station.id}-${station.occurrence ?? index}`}>
          <line className="rtw-timetable-chart-grid" x1={LEFT} x2={WIDTH - RIGHT} y1={model.y(station.distance)} y2={model.y(station.distance)} />
          <text className="rtw-timetable-chart-station" x={LEFT - 12} y={model.y(station.distance) + 4} textAnchor="end">{station.name}</text>
        </g>
      ))}
      {model.lines.map((line) => (
          <g key={line.key} clipPath="url(#rtw-run-chart-plot-clip)">
            <polyline className="rtw-timetable-chart-line" points={line.pathPoints} style={{ stroke: line.color }} />
            {line.markerPath ? <path className="rtw-timetable-chart-point" d={line.markerPath} style={{ fill: line.color }} /> : null}
          </g>
      ))}
    </svg>
  );
}
