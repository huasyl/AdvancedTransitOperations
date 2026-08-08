using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Commands
{
    internal sealed class RoadRetireHandoff
    {
        private sealed class Entry
        {
            internal Entity Owner;
            internal uint NextProbeFrame;
            internal bool Accepted;
            internal RetireBoardingState Boarding;
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly RetireHost m_RetireHost;
        private readonly RoadCommandHost m_CommandHost;
        private readonly Dictionary<Entity, Entry> m_Entries = new Dictionary<Entity, Entry>();

        internal RoadRetireHandoff(
            ModRuntimeHostSystem runtime,
            RetireHost retireHost,
            RoadCommandHost commandHost)
        {
            m_Runtime = runtime;
            m_RetireHost = retireHost;
            m_CommandHost = commandHost;
        }

        internal void Begin(RetireStartInput input, RetireStartContext start)
        {
            Entity vehicle = start.Vehicle;
            if (vehicle == Entity.Null || !m_CommandHost.EntityManager.Exists(vehicle) || m_Entries.ContainsKey(vehicle))
                return;

            Entry entry = new Entry
            {
                Owner = m_Runtime.CanonDepot(input.Owner.m_Owner),
                NextProbeFrame = m_CommandHost.Frame
            };
            m_Entries.Add(vehicle, entry);
            Project(vehicle, entry, input.PublicTransport, m_CommandHost.Frame);
            m_RetireHost.SetRetireDeadline(vehicle, DeadlineKind.RetireBoundary, entry.NextProbeFrame);
        }

        internal void Tick(uint nowFrame, IReadOnlyList<FramePlanEntry> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return;

            for (int i = 0; i < candidates.Count; i++)
            {
                Entity vehicle = candidates[i].Vehicle;
                if (!m_Entries.TryGetValue(vehicle, out Entry entry) || nowFrame < entry.NextProbeFrame)
                    continue;

                m_RetireHost.CountRetireStageExecuted();
                Tick(vehicle, entry, nowFrame);
            }
        }

        internal void Remove(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Entries.Remove(vehicle);
        }

        internal void Clear()
        {
            m_Entries.Clear();
        }

        private void Tick(Entity vehicle, Entry entry, uint nowFrame)
        {
            if (vehicle == Entity.Null || !m_CommandHost.EntityManager.Exists(vehicle))
            {
                Terminal(vehicle, entry);
                return;
            }

            if (IsTerminal(vehicle))
            {
                Terminal(vehicle, entry);
                return;
            }

            if (entry.Accepted)
            {
                entry.NextProbeFrame = nowFrame + RetireCadence.TerminalProbeFrames;
                m_RetireHost.SetRetireDeadline(
                    vehicle,
                    DeadlineKind.RetireHardAck,
                    entry.NextProbeFrame);
                return;
            }

            PublicTransport publicTransport = m_CommandHost.ReadPublicTransport(vehicle);
            Project(vehicle, entry, publicTransport, nowFrame);
            if (IsAccepted(vehicle, entry.Owner, publicTransport))
                Accept(vehicle, entry);

            entry.NextProbeFrame = nowFrame + (entry.Accepted
                ? RetireCadence.TerminalProbeFrames
                : RetireCadence.BoundaryProbeFrames);
            m_RetireHost.SetRetireDeadline(
                vehicle,
                entry.Accepted ? DeadlineKind.RetireHardAck : DeadlineKind.RetireBoundary,
                entry.NextProbeFrame);
        }

        private void Project(Entity vehicle, Entry entry, PublicTransport publicTransport, uint nowFrame)
        {
            bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            RetireBoardingResult result = RetireBoardingControl.Apply(
                publicTransport,
                entry.Boarding,
                boarding || entry.Boarding.WindowEndFrame != 0,
                CountPassengers(vehicle),
                m_CommandHost.EntityManager.HasComponent<CurrentRoute>(vehicle),
                nowFrame);
            entry.Boarding = result.State;
            if (result.Changed)
                m_CommandHost.CommitPublicTransport(vehicle, result.PublicTransport);
        }

        private bool IsAccepted(Entity vehicle, Entity owner, PublicTransport publicTransport)
        {
            if (owner == Entity.Null
                || (publicTransport.m_State & PublicTransportFlags.Returning) == 0
                || m_CommandHost.EntityManager.HasComponent<CurrentRoute>(vehicle)
                || !m_CommandHost.EntityManager.HasComponent<Target>(vehicle))
            {
                return false;
            }

            Entity target = m_CommandHost.ReadTarget(vehicle).m_Target;
            return m_Runtime.CanonDepot(target) == owner;
        }

        private bool IsTerminal(Entity vehicle)
        {
            return vehicle == Entity.Null
                || !m_CommandHost.EntityManager.Exists(vehicle)
                || m_CommandHost.EntityManager.HasComponent<ParkedCar>(vehicle)
                || m_CommandHost.EntityManager.HasComponent<Deleted>(vehicle);
        }

        private void Accept(Entity vehicle, Entry entry)
        {
            if (entry.Accepted)
                return;

            entry.Accepted = true;
            m_Runtime.m_RoadEventSource.RemoveRetireSource(vehicle);
            m_Runtime.m_RouteProgress.Remove(vehicle);
        }

        private void Terminal(Entity vehicle, Entry entry)
        {
            Accept(vehicle, entry);
            m_RetireHost.ReleaseRoadRetireRuntimeOwnership(vehicle);
            m_RetireHost.ClearRetireDeadline(vehicle);
            m_Entries.Remove(vehicle);
        }

        private int CountPassengers(Entity vehicle)
        {
            if (m_CommandHost.EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = m_CommandHost.EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                int count = 0;
                for (int i = 0; i < layout.Length; i++)
                {
                    Entity unit = layout[i].m_Vehicle;
                    if (m_CommandHost.EntityManager.HasBuffer<Passenger>(unit))
                        count += m_CommandHost.EntityManager.GetBuffer<Passenger>(unit, true).Length;
                }
                if (layout.Length > 0)
                    return count;
            }

            return m_CommandHost.EntityManager.HasBuffer<Passenger>(vehicle)
                ? m_CommandHost.EntityManager.GetBuffer<Passenger>(vehicle, true).Length
                : 0;
        }
    }
}
