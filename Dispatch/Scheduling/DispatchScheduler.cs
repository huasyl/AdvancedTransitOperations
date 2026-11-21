using System;
using System.Collections.Generic;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Entities;

namespace RapidTransitMod
{
    internal readonly struct SpawnOrderNotice
    {
        internal readonly Entity Line;
        internal readonly int OrderMinute;
        internal readonly int SlotMinute;

        internal SpawnOrderNotice(Entity line, int orderMinute, int slotMinute)
        {
            Line = line;
            OrderMinute = orderMinute;
            SlotMinute = slotMinute;
        }
    }

    internal sealed class DispatchScheduler
    {
        private readonly struct ArrivalEstimate
        {
            internal readonly ArrivalKind Kind;
            internal readonly uint Frame;
            internal readonly ArrivalBasis Basis;

            internal ArrivalEstimate(ArrivalKind kind, uint frame, ArrivalBasis basis)
            {
                Kind = kind;
                Frame = frame;
                Basis = basis;
            }
        }

        private readonly struct Candidate
        {
            internal readonly Entity Vehicle;
            internal readonly uint ArrivalFrame;

            internal Candidate(Entity vehicle, uint arrivalFrame)
            {
                Vehicle = vehicle;
                ArrivalFrame = arrivalFrame;
            }
        }

        private readonly SlotBook m_Book;
        private readonly DirtyLineSet m_DirtyLines;
        private readonly LineView m_Lines;
        private readonly LineServiceState m_Service;
        private readonly LineProfile m_Profile;
        private readonly VehicleView m_Vehicles;
        private readonly LineTimes m_Times;
        private readonly LineRange m_Range;
        private readonly LineSpawnControl m_SpawnOrders;
        private readonly ServiceCalendar m_Calendar;
        private readonly Dictionary<Entity, SlotChange> m_FormalBeforeChange = new Dictionary<Entity, SlotChange>();
        private readonly Dictionary<Entity, ArrivalEstimate> m_ArrivalCache = new Dictionary<Entity, ArrivalEstimate>();
        private readonly List<Candidate> m_InStationCandidates = new List<Candidate>();
        private readonly List<Candidate> m_EnRouteCandidates = new List<Candidate>();
        private readonly List<MissedSlot> m_Missed = new List<MissedSlot>();
        private readonly List<HolderChange> m_HolderLog = new List<HolderChange>();
        private readonly List<SpawnOrderNotice> m_OpenedSpawnOrders = new List<SpawnOrderNotice>();
        private readonly HashSet<Entity> m_SweepLines = new HashSet<Entity>();
        private readonly List<Entity> m_DeletedLines = new List<Entity>();
        private readonly HashSet<int> m_DepartureMinuteSet = new HashSet<int>();
        private uint m_Frame;
        private double m_ObservedSince;
        private bool m_ClearRowsPending;

        internal DispatchScheduler(SlotBook book, DirtyLineSet dirtyLines, LineView lines,
            LineServiceState service, LineProfile profile, VehicleView vehicles, LineTimes times,
            LineRange range, LineSpawnControl spawnOrders, ServiceCalendar calendar)
        {
            m_Book = book;
            m_DirtyLines = dirtyLines;
            m_Lines = lines;
            m_Service = service;
            m_Profile = profile;
            m_Vehicles = vehicles;
            m_Times = times;
            m_Range = range;
            m_SpawnOrders = spawnOrders;
            m_Calendar = calendar;
        }

        internal IReadOnlyList<MissedSlot> Missed => m_Missed;
        internal IReadOnlyList<SpawnOrderNotice> OpenedSpawnOrders => m_OpenedSpawnOrders;

        internal void BeginFrame(uint nowFrame)
        {
            m_Frame = nowFrame;
            m_FormalBeforeChange.Clear();
            m_Missed.Clear();
            m_OpenedSpawnOrders.Clear();
            if (m_ClearRowsPending)
                ClearAllRows();
        }

        internal void ResetMonitorClock(ClockSnapshot clock) => m_ObservedSince = NowAbsMinute(clock);

        internal void ClearRowsAfterClockChange()
        {
            m_ClearRowsPending = true;
            m_DirtyLines.MarkPendingAllDirty();
        }

        internal void ResetCity()
        {
            m_Book.Clear();
            m_FormalBeforeChange.Clear();
            m_ArrivalCache.Clear();
            m_InStationCandidates.Clear();
            m_EnRouteCandidates.Clear();
            m_Missed.Clear();
            m_HolderLog.Clear();
            m_OpenedSpawnOrders.Clear();
            m_SweepLines.Clear();
            m_DeletedLines.Clear();
            m_DepartureMinuteSet.Clear();
            m_ObservedSince = 0;
        }

        internal SlotGrant RequestAtArrival(
            Entity vehicle, Entity line, uint arrivalFrame, uint nowFrame, ClockSnapshot clock)
        {
            m_Frame = nowFrame;
            m_Book.FinishTrip(vehicle, nowFrame);
            bool heldBefore = m_Book.TryGetHeld(vehicle, out SlotRow previousRow);
            if (!m_Lines.ManagedRuntime(line, true) || !m_Service.IsOperational(line))
            {
                if (heldBefore)
                {
                    m_Book.ReleaseHeld(vehicle, nowFrame, HolderReason.ArrivalLineOff);
                    m_DirtyLines.MarkDirty(line);
                }
                LogHolderChanges(clock, Entity.Null);
                return default;
            }

            SlotTable table = m_Book.GetOrAddTable(line);
            Align(line, table);
            Fill(line, table, clock, m_Times.Lead(line));
            double arrival = FrameToAbsMinute(clock, nowFrame, arrivalFrame);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                SlotRow row = table.Rows[i];
                if ((row.State != SlotState.Empty && row.State != SlotState.Pre)
                    || !table.IsInAcceptWindow(row, arrival)
                    || Suspended(line, row.Slot))
                    continue;
                bool displaced = row.State == SlotState.Pre && row.Holder != vehicle;
                CancelSpawnOrder(row);
                m_Book.SetHolder(row, vehicle, true, nowFrame, HolderReason.Arrival);
                if (displaced || (heldBefore && previousRow != row))
                    m_DirtyLines.MarkDirty(line);
                LogHolderChanges(clock, Entity.Null);
                return new SlotGrant(row.Slot);
            }
            if (heldBefore)
            {
                m_Book.ReleaseHeld(vehicle, nowFrame, HolderReason.ArrivalNoSlot);
                m_DirtyLines.MarkDirty(line);
            }
            LogHolderChanges(clock, Entity.Null);
            return default;
        }

        internal bool MarkLaunched(
            Entity vehicle, uint movingFrame, uint nowFrame, ClockSnapshot clock, out LaunchedSlot result)
        {
            m_Frame = nowFrame;
            result = default;
            if (!m_Book.TryGetHeld(vehicle, out SlotRow row) || row.State != SlotState.Formal)
                return false;
            int early = (int)Math.Max(0d, Math.Floor(row.Slot.AbsMinute - FrameToAbsMinute(clock, nowFrame, movingFrame)));
            m_Book.SetLaunched(row, vehicle, nowFrame);
            result = new LaunchedSlot(row.Line, row.Slot, early);
            return true;
        }

        internal void Release(Entity vehicle)
        {
            Entity line = Entity.Null;
            if (m_Book.TryGetHeld(vehicle, out SlotRow held))
                line = held.Line;
            else if (m_Book.TryGetTrip(vehicle, out SlotRow trip))
                line = trip.Line;
            else if (m_Vehicles.TryGetLine(vehicle, out Entity registeredLine))
                line = registeredLine;
            m_Book.ReleaseHeld(vehicle, m_Frame, HolderReason.VehicleLeft);
            m_Book.FinishTrip(vehicle, m_Frame);
            m_FormalBeforeChange.Remove(vehicle);
            if (line != Entity.Null)
                m_DirtyLines.MarkDirty(line);
        }

        internal void LineChanged(Entity line) => m_DirtyLines.MarkDirty(line);

        internal bool RestoreFormal(Entity vehicle, Entity line, int minute, ClockSnapshot clock)
        {
            if (minute < 0 || minute >= 1440 || !m_Lines.ManagedRuntime(line, true))
                return false;
            SlotKey slot = ScheduleClock.Occurrence(clock, minute, 1439 - m_Lines.Hold(line));
            SlotRow row = m_Book.GetOrAddRow(line, slot);
            if (row.State != SlotState.Empty && (row.State != SlotState.Formal || row.Holder != vehicle))
            {
                Mod.log.Info("[RestoreSlotConflict] line=" + line + " vehicle=" + vehicle
                    + " date=" + slot.Date.ToString("yyyy-MM-dd") + " slotMinute=" + minute);
                return false;
            }
            RecordFormalBeforeChange(vehicle, line);
            m_Book.SetHolder(row, vehicle, true, m_Frame, HolderReason.Restore);
            LogHolderChanges(clock, Entity.Null);
            return true;
        }

        internal void BackfillLaunched(IReadOnlyList<LaunchedSeed> seeds, ClockSnapshot clock)
        {
            if (seeds == null)
                return;
            double now = NowAbsMinute(clock);
            for (int i = 0; i < seeds.Count; i++)
            {
                LaunchedSeed seed = seeds[i];
                DateTime date = new DateTime(seed.DateKey / 10000, seed.DateKey / 100 % 100, seed.DateKey % 100);
                SlotKey slot = new SlotKey(date, seed.Minute, clock.DaysPerYear);
                if (now > slot.AbsMinute + m_Lines.Late(seed.Line))
                    continue;
                SlotRow row = m_Book.GetOrAddRow(seed.Line, slot);
                if (row.State == SlotState.Launched)
                    continue;
                if (row.State == SlotState.Formal)
                {
                    RecordFormalBeforeChange(row.Holder, seed.Line);
                    Mod.log.Info("[RestoreSlotConflict] line=" + seed.Line + " vehicle=" + row.Holder
                        + " launchedVehicle=" + seed.Vehicle + " date=" + date.ToString("yyyy-MM-dd")
                        + " slotMinute=" + seed.Minute);
                }
                CancelSpawnOrder(row);
                m_Book.SetLaunched(row, seed.Vehicle, m_Frame, bindTrip: false);
            }
            LogHolderChanges(clock, Entity.Null);
        }

        internal void TakeChanges(List<SlotChange> output)
        {
            output.Clear();
            foreach (KeyValuePair<Entity, SlotChange> entry in m_FormalBeforeChange)
            {
                SlotChange before = entry.Value;
                bool hasSlot = m_Book.TryGetHeld(entry.Key, out SlotRow held) && held.State == SlotState.Formal;
                if (before.HasSlot == hasSlot && (!hasSlot || (before.Line == held.Line && before.Slot == held.Slot)))
                    continue;
                output.Add(new SlotChange(entry.Key, hasSlot ? held.Line : before.Line,
                    hasSlot, hasSlot ? held.Slot : default));
            }
            output.Sort(CompareChange);
            m_FormalBeforeChange.Clear();
        }

        internal void Scan(ClockSnapshot clock, uint nowFrame, IReadOnlyList<Entity> lines, bool fullMinuteSweep)
        {
            m_Frame = nowFrame;
            if (lines == null)
                return;
            if (fullMinuteSweep)
            {
                m_SweepLines.Clear();
                for (int i = 0; i < lines.Count; i++)
                    m_SweepLines.Add(lines[i]);
                m_DeletedLines.Clear();
                foreach (KeyValuePair<Entity, SlotTable> entry in m_Book.Tables)
                    if (!m_SweepLines.Contains(entry.Key))
                        m_DeletedLines.Add(entry.Key);
                for (int i = 0; i < m_DeletedLines.Count; i++)
                    DropLine(m_DeletedLines[i]);
                LogHolderChanges(clock, Entity.Null);
            }
            for (int i = 0; i < lines.Count; i++)
            {
                Entity line = lines[i];
                if (!m_Lines.ManagedRuntime(line, true))
                {
                    DropLine(line);
                    LogHolderChanges(clock, Entity.Null);
                    continue;
                }
                if (!m_Profile.Stable(line))
                    continue;
                bool operational = m_Service.IsOperational(line);
                float lead = m_Times.Lead(line, out LeadSource source);
                SlotTable table = m_Book.GetOrAddTable(line);
                Organize(line, table, clock, lead, operational);
                if (operational)
                {
                    CollectCandidates(line, table, clock);
                    Pair(line, table, clock);
                    OpenSpawnOrders(line, table, clock, lead, source);
                }
                LogHolderChanges(clock, operational ? line : Entity.Null);
            }
        }

        // 第一步：对齐时刻表、补齐窗口、结算过线班次并收掉已领取的派车记录。
        private void Organize(Entity line, SlotTable table, ClockSnapshot clock, float lead, bool operational)
        {
            Align(line, table);
            Fill(line, table, clock, lead);
            if (!operational)
            {
                m_SpawnOrders.CancelLine(line);
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    SlotRow row = table.Rows[i];
                    if (row.State == SlotState.Formal)
                        RecordFormalBeforeChange(row.Holder, line);
                    if (row.State == SlotState.Pre || row.State == SlotState.Formal)
                        m_Book.ReleaseHeld(row.Holder, m_Frame, HolderReason.LineSuspended);
                    row.SpawnOrderId = 0;
                }
            }
            double now = NowAbsMinute(clock);
            double windowEnd = now + table.Hold + clock.ToMinutes(lead);
            for (int i = 0; i < table.Rows.Count;)
            {
                SlotRow row = table.Rows[i];
                // 超出窗口右端（派车提前量或扣车时间变短后）的空班、预分配：撤单删除，进窗口后再重建
                if (row.State != SlotState.Formal && row.State != SlotState.Launched && row.Slot.AbsMinute > windowEnd)
                {
                    CancelSpawnOrder(row);
                    m_Book.RemoveRow(row, m_Frame, HolderReason.WindowShrunk);
                    continue;
                }
                // 未过迟发上限的行都保留；正式班车已在站上，过了上限也保留到发车
                if (now <= table.AcceptWindowEnd(row) || row.State == SlotState.Formal)
                {
                    i++;
                    continue;
                }
                // 剩下已过期的空班、预分配、已发车：前两种撤单并记漏班，三种都删除
                if (row.State == SlotState.Empty || row.State == SlotState.Pre)
                {
                    CancelSpawnOrder(row);
                    bool suspended = Suspended(line, row.Slot);
                    if ((suspended || row.Slot.AbsMinute >= m_ObservedSince)
                        && m_Lines.TryMonitorRow(line, row.Slot.Minute, out AppliedMonitorRow monitorRow))
                    {
                        MonitorTripState state = suspended ? MonitorTripState.Suspended : MonitorTripState.Missed;
                        m_Missed.Add(new MissedSlot(line, monitorRow, row.Slot.Date, state));
                        Mod.log.Info((suspended ? "[SuspendedSlot]" : "[MissedSlot]")
                            + " line=" + line + " date=" + row.Slot.Date.ToString("yyyy-MM-dd")
                            + " slotMinute=" + row.Slot.Minute);
                    }
                }
                m_Book.RemoveRow(row, m_Frame, HolderReason.Expired);
            }
            if (!operational)
                return;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                SlotRow row = table.Rows[i];
                if (row.SpawnOrderId == 0)
                    continue;
                SpawnOrderState orderState = m_SpawnOrders.Query(row.SpawnOrderId, out Entity vehicle);
                if (orderState == SpawnOrderState.Unclaimed)
                    continue;
                if (orderState == SpawnOrderState.Claimed)
                {
                    if (row.State == SlotState.Empty
                        && m_Vehicles.TryGetLine(vehicle, out Entity vehicleLine) && vehicleLine == line
                        && m_Vehicles.TryGetState(vehicle, out VehicleState state) && state != VehicleState.Retiring
                        && !m_Book.TryGetHeld(vehicle, out _))
                        m_Book.SetHolder(row, vehicle, false, m_Frame, HolderReason.SpawnOrder);
                    m_SpawnOrders.Cancel(row.SpawnOrderId);
                }
                row.SpawnOrderId = 0;
            }
        }

        // 第二步：按线路桶取车，每车只查一次到站时间，失效预分配在本轮重新入池。
        private void CollectCandidates(Entity line, SlotTable table, ClockSnapshot clock)
        {
            m_ArrivalCache.Clear();
            m_InStationCandidates.Clear();
            m_EnRouteCandidates.Clear();
            foreach (Entity vehicle in m_Vehicles.OnLine(line))
            {
                if (!m_Vehicles.TryGetState(vehicle, out VehicleState state)
                    || state == VehicleState.Retiring || m_Range.NeedsDepot(vehicle, line))
                    continue;
                ArrivalEstimate arrival = ReadArrival(vehicle, line);
                if (m_Book.TryGetHeld(vehicle, out _))
                    continue;
                AddCandidate(vehicle, state, arrival);
            }
            for (int i = 0; i < table.Rows.Count; i++)
            {
                SlotRow row = table.Rows[i];
                if (row.State != SlotState.Pre)
                    continue;
                Entity holder = row.Holder;
                bool holderUsable = m_ArrivalCache.TryGetValue(holder, out ArrivalEstimate arrival);
                // 车仍可用且赶得上（已在站、预计未知或预计不晚于迟发上限）：保留预分配，不进候选池
                if (holderUsable && (arrival.Kind != ArrivalKind.Estimated
                    || FrameToAbsMinute(clock, m_Frame, arrival.Frame) <= table.AcceptWindowEnd(row)))
                    continue;
                m_Book.ReleaseHeld(holder, m_Frame, holderUsable ? HolderReason.PreLate : HolderReason.PreUnavailable);
                if (holderUsable && m_Vehicles.TryGetState(holder, out VehicleState state))
                    AddCandidate(holder, state, arrival);
            }
            m_InStationCandidates.Sort(CompareCandidate);
            m_EnRouteCandidates.Sort(CompareCandidate);
        }

        // 第三步：空班先配在站车，再配可接区间内最早到站的在途车，每车只用一次。
        private void Pair(Entity line, SlotTable table, ClockSnapshot clock)
        {
            int nextInStation = 0;
            int nextEnRoute = 0;
            double now = NowAbsMinute(clock);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                SlotRow row = table.Rows[i];
                if (row.State != SlotState.Empty || Suspended(line, row.Slot))
                    continue;
                if (nextInStation < m_InStationCandidates.Count && table.IsInAcceptWindow(row, now))
                {
                    Entity vehicle = m_InStationCandidates[nextInStation++].Vehicle;
                    RecordFormalBeforeChange(vehicle, line);
                    CancelSpawnOrder(row);
                    m_Book.SetHolder(row, vehicle, true, m_Frame, HolderReason.ScanInStation);
                    continue;
                }
                while (nextEnRoute < m_EnRouteCandidates.Count
                    && FrameToAbsMinute(clock, m_Frame, m_EnRouteCandidates[nextEnRoute].ArrivalFrame)
                        < table.AcceptWindowStart(row))
                    nextEnRoute++;
                if (nextEnRoute >= m_EnRouteCandidates.Count
                    || FrameToAbsMinute(clock, m_Frame, m_EnRouteCandidates[nextEnRoute].ArrivalFrame)
                        > table.AcceptWindowEnd(row))
                    continue;
                CancelSpawnOrder(row);
                m_Book.SetHolder(row, m_EnRouteCandidates[nextEnRoute++].Vehicle, false, m_Frame, HolderReason.ScanEnRoute);
            }
        }

        // 第四步：上一班离站只往后推，按站台可用区间中点决定派车时机。
        private void OpenSpawnOrders(Entity line, SlotTable table, ClockSnapshot clock, float lead, LeadSource source)
        {
            if (source == LeadSource.Pending)
            {
                if (RtLog.VerboseEnabled && m_Times.TryMarkSpawnDeferred(line))
                    Mod.log.Info("[SpawnOrderDeferred] line=" + line.Index + " reason=theory-pending");
                return;
            }

            double previousDeparture = double.NegativeInfinity;
            double now = NowAbsMinute(clock);
            double leadMinutes = clock.ToMinutes(lead);
            int originDwell = m_Lines.OriginMinDwell(line);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                SlotRow row = table.Rows[i];
                if (row.State == SlotState.Launched)
                    continue;
                if (row.State == SlotState.Formal || row.State == SlotState.Pre)
                {
                    ArrivalEstimate arrival = ReadArrival(row.Holder, line);
                    double departure = row.Slot.AbsMinute;
                    if (arrival.Kind != ArrivalKind.Unknown)
                        departure = Math.Max(departure, FrameToAbsMinute(clock, m_Frame, arrival.Frame)
                            + originDwell);
                    previousDeparture = Math.Max(previousDeparture, departure);
                    continue;
                }
                if (row.SpawnOrderId != 0
                    && m_SpawnOrders.Query(row.SpawnOrderId, out _, out uint requestFrame) == SpawnOrderState.Unclaimed)
                {
                    previousDeparture = Math.Max(previousDeparture,
                        Math.Max(row.Slot.AbsMinute, FrameToAbsMinute(clock, m_Frame, requestFrame) + leadMinutes + originDwell));
                    continue;
                }
                if (Suspended(line, row.Slot))
                    continue;
                double earliestArrival = Math.Max(table.AcceptWindowStart(row), previousDeparture);
                double latestArrival = row.Slot.AbsMinute - originDwell;
                double targetArrival = Math.Max(earliestArrival, (earliestArrival + latestArrival) / 2d);
                double spawnAt = targetArrival - leadMinutes;
                double newArrival = Math.Max(targetArrival, now + leadMinutes);
                if (newArrival > table.AcceptWindowEnd(row))
                    continue;
                if (now < spawnAt)
                    break;
                int spawnOrderId = m_SpawnOrders.AddForSlot(line, row.Slot, m_Frame);
                row.SpawnOrderId = spawnOrderId;
                m_OpenedSpawnOrders.Add(new SpawnOrderNotice(line, clock.NowMinute, row.Slot.Minute));
                Mod.log.Info("[SpawnOrder] line=" + line + " date=" + row.Slot.Date.ToString("yyyy-MM-dd")
                    + " slotMinute=" + row.Slot.Minute + " order=" + spawnOrderId + " requestFrame=" + m_Frame
                    + " leadSource=" + source + " leadFrames=" + lead.ToString("F1"));
                previousDeparture = Math.Max(previousDeparture, Math.Max(row.Slot.AbsMinute, newArrival + originDwell));
            }
        }

        private void Align(Entity line, SlotTable table)
        {
            int[] times = m_Lines.Times(line) ?? Array.Empty<int>();
            int hold = m_Lines.Hold(line);
            int late = m_Lines.Late(line);
            bool same = table.DepartureMinutes.Length == times.Length && table.Hold == hold && table.Late == late;
            for (int i = 0; same && i < times.Length; i++)
                same = table.DepartureMinutes[i] == times[i];
            if (same)
                return;
            m_DepartureMinuteSet.Clear();
            for (int i = 0; i < times.Length; i++)
                m_DepartureMinuteSet.Add(times[i]);
            for (int i = table.Rows.Count - 1; i >= 0; i--)
            {
                SlotRow row = table.Rows[i];
                if (m_DepartureMinuteSet.Contains(row.Slot.Minute))
                    continue;
                if (row.State == SlotState.Formal)
                    RecordFormalBeforeChange(row.Holder, line);
                CancelSpawnOrder(row);
                m_Book.RemoveRow(row, m_Frame, HolderReason.TimetableChanged);
            }
            table.DepartureMinutes = (int[])times.Clone();
            table.Hold = hold;
            table.Late = late;
        }

        private void Fill(Entity line, SlotTable table, ClockSnapshot clock, float lead)
        {
            double now = NowAbsMinute(clock);
            double windowStart = now - table.Late;
            double windowEnd = now + table.Hold + clock.ToMinutes(lead);
            for (int day = -1; day <= 1; day++)
            {
                DateTime date = ScheduleClock.OffsetDate(clock.NowDate.Date, day, clock.DaysPerYear);
                for (int i = 0; i < table.DepartureMinutes.Length; i++)
                {
                    SlotKey slot = new SlotKey(date, table.DepartureMinutes[i], clock.DaysPerYear);
                    if (slot.AbsMinute >= windowStart && slot.AbsMinute <= windowEnd)
                        m_Book.GetOrAddRow(line, slot);
                }
            }
        }

        private ArrivalEstimate ReadArrival(Entity vehicle, Entity line)
        {
            if (!m_ArrivalCache.TryGetValue(vehicle, out ArrivalEstimate arrival))
            {
                ArrivalKind kind = m_Times.Arrival(vehicle, line, out uint frame, out ArrivalBasis basis);
                arrival = new ArrivalEstimate(kind, frame, basis);
                m_ArrivalCache.Add(vehicle, arrival);
            }
            return arrival;
        }

        private void LogHolderChanges(ClockSnapshot clock, Entity cacheLine)
        {
            if (!RtLog.VerboseEnabled)
                return;
            m_HolderLog.Clear();
            m_Book.TakeHolderChanges(m_HolderLog);
            for (int i = 0; i < m_HolderLog.Count; i++)
                LogHolderChange(m_HolderLog[i], clock, cacheLine);
            m_HolderLog.Clear();
        }

        private void LogHolderChange(HolderChange change, ClockSnapshot clock, Entity cacheLine)
        {
            bool hasFrom = change.FromState != SlotState.Empty;
            bool hasTo = change.ToState != SlotState.Empty;
            SlotKey slot = hasTo ? change.ToSlot : change.FromSlot;
            string text = "[SlotHolder] line=" + change.Line.Index + " vehicle=" + change.Vehicle.Index
                + " slot=" + slot.Date.ToString("yyyy-MM-dd") + " " + MinuteText(slot.Minute)
                + " from=" + (hasFrom ? MinuteText(change.FromSlot.Minute) : "-")
                + " to=" + (hasTo ? MinuteText(change.ToSlot.Minute) : "-")
                + " state=" + (hasTo ? change.ToState.ToString() : "-")
                + " fromState=" + (hasFrom ? change.FromState.ToString() : "-")
                + " reason=" + ReasonText(change.Reason);
            if (change.Reason == HolderReason.Displaced)
                text += " by=" + change.Related.Index;
            if (m_Book.TryGetTable(change.Line, out SlotTable table))
            {
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    SlotRow row = table.Rows[i];
                    if (row.Slot != slot)
                        continue;
                    text += " window=" + AbsMinuteText(table.AcceptWindowStart(row))
                        + "~" + AbsMinuteText(table.AcceptWindowEnd(row));
                    break;
                }
            }
            if (change.Reason == HolderReason.Arrival || change.Reason == HolderReason.ScanInStation
                || change.Reason == HolderReason.ScanEnRoute || change.Reason == HolderReason.SpawnOrder
                || change.Reason == HolderReason.PreLate || change.Reason == HolderReason.PreUnavailable)
            {
                ArrivalEstimate estimate;
                if (change.Line == cacheLine)
                    estimate = ReadArrival(change.Vehicle, change.Line);
                else
                {
                    ArrivalKind kind = m_Times.Arrival(change.Vehicle, change.Line, out uint frame, out ArrivalBasis basis);
                    estimate = new ArrivalEstimate(kind, frame, basis);
                }
                ArrivalBasis used = estimate.Basis;
                text += " eta=" + (estimate.Kind == ArrivalKind.Unknown
                        ? "unknown" : SecondText(FrameToAbsMinute(clock, m_Frame, estimate.Frame)))
                    + " etaBy=" + used.Method + " src=" + used.Source
                    + " next=" + (used.Waypoint >= 0 ? used.Waypoint.ToString() : "-")
                    + " pos=" + (used.Position >= 0f ? used.Position.ToString("F2") : "-")
                    + " scale=" + used.Scale.ToString("F2");
            }
            if (change.Frame != m_Frame)
                text += " frame=" + change.Frame;
            Mod.log.Info(text);
        }

        private static string ReasonText(HolderReason reason)
        {
            switch (reason)
            {
                case HolderReason.Arrival: return "arrival";
                case HolderReason.Restore: return "restore";
                case HolderReason.SpawnOrder: return "spawn-order";
                case HolderReason.ScanInStation: return "scan-in-station";
                case HolderReason.ScanEnRoute: return "scan-en-route";
                case HolderReason.ArrivalLineOff: return "arrival-line-off";
                case HolderReason.ArrivalNoSlot: return "arrival-no-slot";
                case HolderReason.VehicleLeft: return "vehicle-left";
                case HolderReason.LineSuspended: return "line-suspended";
                case HolderReason.PreUnavailable: return "pre-unavailable";
                case HolderReason.PreLate: return "pre-late";
                case HolderReason.TimetableChanged: return "timetable-changed";
                case HolderReason.WindowShrunk: return "window-shrunk";
                case HolderReason.Expired: return "expired";
                case HolderReason.LineDropped: return "line-dropped";
                case HolderReason.ClockChanged: return "clock-changed";
                default: return "displaced";
            }
        }

        private static string MinuteText(int minute) =>
            (minute / 60).ToString("00") + ":" + (minute % 60).ToString("00");

        private static string AbsMinuteText(double absMinute) =>
            MinuteText((int)(((long)Math.Floor(absMinute) % 1440L + 1440L) % 1440L));

        private static string SecondText(double absMinute)
        {
            long seconds = (((long)Math.Floor(absMinute * 60d) % 86400L) + 86400L) % 86400L;
            return (seconds / 3600).ToString("00") + ":" + (seconds / 60 % 60).ToString("00")
                + ":" + (seconds % 60).ToString("00");
        }

        private void AddCandidate(Entity vehicle, VehicleState state, ArrivalEstimate arrival)
        {
            if (arrival.Kind == ArrivalKind.InStation)
            {
                if (state == VehicleState.Idle || state == VehicleState.Holding)
                    m_InStationCandidates.Add(new Candidate(vehicle, arrival.Frame));
            }
            else if (arrival.Kind == ArrivalKind.Estimated)
                m_EnRouteCandidates.Add(new Candidate(vehicle, arrival.Frame));
        }

        private void RecordFormalBeforeChange(Entity vehicle, Entity line)
        {
            if (m_FormalBeforeChange.ContainsKey(vehicle))
                return;
            bool formal = m_Book.TryGetHeld(vehicle, out SlotRow held) && held.State == SlotState.Formal;
            m_FormalBeforeChange.Add(vehicle, new SlotChange(vehicle, formal ? held.Line : line,
                formal, formal ? held.Slot : default));
        }

        private void CancelSpawnOrder(SlotRow row)
        {
            if (row.SpawnOrderId == 0)
                return;
            m_SpawnOrders.Cancel(row.SpawnOrderId);
            row.SpawnOrderId = 0;
        }

        private void DropLine(Entity line)
        {
            if (m_Book.TryGetTable(line, out SlotTable table))
            {
                for (int i = 0; i < table.Rows.Count; i++)
                    if (table.Rows[i].State == SlotState.Formal)
                        RecordFormalBeforeChange(table.Rows[i].Holder, line);
                m_Book.RemoveTable(line, m_Frame, HolderReason.LineDropped);
            }
            m_SpawnOrders.CancelLine(line);
        }

        private void ClearAllRows()
        {
            m_ClearRowsPending = false;
            foreach (KeyValuePair<Entity, SlotTable> pair in m_Book.Tables)
            {
                Entity line = pair.Key;
                SlotTable table = pair.Value;
                m_SpawnOrders.CancelLine(line);
                for (int i = table.Rows.Count - 1; i >= 0; i--)
                {
                    SlotRow row = table.Rows[i];
                    if (row.State == SlotState.Formal)
                        RecordFormalBeforeChange(row.Holder, line);
                    m_Book.RemoveRow(row, m_Frame, HolderReason.ClockChanged);
                }
            }
        }

        private bool Suspended(Entity line, SlotKey slot) => m_Calendar.IsSuspended(line, slot.Date.AddMinutes(slot.Minute));
        private static double NowAbsMinute(ClockSnapshot clock) =>
            ScheduleClock.GameDay(clock.NowDate, clock.DaysPerYear) * 1440d + clock.NowMinuteExact;
        private static double FrameToAbsMinute(ClockSnapshot clock, uint nowFrame, uint frame) =>
            NowAbsMinute(clock) + clock.ToMinutes(unchecked((int)(frame - nowFrame)));

        private static int CompareEntity(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Version.CompareTo(right.Version);
        }

        private static int CompareCandidate(Candidate left, Candidate right)
        {
            int frame = unchecked((int)(left.ArrivalFrame - right.ArrivalFrame));
            return frame != 0 ? frame : CompareEntity(left.Vehicle, right.Vehicle);
        }

        private static int CompareChange(SlotChange left, SlotChange right) => CompareEntity(left.Vehicle, right.Vehicle);
    }
}
