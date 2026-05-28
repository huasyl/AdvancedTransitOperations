using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Creatures;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private struct BoardingCloseAssistStats
        {
            public int enteringPromoted;
            public int stalledLanePromoted;
            public int animalLanePromoted;
            public int noLanePromoted;
            public int readyAlready;
            public int exitingSkipped;
            public int stalledLaneAnimating;
            public int stalledLaneNotEndReached;
            public int stalledLaneWrongLane;
            public int blockedByLanePresence;
            public int canceledHumanTail;
            public int canceledAnimalTail;
        }

        private struct BoardingDepartureAuditSnapshot
        {
            public uint frame;
            public Entity stop;
            public int bound;
            public int ready;
            public int entering;
            public int exiting;
            public int laneVehicle;
            public int laneVehicleEnd;
            public int laneVehicleNeedEnd;
            public int laneVehicleEnterAnim;
            public int laneWrong;
            public int laneWrongEnd;
            public int laneWrongNeedEnd;
            public int laneWrongEnterAnim;
            public int animalLane;
            public int noLaneFallback;
            public int noLaneNoFallback;
            public int waiting;
        }

        private void PrepareVehicleForOriginalBoardingClose(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            EntityCommandBuffer ecb,
            out int scannedPassengers,
            out int readiedPassengers,
            out BoardingCloseAssistStats stats)
        {
            scannedPassengers = 0;
            readiedPassengers = 0;
            stats = default;

            uint nowFrame = m_SimulationSystem.frameIndex;
            publicTransport.m_DepartureFrame = nowFrame > OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                ? nowFrame - OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES
                : 1;
            publicTransport.m_MinWaitingDistance = float.MaxValue;
            publicTransport.m_MaxBoardingDistance = float.MaxValue;
        }

        private void PromoteClosablePassengersForVehicle(
            Entity transportVehicle,
            EntityCommandBuffer ecb,
            ref int scannedPassengers,
            ref int readiedPassengers,
            ref BoardingCloseAssistStats stats)
        {
            if (transportVehicle == Entity.Null
                || !EntityManager.Exists(transportVehicle)
                || !EntityManager.HasBuffer<Passenger>(transportVehicle))
            {
                return;
            }

            DynamicBuffer<Passenger> passengers = EntityManager.GetBuffer<Passenger>(transportVehicle, true);
            scannedPassengers += passengers.Length;
            for (int i = 0; i < passengers.Length; i++)
            {
                Entity passenger = passengers[i].m_Passenger;
                if (passenger == Entity.Null
                    || !EntityManager.Exists(passenger)
                    || !EntityManager.HasComponent<CurrentVehicle>(passenger))
                {
                    continue;
                }

                CurrentVehicle currentVehicle = EntityManager.GetComponentData<CurrentVehicle>(passenger);
                if (currentVehicle.m_Vehicle != transportVehicle)
                    continue;

                if (ShouldCancelLingeringBoardingTailPassenger(passenger, currentVehicle, ref stats))
                {
                    RememberDeferredBoardingTailPassenger(passenger, transportVehicle);
                }
            }
            for (int i = 0; i < passengers.Length; i++)
            {
                Entity passenger = passengers[i].m_Passenger;
                if (passenger == Entity.Null
                    || !EntityManager.Exists(passenger)
                    || !EntityManager.HasComponent<CurrentVehicle>(passenger))
                {
                    continue;
                }

                CurrentVehicle currentVehicle = EntityManager.GetComponentData<CurrentVehicle>(passenger);
                if (currentVehicle.m_Vehicle != transportVehicle)
                    continue;

                if ((currentVehicle.m_Flags & CreatureVehicleFlags.Ready) != 0)
                {
                    stats.readyAlready++;
                    ForgetDeferredBoardingTailPassenger(passenger);
                    continue;
                }

                if (ShouldIgnoreDeferredBoardingTailPassenger(passenger, transportVehicle, currentVehicle))
                    continue;

                bool wasEntering = (currentVehicle.m_Flags & CreatureVehicleFlags.Entering) != 0;
                bool hadNoLanePresence = !EntityManager.HasComponent<HumanCurrentLane>(passenger)
                    && !EntityManager.HasComponent<AnimalCurrentLane>(passenger);
                bool hadAnimalLane = EntityManager.HasComponent<AnimalCurrentLane>(passenger);
                if (!CanSafelyPromotePassengerReady(passenger, currentVehicle, ref stats))
                    continue;

                if (!hadNoLanePresence)
                {
                    RememberDeferredBoardingTailPassenger(passenger, transportVehicle);
                    continue;
                }

                currentVehicle.m_Flags |= CreatureVehicleFlags.Ready;
                currentVehicle.m_Flags &= ~CreatureVehicleFlags.Entering;
                EntityManager.SetComponentData(passenger, currentVehicle);
                if (!EntityManager.HasComponent<BatchesUpdated>(passenger))
                    ecb.AddComponent<BatchesUpdated>(passenger);
                ForgetDeferredBoardingTailPassenger(passenger);
                if (wasEntering)
                    stats.enteringPromoted++;
                else if (hadAnimalLane)
                    stats.animalLanePromoted++;
                else if (hadNoLanePresence)
                    stats.noLanePromoted++;
                else
                    stats.stalledLanePromoted++;
                readiedPassengers++;
            }
        }

        private bool CanSafelyPromotePassengerReady(Entity passenger, CurrentVehicle currentVehicle, ref BoardingCloseAssistStats stats)
        {
            if ((currentVehicle.m_Flags & CreatureVehicleFlags.Entering) != 0)
                return true;

            if ((currentVehicle.m_Flags & CreatureVehicleFlags.Exiting) != 0)
            {
                stats.exitingSkipped++;
                return false;
            }

            if (EntityManager.HasComponent<HumanCurrentLane>(passenger)
                && IsSafeStalledHumanBoardingPassenger(passenger, currentVehicle, ref stats))
            {
                return true;
            }

            if (EntityManager.HasComponent<AnimalCurrentLane>(passenger)
                && IsSafeStalledAnimalBoardingPassenger(passenger, currentVehicle, ref stats))
            {
                return true;
            }

            bool stillHasLanePresence = EntityManager.HasComponent<HumanCurrentLane>(passenger)
                || EntityManager.HasComponent<AnimalCurrentLane>(passenger);
            if (stillHasLanePresence)
            {
                stats.blockedByLanePresence++;
                return false;
            }

            return true;
        }

        private bool ShouldCancelLingeringBoardingTailPassenger(Entity passenger, CurrentVehicle currentVehicle, ref BoardingCloseAssistStats stats)
        {
            if (!IsLingeringBoardingTailPassenger(passenger, currentVehicle))
                return false;

            if (EntityManager.HasComponent<HumanCurrentLane>(passenger))
                stats.canceledHumanTail++;
            else if (EntityManager.HasComponent<AnimalCurrentLane>(passenger))
                stats.canceledAnimalTail++;
            return true;
        }

        private bool IsLingeringBoardingTailPassenger(Entity passenger, CurrentVehicle currentVehicle)
        {
            if ((currentVehicle.m_Flags & (CreatureVehicleFlags.Ready | CreatureVehicleFlags.Entering | CreatureVehicleFlags.Exiting)) != 0)
                return false;

            if (EntityManager.HasComponent<HumanCurrentLane>(passenger))
            {
                HumanCurrentLane currentLane = EntityManager.GetComponentData<HumanCurrentLane>(passenger);
                if (currentLane.m_Lane == currentVehicle.m_Vehicle)
                    return false;

                if (EntityManager.HasComponent<HumanNavigation>(passenger))
                {
                    HumanNavigation navigation = EntityManager.GetComponentData<HumanNavigation>(passenger);
                    bool animatingEnter = navigation.m_TargetActivity == (byte)ActivityType.Enter
                        && navigation.m_TransformState == Game.Objects.TransformState.Action;
                    if (animatingEnter)
                        return false;
                }

                return true;
            }

            if (EntityManager.HasComponent<AnimalCurrentLane>(passenger))
            {
                AnimalCurrentLane currentLane = EntityManager.GetComponentData<AnimalCurrentLane>(passenger);
                return currentLane.m_Lane != currentVehicle.m_Vehicle;
            }

            return false;
        }

        private void RememberDeferredBoardingTailPassenger(Entity passenger, Entity vehicle)
        {
            if (passenger == Entity.Null || vehicle == Entity.Null)
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            m_DeferredBoardingTailIgnores[passenger] = new DeferredBoardingTailIgnoreEntry
            {
                Vehicle = vehicle,
                ExpireFrame = nowFrame + BOARDING_TAIL_IGNORE_TTL_FRAMES
            };
            TrackDeferredBoardingTailPassengerType(passenger);
        }

        private void ForgetDeferredBoardingTailPassenger(Entity passenger)
        {
            if (passenger == Entity.Null)
                return;

            m_DeferredBoardingTailIgnores.Remove(passenger);
            m_DeferredBoardingHumanTailIgnores.Remove(passenger);
            m_DeferredBoardingPetTailIgnores.Remove(passenger);
        }

        private void TrackDeferredBoardingTailPassengerType(Entity passenger)
        {
            if (passenger == Entity.Null)
                return;

            m_DeferredBoardingHumanTailIgnores.Remove(passenger);
            m_DeferredBoardingPetTailIgnores.Remove(passenger);

            if (!EntityManager.Exists(passenger))
                return;

            if (EntityManager.HasComponent<HumanCurrentLane>(passenger))
            {
                m_DeferredBoardingHumanTailIgnores.Add(passenger);
            }
            else if (EntityManager.HasComponent<AnimalCurrentLane>(passenger))
            {
                m_DeferredBoardingPetTailIgnores.Add(passenger);
            }
        }

        private bool ShouldIgnoreDeferredBoardingTailPassenger(Entity passenger, Entity vehicle, CurrentVehicle currentVehicle)
        {
            if (!m_DeferredBoardingTailIgnores.TryGetValue(passenger, out DeferredBoardingTailIgnoreEntry entry))
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (entry.Vehicle != vehicle
                || nowFrame >= entry.ExpireFrame
                || currentVehicle.m_Vehicle != vehicle
                || (currentVehicle.m_Flags & CreatureVehicleFlags.Ready) != 0
                || !IsLingeringBoardingTailPassenger(passenger, currentVehicle))
            {
                m_DeferredBoardingTailIgnores.Remove(passenger);
                return false;
            }

            return true;
        }

        internal bool ShouldBlockNewBoardingForClosingVehicle(Entity vehicleEntity)
        {
            if (vehicleEntity == Entity.Null
                || !EntityManager.Exists(vehicleEntity)
                || !m_ForcedMidStopBoardingHardCloseAfter.TryGetValue(vehicleEntity, out uint hardCloseAfter))
            {
                return false;
            }

            return true;
        }

        internal void ProcessForcedMidStopHardCloseTailCancels(bool processResidents, bool processPets)
        {
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_DeferredBoardingTailIgnores.Count == 0 || m_ForcedMidStopBoardingHardCloseAfter.Count == 0)
                return;

            HashSet<Entity> pendingPassengers = processResidents
                ? m_DeferredBoardingHumanTailIgnores
                : m_DeferredBoardingPetTailIgnores;
            if (pendingPassengers.Count == 0)
                return;

            m_DeferredBoardingTailScratch.Clear();
            int canceledHumans = 0;
            int canceledAnimals = 0;

            foreach (Entity passenger in pendingPassengers)
            {
                if (!m_DeferredBoardingTailIgnores.TryGetValue(passenger, out DeferredBoardingTailIgnoreEntry entry))
                {
                    m_DeferredBoardingTailScratch.Add(passenger);
                    continue;
                }

                if (passenger == Entity.Null
                    || !EntityManager.Exists(passenger)
                    || nowFrame >= entry.ExpireFrame
                    || !m_ForcedMidStopBoardingHardCloseAfter.TryGetValue(entry.Vehicle, out uint hardCloseAfter)
                    || nowFrame < hardCloseAfter
                    || !EntityManager.HasComponent<CurrentVehicle>(passenger))
                {
                    m_DeferredBoardingTailScratch.Add(passenger);
                    continue;
                }

                CurrentVehicle currentVehicle = EntityManager.GetComponentData<CurrentVehicle>(passenger);
                if (currentVehicle.m_Vehicle != entry.Vehicle
                    || (currentVehicle.m_Flags & CreatureVehicleFlags.Ready) != 0
                    || !IsLingeringBoardingTailPassenger(passenger, currentVehicle))
                {
                    m_DeferredBoardingTailScratch.Add(passenger);
                    continue;
                }

                if (processResidents && EntityManager.HasComponent<HumanCurrentLane>(passenger))
                {
                    CancelResidentBoardingTail(passenger, currentVehicle);
                    m_DeferredBoardingTailScratch.Add(passenger);
                    canceledHumans++;
                    continue;
                }

                if (processPets && EntityManager.HasComponent<AnimalCurrentLane>(passenger))
                {
                    CancelPetBoardingTail(passenger, currentVehicle);
                    m_DeferredBoardingTailScratch.Add(passenger);
                    canceledAnimals++;
                    continue;
                }

                m_DeferredBoardingTailScratch.Add(passenger);
            }

            for (int i = 0; i < m_DeferredBoardingTailScratch.Count; i++)
            {
                ForgetDeferredBoardingTailPassenger(m_DeferredBoardingTailScratch[i]);
            }
            m_DeferredBoardingTailScratch.Clear();

            if (canceledHumans > 0 || canceledAnimals > 0)
            {
                log.Info("[停站硬收口协助] cancelHuman=" + canceledHumans
                    + " cancelAnimal=" + canceledAnimals);
            }
        }

        private void CancelResidentBoardingTail(Entity passenger, CurrentVehicle currentVehicle)
        {
            if (EntityManager.HasBuffer<Passenger>(currentVehicle.m_Vehicle))
            {
                DynamicBuffer<Passenger> passengers = EntityManager.GetBuffer<Passenger>(currentVehicle.m_Vehicle);
                for (int i = 0; i < passengers.Length; i++)
                {
                    if (passengers[i].m_Passenger == passenger)
                    {
                        passengers.RemoveAt(i);
                        break;
                    }
                }
            }

            if (EntityManager.HasComponent<CurrentVehicle>(passenger))
                EntityManager.RemoveComponent<CurrentVehicle>(passenger);

            if (EntityManager.HasComponent<Game.Creatures.Resident>(passenger))
            {
                Game.Creatures.Resident resident = EntityManager.GetComponentData<Game.Creatures.Resident>(passenger);
                resident.m_Flags &= ~ResidentFlags.InVehicle;
                resident.m_Timer = 0;
                EntityManager.SetComponentData(passenger, resident);
            }

            if (EntityManager.HasComponent<Human>(passenger))
            {
                Human human = EntityManager.GetComponentData<Human>(passenger);
                human.m_Flags &= ~(HumanFlags.Run | HumanFlags.Emergency);
                EntityManager.SetComponentData(passenger, human);
            }

            if (EntityManager.HasComponent<PathOwner>(passenger) && EntityManager.HasBuffer<PathElement>(passenger))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(passenger);
                DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(passenger);
                bool trimmed = false;
                for (int i = pathOwner.m_ElementIndex; i < pathElements.Length; i++)
                {
                    if (pathElements[i].m_Target == currentVehicle.m_Vehicle)
                    {
                        pathElements.RemoveRange(0, i + 1);
                        pathOwner.m_ElementIndex = 0;
                        trimmed = true;
                        break;
                    }
                }

                if (!trimmed)
                {
                    pathElements.Clear();
                    pathOwner.m_ElementIndex = 0;
                    pathOwner.m_State &= ~PathFlags.Failed;
                    pathOwner.m_State |= PathFlags.Obsolete;
                }

                EntityManager.SetComponentData(passenger, pathOwner);
            }
        }

        private void CancelPetBoardingTail(Entity passenger, CurrentVehicle currentVehicle)
        {
            if (EntityManager.HasBuffer<Passenger>(currentVehicle.m_Vehicle))
            {
                DynamicBuffer<Passenger> passengers = EntityManager.GetBuffer<Passenger>(currentVehicle.m_Vehicle);
                for (int i = 0; i < passengers.Length; i++)
                {
                    if (passengers[i].m_Passenger == passenger)
                    {
                        passengers.RemoveAt(i);
                        break;
                    }
                }
            }

            if (EntityManager.HasComponent<CurrentVehicle>(passenger))
                EntityManager.RemoveComponent<CurrentVehicle>(passenger);
        }

        private void MarkForcedMidStopClosingConsist(Entity vehicle, uint graceUntil, uint hardCloseAfter)
        {
            MarkForcedMidStopClosingVehicle(vehicle, graceUntil, hardCloseAfter);
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                return;
            }

            DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            for (int i = 0; i < layout.Length; i++)
                MarkForcedMidStopClosingVehicle(layout[i].m_Vehicle, graceUntil, hardCloseAfter);
        }

        private void MarkForcedMidStopClosingVehicle(Entity vehicle, uint graceUntil, uint hardCloseAfter)
        {
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            m_ForcedMidStopBoardingGraceUntil[vehicle] = graceUntil;
            if (!m_ForcedMidStopBoardingHardCloseAfter.ContainsKey(vehicle))
                m_ForcedMidStopBoardingHardCloseAfter[vehicle] = hardCloseAfter;
        }

        private void ClearForcedMidStopClosingConsist(Entity vehicle)
        {
            ClearForcedMidStopClosingVehicle(vehicle);
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                return;
            }

            DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            for (int i = 0; i < layout.Length; i++)
                ClearForcedMidStopClosingVehicle(layout[i].m_Vehicle);
        }

        private void ClearForcedMidStopClosingVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_ForcedMidStopBoardingHardCloseAfter.Remove(vehicle);
            m_MidStopTimeoutLogCache.Remove(vehicle);
        }

        private void CleanupDeferredBoardingTailIgnores(uint nowFrame)
        {
            if (m_DeferredBoardingTailIgnores.Count == 0)
                return;
            if (m_LastDeferredBoardingTailCleanupFrame != 0
                && (nowFrame - m_LastDeferredBoardingTailCleanupFrame) < BOARDING_TAIL_IGNORE_CLEANUP_INTERVAL_FRAMES)
            {
                return;
            }

            m_LastDeferredBoardingTailCleanupFrame = nowFrame;
            m_DeferredBoardingTailScratch.Clear();
            foreach (KeyValuePair<Entity, DeferredBoardingTailIgnoreEntry> entry in m_DeferredBoardingTailIgnores)
            {
                Entity passenger = entry.Key;
                if (passenger == Entity.Null
                    || !EntityManager.Exists(passenger)
                    || nowFrame >= entry.Value.ExpireFrame
                    || !EntityManager.HasComponent<CurrentVehicle>(passenger))
                {
                    m_DeferredBoardingTailScratch.Add(passenger);
                    continue;
                }

                CurrentVehicle currentVehicle = EntityManager.GetComponentData<CurrentVehicle>(passenger);
                if (currentVehicle.m_Vehicle != entry.Value.Vehicle
                    || (currentVehicle.m_Flags & CreatureVehicleFlags.Ready) != 0
                    || !IsLingeringBoardingTailPassenger(passenger, currentVehicle))
                {
                    m_DeferredBoardingTailScratch.Add(passenger);
                }
            }

            for (int i = 0; i < m_DeferredBoardingTailScratch.Count; i++)
                ForgetDeferredBoardingTailPassenger(m_DeferredBoardingTailScratch[i]);
            m_DeferredBoardingTailScratch.Clear();
        }

        private bool IsSafeStalledHumanBoardingPassenger(Entity passenger, CurrentVehicle currentVehicle, ref BoardingCloseAssistStats stats)
        {
            HumanCurrentLane currentLane = EntityManager.GetComponentData<HumanCurrentLane>(passenger);
            if (currentLane.m_Lane != currentVehicle.m_Vehicle)
            {
                stats.stalledLaneWrongLane++;
                return false;
            }

            if ((currentLane.m_Flags & CreatureLaneFlags.EndReached) == 0)
            {
                stats.stalledLaneNotEndReached++;
                return false;
            }

            if (!EntityManager.HasComponent<HumanNavigation>(passenger))
                return true;

            HumanNavigation navigation = EntityManager.GetComponentData<HumanNavigation>(passenger);
            bool animatingEnter = navigation.m_TargetActivity == (byte)ActivityType.Enter
                && navigation.m_TransformState == Game.Objects.TransformState.Action;
            if (animatingEnter)
            {
                stats.stalledLaneAnimating++;
                return false;
            }

            return true;
        }

        private bool IsSafeStalledAnimalBoardingPassenger(Entity passenger, CurrentVehicle currentVehicle, ref BoardingCloseAssistStats stats)
        {
            AnimalCurrentLane currentLane = EntityManager.GetComponentData<AnimalCurrentLane>(passenger);
            if (currentLane.m_Lane != currentVehicle.m_Vehicle)
                return false;

            if ((currentLane.m_Flags & CreatureLaneFlags.EndReached) == 0)
                return false;

            return true;
        }

        private BoardingDepartureAuditSnapshot CaptureBoardingDepartureAuditSnapshot(Entity vehicle, Entity stop, uint nowFrame)
        {
            BoardingDepartureAuditSnapshot snapshot = default;
            snapshot.frame = nowFrame;
            snapshot.stop = stop;
            snapshot.waiting = GetWaitingPassengerCount(stop);
            AccumulateBoardingDepartureAuditSnapshot(vehicle, vehicle, ref snapshot);
            if (EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                for (int i = 0; i < layout.Length; i++)
                    AccumulateBoardingDepartureAuditSnapshot(layout[i].m_Vehicle, layout[i].m_Vehicle, ref snapshot);
            }
            return snapshot;
        }

        private void AccumulateBoardingDepartureAuditSnapshot(Entity transportVehicle, Entity owningVehicle, ref BoardingDepartureAuditSnapshot snapshot)
        {
            if (transportVehicle == Entity.Null
                || !EntityManager.Exists(transportVehicle)
                || !EntityManager.HasBuffer<Passenger>(transportVehicle))
            {
                return;
            }

            DynamicBuffer<Passenger> passengers = EntityManager.GetBuffer<Passenger>(transportVehicle, true);
            for (int i = 0; i < passengers.Length; i++)
            {
                Entity passenger = passengers[i].m_Passenger;
                if (passenger == Entity.Null
                    || !EntityManager.Exists(passenger)
                    || !EntityManager.HasComponent<CurrentVehicle>(passenger))
                {
                    continue;
                }

                CurrentVehicle currentVehicle = EntityManager.GetComponentData<CurrentVehicle>(passenger);
                if (currentVehicle.m_Vehicle != owningVehicle)
                    continue;

                snapshot.bound++;
                if ((currentVehicle.m_Flags & CreatureVehicleFlags.Ready) != 0)
                    snapshot.ready++;
                if ((currentVehicle.m_Flags & CreatureVehicleFlags.Entering) != 0)
                    snapshot.entering++;
                if ((currentVehicle.m_Flags & CreatureVehicleFlags.Exiting) != 0)
                    snapshot.exiting++;

                bool hasLanePresence = false;
                if (EntityManager.HasComponent<HumanCurrentLane>(passenger))
                {
                    hasLanePresence = true;
                    HumanCurrentLane currentLane = EntityManager.GetComponentData<HumanCurrentLane>(passenger);
                    bool animatingEnter = false;
                    if (EntityManager.HasComponent<HumanNavigation>(passenger))
                    {
                        HumanNavigation navigation = EntityManager.GetComponentData<HumanNavigation>(passenger);
                        animatingEnter = navigation.m_TargetActivity == (byte)ActivityType.Enter
                            && navigation.m_TransformState == Game.Objects.TransformState.Action;
                    }
                    if (currentLane.m_Lane == owningVehicle)
                    {
                        snapshot.laneVehicle++;
                        if ((currentLane.m_Flags & CreatureLaneFlags.EndReached) != 0)
                            snapshot.laneVehicleEnd++;
                        else
                            snapshot.laneVehicleNeedEnd++;
                        if (animatingEnter)
                            snapshot.laneVehicleEnterAnim++;
                    }
                    else
                    {
                        snapshot.laneWrong++;
                        if ((currentLane.m_Flags & CreatureLaneFlags.EndReached) != 0)
                            snapshot.laneWrongEnd++;
                        else
                            snapshot.laneWrongNeedEnd++;
                        if (animatingEnter)
                            snapshot.laneWrongEnterAnim++;
                    }
                }
                else if (EntityManager.HasComponent<AnimalCurrentLane>(passenger))
                {
                    hasLanePresence = true;
                    snapshot.animalLane++;
                }

                if (!hasLanePresence)
                {
                    bool hasFallback = EntityManager.HasComponent<Game.Objects.Unspawned>(passenger)
                        || EntityManager.HasComponent<Game.Objects.Relative>(passenger);
                    if (hasFallback)
                        snapshot.noLaneFallback++;
                    else
                        snapshot.noLaneNoFallback++;
                }
            }
        }

        private int GetWaitingPassengerCount(Entity stop)
        {
            if (stop == Entity.Null
                || !EntityManager.Exists(stop)
                || !EntityManager.HasComponent<WaitingPassengers>(stop))
            {
                return -1;
            }

            return EntityManager.GetComponentData<WaitingPassengers>(stop).m_Count;
        }

        private void RememberBoardingAssistSnapshot(Entity vehicle, Entity stop, uint nowFrame)
        {
            if (vehicle == Entity.Null || stop == Entity.Null)
                return;

            m_LastBoardingAssistSnapshots[vehicle] = CaptureBoardingDepartureAuditSnapshot(vehicle, stop, nowFrame);
        }

        private void TryLogBoardingDepartureAudit(
            Entity vehicle,
            string lineTag,
            Entity stop,
            string stopName,
            uint nowFrame)
        {
            if (vehicle == Entity.Null || stop == Entity.Null)
                return;
            if (!m_LastBoardingAssistSnapshots.TryGetValue(vehicle, out BoardingDepartureAuditSnapshot assistSnapshot))
                return;
            if (assistSnapshot.stop != stop)
                return;

            BoardingDepartureAuditSnapshot departSnapshot = CaptureBoardingDepartureAuditSnapshot(vehicle, stop, nowFrame);
            log.Info("[离站对照] " + lineTag
                + " 车辆" + vehicle.Index
                + " stop=\"" + stopName + "\""
                + " assist{" + FormatBoardingDepartureAuditSnapshot(assistSnapshot) + "}"
                + " depart{" + FormatBoardingDepartureAuditSnapshot(departSnapshot) + "}"
                + " deltaBound=" + (departSnapshot.bound - assistSnapshot.bound)
                + " deltaReady=" + (departSnapshot.ready - assistSnapshot.ready)
                + " deltaWaiting=" + ((assistSnapshot.waiting >= 0 && departSnapshot.waiting >= 0)
                    ? (departSnapshot.waiting - assistSnapshot.waiting).ToString()
                    : "?"));
            m_LastBoardingAssistSnapshots.Remove(vehicle);
        }

        private static string FormatBoardingDepartureAuditSnapshot(BoardingDepartureAuditSnapshot snapshot)
        {
            return "frame=" + snapshot.frame
                + " bound=" + snapshot.bound
                + " ready=" + snapshot.ready
                + " entering=" + snapshot.entering
                + " exiting=" + snapshot.exiting
                + " laneV=" + snapshot.laneVehicle
                + " laneVEnd=" + snapshot.laneVehicleEnd
                + " laneVNeedEnd=" + snapshot.laneVehicleNeedEnd
                + " laneVAnim=" + snapshot.laneVehicleEnterAnim
                + " laneWrong=" + snapshot.laneWrong
                + " laneWrongEnd=" + snapshot.laneWrongEnd
                + " laneWrongNeedEnd=" + snapshot.laneWrongNeedEnd
                + " laneWrongAnim=" + snapshot.laneWrongEnterAnim
                + " animalLane=" + snapshot.animalLane
                + " fallback=" + snapshot.noLaneFallback
                + " noFallback=" + snapshot.noLaneNoFallback
                + " waiting=" + snapshot.waiting;
        }

        private static string FormatBoardingCloseAssistStats(BoardingCloseAssistStats stats)
        {
            return "assist[entering=" + stats.enteringPromoted
                + " stalled=" + stats.stalledLanePromoted
                + " animal=" + stats.animalLanePromoted
                + " noLane=" + stats.noLanePromoted
                + " ready=" + stats.readyAlready
                + " exiting=" + stats.exitingSkipped
                + " cancelHuman=" + stats.canceledHumanTail
                + " cancelAnimal=" + stats.canceledAnimalTail
                + " anim=" + stats.stalledLaneAnimating
                + " laneNotEnd=" + stats.stalledLaneNotEndReached
                + " laneWrong=" + stats.stalledLaneWrongLane
                + " laneBlock=" + stats.blockedByLanePresence
                + "]";
        }
    }
}
