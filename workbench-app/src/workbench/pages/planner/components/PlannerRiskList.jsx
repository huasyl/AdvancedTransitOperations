import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

export default function PlannerRiskList({ risks }) {
  const { t } = useNativeScheduleI18n();

  if (!Array.isArray(risks) || risks.length === 0) {
    return <div className="dw-planner-inline-note">{t("planner.empty.noRiskZones")}</div>;
  }

  return (
    <div className="dw-planner-risk-list">
      {risks.map((risk) => (
        <div key={risk.id} className={"dw-planner-risk-item " + (risk.itemToneClass || (risk.warning ? "is-error" : "is-warning"))}>
          <div className="dw-planner-risk-head">
            <span className={"dw-planner-risk-badge " + (risk.typeToneClass || (risk.warning ? "is-error" : "is-warning"))}>{risk.status}</span>
            {risk.stateLabel ? (
              <span className={"dw-planner-risk-badge " + (risk.stateToneClass || (risk.warning ? "is-error" : "is-warning"))}>{risk.stateLabel}</span>
            ) : null}
            <span className="dw-planner-risk-route">
              <span className="dw-planner-risk-route-source">{risk.lineSrc}</span>
              <span className="dw-planner-risk-route-arrow">→</span>
              <span className="dw-planner-risk-route-dest">{risk.lineDest}</span>
            </span>
          </div>
          {risk.summary && risk.tripPair ? <div className="dw-planner-risk-trip">{risk.tripPair}</div> : null}
          {risk.summary ? <div className="dw-planner-risk-summary">{risk.summary}</div> : null}
          {risk.detail ? <div className="dw-planner-risk-detail">{risk.detail}</div> : null}
          {!risk.summary && (risk.events || []).length === 0 ? (
            <div className="dw-planner-risk-range">
              <span className="dw-planner-risk-label">{t("planner.risk.range")}</span>
              <span>{risk.interval}</span>
            </div>
          ) : null}
          {!risk.summary && (risk.events || []).length > 0 ? (
            <div className="dw-planner-risk-events">
              {(risk.events || []).map((event) => (
                <div key={event.id} className="dw-planner-risk-event">
                  <div className="dw-planner-risk-event-head">
                    <span className={"dw-planner-risk-badge " + (event.warning ? "is-error" : "is-warning")}>{event.status}</span>
                    <span>{event.tripPair}</span>
                  </div>
                  <div className="dw-planner-risk-range">
                    <span className="dw-planner-risk-label">{t("planner.risk.range")}</span>
                    <span>{event.interval || risk.interval}</span>
                  </div>
                  {event.waitStation ? (
                    <div className="dw-planner-risk-range">
                      <span className="dw-planner-risk-label">{t("planner.risk.waitStation")}</span>
                      <span>{event.waitStation}</span>
                    </div>
                  ) : null}
                  <div className="dw-planner-risk-stats">
                    {event.catchupTime ? <span>{t("planner.risk.catchupTime")} <span className="dw-planner-risk-stat-value">{event.catchupTime}</span></span> : null}
                    <span>{t("planner.risk.requiredAdjustment")} <span className="dw-planner-risk-stat-value">{event.required}</span></span>
                    {event.planned ? <span>{t("planner.risk.plannedAdjustment")} <span className="dw-planner-risk-stat-value">{event.planned}</span></span> : null}
                    <span>{t("planner.risk.waitLimit")} <span className="dw-planner-risk-stat-value">{event.budget}</span></span>
                  </div>
                  {event.reason ? (
                    <div className="dw-planner-risk-action">
                      <span className="dw-planner-risk-action-label">{t("planner.risk.reasonLabel")}</span>
                      <span>{event.reason}</span>
                    </div>
                  ) : null}
                </div>
              ))}
            </div>
          ) : null}
          {!risk.summary && (risk.events || []).length === 0 ? (
            <div className="dw-planner-risk-stats">
              <span>{t("planner.risk.catchups")} <span className="dw-planner-risk-stat-value">{risk.catchups}</span></span>
              <span>{t("planner.risk.maxGap")} <span className="dw-planner-risk-stat-value">{risk.severity}</span></span>
            </div>
          ) : null}
          <div className="dw-planner-risk-action">
            <span className="dw-planner-risk-action-label">{t("planner.risk.actionLabel")}</span>
            <span>{risk.action || risk.suggestion}</span>
          </div>
        </div>
      ))}
    </div>
  );
}
