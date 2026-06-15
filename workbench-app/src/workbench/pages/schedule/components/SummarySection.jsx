import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import WorkbenchDropdown from "../../../shared/WorkbenchDropdown";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { SummaryBadge } from "./ScheduleFields";
import { DemoAlertIcon, DemoAppliedStateIcon, DemoEmptyScheduleIcon, DemoPlayIcon } from "./ScheduleIcons";
import { DemoSectionHeader } from "./ScheduleMessages";

function SummaryTable({
  rows,
  editableLineId,
  onRemoveRow,
  summaryScrollRef,
  summaryFilter,
  onSummaryFilterChange,
  dropdownPortalHostRef
}) {
  const { t, script } = useNativeScheduleI18n();
  const isLatin = script === "latin";

  return (
    <>
      <div className="dw-demo-summary-head">
        <div className="is-time">{t("nativeSchedule.summary.head.time")}</div>
        <WorkbenchDropdown
          value=""
          title={t("nativeSchedule.summary.filter.title")}
          options={[
            { value: "all", label: t("nativeSchedule.summary.filter.all"), active: summaryFilter === "all" },
            { value: "current", label: t("nativeSchedule.summary.filter.current"), active: summaryFilter === "current" },
            { value: "local", label: t("nativeSchedule.summary.filter.local"), active: summaryFilter === "local" },
            { value: "express", label: t("nativeSchedule.summary.filter.express"), active: summaryFilter === "express" }
          ]}
          onSelect={onSummaryFilterChange}
          className="is-line dw-demo-summary-head-line"
          variant="filter"
          positioning="portal"
          triggerClassName={`dw-demo-summary-head-filter-trigger ${summaryFilter !== "all" ? "is-filtered" : ""}`}
          menuClassName="dw-demo-summary-head-filter-menu"
          portalHostRef={dropdownPortalHostRef}
          menuWidth={108}
          triggerContent={(
            <>
              <span className="dw-demo-summary-head-filter-label">{t("nativeSchedule.summary.filter.label")}</span>
              <span className="dw-demo-summary-head-filter-caret" aria-hidden="true">
                <svg viewBox="0 0 16 16" className="dw-demo-summary-head-filter-icon">
                  <path className="dw-demo-summary-head-filter-path" d="M4.2 6.2 8 10l3.8-3.8" fill="none" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </span>
            </>
          )}
        />
        <div className="is-origin">{t("nativeSchedule.summary.head.origin")}</div>
        <div className="is-status">{t("nativeSchedule.summary.head.status")}</div>
        <div className="is-action" aria-hidden="true" />
      </div>

      <WorkbenchScrollArea className="dw-demo-summary-scroll" metricsKey={rows.length} externalScrollRef={summaryScrollRef}>
        <div className="dw-demo-summary-table">
          {rows.length === 0 ? (
            <div className="dw-demo-empty-state">
              <div className="dw-demo-empty-icon-wrap" aria-hidden="true">
                <DemoEmptyScheduleIcon />
              </div>
              <div className="dw-demo-empty-title">{t("nativeSchedule.summary.empty.title")}</div>
              <div className="dw-demo-empty-text">{t("nativeSchedule.summary.empty.body")}</div>
              <div className="dw-demo-empty-text">{t("nativeSchedule.summary.empty.next")}</div>
            </div>
          ) : (
            rows.map((row) => (
              <div key={row.id} className={`dw-demo-summary-row ${row.isConflict ? "is-conflict" : ""} ${row.isExpress ? "is-express" : ""}`}>
                <div className="is-time">{row.time}</div>
                <div className="is-line">
                  <span
                    className={`dw-demo-dot ${row.isConflict ? "is-conflict" : ""}`}
                    style={row.isConflict ? undefined : { backgroundColor: row.lineColor || undefined }}
                  />
                  <div className="dw-demo-line-meta">
                    <div className="dw-demo-line-meta-top">
                      <span className="dw-demo-line-name">{row.lineName}</span>
                      <SummaryBadge kind={row.kind} />
                    </div>
                  </div>
                </div>
                <div className="is-origin dw-demo-origin-cell">
                  {row.origin}
                </div>
                <div className="is-status">
                  {row.isConflict && isLatin ? (
                    <div className="dw-demo-status-stack is-conflict">
                      <span className="dw-demo-status-text is-conflict">
                        {t("nativeSchedule.summary.status.conflictTitle")}
                      </span>
                      <span className="dw-demo-status-subtext is-conflict">
                        {row.conflictReasonLabel || t("nativeSchedule.summary.status.conflictUnknown")}
                      </span>
                    </div>
                  ) : (
                    <span className={`dw-demo-status-text ${row.isConflict ? "is-conflict" : row.isApplied ? "is-applied" : "is-pending"}`}>
                      {row.isConflict
                        ? t("nativeSchedule.summary.status.conflict.compact", {
                          reason: row.conflictReasonLabel || t("nativeSchedule.summary.status.conflictUnknown")
                        })
                        : row.isApplied
                          ? t("nativeSchedule.summary.status.applied")
                          : t("nativeSchedule.summary.status.pending")}
                    </span>
                  )}
                </div>
                <div className="is-action">
                  <button
                    type="button"
                    className="dw-demo-link-danger dw-demo-row-action"
                    onClick={() => onRemoveRow(row.id)}
                  >
                    {t("nativeSchedule.summary.action.remove")}
                  </button>
                </div>
              </div>
            ))
          )}
        </div>
      </WorkbenchScrollArea>
    </>
  );
}

export default function SummarySection({
  summaryStateLabel,
  hasAppliedSchedule,
  summaryRows,
  editableLineId,
  earliestStart,
  conflictCount,
  summaryFilter,
  onSummaryFilterChange,
  summaryScrollRef,
  onRemoveRow,
  onClearSummary,
  onApplySchedule,
  onLocateConflict,
  isApplyingSchedule,
  footerNote,
  dropdownPortalHostRef
}) {
  const { t } = useNativeScheduleI18n();
  const hasConflicts = conflictCount > 0;

  return (
    <section className="dw-demo-left">
      <DemoSectionHeader
        title={summaryStateLabel}
        applied={hasAppliedSchedule}
        metrics={(
          <div className="dw-demo-summary-metrics-group">
            <span>{t("nativeSchedule.summary.metric.total", { count: summaryRows.length })}</span>
            <span>{t("nativeSchedule.summary.metric.earliest", { time: earliestStart })}</span>
            <span>{t("nativeSchedule.summary.metric.conflict", { count: conflictCount })}</span>
          </div>
        )}
      />

      <SummaryTable
        rows={summaryRows}
        editableLineId={editableLineId}
        summaryScrollRef={summaryScrollRef}
        summaryFilter={summaryFilter}
        onSummaryFilterChange={onSummaryFilterChange}
        onRemoveRow={onRemoveRow}
        dropdownPortalHostRef={dropdownPortalHostRef}
      />

      <div className="dw-demo-footer">
        <button type="button" className="dw-demo-flat-button is-muted" onClick={onClearSummary}>{t("nativeSchedule.summary.action.clear")}</button>
        {footerNote ? <span className={`dw-demo-footer-note ${footerNote.tone ? `is-${footerNote.tone}` : ""}`}>{footerNote.text}</span> : null}
        <button
          type="button"
          className={`dw-demo-primary dw-demo-cta ${hasConflicts ? "is-conflict" : hasAppliedSchedule ? "is-applied" : ""}${isApplyingSchedule ? " is-loading" : ""}`}
          disabled={isApplyingSchedule}
          onClick={hasConflicts ? onLocateConflict : onApplySchedule}
        >
          <span className="dw-demo-button-content">
            <span className="dw-demo-button-icon-wrap" aria-hidden="true">
              {hasConflicts ? <DemoAlertIcon /> : hasAppliedSchedule ? <DemoAppliedStateIcon /> : <DemoPlayIcon />}
            </span>
            <span>{isApplyingSchedule ? t("nativeSchedule.summary.action.applying") : hasConflicts ? t("nativeSchedule.summary.action.locateConflict") : hasAppliedSchedule ? t("nativeSchedule.summary.action.applied") : t("nativeSchedule.summary.action.apply")}</span>
          </span>
        </button>
      </div>
    </section>
  );
}
