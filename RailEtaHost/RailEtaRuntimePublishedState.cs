using System;
using System.Collections.Generic;
using System.Threading;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Scheduling;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEta.Engine;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class PublishedRailEtaControlledHoldSnapshot
    {
        internal static readonly PublishedRailEtaControlledHoldSnapshot Empty = new PublishedRailEtaControlledHoldSnapshot(0, new Dictionary<Entity, RailControlledHoldSnapshot>());
        internal PublishedRailEtaControlledHoldSnapshot(uint frame, IReadOnlyDictionary<Entity, RailControlledHoldSnapshot> holds)
        { Frame = frame; Holds = holds; }
        internal uint Frame { get; }
        internal IReadOnlyDictionary<Entity, RailControlledHoldSnapshot> Holds { get; }
    }

    public partial class DispatchRuntimeSystem
    {
        private PublishedRailEtaControlledHoldSnapshot m_RailEtaControlledHolds = PublishedRailEtaControlledHoldSnapshot.Empty;
        internal PublishedRailEtaControlledHoldSnapshot RailEtaControlledHolds => Volatile.Read(ref m_RailEtaControlledHolds);

        internal bool ShouldPublishRailEtaControlledHolds()
        {
            RailEtaHost.RailEtaService service = m_RailEtaService;
            return service != null && !service.IsDisposed && service.ShouldCaptureHoldSnapshot;
        }

        internal void PublishRailEtaControlledHolds(uint frame, Dictionary<Entity, RailControlledHoldSnapshot> holds)
        {
            Volatile.Write(ref m_RailEtaControlledHolds, new PublishedRailEtaControlledHoldSnapshot(frame, holds));
            // Capture request is edge-triggered: Dispatch consumed it for this frame.
            m_RailEtaService?.ClearHoldSnapshotCapture();
        }

        internal RailControlledHoldSnapshot BuildRailEtaControlledHold(Entity vehicle, VehicleState state, int targetMinute, uint frame, int nowMinute)
        {
            if (Bypass.TryGetConflictEpisode(vehicle, out BypassConflictEpisode episode) && episode.BlockerVehicle != Entity.Null)
                return BuildRailEtaBypassHold(episode);
            if (state != VehicleState.Holding) return null;
            bool atOrigin = m_CachedWpIdx.IsCreated && m_CachedWpIdx.TryGetValue(vehicle, out int waypointIndex) && waypointIndex == 0;
            if (targetMinute < 0 || !atOrigin)
                return new RailControlledHoldSnapshot { Kind = RailControlledHoldKind.UnknownControlledHold, ReasonCode = "controlled-hold-without-authoritative-release" };
            double minuteExact = m_TimeSystem.normalizedTime * 1440.0;
            double minutes = ScheduleClock.CurrentOrRecent(nowMinute, targetMinute) ? 0.0 : (targetMinute - minuteExact + 1440.0) % 1440.0;
            uint scheduled = unchecked(frame + (uint)Math.Ceiling(minutes * SIM_FRAMES_PER_MINUTE));
            return new RailControlledHoldSnapshot
            {
                Kind = RailControlledHoldKind.OriginScheduled,
                EarliestReleaseFrame = unchecked(scheduled + RailPhysicsCalculator.FramesUntilNextNavigationTick(scheduled & 15u)),
                ReasonCode = "origin-schedule-target"
            };
        }

        private RailControlledHoldSnapshot BuildRailEtaBypassHold(BypassConflictEpisode episode)
        {
            BypassLatchedBlockerProjection projection = episode.LatchedBlockerProjection;
            if (!episode.HasLatchedBlockerProjection || !projection.Available || projection.SharedTrackVersion != TrackModel.SharedIndexVersion
                || !TrackModel.TryChain(projection.ExpressLine, out LineTrackChain chain) || chain == null || chain.Signature != projection.ExpressChainSignature)
                return new RailControlledHoldSnapshot { Kind = RailControlledHoldKind.UnknownControlledHold, ReasonCode = "bypass-release-projection-unavailable" };
            int intervalStart = projection.ExpressProtectedInterval.StartAtomIndex;
            int intervalEnd = projection.ExpressProtectedInterval.EndAtomIndexExclusive;
            double coordinate = Math.Max(0.0, Math.Min(intervalEnd - intervalStart, projection.ExpressReleaseCoordinate));
            int relativeAtom = Math.Min(Math.Max(0, intervalEnd - intervalStart - 1), (int)Math.Floor(coordinate));
            int atomIndex = intervalStart + relativeAtom;
            if (atomIndex < 0 || atomIndex >= chain.TrackAtoms.Count)
                return new RailControlledHoldSnapshot { Kind = RailControlledHoldKind.UnknownControlledHold, ReasonCode = "bypass-release-atom-out-of-range" };
            TrackAtom atom = chain.TrackAtoms[atomIndex];
            if (atom.AtomClass != TrackAtomClass.PrimaryLane || atom.Key.PhysicalLaneKey == Entity.Null)
                return new RailControlledHoldSnapshot { Kind = RailControlledHoldKind.UnknownControlledHold, ReasonCode = "bypass-release-atom-not-physical-lane" };
            double atomPosition = Math.Max(0.0, Math.Min(1.0, coordinate - relativeAtom));
            return new RailControlledHoldSnapshot
            {
                Kind = RailControlledHoldKind.BypassYield,
                ReleaseVehicleId = new RailVehicleId(RailEtaHost.RailEtaEntityId.Pack(episode.BlockerVehicle)),
                ReleaseLaneId = new RailLaneId(RailEtaHost.RailEtaEntityId.Pack(atom.Key.PhysicalLaneKey)),
                ReleaseLaneFraction = atom.TargetDelta.x + (atom.TargetDelta.y - atom.TargetDelta.x) * atomPosition,
                ReleaseDirection = atom.TraversalDir == TrackTraversalDir.Reverse ? -1 : 1,
                TrackModelSignature = chain.Signature,
                ReasonCode = "bypass-express-release-coordinate"
            };
        }
    }
}
