#if RT_DEBUG_TOOLS
using Game;

namespace RapidTransitMod.Dispatch.Diagnostics
{
    // 仅处理只读探针请求；不运行调度阶段，也不推进追踪采样帧。
    internal sealed partial class RuntimeProbeSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            ModRuntimeHostSystem.Instance?.PollProbeRequests();
        }
    }
}
#endif
