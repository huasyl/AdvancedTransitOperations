export default function PassengerSectionRanking({ sections }) {
  if (!sections.length) {
    return <div className="rtw-passenger-empty">暂无真实断面流量排行</div>;
  }

  const sorted = [...sections].sort((left, right) => Number(right?.volume || 0) - Number(left?.volume || 0)).slice(0, 10);
  const maxValue = Math.max(1, ...sorted.map((entry) => Number(entry?.volume || 0)));
  return (
    <div className="rtw-passenger-ranking">
      {sorted.map((entry, index) => (
        <div key={`${entry?.label || index}`} className="rtw-passenger-ranking-row">
          <div className="rtw-passenger-ranking-label">{entry?.label || `${entry?.fromStationId || ""}-${entry?.toStationId || ""}`}</div>
          <div className="rtw-passenger-ranking-track">
            <span className="rtw-passenger-ranking-bar" style={{ width: `${(Number(entry?.volume || 0) / maxValue) * 100}%` }} />
          </div>
          <div className="rtw-passenger-ranking-value">{Number(entry?.volume || 0).toLocaleString()}</div>
        </div>
      ))}
    </div>
  );
}
