import { useEffect, useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

const WIDTH = 720;
const HEIGHT = 300;
const LEFT = 52;
const RIGHT = 12;
const TOP = 16;
const BOTTOM = 104;
const PLOT_WIDTH = WIDTH - LEFT - RIGHT;
const PLOT_HEIGHT = HEIGHT - TOP - BOTTOM;
const ENABLE_PASSENGER_CHART_HOVER = false;
const HOVER_THROTTLE_MS = 80;
const MAX_STATIONS = 12;

function getValue(entry, key) {
  const value = Number(entry?.[key] || 0);
  return Number.isFinite(value) && value > 0 ? value : 0;
}

function getStationName(entry, index, t) {
  return entry?.stationName || entry?.name || entry?.stationId || t("nativeWorkbench.passenger.fallback.stationName", { index: index + 1 });
}

function splitStationLabel(value) {
  const text = String(value || "").trim();
  if (!text || text.length <= 12) {
    return [text];
  }

  const words = text.split(/\s+/).filter(Boolean);
  if (words.length >= 2) {
    const midpoint = Math.ceil(words.length / 2);
    return [words.slice(0, midpoint).join(" "), words.slice(midpoint).join(" ")];
  }

  const pivot = Math.ceil(text.length / 2);
  return [text.slice(0, pivot), text.slice(pivot)];
}

function buildDisplayVolumes(volumes) {
  const stationMap = new Map();
  volumes.forEach((entry, index) => {
    const stationId = String(entry?.stationId || entry?.stationName || entry?.name || index);
    if (!stationMap.has(stationId)) {
      stationMap.set(stationId, {
        ...entry,
        stationId,
        inflow: 0,
        outflow: 0
      });
    }
    const station = stationMap.get(stationId);
    station.inflow += getValue(entry, "inflow");
    station.outflow += getValue(entry, "outflow");
  });
  return [...stationMap.values()]
    .sort((left, right) => (getValue(right, "inflow") + getValue(right, "outflow")) - (getValue(left, "inflow") + getValue(left, "outflow")))
    .slice(0, MAX_STATIONS);
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

function buildChartData(volumes, t) {
  const maxValue = Math.max(1, ...volumes.map((entry) => Math.max(getValue(entry, "inflow"), getValue(entry, "outflow"))));
  const yMax = chartMax(maxValue);
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
        name: getStationName(entry, index, t),
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
  const { t } = useNativeScheduleI18n();
  const [hoveredIndex, setHoveredIndex] = useState(null);
  const [chartViewport, setChartViewport] = useState(null);
  const chartWrapRef = useRef(null);
  const hoverRef = useRef({ lastTime: 0 });
  const displayVolumes = useMemo(() => buildDisplayVolumes(volumes), [volumes]);
  const chart = useMemo(() => buildChartData(displayVolumes, t), [displayVolumes, t]);
  const hovered = !ENABLE_PASSENGER_CHART_HOVER || hoveredIndex === null ? null : chart.items[hoveredIndex];

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
      const scale = Math.min(rect.width / WIDTH, rect.height / HEIGHT);
      const width = WIDTH * scale;
      const height = HEIGHT * scale;
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
  }, [displayVolumes.length]);

  if (!displayVolumes.length) {
    return <div className="rtw-passenger-empty">{t("nativeWorkbench.passenger.empty.stationVolumes")}</div>;
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
    <div ref={chartWrapRef} className="rtw-passenger-chart-wrap" onMouseLeave={handleHoverLeave}>
      <div className="rtw-passenger-station-legend">
        <span className="rtw-passenger-station-legend-item">
          <span className="rtw-passenger-station-legend-swatch is-inflow" />
          <span>{t("nativeWorkbench.passenger.legend.inflow")}</span>
        </span>
        <span className="rtw-passenger-station-legend-item">
          <span className="rtw-passenger-station-legend-swatch is-outflow" />
          <span>{t("nativeWorkbench.passenger.legend.outflow")}</span>
        </span>
      </div>
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} className="rtw-passenger-chart-svg">
        {chart.yTicks.map((tick) => (
          <g key={`y-${tick.value}`}>
            {tick.value > 0 && tick.value < chart.yMax ? (
              <line x1={LEFT} y1={tick.y} x2={LEFT + PLOT_WIDTH} y2={tick.y} stroke="#27272a" strokeWidth="1" strokeDasharray="3 3" />
            ) : null}
            <text x={LEFT - 10} y={tick.y + 5} fill="#71717a" fontSize="14" fontWeight="600" textAnchor="end">{formatTick(tick.value)}</text>
          </g>
        ))}
        {chart.items.map((item) => (
          <g key={`${item.entry?.stationId || item.index}`}>
            <rect x={item.x} y={TOP + PLOT_HEIGHT - item.inflowHeight} width={item.barWidth} height={item.inflowHeight} rx="4" fill="#10b981" />
            <rect x={item.x + item.barWidth + 4} y={TOP + PLOT_HEIGHT - item.outflowHeight} width={item.barWidth} height={item.outflowHeight} rx="4" fill="#f59e0b" />
          </g>
        ))}
      </svg>
      {chartViewport ? <div className="rtw-passenger-station-label-layer">
        {chart.items.map((item) => (
          <div
            key={`label-${item.entry?.stationId || item.index}`}
            className="rtw-passenger-station-label"
            style={{
              left: `${chartViewport.left + (item.labelX / WIDTH) * chartViewport.width}px`,
              top: `${chartViewport.top + ((TOP + PLOT_HEIGHT + 18) / HEIGHT) * chartViewport.height}px`
            }}
          >
            {splitStationLabel(item.name).map((label, labelIndex) => (
              <span key={`${item.entry?.stationId || item.index}-${labelIndex}`}>{label}</span>
            ))}
          </div>
        ))}
      </div> : null}
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
          <div className="rtw-passenger-chart-tooltip-value">{t("nativeWorkbench.passenger.tooltip.inflow", { value: hovered.inflow.toLocaleString() })}</div>
          <div className="rtw-passenger-chart-tooltip-value">{t("nativeWorkbench.passenger.tooltip.outflow", { value: hovered.outflow.toLocaleString() })}</div>
        </div>
      ) : null}
    </div>
  );
}
