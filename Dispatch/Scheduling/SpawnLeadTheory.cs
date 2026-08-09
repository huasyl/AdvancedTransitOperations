using System.Collections.Generic;
using RapidTransitMod.Core;
using Game.Routes;
using RapidTransitMod.RailEtaHost;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal sealed class SpawnLeadTheory
    {
        private sealed class Entry
        {
            public ulong Signature;
            public Entity Depot;
            public Entity Waypoint;
            public Entity Model;
            public Entity SecondaryModel;
            public uint TheoryFrames;
            public uint RawFrames;
            public bool Ready;
            public bool Failed;
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, Entry> m_Entries = new Dictionary<Entity, Entry>();
        private readonly Dictionary<Entity, string> m_FactFailures = new Dictionary<Entity, string>();
        private readonly Queue<Entity> m_Queue = new Queue<Entity>();
        private readonly HashSet<Entity> m_Queued = new HashSet<Entity>();
        private Entity m_ActiveLine;
        private RailEtaPublicTicket m_ActiveTicket;

        internal SpawnLeadTheory(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
            m_Runtime.m_SimClock.ClockChanged += OnClockChanged;
        }

        internal void Ensure(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!IsRailLine(line))
                return;
            if (!TryFacts(line, waypoints, out Entry facts, out string failure))
            {
                Invalidate(line, failure);
                return;
            }
            if (m_Entries.TryGetValue(line, out Entry current) && current.Signature == facts.Signature)
            {
                if (!current.Ready && !current.Failed && m_ActiveLine != line && !m_Queued.Contains(line))
                    Enqueue(line);
                return;
            }
            m_FactFailures.Remove(line);
            m_Entries[line] = facts;
            float legacy = m_Runtime.m_DispatchCache.Read(line);
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[SpawnLead] line=" + line.Index + " source=" + FallbackSource(legacy)
                    + " reason=rail-eta-theory-pending prepUsedFrames=" + m_Runtime.m_DispatchCache.ReadPrep(line)
                    + " legacyFrames=" + legacy.ToString("F0"));
            }
            if (m_ActiveLine == line)
            {
                m_Runtime.m_RailEtaService?.Cancel(m_ActiveTicket);
                m_ActiveLine = Entity.Null;
                m_ActiveTicket = default;
            }
            Enqueue(line);
        }

        internal void Tick()
        {
            PollActive();
            if (m_ActiveLine != Entity.Null || m_Queue.Count == 0)
                return;
            Entity line = m_Queue.Dequeue();
            m_Queued.Remove(line);
            if (!m_Entries.TryGetValue(line, out Entry entry) || entry.Ready || entry.Failed)
                return;
            RailEtaBridgeService service = m_Runtime.m_RailEtaService;
            if (service == null || !service.CanSubmit)
            {
                Enqueue(line);
                return;
            }
            RailEtaPublicRequest request = new RailEtaPublicRequest(
                line.Index, line.Version, Pack(entry.Waypoint), RailEtaMode.Theory,
                entry.Depot.Index, entry.Depot.Version, entry.Model.Index, entry.Model.Version,
                entry.SecondaryModel.Index, entry.SecondaryModel.Version);
            RailEtaPublicTicket ticket = service.RequestEta(request);
            if (!ticket.IsValid)
            {
                entry.Failed = true;
                LogFailure(line, "TheorySubmitRejected");
                return;
            }
            m_ActiveLine = line;
            m_ActiveTicket = ticket;
        }

        internal bool TryRead(Entity line, out float frames)
        {
            if (!IsRailLine(line))
            {
                frames = 0f;
                return false;
            }
            if (m_Entries.TryGetValue(line, out Entry entry) && entry.Ready)
            {
                frames = entry.TheoryFrames + m_Runtime.m_DispatchCache.ReadPrep(line);
                return true;
            }
            frames = 0f;
            return false;
        }

        internal void Clear()
        {
            if (m_ActiveTicket.IsValid) m_Runtime.m_RailEtaService?.Cancel(m_ActiveTicket);
            m_Entries.Clear();
            m_FactFailures.Clear();
            m_Queue.Clear();
            m_Queued.Clear();
            m_ActiveLine = Entity.Null;
            m_ActiveTicket = default;
        }

        private void PollActive()
        {
            if (m_ActiveLine == Entity.Null || !m_ActiveTicket.IsValid)
                return;
            RailEtaBridgeService service = m_Runtime.m_RailEtaService;
            if (service == null || !service.TryGetState(m_ActiveTicket, out RailEtaPublicStatus status))
                return;
            if (!Terminal(status.State)) return;
            Entity line = m_ActiveLine;
            m_ActiveLine = Entity.Null;
            m_ActiveTicket = default;
            if (!m_Entries.TryGetValue(line, out Entry entry)) return;
            if (status.State == "ClockChanged")
            {
                Enqueue(line);
                return;
            }
            if (status.State != "Completed" || status.EtaFrame == 0u
                || unchecked(status.EtaFrame - status.OriginFrame) >= 0x80000000u)
            {
                entry.Failed = true;
                LogFailure(line, status.Failure + ":" + status.Detail);
                return;
            }
            uint raw = unchecked(status.EtaFrame - status.OriginFrame);
            entry.RawFrames = raw;
            entry.TheoryFrames = raw;
            entry.Ready = true;
            uint prep = m_Runtime.m_DispatchCache.ReadPrep(line);
            float legacy = m_Runtime.m_DispatchCache.Read(line);
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[SpawnLead] line=" + line.Index + " source=rail-eta-theory"
                    + " prepRawMaxFrames=" + prep + " prepUsedFrames=" + prep
                    + " theoryRawFrames=" + raw + " theoryUsedFrames=" + raw
                    + " spawnLeadFrames=" + (prep + raw) + " legacyFrames=" + legacy.ToString("F0"));
            }
        }

        private bool TryFacts(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out Entry entry, out string failure)
        {
            entry = null;
            failure = string.Empty;
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line))
            {
                failure = "TheoryLineMissing";
                return false;
            }
            if (waypoints.Length == 0)
            {
                failure = "TheoryWaypointMissing";
                return false;
            }
            if (!m_Runtime.EntityManager.HasBuffer<VehicleModel>(line))
            {
                failure = "TheoryVehicleModelsMissing";
                return false;
            }
            DynamicBuffer<VehicleModel> models = m_Runtime.EntityManager.GetBuffer<VehicleModel>(line, true);
            Entity model = models.Length > 0 ? models[0].m_PrimaryPrefab : Entity.Null;
            Entity secondary = models.Length > 0 ? models[0].m_SecondaryPrefab : Entity.Null;
            Entity waypoint = waypoints[0].m_Waypoint;
            if (model == Entity.Null || !m_Runtime.EntityManager.Exists(model))
            {
                failure = "TheoryVehicleModelMissing";
                return false;
            }
            if (waypoint == Entity.Null || !m_Runtime.EntityManager.Exists(waypoint))
            {
                failure = "TheoryWaypointMissing";
                return false;
            }
            Entity depot = m_Runtime.GetDepot(line);
            ulong signature = 1469598103934665603UL;
            signature = Mix(signature, line); signature = Mix(signature, depot);
            signature = Mix(signature, waypoint); signature = Mix(signature, model);
            signature = Mix(signature, secondary);
            signature = Mix(signature, m_Runtime.m_RailEtaService?.HotBuildId);
            if (m_Runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
                signature ^= chain.Signature;
            entry = new Entry
            {
                Signature = signature,
                Depot = depot,
                Waypoint = waypoint,
                Model = model,
                SecondaryModel = secondary
            };
            return true;
        }

        private void Invalidate(Entity line, string reason)
        {
            bool removed = m_Entries.Remove(line);
            if (m_ActiveLine == line)
            {
                m_Runtime.m_RailEtaService?.Cancel(m_ActiveTicket);
                m_ActiveLine = Entity.Null;
                m_ActiveTicket = default;
            }
            if (removed || !m_FactFailures.TryGetValue(line, out string previous) || previous != reason)
            {
                m_FactFailures[line] = reason;
                LogFailure(line, reason);
            }
        }

        private void Enqueue(Entity line)
        {
            if (m_Queued.Add(line)) m_Queue.Enqueue(line);
        }

        private void LogFailure(Entity line, string reason)
        {
            float legacy = m_Runtime.m_DispatchCache.Read(line);
            m_Runtime.log.Info("[SpawnLead] line=" + line.Index + " source=rail-eta-theory failure=" + reason
                + " fallback=" + FallbackSource(legacy) + " legacyFrames=" + legacy.ToString("F0"));
        }

        private static string FallbackSource(float legacy) => legacy > 0f ? "legacy-dispatch-cache" : "lap-duration-fallback";

        private static bool Terminal(string state)
        {
            return state == "Completed" || state == "Failed" || state == "Cancelled" || state == "Busy"
                || state == "WorkerLost" || state == "Unavailable" || state == "NotConverged"
                || state == "ClockChanged";
        }

        private void OnClockChanged(ClockSnapshot oldClockSnapshot, ClockSnapshot newClockSnapshot)
        {
            _ = oldClockSnapshot;
            _ = newClockSnapshot;
            if (m_ActiveLine == Entity.Null || !m_ActiveTicket.IsValid) return;
            Entity line = m_ActiveLine;
            m_Runtime.m_RailEtaService?.Cancel(m_ActiveTicket);
            m_ActiveLine = Entity.Null;
            m_ActiveTicket = default;
            if (m_Entries.TryGetValue(line, out Entry entry) && !entry.Ready) Enqueue(line);
        }

        private static long Pack(Entity value) => ((long)(uint)value.Index << 32) | (uint)value.Version;

        private bool IsRailLine(Entity line)
        {
            return line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(m_Runtime.EntityManager, line)).Lifecycle == LifecycleKind.Rail;
        }

        private static ulong Mix(ulong hash, Entity value)
        {
            hash ^= (uint)value.Index;
            hash *= 1099511628211UL;
            hash ^= (uint)value.Version;
            return hash * 1099511628211UL;
        }

        private static ulong Mix(ulong hash, string value)
        {
            if (string.IsNullOrEmpty(value)) return hash;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
