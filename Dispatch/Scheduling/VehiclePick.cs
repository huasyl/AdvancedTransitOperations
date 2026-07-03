using Unity.Entities;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal sealed class VehiclePick
    {
        internal readonly struct Result
        {
            public readonly Entity Vehicle;
            public readonly int Tier;
            public readonly float Eta;
            public readonly float Range;
            public readonly int PrevTarget;
            public readonly Entity NearVehicle;
            public readonly VehicleState NearState;
            public readonly float NearEta;
            public readonly string NearReason;

            public Result(
                Entity vehicle,
                int tier,
                float eta,
                float range,
                int prevTarget,
                Entity nearVehicle,
                VehicleState nearState,
                float nearEta,
                string nearReason)
            {
                Vehicle = vehicle;
                Tier = tier;
                Eta = eta;
                Range = range;
                PrevTarget = prevTarget;
                NearVehicle = nearVehicle;
                NearState = nearState;
                NearEta = nearEta;
                NearReason = nearReason;
            }
        }

        private readonly DispatchRuntimeSystem m_Runtime;

        public VehiclePick(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public Result Pick(LineTick tick, int slot, int minsToSlot, bool spawnOnlyScan, float slotFramesAway)
        {
            Entity bestVehicle = Entity.Null;
            int bestTier = 99;
            float bestEta = float.MaxValue;
            float bestRange = -1f;
            int bestPrevTarget = -1;
            Entity nearestVehicle = Entity.Null;
            VehicleState nearestState = VehicleState.Preparing;
            float nearestEta = float.MaxValue;
            string nearestReason = "none";

            for (int i = 0; i < tick.Vehicles.Count; i++)
            {
                Entity vehicle = tick.Vehicles[i];
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                    continue;
                if (state == VehicleState.Retiring || m_Runtime.m_BVMisfire.Contains(vehicle))
                    continue;

                int assignedTarget = -1;
                if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin) && targetMin >= 0)
                {
                    assignedTarget = targetMin;
                    if (targetMin != slot)
                    {
                        if (ScheduleClock.CurrentOrRecent(tick.Now, targetMin))
                            continue;
                        int minsToAssigned = ScheduleClock.MinutesUntil(tick.Now, targetMin);
                        if (minsToAssigned <= minsToSlot)
                            continue;
                    }
                }

                if (state == VehicleState.Running
                    && assignedTarget >= 0
                    && assignedTarget != slot
                    && ScheduleClock.CurrentOrRecent(tick.Now, assignedTarget)
                    && m_Runtime.m_LineProfile.IsBorderlineOriginArrivalCandidate(vehicle, tick.Ways))
                {
                    continue;
                }

                if (m_Runtime.m_LineRange.Needs(vehicle) || !m_Runtime.m_LineRange.CanFinish(vehicle))
                    continue;

                int tier = 99;
                float eta = float.MaxValue;

                if (state == VehicleState.Idle || state == VehicleState.Holding)
                {
                    if (spawnOnlyScan)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEta = 0f;
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
                            nearestEta = 0f;
                            nearestReason = "cachedWp=" + cachedWaypointIndex;
                        }
                        continue;
                    }

                    tier = 0;
                    eta = 0f;
                }
                else if (state == VehicleState.Running)
                {
                    float etaFrames = m_Runtime.m_LineTimes.Run(vehicle, tick.Line, tick.Ways, tick.Frame, tick.Lap, tick.Run);
                    if (etaFrames == float.MaxValue)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEta = float.MaxValue;
                            nearestReason = "no-running-eta";
                        }
                        continue;
                    }

                    tier = 1;
                    eta = etaFrames;
                }
                else if (state == VehicleState.Preparing)
                {
                    float etaFrames = m_Runtime.m_LineTimes.Prep(vehicle, tick.Line, tick.Ways, tick.Lap);
                    if (etaFrames == float.MaxValue)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEta = float.MaxValue;
                            nearestReason = "no-preparing-eta";
                        }
                        continue;
                    }

                    tier = 1;
                    eta = etaFrames;
                }
                else
                {
                    continue;
                }

                if (eta > slotFramesAway)
                {
                    if (eta < nearestEta || nearestVehicle == Entity.Null)
                    {
                        nearestVehicle = vehicle;
                        nearestState = state;
                        nearestEta = eta;
                        nearestReason = "late-for-slot";
                    }
                    continue;
                }

                if (spawnOnlyScan)
                {
                    float earliestHoldArrivalFrames = slotFramesAway
                        - tick.Hold * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
                    if (earliestHoldArrivalFrames > 0f && eta < earliestHoldArrivalFrames)
                    {
                        if (nearestVehicle == Entity.Null)
                        {
                            nearestVehicle = vehicle;
                            nearestState = state;
                            nearestEta = eta;
                            nearestReason = "before-origin-hold-window";
                        }
                        continue;
                    }
                }

                float range = m_Runtime.m_LineRange.Left(vehicle);
                bool better = tier < bestTier
                    || (tier == bestTier && eta < bestEta)
                    || (tier == bestTier && eta == bestEta && range > bestRange);
                if (better)
                {
                    bestVehicle = vehicle;
                    bestTier = tier;
                    bestEta = eta;
                    bestRange = range;
                    bestPrevTarget = assignedTarget;
                }
            }

            return new Result(
                bestVehicle,
                bestTier,
                bestEta,
                bestRange,
                bestPrevTarget,
                nearestVehicle,
                nearestState,
                nearestEta,
                nearestReason);
        }
    }
}
