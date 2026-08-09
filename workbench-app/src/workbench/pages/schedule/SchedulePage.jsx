import { memo } from "react";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import useScheduleController from "./useScheduleController";
import AutoRuleSection from "./components/AutoRuleSection";
import ManualDraftSection from "./components/ManualDraftSection";
import ScheduleTopbar from "./components/ScheduleTopbar";
import SummarySection from "./components/SummarySection";

function SchedulePage({ registerHostActions, activeTransportMode = "train", isActive = false }) {
  const { t } = useNativeScheduleI18n();
  const { topbar, summary, auto, manual, refs, actions } = useScheduleController({
    registerHostActions,
    activeTransportMode,
    isActive
  });

  return (
    <div className="dw-demo-page-root">
      <div className="dw-demo-shell">
        <ScheduleTopbar topbar={topbar} refs={refs} actions={actions} />
        <div className="dw-demo-main">
          <SummarySection
            summaryStateLabel={summary.summaryStateLabel}
            hasAppliedSchedule={summary.hasAppliedSchedule}
            summaryRows={summary.rows}
            editableLineId={summary.editableLineId}
            earliestStart={summary.earliestStart}
            conflictCount={summary.conflictCount}
            summaryFilter={summary.summaryFilter}
            supportsExpress={summary.supportsExpress}
            onSummaryFilterChange={actions.setSummaryFilter}
            summaryScrollRef={refs.summaryScrollRef}
            onRemoveRow={actions.removeSummaryRow}
            onClearSummary={actions.clearSummaryTable}
            onApplySchedule={actions.applySchedule}
            onLocateConflict={actions.locateConflict}
            isApplyingSchedule={summary.isApplyingSchedule}
            footerNote={summary.footerNote}
            dropdownPortalHostRef={refs.dropdownPortalHostRef}
          />

          <section className="dw-demo-right">
            <div className="dw-demo-tabs">
              <button
                type="button"
                className={`dw-demo-tab ${auto.activeRightTab === "auto" ? "is-active" : ""}`}
                onClick={() => actions.setActiveRightTab("auto")}
              >
                {t("nativeSchedule.tab.auto")}
              </button>
              <button
                type="button"
                className={`dw-demo-tab ${manual.activeRightTab === "manual" ? "is-active" : ""}`}
                onClick={() => actions.setActiveRightTab("manual")}
              >
                {t("nativeSchedule.tab.manual")}
              </button>
            </div>

            {auto.activeRightTab === "auto" ? (
              <AutoRuleSection
                editorStart={auto.editorStart}
                editorEnd={auto.editorEnd}
                autoFrequencyText={auto.autoFrequencyText}
                autoFrequencyPerHour={auto.autoFrequencyPerHour}
                selectedLineType={auto.selectedLineType}
                supportsExpress={auto.supportsExpress}
                autoOffsetDirection={auto.autoOffsetDirection}
                autoOffsetMinutesText={auto.autoOffsetMinutesText}
                liveAutoPreview={auto.liveAutoPreview}
                autoRules={auto.autoRules}
                footerNote={auto.footerNote}
                editorEndInputRef={refs.editorEndInputRef}
                frequencyInputRef={refs.frequencyInputRef}
                onEditorStartChange={actions.changeEditorStart}
                onEditorEndChange={actions.changeEditorEnd}
                onAutoFrequencyChange={actions.changeAutoFrequency}
                onAutoOffsetDirectionChange={actions.changeAutoOffsetDirection}
                onAutoOffsetMinutesChange={actions.changeAutoOffsetMinutes}
                onAddAutoRule={actions.addAutoRule}
                onRemoveAutoRule={actions.removeAutoRule}
                onImportAutoToSummary={actions.importAutoToSummary}
              />
            ) : (
              <ManualDraftSection
                manualInput={manual.manualInput}
                manualInputRef={refs.manualInputRef}
                manualDrafts={manual.manualDrafts}
                manualInputError={manual.manualInputError}
                isAddManualDisabled={manual.isAddManualDisabled}
                footerNote={manual.footerNote}
                onManualInputChange={actions.changeManualInput}
                onAddManualDraft={actions.addManualDraft}
                onRemoveManualDraft={actions.removeManualDraft}
                onImportManualToSummary={actions.importManualToSummary}
              />
            )}
          </section>
        </div>
      </div>
      <div ref={refs.dropdownPortalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}

export default memo(SchedulePage);
