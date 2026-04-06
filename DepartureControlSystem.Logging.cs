using System.Collections.Generic;
using Game.Common;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private readonly Dictionary<Entity, string> m_YieldSkipLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastSpawnTriggerSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastVehicleRegisterSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastHoldingSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastDispatchSampleSummary = new Dictionary<Entity, string>();

        private void ClearDispatchLogCaches()
        {
            m_BypassDecisionLogCache.Clear();
            m_PreparingSlotLogCache.Clear();
            m_HoldingSkipLogCache.Clear();
            m_LateDispatchLogCache.Clear();
            m_YieldSkipLogCache.Clear();
        }

        public string GetCurrentGameTimeLabel()
        {
            if (m_TimeSystem == null) return string.Empty;
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            if (nowMin < 0) nowMin += 1440;
            return "[游戏时间 " + SlotStr(nowMin) + "]";
        }

        private void ClearLineDispatchDebugSummaries()
        {
            m_LineLastSpawnTriggerSummary.Clear();
            m_LineLastVehicleRegisterSummary.Clear();
            m_LineLastHoldingSummary.Clear();
            m_LineLastDispatchSampleSummary.Clear();
        }

        private void RecordLineSpawnTriggerSummary(Entity line, int nowMin, int slot, int actualCount)
        {
            if (line == Entity.Null)
                return;

            m_LineLastSpawnTriggerSummary[line] = SlotStr(nowMin)
                + " 班次" + SlotStr(slot)
                + " 真实产车命令 当前=" + actualCount;
        }

        private void RecordLineVehicleRegisterSummary(Entity line, int nowMin, Entity vehicle, VehicleState finalState)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            string depotSummary = DescribeVehicleOwnerDepot(vehicle);
            m_LineLastVehicleRegisterSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 注册 -> " + finalState
                + " depot=" + depotSummary;
        }

        private void RecordLineHoldingSummary(Entity line, int nowMin, Entity vehicle, int targetMin)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            m_LineLastHoldingSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 到站/Holding"
                + (targetMin >= 0 ? " " + SlotStr(targetMin) : " 等待调度");
        }

        private void RecordLineDispatchSampleSummary(Entity line, int nowMin, Entity vehicle, float sampleMinutes)
        {
            if (line == Entity.Null || vehicle == Entity.Null || sampleMinutes <= 0f)
                return;

            m_LineLastDispatchSampleSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 出库用时=" + sampleMinutes.ToString("F1") + "分钟";
        }

        private string DescribeVehicleOwnerDepot(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasComponent<Owner>(vehicle))
            {
                return "-";
            }

            Entity depot = EntityManager.GetComponentData<Owner>(vehicle).m_Owner;
            if (depot == Entity.Null || !EntityManager.Exists(depot))
                return "-";

            string name = m_NameSystem.GetRenderedLabelName(depot);
            return string.IsNullOrEmpty(name)
                ? "#" + depot.Index
                : ("#" + depot.Index + "[" + name + "]");
        }
    }
}
