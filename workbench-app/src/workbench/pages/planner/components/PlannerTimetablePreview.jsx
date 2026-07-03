import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

export default function PlannerTimetablePreview({ rows }) {
  const { t } = useNativeScheduleI18n();

  if (!Array.isArray(rows) || rows.length === 0) {
    return null;
  }

  return (
    <section className="dw-planner-section">
      <div className="dw-planner-card-head">{t("planner.table.title")}</div>
      <div className="dw-planner-section-rule" />
      <div className="dw-planner-table-head">
        <div className="is-time">{t("planner.table.head.time")}</div>
        <div className="is-line">{t("planner.table.head.line")}</div>
        <div className="is-station">{t("planner.table.head.station")}</div>
        <div className="is-status">{t("planner.table.head.status")}</div>
      </div>
      <div className="dw-planner-table-body">
        {rows.map((row) => row.skip ? (
          <div key={row.id} className="dw-planner-table-skip">{row.message}</div>
        ) : (
          <div key={row.id} className={`dw-planner-table-row ${row.warning ? "is-warning" : ""}`}>
            <div className="is-time dw-planner-table-time">{row.time}</div>
            <div className="is-line">
              <div className="dw-planner-line-stack">
                <span className={`dw-planner-line-dot is-${row.dotTone || "local"}`} aria-hidden="true" />
                <span className="dw-planner-line-name">{row.line}</span>
                <span className={`dw-planner-type-badge is-${row.dotTone || "local"}`}>{row.type}</span>
              </div>
            </div>
            <div className="is-station dw-planner-table-station">{row.station || "--"}</div>
            <div className="is-status">
              <span className={`dw-planner-table-status ${row.warning ? "is-warning" : "is-accent"}`}>{row.status}</span>
              {row.info ? <span className="dw-planner-table-info">{row.info}</span> : null}
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}
