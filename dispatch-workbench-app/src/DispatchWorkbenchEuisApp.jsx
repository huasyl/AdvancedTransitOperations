import DispatchWorkbenchContent from "./DispatchWorkbenchContent";
import { SafeControlText } from "./components/ChoiceButtons";
import { useI18n } from "./lib/i18n";
import TopTabs from "./components/TopTabs";
import { useWorkbenchController } from "./lib/useWorkbenchController";

// This is the only real application shell.
// All production-facing frontend validation must happen inside EUIS.
export default function DispatchWorkbenchEuisApp() {
  const { t } = useI18n();
  const controller = useWorkbenchController();

  return (
    <div className="dw-euis-shell">
      <div className="dw-euis-frame">
        <header className="dw-euis-header">
          <div className="dw-title-block">
            <h1>
              <SafeControlText>{`${t("app.title")}${t("app.reloadTestSuffix")}`}</SafeControlText>
            </h1>
          </div>
        </header>

        <div className="dw-euis-tabs">
          <TopTabs activeTab={controller.activeTab} onChange={controller.setActiveTab} />
        </div>

        <section className="dw-euis-body">
          <DispatchWorkbenchContent shellMode="euis" {...controller} />
        </section>
      </div>
    </div>
  );
}
