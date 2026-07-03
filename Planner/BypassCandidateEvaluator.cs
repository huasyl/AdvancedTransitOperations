using System.Collections.Generic;

namespace RapidTransitMod.Planner
{
    internal sealed class BypassCandidateEvaluator
    {
        public void Enrich(List<PlannerRiskCluster> clusters, PlannerContext context)
        {
            for (int i = 0; i < clusters.Count; i++)
            {
                PlannerRiskCluster cluster = clusters[i];
                cluster.RecommendedActionCodes.Clear();
                if (cluster.UnresolvedRiskMinutes > 0f)
                {
                    cluster.RecommendedActionCodes.Add("addBypassStation");
                    cluster.RecommendedActionCodes.Add("relaxWaitLimit");
                    cluster.RecommendedActionCodes.Add("shiftExpressOffset");
                }
                else if (cluster.RobustnessRiskMinutes > 0f)
                {
                    cluster.RecommendedActionCodes.Add("addBuffer");
                    cluster.RecommendedActionCodes.Add("retimeLocalTrip");
                }
                else
                {
                    cluster.RecommendedActionCodes.Add("keepCurrentPlan");
                }

                if (context.Request.maxAdditionalBypassStations > 0
                    && cluster.RecommendedBypassStation != null)
                {
                    cluster.RecommendedActionCodes.Add("preferBypassStation");
                }
            }
        }
    }
}
