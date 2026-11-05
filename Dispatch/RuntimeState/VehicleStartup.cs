using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleStartup
    {
        private struct StartupCandidate
        {
            public Entity Line;
            public Entity Vehicle;
            public LifecycleKind Lifecycle;
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly EntityManager m_EntityManager;

        public VehicleStartup(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
            m_EntityManager = runtime.EntityManager;
        }

        internal void Activate(uint frame)
        {
            long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            var lines = new List<Entity>();
            var candidates = new List<StartupCandidate>();
            CollectStartupIndex(candidates, lines);
            m_Runtime.m_VanillaServiceControl.BaselineStartup(lines, m_Runtime.m_SimClock.Snapshot);

            int adopted = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                StartupCandidate candidate = candidates[i];
                if (!m_Runtime.m_LineServiceState.IsOperational(candidate.Line))
                    continue;

                if (candidate.Lifecycle == LifecycleKind.Rail)
                    ActivateRailVehicle(candidate.Line, candidate.Vehicle, frame);
                else
                    ActivateRoadVehicle(candidate.Line, candidate.Vehicle, frame);
                adopted++;
            }

            for (int i = 0; i < lines.Count; i++)
                m_Runtime.m_LineInitialAdopted.Add(lines[i]);
            double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            LogStartupAdoption(frame, candidates, lines, adopted);
            m_Runtime.log.Info("[Startup] First activation: lines=" + lines.Count
                + " vehicles=" + candidates.Count
                + " adopted=" + adopted
                + " ms=" + elapsedMs.ToString("F1"));
        }

        private void ActivateRailVehicle(Entity line, Entity vehicle, uint frame)
        {
            VehicleRegistrar registrar = m_Runtime.m_VehicleRegistrar;
            registrar.RemovePendingConfirmation(vehicle);
            PublicTransport publicTransport = m_EntityManager.GetComponentData<PublicTransport>(vehicle);
            DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
            VehicleState initialState = registrar.InferInitialState(
                vehicle,
                waypoints,
                publicTransport,
                (publicTransport.m_State & PublicTransportFlags.Boarding) != 0,
                -1,
                adoptExistingVehicles: true,
                out _);
            AdoptVehicle(line, vehicle, initialState, frame);

            m_Runtime.m_RailEventSource.RebaselineStartup(vehicle, frame);
            if (!m_Runtime.m_RailEventSource.PrepareStartupStopSource(vehicle, line, frame, out bool boarding, out int waypoint))
                return;

            m_Runtime.m_VehicleCache.SeedStartupRunningLapStart(vehicle, line);
            StopFact restoredStop = m_Runtime.m_StopRuntime.RestoreRegistration(vehicle, line, boarding, waypoint, frame);
            if (restoredStop.Exists)
            {
                registrar.SetRegistrationReady(vehicle, restoredStop.WaypointIndex, m_Runtime.m_VehicleView.GetState(vehicle), frame);
                m_Runtime.PublishStopFact(restoredStop);
            }
            else if (boarding && waypoint < 0)
            {
                registrar.AddPendingConfirmation(vehicle, line);
            }
            registrar.CommitFinalState(vehicle, line, frame);
        }

        private void ActivateRoadVehicle(Entity line, Entity vehicle, uint frame)
        {
            bool waypointKnown = m_Runtime.m_RoadEventSource.TryReadCurrentWaypoint(vehicle, line, out bool boarding, out int waypoint);
            PublicTransport publicTransport = m_EntityManager.GetComponentData<PublicTransport>(vehicle);
            VehicleState initialState = VehicleRegistrar.ResolveRoadInitialState(
                waypointKnown,
                waypoint,
                boarding,
                adoptExistingVehicles: true,
                publicTransport.m_State);
            m_Runtime.m_RoadEventSource.RegisterSource(vehicle, line, waypointKnown ? waypoint : -1);
            AdoptVehicle(line, vehicle, initialState, frame);

            m_Runtime.m_RoadEventSource.RebaselineStartup(vehicle, frame);
            if (!m_Runtime.m_RoadEventSource.PrepareStartupStopSource(vehicle, line, frame, out boarding, out waypoint))
                return;

            m_Runtime.m_VehicleCache.SeedStartupRunningLapStart(vehicle, line);
            m_Runtime.PublishStopFact(m_Runtime.m_StopRuntime.RestoreRegistration(vehicle, line, boarding, waypoint, frame));
            m_Runtime.m_VehicleRegistrar.CommitFinalState(vehicle, line, frame);
        }

        private void AdoptVehicle(Entity line, Entity vehicle, VehicleState initialState, uint frame)
        {
            VehicleRegistry registry = m_Runtime.m_VehicleRegistry;
            registry.BeginRestore();
            try
            {
                m_Runtime.m_RuntimeEngine.Adopt(vehicle, line, initialState, frame, null);
                m_Runtime.m_VehicleCache.Restore(vehicle, line, initialState != VehicleState.Holding, out _);
                registry.EndRestore(vehicle, line);
            }
            catch
            {
                registry.CancelRestore();
                throw;
            }
        }

        private void CollectStartupIndex(List<StartupCandidate> candidates, List<Entity> lines)
        {
            var lifecycles = new Dictionary<Entity, LifecycleKind>();
            using NativeArray<Entity> queriedLines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < queriedLines.Length; i++)
            {
                Entity line = queriedLines[i];
                if (m_EntityManager.GetBuffer<RouteWaypoint>(line, true).Length < 2
                    || !m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch()))
                    continue;

                lines.Add(line);
                if (RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle))
                    lifecycles.Add(line, lifecycle);
            }

            using NativeArray<Entity> vehicles = m_Runtime.m_VehicleRegistrar.VehicleQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = vehicles[i];
                Entity line = m_EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
                if (!lifecycles.TryGetValue(line, out LifecycleKind lifecycle))
                    continue;

                DynamicBuffer<RouteVehicle> members =
                    m_EntityManager.GetBuffer<RouteVehicle>(line, true);
                if (!IsStartupVehicle(vehicle, members))
                    continue;

                candidates.Add(new StartupCandidate
                {
                    Line = line,
                    Vehicle = vehicle,
                    Lifecycle = lifecycle
                });
            }

            lines.Sort(CompareEntity);
            candidates.Sort(CompareStartupCandidate);
        }

        private bool IsStartupVehicle(Entity vehicle, DynamicBuffer<RouteVehicle> members)
        {
            if (m_EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                return false;

            PublicTransport publicTransport = m_EntityManager.GetComponentData<PublicTransport>(vehicle);
            if ((publicTransport.m_State & (PublicTransportFlags.Returning | PublicTransportFlags.AbandonRoute)) != 0)
                return false;

            for (int i = 0; i < members.Length; i++)
            {
                if (m_Runtime.m_Resolve.RuntimeVehicle(members[i].m_Vehicle) == vehicle)
                    return true;
            }
            return false;
        }

        private static int CompareStartupCandidate(StartupCandidate left, StartupCandidate right)
        {
            int line = CompareEntity(left.Line, right.Line);
            return line != 0 ? line : CompareEntity(left.Vehicle, right.Vehicle);
        }

        private static int CompareEntity(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Version.CompareTo(right.Version);
        }

        private void LogStartupAdoption(
            uint frame,
            List<StartupCandidate> candidates,
            List<Entity> lines,
            int adopted)
        {
            if (!RtLog.VerboseEnabled)
                return;

            // 只在首次激活时读取一次，保留周期写回车辆缓存前的证据。
            var candidateLines = new Dictionary<Entity, Entity>();
            for (int i = 0; i < candidates.Count; i++)
                candidateLines[candidates[i].Vehicle] = candidates[i].Line;

            Entity city = m_Runtime.m_CitySystem.City;
            bool hasCache = city != Entity.Null
                && m_EntityManager.HasBuffer<VehicleStateCacheElement>(city);
            m_Runtime.log.Info("[StartupAdoption] phase=complete frame=" + frame
                + " lines=" + lines.Count
                + " candidates=" + candidates.Count
                + " operationalCandidates=" + adopted
                + " registered=" + m_Runtime.m_VehicleView.Count
                + " cacheReady=" + m_Runtime.m_VehicleCacheBufferReady
                + " hasCache=" + hasCache);
            if (hasCache)
            {
                DynamicBuffer<VehicleStateCacheElement> cache =
                    m_EntityManager.GetBuffer<VehicleStateCacheElement>(city, true);
                for (int i = 0; i < cache.Length; i++)
                {
                    VehicleStateCacheElement item = cache[i];
                    m_Runtime.log.Info("[StartupVehicleCache] frame=" + frame
                        + " vehicle=" + item.m_VehicleEntity.Index + ":" + item.m_VehicleEntity.Version
                        + " state=" + item.m_State + " targetMin=" + item.m_TargetMin);
                }
            }

            NativeArray<Entity> queriedLines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < queriedLines.Length; i++)
                {
                    Entity line = queriedLines[i];
                    bool hasMembers = m_EntityManager.HasBuffer<RouteVehicle>(line);
                    bool collected = lines.Contains(line);
                    bool isOperational = m_Runtime.m_LineServiceState.IsOperational(line);
                    m_Runtime.log.Info("[StartupAdoptionLine] frame=" + frame
                        + " line=" + line.Index + ":" + line.Version
                        + " name=" + m_Runtime.EntityName(line)
                        + " collected=" + collected + " operational=" + isOperational
                        + " hasMembers=" + hasMembers);
                    if (!hasMembers)
                        continue;

                    DynamicBuffer<RouteVehicle> members =
                        m_EntityManager.GetBuffer<RouteVehicle>(line, true);
                    for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                    {
                        Entity member = members[memberIndex].m_Vehicle;
                        Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(member);
                        bool exists = vehicle != Entity.Null && m_EntityManager.Exists(vehicle);
                        bool hasTransport = exists && m_EntityManager.HasComponent<PublicTransport>(vehicle);
                        bool hasRoute = exists && m_EntityManager.HasComponent<CurrentRoute>(vehicle);
                        bool hasTarget = exists && m_EntityManager.HasComponent<Target>(vehicle);
                        bool retireLock = exists && m_EntityManager.HasComponent<RtRetireDispatchLock>(vehicle);
                        Entity route = hasRoute
                            ? m_EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route : Entity.Null;
                        Entity target = hasTarget
                            ? m_EntityManager.GetComponentData<Target>(vehicle).m_Target : Entity.Null;
                        bool candidate = candidateLines.TryGetValue(vehicle, out Entity candidateLine);
                        bool registered = m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state);
                        m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity registeredLine);
                        string selection = !collected ? "line-not-collected"
                            : vehicle == Entity.Null ? "unresolved-member"
                            : !candidate ? "not-collected"
                            : candidateLine != line ? "collected-on-other-line"
                            : !isOperational ? "line-not-operational"
                            : "operational-candidate";
                        m_Runtime.log.Info("[StartupAdoptionVehicle] frame=" + frame
                            + " line=" + line.Index + ":" + line.Version
                            + " member=" + member.Index + ":" + member.Version
                            + " vehicle=" + vehicle.Index + ":" + vehicle.Version
                            + " selection=" + selection
                            + " candidateLine=" + candidateLine.Index + ":" + candidateLine.Version
                            + " exists=" + exists + " retireLock=" + retireLock
                            + " hasRoute=" + hasRoute + " route=" + route.Index + ":" + route.Version
                            + " pt=" + (hasTransport
                                ? m_EntityManager.GetComponentData<PublicTransport>(vehicle).m_State.ToString()
                                : "missing")
                            + " hasTarget=" + hasTarget + " target=" + target.Index + ":" + target.Version
                            + " registered=" + registered
                            + " state=" + (registered ? state.ToString() : "missing")
                            + " registeredLine=" + registeredLine.Index + ":" + registeredLine.Version);
                    }
                }
            }
            finally
            {
                queriedLines.Dispose();
            }
        }
    }
}
