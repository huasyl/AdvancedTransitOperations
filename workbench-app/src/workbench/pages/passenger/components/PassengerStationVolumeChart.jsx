import { useMemo, useRef, useState } from "react";

const WIDTH = 720;
const HEIGHT = 250;
const LEFT = 52;
const RIGHT = 12;
const TOP = 16;
const BOTTOM = 56;
const PLOT_WIDTH = WIDTH - LEFT - RIGHT;
const PLOT_HEIGHT = HEIGHT - TOP - BOTTOM;
const ENABLE_PASSENGER_CHART_HOVER = false;
const HOVER_THROTTLE_MS = 80;

function getValue(entry, key) {
  const value = Number(entry?.[key] || 0);
  return Number.isFinite(value) && value > 0 ? value : 0;
}

function getStationName(entry, index) {
  return entry?.stationName || entry?.name || entry?.stationId || `Station ${index + 1}`;
}

function formatTick(value) {
  return Math.round(value).toLocaleString();
}

function buildChartData(volumes) {
  const maxValue = Math.max(1, ...volumes.map((entry) => getValue(entry, "inflow") + getValue(entry, "outflow")));
  const yMax = Math.ceil(maxValue / 1000) * 1000;
  const bandWidth = PLOT_WIDTH / Math.max(1, volumes.length);
  const yTicks = [0, 0.25, 0.5, 0.75, 1].map((ratio) => ({
    value: yMax * ratio,
    y: TOP + PLOT_HEIGHT - PLOT_HEIGHT * ratio
  }));

  return {
    yMax,
    bandWidth,
    yTicks,
    items: volumes.map((entry, index) => {
      const inflow = getValue(entry, "inflow");
      const outflow = getValue(entry, "outflow");
      const x = LEFT + index * bandWidth + bandWidth * 0.22;
      const barWidth = Math.max(5, bandWidth * 0.18);
      return {
        entry,
        index,
        name: getStationName(entry, index),
        inflow,
        outflow,
        x,
        labelX: LEFT + index * bandWidth + bandWidth * 0.5,
        inflowHeight: (inflow / yMax) * PLOT_HEIGHT,
        outflowHeight: (outflow / yMax) * PLOT_HEIGHT,
        barWidth
      };
    })
  };
}

export default function PassengerStationVolumeChart({ volumes }) {
  const [hoveredIndex, setHoveredIndex] = useState(null);
  const hoverRef = useRef({ lastTime: 0 });
  const chart = useMemo(() => buildChartData(volumes), [volumes]);
  const hovered = !ENABLE_PASSENGER_CHART_HOVER || hoveredIndex === null ? null : chart.items[hoveredIndex];

  if (!volumes.length) {
    return <div className="rtw-passenger-empty">暂无真实站点进出站数据</div>;
  }

  function handleHoverMove(event) {
    if (!ENABLE_PASSENGER_CHART_HOVER || chart.items.length === 0) {
      return;
    }
    const now = Date.now();
    if (now - hoverRef.current.lastTime < HOVER_THROTTLE_MS) {
      return;
    }
    hoverRef.current.lastTime = now;

    const rect = event.currentTarget.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) {
      return;
    }

    const plotX = Math.max(0, Math.min(PLOT_WIDTH, ((event.clientX - rect.left) / rect.width) * PLOT_WIDTH));
    const localX = LEFT + plotX;
    const rawIndex = Math.floor((localX - LEFT) / chart.bandWidth);
    const nextIndex = Math.max(0, Math.min(chart.items.length - 1, rawIndex));

    setHoveredIndex((previousIndex) => previousIndex === nextIndex ? previousIndex : nextIndex);
  }

  function handleHoverLeave() {
    setHoveredIndex(null);
    hoverRef.current.lastTime = 0;
  }

  return (
    <div className="rtw-passenger-chart-wrap" onMouseLeave={handleHoverLeave}>
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} className="rtw-passenger-chart-svg">
        {chart.yTicks.map((tick) => (
          <g key={`y-${tick.value}`}>
            <line x1={LEFT} y1={tick.y} x2={LEFT + PLOT_WIDTH} y2={tick.y} stroke="#27272a" strokeWidth="1" strokeDasharray="3 3" />
            <text x={LEFT - 10} y={tick.y + 5} fill="#71717a" fontSize="14" fontWeight="600" textAnchor="end">{formatTick(tick.value)}</text>
          </g>
        ))}
        {chart.items.map((item) => (
          <g key={`${item.entry?.stationId || item.index}`}>
            <rect x={item.x} y={TOP + PLOT_HEIGHT - item.inflowHeight} width={item.barWidth} height={item.inflowHeight} rx="4" fill="#10b981" />
            <rect x={item.x + item.barWidth + 4} y={TOP + PLOT_HEIGHT - item.outflowHeight} width={item.barWidth} height={item.outflowHeight} rx="4" fill="#f59e0b" />
            <text
              x={item.labelX}
              y={TOP + PLOT_HEIGHT + 18}
              fill="#71717a"
              fontSize="13"
              fontWeight="600"
              textAnchor="end"
              transform={`rotate(-30 ${item.labelX} ${TOP + PLOT_HEIGHT + 18})`}
            >
              {item.name}
            </text>
          </g>
        ))}
      </svg>
      {ENABLE_PASSENGER_CHART_HOVER ? (
        <div
          className="rtw-passenger-hit-zones is-stations"
          onMouseMove={handleHoverMove}
          onMouseLeave={handleHoverLeave}
        />
      ) : null}
      {hovered ? (
        <div className="rtw-passenger-chart-tooltip" style={{ left: `${(hovered.labelX / WIDTH) * 100}%`, top: `${((TOP + PLOT_HEIGHT - Math.max(hovered.inflowHeight, hovered.outflowHeight)) / HEIGHT) * 100}%` }}>
          <div className="rtw-passenger-chart-tooltip-title">{hovered.name}</div>
          <div className="rtw-passenger-chart-tooltip-value">进站量: {hovered.inflow.toLocaleString()}</div>
          <div className="rtw-passenger-chart-tooltip-value">出站量: {hovered.outflow.toLocaleString()}</div>
        </div>
      ) : null}
    </div>
  );
}
