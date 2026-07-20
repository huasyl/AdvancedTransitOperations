using System;
using System.Collections.Generic;
using Game;
using Game.Pathfind;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.RailTravel
{
    internal sealed partial class QuerySystem : GameSystemBase
    {
        private const uint DefaultTimeoutFrames = 512;
        private const uint CompletedKeepFrames = 256;
        private PathfindSetupSystem m_PathfindSetupSystem;
        private readonly Dictionary<string, State> m_Requests = new Dictionary<string, State>(StringComparer.Ordinal);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PathfindSetupSystem = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
        }

        protected override void OnUpdate()
        {
            Cleanup();
        }

        internal string Start(
            PathfindParameters parameters,
            SetupQueueTarget origin,
            SetupQueueTarget destination,
            uint timeoutFrames = DefaultTimeoutFrames,
            int maxDelayFrames = 64,
            int spreadFrames = 0)
        {
            string id = "rail-travel-" + Guid.NewGuid().ToString("N");
            // Temporary owner only receives vanilla path results; it is not a vehicle.
            Entity owner = EntityManager.CreateEntity();
            EntityManager.AddComponentData(owner, new PathInformation
            {
                m_State = PathFlags.Pending
            });
            EntityManager.AddBuffer<PathElement>(owner);

            NativeQueue<SetupQueueItem> queue = m_PathfindSetupSystem.GetQueue(this, maxDelayFrames, spreadFrames);
            queue.Enqueue(new SetupQueueItem(owner, parameters, origin, destination));

            uint frame = GetFrame();
            m_Requests[id] = new State
            {
                Id = id,
                Owner = owner,
                CreatedFrame = frame,
                TimeoutFrame = frame + Math.Max(1u, timeoutFrames),
                StateText = "pending"
            };
            return id;
        }

        internal bool TryGetResult(string id, out QueryResult result, bool cleanupOwner = true)
        {
            return TryGetResult(id, false, out result, cleanupOwner);
        }

        internal bool TryGetTheoryDepotResult(string id, out QueryResult result, bool cleanupOwner = true)
        {
            return TryGetResult(id, true, out result, cleanupOwner);
        }

        private bool TryGetResult(string id, bool theoryDepot, out QueryResult result, bool cleanupOwner)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(id) || !m_Requests.TryGetValue(id, out State state))
                return false;

            if (!EntityManager.Exists(state.Owner))
            {
                result = QueryResult.Failed(id, "rail-travel-query-owner-missing");
                m_Requests.Remove(id);
                return true;
            }

            PathInformation info = EntityManager.GetComponentData<PathInformation>(state.Owner);
            if ((info.m_State & PathFlags.Pending) != 0)
            {
                result = QueryResult.Pending(id);
                return true;
            }

            Path path = null;
            string error = string.Empty;
            bool rawSuccess = info.m_Origin != Entity.Null
                && info.m_Destination != Entity.Null
                && !float.IsNaN(info.m_TotalCost)
                && !float.IsInfinity(info.m_TotalCost)
                && info.m_TotalCost >= 0f
                && EntityManager.HasBuffer<PathElement>(state.Owner)
                && EntityManager.GetBuffer<PathElement>(state.Owner, true).Length != 0;
            bool projectionSuccess = theoryDepot
                ? new PathQuery(EntityManager).TryBuildTheoryDepot(state.Owner, out path)
                : new PathQuery(EntityManager).TryBuild(state.Owner, out path);
            bool success = theoryDepot ? rawSuccess : projectionSuccess;
            if (theoryDepot && !rawSuccess)
                error = "rail-travel-query-no-path";
            else if (!projectionSuccess)
                error = theoryDepot
                    ? "rail-travel-query-theory-depot-projection-failed"
                    : "rail-travel-query-path-empty";

            result = new QueryResult
            {
                Id = id,
                State = success ? "completed" : "failed",
                Success = success,
                ProjectionSuccess = projectionSuccess,
                Error = error,
                Owner = state.Owner,
                Information = info,
                Path = path
            };

            state.StateText = result.State;
            state.CompletedFrame = GetFrame();
            m_Requests[id] = state;

            if (cleanupOwner)
            {
                DestroyOwner(state.Owner);
                m_Requests.Remove(id);
            }

            return true;
        }

        internal bool Cancel(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !m_Requests.TryGetValue(id, out State state))
                return false;

            DestroyOwner(state.Owner);
            m_Requests.Remove(id);
            return true;
        }

        private void Cleanup()
        {
            if (m_Requests.Count == 0)
                return;

            uint frame = GetFrame();
            List<string> remove = null;
            foreach (KeyValuePair<string, State> entry in m_Requests)
            {
                State state = entry.Value;
                if (!EntityManager.Exists(state.Owner))
                {
                    (remove ?? (remove = new List<string>())).Add(entry.Key);
                    continue;
                }

                if (frame >= state.TimeoutFrame)
                {
                    DestroyOwner(state.Owner);
                    (remove ?? (remove = new List<string>())).Add(entry.Key);
                    continue;
                }

                if (state.CompletedFrame != 0 && frame - state.CompletedFrame > CompletedKeepFrames)
                {
                    DestroyOwner(state.Owner);
                    (remove ?? (remove = new List<string>())).Add(entry.Key);
                }
            }

            if (remove == null)
                return;

            for (int i = 0; i < remove.Count; i++)
                m_Requests.Remove(remove[i]);
        }

        private uint GetFrame()
        {
            return World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex;
        }

        private void DestroyOwner(Entity owner)
        {
            if (owner != Entity.Null && EntityManager.Exists(owner))
                EntityManager.DestroyEntity(owner);
        }

        private struct State
        {
            public string Id;
            public Entity Owner;
            public uint CreatedFrame;
            public uint TimeoutFrame;
            public uint CompletedFrame;
            public string StateText;
        }
    }

    internal struct QueryResult
    {
        public string Id { get; set; }
        public string State { get; set; }
        public bool Success { get; set; }
        public bool ProjectionSuccess { get; set; }
        public string Error { get; set; }
        public Entity Owner { get; set; }
        public PathInformation Information { get; set; }
        public Path Path { get; set; }

        public static QueryResult Pending(string id)
        {
            return new QueryResult
            {
                Id = id ?? string.Empty,
                State = "pending",
                Success = false,
                Error = string.Empty
            };
        }

        public static QueryResult Failed(string id, string error)
        {
            return new QueryResult
            {
                Id = id ?? string.Empty,
                State = "failed",
                Success = false,
                Error = error ?? string.Empty
            };
        }
    }
}
