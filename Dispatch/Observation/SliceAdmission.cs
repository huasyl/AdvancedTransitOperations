using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal readonly struct TraversalSliceDailyQuota
    {
        public readonly int DateKey;
        public readonly int UsedCount;

        public TraversalSliceDailyQuota(int dateKey, int usedCount)
        {
            DateKey = dateKey;
            UsedCount = usedCount;
        }
    }

    internal readonly struct TraversalSliceColdStart
    {
        public readonly ulong ProfileSignature;
        public readonly int Remaining;
        public readonly int PendingFinalMinute;
        public readonly int PendingFinalDateKey;

        public TraversalSliceColdStart(
            ulong profileSignature,
            int remaining,
            int pendingFinalMinute,
            int pendingFinalDateKey)
        {
            ProfileSignature = profileSignature;
            Remaining = remaining;
            PendingFinalMinute = pendingFinalMinute;
            PendingFinalDateKey = pendingFinalDateKey;
        }
    }

    internal sealed class SliceAdmission
    {
        private const int DailyLimit = 4;
        private readonly SliceStore m_Slices;
        private readonly SliceAdmissionPort m_Port;
        private readonly Dictionary<LineKey, TraversalSliceDailyQuota> m_DailyQuotas =
            new Dictionary<LineKey, TraversalSliceDailyQuota>();
        private readonly Dictionary<LineKey, TraversalSliceColdStart> m_ColdStarts =
            new Dictionary<LineKey, TraversalSliceColdStart>();
        private readonly Dictionary<Entity, LineKey> m_AllowedVehicles =
            new Dictionary<Entity, LineKey>();
        private readonly Dictionary<LineKey, int> m_LoggedPlans =
            new Dictionary<LineKey, int>();

        internal SliceAdmission(SliceStore slices, SliceAdmissionPort port)
        {
            m_Slices = slices;
            m_Port = port;
        }

        internal bool Begin(Entity line, Entity vehicle, int slotMinute)
        {
            (bool keySuccess, LineKey lak) = m_Port.StableKey(line);
            if (!keySuccess || lak.IsEmpty)
                return LogDecision(LineKey.Empty, 0, slotMinute, 0, false, "invalid-lak");

            if (m_AllowedVehicles.TryGetValue(vehicle, out LineKey allowedLak))
            {
                if (allowedLak == lak)
                    return LogDecision(lak, ToDateKey(m_Port.ServiceDate()), slotMinute, 0, true, "already-admitted");

                End(vehicle);
            }

            m_DailyQuotas.TryGetValue(lak, out TraversalSliceDailyQuota storedQuota);
            int dateKey = ToDateKey(m_Port.ServiceDate());
            TraversalSliceDailyQuota quota = storedQuota.DateKey == dateKey
                ? storedQuota
                : new TraversalSliceDailyQuota(dateKey, 0);
            if (quota.UsedCount >= DailyLimit)
                return LogDecision(lak, dateKey, slotMinute, quota.UsedCount, false, "daily-cap");

            if (slotMinute < 0)
                return LogDecision(lak, dateKey, slotMinute, quota.UsedCount, false, "slot-missing");

            int[] departureMinutes = m_Port.DepartureMinutes(line) ?? Array.Empty<int>();
            if (departureMinutes.Length == 0)
                return LogDecision(lak, dateKey, slotMinute, quota.UsedCount, false, "schedule-missing");
            if (Array.BinarySearch(departureMinutes, slotMinute) < 0)
                return LogDecision(lak, dateKey, slotMinute, quota.UsedCount, false, "slot-unmapped");

            bool coldContinuous = false;
            bool coldFinal = false;
            bool coldEnded = false;
            bool hasColdStart = m_ColdStarts.TryGetValue(lak, out TraversalSliceColdStart cold);
            bool hasObservation = m_Slices.HasLineObservation(line);
            if (hasColdStart
                && cold.Remaining == 0
                && (!HasPendingFinal(cold)
                    || cold.PendingFinalDateKey != dateKey
                    || Array.BinarySearch(departureMinutes, cold.PendingFinalMinute) < 0))
            {
                InvalidateColdStart(lak);
                hasColdStart = false;
                cold = default;
                coldEnded = true;
            }

            ulong signature = 0UL;
            if (hasColdStart || (!hasObservation && !coldEnded))
            {
                (bool Success, ulong Signature) profile = m_Port.ProfileSignature(line);
                signature = profile.Signature;
                if (!profile.Success || signature == 0UL)
                    return LogDecision(lak, dateKey, slotMinute, quota.UsedCount, false, "profile-missing");

                if (hasColdStart && cold.ProfileSignature != signature)
                {
                    InvalidateColdStart(lak);
                    hasColdStart = false;
                    cold = default;
                }
            }

            if (hasColdStart)
            {
                if (cold.Remaining > 0)
                {
                    coldContinuous = true;
                }
                else
                {
                    coldFinal = slotMinute == cold.PendingFinalMinute;
                }
            }
            else if (!hasObservation && !coldEnded)
            {
                cold = new TraversalSliceColdStart(signature, 3, -1, 0);
                coldContinuous = true;
            }

            bool coldAdmission = coldContinuous || coldFinal;
            LogPlan(lak, dateKey, slotMinute, departureMinutes, quota, hasColdStart || coldContinuous ? cold : default);
            if (hasColdStart && cold.Remaining == 0 && !coldFinal)
                return LogDecision(lak, dateKey, slotMinute, quota.UsedCount, false, "cold-final-wait");
            if (!coldAdmission && !IsSelectedSlot(slotMinute, departureMinutes))
                return LogDecision(lak, dateKey, slotMinute, quota.UsedCount, false, "not-selected");

            TraversalSliceDailyQuota nextQuota = new TraversalSliceDailyQuota(dateKey, quota.UsedCount + 1);
            if (!m_Port.TryFlushDailyQuota(lak, nextQuota))
                return LogDecision(lak, dateKey, slotMinute, quota.UsedCount, false, "quota-persist-failed");

            if (coldContinuous)
            {
                int nextRemaining = Math.Max(0, cold.Remaining - 1);
                int pendingFinalMinute = nextRemaining == 0
                    ? FindPendingFinalMinute(slotMinute, departureMinutes)
                    : -1;
                if (nextRemaining > 0 || pendingFinalMinute >= 0)
                {
                    TraversalSliceColdStart nextCold = new TraversalSliceColdStart(
                        signature,
                        nextRemaining,
                        pendingFinalMinute,
                        pendingFinalMinute >= 0 ? dateKey : 0);
                    if (!m_Port.TryFlushColdStart(lak, nextCold))
                    {
                        m_DailyQuotas[lak] = nextQuota;
                        return LogDecision(lak, dateKey, slotMinute, nextQuota.UsedCount, false, "cold-start-persist-failed");
                    }
                    m_ColdStarts[lak] = nextCold;
                }
                else
                {
                    InvalidateColdStart(lak);
                }
            }
            else if (coldFinal)
            {
                InvalidateColdStart(lak);
            }

            m_DailyQuotas[lak] = nextQuota;
            m_AllowedVehicles[vehicle] = lak;
            return LogDecision(lak, dateKey, slotMinute, nextQuota.UsedCount, true, "accept");
        }

        internal bool CanObserve(Entity vehicle) => m_AllowedVehicles.ContainsKey(vehicle);

        internal void End(Entity vehicle) => m_AllowedVehicles.Remove(vehicle);

        internal void OnSliceWritten(Entity line)
        {
            (bool success, LineKey lak) = m_Port.StableKey(line);
            if (!success
                || !m_ColdStarts.TryGetValue(lak, out TraversalSliceColdStart cold)
                || cold.Remaining != 0
                || HasPendingFinal(cold)
                || !m_Slices.HasLineObservation(line))
            {
                return;
            }

            InvalidateColdStart(lak);
        }

        internal void RestoreDailyQuota(LineKey lak, int dateKey, int usedCount)
        {
            if (!lak.IsEmpty)
                m_DailyQuotas[lak] = new TraversalSliceDailyQuota(dateKey, Math.Max(0, Math.Min(DailyLimit, usedCount)));
        }

        internal void RestoreColdStart(
            LineKey lak,
            ulong signature,
            int remaining,
            int pendingFinalMinute,
            int pendingFinalDateKey)
        {
            if (!lak.IsEmpty)
                m_ColdStarts[lak] = new TraversalSliceColdStart(
                    signature,
                    Math.Max(0, Math.Min(3, remaining)),
                    pendingFinalMinute,
                    pendingFinalDateKey);
        }

        internal void ClearPersistedState()
        {
            m_DailyQuotas.Clear();
            m_ColdStarts.Clear();
            m_LoggedPlans.Clear();
        }

        internal void Clear()
        {
            ClearPersistedState();
            m_AllowedVehicles.Clear();
        }

        internal static bool IsSelectedSlot(int slotMinute, int[] departureMinutes)
        {
            int index = Array.BinarySearch(departureMinutes, slotMinute);
            if (index < 0)
                return false;
            if (departureMinutes.Length <= DailyLimit)
                return true;

            for (int i = 0; i < DailyLimit; i++)
            {
                int selected = (int)Math.Round(i * (departureMinutes.Length - 1) / 3d, MidpointRounding.AwayFromZero);
                if (index == selected)
                    return true;
            }
            return false;
        }

        internal static int ToDateKey(DateTime serviceDate) =>
            serviceDate.Year * 10000 + serviceDate.Month * 100 + serviceDate.Day;

        internal void InvalidateColdStart(LineKey lak)
        {
            m_ColdStarts.Remove(lak);
            m_Port.RemoveColdStart(lak);
        }

        internal void InvalidateLine(Entity line)
        {
            (bool success, LineKey lak) = m_Port.StableKey(line);
            if (!success || lak.IsEmpty)
                return;

            InvalidateColdStart(lak);
            List<Entity> vehicles = new List<Entity>();
            foreach (KeyValuePair<Entity, LineKey> pair in m_AllowedVehicles)
                if (pair.Value == lak) vehicles.Add(pair.Key);
            for (int i = 0; i < vehicles.Count; i++)
                m_AllowedVehicles.Remove(vehicles[i]);
        }

        private void LogPlan(
            LineKey lak,
            int dateKey,
            int slotMinute,
            int[] departureMinutes,
            TraversalSliceDailyQuota quota,
            TraversalSliceColdStart cold)
        {
            if (!RtLog.VerboseEnabled
                || (m_LoggedPlans.TryGetValue(lak, out int loggedDate) && loggedDate == dateKey))
            {
                return;
            }

            int[] plannedMinutes = BuildPlannedMinutes(
                slotMinute,
                departureMinutes,
                quota.UsedCount,
                dateKey,
                cold);
            string[] plannedMinuteTexts = Array.ConvertAll(plannedMinutes, value => m_Port.FormatMinute(value));
            m_LoggedPlans[lak] = dateKey;
            m_Port.Log("[LogSlicePlan] lak=" + lak + " date=" + dateKey + " slots=" + string.Join(",", plannedMinuteTexts));
        }

        private static int[] BuildPlannedMinutes(
            int slotMinute,
            int[] departureMinutes,
            int usedCount,
            int dateKey,
            TraversalSliceColdStart cold)
        {
            List<int> plannedMinutes = new List<int>();
            int start = Array.BinarySearch(departureMinutes, slotMinute);
            if (start < 0)
                return plannedMinutes.ToArray();

            int capacity = Math.Max(0, DailyLimit - usedCount);
            int normalStart = start;
            if (cold.ProfileSignature != 0UL && cold.Remaining > 0)
            {
                int continuousCount = Math.Min(
                    Math.Min(cold.Remaining, capacity),
                    departureMinutes.Length - start);
                for (int i = 0; i < continuousCount; i++)
                    plannedMinutes.Add(departureMinutes[start + i]);
                if (continuousCount < cold.Remaining || plannedMinutes.Count >= capacity)
                    return plannedMinutes.ToArray();

                int thirdMinute = departureMinutes[start + continuousCount - 1];
                int pendingFinalMinute = FindPendingFinalMinute(thirdMinute, departureMinutes);
                if (pendingFinalMinute < 0)
                    return plannedMinutes.ToArray();

                plannedMinutes.Add(pendingFinalMinute);
                normalStart = Array.BinarySearch(departureMinutes, pendingFinalMinute) + 1;
            }
            else if (HasPendingFinal(cold) && cold.PendingFinalDateKey == dateKey)
            {
                int pendingIndex = Array.BinarySearch(departureMinutes, cold.PendingFinalMinute);
                if (pendingIndex < start)
                    return plannedMinutes.ToArray();
                plannedMinutes.Add(cold.PendingFinalMinute);
                normalStart = pendingIndex + 1;
            }

            for (int i = normalStart; i < departureMinutes.Length && plannedMinutes.Count < capacity; i++)
            {
                if (IsSelectedSlot(departureMinutes[i], departureMinutes)
                    && !plannedMinutes.Contains(departureMinutes[i]))
                {
                    plannedMinutes.Add(departureMinutes[i]);
                }
            }
            return plannedMinutes.ToArray();
        }

        private static bool HasPendingFinal(TraversalSliceColdStart cold) =>
            cold.PendingFinalMinute >= 0 && cold.PendingFinalDateKey > 0;

        private static int FindPendingFinalMinute(int slotMinute, int[] departureMinutes)
        {
            int currentIndex = Array.BinarySearch(departureMinutes, slotMinute);
            int remainingStart = currentIndex + 1;
            int remainingCount = departureMinutes.Length - remainingStart;
            return currentIndex >= 0 && remainingCount > 0
                ? departureMinutes[remainingStart + remainingCount / 2]
                : -1;
        }

        private bool LogDecision(LineKey lak, int dateKey, int slotMinute, int usedCount, bool accepted, string reason)
        {
            if (RtLog.VerboseEnabled)
            {
                m_Port.Log("[LogSliceDecision] lak=" + (lak.IsEmpty ? "-" : lak.ToString())
                    + " date=" + dateKey
                    + " slot=" + (slotMinute >= 0 ? m_Port.FormatMinute(slotMinute) : "-")
                    + " used=" + usedCount
                    + " decision=" + (accepted ? "accept" : "reject")
                    + " reason=" + reason);
            }
            return accepted;
        }
    }
}
