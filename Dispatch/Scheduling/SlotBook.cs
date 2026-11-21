using System;
using System.Collections.Generic;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal readonly struct SlotKey : IEquatable<SlotKey>
    {
        internal readonly DateTime Date;
        internal readonly int Minute;
        internal readonly long AbsMinute;

        internal SlotKey(DateTime date, int minute, int daysPerYear)
        {
            Date = date.Date;
            Minute = minute;
            AbsMinute = ScheduleClock.GameDay(Date, daysPerYear) * 1440L + minute;
        }

        public bool Equals(SlotKey other) => AbsMinute == other.AbsMinute;

        public override bool Equals(object obj) => obj is SlotKey other && Equals(other);

        public override int GetHashCode() => AbsMinute.GetHashCode();

        public static bool operator ==(SlotKey left, SlotKey right) => left.Equals(right);

        public static bool operator !=(SlotKey left, SlotKey right) => !left.Equals(right);
    }

    internal enum SlotState : byte
    {
        Empty,
        Pre,
        Formal,
        Launched
    }

    internal sealed class SlotRow
    {
        internal readonly Entity Line;
        internal readonly SlotKey Slot;
        internal SlotState State;
        internal Entity Holder;
        internal int SpawnOrderId;

        internal SlotRow(Entity line, SlotKey slot)
        {
            Line = line;
            Slot = slot;
            State = SlotState.Empty;
            Holder = Entity.Null;
            SpawnOrderId = 0;
        }
    }

    internal sealed class SlotTable
    {
        /// <summary>
        /// 窗口内的班次行：每行是某一天的一次具体发车，日期 + 班次时刻，按 SlotKey.AbsMinute 升序。
        /// </summary>
        internal readonly List<SlotRow> Rows = new List<SlotRow>();
        /// <summary>
        /// 已应用时刻表的班次时刻：每天几点发车，当天分钟数，不带日期，由 Align 从 LineView.Times 同步。
        /// </summary>
        internal int[] DepartureMinutes = Array.Empty<int>();
        internal int Hold;
        internal int Late;

        /// <summary>
        /// 可接区间起点：车在此时刻之后到达始发站，才能发这一班。
        /// </summary>
        internal double AcceptWindowStart(SlotRow row) => row.Slot.AbsMinute - Hold;

        /// <summary>
        /// 可接区间终点，即迟发上限：车最晚在此时刻到达始发站；过了此时刻这一班算过期。
        /// </summary>
        internal double AcceptWindowEnd(SlotRow row) => row.Slot.AbsMinute + Late;

        internal bool IsInAcceptWindow(SlotRow row, double absMinute) =>
            absMinute >= AcceptWindowStart(row) && absMinute <= AcceptWindowEnd(row);
    }

    internal readonly struct SlotGrant
    {
        internal readonly bool Granted;
        internal readonly SlotKey Slot;

        internal SlotGrant(SlotKey slot)
        {
            Granted = true;
            Slot = slot;
        }
    }

    internal readonly struct LaunchedSlot
    {
        internal readonly Entity Line;
        internal readonly SlotKey Slot;
        internal readonly int EarlyMinutes;

        internal LaunchedSlot(Entity line, SlotKey slot, int earlyMinutes)
        {
            Line = line;
            Slot = slot;
            EarlyMinutes = earlyMinutes;
        }
    }

    internal readonly struct SlotChange
    {
        internal readonly Entity Vehicle;
        internal readonly Entity Line;
        internal readonly bool HasSlot;
        internal readonly SlotKey Slot;

        internal SlotChange(Entity vehicle, Entity line, bool hasSlot, SlotKey slot)
        {
            Vehicle = vehicle;
            Line = line;
            HasSlot = hasSlot;
            Slot = slot;
        }
    }

    internal enum HolderReason : byte
    {
        Arrival, Restore, SpawnOrder, ScanInStation, ScanEnRoute, ArrivalLineOff, ArrivalNoSlot,
        VehicleLeft, LineSuspended, PreUnavailable, PreLate, TimetableChanged, WindowShrunk,
        Expired, LineDropped, ClockChanged, Displaced
    }

    internal readonly struct HolderChange
    {
        internal readonly uint Frame;
        internal readonly Entity Line;
        internal readonly Entity Vehicle;
        internal readonly Entity Related;
        internal readonly HolderReason Reason;
        internal readonly SlotKey FromSlot;
        internal readonly SlotState FromState;
        internal readonly SlotKey ToSlot;
        internal readonly SlotState ToState;

        internal HolderChange(uint frame, Entity line, Entity vehicle, Entity related, HolderReason reason,
            SlotKey fromSlot, SlotState fromState, SlotKey toSlot, SlotState toState)
        {
            Frame = frame;
            Line = line;
            Vehicle = vehicle;
            Related = related;
            Reason = reason;
            FromSlot = fromSlot;
            FromState = fromState;
            ToSlot = toSlot;
            ToState = toState;
        }
    }

    internal readonly struct LaunchedSeed
    {
        internal readonly Entity Line;
        internal readonly Entity Vehicle;
        internal readonly int Minute;
        internal readonly int DateKey;

        internal LaunchedSeed(Entity line, Entity vehicle, int minute, int dateKey)
        {
            Line = line;
            Vehicle = vehicle;
            Minute = minute;
            DateKey = dateKey;
        }
    }

    internal readonly struct MissedSlot
    {
        internal readonly Entity Line;
        internal readonly AppliedMonitorRow MonitorRow;
        internal readonly DateTime Date;
        internal readonly MonitorTripState State;

        internal MissedSlot(Entity line, AppliedMonitorRow monitorRow, DateTime date, MonitorTripState state)
        {
            Line = line;
            MonitorRow = monitorRow;
            Date = date;
            State = state;
        }
    }

    internal sealed class SlotBook
    {
        private readonly FrameEvents m_Events;
        private readonly Dictionary<Entity, SlotTable> m_Tables = new Dictionary<Entity, SlotTable>();
        private readonly Dictionary<Entity, SlotRow> m_Held = new Dictionary<Entity, SlotRow>();
        private readonly Dictionary<Entity, SlotRow> m_Trips = new Dictionary<Entity, SlotRow>();
        private readonly List<Entity> m_TripScratch = new List<Entity>();
        private readonly List<HolderChange> m_HolderChanges = new List<HolderChange>();

        internal SlotBook(FrameEvents events)
        {
            m_Events = events;
        }

        internal IEnumerable<KeyValuePair<Entity, SlotTable>> Tables => m_Tables;

        internal bool TryGetHeld(Entity vehicle, out SlotRow row) => m_Held.TryGetValue(vehicle, out row);

        internal bool TryGetTrip(Entity vehicle, out SlotRow row) => m_Trips.TryGetValue(vehicle, out row);

        internal bool HasFormal(Entity vehicle) =>
            m_Held.TryGetValue(vehicle, out SlotRow row) && row.State == SlotState.Formal;

        internal bool TryGetTable(Entity line, out SlotTable table) => m_Tables.TryGetValue(line, out table);

        internal SlotTable GetOrAddTable(Entity line)
        {
            if (!m_Tables.TryGetValue(line, out SlotTable table))
            {
                table = new SlotTable();
                m_Tables.Add(line, table);
            }
            return table;
        }

        internal SlotRow GetOrAddRow(Entity line, SlotKey slot)
        {
            SlotTable table = GetOrAddTable(line);
            int index = Find(table, slot);
            if (index >= 0)
                return table.Rows[index];
            SlotRow row = new SlotRow(line, slot);
            table.Rows.Insert(~index, row);
            return row;
        }

        internal void SetHolder(SlotRow row, Entity vehicle, bool formal, uint frame, HolderReason reason)
        {
            int previous = m_Held.TryGetValue(vehicle, out SlotRow held) ? held.Slot.Minute : -1;
            SlotKey previousSlot = held != null ? held.Slot : default;
            SlotState previousState = held != null ? held.State : SlotState.Empty;
            if (held != null && held != row)
                Unbind(vehicle);
            if ((row.State == SlotState.Pre || row.State == SlotState.Formal) && row.Holder != vehicle)
                ReleaseHeld(row.Holder, frame, HolderReason.Displaced, vehicle);

            row.State = formal ? SlotState.Formal : SlotState.Pre;
            row.Holder = vehicle;
            m_Held[vehicle] = row;
            if (previous != row.Slot.Minute)
                m_Events.AppendDispatch(vehicle, frame, DispatchFactKind.Target, default, default,
                    row.Line, previous, row.Slot.Minute);
            if (RtLog.VerboseEnabled && (previous != row.Slot.Minute || previousState != row.State))
                m_HolderChanges.Add(new HolderChange(frame, row.Line, vehicle, default, reason,
                    previousSlot, previousState, row.Slot, row.State));
        }

        internal void ReleaseHeld(Entity vehicle, uint frame, HolderReason reason, Entity related = default)
        {
            if (RtLog.VerboseEnabled && m_Held.TryGetValue(vehicle, out SlotRow held))
                m_HolderChanges.Add(new HolderChange(frame, held.Line, vehicle, related, reason,
                    held.Slot, held.State, default, SlotState.Empty));
            DropHeld(vehicle, frame);
        }

        internal void TakeHolderChanges(List<HolderChange> output)
        {
            output.AddRange(m_HolderChanges);
            m_HolderChanges.Clear();
        }

        private void DropHeld(Entity vehicle, uint frame)
        {
            if (!m_Held.TryGetValue(vehicle, out SlotRow held))
                return;
            Unbind(vehicle);
            m_Events.AppendDispatch(vehicle, frame, DispatchFactKind.Target, default, default,
                held.Line, held.Slot.Minute, -1);
        }

        internal void SetLaunched(SlotRow row, Entity vehicle, uint frame, bool bindTrip = true)
        {
            if (row.State == SlotState.Pre || row.State == SlotState.Formal)
                DropHeld(row.Holder, frame);
            row.State = SlotState.Launched;
            row.Holder = vehicle;
            row.SpawnOrderId = 0;
            if (!bindTrip)
                return;
            int previous = m_Trips.TryGetValue(vehicle, out SlotRow trip) ? trip.Slot.Minute : -1;
            m_Trips[vehicle] = row;
            if (previous != row.Slot.Minute)
                m_Events.AppendDispatch(vehicle, frame, DispatchFactKind.Slot, default, default,
                    row.Line, previous, row.Slot.Minute);
        }

        internal void FinishTrip(Entity vehicle, uint frame)
        {
            if (!m_Trips.TryGetValue(vehicle, out SlotRow trip))
                return;
            m_Trips.Remove(vehicle);
            m_Events.AppendDispatch(vehicle, frame, DispatchFactKind.Slot, default, default,
                trip.Line, trip.Slot.Minute, -1);
        }

        internal void RemoveRow(SlotRow row, uint frame, HolderReason reason)
        {
            SlotTable table = m_Tables[row.Line];
            int index = Find(table, row.Slot);
            if (row.State == SlotState.Pre || row.State == SlotState.Formal)
                ReleaseHeld(row.Holder, frame, reason);
            table.Rows.RemoveAt(index);
        }

        internal void RemoveTable(Entity line, uint frame, HolderReason reason)
        {
            if (!m_Tables.TryGetValue(line, out SlotTable table))
                return;
            for (int i = table.Rows.Count - 1; i >= 0; i--)
                RemoveRow(table.Rows[i], frame, reason);
            m_Tables.Remove(line);
            m_TripScratch.Clear();
            foreach (KeyValuePair<Entity, SlotRow> trip in m_Trips)
                if (trip.Value.Line == line)
                    m_TripScratch.Add(trip.Key);
            for (int i = 0; i < m_TripScratch.Count; i++)
                FinishTrip(m_TripScratch[i], frame);
            m_TripScratch.Clear();
        }

        internal void Clear()
        {
            m_Tables.Clear();
            m_Held.Clear();
            m_Trips.Clear();
            m_TripScratch.Clear();
            m_HolderChanges.Clear();
        }

        private static int Find(SlotTable table, SlotKey slot)
        {
            int low = 0;
            int high = table.Rows.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                long current = table.Rows[middle].Slot.AbsMinute;
                if (current == slot.AbsMinute)
                    return middle;
                if (current < slot.AbsMinute)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return ~low;
        }

        private void Unbind(Entity vehicle)
        {
            SlotRow row = m_Held[vehicle];
            m_Held.Remove(vehicle);
            row.State = SlotState.Empty;
            row.Holder = Entity.Null;
        }
    }
}
