export function SafeControlText({ children }) {
  return (
    <span className="dw-ui-text">
      <span className="dw-ui-text-line">{children}</span>
    </span>
  );
}

// ControlText is the only approved path for button-like UI text.
// Do not replace it with SafeControlText or raw strings inside controls.
export function ControlText({ children }) {
  return <span className="dw-control-text">{children}</span>;
}

export function ChoiceButtons({
  options = [],
  value,
  onChange,
  className = "",
  compact = false,
  disabled = false
}) {
  const groupClassName = `dw-choice-group${compact ? " is-compact" : ""}${className ? ` ${className}` : ""}`;

  return (
    <div className={groupClassName}>
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          className={`dw-choice-btn ${value === option.value ? "is-active" : ""}`}
          title={option.title || option.label}
          disabled={disabled}
          onClick={() => {
            if (disabled) {
              return;
            }

            onChange?.(option.value);
          }}
        >
          <ControlText>{option.label}</ControlText>
        </button>
      ))}
    </div>
  );
}

export function ToggleChoice({ checked, label, onToggle }) {
  return (
    <button
      type="button"
      className={`dw-toggle-btn ${checked ? "is-active" : ""}`}
      onClick={() => onToggle?.(!checked)}
    >
      <span className={`dw-check-mark ${checked ? "is-checked" : ""}`} aria-hidden="true" />
      <ControlText>{label}</ControlText>
    </button>
  );
}
