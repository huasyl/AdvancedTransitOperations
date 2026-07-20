using System;
using System.Reflection;
using Game.Simulation;

namespace RapidTransitMod.Core
{
    public readonly struct ClockSnapshot
    {
        public ClockSnapshot(
            int nowMinute,
            DateTime nowDate,
            int ticksPerDay,
            double framesPerMinute,
            long clockEpoch)
        {
            NowMinute = nowMinute;
            NowDate = nowDate;
            TicksPerDay = ticksPerDay;
            FramesPerMinute = framesPerMinute;
            ClockEpoch = clockEpoch;
        }

        public int NowMinute { get; }

        public DateTime NowDate { get; }

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
        private const int DEFAULT_TICKS_PER_DAY = 262144;
        private const uint PROBE_CADENCE_FRAMES = 32u;
        private const string PROVIDER_TYPE_NAME = "Time2Work.Time2WorkTimeSystem";
        private const string PROVIDER_TICKS_PER_DAY_FIELD_NAME = "kTicksPerDay";

        private readonly TimeSystem m_GameClockSystem;
        private FieldInfo m_ProviderTicksPerDayField;
        private bool m_ProviderReflectionResolved;
        private bool m_ProviderAssemblyRescanUsed;
        private bool m_HasProbeFrame;
        private uint m_LastProbeFrame;

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
                float normalizedDay = m_GameClockSystem.normalizedTime;
                if (float.IsNaN(normalizedDay) || float.IsInfinity(normalizedDay))
                    return 0;

                int rawMinute = (int)Math.Floor(normalizedDay * 1440d);
                return ((rawMinute % 1440) + 1440) % 1440;
            }
        }

        public DateTime NowDate => m_GameClockSystem.GetCurrentDateTime().Date;

        public int TicksPerDay { get; private set; }

        public double FramesPerMinute { get; private set; }

        public long ClockEpoch { get; private set; }

        public ClockSnapshot Snapshot => CreateSnapshot();

        public void RefreshIfDue(uint simulationFrame)
        {
            if (m_ProviderTicksPerDayField == null)
                return;

            if (m_HasProbeFrame && unchecked(simulationFrame - m_LastProbeFrame) < PROBE_CADENCE_FRAMES)
                return;

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
            m_LastProbeFrame = simulationFrame;
            m_HasProbeFrame = true;

            ResolveProviderReflection();
            if (!TryReadProviderTicksPerDay(out int providerTicksPerDay) || providerTicksPerDay == TicksPerDay)
                return;

            int nowMinute = NowMinute;
            DateTime nowDate = NowDate;
            ClockSnapshot oldSnapshot = new ClockSnapshot(
                nowMinute,
                nowDate,
                TicksPerDay,
                FramesPerMinute,
                ClockEpoch);

            TicksPerDay = providerTicksPerDay;
            FramesPerMinute = providerTicksPerDay / 1440d;
            ClockEpoch++;

            ClockSnapshot newSnapshot = new ClockSnapshot(
                nowMinute,
                nowDate,
                TicksPerDay,
                FramesPerMinute,
                ClockEpoch);

            Mod.log.Info("[SimClock] day length changed oldTicksPerDay=" + oldSnapshot.TicksPerDay
                + " newTicksPerDay=" + newSnapshot.TicksPerDay
                + " oldFramesPerMinute=" + oldSnapshot.FramesPerMinute.ToString("0.########")
                + " newFramesPerMinute=" + newSnapshot.FramesPerMinute.ToString("0.########")
                + " clockEpoch=" + newSnapshot.ClockEpoch);
            ClockChanged?.Invoke(oldSnapshot, newSnapshot);
        }

        private ClockSnapshot CreateSnapshot()
        {
            return new ClockSnapshot(NowMinute, NowDate, TicksPerDay, FramesPerMinute, ClockEpoch);
        }

        private void ResolveProviderReflection()
        {
            if (m_ProviderReflectionResolved)
                return;

            m_ProviderReflectionResolved = true;
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

                m_ProviderTicksPerDayField = providerClockType.GetField(
                    PROVIDER_TICKS_PER_DAY_FIELD_NAME,
                    BindingFlags.Public | BindingFlags.Static);
                if (m_ProviderTicksPerDayField?.FieldType != typeof(int))
                    m_ProviderTicksPerDayField = null;
                return;
            }
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
