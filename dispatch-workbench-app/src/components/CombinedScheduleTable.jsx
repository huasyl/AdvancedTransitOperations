import { useState } from "react";
import { ControlText, SafeControlText } from "./ChoiceButtons";
import { useI18n } from "../lib/i18n";

export default function CombinedScheduleTable({
  rows = [],
  selectedEditLine,
  previewSummary,
  fallbackOriginStationName = "",
  actionMessage,
  onApply,
  onClearCurrentLine,
  onRemoveRow,
  readOnly = false
}) {
  const { locale } = useI18n();
  const [filterMode, setFilterMode] = useState("all");
  const conflictCount = rows.filter((row) => row.isConflict).length;
  const filteredRows = rows.filter((row) => {
    if (filterMode === "current") {
      return row.lineId === selectedEditLine;
    }
    if (filterMode === "express") {
      return row.kind === "express";
    }
    return true;
  });
  const visiblePreviewCount = filteredRows.length;
  const visibleEarliestStart = filteredRows[0]?.time || previewSummary?.earliestStart || "--:--";

  function handleApplyClick() {
    if (readOnly) {
      return;
    }

    if (conflictCount > 0) {
      return;
    }
    onApply?.();
  }

  return (
    <section className="dw-panel">
      <div className="dw-panel-header">
        <SafeControlText>{locale === "zh-CN" ? "汇总时刻表" : "Staged Timetable"}</SafeControlText>
      </div>
      <div className="dw-panel-body dw-schedule-merged-body">
        <div className="dw-chip-row is-schedule-help">
          <span className="dw-chip is-warn">
            <ControlText>
              {locale === "zh-CN"
                ? "左右两侧只是草稿。进入这里后立即按始发站、线路类型和时间检测冲突。"
                : "The side columns are drafts only. Conflicts are checked here immediately by origin station, line type, and departure time."}
            </ControlText>
          </span>
        </div>

        <div className="dw-schedule-summary-strip">
          <div className="dw-schedule-summary-box">
            <span className="dw-metric-label">
              <SafeControlText>{locale === "zh-CN" ? "汇总行数" : "Staged rows"}</SafeControlText>
            </span>
            <strong className="dw-metric-value">{rows.length}</strong>
          </div>
          <div className="dw-schedule-summary-box">
            <span className="dw-metric-label">
              <SafeControlText>{locale === "zh-CN" ? "当前预览" : "Preview"}</SafeControlText>
            </span>
            <strong className="dw-metric-value">{visiblePreviewCount}</strong>
          </div>
          <div className="dw-schedule-summary-box">
            <span className="dw-metric-label">
              <SafeControlText>{locale === "zh-CN" ? "最早发车" : "Earliest start"}</SafeControlText>
            </span>
            <strong className="dw-metric-value">{visibleEarliestStart}</strong>
          </div>
          <div className="dw-schedule-summary-box">
            <span className="dw-metric-label">
              <SafeControlText>{locale === "zh-CN" ? "冲突" : "Conflicts"}</SafeControlText>
            </span>
            <strong className={`dw-metric-value ${conflictCount > 0 ? "is-bad" : "is-good"}`}>
              {conflictCount}
            </strong>
          </div>
        </div>

        <div className="dw-schedule-toolbar is-merged">
          <button
            type="button"
            className={`dw-btn ${filterMode === "all" ? "dw-btn-primary" : ""}`}
            onClick={() => setFilterMode("all")}
          >
            <ControlText>{locale === "zh-CN" ? "查看全部" : "Show all"}</ControlText>
          </button>
          <button
            type="button"
            className={`dw-btn ${filterMode === "current" ? "dw-btn-primary" : ""}`}
            onClick={() => setFilterMode("current")}
          >
            <ControlText>{locale === "zh-CN" ? "当前线路" : "Current line"}</ControlText>
          </button>
          <button
            type="button"
            className={`dw-btn ${filterMode === "express" ? "dw-btn-primary" : ""}`}
            onClick={() => setFilterMode("express")}
          >
            <ControlText>{locale === "zh-CN" ? "仅快车" : "Express only"}</ControlText>
          </button>
        </div>

        <div className="dw-table-wrap is-schedule-merged">
          <div className="dw-grid-table dw-grid-table-schedule">
            <div className="dw-grid-row is-head">
              <div className="dw-grid-cell is-time">
                <SafeControlText>{locale === "zh-CN" ? "时间" : "Time"}</SafeControlText>
              </div>
              <div className="dw-grid-cell is-line">
                <SafeControlText>{locale === "zh-CN" ? "线路" : "Line"}</SafeControlText>
              </div>
              <div className="dw-grid-cell is-service">
                <SafeControlText>{locale === "zh-CN" ? "线路类型" : "Line type"}</SafeControlText>
              </div>
              <div className="dw-grid-cell is-source">
                <SafeControlText>{locale === "zh-CN" ? "来源" : "Source"}</SafeControlText>
              </div>
              <div className="dw-grid-cell is-note">
                <SafeControlText>{locale === "zh-CN" ? "始发站" : "Origin"}</SafeControlText>
              </div>
              <div className="dw-grid-cell is-action">
                <SafeControlText>{locale === "zh-CN" ? "操作" : "Action"}</SafeControlText>
              </div>
            </div>

            {filteredRows.length === 0 ? (
              <div className="dw-grid-row is-empty">
                <div className="dw-grid-cell is-empty-cell">
                  <SafeControlText>
                    {locale === "zh-CN" ? "汇总区还没有行，请先从左右草稿加入。" : "The staged timetable is empty. Add rows from the draft panels first."}
                  </SafeControlText>
                </div>
              </div>
            ) : null}

            {filteredRows.map((row) => (
              <div key={row.id} className={`dw-grid-row ${row.kind === "express" ? "is-express-row" : ""} ${row.isConflict ? "is-invalid-row" : ""}`}>
                <div className="dw-grid-cell is-time">
                  <SafeControlText>{row.time}</SafeControlText>
                </div>
                <div className="dw-grid-cell is-line">
                  <span className="dw-line-row-chip">
                    <span className="dw-line-row-swatch" style={{ backgroundColor: row.lineColor || "#4f90a8" }} />
                    <SafeControlText>{row.lineName}</SafeControlText>
                  </span>
                </div>
                <div className="dw-grid-cell is-service">
                  <span className={`dw-status-pill ${row.kind === "express" ? "is-warn" : "is-good"}`}>
                    <ControlText>{row.kind === "express" ? (locale === "zh-CN" ? "快车" : "Express") : locale === "zh-CN" ? "普通" : "Local"}</ControlText>
                  </span>
                </div>
                <div className="dw-grid-cell is-source">
                  <SafeControlText>{row.source}</SafeControlText>
                </div>
                <div className="dw-grid-cell is-note">
                  <SafeControlText>{row.originStationName || (row.lineId === selectedEditLine ? fallbackOriginStationName : "") || (locale === "zh-CN" ? "待刷新" : "-")}</SafeControlText>
                </div>
                <div className="dw-grid-cell is-action">
                  <button type="button" className="dw-link-btn dw-btn-danger" onClick={() => onRemoveRow?.(row.id)} disabled={readOnly}>
                    <ControlText>{locale === "zh-CN" ? "删除" : "Delete"}</ControlText>
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>

        {actionMessage ? (
          <div className="dw-inline-notice">
            <SafeControlText>{actionMessage}</SafeControlText>
          </div>
        ) : null}

        {conflictCount > 0 ? (
          <div className="dw-inline-notice is-bad">
            <SafeControlText>
              {locale === "zh-CN"
                ? "同一始发站下，5 分钟内的发车会视为冲突；同始发站的重复条目也需要先清理。"
                : "Departures from the same origin station within 5 minutes are treated as conflicts. Resolve same-origin duplicates first."}
            </SafeControlText>
          </div>
        ) : null}

        <div className="dw-schedule-toolbar is-merged-bottom">
          <button type="button" className="dw-btn" onClick={onClearCurrentLine} disabled={readOnly}>
            <ControlText>{locale === "zh-CN" ? "移除当前线路" : "Remove current line"}</ControlText>
          </button>
          <button type="button" className="dw-btn dw-btn-primary is-toolbar-end" onClick={handleApplyClick} disabled={readOnly || conflictCount > 0}>
            <ControlText>{locale === "zh-CN" ? "应用时刻表" : "Apply timetable"}</ControlText>
          </button>
        </div>
      </div>
    </section>
  );
}







