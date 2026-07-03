import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

export default function PassengerLineTabs({ lines, selectedLineId, onSelect }) {
  const { t } = useNativeScheduleI18n();

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
        {t("nativeWorkbench.passenger.filter.all")}
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
