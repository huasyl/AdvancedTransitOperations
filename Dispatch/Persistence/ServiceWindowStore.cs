using System;
using System.Collections.Generic;
using Game.Serialization;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Persistence
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

    internal sealed class ServiceWindowStore
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<LineKey, List<ServiceWindow>> m_Restored =
            new Dictionary<LineKey, List<ServiceWindow>>();
        private bool m_Dirty;

        internal ServiceWindowStore(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal void Ensure()
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (city != Entity.Null
                && !m_Runtime.EntityManager.HasBuffer<LineServiceWindowElement>(city))
            {
                m_Runtime.EntityManager.AddBuffer<LineServiceWindowElement>(city);
            }
        }

        internal void Load()
        {
            m_Restored.Clear();
            m_Dirty = false;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<LineServiceWindowElement>(city))
            {
                return;
            }

            DynamicBuffer<LineServiceWindowElement> buffer =
                m_Runtime.EntityManager.GetBuffer<LineServiceWindowElement>(city, true);
            m_Dirty = buffer.Length != 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                LineServiceWindowElement element = buffer[i];
                if (!LineKey.TryParse(element.m_LineKey.ToString(), out LineKey key))
                    continue;
                DateTime start = ReadTime(element.m_StartDateKey, element.m_StartMinute);
                DateTime end = element.m_Open == 1
                    ? DateTime.MinValue
                    : ReadTime(element.m_EndDateKey, element.m_EndMinute);
                if (start == DateTime.MinValue || (!element.m_Open.Equals((byte)1) && end == DateTime.MinValue))
                    continue;
                if (!m_Restored.TryGetValue(key, out List<ServiceWindow> windows))
                {
                    windows = new List<ServiceWindow>();
                    m_Restored[key] = windows;
                }
                windows.Add(new ServiceWindow(start, end, element.m_Open == 1));
            }
        }

        internal List<ServiceWindow> Take(Entity line)
        {
            LineKey key = m_Runtime.m_LineAnchorCatalog.StableKey(line);
            if (key.IsEmpty || !m_Restored.TryGetValue(key, out List<ServiceWindow> windows))
                return null;
            m_Restored.Remove(key);
            return windows;
        }

        internal void RemoveLine(Entity line)
        {
            LineKey key = m_Runtime.m_LineAnchorCatalog.StableKey(line);
            if (!key.IsEmpty)
                m_Restored.Remove(key);
            m_Runtime.m_DispatchScheduler?.RemoveServiceWindows(line);
            m_Dirty = true;
        }

        internal void MarkDirty()
        {
            m_Dirty = true;
        }

        internal void Flush()
        {
            if (!m_Dirty || m_Runtime.m_DispatchScheduler == null)
                return;
            Ensure();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            DynamicBuffer<LineServiceWindowElement> buffer =
                m_Runtime.EntityManager.GetBuffer<LineServiceWindowElement>(city);
            buffer.Clear();
            foreach (KeyValuePair<Entity, List<ServiceWindow>> entry in m_Runtime.m_DispatchScheduler.ServiceWindows())
            {
                LineKey key = m_Runtime.m_LineAnchorCatalog.StableKey(entry.Key);
                if (key.IsEmpty)
                    continue;
                List<ServiceWindow> windows = entry.Value;
                for (int i = 0; i < windows.Count; i++)
                {
                    ServiceWindow window = windows[i];
                    if (!window.Open && window.End <= window.Start)
                        continue;
                    buffer.Add(new LineServiceWindowElement
                    {
                        m_LineKey = LineIdentityService.GetId(key),
                        m_StartDateKey = ScheduleClock.DateKey(window.Start),
                        m_StartMinute = window.Start.Hour * 60 + window.Start.Minute,
                        m_EndDateKey = window.Open ? 0 : ScheduleClock.DateKey(window.End),
                        m_EndMinute = window.Open ? 0 : window.End.Hour * 60 + window.End.Minute,
                        m_Open = window.Open ? (byte)1 : (byte)0
                    });
                }
            }
            m_Dirty = false;
        }

        internal void ClearTracking()
        {
            m_Restored.Clear();
            m_Dirty = false;
        }

        internal void ClearAll()
        {
            m_Restored.Clear();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city != Entity.Null && m_Runtime.EntityManager.HasBuffer<LineServiceWindowElement>(city))
                m_Runtime.EntityManager.GetBuffer<LineServiceWindowElement>(city).Clear();
            m_Dirty = false;
        }

        private static DateTime ReadTime(int dateKey, int minute)
        {
            if (dateKey <= 0 || minute < 0 || minute >= 1440)
                return DateTime.MinValue;
            int year = dateKey / 10000;
            int month = dateKey / 100 % 100;
            int day = dateKey % 100;
            try
            {
                return new DateTime(year, month, day).AddMinutes(minute);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }
    }
}
