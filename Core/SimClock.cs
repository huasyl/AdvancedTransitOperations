using System;
using System.Reflection;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using UnityEngine;

namespace RapidTransitMod.Core
{
    public readonly struct ClockSnapshot
    {
        public ClockSnapshot(
            int nowMinute,
            double nowMinuteExact,
            DateTime nowDate,
            int dayIndex,
            int ticksPerDay,
            double framesPerMinute,
            long clockEpoch,
            int daysPerYear)
        {
            NowMinute = nowMinute;
            NowMinuteExact = nowMinuteExact;
            NowDate = nowDate;
            DayIndex = dayIndex;
            TicksPerDay = ticksPerDay;
            FramesPerMinute = framesPerMinute;
            ClockEpoch = clockEpoch;
            DaysPerYear = daysPerYear;
        }

        public int NowMinute { get; }

        public double NowMinuteExact { get; }

        public DateTime NowDate { get; }

        public int DayIndex { get; }

        public int DaysPerYear { get; }

        public int TicksPerDay { get; }

        public double FramesPerMinute { get; }

        public long ClockEpoch { get; }

        public uint ToFramesCeil(double gameMinutes)
        {
            return ClampFrames(Math.Ceiling(gameMinutes * FramesPerMinute));
        }

        public uint ToFramesRound(double gameMinutes)
        {
            return ClampFrames(Math.Round(gameMinutes * FramesPerMinute, MidpointRounding.AwayFromZero));
        }

        public double ToMinutes(double simulationFrames)
        {
            return simulationFrames / FramesPerMinute;
        }

        public double DayFractionToFrames(double dayFraction)
        {
            return dayFraction * TicksPerDay;
        }

        public uint FramesUntilMinute(int targetMinute)
        {
            if (targetMinute < 0 || targetMinute >= 1440)
                return 0u;

            double remainingMinutes = targetMinute - NowMinuteExact;
            if (remainingMinutes <= 0d)
                remainingMinutes += 1440d;
            return ToFramesCeil(remainingMinutes);
        }

        private static uint ClampFrames(double simulationFrames)
        {
            if (double.IsNaN(simulationFrames) || simulationFrames <= 0d)
                return 0u;

            if (double.IsPositiveInfinity(simulationFrames) || simulationFrames >= uint.MaxValue)
                return uint.MaxValue;

            return (uint)simulationFrames;
        }
    }

    /// <summary>
    /// ATO 游戏业务时钟与仿真帧跨域换算的唯一入口。
    /// Minute/Minutes 表游戏时间，Frame/Frames 表仿真帧。
    /// ToMinutes 只供展示、日志和导出；业务门槛应把分钟转换成帧后比较。
    /// 本类不处理 m_Duration*60、固定帧节拍、容量常量或真实墙钟。
    /// </summary>
    public sealed class SimClock
    {
        private enum SnapshotMode : byte
        {
            Live,
            Load,
            ClockChange
        }

        private const int DEFAULT_TICKS_PER_DAY = 262144;
        private const string PROVIDER_TYPE_NAME = "Time2Work.Time2WorkTimeSystem";
        private const string PROVIDER_TICKS_PER_DAY_FIELD_NAME = "kTicksPerDay";

        private readonly TimeSystem m_GameClockSystem;
        private FieldInfo m_ProviderTicksPerDayField;
        private Func<float> m_ProviderNormalizedTime;
        private Func<float> m_ProviderNormalizedDate;
        private Func<int> m_ProviderYear;
        private Func<int> m_ProviderDaysPerYear;
        private bool m_ProviderReflectionResolved;
        private bool m_ProviderAssemblyRescanUsed;
        private bool m_HasBaseline;
        private ClockSnapshot m_LastSnapshot;
        private SnapshotMode m_SnapshotMode;
        private uint m_ClockChangeFrame;
        private bool m_HasDate;
        private int m_DateYear;
        private int m_DateDaysPerYear;
        private int m_DateDayOfYear;
        private DateTime m_Date;

        public SimClock(TimeSystem gameClockSystem)
        {
            m_GameClockSystem = gameClockSystem ?? throw new ArgumentNullException(nameof(gameClockSystem));
            TicksPerDay = DEFAULT_TICKS_PER_DAY;
            FramesPerMinute = DEFAULT_TICKS_PER_DAY / 1440d;
        }

        public event Action<ClockSnapshot, ClockSnapshot> ClockChanged;

        public int NowMinute
        {
            get
            {
                int rawMinute = (int)Math.Floor(NowMinuteExact);
                return ((rawMinute % 1440) + 1440) % 1440;
            }
        }

        public double NowMinuteExact
        {
            get
            {
                if (m_SnapshotMode != SnapshotMode.Live)
                    return m_LastSnapshot.NowMinuteExact;
                return MinuteOfDay(m_ProviderNormalizedTime?.Invoke() ?? m_GameClockSystem.normalizedTime);
            }
        }

        private static double MinuteOfDay(float normalizedDay)
        {
            if (float.IsNaN(normalizedDay) || float.IsInfinity(normalizedDay))
                return 0d;
            double rawMinute = normalizedDay * 1440d % 1440d;
            return rawMinute < 0d ? rawMinute + 1440d : rawMinute;
        }

        public DateTime NowDate
        {
            get
            {
                if (m_SnapshotMode != SnapshotMode.Live)
                    return m_LastSnapshot.NowDate;
                return DateOf(ClockYear, ClockDaysPerYear, DayOfYear(NowMinuteExact));
            }
        }

        public int DayIndex => m_SnapshotMode != SnapshotMode.Live ? m_LastSnapshot.DayIndex
            : (ClockYear - 1) * ClockDaysPerYear + DayOfYear(NowMinuteExact);

        private int DayOfYear(double minuteExact) => DayOfYear(minuteExact, ClockDaysPerYear, ClockNormalizedDate);

        private static int DayOfYear(double minuteExact, int daysPerYear, float normalizedDate)
        {
            int day = (int)Math.Round(daysPerYear * (double)normalizedDate - minuteExact / 1440d);
            return day % daysPerYear;
        }

        private DateTime DateOf(int year, int daysPerYear, int dayOfYear)
        {
            if (!m_HasDate || year != m_DateYear || daysPerYear != m_DateDaysPerYear
                || dayOfYear != m_DateDayOfYear)
            {
                m_Date = new DateTime(0L, DateTimeKind.Utc).AddYears(year - 1).AddDays(dayOfYear).Date;
                m_DateYear = year;
                m_DateDaysPerYear = daysPerYear;
                m_DateDayOfYear = dayOfYear;
                m_HasDate = true;
            }
            return m_Date;
        }

        private int ClockYear => m_ProviderYear?.Invoke() ?? m_GameClockSystem.year;

        private int ClockDaysPerYear => m_ProviderDaysPerYear?.Invoke() ?? m_GameClockSystem.daysPerYear;

        private float ClockNormalizedDate => m_ProviderNormalizedDate?.Invoke() ?? m_GameClockSystem.normalizedDate;

        public int TicksPerDay { get; private set; }

        public double FramesPerMinute { get; private set; }

        public long ClockEpoch { get; private set; }

        public ClockSnapshot Snapshot => CreateSnapshot();

        public void RefreshIfDue(uint simulationFrame)
        {
            Refresh(simulationFrame);
        }

        public void ForceRefresh(uint simulationFrame)
        {
            if (m_ProviderReflectionResolved
                && m_ProviderTicksPerDayField == null
                && !m_ProviderAssemblyRescanUsed)
            {
                m_ProviderAssemblyRescanUsed = true;
                m_ProviderReflectionResolved = false;
            }
            m_HasBaseline = false;
            m_SnapshotMode = SnapshotMode.Live;
            Refresh(simulationFrame);
        }

        public uint ToFramesCeil(double gameMinutes)
        {
            return CreateSnapshot().ToFramesCeil(gameMinutes);
        }

        public uint ToFramesRound(double gameMinutes)
        {
            return CreateSnapshot().ToFramesRound(gameMinutes);
        }

        public double ToMinutes(double simulationFrames)
        {
            return CreateSnapshot().ToMinutes(simulationFrames);
        }

        public double DayFractionToFrames(double dayFraction)
        {
            return CreateSnapshot().DayFractionToFrames(dayFraction);
        }

        private void Refresh(uint simulationFrame)
        {
            ResolveProviderReflection();
            int ticksPerDay = TicksPerDay;
            if (TryReadProviderTicksPerDay(out int providerTicksPerDay))
                ticksPerDay = providerTicksPerDay;
            int daysPerYear = ClockDaysPerYear;
            if (!m_HasBaseline)
            {
                TicksPerDay = ticksPerDay;
                FramesPerMinute = ticksPerDay / 1440d;
                if (providerTicksPerDay > 0 && TryCreateLoadSnapshot(simulationFrame, out m_LastSnapshot))
                    m_SnapshotMode = SnapshotMode.Load;
                else
                    m_LastSnapshot = CreateSnapshot();
                m_HasBaseline = true;
                return;
            }
            bool changed = ticksPerDay != TicksPerDay || daysPerYear != m_LastSnapshot.DaysPerYear;
            if (changed && m_SnapshotMode != SnapshotMode.ClockChange)
            {
                m_SnapshotMode = SnapshotMode.ClockChange;
                m_ClockChangeFrame = simulationFrame;
                return;
            }
            if (m_SnapshotMode == SnapshotMode.ClockChange && simulationFrame == m_ClockChangeFrame)
                return;
            ClockSnapshot oldSnapshot = m_LastSnapshot;
            m_SnapshotMode = SnapshotMode.Live;
            if (changed)
            {
                TicksPerDay = ticksPerDay;
                FramesPerMinute = ticksPerDay / 1440d;
                ClockEpoch++;
            }
            m_LastSnapshot = CreateSnapshot();
            if (changed)
            {
                Mod.log.Info("[SimClock] settings changed oldTicksPerDay=" + oldSnapshot.TicksPerDay
                    + " newTicksPerDay=" + m_LastSnapshot.TicksPerDay
                    + " oldDaysPerYear=" + oldSnapshot.DaysPerYear
                    + " newDaysPerYear=" + m_LastSnapshot.DaysPerYear
                    + " clockEpoch=" + ClockEpoch);
                ClockChanged?.Invoke(oldSnapshot, m_LastSnapshot);
            }
        }

        private ClockSnapshot CreateSnapshot()
        {
            if (m_SnapshotMode != SnapshotMode.Live)
                return m_LastSnapshot;
            return CreateSnapshot(NowMinuteExact, ClockYear, ClockDaysPerYear, ClockNormalizedDate);
        }

        private bool TryCreateLoadSnapshot(uint simulationFrame, out ClockSnapshot snapshot)
        {
            snapshot = default;
            EntityManager entities = m_GameClockSystem.EntityManager;
            using var timeQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<TimeData>());
            using var settingsQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<TimeSettingsData>());
            if (!timeQuery.TryGetSingleton<TimeData>(out var data)
                || !settingsQuery.TryGetSingleton<TimeSettingsData>(out var settings))
                return false;

            int daysPerYear = settings.m_DaysPerYear;
            int ticks = unchecked((int)simulationFrame - (int)data.m_FirstFrame
                + Mathf.RoundToInt(data.TimeOffset * TicksPerDay)
                + Mathf.RoundToInt(data.GetDateOffset(daysPerYear) * TicksPerDay * daysPerYear));
            int ticksPerYear = TicksPerDay * daysPerYear;
            float normalizedTime = (float)(ticks % TicksPerDay) / TicksPerDay;
            float normalizedDate = (float)(ticks % ticksPerYear) / ticksPerYear;
            int year = data.m_StartingYear + ticks / ticksPerYear;
            snapshot = CreateSnapshot(MinuteOfDay(normalizedTime), year, daysPerYear, normalizedDate);
            return true;
        }

        private ClockSnapshot CreateSnapshot(double nowMinuteExact, int year, int daysPerYear, float normalizedDate)
        {
            int dayOfYear = DayOfYear(nowMinuteExact, daysPerYear, normalizedDate);
            return new ClockSnapshot(
                (int)Math.Floor(nowMinuteExact),
                nowMinuteExact,
                DateOf(year, daysPerYear, dayOfYear),
                (year - 1) * daysPerYear + dayOfYear,
                TicksPerDay,
                FramesPerMinute,
                ClockEpoch,
                daysPerYear);
        }

        private void ResolveProviderReflection()
        {
            if (m_ProviderReflectionResolved)
                return;

            Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < loadedAssemblies.Length; assemblyIndex++)
            {
                Type providerClockType;
                try
                {
                    providerClockType = loadedAssemblies[assemblyIndex].GetType(PROVIDER_TYPE_NAME, false);
                }
                catch
                {
                    continue;
                }

                if (providerClockType == null)
                    continue;

                FieldInfo ticksPerDayField = providerClockType.GetField(
                    PROVIDER_TICKS_PER_DAY_FIELD_NAME,
                    BindingFlags.Public | BindingFlags.Static);
                var providerClock = m_GameClockSystem.World.GetExistingSystemManaged(providerClockType);
                if (providerClock == null)
                    return;
                if (ticksPerDayField?.FieldType != typeof(int))
                    throw new MissingFieldException(providerClockType.FullName, PROVIDER_TICKS_PER_DAY_FIELD_NAME);

                var normalizedTime = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), providerClock, "get_normalizedTime");
                var normalizedDate = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), providerClock, "get_normalizedDate");
                var year = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), providerClock, "get_year");
                var daysPerYear = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), providerClock, "get_daysPerYear");

                m_ProviderNormalizedTime = normalizedTime;
                m_ProviderNormalizedDate = normalizedDate;
                m_ProviderYear = year;
                m_ProviderDaysPerYear = daysPerYear;
                m_ProviderTicksPerDayField = ticksPerDayField;
                m_ProviderReflectionResolved = true;
                return;
            }
            m_ProviderReflectionResolved = true;
        }

        private bool TryReadProviderTicksPerDay(out int providerTicksPerDay)
        {
            providerTicksPerDay = 0;
            if (m_ProviderTicksPerDayField == null)
                return false;

            try
            {
                providerTicksPerDay = (int)m_ProviderTicksPerDayField.GetValue(null);
                return providerTicksPerDay > 0;
            }
            catch
            {
                providerTicksPerDay = 0;
                return false;
            }
        }
    }
}
