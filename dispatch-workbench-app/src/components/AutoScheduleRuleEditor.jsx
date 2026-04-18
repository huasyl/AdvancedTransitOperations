import { useMemo, useRef } from "react";
import { useI18n } from "../lib/i18n";
import { normalizeCompactTimeInput } from "../lib/time";
import { ChoiceButtons, ControlText, SafeControlText } from "./ChoiceButtons";
import TimeInput from "./TimeInput";
import WorkbenchInput from "./WorkbenchInput";

function buildPreviewLabel(rule, previewEntry, locale) {
  if (!previewEntry) {
    return locale === "zh-CN" ? "当前规则尚未生成预览" : "No preview available yet.";
  }

  const previewTimes = Array.isArray(previewEntry.times) ? previewEntry.times : [];
  if (previewTimes.length > 0) {
    return previewTimes.join(" / ");
  }

  if (previewEntry.reason === "missing-paired-reference" && rule.kind === "express") {
    return locale === "zh-CN"
      ? "当前时间窗内没有可配对普通车，快车将按规则直接生成。"
      : "No paired local departures exist in the selected window.";
  }

  if (previewEntry.reason === "invalid") {
    return locale === "zh-CN" ? "参数无效，无法生成预览" : "Invalid parameters. No preview generated.";
  }

  return locale === "zh-CN" ? "当前规则不会生成任何车次" : "This rule would not generate any departures.";
}

export default function AutoScheduleRuleEditor({
  rules = [],
  setRules,
  selectedEditLine,
  selectedLineName,
  selectedLineKind = "local",
  previewPlan,
  onAddToStaged,
  readOnly = false
}) {
  const { locale } = useI18n();
  const rulesForLine = useMemo(
    () => rules.filter((rule) => rule.lineId === selectedEditLine),
    [rules, selectedEditLine]
  );
  const activeKind = selectedLineKind === "express" ? "express" : "local";
  const startInputRefs = useRef(new Map());
  const endInputRefs = useRef(new Map());

  function focusAndSelectInput(input) {
    if (!input) {
      return;
    }
    input.focus();
    if (typeof input.select === "function") {
      setTimeout(() => input.select(), 0);
    }
  }

  function setInputRef(refMap, ruleId) {
    return (element) => {
      if (element) {
        refMap.current.set(ruleId, element);
      } else {
        refMap.current.delete(ruleId);
      }
    };
  }

  const offsetOptions = [
    { value: "after", label: locale === "zh-CN" ? "晚于普通车" : "After local" },
    { value: "before", label: locale === "zh-CN" ? "早于普通车" : "Before local" }
  ];

  function updateRule(id, patch) {
    setRules((current) =>
      current.map((rule) => {
        if (rule.id !== id) {
          return rule;
        }

        const nextRule = { ...rule, ...patch, enabled: true, kind: activeKind };
        const departuresPerHour = Number(nextRule.departuresPerHour) || 0;
        nextRule.localPerHour = activeKind === "local" ? departuresPerHour : 0;
        nextRule.expressPerHour = activeKind === "express" ? departuresPerHour : 0;
        if (activeKind !== "express") {
          nextRule.expressOffsetMode = "after";
          nextRule.expressOffsetMinutes = 0;
        }
        return nextRule;
      })
    );
  }

  function addRule() {
    setRules((current) => [
      ...current,
      {
        id: `rule-${Date.now()}`,
        lineId: selectedEditLine,
        enabled: true,
        start: "10:00",
        end: "11:00",
        kind: activeKind,
        departuresPerHour: 4,
        localPerHour: activeKind === "local" ? 4 : 0,
        expressPerHour: activeKind === "express" ? 4 : 0,
        expressOffsetMode: "after",
        expressOffsetMinutes: 0
      }
    ]);
  }

  function clearCurrentLine() {
    setRules((current) => current.filter((rule) => rule.lineId !== selectedEditLine));
  }

  function removeRule(id) {
    setRules((current) => current.filter((rule) => rule.id !== id));
  }

  return (
    <section className="dw-panel">
      <div className="dw-panel-header">
        <SafeControlText>{locale === "zh-CN" ? "自动草稿" : "Automatic Draft"}</SafeControlText>
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
              {activeKind === "express"
                ? (locale === "zh-CN"
                    ? "快车偏移优先基于中间汇总里的普通车时刻；如果当前没有普通车，也可以按规则窗口直接生成。"
                    : "Express offsets should prefer the paired local line in the staged timetable; if none exists yet, the rule can still generate departures from its own window.")
                : (locale === "zh-CN"
                    ? "这里编辑普通车自动生成规则。添加到时刻表后会立即并入中间汇总。"
                    : "Edit automatic local rules here. Adding them will merge them into the staged timetable immediately.")}
            </ControlText>
          </span>
        </div>

        <div className="dw-schedule-toolbar">
          <button type="button" className="dw-btn dw-btn-primary" onClick={addRule} disabled={readOnly}>
            <ControlText>{locale === "zh-CN" ? "添加规则" : "Add rule"}</ControlText>
          </button>
          <button type="button" className="dw-btn" onClick={clearCurrentLine} disabled={readOnly}>
            <ControlText>{locale === "zh-CN" ? "清空当前线路" : "Clear current line"}</ControlText>
          </button>
          <button type="button" className="dw-btn is-toolbar-end" onClick={() => onAddToStaged?.(rulesForLine)} disabled={readOnly}>
            <ControlText>{locale === "zh-CN" ? "添加到时刻表" : "Add to timetable"}</ControlText>
          </button>
        </div>

        <div className="dw-rule-table-wrap is-schedule-draft">
          <div className="dw-rule-table">
            <div className="dw-rule-row is-head">
              <div className="dw-rule-head is-window">
                <SafeControlText>{locale === "zh-CN" ? "时间窗" : "Window"}</SafeControlText>
              </div>
              <div className="dw-rule-head is-rate">
                <SafeControlText>{locale === "zh-CN" ? "班次/小时" : "Trips / h"}</SafeControlText>
              </div>
              <div className="dw-rule-head is-offset-mode">
                <SafeControlText>{locale === "zh-CN" ? "偏移" : "Offset"}</SafeControlText>
              </div>
              <div className="dw-rule-head is-offset-min">
                <SafeControlText>{locale === "zh-CN" ? "分钟" : "Minutes"}</SafeControlText>
              </div>
              <div className="dw-rule-head is-action">
                <SafeControlText>{locale === "zh-CN" ? "操作" : "Action"}</SafeControlText>
              </div>
            </div>

            {rulesForLine.length === 0 ? (
              <div className="dw-rule-row is-empty">
                <div className="dw-rule-cell is-empty-cell">
                  <SafeControlText>
                    {locale === "zh-CN" ? "当前线路还没有自动规则。" : "No automatic draft rules for the current line."}
                  </SafeControlText>
                </div>
              </div>
            ) : null}

            {rulesForLine.map((rule) => (
              <div className="dw-rule-stack" key={rule.id}>
                <div className="dw-rule-row">
                  <div className="dw-rule-cell is-window">
                    <div className="dw-rule-window">
                      <div className="dw-rule-window-content">
                        <span className="dw-rule-time-range">
                          <TimeInput
                            ref={setInputRef(startInputRefs, rule.id)}
                            value={rule.start}
                            placeholder="HH:mm"
                            readOnly={readOnly}
                            onChange={(event) => {
                              const nextValue = normalizeCompactTimeInput(event.target.value);
                              updateRule(rule.id, { start: nextValue });
                              if (nextValue.length >= 5) {
                                focusAndSelectInput(endInputRefs.current.get(rule.id));
                              }
                            }}
                          />
                          <span>
                            <SafeControlText>{locale === "zh-CN" ? "到" : "to"}</SafeControlText>
                          </span>
                          <TimeInput
                            ref={setInputRef(endInputRefs, rule.id)}
                            value={rule.end}
                            placeholder="HH:mm"
                            readOnly={readOnly}
                            onChange={(event) => updateRule(rule.id, { end: normalizeCompactTimeInput(event.target.value) })}
                          />
                        </span>
                      </div>
                    </div>
                  </div>

                  <div className="dw-rule-cell is-rate">
                    <WorkbenchInput
                      value={rule.departuresPerHour}
                      inputMode="decimal"
                      readOnly={readOnly}
                      onChange={(event) => updateRule(rule.id, { departuresPerHour: event.target.value })}
                    />
                  </div>

                  <div className="dw-rule-cell is-offset-mode">
                    {activeKind === "express" ? (
                      <ChoiceButtons
                        compact
                        className="is-rule-offset-buttons"
                        options={offsetOptions}
                        value={rule.expressOffsetMode}
                        disabled={readOnly}
                        onChange={(value) => updateRule(rule.id, { expressOffsetMode: value })}
                      />
                    ) : (
                      <span className="dw-muted-inline">
                        <SafeControlText>{locale === "zh-CN" ? "无" : "None"}</SafeControlText>
                      </span>
                    )}
                  </div>

                  <div className="dw-rule-cell is-offset-min">
                    {activeKind === "express" ? (
                      <WorkbenchInput
                        value={rule.expressOffsetMinutes}
                        inputMode="numeric"
                        readOnly={readOnly}
                        onChange={(event) => updateRule(rule.id, { expressOffsetMinutes: event.target.value })}
                      />
                    ) : (
                      <span className="dw-muted-inline">
                        <SafeControlText>-</SafeControlText>
                      </span>
                    )}
                  </div>

                  <div className="dw-rule-cell is-action">
                    <button type="button" className="dw-link-btn dw-btn-danger" onClick={() => removeRule(rule.id)} disabled={readOnly}>
                      <ControlText>{locale === "zh-CN" ? "删除" : "Delete"}</ControlText>
                    </button>
                  </div>
                </div>

                <div className="dw-rule-preview-row">
                  <span className="dw-rule-preview-label">
                    <SafeControlText>{locale === "zh-CN" ? "预览" : "Preview"}</SafeControlText>
                  </span>
                  <span className="dw-rule-preview-values">
                    <SafeControlText>{buildPreviewLabel(rule, previewPlan?.previewsByRule?.[rule.id], locale)}</SafeControlText>
                  </span>
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}



