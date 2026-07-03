import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { DemoTextField } from "./ScheduleFields";
import { DemoImportLeftIcon } from "./ScheduleIcons";

export default function ManualDraftSection({
  manualInput,
  manualInputRef,
  manualDrafts,
  manualInputError,
  isAddManualDisabled,
  footerNote,
  onManualInputChange,
  onAddManualDraft,
  onRemoveManualDraft,
  onImportManualToSummary
}) {
  const { t } = useNativeScheduleI18n();

  return (
    <div className="dw-demo-right-body">
      <div className="dw-demo-rule-editor">
        <div className="dw-demo-rule-editor-row is-manual">
          <DemoTextField
            label={t("nativeSchedule.manual.field.departure")}
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
          <button type="button" className={`dw-demo-flat-button is-theme is-manual-add ${isAddManualDisabled ? "is-disabled" : ""}`} onClick={onAddManualDraft} disabled={isAddManualDisabled}>{t("nativeSchedule.manual.button.add")}</button>
        </div>
      </div>

      <WorkbenchScrollArea className="dw-demo-rule-list-scroll" metricsKey={manualDrafts.length}>
        {manualDrafts.map((draft) => (
          <div key={draft.id} className="dw-demo-manual-row">
            <div className="dw-demo-manual-meta">
              <span className="dw-demo-manual-time">{draft.time}</span>
              {draft.validation?.status !== "ok" ? (
                <span className={`dw-demo-manual-validation is-${draft.validation.status}`}>{draft.validation.message}</span>
              ) : null}
            </div>
            <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => onRemoveManualDraft(draft.id)}>{t("nativeSchedule.summary.action.remove")}</button>
          </div>
        ))}
      </WorkbenchScrollArea>

      <div className="dw-demo-footer">
        {footerNote ? <span className={`dw-demo-footer-note ${footerNote.tone ? `is-${footerNote.tone}` : ""}`}>{footerNote.text}</span> : <span />}
        <button type="button" className="dw-demo-primary dw-demo-cta is-secondary" onClick={onImportManualToSummary}>
          <span className="dw-demo-button-content">
            <span className="dw-demo-button-icon-wrap" aria-hidden="true">
              <DemoImportLeftIcon />
            </span>
            <span>{t("nativeSchedule.manual.button.import")}</span>
          </span>
        </button>
      </div>
    </div>
  );
}
