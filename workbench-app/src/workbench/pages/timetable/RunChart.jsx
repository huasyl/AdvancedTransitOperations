import { useEffect, useMemo, useRef, useState } from "react";
import { minutesToTime } from "./timetable-data";

const WIDTH = 960;
const RIGHT = 12;
const CHART_LAYOUTS = {
  expanded: { height: 432, left: 148, top: 24, bottom: 40 },
  collapsed: { height: 300, left: 112, top: 18, bottom: 34 }
};
const TICK_STEPS = [15, 30, 60, 120];
const MIN_TICK_GAP = 64;

function isChartPoint(point) {
  return (Number.isFinite(point?.arrivalTime)
    || Number.isFinite(point?.departureTime))
    && Number.isFinite(point?.distance);
}

function buildTimeTicks(minTime, maxTime, plotWidth) {
  const timeRange = maxTime - minTime;
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

export default function RunChart({ stations, series, startMinute, endMinute, emptyText, sidebarCollapsed }) {
  const chartWrapRef = useRef(null);
  const [chartViewport, setChartViewport] = useState(null);
  const layout = sidebarCollapsed ? CHART_LAYOUTS.collapsed : CHART_LAYOUTS.expanded;
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
    const plotWidth = WIDTH - layout.left - RIGHT;
    const x = (value) => layout.left + ((value - minTime) / timeRange) * plotWidth;
    const y = (value) => layout.top + ((value - minDistance) / distanceRange) * (layout.height - layout.top - layout.bottom);
    const ticks = buildTimeTicks(minTime, maxTime, plotWidth);
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
        const markerMinutes = [];
        if (Number.isFinite(point.arrivalTime)) {
          markerMinutes.push(point.arrivalTime);
        }
        if (Number.isFinite(point.departureTime)
          && point.departureTime !== point.arrivalTime) {
          markerMinutes.push(point.departureTime);
        }
        markerMinutes.forEach((markerMinute) => {
          if (markerMinute < minTime || markerMinute > maxTime) {
            return;
          }
          const markerX = x(markerMinute);
          const markerY = y(point.distance);
          markerPaths.push(`M${markerX - 2},${markerY}a2,2 0 1,0 4,0a2,2 0 1,0 -4,0`);
        });
      });
      if (pathPoints.length < 2) {
        return [];
      }
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
  }, [endMinute, layout, series, startMinute, stations]);

  useEffect(() => {
    function updateChartViewport() {
      const element = chartWrapRef.current;
      if (!element || typeof element.getBoundingClientRect !== "function") {
        return;
      }
      const rect = element.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) {
        return;
      }
      const scale = Math.min(rect.width / WIDTH, rect.height / layout.height);
      const width = WIDTH * scale;
      const height = layout.height * scale;
      setChartViewport({
        left: (rect.width - width) / 2,
        top: (rect.height - height) / 2,
        width,
        height
      });
    }

    updateChartViewport();
    const shortTimer = window.setTimeout(updateChartViewport, 0);
    const revealTimer = window.setTimeout(updateChartViewport, 250);
    window.addEventListener("resize", updateChartViewport);
    return () => {
      window.clearTimeout(shortTimer);
      window.clearTimeout(revealTimer);
      window.removeEventListener("resize", updateChartViewport);
    };
  }, [layout.height, model, stations.length]);

  if (!model) {
    return emptyText ? <div className="rtw-timetable-chart-empty">{emptyText}</div> : null;
  }

  return (
    <div ref={chartWrapRef} className="rtw-timetable-chart-wrap">
      <svg className="rtw-timetable-chart-svg" viewBox={`0 0 ${WIDTH} ${layout.height}`} preserveAspectRatio="xMidYMid meet" aria-hidden="true">
        <defs>
          <clipPath id="rtw-run-chart-plot-clip">
            <rect x={layout.left} y={layout.top - 3} width={WIDTH - layout.left - RIGHT} height={layout.height - layout.top - layout.bottom + 6} />
          </clipPath>
        </defs>
        {model.ticks.map((tick, index) => (
          <g key={`time-${tick}`}>
            <line className="rtw-timetable-chart-grid is-time" x1={model.x(tick)} x2={model.x(tick)} y1={layout.top} y2={layout.height - layout.bottom} />
            <text
              className="rtw-timetable-chart-time"
              x={model.x(tick)}
              y={layout.height - 10}
              textAnchor={index === 0 ? "start" : index === model.ticks.length - 1 ? "end" : "middle"}
            >
              {minutesToTime(tick)}
            </text>
          </g>
        ))}
        {stations.map((station, index) => (
          <g key={`${station.id}-${station.occurrence ?? index}`}>
            <line className="rtw-timetable-chart-grid" x1={layout.left} x2={WIDTH - RIGHT} y1={model.y(station.distance)} y2={model.y(station.distance)} />
          </g>
        ))}
        <g clipPath="url(#rtw-run-chart-plot-clip)">
          {model.lines.map((line) => (
            <g key={line.key}>
              <polyline className="rtw-timetable-chart-line" points={line.pathPoints} style={{ stroke: line.color }} />
              {line.markerPath ? <path className="rtw-timetable-chart-point" d={line.markerPath} /> : null}
            </g>
          ))}
        </g>
      </svg>
      {chartViewport ? <div className="rtw-timetable-chart-station-layer">
        {stations.map((station, index) => (
          <div
            key={`label-${station.id}-${station.occurrence ?? index}`}
            className="rtw-timetable-chart-station-label"
            style={{
              left: `${chartViewport.left}px`,
              width: `${((layout.left - 12) / WIDTH) * chartViewport.width}px`,
              top: `${chartViewport.top + (model.y(station.distance) / layout.height) * chartViewport.height}px`
            }}
          >
            {station.name}
          </div>
        ))}
      </div> : null}
    </div>
  );
}
