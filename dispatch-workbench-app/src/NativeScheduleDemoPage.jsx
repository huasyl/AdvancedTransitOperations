import { useEffect, useMemo, useRef, useState } from "react";

function DemoDropdown({
  label,
  labelIcon,
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
      <label className="dw-demo-label">
        <span className="dw-demo-label-content">
          {labelIcon ? <span className="dw-demo-label-icon" aria-hidden="true">{labelIcon}</span> : null}
          <span>{label}</span>
        </span>
      </label>
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
  labelIcon,
  value,
  onCommit,
  className,
  placeholder,
  readOnly = false,
  inputRef,
  timeMode = false,
  nextInputRef
}) {
  const localInputRef = useRef(null);
  const resolvedInputRef = inputRef || localInputRef;
  const [draftValue, setDraftValue] = useState(String(value ?? ""));

  useEffect(() => {
    const nextValue = String(value ?? "");
    setDraftValue(nextValue);
  }, [value]);

  function commitCurrentValue() {
    if (readOnly || typeof onCommit !== "function") {
      return;
    }

    let nextValue = String(draftValue || "");
    if (timeMode) {
      nextValue = normalizeTimeInput(nextValue);
      if (!isValidTimeValue(nextValue)) {
        setDraftValue(String(value ?? ""));
        return;
      }
      setDraftValue(nextValue);
    }

    onCommit(nextValue);
  }

  return (
    <div className={`dw-demo-field ${className || ""}`}>
      <label className="dw-demo-label">
        <span className="dw-demo-label-content">
          {labelIcon ? <span className="dw-demo-label-icon" aria-hidden="true">{labelIcon}</span> : null}
          <span>{label}</span>
        </span>
      </label>
      <input
        ref={resolvedInputRef}
        type="text"
        value={draftValue}
        placeholder={placeholder}
        readOnly={readOnly}
        className={`dw-demo-input ${readOnly ? "is-readonly" : ""}`}
        inputMode={timeMode ? "numeric" : "text"}
        maxLength={timeMode ? 5 : undefined}
        onChange={(event) => {
          const rawValue = event.currentTarget.value;
          setDraftValue(timeMode ? normalizeTimeInput(rawValue) : rawValue);
        }}
        onPaste={(event) => {
          if (!timeMode) {
            return;
          }

          event.preventDefault();
          const pastedText = event.clipboardData?.getData("text") || "";
          setDraftValue(normalizeTimeInput(pastedText));
        }}
        onBlur={commitCurrentValue}
        onKeyDown={(event) => {
          if (timeMode) {
            const allowedKeys = [
              "Backspace",
              "Delete",
              "Tab",
              "ArrowLeft",
              "ArrowRight",
              "ArrowUp",
              "ArrowDown",
              "Home",
              "End",
              "Enter"
            ];
            const isDigitKey = event.key >= "0" && event.key <= "9";
            const isCtrlCommand = event.ctrlKey || event.metaKey;
            if (!isDigitKey && !allowedKeys.includes(event.key) && !isCtrlCommand) {
              event.preventDefault();
              return;
            }
          }

          if (event.key === "Tab" && nextInputRef?.current) {
            event.preventDefault();
            commitCurrentValue();
            nextInputRef.current.focus();
            if (typeof nextInputRef.current.select === "function") {
              nextInputRef.current.select();
            }
            return;
          }

          if (event.key === "Enter") {
            commitCurrentValue();
            event.currentTarget.blur();
          }
        }}
      />
    </div>
  );
}

function DemoDisplayField({
  label,
  labelIcon,
  value,
  className
}) {
  return (
    <div className={`dw-demo-field ${className || ""}`}>
      <label className="dw-demo-label">
        <span className="dw-demo-label-content">
          {labelIcon ? <span className="dw-demo-label-icon" aria-hidden="true">{labelIcon}</span> : null}
          <span>{label}</span>
        </span>
      </label>
      <div className="dw-demo-display-value" title={value || ""}>
        <span>{value}</span>
      </div>
    </div>
  );
}

function DemoLineIcon() {
  return (
    <svg viewBox="0 0 16 16" className="dw-demo-inline-icon">
      <path d="M3 3.5h10v6.5H3z" fill="none" stroke="currentColor" strokeWidth="1.4" />
      <path d="M5.5 12.5h5" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <circle cx="5" cy="10.5" r="1" fill="currentColor" />
      <circle cx="11" cy="10.5" r="1" fill="currentColor" />
    </svg>
  );
}

function DemoDepotIcon() {
  return (
    <svg viewBox="0 0 16 16" className="dw-demo-inline-icon">
      <path d="M2.5 6.5 8 2.5l5.5 4v7H2.5z" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round" />
      <path d="M6 13.5v-3h4v3" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  );
}

function DemoOriginIcon() {
  return (
    <svg viewBox="0 0 16 16" className="dw-demo-inline-icon">
      <path d="M8 13.2s3.3-3.7 3.3-6A3.3 3.3 0 1 0 4.7 7.2c0 2.3 3.3 6 3.3 6Z" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round" />
      <circle cx="8" cy="7.1" r="1.4" fill="none" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  );
}

function DemoOffsetField({
  label,
  direction,
  minutes,
  onDirectionChange,
  onMinutesChange,
  className,
  hint
}) {
  return (
    <div className={`dw-demo-field dw-demo-offset-field ${className || ""}`}>
      <label className="dw-demo-label">{label}</label>
      <div className="dw-demo-offset-control">
        <div className="dw-demo-offset-toggle">
          <button
            type="button"
            className={`dw-demo-offset-toggle-button ${direction === "early" ? "is-active" : ""}`}
            onClick={() => onDirectionChange(direction === "early" ? "" : "early")}
          >
            早
          </button>
          <button
            type="button"
            className={`dw-demo-offset-toggle-button ${direction === "late" ? "is-active" : ""}`}
            onClick={() => onDirectionChange(direction === "late" ? "" : "late")}
          >
            晚
          </button>
        </div>
        <div className="dw-demo-offset-minutes-wrap">
          <input
            type="text"
            value={minutes}
            className="dw-demo-input dw-demo-offset-minutes"
            inputMode="numeric"
            maxLength={2}
            onChange={(event) => {
              onMinutesChange(String(event.currentTarget.value || "").replace(/\D/g, "").slice(0, 2));
            }}
          />
          {!minutes ? <span className="dw-demo-offset-hint">{hint}</span> : null}
        </div>
      </div>
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

function normalizeTimeInput(rawValue) {
  const digitsOnly = String(rawValue || "").replace(/\D/g, "").slice(0, 4);
  if (digitsOnly.length < 2) {
    return digitsOnly;
  }
  if (digitsOnly.length === 2) {
    return digitsOnly + ":";
  }

  return digitsOnly.slice(0, 2) + ":" + digitsOnly.slice(2);
}

function isValidTimeValue(value) {
  const match = /^(\d{2}):(\d{2})$/.exec(String(value || "").trim());
  if (!match) {
    return false;
  }

  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  return Number.isFinite(hours) && Number.isFinite(minutes) && hours >= 0 && hours <= 23 && minutes >= 0 && minutes <= 59;
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
  { id: 7, time: "02:30", line: "直达线", origin: "工业区", type: "快车", status: "待应用", isConflict: false, isExpress: true },
  { id: 8, time: "03:00", line: "区间线", origin: "工业区", type: "普通", status: "待应用", isConflict: false, isExpress: false },
  { id: 9, time: "03:30", line: "直达线", origin: "工业区", type: "快车", status: "待应用", isConflict: false, isExpress: true },
  { id: 10, time: "04:00", line: "区间线", origin: "工业区", type: "普通", status: "待应用", isConflict: false, isExpress: false },
  { id: 11, time: "04:30", line: "区间线", origin: "工业区", type: "普通", status: "待应用", isConflict: false, isExpress: false }
];

const INITIAL_AUTO_RULES = [
  { id: 1, window: "00:00 - 06:00", freq: "2", offset: "无", preview: "00:00 · 00:30 · 01:00 · 01:30 · 02:00 · 02:30", type: "普通" },
  { id: 2, window: "06:00 - 09:00", freq: "4", offset: "晚 5 分", preview: "06:05 · 06:20 · 06:35 · 06:50 · 07:05 · 07:20 · 07:35 · 07:50", type: "快车" },
  { id: 3, window: "09:00 - 17:00", freq: "3", offset: "无", preview: "09:00 · 09:20 · 09:40 · 10:00 · 10:20 · 10:40 · 11:00 · 11:20", type: "普通" }
];

const INITIAL_MANUAL_DRAFTS = [
  { id: 1, time: "12:20" },
  { id: 2, time: "12:30" }
];

function buildSummaryRowsWithConflicts(rows) {
  const orderedRows = [...rows].sort((left, right) => {
    const leftMinutes = timeToMinutes(left.time) ?? 9999;
    const rightMinutes = timeToMinutes(right.time) ?? 9999;
    if (leftMinutes !== rightMinutes) {
      return leftMinutes - rightMinutes;
    }
    return String(left.id).localeCompare(String(right.id));
  });

  return orderedRows.map((row, index, sourceRows) => {
    const hasConflict = sourceRows.some((candidate, candidateIndex) => {
      if (candidateIndex === index) {
        return false;
      }

      return candidate.time === row.time && candidate.origin === row.origin;
    });

    return {
      ...row,
      status: hasConflict ? "冲突" : "待应用",
      isConflict: hasConflict,
      isExpress: row.type === "快车"
    };
  });
}

function timeToMinutes(value) {
  if (!isValidTimeValue(value)) {
    return null;
  }

  const [hours, minutes] = String(value).split(":").map(Number);
  return hours * 60 + minutes;
}

function minutesToTime(totalMinutes) {
  if (!Number.isFinite(totalMinutes)) {
    return "--:--";
  }

  const wrapped = (((Math.round(totalMinutes) % 1440) + 1440) % 1440);
  const hours = String(Math.floor(wrapped / 60)).padStart(2, "0");
  const minutes = String(wrapped % 60).padStart(2, "0");
  return `${hours}:${minutes}`;
}

function buildDemoAutoPreview(startText, endText, departuresPerHour, offsetMinutes = 0) {
  const startMinutes = timeToMinutes(startText);
  const endMinutes = timeToMinutes(endText);
  const rate = Number(departuresPerHour) || 0;
  if (startMinutes === null || endMinutes === null || endMinutes <= startMinutes || rate <= 0) {
    return [];
  }

  const interval = 60 / rate;
  const result = [];
  for (let minute = startMinutes; minute < endMinutes; minute += interval) {
    const candidateMinute = Math.round(minute) + offsetMinutes;
    if (candidateMinute >= startMinutes && candidateMinute < endMinutes) {
      result.push(minutesToTime(candidateMinute));
    }
  }
  return result;
}

function resolveOffsetMinutes(direction, minutesText) {
  const minutes = Number(minutesText);
  if (!Number.isFinite(minutes) || minutes <= 0) {
    return 0;
  }

  if (direction === "early") {
    return -minutes;
  }

  if (direction === "late") {
    return minutes;
  }

  return 0;
}

function formatOffsetLabel(direction, minutesText) {
  const minutes = Math.abs(resolveOffsetMinutes(direction, minutesText));
  if (minutes === 0) {
    return "无";
  }

  return `${direction === "early" ? "早" : "晚"} ${minutes} 分`;
}

function DemoSectionHeader({
  title,
  applied = false,
  metrics
}) {
  return (
    <div className={`dw-demo-section-header ${applied ? "is-applied" : ""}`}>
      <div className="dw-demo-section-title-wrap">
        <span className="dw-demo-section-accent" aria-hidden="true" />
        <div className="dw-demo-section-title">{title}</div>
      </div>
      <div className="dw-demo-summary-metrics">{metrics}</div>
    </div>
  );
}

function DemoScrollArea({
  className,
  metricsKey,
  children
}) {
  const scrollRef = useRef(null);
  const indicatorRef = useRef(null);
  const hideTimerRef = useRef(0);
  const hoverRef = useRef(false);
  const dragRef = useRef({
    dragging: false,
    pointerId: null,
    pointerOffset: 0
  });
  const [scrollMetrics, setScrollMetrics] = useState({
    visible: false,
    thumbTop: 0,
    thumbHeight: 0
  });
  const [indicatorActive, setIndicatorActive] = useState(false);

  useEffect(() => {
    const scrollElement = scrollRef.current;
    if (!scrollElement) {
      return undefined;
    }

    let frameId = 0;
    let timeoutId = 0;

    function showIndicatorTemporarily() {
      setIndicatorActive(true);

      if (hideTimerRef.current) {
        window.clearTimeout(hideTimerRef.current);
      }

      hideTimerRef.current = window.setTimeout(() => {
        if (!hoverRef.current && !dragRef.current.dragging) {
          setIndicatorActive(false);
        }
      }, 1400);
    }

    function updateMetrics() {
      frameId = 0;

      const clientHeight = scrollElement.clientHeight;
      const scrollHeight = scrollElement.scrollHeight;
      const scrollTop = scrollElement.scrollTop;
      const maxScroll = scrollHeight - clientHeight;

      if (clientHeight <= 0 || maxScroll <= 0) {
        setScrollMetrics({
          visible: false,
          thumbTop: 0,
          thumbHeight: 0
        });
        return;
      }

      const thumbHeight = Math.max(24, Math.round((clientHeight * clientHeight) / scrollHeight));
      const maxThumbTop = Math.max(0, clientHeight - thumbHeight);
      const thumbTop = Math.round((scrollTop / maxScroll) * maxThumbTop);

      setScrollMetrics({
        visible: true,
        thumbTop,
        thumbHeight
      });
    }

    function scheduleUpdate() {
      if (frameId) {
        return;
      }
      frameId = window.requestAnimationFrame(updateMetrics);
    }

    scheduleUpdate();
    timeoutId = window.setTimeout(updateMetrics, 80);

    function handleScroll() {
      scheduleUpdate();
      showIndicatorTemporarily();
    }

    scrollElement.addEventListener("scroll", handleScroll);
    window.addEventListener("resize", scheduleUpdate);

    return () => {
      if (frameId) {
        window.cancelAnimationFrame(frameId);
      }
      if (timeoutId) {
        window.clearTimeout(timeoutId);
      }
      if (hideTimerRef.current) {
        window.clearTimeout(hideTimerRef.current);
      }
      scrollElement.removeEventListener("scroll", handleScroll);
      window.removeEventListener("resize", scheduleUpdate);
    };
  }, [metricsKey]);

  function handleIndicatorMouseEnter() {
    hoverRef.current = true;
    setIndicatorActive(true);
    if (hideTimerRef.current) {
      window.clearTimeout(hideTimerRef.current);
    }
  }

  function handleIndicatorMouseLeave() {
    hoverRef.current = false;
    if (!dragRef.current.dragging) {
      setIndicatorActive(false);
    }
  }

  function applyDragPosition(clientY, pointerOffset) {
    const scrollElement = scrollRef.current;
    const indicatorElement = indicatorRef.current;
    if (!scrollElement || !indicatorElement) {
      return;
    }

    const indicatorRect = indicatorElement.getBoundingClientRect();
    const trackHeight = indicatorRect.height;
    const clientHeight = scrollElement.clientHeight;
    const scrollHeight = scrollElement.scrollHeight;
    const scrollRange = scrollHeight - clientHeight;

    if (clientHeight <= 0 || scrollRange <= 0) {
      setScrollMetrics({
        visible: false,
        thumbTop: 0,
        thumbHeight: 0
      });
      return;
    }

    const thumbHeight = Math.max(24, Math.round((clientHeight * clientHeight) / scrollHeight));
    const maxThumbTop = Math.max(0, trackHeight - thumbHeight);
    const rawTop = clientY - indicatorRect.top - pointerOffset;
    const nextThumbTop = Math.max(0, Math.min(maxThumbTop, rawTop));
    const nextScrollTop = maxThumbTop <= 0 ? 0 : (nextThumbTop / maxThumbTop) * scrollRange;
    scrollElement.scrollTop = nextScrollTop;

    setScrollMetrics({
      visible: true,
      thumbTop: Math.round(nextThumbTop),
      thumbHeight
    });
  }

  function handleIndicatorMouseDown(event) {
    const indicatorElement = indicatorRef.current;
    if (!indicatorElement || !scrollMetrics.visible) {
      return;
    }

    event.preventDefault();

    const thumbOffset = event.target === indicatorElement
      ? scrollMetrics.thumbHeight / 2
      : event.clientY - indicatorElement.getBoundingClientRect().top - scrollMetrics.thumbTop;

    dragRef.current = {
      dragging: true,
      pointerId: null,
      pointerOffset: thumbOffset
    };
    setIndicatorActive(true);

    applyDragPosition(event.clientY, thumbOffset);

    function handleMouseMove(moveEvent) {
      applyDragPosition(moveEvent.clientY, dragRef.current.pointerOffset);
    }

    function handleMouseUp() {
      dragRef.current.dragging = false;
      window.removeEventListener("mousemove", handleMouseMove);
      window.removeEventListener("mouseup", handleMouseUp);

      if (!hoverRef.current) {
        setIndicatorActive(false);
      }
    }

    window.addEventListener("mousemove", handleMouseMove);
    window.addEventListener("mouseup", handleMouseUp);
  }

  function handleIndicatorPointerDown(event) {
    const scrollElement = scrollRef.current;
    const indicatorElement = indicatorRef.current;
    if (!scrollElement || !indicatorElement || !scrollMetrics.visible) {
      return;
    }

    event.preventDefault();
    if (typeof indicatorElement.setPointerCapture === "function") {
      indicatorElement.setPointerCapture(event.pointerId);
    }

    const thumbOffset = event.target === indicatorElement
      ? scrollMetrics.thumbHeight / 2
      : event.clientY - indicatorElement.getBoundingClientRect().top - scrollMetrics.thumbTop;

    dragRef.current = {
      dragging: true,
      pointerId: event.pointerId,
      pointerOffset: thumbOffset
    };
    setIndicatorActive(true);
    applyDragPosition(event.clientY, thumbOffset);
  }

  function handleIndicatorPointerMove(event) {
    const scrollElement = scrollRef.current;
    const indicatorElement = indicatorRef.current;
    if (!scrollElement || !indicatorElement || !dragRef.current.dragging || dragRef.current.pointerId !== event.pointerId) {
      return;
    }

    applyDragPosition(event.clientY, dragRef.current.pointerOffset);
  }

  function handleIndicatorPointerUp(event) {
    const indicatorElement = indicatorRef.current;
    if (dragRef.current.pointerId !== event.pointerId) {
      return;
    }

    if (indicatorElement && typeof indicatorElement.releasePointerCapture === "function") {
      indicatorElement.releasePointerCapture(event.pointerId);
    }

    dragRef.current.dragging = false;
    dragRef.current.pointerId = null;

    if (!hoverRef.current) {
      setIndicatorActive(false);
    }
  }

  return (
    <div className="dw-demo-scroll-shell">
      <div ref={scrollRef} className={`dw-demo-scroll-body ${className}`}>
        {children}
      </div>
      <div
        ref={indicatorRef}
        className={`dw-demo-scroll-indicator ${scrollMetrics.visible ? "is-visible" : ""} ${indicatorActive ? "is-active" : ""}`}
        onMouseEnter={handleIndicatorMouseEnter}
        onMouseLeave={handleIndicatorMouseLeave}
        onMouseDown={handleIndicatorMouseDown}
        onPointerDown={handleIndicatorPointerDown}
        onPointerMove={handleIndicatorPointerMove}
        onPointerUp={handleIndicatorPointerUp}
      >
        <div
          className="dw-demo-scroll-thumb"
          style={{
            height: `${scrollMetrics.thumbHeight}px`,
            transform: `translateY(${scrollMetrics.thumbTop}px)`
          }}
        />
      </div>
    </div>
  );
}

function SummaryTable({
  rows,
  hasAppliedSchedule,
  onRemoveRow
}) {
  return (
    <>
      <div className="dw-demo-summary-head">
        <div className="is-time">时间</div>
        <div className="is-line">线路与类型</div>
        <div className="is-origin">始发站</div>
        <div className="is-status">状态</div>
        <div className="is-action" aria-hidden="true" />
      </div>

      <DemoScrollArea className="dw-demo-summary-scroll" metricsKey={rows.length}>
        <div className="dw-demo-summary-table">
          {rows.map((row) => (
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
                <span className={`dw-demo-status-text ${row.isConflict ? "is-conflict" : hasAppliedSchedule ? "is-applied" : "is-pending"}`}>
                  {row.isConflict ? "冲突" : hasAppliedSchedule ? "已应用" : "待应用"}
                </span>
              </div>
              <div className="is-action">
                <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => onRemoveRow(row.id)}>移除</button>
              </div>
            </div>
          ))}
        </div>
      </DemoScrollArea>
    </>
  );
}

function SummarySection({
  summaryStateLabel,
  hasAppliedSchedule,
  summaryRows,
  earliestStart,
  conflictCount,
  onRemoveRow,
  onClearSummary,
  onApplySchedule
}) {
  return (
    <section className="dw-demo-left">
      <DemoSectionHeader
        title={summaryStateLabel}
        applied={hasAppliedSchedule}
        metrics={(
          <>
            <span>总数 {summaryRows.length}</span>
            <span>最早 {earliestStart}</span>
            <span>冲突 {conflictCount}</span>
          </>
        )}
      />

      <SummaryTable
        rows={summaryRows}
        hasAppliedSchedule={hasAppliedSchedule}
        onRemoveRow={onRemoveRow}
      />

      <div className="dw-demo-footer">
        <button type="button" className="dw-demo-flat-button is-muted" onClick={onClearSummary}>清空时刻表</button>
        <button type="button" className={`dw-demo-primary dw-demo-cta ${hasAppliedSchedule ? "is-applied" : ""}`} onClick={onApplySchedule}>
          {hasAppliedSchedule ? "已应用至游戏模拟" : "应用至游戏模拟"}
        </button>
      </div>
    </section>
  );
}

function AutoRuleEditor({
  editorStart,
  editorEnd,
  autoFrequencyPerHour,
  showOffsetField,
  autoOffsetDirection,
  autoOffsetMinutesText,
  liveAutoPreview,
  editorEndInputRef,
  frequencyInputRef,
  onEditorStartChange,
  onEditorEndChange,
  onAutoOffsetDirectionChange,
  onAutoOffsetMinutesChange,
  onAddAutoRule
}) {
  return (
    <div className="dw-demo-rule-editor">
      <div className="dw-demo-rule-editor-row">
        <DemoTextField
          label="开始时间"
          value={editorStart}
          onCommit={onEditorStartChange}
          className="is-window-start"
          timeMode
          nextInputRef={editorEndInputRef}
        />
        <DemoTextField
          label="结束时间"
          value={editorEnd}
          onCommit={onEditorEndChange}
          className="is-window-end"
          timeMode
          inputRef={editorEndInputRef}
          nextInputRef={frequencyInputRef}
        />
        <DemoTextField
          label="频率 (班/h)"
          value={String(autoFrequencyPerHour)}
          className="is-rate"
          readOnly
          inputRef={frequencyInputRef}
        />
        {showOffsetField ? (
          <DemoOffsetField
            label="发车偏移"
            direction={autoOffsetDirection}
            minutes={autoOffsetMinutesText}
            onDirectionChange={onAutoOffsetDirectionChange}
            onMinutesChange={onAutoOffsetMinutesChange}
            className="is-offset"
            hint="早于或晚于慢车"
          />
        ) : null}
      </div>

      <div className="dw-demo-preview-panel">
        <div className="dw-demo-preview-inline is-window">
          <span className="dw-demo-preview-tag">预计</span>
          <span className="dw-demo-preview-values">{liveAutoPreview.length > 0 ? liveAutoPreview.join(" · ") : "--"}</span>
        </div>
        <div className="dw-demo-preview-spacer is-rate" />
        <div className="dw-demo-preview-spacer is-offset" />
        <button type="button" className="dw-demo-flat-button is-theme" onClick={onAddAutoRule}>添加</button>
      </div>
    </div>
  );
}

function AutoRuleTable({
  autoRules,
  showOffsetColumn,
  onRemoveAutoRule
}) {
  return (
    <>
      <div className={`dw-demo-rule-list-head ${showOffsetColumn ? "is-with-offset" : "is-no-offset"}`}>
        <div className="is-window">时间窗</div>
        <div className="is-rate">频率</div>
        {showOffsetColumn ? <div className="is-offset">偏移</div> : null}
        <div className="is-action">操作</div>
      </div>

      <DemoScrollArea className="dw-demo-rule-list-scroll" metricsKey={autoRules.length}>
        {autoRules.map((rule) => (
          <div key={rule.id} className="dw-demo-rule-row">
            <div className={`dw-demo-rule-row-main ${showOffsetColumn ? "is-with-offset" : "is-no-offset"}`}>
              <div className="is-window">
                <span className="dw-demo-rule-window">{rule.window}</span>
              </div>
              <div className="is-rate">
                <span className="dw-demo-rule-rate">{rule.freq} 班</span>
              </div>
              {showOffsetColumn ? (
                <div className="is-offset">
                  <span className="dw-demo-rule-offset">{rule.offset}</span>
                </div>
              ) : null}
              <div className="is-action">
                <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => onRemoveAutoRule(rule.id)}>移除</button>
              </div>
            </div>
            <div className="dw-demo-rule-preview">{rule.preview}</div>
          </div>
        ))}
      </DemoScrollArea>
    </>
  );
}

function ManualDraftSection({
  manualInput,
  manualInputRef,
  manualDrafts,
  onManualInputChange,
  onAddManualDraft,
  onRemoveManualDraft,
  onImportDraftsToSummary
}) {
  return (
    <div className="dw-demo-right-body">
      <div className="dw-demo-rule-editor">
        <div className="dw-demo-rule-editor-row is-manual">
          <DemoTextField
            label="发车时间 (单班次)"
            value={manualInput}
            onCommit={onManualInputChange}
            className="is-manual-time"
            inputRef={manualInputRef}
            timeMode
          />
          <button type="button" className="dw-demo-flat-button is-theme" onClick={onAddManualDraft}>添加</button>
        </div>
      </div>

      <DemoScrollArea className="dw-demo-rule-list-scroll" metricsKey={manualDrafts.length}>
        {manualDrafts.map((draft) => (
          <div key={draft.id} className="dw-demo-manual-row">
            <span className="dw-demo-manual-time">{draft.time}</span>
            <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => onRemoveManualDraft(draft.id)}>移除</button>
          </div>
        ))}
      </DemoScrollArea>

      <div className="dw-demo-footer">
        <button type="button" className="dw-demo-primary dw-demo-cta is-secondary" onClick={onImportDraftsToSummary}>《《 导入左侧汇总表</button>
      </div>
    </div>
  );
}

function AutoRuleSection({
  editorStart,
  editorEnd,
  autoFrequencyPerHour,
  selectedLineType,
  autoOffsetDirection,
  autoOffsetMinutesText,
  liveAutoPreview,
  autoRules,
  editorEndInputRef,
  frequencyInputRef,
  onEditorStartChange,
  onEditorEndChange,
  onAutoOffsetDirectionChange,
  onAutoOffsetMinutesChange,
  onAddAutoRule,
  onRemoveAutoRule,
  onImportDraftsToSummary
}) {
  return (
    <div className="dw-demo-right-body">
      <AutoRuleEditor
        editorStart={editorStart}
        editorEnd={editorEnd}
        autoFrequencyPerHour={autoFrequencyPerHour}
        showOffsetField={selectedLineType === "快车"}
        autoOffsetDirection={autoOffsetDirection}
        autoOffsetMinutesText={autoOffsetMinutesText}
        liveAutoPreview={liveAutoPreview}
        editorEndInputRef={editorEndInputRef}
        frequencyInputRef={frequencyInputRef}
        onEditorStartChange={onEditorStartChange}
        onEditorEndChange={onEditorEndChange}
        onAutoOffsetDirectionChange={onAutoOffsetDirectionChange}
        onAutoOffsetMinutesChange={onAutoOffsetMinutesChange}
        onAddAutoRule={onAddAutoRule}
      />

      <AutoRuleTable
        autoRules={autoRules}
        showOffsetColumn={selectedLineType === "快车"}
        onRemoveAutoRule={onRemoveAutoRule}
      />

      <div className="dw-demo-footer">
        <button type="button" className="dw-demo-primary dw-demo-cta is-secondary" onClick={onImportDraftsToSummary}>《《 导入左侧汇总表</button>
      </div>
    </div>
  );
}

export default function NativeScheduleDemoPage() {
  const [activeRightTab, setActiveRightTab] = useState("auto");
  const [selectedLineId, setSelectedLineId] = useState(LINE_OPTIONS[0].id);
  const [selectedLineType, setSelectedLineType] = useState(LINE_OPTIONS[0].type);
  const [selectedDepot, setSelectedDepot] = useState(LINE_OPTIONS[0].depot);
  const [origin, setOrigin] = useState(LINE_OPTIONS[0].origin);
  const [holdMinutes, setHoldMinutes] = useState(LINE_OPTIONS[0].hold);
  const [dwellMinutes, setDwellMinutes] = useState(LINE_OPTIONS[0].dwell);
  const [summaryRows, setSummaryRows] = useState(() => buildSummaryRowsWithConflicts(SUMMARY_ROWS));
  const [autoRules, setAutoRules] = useState(INITIAL_AUTO_RULES);
  const [manualDrafts, setManualDrafts] = useState(INITIAL_MANUAL_DRAFTS);
  const [manualInput, setManualInput] = useState("12:00");
  const [editorStart, setEditorStart] = useState("08:00");
  const [editorEnd, setEditorEnd] = useState("10:00");
  const [autoOffsetDirection, setAutoOffsetDirection] = useState("");
  const [autoOffsetMinutesText, setAutoOffsetMinutesText] = useState("");
  const [hasAppliedSchedule, setHasAppliedSchedule] = useState(false);
  const manualInputRef = useRef(null);
  const editorEndInputRef = useRef(null);
  const frequencyInputRef = useRef(null);

  const selectedLine = useMemo(
    () => LINE_OPTIONS.find((line) => line.id === selectedLineId) ?? LINE_OPTIONS[0],
    [selectedLineId]
  );
  const autoFrequencyPerHour = 4;
  const autoOffsetMinutes = useMemo(
    () => resolveOffsetMinutes(autoOffsetDirection, autoOffsetMinutesText),
    [autoOffsetDirection, autoOffsetMinutesText]
  );
  const autoOffsetLabel = useMemo(
    () => formatOffsetLabel(autoOffsetDirection, autoOffsetMinutesText),
    [autoOffsetDirection, autoOffsetMinutesText]
  );
  const effectiveAutoOffsetMinutes = selectedLineType === "快车" ? autoOffsetMinutes : 0;
  const effectiveAutoOffsetLabel = selectedLineType === "快车" ? autoOffsetLabel : "无";
  const liveAutoPreview = useMemo(
    () => buildDemoAutoPreview(editorStart, editorEnd, autoFrequencyPerHour, effectiveAutoOffsetMinutes),
    [editorEnd, editorStart, autoFrequencyPerHour, effectiveAutoOffsetMinutes]
  );

  const conflictCount = summaryRows.filter((row) => row.isConflict).length;
  const earliestStart = summaryRows[0]?.time || "--:--";
  const summaryStateLabel = hasAppliedSchedule ? "已应用时刻表" : "待应用汇总";

  function markDraftDirty() {
    setHasAppliedSchedule(false);
  }

  function handleSelectLine(lineId) {
    const nextLine = LINE_OPTIONS.find((line) => line.id === lineId);
    if (!nextLine) {
      return;
    }

    markDraftDirty();
    setSelectedLineId(nextLine.id);
    setSelectedLineType(nextLine.type);
    setSelectedDepot(nextLine.depot);
    setOrigin(nextLine.origin);
    setHoldMinutes(nextLine.hold);
    setDwellMinutes(nextLine.dwell);
  }

  function handleLineTypeSelect(nextType) {
    if (nextType !== "普通" && nextType !== "快车") {
      return;
    }

    markDraftDirty();
    setSelectedLineType(nextType);
  }

  function handleDepotChange(value) {
    markDraftDirty();
    setSelectedDepot(value);
  }

  function handleHoldMinutesChange(value) {
    markDraftDirty();
    setHoldMinutes(value);
  }

  function handleDwellMinutesChange(value) {
    markDraftDirty();
    setDwellMinutes(value);
  }

  function handleEditorStartChange(value) {
    markDraftDirty();
    setEditorStart(value);
  }

  function handleEditorEndChange(value) {
    markDraftDirty();
    setEditorEnd(value);
  }

  function handleManualInputChange(value) {
    markDraftDirty();
    setManualInput(value);
  }

  function handleAutoOffsetDirectionChange(nextDirection) {
    markDraftDirty();
    setAutoOffsetDirection(nextDirection);
  }

  function handleAutoOffsetMinutesChange(nextValue) {
    markDraftDirty();
    setAutoOffsetMinutesText(nextValue);
  }

  function addAutoRule() {
    if (!isValidTimeValue(editorStart) || !isValidTimeValue(editorEnd)) {
      return;
    }

    const previewTimes = buildDemoAutoPreview(editorStart, editorEnd, autoFrequencyPerHour, effectiveAutoOffsetMinutes);
    if (previewTimes.length === 0) {
      return;
    }

    markDraftDirty();
    setAutoRules((current) => [
      ...current,
      {
        id: Date.now(),
        window: editorStart + " - " + editorEnd,
        freq: String(autoFrequencyPerHour),
        offset: effectiveAutoOffsetLabel,
        preview: previewTimes.join(" · "),
        type: selectedLineType
      }
    ]);
  }

  function removeAutoRule(ruleId) {
    markDraftDirty();
    setAutoRules((current) => current.filter((rule) => rule.id !== ruleId));
  }

  function addManualDraft() {
    const rawValue = manualInputRef.current ? manualInputRef.current.value : manualInput;
    const normalized = normalizeTimeInput(String(rawValue || "").trim());
    if (!isValidTimeValue(normalized)) {
      if (manualInputRef.current) {
        manualInputRef.current.value = manualInput;
      }
      return;
    }

    markDraftDirty();
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
    markDraftDirty();
    setManualDrafts((current) => current.filter((draft) => draft.id !== draftId));
  }

  function removeSummaryRow(rowId) {
    markDraftDirty();
    setSummaryRows((current) => buildSummaryRowsWithConflicts(current.filter((row) => row.id !== rowId)));
  }

  function clearSummaryTable() {
    markDraftDirty();
    setSummaryRows([]);
  }

  function importDraftsToSummary() {
    const importedRows = [];

    manualDrafts.forEach((draft) => {
      if (!isValidTimeValue(draft.time)) {
        return;
      }

      importedRows.push({
        id: `summary-manual-${draft.id}-${Date.now()}`,
        time: draft.time,
        line: selectedLine.name,
        origin,
        type: selectedLineType
      });
    });

    autoRules
      .filter((rule) => (rule.type || "普通") === selectedLineType)
      .forEach((rule) => {
        String(rule.preview || "")
          .split("·")
          .map((entry) => entry.trim())
          .filter((entry) => isValidTimeValue(entry))
          .forEach((time, previewIndex) => {
            importedRows.push({
              id: `summary-auto-${rule.id}-${previewIndex}-${Date.now()}`,
              time,
              line: selectedLine.name,
              origin,
              type: selectedLineType
            });
          });
      });

    if (importedRows.length === 0) {
      return;
    }

    markDraftDirty();
    setSummaryRows((current) => buildSummaryRowsWithConflicts([...current, ...importedRows]));
  }

  function handleApplySchedule() {
    setHasAppliedSchedule(true);
  }

  return (
    <div className="dw-demo-shell">
      <div className="dw-demo-topbar">
        <DemoDropdown
          label="当前线路"
          labelIcon={<DemoLineIcon />}
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
            <button
              type="button"
              className={`dw-demo-toggle ${selectedLineType === "普通" ? "is-active" : ""}`}
              onClick={() => handleLineTypeSelect("普通")}
            >
              普通
            </button>
            <button
              type="button"
              className={`dw-demo-toggle ${selectedLineType === "快车" ? "is-active is-express" : ""}`}
              onClick={() => handleLineTypeSelect("快车")}
            >
              快车
            </button>
          </div>
        </div>

        <DemoDisplayField
          label="始发站"
          labelIcon={<DemoOriginIcon />}
          value={origin}
          className="is-origin"
        />
        <DemoDropdown
          label="停放车库"
          labelIcon={<DemoDepotIcon />}
          value={selectedDepot}
          options={[
            { value: "任意车库", label: "任意车库", active: selectedDepot === "任意车库" },
            { value: "北区车库", label: "北区车库", active: selectedDepot === "北区车库" }
          ]}
          onSelect={handleDepotChange}
          className="is-depot"
        />

        <DemoTextField label="候车(分)" value={holdMinutes} onCommit={handleHoldMinutesChange} className="is-hold" />
        <DemoTextField label="最长停站" value={dwellMinutes} onCommit={handleDwellMinutesChange} className="is-dwell" />
      </div>

      <div className="dw-demo-main">
        <SummarySection
          summaryStateLabel={summaryStateLabel}
          hasAppliedSchedule={hasAppliedSchedule}
          summaryRows={summaryRows}
          earliestStart={earliestStart}
          conflictCount={conflictCount}
          onRemoveRow={removeSummaryRow}
          onClearSummary={clearSummaryTable}
          onApplySchedule={handleApplySchedule}
        />

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
            <AutoRuleSection
              editorStart={editorStart}
              editorEnd={editorEnd}
              autoFrequencyPerHour={autoFrequencyPerHour}
              selectedLineType={selectedLineType}
              autoOffsetDirection={autoOffsetDirection}
              autoOffsetMinutesText={autoOffsetMinutesText}
              liveAutoPreview={liveAutoPreview}
              autoRules={autoRules}
              editorEndInputRef={editorEndInputRef}
              frequencyInputRef={frequencyInputRef}
              onEditorStartChange={handleEditorStartChange}
              onEditorEndChange={handleEditorEndChange}
              onAutoOffsetDirectionChange={handleAutoOffsetDirectionChange}
              onAutoOffsetMinutesChange={handleAutoOffsetMinutesChange}
              onAddAutoRule={addAutoRule}
              onRemoveAutoRule={removeAutoRule}
              onImportDraftsToSummary={importDraftsToSummary}
            />
          ) : (
            <ManualDraftSection
              manualInput={manualInput}
              manualInputRef={manualInputRef}
              manualDrafts={manualDrafts}
              onManualInputChange={handleManualInputChange}
              onAddManualDraft={addManualDraft}
              onRemoveManualDraft={removeManualDraft}
              onImportDraftsToSummary={importDraftsToSummary}
            />
          )}
        </section>
      </div>
    </div>
  );
}
