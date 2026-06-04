import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import { formatPercentLabel } from "../planner-format.js";
import { PlannerMetric } from "./PlannerFields.jsx";
import PlannerPlanTabs from "./PlannerPlanTabs.jsx";
import PlannerRiskList from "./PlannerRiskList.jsx";
import PlannerTimetablePreview from "./PlannerTimetablePreview.jsx";

export default function PlannerResults({ result, preview, actions }) {
  const { t } = useNativeScheduleI18n();
  const { activePlan, activePlanId, importDisabled, importReferenceOnly, isGenerating, planLabels, plans, showGenericPlanError } = result;

  return (
    <section className="dw-planner-main">
      <PlannerPlanTabs plans={plans} activeId={activePlanId} onChange={actions.setActivePlanId} labels={planLabels} />
      {isGenerating ? (
        <div className="dw-planner-loading"><div className="dw-planner-spinner" aria-hidden="true" /><div className="dw-planner-loading-title">{t("planner.loading.title")}</div><div className="dw-planner-loading-body">{t("planner.loading.body")}</div></div>
      ) : (
        <>
          <WorkbenchScrollArea className="dw-planner-main-scroll" metricsKey={activePlanId}>
            <div className="dw-planner-main-content">
              {showGenericPlanError ? (
                <div className="dw-planner-error-state"><div className="dw-planner-error-title">{t("planner.error.title")}</div><div className="dw-planner-diagnostics-list">{activePlan.diagnostics.map((item) => <div key={item} className="dw-planner-diagnostics-item">{item}</div>)}</div></div>
              ) : (
                <>
                  <section className="dw-planner-section">
                    <div className="dw-planner-card-head">{t("planner.overview.title")}</div><div className="dw-planner-section-rule" />
                    <div className="dw-planner-metrics-row">
                      <PlannerMetric label={t("planner.metrics.expressSave")} value={(activePlan?.metrics?.expressSave ?? 0) + "m"} tone="success" />
                      <PlannerMetric label={t("planner.metrics.baselineHighestCapacityConsumption")} value={formatPercentLabel(activePlan?.metrics?.baselineHighestCapacityConsumptionPercent ?? 0)} />
                      <PlannerMetric label={t("planner.metrics.optimizedHighestCapacityConsumption")} value={formatPercentLabel(activePlan?.metrics?.optimizedHighestCapacityConsumptionPercent ?? 0)} tone={activePlan?.type === "warning" ? "warning" : "default"} />
                      <PlannerMetric label={t("planner.metrics.averageLocalWait")} value={(activePlan?.metrics?.averageLocalWait ?? 0) + "m"} tone="default" />
                      <PlannerMetric label={t("planner.metrics.affectedWaitTrips")} value={String(activePlan?.metrics?.affectedWaitTrips ?? 0)} />
                      <PlannerMetric label={t("planner.metrics.bypassCount")} value={String(activePlan?.metrics?.overtakes ?? 0)} />
                    </div>
                  </section>
                  <section className="dw-planner-section">
                    <div className="dw-planner-card-head">{t("planner.changes.title")}</div><div className="dw-planner-section-rule" />
                    {(activePlan?.changedWindows || []).length > 0 ? (
                      <div className="dw-planner-window-list">{(activePlan?.changedWindows || []).map((window) => <div key={window.id} className="dw-planner-window-item"><div className="dw-planner-window-head"><div className="dw-planner-window-title">{window.title}</div></div><div className="dw-planner-window-rows">{(window.rows || []).map((row) => <div key={row.id} className="dw-planner-window-row"><div className="dw-planner-window-row-line">{row.line}</div><div className="dw-planner-window-row-summary">{[row.timeText, row.summary].filter(Boolean).join(" / ")}</div></div>)}</div></div>)}</div>
                    ) : <div className="dw-planner-inline-note">{t("planner.empty.noLocalAdjustments")}</div>}
                  </section>
                  <section className="dw-planner-section">
                    <div className="dw-planner-card-head">{t("planner.riskZones.title")}</div><div className="dw-planner-section-rule" />
                    <div className="dw-planner-inline-summary"><span className="dw-planner-inline-summary-label">{t("planner.risk.selectedBypass")}</span><span className="dw-planner-inline-summary-value">{activePlan?.stations || "--"}</span></div>
                    <PlannerRiskList risks={activePlan?.risks || []} />
                  </section>
                  <PlannerTimetablePreview rows={preview.timetableRows} />
                </>
              )}
            </div>
          </WorkbenchScrollArea>
          {activePlan?.type !== "error" ? <footer className="dw-planner-main-foot"><button type="button" className={"dw-planner-primary-button " + (importDisabled ? "is-disabled" : "")} onClick={actions.writePlanToDraft} disabled={importDisabled}>{importReferenceOnly ? t("planner.footer.referenceOnly") : t("planner.footer.apply")}</button></footer> : null}
        </>
      )}
    </section>
  );
}
