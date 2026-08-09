import { useNativeScheduleI18n } from "../../../shared/workbench-i18n";

// Overview rail icons are adapted from Lucide Icons under the ISC license.
// See THIRD_PARTY_NOTICES.txt at the repository root.
function SubwayIcon() {
  return (
    <svg className="rtw-overview-mode-svg" viewBox="0 0 24 24" aria-hidden="true">
      <rect width="16" height="16" x="4" y="3" rx="2" />
      <path d="M4 11h16" />
      <path d="M12 3v8" />
      <path d="m8 19-2 3" />
      <path d="m18 22-2-3" />
      <path d="M8 15h.01" />
      <path d="M16 15h.01" />
    </svg>
  );
}

function TrainIcon() {
  return (
    <svg className="rtw-overview-mode-svg" viewBox="0 0 24 24" aria-hidden="true">
      <path d="M8 3.1V7a4 4 0 0 0 8 0V3.1" />
      <path d="m9 15-1-1" />
      <path d="m15 15 1-1" />
      <path d="M9 19c-2.8 0-5-2.2-5-5v-4a8 8 0 0 1 16 0v4c0 2.8-2.2 5-5 5Z" />
      <path d="m8 19-2 3" />
      <path d="m16 19 2 3" />
    </svg>
  );
}

function TramIcon() {
  return (
    <svg className="rtw-overview-mode-svg rtw-overview-tram-svg" viewBox="0 0 32 32" aria-hidden="true">
      <path className="rtw-overview-tram-path" stroke="none" d="M21 6h-4V4h6V2H9v2h6v2h-4a5.006 5.006 0 0 0-5 5v11a4.99 4.99 0 0 0 3.582 4.77L8.198 30h2.176l1.285-3h8.682l1.286 3h2.175l-1.384-3.23A4.99 4.99 0 0 0 26 22V11a5.006 5.006 0 0 0-5-5M11 8h10a2.995 2.995 0 0 1 2.816 2H8.184A2.995 2.995 0 0 1 11 8m13 13h-3v2h2.816A2.995 2.995 0 0 1 21 25H11a2.995 2.995 0 0 1-2.816-2H11v-2H8v-2h16Zm0-4H8v-5h16Z" />
    </svg>
  );
}

function BusIcon() {
  return (
    <svg className="rtw-overview-mode-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M8 6v6" />
      <path d="M15 6v6" />
      <path d="M2 12h19.6" />
      <path d="M18 18h3s.5-1.7.8-2.8c.1-.4.2-.8.2-1.2 0-.4-.1-.8-.2-1.2l-1.4-5C20.1 6.8 19.1 6 18 6H4a2 2 0 0 0-2 2v10h3" />
      <circle cx="7" cy="18" r="2" />
      <path d="M9 18h5" />
      <circle cx="16" cy="18" r="2" />
    </svg>
  );
}

function getModeIcon(mode, label) {
  if (mode === "Subway") {
    return <SubwayIcon />;
  }
  if (mode === "Train") {
    return <TrainIcon />;
  }
  if (mode === "Tram") {
    return <TramIcon />;
  }
  if (mode === "Bus") {
    return <BusIcon />;
  }
  return label.slice(0, 1);
}

export default function OverviewModeRail({ modes, activeMode, onModeChange }) {
  const { t } = useNativeScheduleI18n();

  return (
    <div className="rtw-overview-mode-rail">
      <div className="rtw-overview-section-label">{t("nativeWorkbench.overview.section.mode")}</div>
      <div className="rtw-overview-mode-list">
        {modes.map((mode) => (
          <button
            key={mode.mode}
            type="button"
            className={`rtw-overview-mode-button ${activeMode === mode.mode ? "is-active" : ""}`}
            onClick={() => onModeChange(mode.mode)}
          >
            <span className="rtw-overview-mode-icon">{getModeIcon(mode.mode, mode.label)}</span>
            <span className="rtw-overview-mode-main">
              <span className="rtw-overview-mode-name">{mode.label}</span>
              <span className="rtw-overview-mode-meta">{t("nativeWorkbench.overview.mode.meta", { lineCount: mode.lineCount, appliedDepartureCount: mode.appliedDepartureCount })}</span>
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}
