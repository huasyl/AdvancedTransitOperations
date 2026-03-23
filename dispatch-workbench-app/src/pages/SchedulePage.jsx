import { useEffect, useMemo, useState } from "react";
import AutoScheduleRuleEditor from "../components/AutoScheduleRuleEditor";
import CombinedScheduleTable from "../components/CombinedScheduleTable";
import ManualTimetableEditor from "../components/ManualTimetableEditor";
import { ChoiceButtons, ControlText, SafeControlText } from "../components/ChoiceButtons";
import { useI18n } from "../lib/i18n";

export default function SchedulePage({
  shellMode,
  manualRows,
  setManualRows,
  autoRules,
  setAutoRules,
  stagedRows,
  lines,
  depots = [],
  stationOptions = [],
  mergedView,
  selectedEditLine,
  setSelectedEditLine,
  combinedRows,
  previewSummary,
  autoPreviewPlan,
  onApplyDraft,
  onAddManualToStaged,
  onAddAutoToStaged,
  onClearStagedLine,
  onRemoveStagedRow,
  onOriginHoldLimitChange,
  onSelectedLineKindChange,
  onAllowedDepotChange,
  onMaxStationDwellChange,
  saveState
}) {
  const { locale } = useI18n();
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [depotDropdownOpen, setDepotDropdownOpen] = useState(false);
  const [actionMessage, setActionMessage] = useState("");
  const selectedLine = useMemo(
    () => lines.find((line) => line.id === selectedEditLine) ?? lines[0] ?? null,
    [lines, selectedEditLine]
  );
  const currentLineKind = useMemo(() => {
    const expressLineIds = Array.isArray(mergedView?.expressLineIds)
      ? mergedView.expressLineIds
      : mergedView?.expressLineId
        ? [mergedView.expressLineId]
        : [];
    return expressLineIds.includes(selectedEditLine) ? "express" : "local";
  }, [mergedView, selectedEditLine]);
  const selectedOriginStationName = selectedLine?.originStationName || stationOptions[0]?.name || (locale === "zh-CN" ? "始发站未加载" : "Origin pending");
  const availableDepots = useMemo(() => {
    if (!selectedLine?.transportType) {
      return depots;
    }

    return depots.filter((depot) => !depot.transportType || depot.transportType === selectedLine.transportType);
  }, [depots, selectedLine?.transportType]);
  const [draftLineKind, setDraftLineKind] = useState(currentLineKind);
  const [originHoldInput, setOriginHoldInput] = useState(String(selectedLine?.originHoldLimitMinutes ?? 20));
  const [maxStationDwellInput, setMaxStationDwellInput] = useState(String(selectedLine?.maxStationDwellMinutes ?? 10));

  useEffect(() => {
    if (saveState?.message) {
      setActionMessage(saveState.message);
    }
  }, [saveState]);

  useEffect(() => {
    setDraftLineKind(currentLineKind);
  }, [currentLineKind, selectedEditLine]);

  useEffect(() => {
    setOriginHoldInput(String(selectedLine?.originHoldLimitMinutes ?? 20));
  }, [selectedLine?.id, selectedLine?.originHoldLimitMinutes]);

  useEffect(() => {
    setMaxStationDwellInput(String(selectedLine?.maxStationDwellMinutes ?? 10));
  }, [selectedLine?.id, selectedLine?.maxStationDwellMinutes]);

  function commitOriginHoldLimit() {
    const normalizedValue =
      Number.isFinite(Number(originHoldInput)) && Number(originHoldInput) > 0
        ? Math.max(1, Math.min(120, Math.round(Number(originHoldInput))))
        : 20;
    setOriginHoldInput(String(normalizedValue));
    onOriginHoldLimitChange?.(selectedEditLine, normalizedValue);
  }

  function commitMaxStationDwell() {
    const normalizedValue =
      Number.isFinite(Number(maxStationDwellInput)) && Number(maxStationDwellInput) > 0
        ? Math.max(1, Math.min(120, Math.round(Number(maxStationDwellInput))))
        : 10;
    setMaxStationDwellInput(String(normalizedValue));
    onMaxStationDwellChange?.(selectedEditLine, normalizedValue);
  }

  return (
    <div className={`dw-page-grid is-schedule is-shell-${shellMode}`}>
      <div className="dw-col-main">
        <div className="dw-panel dw-schedule-page-toolbar-panel">
          <div className="dw-panel-body dw-schedule-page-toolbar">
            <div className="dw-field dw-schedule-page-line-field">
              <label>
                <SafeControlText>{locale === "zh-CN" ? "编辑线路" : "Editing line"}</SafeControlText>
              </label>
              <div
                className="dw-line-dropdown"
                onBlur={(event) => {
                  if (!event.currentTarget.contains(event.relatedTarget)) {
                    setDropdownOpen(false);
                  }
                }}
              >
                <button
                  type="button"
                  className={`dw-line-dropdown-trigger ${dropdownOpen ? "is-open" : ""}`}
                  title={selectedLine?.rawName || selectedLine?.name || ""}
                  onClick={() => setDropdownOpen((current) => !current)}
                >
                  <ControlText>{selectedLine?.name || (locale === "zh-CN" ? "选择线路" : "Select line")}</ControlText>
                  <span className="dw-line-dropdown-caret" aria-hidden="true">
                    v
                  </span>
                </button>
                {dropdownOpen ? (
                  <div className="dw-line-dropdown-menu" role="listbox">
                    {lines.map((line) => (
                      <button
                        key={line.id}
                        type="button"
                        className={`dw-line-dropdown-option ${line.id === selectedEditLine ? "is-active" : ""}`}
                        title={line.rawName || line.name}
                        onClick={() => {
                          setSelectedEditLine(line.id);
                          setDropdownOpen(false);
                        }}
                      >
                        <ControlText>{line.name}</ControlText>
                      </button>
                    ))}
                  </div>
                ) : null}
              </div>
            </div>

            <div className="dw-field dw-schedule-page-kind-field">
              <label>
                <SafeControlText>{locale === "zh-CN" ? "线路类型" : "Line type"}</SafeControlText>
              </label>
              <ChoiceButtons
                options={[
                  { value: "local", label: locale === "zh-CN" ? "普通" : "Local" },
                  { value: "express", label: locale === "zh-CN" ? "快车" : "Express" }
                ]}
                value={draftLineKind}
                onChange={(nextValue) => {
                  setDraftLineKind(nextValue);
                  onSelectedLineKindChange?.(nextValue);
                }}
              />
            </div>
            <div className="dw-field dw-schedule-page-depot-field">
              <label>
                <SafeControlText>Allowed depot</SafeControlText>
              </label>
              <div
                className="dw-line-dropdown"
                onBlur={(event) => {
                  if (!event.currentTarget.contains(event.relatedTarget)) {
                    setDepotDropdownOpen(false);
                  }
                }}
              >
                <button
                  type="button"
                  className={`dw-line-dropdown-trigger ${depotDropdownOpen ? "is-open" : ""}`}
                  title={availableDepots.find((depot) => depot.id === selectedLine?.allowedDepotId)?.name || ""}
                  onClick={() => setDepotDropdownOpen((current) => !current)}
                >
                  <ControlText>
                    {availableDepots.find((depot) => depot.id === selectedLine?.allowedDepotId)?.name || "Any depot"}
                  </ControlText>
                  <span className="dw-line-dropdown-caret" aria-hidden="true">
                    v
                  </span>
                </button>
                {depotDropdownOpen ? (
                  <div className="dw-line-dropdown-menu" role="listbox">
                    <button
                      type="button"
                      className={`dw-line-dropdown-option ${!selectedLine?.allowedDepotId ? "is-active" : ""}`}
                      onClick={() => {
                        onAllowedDepotChange?.(selectedEditLine, "");
                        setDepotDropdownOpen(false);
                      }}
                    >
                      <ControlText>Any depot</ControlText>
                    </button>
                    {availableDepots.map((depot) => (
                      <button
                        key={depot.id}
                        type="button"
                        className={`dw-line-dropdown-option ${depot.id === selectedLine?.allowedDepotId ? "is-active" : ""}`}
                        title={depot.name}
                        onClick={() => {
                          onAllowedDepotChange?.(selectedEditLine, depot.id);
                          setDepotDropdownOpen(false);
                        }}
                      >
                        <ControlText>{depot.name}</ControlText>
                      </button>
                    ))}
                  </div>
                ) : null}
              </div>
            </div>
            <div className="dw-field dw-schedule-page-origin-field">
              <label>
                <SafeControlText>{locale === "zh-CN" ? "始发站" : "Origin"}</SafeControlText>
              </label>
              <div className="dw-static-help dw-schedule-page-origin-value">
                <SafeControlText>{selectedOriginStationName}</SafeControlText>
              </div>
            </div>
            <div className="dw-field dw-schedule-page-hold-field">
              <label>
                <SafeControlText>{locale === "zh-CN" ? "候车窗口 / 分钟" : "Hold window / min"}</SafeControlText>
              </label>
              <input
                type="text"
                inputMode="numeric"
                value={originHoldInput}
                onChange={(event) => setOriginHoldInput(event.target.value.replace(/[^0-9]/g, ""))}
                onBlur={commitOriginHoldLimit}
              />
            </div>
            <div className="dw-field dw-schedule-page-dwell-field">
              <label>
                <SafeControlText>{locale === "zh-CN" ? "最长停站时间" : "Max dwell / min"}</SafeControlText>
              </label>
              <input
                type="text"
                inputMode="numeric"
                value={maxStationDwellInput}
                onChange={(event) => setMaxStationDwellInput(event.target.value.replace(/[^0-9]/g, ""))}
                onBlur={commitMaxStationDwell}
              />
            </div>
          </div>
        </div>

        <div className="dw-schedule-board">
          <div className="dw-schedule-board-col is-left">
            <ManualTimetableEditor
              rows={manualRows}
              setRows={setManualRows}
              selectedEditLine={selectedEditLine}
              selectedLineName={selectedLine?.name || (locale === "zh-CN" ? "未选择线路" : "No line selected")}
              currentLineKind={draftLineKind}
              onAddToStaged={onAddManualToStaged}
            />
          </div>

          <div className="dw-schedule-board-col is-center">
            <CombinedScheduleTable
              rows={combinedRows}
              selectedEditLine={selectedEditLine}
              previewSummary={previewSummary}
              fallbackOriginStationName={selectedOriginStationName}
              actionMessage={actionMessage}
              onApply={onApplyDraft}
              onClearCurrentLine={onClearStagedLine}
              onRemoveRow={onRemoveStagedRow}
              stagedRows={stagedRows}
            />
          </div>

          <div className="dw-schedule-board-col is-right">
            <AutoScheduleRuleEditor
              rules={autoRules}
              setRules={setAutoRules}
              selectedEditLine={selectedEditLine}
              selectedLineName={selectedLine?.name || (locale === "zh-CN" ? "未选择线路" : "No line selected")}
              selectedLineKind={draftLineKind}
              previewPlan={autoPreviewPlan}
              onAddToStaged={onAddAutoToStaged}
            />
          </div>
        </div>
      </div>
    </div>
  );
}
