import { ControlText, SafeControlText } from "./ChoiceButtons";
import { useI18n } from "../lib/i18n";

export default function SideContextPanel({
  title,
  lineSummary = [],
  modeSummary = [],
  validationSummary = [],
  suggestedActions = [],
  onActionClick
}) {
  const { t } = useI18n();

  return (
    <aside className="dw-panel">
      <div className="dw-panel-header">
        <SafeControlText>{title}</SafeControlText>
      </div>
      <div className="dw-panel-body dw-side-stack">
        <section className="dw-context-block">
          <h4>
            <SafeControlText>{t("side.summary")}</SafeControlText>
          </h4>
          <dl>
            {lineSummary.map((item) => (
              <div className="dw-context-pair" key={item.label}>
                <dt>
                  <SafeControlText>{item.label}</SafeControlText>
                </dt>
                <dd>
                  <SafeControlText>{item.value}</SafeControlText>
                </dd>
              </div>
            ))}
          </dl>
        </section>

        <section className="dw-context-block">
          <h4>
            <SafeControlText>{t("side.notes")}</SafeControlText>
          </h4>
          <ul className="dw-plain-list">
            {modeSummary.map((item) => (
              <li key={item}>
                <SafeControlText>{item}</SafeControlText>
              </li>
            ))}
          </ul>
        </section>

        <section className="dw-context-block">
          <h4>
            <SafeControlText>{t("side.validation")}</SafeControlText>
          </h4>
          <ul className="dw-plain-list">
            {validationSummary.map((item) => (
              <li key={item}>
                <SafeControlText>{item}</SafeControlText>
              </li>
            ))}
          </ul>
        </section>

        <section className="dw-context-block">
          <h4>
            <SafeControlText>{t("side.actions")}</SafeControlText>
          </h4>
          <div className="dw-suggested-actions">
            {suggestedActions.map((action) => (
              <button
                key={action.key}
                type="button"
                className="dw-btn"
                onClick={() => onActionClick?.(action.key)}
              >
                <ControlText>{action.label}</ControlText>
              </button>
            ))}
          </div>
        </section>
      </div>
    </aside>
  );
}
