import { useMemo, useState } from "react";
import { useI18n } from "../lib/i18n";
import { normalizeCompactTimeInput } from "../lib/time";
import { validateManualRows } from "../lib/validation";
import { ControlText, SafeControlText } from "./ChoiceButtons";
import TimeInput from "./TimeInput";

const emptyDraft = {
  time: ""
};

export default function ManualTimetableEditor({
  rows = [],
  setRows,
  selectedEditLine,
  selectedLineName,
  currentLineKind = "local",
  onAddToStaged,
  readOnly = false
}) {
  const { locale } = useI18n();
  const [draft, setDraft] = useState(emptyDraft);
  const rowsForLine = useMemo(
    () => rows.filter((row) => row.lineId === selectedEditLine),
    [rows, selectedEditLine]
  );
  const validatedRows = useMemo(
    () => validateManualRows(rowsForLine, keyForLocale(locale)),
    [rowsForLine, locale]
  );

  function addDraftRow() {
    if (!selectedEditLine) {
      return;
    }

    setRows((current) => [
      ...current,
      {
        id: `row-${Date.now()}`,
        lineId: selectedEditLine,
        time: draft.time,
        kind: currentLineKind,
        offsetMode: "none",
        offsetMinutes: ""
      }
    ]);
    setDraft(emptyDraft);
  }

  function sortCurrentLine() {
    setRows((current) => {
      const next = [...current];
      const lineRows = next.filter((row) => row.lineId === selectedEditLine).sort((left, right) =>
        left.time.localeCompare(right.time)
      );
      let cursor = 0;
      return next.map((row) => (row.lineId === selectedEditLine ? lineRows[cursor++] : row));
    });
  }

  function clearCurrentLine() {
    setRows((current) => current.filter((row) => row.lineId !== selectedEditLine));
  }

  function updateRow(id, patch) {
    setRows((current) => current.map((row) => (row.id === id ? { ...row, ...patch } : row)));
  }

  function removeRow(id) {
    setRows((current) => current.filter((row) => row.id !== id));
  }

  return (
    <section className="dw-panel">
      <div className="dw-panel-header">
        <SafeControlText>{locale === "zh-CN" ? "手工草稿" : "Manual Draft"}</SafeControlText>
      </div>
      <div className="dw-panel-body dw-schedule-draft-body">
        <div className="dw-schedule-focus-line">
          <span className="dw-schedule-focus-label">
            <SafeControlText>{locale === "zh-CN" ? "当前线路" : "Current line"}</SafeControlText>
          </span>
          <strong className="dw-schedule-focus-value">
            <SafeControlText>{selectedLineName}</SafeControlText>
          </strong>
        </div>

        <div className="dw-chip-row is-schedule-help">
          <span className="dw-chip is-warn">
            <ControlText>
              {locale === "zh-CN"
                ? "先整理当前线路的手工发车草稿，再加入中间汇总。"
                : "Prepare manual departures for the current line, then add them into the staged timetable."}
            </ControlText>
          </span>
        </div>

        <div className="dw-schedule-toolbar">
          <button type="button" className="dw-btn dw-btn-primary" onClick={addDraftRow} disabled={readOnly}>
            <ControlText>{locale === "zh-CN" ? "添加草稿行" : "Add draft row"}</ControlText>
          </button>
          <button type="button" className="dw-btn" onClick={sortCurrentLine} disabled={readOnly}>
            <ControlText>{locale === "zh-CN" ? "排序当前线路" : "Sort current line"}</ControlText>
          </button>
          <button type="button" className="dw-btn" onClick={clearCurrentLine} disabled={readOnly}>
            <ControlText>{locale === "zh-CN" ? "清空当前线路" : "Clear current line"}</ControlText>
          </button>
          <button type="button" className="dw-btn is-toolbar-end" onClick={() => onAddToStaged?.(rowsForLine)} disabled={readOnly}>
            <ControlText>{locale === "zh-CN" ? "添加到时刻表" : "Add to timetable"}</ControlText>
          </button>
        </div>

        <div className="dw-manual-input-row is-compact">
          <div className="dw-field dw-manual-field is-departure">
            <label>
              <SafeControlText>{locale === "zh-CN" ? "发车" : "Departure"}</SafeControlText>
            </label>
            <TimeInput
              placeholder="HH:mm"
              value={draft.time}
              readOnly={readOnly}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  time: normalizeCompactTimeInput(event.target.value)
                }))
              }
            />
          </div>
        </div>

        <div className="dw-table-wrap is-schedule-draft">
          <div className="dw-grid-table dw-grid-table-manual">
            <div className="dw-grid-row is-head">
              <div className="dw-grid-cell is-departure">
                <SafeControlText>{locale === "zh-CN" ? "发车" : "Departure"}</SafeControlText>
              </div>
              <div className="dw-grid-cell is-service">
                <SafeControlText>{locale === "zh-CN" ? "线路类型" : "Line type"}</SafeControlText>
              </div>
              <div className="dw-grid-cell is-status">
                <SafeControlText>{locale === "zh-CN" ? "状态" : "Status"}</SafeControlText>
              </div>
              <div className="dw-grid-cell is-action">
                <SafeControlText>{locale === "zh-CN" ? "操作" : "Action"}</SafeControlText>
              </div>
            </div>

            {validatedRows.length === 0 ? (
              <div className="dw-grid-row is-empty">
                <div className="dw-grid-cell is-empty-cell">
                  <SafeControlText>
                    {locale === "zh-CN" ? "当前线路还没有手工草稿行。" : "No manual draft rows for the current line."}
                  </SafeControlText>
                </div>
              </div>
            ) : null}

            {validatedRows.map((row) => (
              <div
                key={row.id}
                className={`dw-grid-row ${row.validation.status === "error" ? "is-invalid-row" : ""}`}
              >
                <div className="dw-grid-cell is-departure">
                  <TimeInput
                    value={row.time}
                    readOnly={readOnly}
                    onChange={(event) =>
                      updateRow(row.id, {
                        time: normalizeCompactTimeInput(event.target.value)
                      })
                    }
                  />
                </div>
                <div className="dw-grid-cell is-service">
                  <span className={`dw-status-pill ${row.kind === "express" ? "is-warn" : "is-good"}`}>
                    <ControlText>
                      {row.kind === "express"
                        ? locale === "zh-CN"
                          ? "快车"
                          : "Express"
                        : locale === "zh-CN"
                          ? "普通"
                          : "Local"}
                    </ControlText>
                  </span>
                </div>
                <div className="dw-grid-cell is-status">
                  <span
                    className={`dw-status-pill ${
                      row.validation.status === "error"
                        ? "is-bad"
                        : row.validation.status === "warning"
                          ? "is-warn"
                          : "is-good"
                    }`}
                  >
                    <ControlText>{row.validation.message}</ControlText>
                  </span>
                </div>
                <div className="dw-grid-cell is-action">
                  <button type="button" className="dw-link-btn dw-btn-danger" onClick={() => removeRow(row.id)} disabled={readOnly}>
                    <ControlText>{locale === "zh-CN" ? "删除" : "Delete"}</ControlText>
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}

function keyForLocale(locale) {
  const zh = locale === "zh-CN";
  return (key) => {
    const map = {
      "validation.ok": zh ? "正常" : "OK",
      "validation.error.timeFormat": zh ? "时间必须使用 HH:mm 格式。" : "Time must use HH:mm format.",
      "validation.error.duplicate": zh ? "同一线路类型存在重复发车时间。" : "Duplicate departure minute for the same line type.",
      "validation.error.order": zh ? "各行必须保持时间升序。" : "Rows must remain in ascending time order.",
      "validation.error.offsetInteger": zh ? "偏移分钟必须是非负整数。" : "Offset minutes must be a non-negative integer.",
      "validation.warning.offsetIgnored": zh ? "手工草稿不再使用偏移。" : "Offset values are ignored in the manual draft."
    };
    return map[key] || key;
  };
}

