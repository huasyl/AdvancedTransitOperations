using System;
using System.Collections.Generic;
using Colossal.Core;
using RapidTransitMod;
using Unity.Entities;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class WorkbenchAccess
    {
        private readonly Host m_Host;

        internal WorkbenchAccess(Host host)
        {
            m_Host = host;
        }

        internal EntityManager EntityManager => m_Host.EntityManager;
        internal TimedLogger Log => m_Host.Log;
        internal bool Enabled => m_Host.Enabled;
        internal ulong Version => m_Host.Version;

        internal void Next() => m_Host.Next();
        internal void Load() => m_Host.Load();
        internal void Save() => m_Host.Save();
        internal void Run(Action action) => m_Host.Run(action);
        internal List<WorkbenchLineRuntime> Lines() => m_Host.Lines();
        internal string StationName(Entity stopEntity) => m_Host.StationName(stopEntity);
        internal string Name(Entity entity) => m_Host.Name(entity);
        internal string Error(Exception ex) => m_Host.Error(ex);
    }
}
