using System;
using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Diagnostics
{
    internal sealed class SpawnIntentTrace
    {
        private sealed class Entry
        {
            public string Id = string.Empty;
            public Entity Line;
            public Entity Vehicle;
            public int Slot;
            public uint TriggerFrame;
            public uint ExpectedOriginFrame;
            public uint SpawnFrame;
            public uint ArrivalFrame;
            public uint DepartureFrame;
            public float LeadFrames;
            public string LeadSource = string.Empty;
            public int OriginHoldMinutes;
            public int ActualCount;
            public Entity NearVehicle;
            public VehicleState NearState;
            public float NearEta;
            public string NearReason = string.Empty;
        }

        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Dictionary<Entity, Entry> m_Pending = new Dictionary<Entity, Entry>();
        private readonly Dictionary<Entity, Entry> m_Vehicles = new Dictionary<Entity, Entry>();

        internal SpawnIntentTrace(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal string Create(
            Entity line,
            int slot,
            uint frame,
            float leadFrames,
            string leadSource,
            int originHoldMinutes,
            int actualCount,
            Entity nearVehicle,
            VehicleState nearState,
            float nearEta,
            string nearReason)
        {
            uint roundedLead = (uint)math.max(0f, math.round(leadFrames));
            var entry = new Entry
            {
                Id = line.Index + ":" + frame,
                Line = line,
                Slot = slot,
                TriggerFrame = frame,
                ExpectedOriginFrame = unchecked(frame + roundedLead),
                LeadFrames = leadFrames,
                LeadSource = leadSource ?? string.Empty,
                OriginHoldMinutes = originHoldMinutes,
                ActualCount = actualCount,
                NearVehicle = nearVehicle,
                NearState = nearState,
                NearEta = nearEta,
                NearReason = nearReason ?? string.Empty
            };
            m_Pending[line] = entry;
            return Core(entry)
                + " actualCount=" + actualCount
                + " nearVehicle=" + Id(nearVehicle)
                + " nearState=" + nearState
                + " nearEtaFrames=" + Frames(nearEta)
                + " nearReason=" + Safe(nearReason);
        }

        internal string Bind(Entity line, Entity vehicle, uint triggerFrame, uint spawnFrame)
        {
            if (!m_Pending.TryGetValue(line, out Entry entry) || entry.TriggerFrame != triggerFrame)
                return string.Empty;
            m_Pending.Remove(line);
            entry.Vehicle = vehicle;
            entry.SpawnFrame = spawnFrame;
            m_Vehicles[vehicle] = entry;
            return Core(entry)
                + " spawnDelayFrames=" + unchecked(spawnFrame - triggerFrame);
        }

        internal string Arrive(Entity vehicle, uint frame)
        {
            if (!m_Vehicles.TryGetValue(vehicle, out Entry entry))
                return string.Empty;
            if (entry.ArrivalFrame == 0u) entry.ArrivalFrame = frame;
            int assigned = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int target) ? target : -1;
            Entity holder = Holder(entry.Line, vehicle, entry.Slot);
            long delta = (long)entry.ArrivalFrame - entry.ExpectedOriginFrame;
            return Core(entry)
                + " actualOriginFrame=" + entry.ArrivalFrame
                + " arrivalDeltaFrames=" + delta
                + " assignedSlot=" + Slot(assigned)
                + " intendedHolder=" + Id(holder);
        }

        internal string Launch(Entity vehicle, int actualSlot, uint frame)
        {
            if (!m_Vehicles.TryGetValue(vehicle, out Entry entry))
                return string.Empty;
            if (entry.DepartureFrame == 0u) entry.DepartureFrame = frame;
            return Core(entry)
                + " actualSlot=" + Slot(actualSlot)
                + " purposeMatched=" + (actualSlot == entry.Slot ? "1" : "0");
        }

        internal string Describe(Entity vehicle)
        {
            return m_Vehicles.TryGetValue(vehicle, out Entry entry) ? Core(entry) : string.Empty;
        }

        internal string Retire(Entity vehicle, uint frame)
        {
            if (!m_Vehicles.TryGetValue(vehicle, out Entry entry))
                return string.Empty;
            m_Vehicles.Remove(vehicle);
            int assigned = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int target) ? target : -1;
            string phase = entry.DepartureFrame != 0u
                ? "after-service"
                : entry.ArrivalFrame != 0u ? "at-origin-before-departure" : "before-origin";
            return Core(entry)
                + " assignedSlot=" + Slot(assigned)
                + " retirePhase=" + phase
                + " livedFrames=" + unchecked(frame - entry.TriggerFrame)
                + " originWaitFrames=" + (entry.ArrivalFrame != 0u ? unchecked(frame - entry.ArrivalFrame) : 0u)
                + " departed=" + (entry.DepartureFrame != 0u ? "1" : "0");
        }

        internal void Remove(Entity vehicle)
        {
            m_Vehicles.Remove(vehicle);
        }

        internal void Clear()
        {
            m_Pending.Clear();
            m_Vehicles.Clear();
        }

        private Entity Holder(Entity line, Entity vehicle, int slot)
        {
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line)
                || !m_Runtime.EntityManager.HasBuffer<RouteVehicle>(line)) return Entity.Null;
            DynamicBuffer<RouteVehicle> vehicles = m_Runtime.EntityManager.GetBuffer<RouteVehicle>(line, true);
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity other = m_Runtime.m_Resolve.RuntimeVehicle(vehicles[i].m_Vehicle);
                if (other == Entity.Null || other == vehicle || !m_Runtime.EntityManager.Exists(other)) continue;
                if ((m_Runtime.m_VehicleView.TryGetSlot(other, out int current) && current == slot)
                    || (m_Runtime.m_VehicleView.TryGetTarget(other, out int target) && target == slot)) return other;
            }
            return Entity.Null;
        }

        private static string Core(Entry entry)
        {
            return " intent=" + entry.Id
                + " intendedSlot=" + Slot(entry.Slot)
                + " triggerFrame=" + entry.TriggerFrame
                + " expectedOriginFrame=" + entry.ExpectedOriginFrame
                + " spawnLeadSource=" + Safe(entry.LeadSource)
                + " spawnLeadFrames=" + Frames(entry.LeadFrames)
                + " originHoldMinutes=" + entry.OriginHoldMinutes
                + (entry.SpawnFrame != 0u ? " spawnFrame=" + entry.SpawnFrame : string.Empty);
        }

        private static string Id(Entity value) => value == Entity.Null ? "-" : value.Index.ToString();
        private static string Slot(int value) => value < 0 ? "-" : DispatchRuntimeSystem.SlotStr(value);
        private static string Frames(float value) => value == float.MaxValue ? "-" : math.round(value).ToString();
        private static string Safe(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace(' ', '_');
    }
}
