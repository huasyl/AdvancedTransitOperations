import { useEffect, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";
import { getLocalizedLineType } from "../schedule-catalog";
import { isValidTimeValue, normalizeTimeInput } from "../schedule-normalize";

export function DemoTextField({
  label,
  value,
  onCommit,
  className,
  placeholder,
  readOnly = false,
  inputRef,
  timeMode = false,
  nextInputRef,
  onDraftChange,
  errorText = "",
  preserveInvalidTime = false,
  reserveErrorSpace = false,
  suffix = ""
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
        if (!preserveInvalidTime) {
          setDraftValue(String(value ?? ""));
        }
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
          <span>{label}</span>
        </span>
      </label>
      <div className={`dw-demo-input-row ${suffix ? "has-suffix" : ""}`}>
        <input
          ref={resolvedInputRef}
          type="text"
          value={draftValue}
          placeholder={placeholder}
          readOnly={readOnly}
          className={`dw-demo-input ${suffix ? "has-suffix" : ""} ${readOnly ? "is-readonly" : ""} ${errorText ? "is-error" : ""}`}
          inputMode={timeMode ? "numeric" : "text"}
          maxLength={timeMode ? 5 : undefined}
          onChange={(event) => {
            const rawValue = event.currentTarget.value;
            const nextDraftValue = timeMode ? normalizeTimeInput(rawValue) : rawValue;
            setDraftValue(nextDraftValue);
            if (typeof onDraftChange === "function") {
              onDraftChange(nextDraftValue);
            }
          }}
          onPaste={(event) => {
            if (!timeMode) {
              return;
            }

            event.preventDefault();
            const pastedText = event.clipboardData?.getData("text") || "";
            const nextDraftValue = normalizeTimeInput(pastedText);
            setDraftValue(nextDraftValue);
            if (typeof onDraftChange === "function") {
              onDraftChange(nextDraftValue);
            }
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
        {suffix ? <span className="dw-demo-input-suffix">{suffix}</span> : null}
      </div>
      <div className={`dw-demo-field-error-slot ${reserveErrorSpace ? "is-reserved" : ""} ${errorText ? "has-error" : ""}`}>
        {errorText ? <div className="dw-demo-field-error">{errorText}</div> : null}
      </div>
    </div>
  );
}

export function DemoDisplayField({
  label,
  value,
  className
}) {
  return (
    <div className={`dw-demo-field ${className || ""}`}>
      <label className="dw-demo-label">
        <span className="dw-demo-label-content">
          <span>{label}</span>
        </span>
      </label>
      <div className="dw-demo-display-value" title={value || ""}>
        <span>{value}</span>
      </div>
    </div>
  );
}
export function SummaryBadge({ kind }) {
  const { t } = useNativeScheduleI18n();

  return (
    <span className={`dw-demo-badge ${kind === "express" ? "is-express" : "is-local"}`}>
      {getLocalizedLineType(kind, t, "compact")}
    </span>
  );
}
export function DemoOffsetField({
  label,
  direction,
  minutes,
  onDirectionChange,
  onMinutesChange,
  className,
  hint
}) {
  const { t } = useNativeScheduleI18n();

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
            {t("nativeSchedule.toggle.early")}
          </button>
          <button
            type="button"
            className={`dw-demo-offset-toggle-button ${direction === "late" ? "is-active" : ""}`}
            onClick={() => onDirectionChange(direction === "late" ? "" : "late")}
          >
            {t("nativeSchedule.toggle.late")}
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
