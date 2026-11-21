using System;
using System.Collections.Generic;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal readonly struct ServiceWindow
    {
        internal readonly DateTime Start;
        internal readonly DateTime End;
        internal readonly bool Open;

        internal ServiceWindow(DateTime start, DateTime end, bool open)
        {
            Start = start;
            End = end;
            Open = open;
        }
    }

    internal sealed class ServiceCalendar
    {
        private readonly LineServiceState m_ServiceState;
        private readonly Dictionary<Entity, List<ServiceWindow>> m_Windows =
            new Dictionary<Entity, List<ServiceWindow>>();

        internal ServiceCalendar(LineServiceState serviceState)
        {
            m_ServiceState = serviceState ?? throw new ArgumentNullException(nameof(serviceState));
        }

        internal bool Dirty { get; private set; }

        internal void ClearDirty() => Dirty = false;

        internal bool IsSuspended(Entity line, DateTime occurrence)
        {
            if (!m_Windows.TryGetValue(line, out List<ServiceWindow> windows))
                return false;
            for (int i = 0; i < windows.Count; i++)
            {
                ServiceWindow window = windows[i];
                if (occurrence >= window.Start && (window.Open || occurrence < window.End))
                    return true;
            }
            return false;
        }

        internal void Pause(Entity line, ClockSnapshot clock)
        {
            if (line == Entity.Null)
                return;
            List<ServiceWindow> windows = GetWindows(line);
            if (HasOpenWindow(windows))
                return;
            windows.Add(new ServiceWindow(ClockTime(clock), DateTime.MinValue, true));
            Dirty = true;
        }

        internal void Resume(Entity line, ClockSnapshot clock)
        {
            if (line == Entity.Null || !m_Windows.TryGetValue(line, out List<ServiceWindow> windows))
                return;
            DateTime now = ClockTime(clock);
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                if (!windows[i].Open)
                    continue;
                windows[i] = new ServiceWindow(windows[i].Start, now, false);
                Normalize(windows);
                Dirty = true;
                break;
            }
        }

        internal void Baseline(
            Entity line,
            bool operational,
            ClockSnapshot clock,
            List<ServiceWindow> restored)
        {
            if (line == Entity.Null)
                return;
            List<ServiceWindow> windows = GetWindows(line);
            if (restored != null)
                windows.AddRange(restored);
            Normalize(windows);
            bool changed = false;
            DateTime now = ClockTime(clock);
            bool open = HasOpenWindow(windows);
            if (!operational && !open)
            {
                windows.Add(new ServiceWindow(now, DateTime.MinValue, true));
                changed = true;
            }
            else if (operational && open)
            {
                for (int i = windows.Count - 1; i >= 0; i--)
                {
                    if (!windows[i].Open)
                        continue;
                    windows[i] = new ServiceWindow(windows[i].Start, now, false);
                    changed = true;
                    break;
                }
            }
            Normalize(windows);
            if (changed || restored != null)
                Dirty = true;
        }

        internal void Reset(ClockSnapshot clock, NativeArray<Entity> lines)
        {
            Clear();
            for (int i = 0; i < lines.Length; i++)
                Baseline(lines[i], m_ServiceState.IsOperational(lines[i]), clock, null);
            Dirty = true;
        }

        internal void Prune(ClockSnapshot clock)
        {
            foreach (KeyValuePair<Entity, List<ServiceWindow>> entry in m_Windows)
            {
                List<ServiceWindow> windows = entry.Value;
                for (int i = windows.Count - 1; i >= 0; i--)
                {
                    ServiceWindow window = windows[i];
                    if (!window.Open
                        && ScheduleClock.RelativeMinutes(clock, window.End.Date, window.End.TimeOfDay.TotalMinutes) < -1440d)
                    {
                        windows.RemoveAt(i);
                        Dirty = true;
                    }
                }
            }
        }

        internal void RemoveLine(Entity line)
        {
            if (line != Entity.Null && m_Windows.Remove(line))
                Dirty = true;
        }

        internal void Clear()
        {
            m_Windows.Clear();
            Dirty = false;
        }

        internal IEnumerable<KeyValuePair<Entity, List<ServiceWindow>>> Windows() => m_Windows;

        private List<ServiceWindow> GetWindows(Entity line)
        {
            if (!m_Windows.TryGetValue(line, out List<ServiceWindow> windows))
            {
                windows = new List<ServiceWindow>();
                m_Windows[line] = windows;
            }
            return windows;
        }

        private static bool HasOpenWindow(List<ServiceWindow> windows)
        {
            for (int i = 0; i < windows.Count; i++)
                if (windows[i].Open)
                    return true;
            return false;
        }

        private static DateTime ClockTime(ClockSnapshot clock)
        {
            return clock.NowDate.Date.AddMinutes(clock.NowMinute);
        }

        private static void Normalize(List<ServiceWindow> windows)
        {
            if (windows.Count < 2)
                return;
            windows.Sort((left, right) => left.Start.CompareTo(right.Start));
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                ServiceWindow window = windows[i];
                if (!window.Open && window.End <= window.Start)
                    windows.RemoveAt(i);
            }
            for (int i = 1; i < windows.Count;)
            {
                ServiceWindow previous = windows[i - 1];
                ServiceWindow current = windows[i];
                if (!previous.Open && current.Start > previous.End)
                {
                    i++;
                    continue;
                }
                bool open = previous.Open || current.Open;
                DateTime end = open
                    ? DateTime.MinValue
                    : previous.End >= current.End ? previous.End : current.End;
                windows[i - 1] = new ServiceWindow(previous.Start, end, open);
                windows.RemoveAt(i);
            }
        }
    }
}
