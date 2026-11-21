import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { DemoTextField } from "./ScheduleFields";
import { DemoImportLeftIcon } from "./ScheduleIcons";

export default function ManualDraftSection({
  singleLine = false,
  isApplying = false,
  editDisabled = false,
  manualInput,
  manualInputRef,
  manualDrafts,
  manualInputError,
  isAddManualDisabled,
  footerNote,
  onApplySingle,
  onManualInputChange,
  onAddManualDraft,
  onRemoveManualDraft,
  onImportManualToSummary
}) {
  const { t } = useNativeScheduleI18n();
  const manualField = (
    <DemoTextField
      label={t(singleLine ? "nativeSchedule.line.departureTimes" : "nativeSchedule.manual.field.departure")}
      readOnly={editDisabled}
      value={manualInput}
      onCommit={onManualInputChange}
      onDraftChange={onManualInputChange}
      className="is-manual-time"
      inputRef={manualInputRef}
      timeMode
      errorText={manualInputError}
      preserveInvalidTime
      reserveErrorSpace
    />
  );
  const addButton = <button type="button" className={`dw-demo-flat-button is-theme is-manual-add ${isAddManualDisabled || editDisabled ? "is-disabled" : ""}`} onClick={onAddManualDraft} disabled={isAddManualDisabled || editDisabled}>{t("nativeSchedule.manual.button.add")}</button>;
  const draftRows = manualDrafts.map((draft) => (
    <div key={draft.id} className="dw-demo-manual-row">
      <div className="dw-demo-manual-meta">
        <span className="dw-demo-manual-time">{draft.time}</span>
        {draft.validation?.status !== "ok" ? (
          <span className={`dw-demo-manual-validation is-${draft.validation.status}`}>{draft.validation.message}</span>
        ) : null}
      </div>
      <button type="button" className="dw-demo-link-danger dw-demo-row-action" disabled={editDisabled} onClick={() => onRemoveManualDraft(draft.id)}>{t("nativeSchedule.summary.action.remove")}</button>
    </div>
  ));

  return (
    <div className="dw-demo-right-body">
      {singleLine ? (
        <div className="dw-departure-quick-segment">
          <div className="dw-departure-quick-controls is-manual">
            {manualField}
            <button type="button" className={`dw-departure-quick-apply ${isAddManualDisabled || editDisabled || isApplying ? "is-disabled" : ""}`} disabled={isAddManualDisabled || editDisabled || isApplying} onClick={onApplySingle}>{t("nativeSchedule.line.addApply")}</button>
            {footerNote ? <span className={`dw-departure-quick-message ${footerNote.tone === "error" ? "is-error" : ""}`}>{footerNote.text}</span> : null}
          </div>
        </div>
      ) : (
        <>
          <div className="dw-demo-rule-editor">
            <div className="dw-demo-rule-editor-row is-manual">
              {manualField}
              {addButton}
            </div>
          </div>

          <WorkbenchScrollArea className="dw-demo-rule-list-scroll" metricsKey={manualDrafts.length}>
            {draftRows}
          </WorkbenchScrollArea>

          <div className="dw-demo-footer">
            {footerNote ? <span className={`dw-demo-footer-note ${footerNote.tone ? `is-${footerNote.tone}` : ""}`}>{footerNote.text}</span> : <span />}
            <button type="button" className={`dw-demo-primary dw-demo-cta is-secondary ${isApplying ? "is-disabled" : ""}`} disabled={isApplying} onClick={onImportManualToSummary}>
              <span className="dw-demo-button-content">
                <span className="dw-demo-button-icon-wrap" aria-hidden="true">
                  <DemoImportLeftIcon />
                </span>
                <span>{t("nativeSchedule.manual.button.import")}</span>
              </span>
            </button>
          </div>
        </>
      )}
    </div>
  );
}
