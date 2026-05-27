import React from "react";
import ReactDOM from "react-dom/client";
import PlannerWorkbenchPage from "./PlannerWorkbenchPage.jsx";
import { NativeScheduleI18nProvider } from "./native-schedule-i18n";
import "./styles/native-schedule-demo.css";
import "./styles/native-planner-page.css";

const CALLS = {
  loadPlannerContext: "suhua::rt.workbench.loadPlannerContext",
  runPlanner: "suhua::rt.workbench.runPlanner",
  getLocale: "suhua::rt.workbench.getLocale"
};

async function readJsonResponse(response, fallbackValue) {
  if (!response.ok) {
    return fallbackValue;
  }

  try {
    return await response.json();
  } catch {
    return fallbackValue;
  }
}

function installPlannerLabEngine() {
  window.__RT_PLANNER_LAB__ = true;
  window.__RT_NATIVE_SCHEDULE_LOCALE__ = "zh-CN";
  document.documentElement.style.backgroundColor = "#0a1013";
  document.body.style.backgroundColor = "#0a1013";
  document.body.style.margin = "0";
  window.engine = {
    async call(name, payload) {
      if (name === CALLS.loadPlannerContext) {
        return readJsonResponse(await fetch("/api/planner-lab/context"), {});
      }
      if (name === CALLS.runPlanner) {
        return readJsonResponse(await fetch("/api/planner-lab/run", {
          method: "POST",
          headers: {
            "Content-Type": "application/json"
          },
          body: payload || "{}"
        }), { success: false, plans: [], diagnostics: [] });
      }
      if (name === CALLS.getLocale) {
        return "zh-CN";
      }
      return {};
    },
    on() {},
    off() {}
  };
}

function PlannerLabApp() {
  return (
    <NativeScheduleI18nProvider>
      <div className="dw-native-schedule-root dw-native-workbench-root" data-planner-lab="true">
        <PlannerWorkbenchPage pageEnterSequence={1} />
      </div>
    </NativeScheduleI18nProvider>
  );
}

installPlannerLabEngine();

ReactDOM.createRoot(document.getElementById("root")).render(<PlannerLabApp />);
