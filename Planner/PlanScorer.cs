using System;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class PlanScorer
    {
        public void Apply(PlannerPlanModel plan)
        {
            PlannerObjectiveDefinition objective = PlannerDefaults.Objectives.FirstOrDefault(definition =>
                string.Equals(definition.Id, plan.ObjectiveId, StringComparison.Ordinal))
                ?? PlannerDefaults.Objectives[0];

            float score =
                (plan.ExpressSavedMinutes * objective.ExpressBenefitWeight)
                - (plan.LocalWaitMinutes * objective.LocalWaitWeight)
                - (plan.UnresolvedRiskMinutes * objective.UnresolvedRiskWeight)
                - (plan.RobustnessRiskMinutes * objective.RobustnessRiskWeight)
                - (plan.AddedBypassStationCount * objective.BypassStationWeight);
            plan.Score = PlannerMath.Round2(score);
        }
    }
}
