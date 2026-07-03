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

function getModeIcon(mode, label) {
  if (mode === "Subway") {
    return <SubwayIcon />;
  }
  if (mode === "Train") {
    return <TrainIcon />;
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
