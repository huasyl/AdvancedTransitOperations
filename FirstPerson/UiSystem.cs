using Colossal.UI.Binding;
using Game;
using Game.UI;
using RapidTransitMod.Settings;
using UnityEngine.Scripting;

namespace RapidTransitMod.FirstPerson
{
    public partial class FirstPersonUISystem : UISystemBase
    {
        private ValueBinding<string> m_Data;
        private string m_LastJson = "";
        public override GameMode gameMode => GameMode.Game;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(m_Data = new ValueBinding<string>("ATOFirstPerson", "dataJson", ""));
            AddUpdateBinding(new GetterValueBinding<string>("ATOFirstPerson", "place",
                () => Mod.Options.FirstPersonPosition == FirstPersonPlace.Left ? "left" : "center"));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();
            ModRuntimeHostSystem runtime = ModRuntimeHostSystem.Instance;
            string json = Mod.Options.ShowFirstPersonInfo && runtime != null && runtime.m_SystemReady
                ? runtime.m_FirstPersonQuery?.ReadJson() ?? "" : "";
            if (json == m_LastJson)
                return;
            m_LastJson = json;
            m_Data.Update(json);
        }

        [Preserve]
        protected override void OnStopRunning()
        {
            m_LastJson = "";
            m_Data.Update("");
            base.OnStopRunning();
        }
    }
}
