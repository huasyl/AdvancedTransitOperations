using System;
using Game;
using Game.Common;
using Game.Objects;
using Game.Tools;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed partial class LineServiceChangeSourceSystem : GameSystemBase
    {
        private EntityQuery m_UpdatedLines;
        private Action<Entity> m_Observe;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_UpdatedLines = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<Updated>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
            RequireForUpdate(m_UpdatedLines);
        }

        internal void Bind(Action<Entity> observe)
        {
            m_Observe = observe;
        }

        internal void Unbind()
        {
            m_Observe = null;
        }

        protected override void OnUpdate()
        {
            if (m_Observe == null)
                return;
            m_UpdatedLines.CompleteDependency();
            NativeArray<Entity> lines = m_UpdatedLines.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                    m_Observe(lines[i]);
            }
            finally
            {
                if (lines.IsCreated)
                    lines.Dispose();
            }
        }
    }
}
