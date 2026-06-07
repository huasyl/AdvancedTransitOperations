export default function OverviewSystemSwitches({ systems, onSystemToggle }) {
  return (
    <div className="rtw-overview-switches">
      <div className="rtw-overview-section-label">功能开关 / TOGGLES</div>
      {systems.map((system) => (
        <button
          key={system.key}
          type="button"
          className={`rtw-overview-switch-row ${system.enabled ? "is-on" : "is-off"}`}
          onClick={() => onSystemToggle(system.key)}
        >
          <span className="rtw-overview-switch-title">{system.title}</span>
          <span className="rtw-overview-switch-track">
            <span className="rtw-overview-switch-thumb" />
          </span>
        </button>
      ))}
    </div>
  );
}
