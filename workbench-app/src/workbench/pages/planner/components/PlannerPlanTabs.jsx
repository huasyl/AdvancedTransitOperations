export default function PlannerPlanTabs({ plans, activeId, onChange, labels }) {
  return (
    <div className="dw-planner-plan-tabs">
      {plans.map((plan) => {
        const isActive = activeId === plan.id;
        return (
          <button
            key={plan.id}
            type="button"
            className={`dw-planner-plan-tab ${isActive ? "is-active" : ""}`}
            onClick={() => onChange(plan.id)}
          >
            <span className="dw-planner-plan-tab-title">{plan.title}</span>
            <span className={`dw-planner-plan-badge is-${plan.type}`}>
              {plan.badgeLabel || (plan.type === "optimal"
                ? labels.feasible
                : plan.type === "warning"
                  ? labels.risk
                  : labels.infeasible)}
            </span>
          </button>
        );
      })}
    </div>
  );
}
