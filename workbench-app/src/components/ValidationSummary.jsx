import { SafeControlText } from "./ChoiceButtons";
import { useI18n } from "../lib/i18n";

export default function ValidationSummary({
  validatedRows = [],
  validationIssues = [],
  previewSummary,
  actionMessage
}) {
  const { t } = useI18n();
  const summary = previewSummary ?? {
    title: t("validation.previewTitle"),
    description: t("validation.previewUnavailable"),
    generatedTrips: 0,
    earliestStart: "--:--",
    issueCount: 0
  };
  const errorCount = validationIssues.filter((issue) => issue.severity === "error").length;
  const warningCount = validationIssues.filter((issue) => issue.severity === "warning").length;

  return (
    <section className="dw-panel">
      <div className="dw-panel-header">
        <SafeControlText>{t("schedule.panel.validation")}</SafeControlText>
      </div>
      <div className="dw-panel-body">
        <div className="dw-summary-metrics dw-summary-metrics-3">
          <div className="dw-metric">
            <span className="dw-metric-label">
              <SafeControlText>{t("validation.rows")}</SafeControlText>
            </span>
            <span className="dw-metric-value">{validatedRows.length}</span>
          </div>
          <div className="dw-metric">
            <span className="dw-metric-label">
              <SafeControlText>{t("validation.errors")}</SafeControlText>
            </span>
            <span className={`dw-metric-value ${errorCount ? "is-bad" : "is-good"}`}>
              {errorCount}
            </span>
          </div>
          <div className="dw-metric">
            <span className="dw-metric-label">
              <SafeControlText>{t("validation.warnings")}</SafeControlText>
            </span>
            <span className="dw-metric-value">{warningCount}</span>
          </div>
        </div>

        <div className={`dw-validation-box ${errorCount ? "is-error" : "is-ok"}`}>
          <SafeControlText>{summary.description}</SafeControlText>
        </div>

        <div className="dw-preview-box">
          <div className="dw-preview-title">
            <SafeControlText>{summary.title}</SafeControlText>
          </div>
          <div className="dw-preview-grid">
            <div>
              <span className="dw-preview-label">
                <SafeControlText>{t("validation.generatedTrips")}</SafeControlText>
              </span>
              <strong>{summary.generatedTrips}</strong>
            </div>
            <div>
              <span className="dw-preview-label">
                <SafeControlText>{t("validation.earliestStart")}</SafeControlText>
              </span>
              <strong>{summary.earliestStart}</strong>
            </div>
            <div>
              <span className="dw-preview-label">
                <SafeControlText>{t("validation.openIssues")}</SafeControlText>
              </span>
              <strong>{summary.issueCount}</strong>
            </div>
          </div>
        </div>

        {actionMessage ? (
          <div className="dw-inline-notice">
            <SafeControlText>{actionMessage}</SafeControlText>
          </div>
        ) : null}

        {validationIssues.length > 0 ? (
          <ul className="dw-validation-list">
            {validationIssues.map((issue) => (
              <li key={`${issue.rowId}-${issue.message}`}>
                <SafeControlText>{issue.message}</SafeControlText>
              </li>
            ))}
          </ul>
        ) : (
          <ul className="dw-validation-list">
            <li>
              <SafeControlText>{t("validation.manualRowsPass")}</SafeControlText>
            </li>
            <li>
              <SafeControlText>{t("validation.previewOrSave")}</SafeControlText>
            </li>
          </ul>
        )}
      </div>
    </section>
  );
}
