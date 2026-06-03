using System;
using System.Collections.Generic;
using Colossal.Core;
using RapidTransitMod;
using Unity.Entities;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal abstract class Host
    {
        internal abstract EntityManager EntityManager { get; }
        internal abstract TimedLogger Log { get; }
        internal abstract bool Enabled { get; }
        internal abstract ulong Version { get; }

        internal abstract void Next();
        internal abstract void Load();
        internal abstract void Save();
        internal abstract List<WorkbenchLineRuntime> Lines();
        internal abstract string Error(Exception ex);
    }
}
