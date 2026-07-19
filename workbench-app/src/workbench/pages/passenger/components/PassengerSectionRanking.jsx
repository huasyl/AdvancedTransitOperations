import { useState } from "react";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

const FALLBACK_COLORS = ["#38bdf8", "#f59e0b", "#10b981", "#ef4444", "#a78bfa", "#f472b6", "#22c55e", "#eab308"];

function sectionKey(entry) {
  const fromStationId = entry?.fromStationId || "";
  const toStationId = entry?.toStationId || "";
  return fromStationId && toStationId ? `${fromStationId}->${toStationId}` : "";
}

function sectionLabel(entry) {
  return entry?.label || `${entry?.fromStationId || ""}-${entry?.toStationId || ""}`;
}

function sectionTotal(entry) {
  const volume = Number(entry?.volume || 0);
  const sampleCount = Number(entry?.sampleCount || 0);
  const total = sampleCount > 0 ? volume * sampleCount : volume;
  return Number.isFinite(total) && total > 0 ? total : 0;
}

function buildLineMap(lines) {
  return new Map((Array.isArray(lines) ? lines : []).map((line, index) => [
    line?.id || "",
    {
      label: String(line?.shortName || line?.name || line?.code || "--").trim(),
      color: line?.color || FALLBACK_COLORS[index % FALLBACK_COLORS.length]
    }
  ]));
}

function buildStackedSections(sections, lines) {
  const lineMap = buildLineMap(lines);
  const sectionMap = new Map();

  sections.forEach((entry) => {
    const key = sectionKey(entry);
    const lineId = entry?.lineId || "";
    const total = sectionTotal(entry);
    if (!key || !lineId || total <= 0) {
      return;
    }

    if (!sectionMap.has(key)) {
      sectionMap.set(key, {
        key,
        label: sectionLabel(entry),
        total: 0,
        segments: new Map()
      });
    }

    const section = sectionMap.get(key);
    section.total += total;
    const existing = section.segments.get(lineId) || {
      lineId,
      label: lineMap.get(lineId)?.label || "--",
      color: lineMap.get(lineId)?.color || FALLBACK_COLORS[section.segments.size % FALLBACK_COLORS.length],
      total: 0
    };
    existing.total += total;
    section.segments.set(lineId, existing);
  });

  return [...sectionMap.values()]
    .map((section) => ({
      ...section,
      segments: [...section.segments.values()].sort((left, right) => Number(right.total || 0) - Number(left.total || 0))
    }))
    .sort((left, right) => Number(right.total || 0) - Number(left.total || 0))
    .slice(0, 10);
}

export default function PassengerSectionRanking({ sections, lines = [] }) {
  const { t } = useNativeScheduleI18n();
  const [hoveredIndex, setHoveredIndex] = useState(null);

  if (!sections.length) {
    return <div className="rtw-passenger-empty">{t("nativeWorkbench.passenger.empty.sectionRanking")}</div>;
  }

  const sorted = buildStackedSections(sections, lines);
  const maxValue = Math.max(1, ...sorted.map((entry) => Number(entry?.total || 0)));
  const hovered = hoveredIndex === null ? null : sorted[hoveredIndex];

  if (!sorted.length) {
    return <div className="rtw-passenger-empty">{t("nativeWorkbench.passenger.empty.sectionRanking")}</div>;
  }

  function handleHoverEnter(index) {
    setHoveredIndex((previousIndex) => previousIndex === index ? previousIndex : index);
  }

  function handleHoverLeave() {
    setHoveredIndex(null);
  }

  return (
    <div className="rtw-passenger-ranking" onMouseLeave={handleHoverLeave}>
      {sorted.map((entry, index) => {
        const total = Number(entry?.total || 0);
        return (
          <div
            key={entry.key || index}
            className={`rtw-passenger-ranking-row ${hoveredIndex === index ? "is-hovered" : ""}`}
            onMouseEnter={() => handleHoverEnter(index)}
          >
            <div className="rtw-passenger-ranking-label">{entry.label}</div>
            <div className="rtw-passenger-ranking-track">
              <span className="rtw-passenger-ranking-bar" style={{ width: `${(total / maxValue) * 100}%` }}>
                {entry.segments.map((segment) => (
                  <span
                    key={segment.lineId}
                    className="rtw-passenger-ranking-segment"
                    style={{
                      width: `${total > 0 ? (Number(segment.total || 0) / total) * 100 : 0}%`,
                      backgroundColor: segment.color
                    }}
                  />
                ))}
              </span>
            </div>
            <div className="rtw-passenger-ranking-value">{Math.round(total).toLocaleString()}</div>
          </div>
        );
      })}
      {hovered ? (
        <div className="rtw-passenger-chart-tooltip is-ranking" style={{ left: "68%", top: `${Math.max(8, Math.min(88, 6 + (hoveredIndex || 0) * 10))}%` }}>
          <div className="rtw-passenger-chart-tooltip-title">{hovered.label}</div>
          <div className="rtw-passenger-chart-tooltip-total">
            <span className="rtw-passenger-chart-tooltip-total-label">{t("nativeWorkbench.passenger.tooltip.sectionTotal")}</span>
            <span className="rtw-passenger-chart-tooltip-total-number">{Math.round(Number(hovered.total || 0)).toLocaleString()}</span>
          </div>
          <div className="rtw-passenger-chart-tooltip-lines">
            {hovered.segments.slice(0, 4).map((segment) => (
              <div key={segment.lineId} className="rtw-passenger-chart-tooltip-row">
                <span className="rtw-passenger-chart-tooltip-label" style={{ color: segment.color }}>{segment.label}</span>
                <span className="rtw-passenger-chart-tooltip-number" style={{ color: segment.color }}>{Math.round(Number(segment.total || 0)).toLocaleString()}</span>
              </div>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}
