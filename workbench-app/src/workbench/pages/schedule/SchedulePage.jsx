import { memo } from "react";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import { NoLineIcon } from "./components/ScheduleIcons";
import ScheduleLineView from "./components/ScheduleLineView";
import ScheduleTopbar from "./components/ScheduleTopbar";
import SummarySection from "./components/SummarySection";
import useDepartureData from "./useDepartureData";
import useDepartureLoads from "./useDepartureLoads";
import useDepartureRecords from "./useDepartureRecords";

function SchedulePage({ controller, activeTransportMode = "train", isActive = false, onOpenRecord }) {
  const { t } = useNativeScheduleI18n();
  const { topbar, summary, line, refs, actions } = controller;
  const period = controller.auto.quickSegments[line.selectedSegment];
  const noLines = line.ready && topbar.lineOptions.length === 0;
  const lineActive = isActive && line.ready && !noLines && line.view === "line";
  const diagram = useDepartureData({
    mode: activeTransportMode,
    lineId: topbar.selectedLineId,
    isActive: lineActive,
    catalogRevision: line.catalogRevision,
    fromMinute: period?.start,
    toMinute: period?.end
  });
  const loads = useDepartureLoads({ mode: activeTransportMode, lineId: topbar.selectedLineId, isActive: lineActive });
  const records = useDepartureRecords({ lineId: topbar.selectedLineId, isActive: lineActive });

  return (
    <div className="dw-demo-page-root">
      <div className="dw-demo-shell dw-departure-shell">
        <div className="dw-departure-workspace">
        <ScheduleTopbar topbar={topbar} refs={refs} actions={actions} hideLine={line.view !== "all"} />
        <div className="dw-demo-main">
          {noLines ? <div className="dw-departure-empty">
            <div className="dw-departure-empty-icon-wrap"><NoLineIcon /></div>
            <div className="dw-departure-empty-title">{t("nativeSchedule.line.noLines")}</div>
          </div> : line.view === "all" ? <div className="dw-departure-summary-view">
          <SummarySection
            editingLineId={summary.editingLineId}
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
          </div> : <ScheduleLineView controller={controller} diagram={diagram} loads={loads} records={records} isActive={isActive} onOpenRecord={onOpenRecord} />}

        </div>
      </div>
      </div>
      <div ref={refs.dropdownPortalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}

export default memo(SchedulePage);
