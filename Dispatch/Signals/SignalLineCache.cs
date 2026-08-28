using System;
using System.Collections.Generic;
using Game.Net;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Signals
{
    internal readonly struct TramSignalPosition
    {
        internal readonly int AtomIndex;
        internal readonly int SectionIndex;
        internal readonly Entity CurrentLane;
        internal readonly float VehicleMeters;

        internal TramSignalPosition(
            int atomIndex,
            int sectionIndex,
            Entity currentLane,
            float vehicleMeters)
        {
            AtomIndex = atomIndex;
            SectionIndex = sectionIndex;
            CurrentLane = currentLane;
            VehicleMeters = vehicleMeters;
        }
    }

    internal readonly struct SignalTrackAtom
    {
        internal readonly float StartMeters;
        internal readonly float EndMeters;

        internal SignalTrackAtom(float startMeters, float endMeters)
        {
            StartMeters = startMeters;
            EndMeters = endMeters;
        }
    }

    internal readonly struct SignalJunction
    {
        internal readonly int AtomIndex;
        internal readonly Entity SignalLane;

        internal SignalJunction(
            int atomIndex,
            Entity signalLane)
        {
            AtomIndex = atomIndex;
            SignalLane = signalLane;
        }
    }

    internal readonly struct SignalTrackSection
    {
        internal readonly int StartAtomIndex;
        internal readonly int WrapJunctionOffset;
        internal readonly bool WrapsAtoms;
        internal readonly int FirstJunctionIndex;
        internal readonly int JunctionCount;

        internal SignalTrackSection(
            int startAtomIndex,
            int wrapJunctionOffset,
            bool wrapsAtoms,
            int firstJunctionIndex,
            int junctionCount)
        {
            StartAtomIndex = startAtomIndex;
            WrapJunctionOffset = wrapJunctionOffset;
            WrapsAtoms = wrapsAtoms;
            FirstJunctionIndex = firstJunctionIndex;
            JunctionCount = junctionCount;
        }
    }

    internal sealed class SignalLineModel
    {
        internal float TotalDistanceMeters;
        internal SignalTrackAtom[] Atoms = Array.Empty<SignalTrackAtom>();
        internal SignalJunction[] Junctions = Array.Empty<SignalJunction>();
        internal SignalTrackSection[] Sections = Array.Empty<SignalTrackSection>();
        internal int[] SectionBySegment = Array.Empty<int>();
        internal int[] DepartureSectionByWaypoint = Array.Empty<int>();
    }

    internal sealed class SignalLineCache
    {
        private readonly EntityManager m_Entities;
        private readonly Dictionary<Entity, SignalLineModel> m_Lines =
            new Dictionary<Entity, SignalLineModel>();

        internal SignalLineCache(EntityManager entities)
        {
            m_Entities = entities;
        }

        internal void Invalidate(Entity line)
        {
            if (line != Entity.Null)
                m_Lines.Remove(line);
        }

        internal bool TryGet(
            Entity line,
            LineTrackChain chain,
            LineStopLayout layout,
            LineMileageModel mileage,
            out SignalLineModel model)
        {
            model = null;
            if (line == Entity.Null
                || chain == null
                || layout == null
                || mileage == null)
            {
                return false;
            }

            if (!m_Lines.TryGetValue(line, out model))
            {
                model = BuildModel(chain, layout, mileage);
                m_Lines[line] = model;
            }
            return model != null;
        }

        internal void Clear()
        {
            m_Lines.Clear();
        }

        internal static TramSignalPosition MapPosition(
            SignalLineModel model,
            VehicleTrackCursor trackPosition,
            Entity currentLane)
        {
            SignalTrackAtom atom = model.Atoms[trackPosition.AtomCursorIndex];
            float atomMeters = LineMileage.Forward(
                model.TotalDistanceMeters,
                atom.StartMeters,
                atom.EndMeters);
            float vehicleMeters = Normalize(
                atom.StartMeters + atomMeters * trackPosition.AtomPosition01,
                model.TotalDistanceMeters);
            return new TramSignalPosition(
                trackPosition.AtomCursorIndex,
                model.SectionBySegment[trackPosition.SegmentIndex],
                currentLane,
                vehicleMeters);
        }

        private SignalLineModel BuildModel(
            LineTrackChain chain,
            LineStopLayout layout,
            LineMileageModel mileage)
        {
            int waypointCount = layout.WaypointCount;
            if (chain.Mode != TransitMode.Tram
                || !chain.ChainComplete
                || waypointCount <= 0
                || layout.StopCount < 2
                || chain.SegmentRanges == null
                || chain.SegmentRanges.Count != waypointCount
                || mileage == null
                || !math.isfinite(mileage.TotalDistanceMeters)
                || mileage.TotalDistanceMeters <= 0f
                || mileage.WaypointDistances == null
                || mileage.WaypointDistances.Length != waypointCount)
            {
                return null;
            }

            SignalTrackAtom[] atoms = new SignalTrackAtom[chain.TrackAtoms.Count];
            for (int segmentIndex = 0; segmentIndex < waypointCount; segmentIndex++)
            {
                TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                if (range.StartAtomIndex < 0
                    || range.EndAtomIndexExclusive <= range.StartAtomIndex
                    || range.EndAtomIndexExclusive > chain.TrackAtoms.Count
                    || !TryMapSegment(chain, mileage, segmentIndex, range, atoms))
                {
                    return null;
                }
            }

            int[] sectionBySegment = NewIndex(waypointCount);
            int[] departureByWaypoint = NewIndex(waypointCount);
            List<SignalTrackSection> sections = new List<SignalTrackSection>(layout.StopCount);
            List<SignalJunction> junctions = new List<SignalJunction>(chain.JunctionMarkers.Count);
            for (int stopIndex = 0; stopIndex < layout.StopCount; stopIndex++)
            {
                int start = layout[stopIndex].WaypointIndex;
                int end = layout[(stopIndex + 1) % layout.StopCount].WaypointIndex;
                if (start < 0 || start >= waypointCount || end < 0 || end >= waypointCount
                    || start == end || departureByWaypoint[start] >= 0)
                {
                    return null;
                }

                int sectionIndex = sections.Count;
                departureByWaypoint[start] = sectionIndex;
                int firstJunction = junctions.Count;
                int segment = start;
                int traversed = 0;
                while (segment != end && traversed < waypointCount)
                {
                    if (sectionBySegment[segment] >= 0)
                        return null;
                    sectionBySegment[segment] = sectionIndex;
                    AppendJunctions(chain, atoms, segment, junctions);
                    segment = (segment + 1) % waypointCount;
                    traversed++;
                }
                if (segment != end || traversed == 0)
                    return null;

                int junctionCount = junctions.Count - firstJunction;
                int wrapOffset = junctionCount;
                for (int i = 1; i < junctionCount; i++)
                {
                    if (junctions[firstJunction + i].AtomIndex
                        < junctions[firstJunction + i - 1].AtomIndex)
                    {
                        wrapOffset = i;
                        break;
                    }
                }

                sections.Add(new SignalTrackSection(
                    chain.SegmentRanges[start].StartAtomIndex,
                    wrapOffset,
                    start > end,
                    firstJunction,
                    junctionCount));
            }

            for (int i = 0; i < sectionBySegment.Length; i++)
            {
                if (sectionBySegment[i] < 0 || sectionBySegment[i] >= sections.Count)
                    return null;
            }
            for (int i = 0; i < departureByWaypoint.Length; i++)
            {
                if (departureByWaypoint[i] >= sections.Count)
                    return null;
            }

            if (atoms.Length != chain.TrackAtoms.Count
                || sectionBySegment.Length != chain.SegmentRanges.Count
                || departureByWaypoint.Length != chain.SegmentRanges.Count)
            {
                return null;
            }

            return new SignalLineModel
            {
                TotalDistanceMeters = mileage.TotalDistanceMeters,
                Atoms = atoms,
                Junctions = junctions.ToArray(),
                Sections = sections.ToArray(),
                SectionBySegment = sectionBySegment,
                DepartureSectionByWaypoint = departureByWaypoint
            };
        }

        private bool TryMapSegment(
            LineTrackChain chain,
            LineMileageModel mileage,
            int segmentIndex,
            TrackSegmentRange range,
            SignalTrackAtom[] result)
        {
            float rawTotal = 0f;
            for (int atomIndex = range.StartAtomIndex; atomIndex < range.EndAtomIndexExclusive; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                Entity curveLane = atom.SourceTarget;
                if (curveLane == Entity.Null
                    || !m_Entities.Exists(curveLane)
                    || !m_Entities.HasComponent<Curve>(curveLane))
                {
                    return false;
                }
                Curve curve = m_Entities.GetComponentData<Curve>(curveLane);
                float raw = curve.m_Length * math.abs(atom.TargetDelta.y - atom.TargetDelta.x);
                if (!math.isfinite(raw) || raw < 0f)
                    return false;
                rawTotal += raw;
            }
            if (!math.isfinite(rawTotal) || rawTotal <= 0f)
                return false;

            int next = (segmentIndex + 1) % mileage.WaypointDistances.Length;
            float startMeters = mileage.WaypointDistances[segmentIndex];
            float axisMeters = LineMileage.Forward(
                mileage.TotalDistanceMeters,
                startMeters,
                mileage.WaypointDistances[next]);
            if (!math.isfinite(axisMeters) || axisMeters <= 0f)
                return false;

            float rawPrefix = 0f;
            for (int atomIndex = range.StartAtomIndex; atomIndex < range.EndAtomIndexExclusive; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                Curve curve = m_Entities.GetComponentData<Curve>(atom.SourceTarget);
                float raw = curve.m_Length * math.abs(atom.TargetDelta.y - atom.TargetDelta.x);
                float atomStart = Normalize(startMeters + axisMeters * rawPrefix / rawTotal, mileage.TotalDistanceMeters);
                rawPrefix += raw;
                float atomEnd = Normalize(startMeters + axisMeters * rawPrefix / rawTotal, mileage.TotalDistanceMeters);
                result[atomIndex] = new SignalTrackAtom(atomStart, atomEnd);
            }
            return true;
        }

        private static void AppendJunctions(
            LineTrackChain chain,
            SignalTrackAtom[] atoms,
            int segmentIndex,
            List<SignalJunction> result)
        {
            for (int i = 0; i < chain.JunctionMarkers.Count; i++)
            {
                TrackJunctionMarker marker = chain.JunctionMarkers[i];
                if (marker.SegmentIndex != segmentIndex
                    || marker.AtomIndex < 0
                    || marker.AtomIndex >= atoms.Length)
                {
                    continue;
                }
                result.Add(new SignalJunction(
                    marker.AtomIndex,
                    marker.SignalLane));
            }
        }

        private static int[] NewIndex(int length)
        {
            int[] result = new int[length];
            for (int i = 0; i < result.Length; i++)
                result[i] = -1;
            return result;
        }

        private static float Normalize(float meters, float totalMeters)
        {
            float normalized = meters % totalMeters;
            return normalized < 0f ? normalized + totalMeters : normalized;
        }
    }
}
