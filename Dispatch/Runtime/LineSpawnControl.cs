using System.Collections.Generic;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal enum SpawnOrderState : byte
    {
        None,
        Unclaimed,
        Claimed
    }

    internal readonly struct SpawnOrder
    {
        internal readonly int Id;
        internal readonly uint RequestFrame;
        internal readonly bool HasSlot;
        internal readonly SlotKey Slot;
        internal readonly Entity ClaimedBy;

        internal SpawnOrder(int id, uint requestFrame, bool hasSlot, SlotKey slot, Entity claimedBy = default)
        {
            Id = id;
            RequestFrame = requestFrame;
            HasSlot = hasSlot;
            Slot = slot;
            ClaimedBy = claimedBy;
        }
    }

    internal readonly struct SpawnClaim
    {
        internal readonly bool Found;
        internal readonly int Id;
        internal readonly uint RequestFrame;
        internal readonly bool HasSlot;
        internal readonly SlotKey Slot;

        internal SpawnClaim(SpawnOrder order, uint requestFrame)
        {
            Found = true;
            Id = order.Id;
            RequestFrame = requestFrame;
            HasSlot = order.HasSlot;
            Slot = order.Slot;
        }
    }

    internal sealed class LineSpawnControl
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, List<SpawnOrder>> m_Orders =
            new Dictionary<Entity, List<SpawnOrder>>();
        private readonly Dictionary<Entity, uint> m_LastClaimFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<int, Entity> m_OrderLines = new Dictionary<int, Entity>();
        private readonly List<Entity> m_ExpiredLines = new List<Entity>();
        private int m_NextId = 1;

        public LineSpawnControl(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;

        internal int AddForSlot(Entity line, SlotKey slot, uint frame)
        {
            List<SpawnOrder> orders = GetOrders(line);
            for (int i = 0; i < orders.Count; i++)
            {
                SpawnOrder order = orders[i];
                if (order.ClaimedBy == Entity.Null && order.HasSlot && order.Slot == slot)
                    return order.Id;
            }
            int id = m_NextId++;
            orders.Add(new SpawnOrder(id, frame, true, slot));
            m_OrderLines.Add(id, line);
            return id;
        }

        internal int AddWithoutSlot(Entity line, uint frame)
        {
            int id = m_NextId++;
            GetOrders(line).Add(new SpawnOrder(id, frame, false, default));
            m_OrderLines.Add(id, line);
            return id;
        }

        internal SpawnOrderState Query(int id, out Entity vehicle)
        {
            return Query(id, out vehicle, out _);
        }

        internal SpawnOrderState Query(int id, out Entity vehicle, out uint requestFrame)
        {
            vehicle = Entity.Null;
            requestFrame = 0u;
            if (!m_OrderLines.TryGetValue(id, out Entity line))
                return SpawnOrderState.None;
            List<SpawnOrder> orders = m_Orders[line];
            for (int i = 0; i < orders.Count; i++)
            {
                SpawnOrder order = orders[i];
                if (order.Id != id)
                    continue;
                vehicle = order.ClaimedBy;
                requestFrame = order.RequestFrame;
                return vehicle == Entity.Null ? SpawnOrderState.Unclaimed : SpawnOrderState.Claimed;
            }
            return SpawnOrderState.None;
        }

        internal void Cancel(int id)
        {
            if (!m_OrderLines.TryGetValue(id, out Entity line))
                return;
            List<SpawnOrder> orders = m_Orders[line];
            for (int i = 0; i < orders.Count; i++)
            {
                if (orders[i].Id != id)
                    continue;
                orders.RemoveAt(i);
                m_OrderLines.Remove(id);
                return;
            }
        }

        internal void CancelLine(Entity line)
        {
            if (!m_Orders.TryGetValue(line, out List<SpawnOrder> orders))
                return;
            for (int i = 0; i < orders.Count; i++)
                m_OrderLines.Remove(orders[i].Id);
            m_Orders.Remove(line);
        }

        internal SpawnClaim Claim(Entity line, Entity vehicle, uint nowFrame)
        {
            if (!m_Orders.TryGetValue(line, out List<SpawnOrder> orders))
                return default;
            for (int i = 0; i < orders.Count; i++)
            {
                SpawnOrder order = orders[i];
                if (order.ClaimedBy != Entity.Null)
                    continue;
                uint requestFrame = m_LastClaimFrame.TryGetValue(line, out uint lastClaim)
                    && unchecked((int)(order.RequestFrame - lastClaim)) < 0
                        ? lastClaim
                        : order.RequestFrame;
                m_LastClaimFrame[line] = nowFrame;
                if (order.HasSlot)
                    orders[i] = new SpawnOrder(order.Id, order.RequestFrame, true, order.Slot, vehicle);
                else
                {
                    orders.RemoveAt(i);
                    m_OrderLines.Remove(order.Id);
                }
                return new SpawnClaim(order, requestFrame);
            }
            return default;
        }

        internal int SpawnDeficit(Entity line)
        {
            int unclaimed = UnclaimedCount(line);
            if (unclaimed == 0)
                return 0;
            BufferLookup<RouteVehicle> vehicles = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            m_Runtime.m_LineVehicles.Count(line, vehicles, out int unregistered);
            return math.max(0, unclaimed - unregistered);
        }

        internal int DisplayTarget(Entity line)
        {
            int unclaimed = UnclaimedCount(line);
            if (unclaimed == 0)
                return 0;
            BufferLookup<RouteVehicle> vehicles = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            int current = m_Runtime.m_LineVehicles.Count(line, vehicles, out int unregistered);
            return current + math.max(0, unclaimed - unregistered);
        }

        internal void Recompute(Entity line)
        {
            if (line == Entity.Null || !EntityManager.Exists(line))
                return;
            BufferLookup<RouteVehicle> vehicles = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteModifier> modifiers = m_Runtime.GetBufferLookup<RouteModifier>(false);
            BufferLookup<RouteWaypoint> waypoints = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            ApplyVehicleIntervalForLine(line, vehicles, modifiers, waypoints);
        }

        internal void Recompute(IEnumerable<Entity> lines)
        {
            foreach (Entity line in lines)
                Recompute(line);
        }

        internal void Clear()
        {
            m_Orders.Clear();
            m_OrderLines.Clear();
            m_LastClaimFrame.Clear();
            m_ExpiredLines.Clear();
        }

        private List<SpawnOrder> GetOrders(Entity line)
        {
            if (!m_Orders.TryGetValue(line, out List<SpawnOrder> orders))
            {
                orders = new List<SpawnOrder>();
                m_Orders[line] = orders;
            }
            return orders;
        }

        private int UnclaimedCount(Entity line)
        {
            if (!m_Orders.TryGetValue(line, out List<SpawnOrder> orders))
                return 0;
            int count = 0;
            for (int i = 0; i < orders.Count; i++)
                if (orders[i].ClaimedBy == Entity.Null)
                    count++;
            return count;
        }

        public void Tick()
        {
            ApplyVehicleIntervals();
        }

        private void ApplyVehicleIntervals()
        {
            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteModifier> modBuffers = m_Runtime.GetBufferLookup<RouteModifier>(false);
            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);

            try
            {
                m_ExpiredLines.Clear();
                foreach (Entity line in m_Orders.Keys)
                    if (!EntityManager.Exists(line))
                        m_ExpiredLines.Add(line);
                foreach (Entity line in m_LastClaimFrame.Keys)
                    if (!EntityManager.Exists(line) && !m_ExpiredLines.Contains(line))
                        m_ExpiredLines.Add(line);
                for (int i = 0; i < m_ExpiredLines.Count; i++)
                {
                    Entity line = m_ExpiredLines[i];
                    CancelLine(line);
                    m_LastClaimFrame.Remove(line);
                }
                m_ExpiredLines.Clear();

                NativeArray<Entity> lineStableKeys = m_Runtime.m_LineProfile.StabilityKeys(Allocator.Temp);
                for (int i = 0; i < lineStableKeys.Length; i++)
                {
                    if (EntityManager.Exists(lineStableKeys[i])) continue;
                    m_Runtime.m_LineProfile.RemoveStability(lineStableKeys[i]);
                    m_Runtime.m_LineInitialAdopted.Remove(lineStableKeys[i]);
                    m_Runtime.TrackModel.InvalidateLine(lineStableKeys[i]);
                    m_Runtime.m_WaypointIndex.InvalidateLookup(lineStableKeys[i]);
                    m_Runtime.m_LineTimes.InvalidateLine(lineStableKeys[i]);
                }
                lineStableKeys.Dispose();

                foreach (Entity line in lines)
                    ApplyVehicleIntervalForLine(line, rvBuffers, modBuffers, wpBuffers);
            }
            finally { lines.Dispose(); }
        }

        private void ApplyVehicleIntervalForLine(
            Entity line,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteModifier> modBuffers,
            BufferLookup<RouteWaypoint> wpBuffers)
        {
            if (!EntityManager.Exists(line)) return;
            if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> wps) || wps.Length < 2) return;
            if (!m_Runtime.m_LineProfile.Stable(line)) return;
            m_Runtime.m_LineServiceState.EnsureObserved(line);
            if (!m_Runtime.m_LineServiceState.IsOperational(line)) return;
            if (!m_Runtime.m_LineView.Managed(line, m_Runtime.m_Features.Dispatch())) return;
            if (!EntityManager.HasComponent<PrefabRef>(line)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab)) return;
            EntityManager.CompleteDependencyBeforeRW<RouteModifier>();
            if (!modBuffers.TryGetBuffer(line, out DynamicBuffer<RouteModifier> mods)) return;

            TransportLineData lineData = EntityManager.GetComponentData<TransportLineData>(prefab);
            float iDefault = lineData.m_DefaultVehicleInterval;
            if (!TryGetVanillaStableDuration(line, wps, lineData, out float stableDuration))
                stableDuration = iDefault;
            if (stableDuration <= 0f) return;

            int actualCount = m_Runtime.m_LineVehicles.Count(line, rvBuffers, out int unregistered);
            int targetCount = actualCount + math.max(0, UnclaimedCount(line) - unregistered);

            float targetInterval = targetCount <= 0
                ? math.max(iDefault, stableDuration) * 64f
                : stableDuration / targetCount;

            InjectModifier(mods, targetInterval - iDefault);
        }

        private bool TryGetVanillaStableDuration(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            TransportLineData lineData,
            out float stableDuration)
        {
            stableDuration = 0f;
            if (!EntityManager.HasBuffer<RouteSegment>(line))
                return false;

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length < waypoints.Length)
                return false;

            int firstTimedWaypoint = 0;
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (!EntityManager.HasComponent<VehicleTiming>(waypoints[i].m_Waypoint))
                    continue;

                firstTimedWaypoint = i;
                break;
            }

            for (int i = 0; i < waypoints.Length; i++)
            {
                int segmentIndex = firstTimedWaypoint + i;
                if (segmentIndex >= waypoints.Length)
                    segmentIndex -= waypoints.Length;
                int waypointIndex = segmentIndex + 1;
                if (waypointIndex >= waypoints.Length)
                    waypointIndex -= waypoints.Length;

                Entity segment = segments[segmentIndex].m_Segment;
                if (EntityManager.HasComponent<PathInformation>(segment))
                    stableDuration += EntityManager.GetComponentData<PathInformation>(segment).m_Duration;

                if (EntityManager.HasComponent<VehicleTiming>(waypoints[waypointIndex].m_Waypoint))
                    stableDuration += lineData.m_StopDuration;
            }

            return stableDuration > 0f;
        }

        private static void InjectModifier(DynamicBuffer<RouteModifier> mods, float delta)
        {
            int idx = (int)RouteModifierType.VehicleInterval;
            while (mods.Length <= idx)
                mods.Add(new RouteModifier { m_Delta = float2.zero });
            RouteModifier modifier = mods[idx];
            modifier.m_Delta = new float2(delta, 0f);
            mods[idx] = modifier;
        }
    }
}
