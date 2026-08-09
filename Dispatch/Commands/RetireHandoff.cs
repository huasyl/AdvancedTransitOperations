using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Commands
{
    internal sealed class RetireHandoffWatchRecord
    {
        public uint RequestedFrame;
        public string ReasonCode = string.Empty;
        public bool RequestBoarding;
        public bool RequestEnRoute;
        public uint RequestDepartureDelta;
        public int RequestTargetWaypointIndex = -1;
        public bool OfficialTransitionLogged;
    }

    internal enum RetireHandoffStageKind : byte
    {
        PendingBoundary,
        OfficialReturning
    }

    internal sealed class RetireHandoffStageRecord
    {
        public RetireHandoffStageKind Stage;
        public uint NextProbeFrame;
        public uint NextDiagnosticFrame;
        public RetireBoardingState Boarding;
    }

    internal sealed class RetireHandoff
    {
        // New handoff order:
        // Begin(...) request -> pre/post train-AI lock keepers -> vanilla return/path setup ->
        // controller terminal finalize -> controller tail TickRetireHandoffStages(...).
        private readonly RetireHost m_RetireHost;
        private readonly CommandHost m_CommandHost;
        private readonly Dictionary<Entity, RetireHandoffWatchRecord> m_RetireHandoffWatch =
            new Dictionary<Entity, RetireHandoffWatchRecord>();
        private readonly Dictionary<Entity, RetireHandoffStageRecord> m_RetireHandoffStages =
            new Dictionary<Entity, RetireHandoffStageRecord>();
        private readonly Dictionary<Entity, List<string>> m_RetireShadowHistory =
            new Dictionary<Entity, List<string>>();
        private readonly Dictionary<Entity, string> m_RetireShadowLastSnapshot =
            new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_RetireShadowLastFrame =
            new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> m_RetireShadowLastRetiringFrame =
            new Dictionary<Entity, uint>();
        private readonly EntityQuery m_RetireDispatchLockQuery;
        private bool m_RetireDispatchLocksReconciledOnReady;

        private EntityManager EntityManager => m_RetireHost.EntityManager;
        private TimedLogger Log => m_RetireHost.Log;

        public RetireHandoff(RetireHost retireHost, CommandHost commandHost)
        {
            m_RetireHost = retireHost;
            m_CommandHost = commandHost;
            m_RetireDispatchLockQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<RtRetireDispatchLock>()
                },
                Any = new ComponentType[]
                {
                    ComponentType.ReadOnly<ParkedTrain>(),
                    ComponentType.ReadOnly<Deleted>()
                }
            });
        }

        public void Begin(RetireStartInput input, RetireStartContext start)
        {
            Entity vehicle = start.Vehicle;
            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;
            ResetShadow(vehicle);
            string lineTag = start.SourceLine != Entity.Null ? "线路" + start.SourceLine.Index : "线路?";
            Log.Info("[回库] " + lineTag + " 车辆" + vehicle.Index
                + (input.Reason.Length > 0 ? " 原因:" + input.Reason : "") + " -> 车库"
                + start.SpawnIntent);
            RecordShadow(vehicle, "retire-request");
            uint requestedFrame = m_RetireHost.Frame;
            PutWatch(vehicle, new RetireHandoffWatchRecord
            {
                RequestedFrame = requestedFrame,
                ReasonCode = string.IsNullOrWhiteSpace(input.Reason) ? "unspecified" : input.Reason,
                RequestBoarding = (input.PublicTransport.m_State & PublicTransportFlags.Boarding) != 0,
                RequestEnRoute = (input.PublicTransport.m_State & PublicTransportFlags.EnRoute) != 0,
                RequestDepartureDelta = input.PublicTransport.m_DepartureFrame > requestedFrame
                    ? input.PublicTransport.m_DepartureFrame - requestedFrame
                    : 0,
                RequestTargetWaypointIndex = m_RetireHost.GetRouteWaypointIndex(vehicle, input.Target.m_Target),
                OfficialTransitionLogged = false
            });

            EnterRetireDispatchLock(vehicle);
        }

        private void EnterRetireDispatchLock(Entity vehicle)
        {
            ProjectRetireDispatchLock(vehicle);
            EnsureRetireDispatchLockStage(vehicle);
            TickRetireDispatchLockStage(vehicle, m_RetireHost.Frame);
        }

        private void ProjectRetireDispatchLock(Entity vehicle)
        {
            if (!EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                EntityManager.AddComponent<RtRetireDispatchLock>(vehicle);

            if (EntityManager.HasComponent<PublicTransport>(vehicle))
            {
                PublicTransport publicTransport = m_CommandHost.ReadPublicTransport(vehicle);
                if (publicTransport.m_RequestCount != 1)
                {
                    publicTransport.m_RequestCount = 1;
                    m_CommandHost.SetPublicTransport(vehicle, publicTransport);
                }
            }

            m_RetireHost.ClearServiceDispatch(vehicle, out _);
        }

        private void EnsureRetireDispatchLockStage(Entity vehicle)
        {
            if (m_RetireHandoffStages.ContainsKey(vehicle))
                return;

            uint nowFrame = m_RetireHost.Frame;
            m_RetireHandoffStages[vehicle] = new RetireHandoffStageRecord
            {
                Stage = RetireHandoffStageKind.PendingBoundary,
                NextProbeFrame = nowFrame,
                NextDiagnosticFrame = nowFrame
            };
            m_RetireHost.SetRetireDeadline(vehicle, DeadlineKind.RetireBoundary, nowFrame);
        }

        public void ProjectRetireDispatchLocksImmediatelyOnLoad()
        {
            EntityQuery query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RtRetireDispatchLock>());
            NativeArray<Entity> vehicles = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                    {
                        continue;
                    }

                    if (EntityManager.HasComponent<Deleted>(vehicle)
                        || EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        NormalizeRetireDispatchLockTerminal(vehicle, releaseSameRunOwnership: false);
                        continue;
                    }

                    ProjectRetireDispatchLock(vehicle);
                }
            }
            finally
            {
                vehicles.Dispose();
                query.Dispose();
            }
        }

        public void ReconcileRetireDispatchLocksOnReady()
        {
            if (m_RetireDispatchLocksReconciledOnReady)
                return;

            EntityQuery query = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RtRetireDispatchLock>());
            NativeArray<Entity> vehicles = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                    {
                        continue;
                    }

                    if (EntityManager.HasComponent<Deleted>(vehicle)
                        || EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        NormalizeRetireDispatchLockTerminal(vehicle, releaseSameRunOwnership: false);
                        continue;
                    }

                    ProjectRetireDispatchLock(vehicle);
                    EnsureRetireDispatchLockStage(vehicle);
                    TickRetireDispatchLockStage(vehicle, m_RetireHost.Frame);
                }

                m_RetireDispatchLocksReconciledOnReady = true;
            }
            finally
            {
                vehicles.Dispose();
                query.Dispose();
            }
        }

        public bool TryGetForceRetireVehicle(out Entity selected)
        {
            selected = Entity.Null;
            NativeArray<Entity> lines = m_RetireHost.LineEntities(Allocator.Temp);
            BufferLookup<RouteVehicle> routeVehicleBuffers = m_RetireHost.RouteVehicles(true);
            try
            {
                foreach (Entity line in lines)
                {
                    if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                        continue;

                    HashSet<Entity> seenVehicles = new HashSet<Entity>();
                    for (int i = 0; i < routeVehicles.Length; i++)
                    {
                        Entity vehicle = m_RetireHost.ResolveVehicle(routeVehicles[i].m_Vehicle);
                        if (!EntityManager.Exists(vehicle) || !seenVehicles.Add(vehicle))
                            continue;
                        if (!m_RetireHost.TryVehicleState(vehicle, out VehicleState state) || state == VehicleState.Retiring)
                            continue;

                        selected = vehicle;
                        return true;
                    }

                    break;
                }
            }
            finally
            {
                lines.Dispose();
            }

            return false;
        }

        public void TickRetireHandoffStages(uint nowFrame, IReadOnlyList<FramePlanEntry> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return;

            for (int i = 0; i < candidates.Count; i++)
            {
                Entity vehicle = candidates[i].Vehicle;
                if (!m_RetireHandoffStages.TryGetValue(vehicle, out RetireHandoffStageRecord stage)
                    || nowFrame < stage.NextProbeFrame)
                {
                    continue;
                }

                m_RetireHost.CountRetireStageExecuted();
                TickRetireDispatchLockStage(vehicle, nowFrame);
            }
        }

        private void TickRetireDispatchLockStage(Entity vehicle, uint nowFrame)
        {
            if (!m_RetireHandoffStages.TryGetValue(vehicle, out RetireHandoffStageRecord stage))
                return;

            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
            {
                bool sameRun = TryGetWatch(vehicle, out _)
                    || (m_RetireHost.TryVehicleState(vehicle, out VehicleState runtimeState)
                        && runtimeState == VehicleState.Retiring);
                if (sameRun)
                    ReleaseRetireOwnership(vehicle, "retire-lock-entity-gone");
                else
                    RemoveRetireHandoff(vehicle);
                return;
            }
            if (!m_RetireHost.HasRetireDispatchLock(vehicle))
            {
                RemoveRetireHandoff(vehicle);
                return;
            }
            if (EntityManager.HasComponent<Deleted>(vehicle)
                || EntityManager.HasComponent<ParkedTrain>(vehicle))
            {
                stage.NextProbeFrame = nowFrame + RetireCadence.TerminalProbeFrames;
                m_RetireHost.SetRetireDeadline(vehicle, DeadlineKind.RetireHardAck, stage.NextProbeFrame);
                return;
            }

            ProjectRetireDispatchLock(vehicle);
            if (ArmOfficialRetireHandoff(vehicle, nowFrame))
            {
                stage.Stage = RetireHandoffStageKind.PendingBoundary;
                stage.NextProbeFrame = nowFrame + RetireCadence.BoundaryProbeFrames;
                m_RetireHost.SetRetireDeadline(vehicle, DeadlineKind.RetireBoundary, stage.NextProbeFrame);
                return;
            }

            Entity ownerDepot = EntityManager.HasComponent<Owner>(vehicle)
                ? EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                : Entity.Null;
            bool hardAck = IsRetireHandoffHardAck(vehicle, ownerDepot);
            bool currentRoutePresent = EntityManager.HasComponent<CurrentRoute>(vehicle);
            if (RtLog.VerboseEnabled
                && (!currentRoutePresent || hardAck)
                && TryGetWatch(vehicle, out RetireHandoffWatchRecord transitionWatch)
                && !transitionWatch.OfficialTransitionLogged)
            {
                transitionWatch.OfficialTransitionLogged = true;
                bool returning = EntityManager.HasComponent<PublicTransport>(vehicle)
                    && (EntityManager.GetComponentData<PublicTransport>(vehicle).m_State
                        & PublicTransportFlags.Returning) != 0;
                Log.Info("[RetireOfficialTransition] vehicle=" + vehicle.Index
                    + " requestedFrame=" + transitionWatch.RequestedFrame
                    + " transitionFrame=" + nowFrame
                    + " elapsedFrames=" + (nowFrame - transitionWatch.RequestedFrame)
                    + " requestWp=" + transitionWatch.RequestTargetWaypointIndex
                    + " requestBoarding=" + (transitionWatch.RequestBoarding ? "1" : "0")
                    + " requestEnRoute=" + (transitionWatch.RequestEnRoute ? "1" : "0")
                    + " requestDepartureDelta=" + transitionWatch.RequestDepartureDelta
                    + " transitionReturning=" + (returning ? "1" : "0")
                    + " transitionCurrentRoute=" + (currentRoutePresent ? "1" : "0"));
            }
            stage.Stage = hardAck
                ? RetireHandoffStageKind.OfficialReturning
                : RetireHandoffStageKind.PendingBoundary;
            stage.NextProbeFrame = nowFrame + (hardAck
                ? RetireCadence.TerminalProbeFrames
                : RetireCadence.BoundaryProbeFrames);
            m_RetireHost.SetRetireDeadline(vehicle,
                hardAck ? DeadlineKind.RetireHardAck : DeadlineKind.RetireBoundary,
                stage.NextProbeFrame);

            if (RtLog.VerboseEnabled
                && TryGetWatch(vehicle, out RetireHandoffWatchRecord watch)
                && nowFrame - watch.RequestedFrame >= ModRuntimeHostSystem.RETIRE_HANDOFF_MAX_AGE_FRAMES
                && nowFrame >= stage.NextDiagnosticFrame)
            {
                if (!hardAck)
                {
                    stage.NextDiagnosticFrame = nowFrame
                        + ModRuntimeHostSystem.RETIRE_HANDOFF_TRACE_COOLDOWN_FRAMES;
                    Log.Info("[RetireHandoffObserve] 车辆" + vehicle.Index
                        + " vanilla尚未进入hard ack"
                        + " route=" + (currentRoutePresent ? "1" : "0")
                        + " returning=" + (EntityManager.HasComponent<PublicTransport>(vehicle)
                            && (EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Returning) != 0 ? "1" : "0")
                        + " reason=" + watch.ReasonCode);
                }
                else
                {
                    Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);
                    bool vehicleParking = m_RetireHost.HasParkingNavLane(vehicle);
                    bool headParking = headVehicle != vehicle
                        && m_RetireHost.HasParkingNavLane(headVehicle);
                    if (vehicleParking || headParking)
                    {
                        stage.NextDiagnosticFrame = nowFrame
                            + ModRuntimeHostSystem.RETIRE_HANDOFF_TRACE_COOLDOWN_FRAMES;
                        Log.Info("[RetireParkingStall] vehicle=" + vehicle.Index
                            + " elapsedFrames=" + (nowFrame - watch.RequestedFrame)
                            + " vehicleParking=" + (vehicleParking ? "1" : "0")
                            + " headParking=" + (headParking ? "1" : "0")
                            + " returning=" + (EntityManager.HasComponent<PublicTransport>(vehicle)
                                && (EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Returning) != 0 ? "1" : "0")
                            + " route=" + (currentRoutePresent ? "1" : "0")
                            + " reason=" + watch.ReasonCode);
                    }
                }
            }
        }

        private bool ArmOfficialRetireHandoff(Entity vehicle, uint nowFrame)
        {
            if (!EntityManager.HasComponent<PublicTransport>(vehicle)
                || !m_RetireHandoffStages.TryGetValue(vehicle, out RetireHandoffStageRecord stage))
            {
                return false;
            }

            PublicTransport publicTransport = m_CommandHost.ReadPublicTransport(vehicle);
            bool hasCurrentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle);
            bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            bool targetWaypoint = false;
            bool boundaryReady = false;
            if (hasCurrentRoute && EntityManager.HasComponent<Target>(vehicle))
            {
                Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);
                targetWaypoint = m_RetireHost.IsRouteWaypointTarget(
                    vehicle,
                    m_CommandHost.ReadTarget(vehicle).m_Target);
                boundaryReady = IsRetireBoundaryReady(
                    vehicle,
                    headVehicle,
                    strictPathEndReached: false,
                    out _);
            }

            bool allowEnRouteClear = stage.Boarding.WindowEndFrame != 0
                || (hasCurrentRoute
                    ? targetWaypoint && boarding && boundaryReady
                    : boarding && !IsRetireHandoffHardAck(
                        vehicle,
                        EntityManager.HasComponent<Owner>(vehicle)
                            ? EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                            : Entity.Null));
            RetireBoardingResult result = RetireBoardingControl.Apply(
                publicTransport,
                stage.Boarding,
                allowEnRouteClear,
                CountPassengers(vehicle),
                hasCurrentRoute,
                nowFrame);
            stage.Boarding = result.State;
            if (result.Changed)
                m_CommandHost.SetPublicTransport(vehicle, result.PublicTransport);
            return result.WindowActive;
        }

        private int CountPassengers(Entity vehicle)
        {
            int count = 0;
            if (EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                if (layout.Length != 0)
                {
                    for (int i = 0; i < layout.Length; i++)
                    {
                        Entity unit = layout[i].m_Vehicle;
                        if (EntityManager.HasBuffer<Passenger>(unit))
                            count += EntityManager.GetBuffer<Passenger>(unit, true).Length;
                    }
                    return count;
                }
            }

            if (EntityManager.HasBuffer<Passenger>(vehicle))
                count = EntityManager.GetBuffer<Passenger>(vehicle, true).Length;

            return count;
        }

        public void FinalizeRetireDispatchLockTerminals()
        {
            if (m_RetireDispatchLockQuery.IsEmptyIgnoreFilter)
                return;

            NativeArray<Entity> lockedVehicles = m_RetireDispatchLockQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lockedVehicles.Length; i++)
                {
                    Entity vehicle = lockedVehicles[i];
                    bool parked = EntityManager.HasComponent<ParkedTrain>(vehicle);
                    bool deleted = EntityManager.HasComponent<Deleted>(vehicle);
                    if (!parked && !deleted)
                        continue;

                    NormalizeRetireDispatchLockTerminal(vehicle, releaseSameRunOwnership: true);
                }
            }
            finally
            {
                lockedVehicles.Dispose();
            }
        }

        private void NormalizeRetireDispatchLockTerminal(
            Entity vehicle,
            bool releaseSameRunOwnership)
        {
            bool parked = EntityManager.HasComponent<ParkedTrain>(vehicle);
            bool sameRun = releaseSameRunOwnership
                && (TryGetWatch(vehicle, out _)
                    || (m_RetireHost.TryVehicleState(vehicle, out VehicleState runtimeState)
                        && runtimeState == VehicleState.Retiring));

            m_RetireHost.ClearServiceDispatch(vehicle, out _);
            if (EntityManager.HasComponent<PublicTransport>(vehicle))
            {
                PublicTransport publicTransport = m_CommandHost.ReadPublicTransport(vehicle);
                publicTransport.m_RequestCount = 0;
                if (parked)
                    publicTransport.m_State &= ~PublicTransportFlags.Disabled;
                m_CommandHost.SetPublicTransport(vehicle, publicTransport);
            }

            EntityManager.RemoveComponent<RtRetireDispatchLock>(vehicle);
            m_RetireHandoffStages.Remove(vehicle);
            m_RetireHost.ClearRetireDeadline(vehicle);

            if (sameRun)
            {
                ReleaseRetireOwnership(
                    vehicle,
                    parked ? "retire-lock-parked" : "retire-lock-deleted");
            }
        }

        public void RemoveRetireHandoff(Entity vehicle)
        {
            RemoveWatch(vehicle);
            m_RetireHandoffStages.Remove(vehicle);
            m_RetireHost.ClearRetireDeadline(vehicle);
        }

        public void ClearRetireHandoffState()
        {
            ClearAll();
        }

        public void ResetRetireDispatchLockStages()
        {
            m_RetireHandoffStages.Clear();
            // 读档重建前不保留旧实体版本的期限。
            m_RetireHost.ClearRetireDeadline(Entity.Null);
            m_RetireDispatchLocksReconciledOnReady = false;
        }

        public void FlushRetireShadowSnapshots(Entity vehicle, string reason)
        {
            FlushShadow(vehicle, reason);
        }

        public void ResetRetireShadowSnapshots(Entity vehicle)
        {
            ResetShadow(vehicle);
        }

        public string DescribeRetireShadowTargetKind(Entity entity)
        {
            return m_RetireHost.DescribeTargetKind(entity);
        }

        public static string DescribeRetireShadowEntity(Entity entity)
        {
            return entity == Entity.Null ? "-" : entity.Index.ToString();
        }

        private void ReleaseRetireOwnership(Entity vehicle, string reason)
        {
            RemoveWatch(vehicle);
            m_RetireHandoffStages.Remove(vehicle);
            m_RetireHost.ClearRetireDeadline(vehicle);
            m_RetireHost.ReleaseRetireRuntimeOwnership(vehicle, reason);
            FlushShadow(vehicle, reason);
            ResetShadow(vehicle);
        }

        private bool IsRetireBoundaryReady(
            Entity vehicle,
            Entity headVehicle,
            bool strictPathEndReached,
            out string boundary)
        {
            if (strictPathEndReached || m_RetireHost.HasTrainLaneFlags(headVehicle, TrainLaneFlags.EndOfPath | TrainLaneFlags.EndReached))
            {
                boundary = "path-end-reached";
                return true;
            }

            bool vehicleEndOfPath = m_RetireHost.HasTrainLaneFlags(vehicle, TrainLaneFlags.EndOfPath);
            bool headEndOfPath = headVehicle != vehicle
                && m_RetireHost.HasTrainLaneFlags(headVehicle, TrainLaneFlags.EndOfPath);
            if ((vehicleEndOfPath || headEndOfPath)
                && m_RetireHost.HasNoTrainNavigation(vehicle)
                && (headVehicle == vehicle || m_RetireHost.HasNoTrainNavigation(headVehicle)))
            {
                boundary = "end-of-path-without-nav";
                return true;
            }
            if (m_CommandHost.HasConsumedPath(vehicle)
                || (headVehicle != vehicle && m_CommandHost.HasConsumedPath(headVehicle)))
            {
                boundary = "path-consumed-no-nav";
                return true;
            }

            boundary = "not-ready";
            return false;
        }

        private bool IsRetireHandoffHardAck(Entity vehicle, Entity ownerDepot)
        {
            if (EntityManager.HasComponent<PublicTransport>(vehicle))
            {
                PublicTransport publicTransport = m_CommandHost.ReadPublicTransport(vehicle);
                if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0)
                    return true;
            }
            if (m_RetireHost.HasDepotPathTarget(vehicle, ownerDepot))
                return true;

            Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);
            if (headVehicle != vehicle && m_RetireHost.HasDepotPathTarget(headVehicle, ownerDepot))
                return true;

            return m_RetireHost.HasParkingNavLane(vehicle)
                || (headVehicle != vehicle && m_RetireHost.HasParkingNavLane(headVehicle));
        }

        private bool TryGetWatch(Entity vehicle, out RetireHandoffWatchRecord watch)
        {
            return m_RetireHandoffWatch.TryGetValue(vehicle, out watch);
        }

        private void PutWatch(Entity vehicle, RetireHandoffWatchRecord watch)
        {
            m_RetireHandoffWatch[vehicle] = watch;
        }

        private void RemoveWatch(Entity vehicle)
        {
            m_RetireHandoffWatch.Remove(vehicle);
        }

        private void ClearWatch()
        {
            m_RetireHandoffWatch.Clear();
        }

        private void ClearAll()
        {
            ClearWatch();
            m_RetireHandoffStages.Clear();
            m_RetireHost.ClearRetireDeadline(Entity.Null);
            m_RetireDispatchLocksReconciledOnReady = false;
            m_RetireShadowHistory.Clear();
            m_RetireShadowLastSnapshot.Clear();
            m_RetireShadowLastFrame.Clear();
            m_RetireShadowLastRetiringFrame.Clear();
        }

        private void RecordShadow(Entity vehicle, string phase)
        {
            if (!RtLog.VerboseEnabled
                || vehicle == Entity.Null
                || !EntityManager.Exists(vehicle))
                return;

            uint nowFrame = m_RetireHost.Frame;
            if (!ShouldRecordShadowBeforeSnapshot(vehicle, phase, nowFrame))
                return;

            string snapshot = BuildShadowSnapshot(vehicle, phase);
            bool shouldSample = true;
            if (phase == "retiring"
                && m_RetireShadowLastSnapshot.TryGetValue(vehicle, out string lastSnapshot)
                && lastSnapshot == snapshot
                && m_RetireShadowLastFrame.TryGetValue(vehicle, out uint lastFrame)
                && (nowFrame - lastFrame) < ModRuntimeHostSystem.RETIRE_SHADOW_SAMPLE_INTERVAL_FRAMES)
            {
                shouldSample = false;
            }

            if (!shouldSample)
            {
                if (phase == "retiring")
                    m_RetireShadowLastRetiringFrame[vehicle] = nowFrame;
                return;
            }

            m_RetireShadowLastSnapshot[vehicle] = snapshot;
            m_RetireShadowLastFrame[vehicle] = nowFrame;
            if (phase == "retiring")
                m_RetireShadowLastRetiringFrame[vehicle] = nowFrame;

            if (!m_RetireShadowHistory.TryGetValue(vehicle, out List<string> history) || history == null)
            {
                history = new List<string>(ModRuntimeHostSystem.RETIRE_SHADOW_HISTORY_LIMIT);
                m_RetireShadowHistory[vehicle] = history;
            }

            if (history.Count >= ModRuntimeHostSystem.RETIRE_SHADOW_HISTORY_LIMIT)
                history.RemoveAt(0);
            history.Add(snapshot);
        }

        private void FlushShadow(Entity vehicle, string reason)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (!m_RetireShadowHistory.TryGetValue(vehicle, out List<string> history) || history == null || history.Count == 0)
                return;

            for (int i = 0; i < history.Count; i++)
            {
                Log.Info("[RetireShadow] 车辆" + vehicle.Index
                    + " reason=" + reason
                    + " step=" + (i + 1) + "/" + history.Count
                    + " " + history[i]);
            }
        }

        private void ResetShadow(Entity vehicle)
        {
            m_RetireShadowHistory.Remove(vehicle);
            m_RetireShadowLastSnapshot.Remove(vehicle);
            m_RetireShadowLastFrame.Remove(vehicle);
            m_RetireShadowLastRetiringFrame.Remove(vehicle);
        }

        private bool ShouldRecordShadowBeforeSnapshot(Entity vehicle, string phase, uint nowFrame)
        {
            if (phase != "retiring")
                return true;

            if (EntityManager.HasComponent<Deleted>(vehicle)
                || EntityManager.HasComponent<ParkedTrain>(vehicle))
            {
                return true;
            }

            return !m_RetireShadowLastRetiringFrame.TryGetValue(vehicle, out uint lastFrame)
                || nowFrame - lastFrame >= ModRuntimeHostSystem.RETIRE_SHADOW_SAMPLE_INTERVAL_FRAMES;
        }

        private string BuildShadowSnapshot(Entity vehicle, string phase)
        {
            string state = m_RetireHost.TryVehicleState(vehicle, out VehicleState runtimeState)
                ? runtimeState.ToString()
                : "-";
            Entity controllerEntity = EntityManager.HasComponent<Controller>(vehicle)
                ? EntityManager.GetComponentData<Controller>(vehicle).m_Controller
                : Entity.Null;
            Entity ownerDepot = EntityManager.HasComponent<Owner>(vehicle)
                ? EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                : Entity.Null;
            Entity targetEntity = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            string publicFlags = EntityManager.HasComponent<PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<PublicTransport>(vehicle).m_State.ToString()
                : "-";
            string cargoFlags = EntityManager.HasComponent<CargoTransport>(vehicle)
                ? EntityManager.GetComponentData<CargoTransport>(vehicle).m_State.ToString()
                : "-";
            int pathLen = EntityManager.HasBuffer<PathElement>(vehicle) ? EntityManager.GetBuffer<PathElement>(vehicle, true).Length : -1;
            string pathFlags = "-";
            int pathElementIndex = -1;
            PathFlags pathFlagBits = 0;
            if (EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
                pathFlags = pathOwner.m_State.ToString();
                pathElementIndex = pathOwner.m_ElementIndex;
                pathFlagBits = pathOwner.m_State;
            }

            int navLen = EntityManager.HasBuffer<TrainNavigationLane>(vehicle)
                ? EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true).Length
                : -1;
            string lastNavFlags = "-";
            Entity lastNavLane = Entity.Null;
            if (navLen > 0)
            {
                DynamicBuffer<TrainNavigationLane> navigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true);
                lastNavFlags = navigationLanes[navLen - 1].m_Flags.ToString();
                lastNavLane = navigationLanes[navLen - 1].m_Lane;
            }

            string frontLaneFlags = EntityManager.HasComponent<TrainCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle).m_Front.m_LaneFlags.ToString()
                : "-";
            Entity frontLane = EntityManager.HasComponent<TrainCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle).m_Front.m_Lane
                : Entity.Null;
            Entity rearLane = EntityManager.HasComponent<TrainCurrentLane>(vehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(vehicle).m_Rear.m_Lane
                : Entity.Null;
            int layoutLen = EntityManager.HasBuffer<LayoutElement>(vehicle) ? EntityManager.GetBuffer<LayoutElement>(vehicle, true).Length : -1;
            Entity headVehicle = m_RetireHost.ResolveHandoffHead(vehicle);

            int headNavLen = EntityManager.HasBuffer<TrainNavigationLane>(headVehicle)
                ? EntityManager.GetBuffer<TrainNavigationLane>(headVehicle, true).Length
                : -1;
            string headLastNavFlags = "-";
            Entity headLastNavLane = Entity.Null;
            if (headNavLen > 0)
            {
                DynamicBuffer<TrainNavigationLane> headNavigationLanes = EntityManager.GetBuffer<TrainNavigationLane>(headVehicle, true);
                headLastNavFlags = headNavigationLanes[headNavLen - 1].m_Flags.ToString();
                headLastNavLane = headNavigationLanes[headNavLen - 1].m_Lane;
            }

            int headPathLen = EntityManager.HasBuffer<PathElement>(headVehicle)
                ? EntityManager.GetBuffer<PathElement>(headVehicle, true).Length
                : -1;
            string headFrontFlags = EntityManager.HasComponent<TrainCurrentLane>(headVehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(headVehicle).m_Front.m_LaneFlags.ToString()
                : "-";
            Entity headFrontLane = EntityManager.HasComponent<TrainCurrentLane>(headVehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(headVehicle).m_Front.m_Lane
                : Entity.Null;
            Entity headRearLane = EntityManager.HasComponent<TrainCurrentLane>(headVehicle)
                ? EntityManager.GetComponentData<TrainCurrentLane>(headVehicle).m_Rear.m_Lane
                : Entity.Null;
            string headPathFlags = "-";
            PathFlags headPathFlagBits = 0;
            if (EntityManager.HasComponent<PathOwner>(headVehicle))
            {
                PathOwner headPathOwner = EntityManager.GetComponentData<PathOwner>(headVehicle);
                headPathFlags = headPathOwner.m_State.ToString();
                headPathFlagBits = headPathOwner.m_State;
            }

            Entity pathInfoDest = EntityManager.HasComponent<PathInformation>(vehicle)
                ? EntityManager.GetComponentData<PathInformation>(vehicle).m_Destination
                : Entity.Null;
            string pathInfoState = EntityManager.HasComponent<PathInformation>(vehicle)
                ? EntityManager.GetComponentData<PathInformation>(vehicle).m_State.ToString()
                : "-";
            Entity headPathInfoDest = EntityManager.HasComponent<PathInformation>(headVehicle)
                ? EntityManager.GetComponentData<PathInformation>(headVehicle).m_Destination
                : Entity.Null;
            string headPathInfoState = EntityManager.HasComponent<PathInformation>(headVehicle)
                ? EntityManager.GetComponentData<PathInformation>(headVehicle).m_State.ToString()
                : "-";

            string ownerDepotFlags = EntityManager.HasComponent<Game.Buildings.TransportDepot>(ownerDepot)
                ? EntityManager.GetComponentData<Game.Buildings.TransportDepot>(ownerDepot).m_Flags.ToString()
                : "-";
            int ownerDepotAvailable = EntityManager.HasComponent<Game.Buildings.TransportDepot>(ownerDepot)
                ? EntityManager.GetComponentData<Game.Buildings.TransportDepot>(ownerDepot).m_AvailableVehicles
                : -1;
            bool hasHandoffWatch = m_RetireHandoffWatch.TryGetValue(vehicle, out RetireHandoffWatchRecord handoffWatch);

            string targetKind = m_RetireHost.DescribeTargetKind(targetEntity);
            string guess = ClassifyRetireShadowGuess(
                targetEntity,
                ownerDepot,
                pathFlagBits,
                headPathFlagBits,
                navLen,
                headNavLen,
                lastNavFlags,
                headLastNavFlags,
                frontLaneFlags,
                headFrontFlags,
                pathLen,
                pathElementIndex,
                headPathLen,
                EntityManager.HasComponent<ParkedTrain>(vehicle),
                EntityManager.HasComponent<Deleted>(vehicle));

            return "frame=" + m_RetireHost.Frame
                + " phase=" + phase
                + " state=" + state
                + " pending=" + (hasHandoffWatch ? "1" : "0")
                + " guess=" + guess
                + " ctrl=" + m_RetireHost.DescribeEntity(controllerEntity)
                + " owner=" + m_RetireHost.DescribeEntity(ownerDepot)
                + " ownerFlags=" + ownerDepotFlags
                + " ownerAvail=" + ownerDepotAvailable
                + " target=" + m_RetireHost.DescribeEntity(targetEntity)
                + " targetKind=" + targetKind
                + " targetExists=" + ((targetEntity != Entity.Null && EntityManager.Exists(targetEntity)) ? "1" : "0")
                + " targetIsOwner=" + ((targetEntity != Entity.Null && targetEntity == ownerDepot) ? "1" : "0")
                + " route=" + m_RetireHost.DescribeEntity(currentRoute)
                + " deleted=" + (EntityManager.HasComponent<Deleted>(vehicle) ? "1" : "0")
                + " parked=" + (EntityManager.HasComponent<ParkedTrain>(vehicle) ? "1" : "0")
                + " pfu=" + (EntityManager.HasComponent<PathfindUpdated>(vehicle) ? "1" : "0")
                + " upd=" + (EntityManager.HasComponent<Updated>(vehicle) ? "1" : "0")
                + " pt=" + publicFlags
                + " cargo=" + cargoFlags
                + " path=" + pathFlags
                + " pathLen=" + pathLen
                + " pathIdx=" + pathElementIndex
                + " piDest=" + m_RetireHost.DescribeEntity(pathInfoDest)
                + " piState=" + pathInfoState
                + " navLen=" + navLen
                + " navLane=" + m_RetireHost.DescribeEntity(lastNavLane)
                + " navLast=" + lastNavFlags
                + " front=" + frontLaneFlags
                + " frontLane=" + m_RetireHost.DescribeEntity(frontLane)
                + " rearLane=" + m_RetireHost.DescribeEntity(rearLane)
                + " layout=" + layoutLen
                + " head=" + m_RetireHost.DescribeEntity(headVehicle)
                + " headSelf=" + (headVehicle == vehicle ? "1" : "0")
                + " headPfu=" + (EntityManager.HasComponent<PathfindUpdated>(headVehicle) ? "1" : "0")
                + " headUpd=" + (EntityManager.HasComponent<Updated>(headVehicle) ? "1" : "0")
                + " headNav=" + headNavLen
                + " headNavLane=" + m_RetireHost.DescribeEntity(headLastNavLane)
                + " headNavLast=" + headLastNavFlags
                + " headPath=" + headPathLen
                + " headPathFlags=" + headPathFlags
                + " headPiDest=" + m_RetireHost.DescribeEntity(headPathInfoDest)
                + " headPiState=" + headPathInfoState
                + " headFront=" + headFrontFlags
                + " headFrontLane=" + m_RetireHost.DescribeEntity(headFrontLane)
                + " headRearLane=" + m_RetireHost.DescribeEntity(headRearLane);
        }

        private string ClassifyRetireShadowGuess(
            Entity targetEntity,
            Entity ownerDepot,
            PathFlags pathFlags,
            PathFlags headPathFlags,
            int navLen,
            int headNavLen,
            string navLastFlags,
            string headNavLastFlags,
            string frontLaneFlags,
            string headFrontFlags,
            int pathLen,
            int pathElementIndex,
            int headPathLen,
            bool parked,
            bool deleted)
        {
            if (parked) return "parked";
            if (deleted) return "deleted-marked";
            if (targetEntity == Entity.Null || !EntityManager.Exists(targetEntity)) return "target-invalid";
            if ((pathFlags & PathFlags.Stuck) != 0 || (headPathFlags & PathFlags.Stuck) != 0) return "path-stuck";
            if ((pathFlags & PathFlags.Failed) != 0 || (headPathFlags & PathFlags.Failed) != 0) return "path-failed";
            if (navLen == 0 && headNavLen > 0) return "root-nav-missing-head-nav-present";
            if (navLen == 0 && (HasFlagText(frontLaneFlags, "EndOfPath") || HasFlagText(headFrontFlags, "EndOfPath")))
                return "end-of-path-without-nav";
            if (navLen > 0 && HasFlagText(navLastFlags, "ParkingSpace") && !parked) return "parking-space-not-parked";
            if (headNavLen > 0 && HasFlagText(headNavLastFlags, "ParkingSpace") && !parked) return "head-parking-space-not-parked";
            if (navLen > 0 && !HasFlagText(navLastFlags, "ParkingSpace") && HasFlagText(frontLaneFlags, "EndOfPath"))
                return "end-of-path-non-parking";
            if (navLen == 0 && pathLen >= 0 && pathElementIndex >= pathLen) return "path-consumed-no-nav";
            if (headNavLen == 0 && headPathLen >= 0 && headPathLen > 0 && targetEntity == ownerDepot)
                return "head-path-consumed-no-nav";
            return "unknown";
        }

        private static bool HasFlagText(string text, string flag)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf(flag, StringComparison.Ordinal) >= 0;
        }
    }
}
