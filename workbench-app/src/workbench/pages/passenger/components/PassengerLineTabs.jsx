export default function PassengerLineTabs({ lines, selectedLineId, onSelect }) {
  return (
    <div className="rtw-passenger-line-tabs">
      <button
        type="button"
        className={`rtw-passenger-line-tab ${selectedLineId === "ALL" ? "is-active" : ""}`}
        onClick={() => onSelect("ALL")}
      >
        全网综合
      </button>
      {lines.map((line) => (
        <button
          key={line.id}
          type="button"
          className={`rtw-passenger-line-tab ${selectedLineId === line.id ? "is-active" : ""}`}
          onClick={() => onSelect(line.id)}
        >
          <span className="rtw-passenger-line-dot" style={{ backgroundColor: line.color }} />
          {line.code} {line.shortName || line.name}
        </button>
      ))}
    </div>
  );
}
