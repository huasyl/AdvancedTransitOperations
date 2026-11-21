import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import AutoRuleSection from "./AutoRuleSection";
import ManualDraftSection from "./ManualDraftSection";
import CopyLineSection from "./CopyLineSection";

export default function ScheduleEditor({ controller, singleLine = false, onLocateSegment }) {
  const { t } = useNativeScheduleI18n();
  const { auto, manual, copy, line, refs, actions } = controller;
  const blocked = line.limitsError || line.quickConflict;
  const blockedNote = singleLine && blocked ? { tone: "error", text: blocked } : null;
  const quickFeedback = line.removedTimedCount > 0 ? t("nativeSchedule.line.removeTimed", { count: line.removedTimedCount }) : t("nativeSchedule.quick.replaceNotice");
  const lineNote = (line.limitsError ? { tone: "error", text: line.limitsError } : null) || line.message;
  return (
          <section className={`dw-demo-right ${singleLine ? "dw-departure-editor" : ""}`}>
            <div className="dw-demo-tabs">
              <button
                type="button"
                className={`dw-demo-tab ${auto.activeRightTab === "auto" ? "is-active" : ""}`}
                onClick={() => actions.setActiveRightTab("auto")}
              >
                {t("nativeSchedule.tab.auto")}
              </button>
              {singleLine ? (
                <>
                  <button
                    type="button"
                    className={`dw-demo-tab ${manual.activeRightTab === "manual" && line.manualMode === "single" ? "is-active" : ""}`}
                    onClick={() => { actions.setActiveRightTab("manual"); actions.setManualMode("single"); }}
                  >
                    {t("nativeSchedule.line.tab.single")}
                  </button>
                  <button
                    type="button"
                    className={`dw-demo-tab ${manual.activeRightTab === "manual" && line.manualMode === "multiple" ? "is-active" : ""}`}
                    onClick={() => { actions.setActiveRightTab("manual"); actions.setManualMode("multiple"); }}
                  >
                    {t("nativeSchedule.line.tab.multiple")}
                  </button>
                </>
              ) : (
                <button
                  type="button"
                  className={`dw-demo-tab ${manual.activeRightTab === "manual" ? "is-active" : ""}`}
                  onClick={() => actions.setActiveRightTab("manual")}
                >
                  {t("nativeSchedule.tab.manual")}
                </button>
              )}
              <button
                type="button"
                className={`dw-demo-tab ${copy.activeRightTab === "copy" ? "is-active" : ""}`}
                onClick={() => actions.setActiveRightTab("copy")}
              >
                {t("nativeSchedule.tab.copy")}
              </button>
            </div>

            {auto.activeRightTab === "auto" || (singleLine && manual.activeRightTab === "manual" && line.manualMode === "multiple") ? (
              <AutoRuleSection
                autoSubMode={singleLine ? (auto.activeRightTab === "auto" ? "quick" : "custom") : auto.autoSubMode}
                singleLine={singleLine}
                isApplying={line.isApplying}
                quickConflict={blocked}
                quickPending={line.quickPending}
                day={line.day}
                editDisabled={line.editDisabled}
                onApplyQuick={actions.applyQuickLine}
                selectedSegment={line.selectedSegment}
                onLocateSegment={onLocateSegment}
                displaySegments={auto.displaySegments}
                quickSegments={auto.quickSegments}
                quickFooterNote={blockedNote || auto.quickFooterNote || line.message || (singleLine && line.quickPending ? { text: quickFeedback } : null)}
                quickImportDisabled={auto.quickImportDisabled}
                quickImported={auto.quickImported}
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
                footerNote={auto.footerNote || lineNote}
                minimumScheduleMinutes={controller.topbar.minimumScheduleMinutes}
                editorEndInputRef={refs.editorEndInputRef}
                frequencyInputRef={refs.frequencyInputRef}
                onEditorStartChange={actions.changeEditorStart}
                onEditorEndChange={actions.changeEditorEnd}
                onChangeAutoSubMode={actions.setAutoSubMode}
                onAutoFrequencyChange={actions.changeAutoFrequency}
                onAutoOffsetDirectionChange={actions.changeAutoOffsetDirection}
                onAutoOffsetMinutesChange={actions.changeAutoOffsetMinutes}
                onAddAutoRule={actions.addAutoRule}
                onRemoveAutoRule={actions.removeAutoRule}
                onImportAutoToSummary={singleLine ? actions.applyAutoLine : actions.importAutoToSummary}
                onChangeQuickBoundary={actions.changeQuickBoundary}
                onChangeQuickCount={actions.changeQuickCount}
                onImportQuickToSummary={actions.importQuickToSummary}
              />
            ) : manual.activeRightTab === "manual" ? (
              <ManualDraftSection
                singleLine={singleLine}
                editDisabled={line.editDisabled}
                isApplying={line.isApplying}
                manualInput={manual.manualInput}
                manualInputRef={refs.manualInputRef}
                manualDrafts={manual.manualDrafts}
                manualInputError={manual.manualInputError}
                isAddManualDisabled={manual.isAddManualDisabled}
                footerNote={manual.footerNote || lineNote}
                onApplySingle={actions.applySingleLine}
                onManualInputChange={actions.changeManualInput}
                onAddManualDraft={actions.addManualDraft}
                onRemoveManualDraft={actions.removeManualDraft}
                onImportManualToSummary={actions.importManualToSummary}
              />
            ) : (
              <CopyLineSection
                singleLine={singleLine}
                editDisabled={line.editDisabled}
                isApplying={line.isApplying}
                copySourceLineId={copy.copySourceLineId}
                copySourceLabel={copy.copySourceLabel}
                copySourceOptions={copy.copySourceOptions}
                copyPreviewText={copy.copyPreviewText}
                copyEmptyText={copy.copyEmptyText}
                copyRows={copy.copyRows}
                copyImportDisabled={copy.copyImportDisabled}
                copyImported={copy.copyImported}
                footerNote={copy.footerNote || lineNote}
                dropdownPortalHostRef={refs.dropdownPortalHostRef}
                onCopySourceChange={actions.changeCopySource}
                onRefreshNames={actions.refreshNames}
                onImportCopyToSummary={singleLine ? actions.applyCopyLine : actions.importCopyToSummary}
                onRemoveCopyRow={actions.removeCopySourceRow}
              />
            )}
          </section>
  );
}
