import { useEffect, useState } from "react";
import WorkbenchDropdown from "../../../shared/WorkbenchDropdown";
import { isValidTimeValue, normalizeTimeInput } from "../planner-time.js";

export function PlannerField({ label, note = "", children }) {
  return (
    <div className="dw-planner-field">
      <label className="dw-planner-field-label">{label}</label>
      {children}
      {note ? <div className="dw-planner-field-note">{note}</div> : null}
    </div>
  );
}

export function PlannerCompactField({ label, children }) {
  return (
    <div className="dw-planner-compact-field">
      {children}
      <label className="dw-planner-compact-field-label">{label}</label>
    </div>
  );
}

export function PlannerToggleRow({ options, value, onChange, className = "" }) {
  return (
    <div className={`dw-planner-toggle-row${className ? ` ${className}` : ""}`}>
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          className={`dw-planner-toggle-button ${value === option.value ? "is-active" : ""}`}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

export function PlannerChoiceGrid({ options, value, onToggle, disabledValues = null, className = "" }) {
  const disabledSet = disabledValues instanceof Set ? disabledValues : new Set();
  return (
    <div className={`dw-bc-platform-station-buttons dw-planner-station-tray${className ? ` ${className}` : ""}`}>
      {options.map((option) => {
        const isActive = Array.isArray(value) ? value.includes(option.value) : value === option.value;
        const isDisabled = disabledSet.has(option.value);
        return (
          <button
            key={option.value}
            type="button"
            className={`dw-bc-platform-station-button ${isActive ? "is-active" : ""} ${isDisabled ? "is-disabled" : ""}`}
            disabled={isDisabled}
            onClick={() => {
              if (!isDisabled) {
                onToggle(option.value);
              }
            }}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

export function PlannerMultiSelectDropdown({ value, options, onToggle, portalHostRef }) {
  const selectedValues = Array.isArray(value) ? value : [];
  const selectedLabels = selectedValues
    .map((selectedValue) => options.find((option) => option.value === selectedValue)?.label || selectedValue)
    .filter(Boolean);

  return (
    <WorkbenchDropdown
      value={selectedLabels.length > 0 ? selectedLabels.join(" / ") : "--"}
      onSelect={onToggle}
      options={options.map((option) => ({
        ...option,
        key: option.value,
        active: selectedValues.includes(option.value),
        content: (
          <span className="dw-planner-multi-option">
            <span className={`dw-planner-multi-check ${selectedValues.includes(option.value) ? "is-checked" : ""}`} aria-hidden="true">
            </span>
            <span className="dw-planner-multi-label">{option.label}</span>
          </span>
        )
      }))}
      className="dw-planner-dropdown-field dw-planner-multi-dropdown"
      variant="field"
      positioning="portal"
      portalHostRef={portalHostRef}
      closeOnSelect={false}
    />
  );
}

export function PlannerInput({ value, onChange, suffix = "", placeholder = "", mode = "text" }) {
  return (
    <div className="dw-planner-input-shell">
      <input
        className="dw-workbench-input dw-planner-input-core"
        value={value}
        inputMode={mode === "numeric" ? "numeric" : "text"}
        placeholder={placeholder}
        onChange={(event) => onChange(event.target.value)}
      />
      {suffix ? <span className="dw-planner-input-suffix">{suffix}</span> : null}
    </div>
  );
}

export function PlannerTimeInput({ value, onCommit, placeholder = "", onInvalidChange = null }) {
  const [draftValue, setDraftValue] = useState(String(value || ""));
  const [isInvalid, setIsInvalid] = useState(false);

  useEffect(() => {
    setDraftValue(String(value || ""));
    setIsInvalid(false);
    onInvalidChange?.(false);
  }, [onInvalidChange, value]);

  function setInvalidState(nextInvalid) {
    setIsInvalid(nextInvalid);
    onInvalidChange?.(nextInvalid);
  }

  function commitCurrentValue() {
    const nextValue = normalizeTimeInput(draftValue);
    if (!isValidTimeValue(nextValue)) {
      setDraftValue(nextValue);
      setInvalidState(true);
      return;
    }

    setDraftValue(nextValue);
    setInvalidState(false);
    onCommit(nextValue);
  }

  return (
    <div className={`dw-planner-input-shell ${isInvalid ? "is-error" : ""}`}>
      <input
        className="dw-workbench-input dw-planner-input-core"
        value={draftValue}
        inputMode="numeric"
        maxLength={5}
        placeholder={placeholder}
        onChange={(event) => {
          const nextValue = normalizeTimeInput(event.target.value);
          setDraftValue(nextValue);
          setInvalidState(nextValue.length === 5 && !isValidTimeValue(nextValue));
        }}
        onPaste={(event) => {
          event.preventDefault();
          const pastedText = event.clipboardData?.getData("text") || "";
          setDraftValue(normalizeTimeInput(pastedText));
        }}
        onBlur={commitCurrentValue}
        onFocus={(event) => {
          if (typeof event.target.select === "function") {
            setTimeout(() => event.target.select(), 0);
          }
        }}
        onKeyDown={(event) => {
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

          if (event.key === "Enter") {
            commitCurrentValue();
            event.currentTarget.blur();
          }
        }}
      />
    </div>
  );
}

export function PlannerMetric({ label, value, tone = "default" }) {
  return (
    <div className="dw-planner-metric">
      <div className="dw-planner-metric-label">{label}</div>
      <div className={`dw-planner-metric-value is-${tone}`}>{value}</div>
    </div>
  );
}
