import { ControlText } from "./ChoiceButtons";
import { useI18n } from "../lib/i18n";

const TABS = [
  { key: "overview", labelKey: "tab.overview" },
  { key: "schedule", labelKey: "tab.schedule" }
];

export default function TopTabs({ activeTab, onChange }) {
  const { t } = useI18n();

  return (
    <div className="dw-top-tabs">
      {TABS.map((tab) => (
        <button
          key={tab.key}
          type="button"
          className={`dw-top-tab ${activeTab === tab.key ? "is-active" : ""}`}
          onClick={() => onChange(tab.key)}
        >
          <ControlText>{t(tab.labelKey)}</ControlText>
        </button>
      ))}
    </div>
  );
}
