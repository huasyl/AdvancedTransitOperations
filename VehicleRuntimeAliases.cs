using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        // Transitional runtime-side aliases only.
        // Low-risk read paths should use m_VehicleView instead of these old field names.
        private VehicleRuntimeStateStore.MapRef<VehicleState> m_VehicleState => m_VehicleRuntime.State;
        private VehicleRuntimeStateStore.MapRef<int> m_VehicleTargetMin => m_VehicleRuntime.TargetMin;
        private VehicleRuntimeStateStore.MapRef<int> m_VehicleCurrentSlot => m_VehicleRuntime.CurrentSlot;
        private VehicleRuntimeStateStore.MapRef<Entity> m_VehicleLine => m_VehicleRuntime.Line;
        private VehicleRuntimeStateStore.MapRef<uint> m_VehicleIdleStartFrame => m_VehicleRuntime.IdleStartFrame;
        private VehicleRuntimeStateStore.MapRef<uint> m_VehiclePreparingStartFrame => m_VehicleRuntime.PreparingStartFrame;
        private VehicleRuntimeStateStore.MapRef<uint> m_VehicleLastLaunchFrame => m_VehicleRuntime.LastLaunchFrame;
        private VehicleRuntimeStateStore.MapRef<uint> m_LaunchCooldownUntil => m_VehicleRuntime.LaunchCooldownUntil;
        private VehicleRuntimeStateStore.MapRef<uint> m_VehicleDispatchRequestStartFrame => m_VehicleRuntime.DispatchRequestStartFrame;
        private VehicleRuntimeStateStore.SetRef m_NearingTerminus => m_VehicleRuntime.NearingTerminus;
        private VehicleRuntimeStateStore.MapRef<uint> m_OriginArrivalCandidateSinceFrame => m_VehicleRuntime.OriginArrivalCandidateSinceFrame;
        private VehicleRuntimeStateStore.MapRef<uint> m_ForcedOriginReadyFrame => m_VehicleRuntime.ForcedOriginReadyFrame;
        private VehicleRuntimeStateStore.MapRef<uint> m_ForcedOriginBoardingGraceUntil => m_VehicleRuntime.ForcedOriginBoardingGraceUntil;
    }
}
