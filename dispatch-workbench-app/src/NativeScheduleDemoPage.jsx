import { useEffect, useMemo, useRef, useState } from "react";

function DemoDropdown({
  label,
  value,
  options,
  onSelect,
  className,
  title
}) {
  const [open, setOpen] = useState(false);

  return (
    <div
      className={`dw-demo-field ${className || ""}`}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) {
          setOpen(false);
        }
      }}
    >
      <label className="dw-demo-label">{label}</label>
      <div className="dw-demo-dropdown">
        <button
          type="button"
          className={`dw-demo-input dw-demo-dropdown-trigger ${open ? "is-open" : ""}`}
          title={title || value}
          onClick={() => setOpen((current) => !current)}
        >
          <span>{value}</span>
          <span className="dw-demo-dropdown-caret">v</span>
        </button>
        {open ? (
          <div className="dw-demo-dropdown-menu">
            {options.map((option) => (
              <button
                key={option.value}
                type="button"
                className={`dw-demo-dropdown-option ${option.active ? "is-active" : ""}`}
                onClick={() => {
                  onSelect(option.value);
                  setOpen(false);
                }}
              >
                {option.label}
              </button>
            ))}
          </div>
        ) : null}
      </div>
    </div>
  );
}

function DemoTextField({
  label,
  value,
  onCommit,
  className,
  placeholder,
  readOnly = false,
  inputRef
}) {
  const localInputRef = useRef(null);
  const resolvedInputRef = inputRef || localInputRef;

  useEffect(() => {
    const node = resolvedInputRef.current;
    const nextValue = String(value ?? "");
    if (node && node.value !== nextValue) {
      node.value = nextValue;
    }
  }, [resolvedInputRef, value]);

  function commitCurrentValue() {
    if (readOnly || typeof onCommit !== "function" || !resolvedInputRef.current) {
      return;
    }

    onCommit(String(resolvedInputRef.current.value || ""));
  }

  return (
    <div className={`dw-demo-field ${className || ""}`}>
      <label className="dw-demo-label">{label}</label>
      <input
        ref={resolvedInputRef}
        type="text"
        defaultValue={value || ""}
        placeholder={placeholder}
        readOnly={readOnly}
        className={`dw-demo-input ${readOnly ? "is-readonly" : ""}`}
        onBlur={commitCurrentValue}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            commitCurrentValue();
            event.currentTarget.blur();
          }
        }}
      />
    </div>
  );
}

function SummaryBadge({ kind }) {
  return (
    <span className={`dw-demo-badge ${kind === "快车" ? "is-express" : "is-local"}`}>
      {kind}
    </span>
  );
}

const LINE_OPTIONS = [
  {
    id: "line-local",
    name: "区间线",
    type: "普通",
    depot: "任意车库",
    origin: "工业区",
    hold: "15",
    dwell: "6"
  },
  {
    id: "line-express",
    name: "直达线",
    type: "快车",
    depot: "北区车库",
    origin: "工业区",
    hold: "10",
    dwell: "4"
  }
];

const SUMMARY_ROWS = [
  { id: 1, time: "00:00", line: "区间线", origin: "工业区", type: "普通", status: "待应用", isConflict: false, isExpress: false },
  { id: 2, time: "00:30", line: "区间线", origin: "工业区", type: "普通", status: "待应用", isConflict: false, isExpress: false },
  { id: 3, time: "01:00", line: "直达线", origin: "工业区", type: "快车", status: "待应用", isConflict: false, isExpress: true },
  { id: 4, time: "01:00", line: "区间线", origin: "工业区", type: "普通", status: "冲突", isConflict: true, isExpress: false },
  { id: 5, time: "01:30", line: "区间线", origin: "工业区", type: "普通", status: "待应用", isConflict: false, isExpress: false },
  { id: 6, time: "02:00", line: "区间线", origin: "工业区", type: "普通", status: "待应用", isConflict: false, isExpress: false },
  { id: 7, time: "02:30", line: "直达线", origin: "工业区", type: "快车", status: "待应用", isConflict: false, isExpress: true }
];

const INITIAL_AUTO_RULES = [
  { id: 1, window: "00:00 - 06:00", freq: "2", offset: "0", preview: "00:00 · 00:30 · 01:00 · 01:30 · 02:00 · 02:30" },
  { id: 2, window: "06:00 - 09:00", freq: "4", offset: "+5", preview: "06:05 · 06:20 · 06:35 · 06:50 · 07:05 · 07:20 · 07:35 · 07:50" },
  { id: 3, window: "09:00 - 17:00", freq: "3", offset: "0", preview: "09:00 · 09:20 · 09:40 · 10:00 · 10:20 · 10:40 · 11:00 · 11:20" }
];

const INITIAL_MANUAL_DRAFTS = [
  { id: 1, time: "12:20" },
  { id: 2, time: "12:30" }
];

export default function NativeScheduleDemoPage() {
  const [activeRightTab, setActiveRightTab] = useState("auto");
  const [selectedLineId, setSelectedLineId] = useState(LINE_OPTIONS[0].id);
  const [selectedDepot, setSelectedDepot] = useState(LINE_OPTIONS[0].depot);
  const [origin, setOrigin] = useState(LINE_OPTIONS[0].origin);
  const [holdMinutes, setHoldMinutes] = useState(LINE_OPTIONS[0].hold);
  const [dwellMinutes, setDwellMinutes] = useState(LINE_OPTIONS[0].dwell);
  const [autoRules, setAutoRules] = useState(INITIAL_AUTO_RULES);
  const [manualDrafts, setManualDrafts] = useState(INITIAL_MANUAL_DRAFTS);
  const [manualInput, setManualInput] = useState("12:00");
  const [editorStart, setEditorStart] = useState("08:00");
  const [editorEnd, setEditorEnd] = useState("10:00");
  const manualInputRef = useRef(null);

  const selectedLine = useMemo(
    () => LINE_OPTIONS.find((line) => line.id === selectedLineId) ?? LINE_OPTIONS[0],
    [selectedLineId]
  );

  const conflictCount = SUMMARY_ROWS.filter((row) => row.isConflict).length;
  const earliestStart = SUMMARY_ROWS[0]?.time || "--:--";

  function handleSelectLine(lineId) {
    const nextLine = LINE_OPTIONS.find((line) => line.id === lineId);
    if (!nextLine) {
      return;
    }

    setSelectedLineId(nextLine.id);
    setSelectedDepot(nextLine.depot);
    setOrigin(nextLine.origin);
    setHoldMinutes(nextLine.hold);
    setDwellMinutes(nextLine.dwell);
  }

  function addAutoRule() {
    setAutoRules((current) => [
      ...current,
      {
        id: Date.now(),
        window: "08:00 - 10:00",
        freq: "4",
        offset: "+5",
        preview: "08:05 · 08:20 · 08:35 · 08:50 · 09:05 · 09:20"
      }
    ]);
  }

  function removeAutoRule(ruleId) {
    setAutoRules((current) => current.filter((rule) => rule.id !== ruleId));
  }

  function addManualDraft() {
    const rawValue = manualInputRef.current ? manualInputRef.current.value : manualInput;
    const normalized = String(rawValue || "").trim();
    if (!normalized) {
      return;
    }

    setManualDrafts((current) => [
      ...current,
      { id: Date.now(), time: normalized }
    ]);
    setManualInput("");
    if (manualInputRef.current) {
      manualInputRef.current.value = "";
    }
  }

  function removeManualDraft(draftId) {
    setManualDrafts((current) => current.filter((draft) => draft.id !== draftId));
  }

  return (
    <div className="dw-demo-shell">
      <div className="dw-demo-topbar">
        <DemoDropdown
          label="当前线路"
          value={selectedLine.name}
          options={LINE_OPTIONS.map((line) => ({
            value: line.id,
            label: line.name,
            active: line.id === selectedLineId
          }))}
          onSelect={handleSelectLine}
          className="is-line"
        />

        <div className="dw-demo-field is-kind">
          <label className="dw-demo-label">类型</label>
          <div className="dw-demo-toggle-group">
            <button type="button" className={`dw-demo-toggle ${selectedLine.type === "普通" ? "is-active" : ""}`}>
              普通
            </button>
            <button type="button" className={`dw-demo-toggle ${selectedLine.type === "快车" ? "is-active is-express" : ""}`}>
              快车
            </button>
          </div>
        </div>

        <DemoDropdown
          label="停放车库"
          value={selectedDepot}
          options={[
            { value: "任意车库", label: "任意车库", active: selectedDepot === "任意车库" },
            { value: "北区车库", label: "北区车库", active: selectedDepot === "北区车库" }
          ]}
          onSelect={setSelectedDepot}
          className="is-depot"
        />

        <DemoTextField label="始发站" value={origin} onCommit={setOrigin} className="is-origin" />
        <DemoTextField label="候车(分)" value={holdMinutes} onCommit={setHoldMinutes} className="is-hold" />
        <DemoTextField label="最长停站" value={dwellMinutes} onCommit={setDwellMinutes} className="is-dwell" />
      </div>

      <div className="dw-demo-main">
        <section className="dw-demo-left">
          <div className="dw-demo-section-header">
            <div className="dw-demo-section-title">待应用汇总</div>
            <div className="dw-demo-summary-metrics">
              <span>总数 {SUMMARY_ROWS.length}</span>
              <span>最早 {earliestStart}</span>
              <span>冲突 {conflictCount}</span>
            </div>
          </div>

          <div className="dw-demo-summary-scroll">
            <div className="dw-demo-summary-table">
              <div className="dw-demo-summary-head">
                <div className="is-time">时间</div>
                <div className="is-line">线路与类型</div>
                <div className="is-origin">始发站</div>
                <div className="is-status">状态</div>
                <div className="is-action">操作</div>
              </div>
              {SUMMARY_ROWS.map((row) => (
                <div key={row.id} className={`dw-demo-summary-row ${row.isConflict ? "is-conflict" : ""} ${row.isExpress ? "is-express" : ""}`}>
                  <div className="is-time">{row.time}</div>
                  <div className="is-line">
                    <span className={`dw-demo-dot ${row.isConflict ? "is-conflict" : ""}`} />
                    <div className="dw-demo-line-meta-top">
                      <span className="dw-demo-line-name">{row.line}</span>
                      <SummaryBadge kind={row.type} />
                    </div>
                  </div>
                  <div className="is-origin dw-demo-origin-cell">
                    {row.origin}
                  </div>
                  <div className="is-status">
                    <span className={`dw-demo-status ${row.isConflict ? "is-conflict" : "is-pending"}`}>{row.status}</span>
                  </div>
                  <div className="is-action">
                    <button type="button" className="dw-demo-link-danger dw-demo-row-action">移除</button>
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div className="dw-demo-footer">
            <button type="button" className="dw-demo-footer-link">清空草稿</button>
            <button type="button" className="dw-demo-primary">应用至游戏模拟</button>
          </div>
        </section>

        <section className="dw-demo-right">
          <div className="dw-demo-tabs">
            <button
              type="button"
              className={`dw-demo-tab ${activeRightTab === "auto" ? "is-active" : ""}`}
              onClick={() => setActiveRightTab("auto")}
            >
              自动规则
            </button>
            <button
              type="button"
              className={`dw-demo-tab ${activeRightTab === "manual" ? "is-active" : ""}`}
              onClick={() => setActiveRightTab("manual")}
            >
              单点微调
            </button>
          </div>

          {activeRightTab === "auto" ? (
            <div className="dw-demo-right-body">
              <div className="dw-demo-rule-editor">
                <div className="dw-demo-rule-editor-row">
                  <DemoTextField label="开始时间" value={editorStart} onCommit={setEditorStart} className="is-window-start" />
                  <DemoTextField label="结束时间" value={editorEnd} onCommit={setEditorEnd} className="is-window-end" />
                  <DemoTextField label="频率 (班/h)" value="4" className="is-rate" readOnly />
                  <DemoTextField label="发车偏移" value="+5m" className="is-offset" readOnly />
                </div>
                <div className="dw-demo-preview-panel">
                  <div className="dw-demo-preview-inline">
                    <span className="dw-demo-preview-tag">预计</span>
                    <span className="dw-demo-preview-values">08:05 · 08:20 · 08:35 · 08:50 · 09:05 · 09:20</span>
                  </div>
                  <button type="button" className="dw-demo-outline" onClick={addAutoRule}>添加</button>
                </div>
              </div>

              <div className="dw-demo-rule-list-head">
                <div className="is-window">时间窗</div>
                <div className="is-rate">频率</div>
                <div className="is-offset">偏移</div>
                <div className="is-action">操作</div>
              </div>

              <div className="dw-demo-rule-list-scroll">
                {autoRules.map((rule) => (
                  <div key={rule.id} className="dw-demo-rule-row">
                    <div className="dw-demo-rule-row-main">
                      <div className="is-window">
                        <span className="dw-demo-rule-window">{rule.window}</span>
                      </div>
                      <div className="is-rate">
                        <span className="dw-demo-rule-rate">{rule.freq} 班</span>
                      </div>
                      <div className="is-offset">
                        <span className="dw-demo-rule-offset">{rule.offset}m</span>
                      </div>
                      <div className="is-action">
                        <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => removeAutoRule(rule.id)}>移除</button>
                      </div>
                    </div>
                    <div className="dw-demo-rule-preview">{rule.preview}</div>
                  </div>
                ))}
              </div>

              <div className="dw-demo-footer">
                <span className="dw-demo-footer-note">右侧草稿需导入左侧进行冲突检测</span>
                <button type="button" className="dw-demo-primary is-secondary">导入左侧汇总表</button>
              </div>
            </div>
          ) : (
            <div className="dw-demo-right-body">
              <div className="dw-demo-rule-editor">
                <div className="dw-demo-rule-editor-row is-manual">
                  <DemoTextField
                    label="发车时间 (单班次)"
                    value={manualInput}
                    onCommit={setManualInput}
                    className="is-manual-time"
                    inputRef={manualInputRef}
                  />
                  <button type="button" className="dw-demo-outline" onClick={addManualDraft}>添加至草稿</button>
                </div>
              </div>

              <div className="dw-demo-rule-list-scroll">
                {manualDrafts.map((draft) => (
                  <div key={draft.id} className="dw-demo-manual-row">
                    <span className="dw-demo-manual-time">{draft.time}</span>
                    <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => removeManualDraft(draft.id)}>移除</button>
                  </div>
                ))}
              </div>

              <div className="dw-demo-footer">
                <span className="dw-demo-footer-note">右侧草稿需导入左侧进行冲突检测</span>
                <button type="button" className="dw-demo-primary is-secondary">导入左侧汇总表</button>
              </div>
            </div>
          )}
        </section>
      </div>
    </div>
  );
}
