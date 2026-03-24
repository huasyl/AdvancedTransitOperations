using Colossal.UI.Binding;
using Game;
using Game.UI;
using UnityEngine.Scripting;

namespace RapidTransitMod
{
    public class DispatchWorkbenchUISystem : UISystemBase
    {
        private const string kGroup = "DispatchWorkbench";

        private static DispatchWorkbenchUISystem s_Instance;

        private ValueBinding<string> m_SnapshotJsonBinding = null!;
        private ValueBinding<string> m_SaveResultJsonBinding = null!;
        private ValueBinding<string> m_SourceModeBinding = null!;

        public override GameMode gameMode => GameMode.Game;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            s_Instance = this;

            AddBinding(m_SnapshotJsonBinding = new ValueBinding<string>(kGroup, "snapshotJson", string.Empty));
            AddBinding(m_SaveResultJsonBinding = new ValueBinding<string>(kGroup, "saveResultJson", string.Empty));
            AddBinding(m_SourceModeBinding = new ValueBinding<string>(kGroup, "sourceMode", "mock"));

            AddBinding(new TriggerBinding(kGroup, "refreshSnapshot", RefreshSnapshot));
            AddBinding(new TriggerBinding<string>(kGroup, "saveWorkbenchDraft", SaveWorkbenchDraft));
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (s_Instance == this)
            {
                s_Instance = null;
            }
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();
        }

        private void RefreshSnapshot()
        {
            string snapshotJson = DepartureControlSystem.Instance?.RefreshWorkbenchSnapshotJson() ?? string.Empty;
            PublishSnapshotJson(snapshotJson);
        }

        private void SaveWorkbenchDraft(string requestJson)
        {
            string resultJson = DepartureControlSystem.Instance?.SaveWorkbenchDraftJson(requestJson) ?? string.Empty;
            PublishSaveResultJson(resultJson);
        }

        internal static void PublishSnapshotJson(string snapshotJson)
        {
            if (s_Instance == null)
            {
                return;
            }

            s_Instance.m_SnapshotJsonBinding.Update(snapshotJson ?? string.Empty);
            s_Instance.m_SourceModeBinding.Update("backend");
        }

        internal static void PublishSaveResultJson(string resultJson)
        {
            if (s_Instance == null)
            {
                return;
            }

            s_Instance.m_SaveResultJsonBinding.Update(resultJson ?? string.Empty);
            s_Instance.m_SourceModeBinding.Update("backend");
        }
    }
}
