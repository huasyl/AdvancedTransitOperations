export default function OverviewModeRail({ modes, activeMode, onModeChange }) {
  return (
    <div className="rtw-overview-mode-rail">
      <div className="rtw-overview-section-label">交通网模式 / MODE</div>
      <div className="rtw-overview-mode-list">
        {modes.map((mode) => (
          <button
            key={mode.mode}
            type="button"
            className={`rtw-overview-mode-button ${activeMode === mode.mode ? "is-active" : ""}`}
            onClick={() => onModeChange(mode.mode)}
          >
            <span className="rtw-overview-mode-icon">{mode.label.slice(0, 1)}</span>
            <span className="rtw-overview-mode-main">
              <span className="rtw-overview-mode-name">{mode.label}</span>
              <span className="rtw-overview-mode-meta">{mode.lineCount} lines / {mode.scheduledVehicleCount} scheduled</span>
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}
