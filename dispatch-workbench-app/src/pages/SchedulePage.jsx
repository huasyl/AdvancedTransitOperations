import { useEffect, useMemo, useState } from "react";
import AutoScheduleRuleEditor from "../components/AutoScheduleRuleEditor";
import CombinedScheduleTable from "../components/CombinedScheduleTable";
import ManualTimetableEditor from "../components/ManualTimetableEditor";
import { ChoiceButtons, ControlText, SafeControlText } from "../components/ChoiceButtons";
import { useI18n } from "../lib/i18n";

const MIN_LINE_SETTING_MINUTES = 5;

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
  onRefreshMetadata,
  saveState,
  isReadonly = false
}) {
  const { locale, t } = useI18n();
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [depotDropdownOpen, setDepotDropdownOpen] = useState(false);
  const [actionMessage, setActionMessage] = useState("");
  const selectedLine = useMemo(
    () => lines.find((line) => line.id === selectedEditLine) ?? lines[0] ?? null,
    [lines, selectedEditLine]
  );
  const currentLineKind = useMemo(
    () => (selectedLine?.kind === "express" ? "express" : "local"),
    [selectedLine?.kind]
  );
  const selectedOriginStationName = selectedLine?.originStationName || stationOptions[0]?.name || t("schedule.originPending");
  const availableDepots = useMemo(() => {
    if (!selectedLine?.transportType) {
      return depots;
    }

    return depots.filter((depot) => !depot.transportType || depot.transportType === selectedLine.transportType);
  }, [depots, selectedLine?.transportType]);
  const selectedDepotName = useMemo(
    () => depots.find((depot) => depot.id === selectedLine?.allowedDepotId)?.name || "",
    [depots, selectedLine?.allowedDepotId]
  );
  const [draftLineKind, setDraftLineKind] = useState(currentLineKind);
  const [originHoldInput, setOriginHoldInput] = useState(String(selectedLine?.originHoldLimitMinutes ?? 20));
  const [maxStationDwellInput, setMaxStationDwellInput] = useState(String(selectedLine?.maxStationDwellMinutes ?? 10));
  const originHoldInputValue = Number(originHoldInput);
  const maxStationDwellInputValue = Number(maxStationDwellInput);
  const originHoldTooSmall =
    originHoldInput !== "" && Number.isFinite(originHoldInputValue) && originHoldInputValue < MIN_LINE_SETTING_MINUTES;
  const maxStationDwellTooSmall =
    maxStationDwellInput !== "" && Number.isFinite(maxStationDwellInputValue) && maxStationDwellInputValue < MIN_LINE_SETTING_MINUTES;

  useEffect(() => {
    setActionMessage(saveState?.message || "");
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
    if (isReadonly) {
      setOriginHoldInput(String(selectedLine?.originHoldLimitMinutes ?? 20));
      return;
    }

    if (originHoldTooSmall) {
      return;
    }
    const normalizedValue =
      Number.isFinite(Number(originHoldInput)) && Number(originHoldInput) > 0
        ? Math.max(MIN_LINE_SETTING_MINUTES, Math.min(120, Math.round(Number(originHoldInput))))
        : 20;
    setOriginHoldInput(String(normalizedValue));
    onOriginHoldLimitChange?.(selectedEditLine, normalizedValue);
  }

  function commitMaxStationDwell() {
    if (isReadonly) {
      setMaxStationDwellInput(String(selectedLine?.maxStationDwellMinutes ?? 10));
      return;
    }

    if (maxStationDwellTooSmall) {
      return;
    }
    const normalizedValue =
      Number.isFinite(Number(maxStationDwellInput)) && Number(maxStationDwellInput) > 0
        ? Math.max(MIN_LINE_SETTING_MINUTES, Math.min(120, Math.round(Number(maxStationDwellInput))))
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
                <SafeControlText>{t("schedule.editingLine")}</SafeControlText>
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
                  onClick={() => {
                    if (!dropdownOpen) {
                      onRefreshMetadata?.();
                    }
                    setDropdownOpen((current) => !current);
                  }}
                >
                  <ControlText>{selectedLine?.name || t("schedule.selectLine")}</ControlText>
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
                <SafeControlText>{t("schedule.lineType")}</SafeControlText>
              </label>
              <ChoiceButtons
                options={[
                  { value: "local", label: locale === "zh-CN" ? "\u666e\u901a" : "Local" },
                  { value: "express", label: locale === "zh-CN" ? "\u5feb\u8f66" : "Express" }
                ]}
                value={draftLineKind}
                disabled={isReadonly}
                onChange={(nextValue) => {
                  if (isReadonly) {
                    return;
                  }

                  setDraftLineKind(nextValue);
                  onSelectedLineKindChange?.(nextValue);
                }}
              />
            </div>
            <div className="dw-field dw-schedule-page-depot-field">
              <label>
                <SafeControlText>{t("schedule.allowedDepot")}</SafeControlText>
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
                  title={selectedDepotName}
                  disabled={isReadonly}
                  onClick={() => {
                    if (isReadonly) {
                      return;
                    }

                    if (!depotDropdownOpen) {
                      onRefreshMetadata?.();
                    }
                    setDepotDropdownOpen((current) => !current);
                  }}
                >
                  <ControlText>
                    {selectedDepotName || t("schedule.anyDepot")}
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
                      disabled={isReadonly}
                      onClick={() => {
                        onAllowedDepotChange?.(selectedEditLine, "");
                        setDepotDropdownOpen(false);
                      }}
                    >
                      <ControlText>{t("schedule.anyDepot")}</ControlText>
                    </button>
                    {availableDepots.map((depot) => (
                      <button
                        key={depot.id}
                        type="button"
                        className={`dw-line-dropdown-option ${depot.id === selectedLine?.allowedDepotId ? "is-active" : ""}`}
                        title={depot.name}
                        disabled={isReadonly}
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
                <SafeControlText>{locale === "zh-CN" ? "\u59cb\u53d1\u7ad9" : "Origin"}</SafeControlText>
              </label>
              <div className="dw-static-help dw-schedule-page-origin-value">
                <SafeControlText>{selectedOriginStationName}</SafeControlText>
              </div>
            </div>
            <div className={`dw-field dw-schedule-page-hold-field${originHoldTooSmall ? " is-error" : ""}`}>
              <label>
                <SafeControlText>{originHoldTooSmall ? (locale === "zh-CN" ? "\u4e0d\u5f97\u5c0f\u4e8e5\u5206" : "Min 5 min") : (locale === "zh-CN" ? "\u5019\u8f66\u7a97\u53e3 / \u5206\u949f" : "Hold window / min")}</SafeControlText>
              </label>
              <input
                type="text"
                inputMode="numeric"
                value={originHoldInput}
                readOnly={isReadonly}
                onChange={(event) => setOriginHoldInput(event.target.value.replace(/[^0-9]/g, ""))}
                onBlur={commitOriginHoldLimit}
              />
            </div>
            <div className={`dw-field dw-schedule-page-dwell-field${maxStationDwellTooSmall ? " is-error" : ""}`}>
              <label>
                <SafeControlText>{maxStationDwellTooSmall ? (locale === "zh-CN" ? "\u4e0d\u5f97\u5c0f\u4e8e5\u5206" : "Min 5 min") : (locale === "zh-CN" ? "\u6700\u957f\u505c\u7ad9\u65f6\u95f4" : "Max dwell / min")}</SafeControlText>
              </label>
              <input
                type="text"
                inputMode="numeric"
                value={maxStationDwellInput}
                readOnly={isReadonly}
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
              selectedLineName={selectedLine?.name || t("schedule.noLineSelected")}
              currentLineKind={draftLineKind}
              onAddToStaged={onAddManualToStaged}
              readOnly={isReadonly}
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
              readOnly={isReadonly}
            />
          </div>

          <div className="dw-schedule-board-col is-right">
            <AutoScheduleRuleEditor
              rules={autoRules}
              setRules={setAutoRules}
              selectedEditLine={selectedEditLine}
              selectedLineName={selectedLine?.name || t("schedule.noLineSelected")}
              selectedLineKind={draftLineKind}
              previewPlan={autoPreviewPlan}
              onAddToStaged={onAddAutoToStaged}
              readOnly={isReadonly}
            />
          </div>
        </div>
      </div>
    </div>
  );
}
