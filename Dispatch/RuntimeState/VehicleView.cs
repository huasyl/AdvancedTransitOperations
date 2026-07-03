using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleView
    {
        private readonly VehicleStateStore m_Store;

        public VehicleView(VehicleStateStore store)
        {
            m_Store = store;
        }

        public int Count => m_Store.State.Count;

        public bool Contains(Entity vehicle) => m_Store.State.ContainsKey(vehicle);

        public VehicleState GetState(Entity vehicle) => m_Store.State[vehicle];

        public NativeArray<Entity> Keys(Allocator allocator) => m_Store.State.GetKeyArray(allocator);

        public bool TryGetState(Entity vehicle, out VehicleState state) => m_Store.State.TryGetValue(vehicle, out state);

        public bool TryGetTarget(Entity vehicle, out int targetMin) => m_Store.TargetMin.TryGetValue(vehicle, out targetMin);

        public bool TryGetSlot(Entity vehicle, out int slot) => m_Store.CurrentSlot.TryGetValue(vehicle, out slot);

        public bool TryGetLine(Entity vehicle, out Entity line) => m_Store.Line.TryGetValue(vehicle, out line);

        public bool TryGetIdle(Entity vehicle, out uint frame) => m_Store.IdleStartFrame.TryGetValue(vehicle, out frame);

        public bool TryGetPreparing(Entity vehicle, out uint frame) => m_Store.PreparingStartFrame.TryGetValue(vehicle, out frame);

        public bool TryGetLaunch(Entity vehicle, out uint frame) => m_Store.LastLaunchFrame.TryGetValue(vehicle, out frame);

        public bool TryGetCooldown(Entity vehicle, out uint frame) => m_Store.LaunchCooldownUntil.TryGetValue(vehicle, out frame);

        public bool TryGetDispatch(Entity vehicle, out uint frame) => m_Store.DispatchRequestStartFrame.TryGetValue(vehicle, out frame);

        public bool TryGetOrigin(Entity vehicle, out uint frame) => m_Store.OriginArrivalCandidateSinceFrame.TryGetValue(vehicle, out frame);

        public bool TryGetReady(Entity vehicle, out uint frame) => m_Store.ForcedOriginReadyFrame.TryGetValue(vehicle, out frame);

        public bool TryGetBoardingGrace(Entity vehicle, out uint frame) => m_Store.ForcedOriginBoardingGraceUntil.TryGetValue(vehicle, out frame);

        public bool IsInbound(Entity vehicle) => m_Store.NearingTerminus.Contains(vehicle);

        public bool IsFreshPreparing(Entity vehicle, uint nowFrame, uint graceFrames)
        {
            if (vehicle == Entity.Null || !TryGetDispatch(vehicle, out uint dispatchStartFrame))
                return false;

            return nowFrame >= dispatchStartFrame
                && (nowFrame - dispatchStartFrame) <= graceFrames;
        }
    }
}
