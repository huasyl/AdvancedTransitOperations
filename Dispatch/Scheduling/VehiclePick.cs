using Unity.Entities;
using RapidTransitMod.Core;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal sealed class VehiclePick
    {
        internal readonly struct Result
        {
            public readonly Entity Vehicle;
            public readonly int Tier;
            public readonly float EtaFrames;
            public readonly float Range;
            public readonly int PreviousTargetMinute;
            public readonly Entity NearVehicle;
            public readonly VehicleState NearState;
            public readonly float NearestEtaFrames;
            public readonly string NearReason;

            public Result(
                Entity vehicle,
                int tier,
                float etaFrames,
                float range,
                int previousTargetMinute,
                Entity nearVehicle,
                VehicleState nearState,
                float nearestEtaFrames,
                string nearReason)
            {
                Vehicle = vehicle;
                Tier = tier;
                EtaFrames = etaFrames;
                Range = range;
                PreviousTargetMinute = previousTargetMinute;
                NearVehicle = nearVehicle;
                NearState = nearState;
                NearestEtaFrames = nearestEtaFrames;
                NearReason = nearReason;
            }
        }

        private readonly DispatchRuntimeSystem m_Runtime;

        public VehiclePick(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public Result Pick(
            LineTick tick,
            ClockSnapshot clockSnapshot,
            int slotMinute,
            int minutesToSlot,
            bool spawnOnlyScan,
            float slotFramesAway)
        {
            Entity bestVehicle = Entity.Null;
            int bestTier = 99;
            float bestEtaFrames = float.MaxValue;
            float bestRange = -1f;
            int bestPreviousTargetMinute = -1;
            Entity nearestVehicle = Entity.Null;
            VehicleState nearestState = VehicleState.Preparing;
            float nearestEtaFrames = float.MaxValue;
            string nearestReason = "none";

            for (int i = 0; i < tick.Vehicles.Count; i++)
            {
                Entity vehicle = tick.Vehicles[i];
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                    continue;
                if (state == VehicleState.Retiring || m_Runtime.m_BVMisfire.Contains(vehicle))
                    continue;

                int assignedTargetMinute = -1;
                if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute) && targetMinute >= 0)
                {
                    assignedTargetMinute = targetMinute;
                    if (targetMinute != slotMinute)
                    {
                        if (ScheduleClock.CurrentOrRecent(tick.NowMinute, targetMinute))
                            continue;
                        int minutesToAssigned = ScheduleClock.MinutesUntil(tick.NowMinute, targetMinute);
                        if (minutesToAssigned <= minutesToSlot)
                            continue;
                    }
                }

                if (state == VehicleState.Running
                    && assignedTargetMinute >= 0
                    && assignedTargetMinute != slotMinute
                    && ScheduleClock.CurrentOrRecent(tick.NowMinute, assignedTargetMinute)
                    && m_Runtime.m_LineProfile.IsBorderlineOriginArrivalCandidate(vehicle, tick.Ways))
                {
                    continue;
                }

                if (m_Runtime.m_LineRange.Needs(vehicle) || !m_Runtime.m_LineRange.CanFinish(vehicle))
                    continue;

                int tier = 99;
                float etaFrames = float.MaxValue;

                if (state == VehicleState.Idle || state == VehicleState.Holding)
                {
                    if (spawnOnlyScan)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEtaFrames = 0f;
                            nearestReason = "outside-origin-hold-window";
                        }
                        continue;
                    }

                    int cachedWaypointIndex = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypoint) ? cachedWaypoint : -1;
                    if (cachedWaypointIndex != 0)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEtaFrames = 0f;
                            nearestReason = "cachedWp=" + cachedWaypointIndex;
                        }
                        continue;
                    }

                    tier = 0;
                    etaFrames = 0f;
                }
                else if (state == VehicleState.Running)
                {
                    float runningEtaFrames = m_Runtime.m_LineTimes.Run(
                        vehicle,
                        tick.Line,
                        tick.Ways,
                        tick.NowFrame,
                        tick.LapFrames,
                        tick.Run);
                    if (runningEtaFrames == float.MaxValue)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEtaFrames = float.MaxValue;
                            nearestReason = "no-running-eta";
                        }
                        continue;
                    }

                    tier = 1;
                    etaFrames = runningEtaFrames;
                }
                else if (state == VehicleState.Preparing)
                {
                    float preparingEtaFrames = m_Runtime.m_LineTimes.Prep(vehicle, tick.Line, tick.Ways, tick.LapFrames);
                    if (preparingEtaFrames == float.MaxValue)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEtaFrames = float.MaxValue;
                            nearestReason = "no-preparing-eta";
                        }
                        continue;
                    }

                    tier = 1;
                    etaFrames = preparingEtaFrames;
                }
                else
                {
                    continue;
                }

                if (etaFrames > slotFramesAway)
                {
                    if (etaFrames < nearestEtaFrames || nearestVehicle == Entity.Null)
                    {
                        nearestVehicle = vehicle;
                        nearestState = state;
                        nearestEtaFrames = etaFrames;
                        nearestReason = "late-for-slotMinute";
                    }
                    continue;
                }

                if (spawnOnlyScan)
                {
                    float earliestHoldArrivalFrames = slotFramesAway
                        - clockSnapshot.ToFramesCeil(tick.HoldMinutes);
                    if (earliestHoldArrivalFrames > 0f && etaFrames < earliestHoldArrivalFrames)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEtaFrames = etaFrames;
                            nearestReason = "before-origin-hold-window";
                        }
                        continue;
                    }
                }

                float range = m_Runtime.m_LineRange.Left(vehicle);
                bool better = tier < bestTier
                    || (tier == bestTier && etaFrames < bestEtaFrames)
                    || (tier == bestTier && etaFrames == bestEtaFrames && range > bestRange);
                if (better)
                {
                    bestVehicle = vehicle;
                    bestTier = tier;
                    bestEtaFrames = etaFrames;
                    bestRange = range;
                    bestPreviousTargetMinute = assignedTargetMinute;
                }
            }

            return new Result(
                bestVehicle,
                bestTier,
                bestEtaFrames,
                bestRange,
                bestPreviousTargetMinute,
                nearestVehicle,
                nearestState,
                nearestEtaFrames,
                nearestReason);
        }
    }
}
