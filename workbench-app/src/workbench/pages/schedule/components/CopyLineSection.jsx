import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import WorkbenchDropdown from "../../../shared/WorkbenchDropdown";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { DemoImportLeftIcon } from "./ScheduleIcons";

function formatRowNote(row, t) {
  if (row.skipped) {
    return {
      text: t(row.skipReason === "invalid" ? "nativeSchedule.copy.row.skippedInvalid" : "nativeSchedule.copy.row.skipped"),
      tone: "warning"
    };
  }

  if (row.shiftMinutes === 0) {
    return null;
  }

  return {
    text: t(
      row.shiftMinutes > 0 ? "nativeSchedule.copy.row.shiftLater" : "nativeSchedule.copy.row.shiftEarlier",
      { minutes: Math.abs(row.shiftMinutes) }
    ),
    tone: "warning"
  };
}

function CopySourceEditor({
  singleLine,
  editDisabled,
  copySourceLineId,
  copySourceLabel,
  copySourceOptions,
  copyPreviewText,
  dropdownPortalHostRef,
  onCopySourceChange,
  onRefreshNames
}) {
  const { t } = useNativeScheduleI18n();
  const sourceDropdown = (
    <WorkbenchDropdown
      label={t("nativeSchedule.copy.field.source")}
      disabled={editDisabled}
      value={copySourceLabel}
      options={copySourceOptions.map((line) => ({
        value: line.value,
        label: line.label,
        active: line.value === copySourceLineId
      }))}
      onSelect={onCopySourceChange}
      onOpen={onRefreshNames}
      className="is-line"
      variant="field"
      positioning="portal"
      portalHostRef={dropdownPortalHostRef}
    />
  );

  if (singleLine) {
    return (
      <div className="dw-departure-quick-segment">
        <div className="dw-departure-quick-controls">{sourceDropdown}</div>
        <div className="dw-departure-quick-result dw-departure-wide">
          <span className="dw-departure-quick-result-label">{t("nativeSchedule.copy.preview.tag")}</span>
          <span className="dw-departure-quick-result-times dw-departure-quick-text">{copyPreviewText}</span>
        </div>
        <div className="dw-departure-quick-feedback dw-departure-wide">{t("nativeSchedule.line.copyNotice")}</div>
      </div>
    );
  }

  return (
    <div className="dw-demo-rule-editor">
      <div className="dw-demo-rule-editor-row">
        {sourceDropdown}
      </div>

      <div className="dw-demo-preview-panel is-copy">
        <div className="dw-demo-preview-content">
          <div className="dw-demo-preview-inline is-window">
            <span className="dw-demo-preview-tag">{t("nativeSchedule.copy.preview.tag")}</span>
            <span className="dw-demo-preview-values-slot">
              <span className="dw-demo-preview-values">{copyPreviewText}</span>
            </span>
          </div>
          <div className="dw-demo-copy-replace-note">
            {t("nativeSchedule.copy.replaceNotice")}
          </div>
        </div>
      </div>
    </div>
  );
}

export default function CopyLineSection({
  singleLine = false,
  editDisabled = false,
  isApplying = false,
  copySourceLineId,
  copySourceLabel,
  copySourceOptions,
  copyPreviewText,
  copyRows,
  copyImportDisabled,
  copyImported,
  copyEmptyText,
  footerNote,
  dropdownPortalHostRef,
  onCopySourceChange,
  onRefreshNames,
  onImportCopyToSummary,
  onRemoveCopyRow
}) {
  const { t } = useNativeScheduleI18n();
  const copyGrid = (
    <div key={copySourceLineId || "copy-empty"} className="dw-demo-copy-grid">
      {copyRows.length === 0 && copyEmptyText ? (
        <div className="dw-demo-copy-empty">{copyEmptyText}</div>
      ) : null}
      {copyRows.map((row, index) => {
        const note = formatRowNote(row, t);
        return (
          <div
            key={row.key}
            className={`dw-demo-copy-item ${row.skipped ? "is-skipped" : ""}`}
            style={{ animationDelay: `${Math.min(Math.floor(index / 3), 5) * 70}ms` }}
          >
            <span
              className={`dw-demo-copy-times ${row.shiftMinutes === 0 ? "" : "is-retimed"}`}
              aria-label={row.shiftMinutes === 0
                ? row.sourceTime
                : t("nativeSchedule.copy.row.retimed", { from: row.sourceTime, to: row.time })}
            >
              {row.shiftMinutes === 0 ? (
                <span className="dw-demo-copy-time is-final" aria-hidden="true">{row.sourceTime}</span>
              ) : (
                <>
                  <span className="dw-demo-copy-time is-previous" aria-hidden="true">{row.sourceTime}</span>
                  <span className="dw-demo-copy-arrow" aria-hidden="true">→</span>
                  <span className="dw-demo-copy-time is-final" aria-hidden="true">{row.time}</span>
                </>
              )}
            </span>
            {note ? <span className={`dw-demo-copy-reason is-${note.tone}`}>{note.text}</span> : null}
            <button
              type="button"
              className={`dw-demo-copy-remove ${copyImported ? "is-disabled" : ""}`}
              onClick={() => onRemoveCopyRow(row.sourceRowId)}
              disabled={copyImported || editDisabled}
              aria-label={t("nativeSchedule.copy.row.remove")}
              title={t("nativeSchedule.copy.row.remove")}
            >
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M5 7h14M9 7V5h6v2m-8 0 1 12h6l1-12M10 11v5M14 11v5" fill="none" stroke="#8fa0aa" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </button>
          </div>
        );
      })}
    </div>
  );

  return (
    <div className="dw-demo-right-body">
      <CopySourceEditor
        singleLine={singleLine}
        editDisabled={editDisabled}
        copySourceLineId={copySourceLineId}
        copySourceLabel={copySourceLabel}
        copySourceOptions={copySourceOptions}
        copyPreviewText={copyPreviewText}
        dropdownPortalHostRef={dropdownPortalHostRef}
        onCopySourceChange={onCopySourceChange}
        onRefreshNames={onRefreshNames}
      />

      {singleLine ? (
        <div className="dw-departure-quick-segment">
          <div className="dw-departure-wide">{copyGrid}</div>
        </div>
      ) : (
        <WorkbenchScrollArea className="dw-demo-copy-list-scroll" metricsKey={copyRows.length}>
          {copyGrid}
        </WorkbenchScrollArea>
      )}

      {singleLine ? (
        <div className="dw-departure-quick-segment">
          <div className="dw-departure-quick-controls">
            <button type="button" className={`dw-departure-quick-apply ${copyImportDisabled || isApplying ? "is-disabled" : ""}`} onClick={onImportCopyToSummary} disabled={copyImportDisabled || isApplying}>{t("nativeSchedule.line.applyCopy")}</button>
            {footerNote ? <span className={`dw-departure-quick-message ${footerNote.tone === "error" ? "is-error" : ""}`}>{footerNote.text}</span> : null}
          </div>
        </div>
      ) : (
        <div className="dw-demo-footer">
          {footerNote ? <span className={`dw-demo-footer-note ${footerNote.tone ? `is-${footerNote.tone}` : ""}`}>{footerNote.text}</span> : <span />}
          <button type="button" className={`dw-demo-primary dw-demo-cta is-secondary ${copyImportDisabled || isApplying ? "is-disabled" : ""}`} onClick={onImportCopyToSummary} disabled={copyImportDisabled || isApplying}>
            <span className="dw-demo-button-content">
              <span className="dw-demo-button-icon-wrap" aria-hidden="true">
                <DemoImportLeftIcon />
              </span>
              <span>{t(copyImported ? "nativeSchedule.copy.button.imported" : "nativeSchedule.copy.button.import")}</span>
            </span>
          </button>
        </div>
      )}
    </div>
  );
}
