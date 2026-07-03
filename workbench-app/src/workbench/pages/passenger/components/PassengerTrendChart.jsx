import { useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

const WIDTH = 720;
const HEIGHT = 250;
const LEFT = 52;
const RIGHT = 12;
const TOP = 18;
const BOTTOM = 32;
const PLOT_WIDTH = WIDTH - LEFT - RIGHT;
const PLOT_HEIGHT = HEIGHT - TOP - BOTTOM;
const ENABLE_PASSENGER_CHART_HOVER = false;
const HOVER_THROTTLE_MS = 80;

function getPassengerValue(point) {
  const value = Number(point?.passengers || 0);
  return Number.isFinite(value) ? value : 0;
}

function formatTick(value) {
  return Math.round(value).toLocaleString();
}

function chartMax(value) {
  const target = Math.max(1, Number(value || 0) * 1.08);
  const magnitude = Math.pow(10, Math.floor(Math.log10(target)));
  const steps = [1, 2, 5, 10];
  for (let index = 0; index < steps.length; index += 1) {
    const candidate = steps[index] * magnitude;
    if (candidate >= target) {
      return candidate;
    }
  }
  return 10 * magnitude;
}

function buildChartData(points) {
  const maxValue = Math.max(1, ...points.map(getPassengerValue));
  const yMax = chartMax(maxValue);
  const coords = points.map((point, index) => {
    const x = LEFT + (points.length <= 1 ? 0 : (index / (points.length - 1)) * PLOT_WIDTH);
    const y = TOP + PLOT_HEIGHT - (getPassengerValue(point) / yMax) * PLOT_HEIGHT;
    return {
      point,
      x,
      y,
      value: getPassengerValue(point)
    };
  });
  const linePath = coords.map((coord, index) => `${index === 0 ? "M" : "L"} ${coord.x} ${coord.y}`).join(" ");
  const areaPath = `${linePath} L ${LEFT + PLOT_WIDTH} ${TOP + PLOT_HEIGHT} L ${LEFT} ${TOP + PLOT_HEIGHT} Z`;
  const yTicks = [0, 0.25, 0.5, 0.75, 1].map((ratio) => ({
    value: yMax * ratio,
    y: TOP + PLOT_HEIGHT - PLOT_HEIGHT * ratio
  }));
  const xTickStep = Math.max(1, Math.ceil(points.length / 6));
  const xTicks = points
    .map((point, index) => ({ point, index }))
    .filter((item) => item.index % xTickStep === 0 || item.index === points.length - 1)
    .map((item) => ({
      label: item.point?.hour || `${item.index}:00`,
      x: coords[item.index]?.x || LEFT,
      y: TOP + PLOT_HEIGHT
    }));

  return { coords, linePath, areaPath, yTicks, xTicks, yMax };
}

export default function PassengerTrendChart({ points }) {
  const { t } = useNativeScheduleI18n();
  const [hoveredIndex, setHoveredIndex] = useState(null);
  const hoverRef = useRef({ lastTime: 0 });
  const chart = useMemo(() => buildChartData(points), [points]);
  const hovered = !ENABLE_PASSENGER_CHART_HOVER || hoveredIndex === null ? null : chart.coords[hoveredIndex];

  if (!points.length) {
    return <div className="rtw-passenger-empty">{t("nativeWorkbench.passenger.empty.trend")}</div>;
  }

  function handleHoverMove(event) {
    if (!ENABLE_PASSENGER_CHART_HOVER || chart.coords.length === 0) {
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
    const nextIndex = Math.max(0, Math.min(chart.coords.length - 1, Math.round((plotX / PLOT_WIDTH) * (chart.coords.length - 1))));

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
            {tick.value > 0 && tick.value < chart.yMax ? (
              <line x1={LEFT} y1={tick.y} x2={LEFT + PLOT_WIDTH} y2={tick.y} stroke="#27272a" strokeWidth="1" strokeDasharray="3 3" />
            ) : null}
            <text x={LEFT - 10} y={tick.y + 5} fill="#71717a" fontSize="14" fontWeight="600" textAnchor="end">{formatTick(tick.value)}</text>
          </g>
        ))}
        {chart.xTicks.map((tick) => (
          <text key={`x-${tick.label}`} x={tick.x} y={tick.y + 23} fill="#71717a" fontSize="14" fontWeight="600" textAnchor="middle">{tick.label}</text>
        ))}
        <path d={chart.areaPath} fill="rgba(56, 189, 248, 0.20)" />
        <path d={chart.linePath} fill="none" stroke="#38bdf8" strokeWidth="3" />
        {hovered ? (
          <g>
            <line x1={hovered.x} y1={TOP} x2={hovered.x} y2={TOP + PLOT_HEIGHT} stroke="#d4d4d8" strokeWidth="1" />
            <circle cx={hovered.x} cy={hovered.y} r="5" fill="#38bdf8" stroke="#f4f4f5" strokeWidth="2" />
          </g>
        ) : null}
      </svg>
      {ENABLE_PASSENGER_CHART_HOVER ? (
        <div
          className="rtw-passenger-hit-zones"
          onMouseMove={handleHoverMove}
          onMouseLeave={handleHoverLeave}
        />
      ) : null}
      {hovered ? (
        <div className="rtw-passenger-chart-tooltip" style={{ left: `${(hovered.x / WIDTH) * 100}%`, top: `${(hovered.y / HEIGHT) * 100}%` }}>
          <div className="rtw-passenger-chart-tooltip-title">{hovered.point?.hour || ""}</div>
          <div className="rtw-passenger-chart-tooltip-value">{t("nativeWorkbench.passenger.tooltip.realtime", { value: hovered.value.toLocaleString() })}</div>
        </div>
      ) : null}
    </div>
  );
}
