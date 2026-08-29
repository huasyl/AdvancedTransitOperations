using System.Collections.Generic;
using RapidTransitMod.Bypass;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Scheduling;
using RapidTransitMod.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class VanillaServiceControl
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly LineServiceState m_ServiceState;

        internal VanillaServiceControl(ModRuntimeHostSystem runtime, LineServiceState serviceState)
        {
            m_Runtime = runtime;
            m_ServiceState = serviceState;
        }

        internal void Apply(LineServiceChange change, ClockSnapshot clock)
        {
            if (change.Operational)
                ResumeLine(change.Line, clock);
            else
                PauseLine(change.Line, clock);
        }

        internal void BaselineStartup(IReadOnlyList<Entity> lines, ClockSnapshot clock)
        {
            if (lines == null)
                return;
            m_ServiceState.BaselineStableLines(lines);
            for (int i = 0; i < lines.Count; i++)
            {
                Entity line = lines[i];
                if (line == Entity.Null)
                    continue;
                bool operational = m_ServiceState.IsOperational(line);
                m_Runtime.m_DispatchScheduler.BaselineLine(
                    line,
                    operational,
                    clock,
                    m_Runtime.m_ServiceWindowStore.Take(line));
            }
            m_Runtime.m_ServiceWindowStore.Flush();
        }

        private void PauseLine(Entity line, ClockSnapshot clock)
        {
            m_Runtime.m_LineSpawnControl.SuspendLine(line);
            m_Runtime.m_DispatchScheduler.PauseLine(line, clock);
            List<Entity> bypassVehicles = m_Runtime.m_Bypass.ReleaseLineForServicePause(line);
            if (bypassVehicles != null)
            {
                for (int i = 0; i < bypassVehicles.Count; i++)
                    m_Runtime.m_RuntimeFramePlan.AddStage(bypassVehicles[i], RuntimeStageMask.Bypass);
            }

            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                        || vehicleLine != line
                        || !m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                        || state == VehicleState.Retiring
                        || m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                    {
                        continue;
                    }
                    m_Runtime.m_CommandApplier.Retire(vehicle, "线路暂停运营");
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        private void ResumeLine(Entity line, ClockSnapshot clock)
        {
            m_Runtime.m_DispatchScheduler.ResumeLine(line, clock);
            m_Runtime.m_LineSpawnControl.ResumeLine(line);
        }
    }
}
