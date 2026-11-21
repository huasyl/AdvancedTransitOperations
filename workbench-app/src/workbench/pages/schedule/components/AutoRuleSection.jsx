import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { DemoOffsetField, DemoTextField } from "./ScheduleFields";
import { DemoImportLeftIcon } from "./ScheduleIcons";
import LineApplyButton from "./LineApplyButton";
import {
  DemoRuleTextPreviewTimes,
  DemoTopPreviewTimes,
  getRulePreviewMaxItemsPerRow,
  getTopPreviewMaxItemsPerRow
} from "./ScheduleMessages";
import { quickMinutesToTime } from "../../../../lib/auto-schedule";

function QuickStepper({ value, decreaseDisabled, increaseDisabled, onDecrease, onIncrease, ariaLabel, valueClassName = "" }) {
  return (
    <div className="dw-demo-quick-stepper">
      <button
        type="button"
        className={`dw-demo-quick-stepper-button ${decreaseDisabled ? "is-disabled" : ""}`}
        onClick={onDecrease}
        disabled={decreaseDisabled}
        aria-label={ariaLabel}
      >
        −
      </button>
      <span className={`dw-demo-quick-stepper-value ${valueClassName}`}>{value}</span>
      <button
        type="button"
        className={`dw-demo-quick-stepper-button ${increaseDisabled ? "is-disabled" : ""}`}
        onClick={onIncrease}
        disabled={increaseDisabled}
        aria-label={ariaLabel}
      >
        +
      </button>
    </div>
  );
}

function QuickSegment({ segment, index, script, onChangeBoundary, onChangeCount, beforePreview, singleLine = false, applyButton }) {
  const { t } = useNativeScheduleI18n();
  const maxItemsPerRow = getRulePreviewMaxItemsPerRow(script, false);
  const label = t(segment.labelKey);
  const startStepper = <QuickStepper value={quickMinutesToTime(segment.start)} decreaseDisabled={segment.startMinusDisabled} increaseDisabled={segment.startPlusDisabled} onDecrease={() => onChangeBoundary(index, "start", -1)} onIncrease={() => onChangeBoundary(index, "start", 1)} ariaLabel={`${label} ${t("nativeSchedule.quick.action.time")}`} />;
  const endStepper = <QuickStepper value={quickMinutesToTime(segment.end)} decreaseDisabled={segment.endMinusDisabled} increaseDisabled={segment.endPlusDisabled} onDecrease={() => onChangeBoundary(index, "end", -1)} onIncrease={() => onChangeBoundary(index, "end", 1)} ariaLabel={`${label} ${t("nativeSchedule.quick.action.time")}`} />;
  const countStepper = <QuickStepper value={segment.count} valueClassName="is-count" decreaseDisabled={segment.countMinusDisabled} increaseDisabled={segment.countPlusDisabled} onDecrease={() => onChangeCount(index, -1)} onIncrease={() => onChangeCount(index, 1)} ariaLabel={`${label} ${t("nativeSchedule.quick.action.count")}`} />;
  const preview = <DemoRuleTextPreviewTimes entries={segment.entries} showSkipped moveSkippedToEnd maxItemsPerRow={maxItemsPerRow} />;
  if (singleLine) return <div className="dw-demo-quick-segment dw-departure-quick-segment">
    <div className="dw-departure-quick-controls">
      <div className="dw-departure-quick-control"><span>{t("nativeSchedule.auto.field.start")}</span>{startStepper}</div>
      <div className="dw-departure-quick-control"><span>{t("nativeSchedule.auto.field.end")}</span>{endStepper}</div>
      <div className="dw-departure-quick-control"><span>{t("nativeSchedule.quick.field.count")}</span>{countStepper}</div>
      {applyButton}
    </div>
    <div className="dw-departure-quick-result"><span className="dw-departure-quick-result-label">{t("nativeSchedule.line.departureTimes")}</span><span className="dw-departure-quick-result-times">{preview}</span></div>
  </div>;
  return (
    <div className="dw-demo-quick-segment">
      <div className="dw-demo-quick-time-row">
        <span className="dw-demo-quick-segment-label">{singleLine ? t("nativeSchedule.auto.field.start") : label}</span>
        {startStepper}
        <span className={singleLine ? "dw-demo-quick-segment-label is-end" : "dw-demo-quick-to"}>{t(singleLine ? "nativeSchedule.auto.field.end" : "nativeSchedule.quick.separator.to")}</span>
        {endStepper}
      </div>
      <div className="dw-demo-quick-count-row">
        <span className="dw-demo-quick-segment-label is-count">{t("nativeSchedule.quick.field.count")}</span>
        {countStepper}
      </div>
      {beforePreview}
      <div className="dw-demo-quick-preview-row">
        <span className="dw-demo-quick-preview-label">{t("nativeSchedule.quick.field.preview")}</span>
        <span className="dw-demo-quick-preview-values">
          {preview}
        </span>
      </div>
    </div>
  );
}

function QuickAddSection({
  quickSegments,
  footerNote,
  quickImportDisabled,
  quickImported,
  script,
  onChangeBoundary,
  onChangeCount,
  onImport
}) {
  const { t } = useNativeScheduleI18n();
  return (
    <>
      <WorkbenchScrollArea className="dw-demo-quick-segment-scroll" metricsKey={quickSegments.length}>
        {quickSegments.map((segment, index) => (
          <QuickSegment
            key={segment.id}
            segment={segment}
            index={index}
            script={script}
            onChangeBoundary={onChangeBoundary}
            onChangeCount={onChangeCount}
          />
        ))}
      </WorkbenchScrollArea>
      <div className="dw-demo-footer">
        {footerNote ? <span className={`dw-demo-footer-note ${footerNote.tone ? `is-${footerNote.tone}` : ""}`}>{footerNote.text}</span> : <span />}
        <button
          type="button"
          className={`dw-demo-primary dw-demo-cta is-secondary ${quickImportDisabled ? "is-disabled" : ""}`}
          onClick={onImport}
          disabled={quickImportDisabled}
        >
          <span className="dw-demo-button-content">
            <span className="dw-demo-button-icon-wrap" aria-hidden="true">
              <DemoImportLeftIcon />
            </span>
            <span>{t(quickImported ? "nativeSchedule.quick.button.imported" : "nativeSchedule.quick.button.import")}</span>
          </span>
        </button>
      </div>
    </>
  );
}

function AutoRuleEditor({
  singleLine,
  editDisabled,
  addLabelKey,
  addDisabled,
  editorStart,
  editorEnd,
  autoFrequencyText,
  autoFrequencyPerHour,
  showOffsetField,
  autoOffsetDirection,
  autoOffsetMinutesText,
  liveAutoPreview,
  footerNote,
  editorEndInputRef,
  frequencyInputRef,
  onEditorStartChange,
  onEditorEndChange,
  onAutoFrequencyChange,
  onAutoOffsetDirectionChange,
  onAutoOffsetMinutesChange,
  onAddAutoRule
}) {
  const { t, script } = useNativeScheduleI18n();
  const topPreviewMaxItemsPerRow = getTopPreviewMaxItemsPerRow(script, Boolean(liveAutoPreview.meta));
  const startField = (
    <DemoTextField
      label={t("nativeSchedule.auto.field.start")}
      readOnly={editDisabled}
      value={editorStart}
      onCommit={onEditorStartChange}
      onDraftChange={onEditorStartChange}
      className="is-window-start"
      timeMode
      preserveInvalidTime
      nextInputRef={editorEndInputRef}
    />
  );
  const endField = (
    <DemoTextField
      label={t("nativeSchedule.auto.field.end")}
      readOnly={editDisabled}
      value={editorEnd}
      onCommit={onEditorEndChange}
      onDraftChange={onEditorEndChange}
      className="is-window-end"
      timeMode
      preserveInvalidTime
      inputRef={editorEndInputRef}
      nextInputRef={frequencyInputRef}
    />
  );
  const rateField = (
    <DemoTextField
      label={t("nativeSchedule.auto.field.rate")}
      readOnly={editDisabled}
      value={autoFrequencyText}
      onCommit={onAutoFrequencyChange}
      onDraftChange={onAutoFrequencyChange}
      className="is-rate"
      inputRef={frequencyInputRef}
    />
  );
  const offsetField = showOffsetField ? (
    <DemoOffsetField
      label={t("nativeSchedule.auto.field.offset")}
      disabled={editDisabled}
      direction={autoOffsetDirection}
      minutes={autoOffsetMinutesText}
      onDirectionChange={onAutoOffsetDirectionChange}
      onMinutesChange={onAutoOffsetMinutesChange}
      className="is-offset"
      hint={t("nativeSchedule.auto.field.offsetHint")}
    />
  ) : null;

  if (singleLine) {
    return (
      <div className="dw-departure-quick-segment">
        <div className="dw-departure-quick-controls">
          {startField}
          {endField}
          {rateField}
          {offsetField}
          <button type="button" className={`dw-departure-quick-apply ${addDisabled ? "is-disabled" : ""}`} disabled={addDisabled} onClick={onAddAutoRule}>{t(addLabelKey)}</button>
          {footerNote ? <span className={`dw-departure-quick-message ${footerNote.tone === "error" ? "is-error" : ""}`}>{footerNote.text}</span> : null}
        </div>
        <div className="dw-departure-quick-result"><span className="dw-departure-quick-result-label">{t("nativeSchedule.auto.preview.tag")}</span><span className="dw-departure-quick-result-times"><DemoRuleTextPreviewTimes entries={liveAutoPreview.entries} showSkipped moveSkippedToEnd maxItemsPerRow={getRulePreviewMaxItemsPerRow(script, false)} /></span></div>
        {liveAutoPreview.meta ? <div className="dw-departure-quick-feedback dw-departure-wide">{liveAutoPreview.meta}</div> : null}
      </div>
    );
  }

  return (
    <div className="dw-demo-rule-editor">
      <div className="dw-demo-rule-editor-row">
        {startField}
        {endField}
        {rateField}
        {offsetField}
      </div>

      <div className="dw-demo-preview-panel">
        <div className="dw-demo-preview-content">
          <div className="dw-demo-preview-inline is-window">
            <span className="dw-demo-preview-tag">{t("nativeSchedule.auto.preview.tag")}</span>
            <span className="dw-demo-preview-values-slot">
              <span className="dw-demo-preview-values"><DemoTopPreviewTimes times={liveAutoPreview.times} maxItemsPerRow={topPreviewMaxItemsPerRow} /></span>
            </span>
            {liveAutoPreview.meta ? <span className="dw-demo-preview-meta is-inline">{liveAutoPreview.meta}</span> : null}
          </div>
        </div>
        <div className="dw-demo-preview-spacer is-rate" />
        <div className="dw-demo-preview-spacer is-offset" />
        <button type="button" className="dw-demo-flat-button is-theme" disabled={addDisabled} onClick={onAddAutoRule}>{t(addLabelKey)}</button>
      </div>
    </div>
  );
}

function AutoRuleTable({
  editDisabled,
  autoRules,
  showOffsetColumn,
  onRemoveAutoRule
}) {
  const { t, script } = useNativeScheduleI18n();
  const rulePreviewMaxItemsPerRow = getRulePreviewMaxItemsPerRow(script, showOffsetColumn);

  return (
    <>
      <div className={`dw-demo-rule-list-head ${showOffsetColumn ? "is-with-offset" : "is-no-offset"}`}>
        <div className="is-window">{t("nativeSchedule.auto.table.window")}</div>
        <div className="is-rate">{t("nativeSchedule.auto.table.rate")}</div>
        {showOffsetColumn ? <div className="is-offset">{t("nativeSchedule.auto.table.offset")}</div> : null}
        <div className="is-action">{t("nativeSchedule.auto.table.action")}</div>
      </div>

      <WorkbenchScrollArea className="dw-demo-rule-list-scroll" metricsKey={autoRules.length}>
        {autoRules.map((rule) => (
          <div key={rule.id} className="dw-demo-rule-row">
            <div className={`dw-demo-rule-row-main ${showOffsetColumn ? "is-with-offset" : "is-no-offset"}`}>
              <div className="is-window">
                <span className="dw-demo-rule-window">{rule.windowLabel}</span>
              </div>
              <div className="is-rate">
                <span className="dw-demo-rule-rate">{rule.rateLabel}</span>
              </div>
              {showOffsetColumn ? (
                <div className="is-offset">
                  <span className="dw-demo-rule-offset">{rule.offsetLabel}</span>
                </div>
              ) : null}
              <div className="is-action">
                <button type="button" className="dw-demo-link-danger dw-demo-row-action" disabled={editDisabled} onClick={() => onRemoveAutoRule(rule.id)}>{t("nativeSchedule.summary.action.remove")}</button>
              </div>
            </div>
            <div className="dw-demo-rule-preview">
              <span className="dw-demo-preview-values is-rule"><DemoRuleTextPreviewTimes entries={rule.previewEntries} showSkipped moveSkippedToEnd maxItemsPerRow={rulePreviewMaxItemsPerRow} /></span>
              {rule.previewMeta ? <span className="dw-demo-rule-preview-meta">{rule.previewMeta}</span> : null}
            </div>
          </div>
        ))}
      </WorkbenchScrollArea>
    </>
  );
}

function AutoModeHeader({ autoSubMode, onChangeAutoSubMode }) {
  const { t } = useNativeScheduleI18n();
  return (
    <div className={`dw-demo-quick-mode-head ${autoSubMode === "custom" ? "is-custom" : ""}`}>
      <div className="dw-demo-quick-mode-switch">
        <button
          type="button"
          className={`dw-demo-quick-mode-button ${autoSubMode === "quick" ? "is-active" : ""}`}
          onClick={() => onChangeAutoSubMode("quick")}
          aria-pressed={autoSubMode === "quick"}
        >
          {t("nativeSchedule.quick.mode.quick")}
        </button>
        <button
          type="button"
          className={`dw-demo-quick-mode-button ${autoSubMode === "custom" ? "is-active" : ""}`}
          onClick={() => onChangeAutoSubMode("custom")}
          aria-pressed={autoSubMode === "custom"}
        >
          {t("nativeSchedule.quick.mode.custom")}
        </button>
      </div>
      <div className="dw-demo-quick-mode-note">
        {autoSubMode === "quick" ? t("nativeSchedule.quick.replaceNotice") : null}
      </div>
    </div>
  );
}

export default function AutoRuleSection({
  autoSubMode,
  singleLine = false,
  editDisabled = false,
  isApplying = false,
  quickConflict = "",
  quickPending,
  day,
  onApplyQuick,
  selectedSegment = 0,
  displaySegments = [],
  onLocateSegment,
  quickSegments,
  quickFooterNote,
  quickImportDisabled,
  quickImported,
  editorStart,
  editorEnd,
  autoFrequencyText,
  autoFrequencyPerHour,
  selectedLineType,
  supportsExpress,
  autoOffsetDirection,
  autoOffsetMinutesText,
  liveAutoPreview,
  autoRules,
  footerNote,
  minimumScheduleMinutes,
  editorEndInputRef,
  frequencyInputRef,
  onEditorStartChange,
  onEditorEndChange,
  onChangeAutoSubMode,
  onAutoFrequencyChange,
  onAutoOffsetDirectionChange,
  onAutoOffsetMinutesChange,
  onAddAutoRule,
  onRemoveAutoRule,
  onImportAutoToSummary,
  onChangeQuickBoundary,
  onChangeQuickCount,
  onImportQuickToSummary
}) {
  const { t, script } = useNativeScheduleI18n();
  const quickApplyButton = <LineApplyButton isApplying={isApplying} conflict={quickConflict} pending={quickPending} count={day.count} message={quickFooterNote} onApply={onApplyQuick} />;

  return (
    <div className="dw-demo-right-body">
      {!singleLine ? <AutoModeHeader autoSubMode={autoSubMode} onChangeAutoSubMode={onChangeAutoSubMode} /> : null}
      {autoSubMode === "quick" ? (
        singleLine ? selectedSegment < 0 ? <>
          <div className="dw-departure-selected-head">
            <div><h2>{t("nativeSchedule.line.allDay")}</h2>{day.count ? <span>{day.first} – {day.last}</span> : null}<span>{t("nativeSchedule.line.count", { count: day.count })}</span></div>
          </div>
          <div className="dw-departure-period-hint">{t("nativeSchedule.line.pickPeriod")}</div>
          <div className="dw-departure-quick-segment">
            <div className="dw-departure-quick-controls">{quickApplyButton}</div>
          </div>
        </> : <>
          <div className="dw-departure-selected-head">
            <div><h2>{t(quickSegments[selectedSegment].labelKey)}</h2><span>{quickMinutesToTime(quickSegments[selectedSegment].start)} – {quickMinutesToTime(quickSegments[selectedSegment].end)}</span><span>{t("nativeSchedule.line.count", { count: displaySegments[selectedSegment].entries.filter((entry) => !entry.skipped).length })}</span></div>
            <button type="button" className="dw-demo-flat-button is-theme" onClick={onLocateSegment}>{t("nativeSchedule.line.locate")}</button>
          </div>
          <QuickSegment segment={{ ...quickSegments[selectedSegment], entries: displaySegments[selectedSegment].entries }} index={selectedSegment} script={script} onChangeBoundary={onChangeQuickBoundary} onChangeCount={onChangeQuickCount} singleLine
            applyButton={quickApplyButton}
          />
          {displaySegments[selectedSegment].entries.some((entry) => entry.skipped) ? <div className="dw-departure-skip-note">{t("nativeSchedule.preview.meta.skipped", { count: displaySegments[selectedSegment].entries.filter((entry) => entry.skipped).length })}<span>{t("nativeSchedule.preview.reason.gap", { minutes: minimumScheduleMinutes })}</span></div> : null}
        </> : <QuickAddSection
          quickSegments={quickSegments}
          footerNote={quickFooterNote}
          quickImportDisabled={quickImportDisabled}
          quickImported={quickImported}
          script={script}
          onChangeBoundary={onChangeQuickBoundary}
          onChangeCount={onChangeQuickCount}
          onImport={onImportQuickToSummary}
        />
      ) : (
        <>
          <AutoRuleEditor
            singleLine={singleLine}
            editDisabled={editDisabled}
            addLabelKey={singleLine ? "nativeSchedule.line.addApply" : "nativeSchedule.auto.button.add"}
            addDisabled={editDisabled || (singleLine && isApplying)}
            editorStart={editorStart}
            editorEnd={editorEnd}
            autoFrequencyText={autoFrequencyText}
            autoFrequencyPerHour={autoFrequencyPerHour}
            showOffsetField={supportsExpress && selectedLineType === "express"}
            autoOffsetDirection={autoOffsetDirection}
            autoOffsetMinutesText={autoOffsetMinutesText}
            liveAutoPreview={liveAutoPreview}
            footerNote={footerNote}
            editorEndInputRef={editorEndInputRef}
            frequencyInputRef={frequencyInputRef}
            onEditorStartChange={onEditorStartChange}
            onEditorEndChange={onEditorEndChange}
            onAutoFrequencyChange={onAutoFrequencyChange}
            onAutoOffsetDirectionChange={onAutoOffsetDirectionChange}
            onAutoOffsetMinutesChange={onAutoOffsetMinutesChange}
            onAddAutoRule={singleLine ? onImportAutoToSummary : onAddAutoRule}
          />

          {singleLine ? null : (
            <>
              <AutoRuleTable
                editDisabled={editDisabled}
                autoRules={autoRules}
                showOffsetColumn={supportsExpress && selectedLineType === "express"}
                onRemoveAutoRule={onRemoveAutoRule}
              />

              <div className="dw-demo-footer">
                {footerNote ? <span className={`dw-demo-footer-note ${footerNote.tone ? `is-${footerNote.tone}` : ""}`}>{footerNote.text}</span> : <span />}
                <button type="button" className={`dw-demo-primary dw-demo-cta is-secondary ${isApplying ? "is-disabled" : ""}`} disabled={isApplying} onClick={onImportAutoToSummary}>
                  <span className="dw-demo-button-content">
                    <span className="dw-demo-button-icon-wrap" aria-hidden="true">
                      <DemoImportLeftIcon />
                    </span>
                    <span>{t("nativeSchedule.auto.button.import")}</span>
                  </span>
                </button>
              </div>
            </>
          )}
        </>
      )}
    </div>
  );
}
