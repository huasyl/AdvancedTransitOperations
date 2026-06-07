import usePlannerController from "./usePlannerController.js";
import PlannerResults from "./components/PlannerResults.jsx";
import PlannerSidebar from "./components/PlannerSidebar.jsx";

export default function PlannerPage({ pageEnterSequence = 0, activeTransportMode = "train" }) {
  const planner = usePlannerController({ pageEnterSequence, activeTransportMode });

  return (
    <div className="dw-planner-page">
      <div className="dw-planner-shell">
        <PlannerSidebar sidebar={planner.sidebar} refs={planner.refs} actions={planner.actions} />
        <PlannerResults result={planner.result} preview={planner.preview} actions={planner.actions} />
      </div>
      <div ref={planner.refs.dropdownPortalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}
