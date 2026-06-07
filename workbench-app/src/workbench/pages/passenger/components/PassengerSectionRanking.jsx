import { useState } from "react";

export default function PassengerSectionRanking({ sections }) {
  const [hoveredIndex, setHoveredIndex] = useState(null);

  if (!sections.length) {
    return <div className="rtw-passenger-empty">暂无真实断面流量排行</div>;
  }

  const sorted = [...sections].sort((left, right) => Number(right?.volume || 0) - Number(left?.volume || 0)).slice(0, 10);
  const maxValue = Math.max(1, ...sorted.map((entry) => Number(entry?.volume || 0)));
  const hovered = hoveredIndex === null ? null : sorted[hoveredIndex];

  function handleHoverEnter(index) {
    setHoveredIndex((previousIndex) => previousIndex === index ? previousIndex : index);
  }

  function handleHoverLeave() {
    setHoveredIndex(null);
  }

  return (
    <div className="rtw-passenger-ranking" onMouseLeave={handleHoverLeave}>
      {sorted.map((entry, index) => {
        const label = entry?.label || `${entry?.fromStationId || ""}-${entry?.toStationId || ""}`;
        const volume = Number(entry?.volume || 0);
        return (
          <div
            key={`${entry?.label || index}`}
            className={`rtw-passenger-ranking-row ${hoveredIndex === index ? "is-hovered" : ""}`}
            onMouseEnter={() => handleHoverEnter(index)}
          >
            <div className="rtw-passenger-ranking-label">{label}</div>
            <div className="rtw-passenger-ranking-track">
              <span className="rtw-passenger-ranking-bar" style={{ width: `${(volume / maxValue) * 100}%` }} />
            </div>
            <div className="rtw-passenger-ranking-value">{volume.toLocaleString()}</div>
          </div>
        );
      })}
      {hovered ? (
        <div className="rtw-passenger-chart-tooltip is-ranking" style={{ left: "68%", top: `${Math.max(8, Math.min(88, 6 + (hoveredIndex || 0) * 10))}%` }}>
          <div className="rtw-passenger-chart-tooltip-title">{hovered?.label || `${hovered?.fromStationId || ""}-${hovered?.toStationId || ""}`}</div>
          <div className="rtw-passenger-chart-tooltip-value">断面流量: {Number(hovered?.volume || 0).toLocaleString()}</div>
        </div>
      ) : null}
    </div>
  );
}
