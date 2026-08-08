using Game;
using Game.Common;
using Game.Net;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace RapidTransitMod
{
    public partial class DevSightRaycastCollectorSystem : GameSystemBase
    {
        private RaycastSystem m_RaycastSystem = null!;
        private CameraUpdateSystem m_CameraUpdateSystem = null!;
        private static bool s_Enabled;

        public static void SetEnabled(bool enabled)
        {
            s_Enabled = enabled;
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_RaycastSystem = World.GetOrCreateSystemManaged<RaycastSystem>();
            m_CameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!s_Enabled)
                return;

            if (!m_CameraUpdateSystem.TryGetViewer(out var viewer))
                return;

            RaycastInput input = new RaycastInput
            {
                m_Line = ToolRaycastSystem.CalculateRaycastLine(viewer.camera),
                m_TypeMask = TypeMask.StaticObjects | TypeMask.Net | TypeMask.Areas | TypeMask.Lanes,
                m_Flags = RaycastFlags.Markers | RaycastFlags.Decals,
                m_CollisionMask = CollisionMask.OnGround | CollisionMask.Overground | CollisionMask.ExclusiveGround,
                m_NetLayerMask = Layer.TrainTrack | Layer.TramTrack
            };
            m_RaycastSystem.AddInput(this, input);
        }
    }
}
