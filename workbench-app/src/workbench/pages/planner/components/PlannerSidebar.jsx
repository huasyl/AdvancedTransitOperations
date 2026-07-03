import WorkbenchDropdown from "../../../shared/WorkbenchDropdown";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import { PlannerChoiceGrid, PlannerCompactField, PlannerField, PlannerInput, PlannerMultiSelectDropdown, PlannerTimeInput, PlannerToggleRow } from "./PlannerFields.jsx";

function PlannerSidebarSection({ title, children }) {
  return (
    <section className="dw-planner-form-section">
      <div className="dw-planner-form-section-title">{title}</div>
      <div className="dw-planner-form-section-body">{children}</div>
    </section>
  );
}

export default function PlannerSidebar({ sidebar, refs, actions }) {
  const { t } = useNativeScheduleI18n();
  const { dropdownPortalHostRef } = refs;
  const {
    adjustableLineOptions, adjustableLines, analysisEnd, analysisStart, analysisTimeError, dispatchInterval,
    dispatchMode, dispatchOptions, dispatchPhaseStart, dispatchTripsPerHour, existingExpressLine,
    expressLineOptions, expressSource, expressSourceOptions, expressStops, forcedBypassOptions,
    forcedOvertakes, generateDisabled, isGenerating, leftTab, localLineOptions, maxLocalShift,
    maxLocalWait, maxOvertakes, overtakesOptions, phaseAdjustmentRange, plannerLoadError,
    readonlyConstraintLineOptions, stationOptions, virtualBaseLine
  } = sidebar;

  return (
    <section className="dw-planner-sidebar">
      <div className="dw-planner-sidebar-tabs">
        <button type="button" className={"dw-planner-sidebar-tab " + (leftTab === "service" ? "is-active" : "")} onClick={() => actions.setLeftTab("service")}>{t("planner.left.service")}</button>
        <button type="button" className={"dw-planner-sidebar-tab " + (leftTab === "constraints" ? "is-active" : "")} onClick={() => actions.setLeftTab("constraints")}>{t("planner.left.constraints")}</button>
      </div>

      <WorkbenchScrollArea className="dw-planner-sidebar-scroll" metricsKey={[leftTab, expressSource, dispatchMode].join(":")}>
        <div className="dw-planner-sidebar-content">
          {leftTab === "service" ? (
            <>
              <PlannerSidebarSection title={t("planner.group.analysis")}>
                <div className="dw-planner-split-row is-balanced-pair">
                  <div className="dw-planner-split-cell"><PlannerField label={t("planner.field.analysisStart")}><PlannerTimeInput value={analysisStart} onCommit={actions.setAnalysisStart} onInvalidChange={actions.setAnalysisStartInvalid} placeholder="05:00" /></PlannerField></div>
                  <div className="dw-planner-split-cell"><PlannerField label={t("planner.field.analysisEnd")}><PlannerTimeInput value={analysisEnd} onCommit={actions.setAnalysisEnd} onInvalidChange={actions.setAnalysisEndInvalid} placeholder="09:00" /></PlannerField></div>
                </div>
                <div className={"dw-planner-field-error-slot is-reserved " + (analysisTimeError ? "has-error" : "")}>{analysisTimeError ? <div className="dw-planner-field-error">{analysisTimeError}</div> : null}</div>
                {plannerLoadError ? <div className="dw-planner-field-error">{plannerLoadError}</div> : null}
              </PlannerSidebarSection>

              <PlannerSidebarSection title={t("planner.group.planTarget")}>
                <PlannerToggleRow options={expressSourceOptions} value={expressSource} onChange={actions.setExpressSource} />
                {expressSource === "virtual" ? (
                  <PlannerField label={t("planner.field.basedOnLine")}>
                    <WorkbenchDropdown key="virtual-base-line" value={localLineOptions.find((option) => option.value === virtualBaseLine)?.label || ""} onSelect={actions.setVirtualBaseLine} options={localLineOptions.map((option) => ({ ...option, key: option.value, active: option.value === virtualBaseLine }))} className="dw-planner-dropdown-field" variant="field" positioning="portal" portalHostRef={dropdownPortalHostRef} />
                  </PlannerField>
                ) : (
                  <PlannerField label={t("planner.field.targetExpressLine")}>
                    <WorkbenchDropdown key="existing-express-line" value={expressLineOptions.find((option) => option.value === existingExpressLine)?.label || ""} onSelect={actions.setExistingExpressLine} options={expressLineOptions.map((option) => ({ ...option, key: option.value, active: option.value === existingExpressLine }))} className="dw-planner-dropdown-field" variant="field" positioning="portal" portalHostRef={dropdownPortalHostRef} />
                  </PlannerField>
                )}
                {expressSource === "virtual" ? <PlannerField label={t("planner.field.rapidStops")}><PlannerMultiSelectDropdown options={stationOptions} value={expressStops} onToggle={actions.toggleExpressStop} portalHostRef={dropdownPortalHostRef} /></PlannerField> : null}
              </PlannerSidebarSection>

              <PlannerSidebarSection title={t("planner.group.dispatch")}>
                <PlannerField label={t("planner.field.dispatchMode")}><WorkbenchDropdown key={`dispatch-${expressSource}`} value={dispatchOptions.find((option) => option.value === dispatchMode)?.label || ""} onSelect={actions.setDispatchMode} options={dispatchOptions} className="dw-planner-dropdown-field" variant="field" positioning="portal" portalHostRef={dropdownPortalHostRef} /></PlannerField>
                {expressSource === "virtual" && dispatchMode === "interval" ? <PlannerCompactField label={t("planner.field.rapidInterval")}><PlannerInput value={dispatchInterval} onChange={actions.setDispatchInterval} suffix={t("nativeSchedule.unit.minutes")} mode="numeric" /></PlannerCompactField> : null}
                {expressSource === "virtual" && dispatchMode === "frequency" ? <PlannerCompactField label={t("planner.field.tripsPerHour")}><PlannerInput value={dispatchTripsPerHour} onChange={actions.setDispatchTripsPerHour} suffix={t("planner.unit.tripsPerHour")} mode="numeric" /></PlannerCompactField> : null}
                {expressSource === "virtual" && dispatchMode === "phase" ? (
                  <div className="dw-planner-split-row is-balanced-pair">
                    <div className="dw-planner-split-cell"><PlannerCompactField label={t("planner.field.firstDeparturePhase")}><PlannerTimeInput value={dispatchPhaseStart} onCommit={actions.setDispatchPhaseStart} placeholder="05:00" /></PlannerCompactField></div>
                    <div className="dw-planner-split-cell"><PlannerCompactField label={t("planner.field.rapidInterval")}><PlannerInput value={dispatchInterval} onChange={actions.setDispatchInterval} suffix={t("nativeSchedule.unit.minutes")} mode="numeric" /></PlannerCompactField></div>
                  </div>
                ) : null}
                {expressSource === "existing" && dispatchMode === "shift" ? <PlannerCompactField label={t("planner.field.phaseAdjustmentRange")}><PlannerInput value={phaseAdjustmentRange} onChange={actions.setPhaseAdjustmentRange} suffix={t("nativeSchedule.unit.minutes")} mode="numeric" /></PlannerCompactField> : null}
                {expressSource === "existing" && dispatchMode === "reinterval" ? <PlannerCompactField label={t("planner.field.rapidInterval")}><PlannerInput value={dispatchInterval} onChange={actions.setDispatchInterval} suffix={t("nativeSchedule.unit.minutes")} mode="numeric" /></PlannerCompactField> : null}
              </PlannerSidebarSection>
            </>
          ) : (
            <>
              <PlannerSidebarSection title={t("planner.group.adjustment")}>
                <PlannerField label={t("planner.field.adjustableLines")}><PlannerMultiSelectDropdown options={adjustableLineOptions} value={adjustableLines} onToggle={actions.toggleAdjustableLine} portalHostRef={dropdownPortalHostRef} /></PlannerField>
                <PlannerField label={t("planner.field.backgroundConstraintLines")}>
                  <div className="dw-planner-readonly-lines">{readonlyConstraintLineOptions.length > 0 ? readonlyConstraintLineOptions.map((option) => <span key={option.value} className="dw-planner-readonly-line">{option.label}</span>) : <span className="dw-planner-readonly-empty">{t("planner.empty.noConstraintLines")}</span>}</div>
                </PlannerField>
                <div className="dw-planner-split-row is-balanced-pair">
                  <div className="dw-planner-split-cell"><PlannerField label={t("planner.field.maxShift")}><PlannerInput value={maxLocalShift} onChange={actions.setMaxLocalShift} suffix={t("nativeSchedule.unit.minutes")} mode="numeric" /></PlannerField></div>
                  <div className="dw-planner-split-cell"><PlannerField label={t("planner.field.maxWait")}><PlannerInput value={maxLocalWait} onChange={actions.setMaxLocalWait} suffix={t("nativeSchedule.unit.minutes")} mode="numeric" /></PlannerField></div>
                </div>
              </PlannerSidebarSection>
              <PlannerSidebarSection title={t("planner.group.bypassRules")}>
                <PlannerField label={t("planner.field.maxBypass")}><PlannerToggleRow options={overtakesOptions} value={maxOvertakes} onChange={actions.setMaxOvertakes} className="is-compact" /></PlannerField>
                <PlannerField label={t("planner.field.forcedStations")}><PlannerChoiceGrid options={forcedBypassOptions} value={forcedOvertakes} onToggle={actions.toggleForcedOvertake} /></PlannerField>
              </PlannerSidebarSection>
            </>
          )}
        </div>
      </WorkbenchScrollArea>
      <footer className="dw-planner-sidebar-foot"><button type="button" className={"dw-planner-primary-button dw-planner-generate-button " + (isGenerating ? "is-disabled" : "")} onClick={actions.generate} disabled={generateDisabled}>{isGenerating ? t("planner.button.generating") : t("planner.button.generate")}</button></footer>
    </section>
  );
}
