using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class OptimizationRegionBuilder
    {
        public List<PlannerRiskCluster> BuildRiskClusters(
            PlannerContext context,
            List<PlannerCatchupEvent> catchupEvents)
        {
            Dictionary<string, PlannerRiskCluster> clustersByKey = new Dictionary<string, PlannerRiskCluster>(StringComparer.Ordinal);
            foreach (PlannerCatchupEvent catchupEvent in catchupEvents)
            {
                if (string.Equals(catchupEvent.PairRole, "fixed-fixed", StringComparison.Ordinal))
                {
                    context.SuppressedFixedVsFixedClusterCount += 1;
                    continue;
                }

                string key = catchupEvent.LocalLineId + "|" + catchupEvent.ExpressLineId + "|" + catchupEvent.TrunkId;
                if (!clustersByKey.TryGetValue(key, out PlannerRiskCluster cluster))
                {
                    cluster = new PlannerRiskCluster();
                    cluster.ClusterId = key;
                    cluster.LocalLineId = catchupEvent.LocalLineId;
                    cluster.ExpressLineId = catchupEvent.ExpressLineId;
                    cluster.PairRole = catchupEvent.PairRole;
                    cluster.IsPrimaryPlanningRisk = string.Equals(catchupEvent.PairRole, "target-adjustable", StringComparison.Ordinal);
                    cluster.ResolutionState = "resolved";
                    cluster.YieldingLineId = string.IsNullOrEmpty(catchupEvent.YieldingLineId) ? catchupEvent.LocalLineId : catchupEvent.YieldingLineId;
                    cluster.PriorityLineId = string.IsNullOrEmpty(catchupEvent.PriorityLineId) ? catchupEvent.ExpressLineId : catchupEvent.PriorityLineId;
                    cluster.FromStationId = catchupEvent.FromStationId;
                    cluster.ToStationId = catchupEvent.ToStationId;
                    clustersByKey[key] = cluster;
                }

                cluster.CatchupCount += 1;
                cluster.MaxSeverityMinutes = Math.Max(cluster.MaxSeverityMinutes, catchupEvent.SeverityMinutes);
                cluster.UnresolvedRiskMinutes = PlannerMath.Round2(cluster.UnresolvedRiskMinutes + catchupEvent.UnresolvedRiskMinutes);
                cluster.RobustnessRiskMinutes = PlannerMath.Round2(cluster.RobustnessRiskMinutes + catchupEvent.RobustnessRiskMinutes);
                cluster.TotalExpressSavedMinutes = PlannerMath.Round2(cluster.TotalExpressSavedMinutes + catchupEvent.ExpressSavedMinutes);
                cluster.TotalLocalWaitMinutes = PlannerMath.Round2(cluster.TotalLocalWaitMinutes + catchupEvent.ResolvedHoldMinutes);
                cluster.ResolutionState = MergeResolutionState(cluster.ResolutionState, catchupEvent.ResolutionState);
                cluster.CatchupIds.Add(catchupEvent.EventId);
                cluster.SourceCorridorIds = cluster.SourceCorridorIds
                    .Union(catchupEvent.SourceCorridorIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (cluster.RecommendedBypassStation == null || catchupEvent.SeverityMinutes >= cluster.MaxSeverityMinutes)
                {
                    cluster.RecommendedBypassStation = catchupEvent.SelectedBypassStation;
                }
            }

            return clustersByKey.Values
                .OrderByDescending(cluster => cluster.UnresolvedRiskMinutes)
                .ThenByDescending(cluster => cluster.RobustnessRiskMinutes)
                .ThenByDescending(cluster => cluster.MaxSeverityMinutes)
                .ToList();
        }

        private static string MergeResolutionState(string current, string next)
        {
            int currentRank = GetResolutionRank(current);
            int nextRank = GetResolutionRank(next);
            return nextRank > currentRank ? next ?? string.Empty : current ?? string.Empty;
        }

        private static int GetResolutionRank(string state)
        {
            if (string.Equals(state, "blocked", StringComparison.Ordinal))
            {
                return 3;
            }
            if (string.Equals(state, "actionable", StringComparison.Ordinal))
            {
                return 2;
            }
            if (string.Equals(state, "resolved", StringComparison.Ordinal))
            {
                return 1;
            }
            return 0;
        }

        public List<PlannerOptimizationRegion> BuildOptimizationRegions(List<PlannerRiskCluster> clusters)
        {
            List<PlannerOptimizationRegion> regions = new List<PlannerOptimizationRegion>();
            List<PlannerRiskCluster> ordered = clusters
                .OrderBy(cluster => cluster.LocalLineId)
                .ThenBy(cluster => cluster.ExpressLineId)
                .ThenByDescending(cluster => cluster.UnresolvedRiskMinutes + cluster.RobustnessRiskMinutes)
                .ToList();

            foreach (PlannerRiskCluster cluster in ordered)
            {
                PlannerOptimizationRegion region = regions.FirstOrDefault(candidate =>
                    candidate.YieldingLineIds.Contains(cluster.YieldingLineId)
                    || candidate.PriorityLineIds.Contains(cluster.PriorityLineId));
                if (region == null)
                {
                    region = new PlannerOptimizationRegion();
                    region.RegionId = "region-" + regions.Count;
                    regions.Add(region);
                }

                AddUnique(region.ClusterIds, cluster.ClusterId);
                AddUnique(region.YieldingLineIds, string.IsNullOrEmpty(cluster.YieldingLineId) ? cluster.LocalLineId : cluster.YieldingLineId);
                AddUnique(region.PriorityLineIds, string.IsNullOrEmpty(cluster.PriorityLineId) ? cluster.ExpressLineId : cluster.PriorityLineId);
                region.EventCount += cluster.CatchupCount;
                region.TotalUnresolvedRiskMinutes = PlannerMath.Round2(region.TotalUnresolvedRiskMinutes + cluster.UnresolvedRiskMinutes);
                region.TotalRobustnessRiskMinutes = PlannerMath.Round2(region.TotalRobustnessRiskMinutes + cluster.RobustnessRiskMinutes);
            }

            return regions
                .OrderByDescending(region => region.TotalUnresolvedRiskMinutes)
                .ThenByDescending(region => region.TotalRobustnessRiskMinutes)
                .ToList();
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (!string.IsNullOrEmpty(value) && !values.Contains(value))
            {
                values.Add(value);
            }
        }
    }
}
