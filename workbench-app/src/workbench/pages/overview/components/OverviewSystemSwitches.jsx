export default function OverviewSystemSwitches({ systems }) {
  return (
    <div className="rtw-overview-switches">
      <div className="rtw-overview-section-label">功能开关 / TOGGLES</div>
      {systems.map((system) => (
        <div key={system.key} className={`rtw-overview-switch-row ${system.enabled ? "is-on" : "is-off"}`}>
          <span className="rtw-overview-switch-title">{system.title}</span>
          <span className="rtw-overview-switch-track">
            <span className="rtw-overview-switch-thumb" />
          </span>
        </div>
      ))}
    </div>
  );
}
