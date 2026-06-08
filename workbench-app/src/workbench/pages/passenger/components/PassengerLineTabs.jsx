export default function PassengerLineTabs({ lines, selectedLineId, onSelect }) {
  function lineLabel(line) {
    const code = String(line?.code || "").trim();
    const name = String(line?.shortName || line?.name || "").trim();
    return name || code || line?.id || "";
  }

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
          {lineLabel(line)}
        </button>
      ))}
    </div>
  );
}
