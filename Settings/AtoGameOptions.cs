using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Localization;
using RapidTransitMod.Broadcasting;

namespace RapidTransitMod.Settings
{
    /// <summary>
    /// 注册到游戏原版“设置 → 模组”页面的玩家全局选项。
    /// 这不是工作台设置，不是调度功能配置，也不写入存档运行时缓存。
    /// </summary>
    [FileLocation("ModsSettings\\AdvancedTransitOperations\\AtoGameOptions")]
    [SettingsUITabOrder("General", "BroadcastAudio")]
    [SettingsUIGroupOrder("SelectionPanel", "ScheduleAdvanced", "BroadcastHint", "BroadcastAction", "BroadcastFiles")]
    public sealed class AtoGameOptions : ModSetting
    {
        public AtoGameOptions(IMod mod)
            : base(mod)
        {
        }

        [SettingsUISection("General", "SelectionPanel")]
        public bool AutoOpenSelectionPanel { get; set; } = true;

        private bool m_AllowOneMinuteLimits = false;

        [SettingsUISection("General", "ScheduleAdvanced")]
        public bool AllowOneMinuteLimits
        {
            get => m_AllowOneMinuteLimits;
            set
            {
                if (m_AllowOneMinuteLimits == value)
                    return;
                m_AllowOneMinuteLimits = value;
                Colossal.Core.MainThreadDispatcher.RunOnMainThread(() =>
                {
                    if (GameManager.instance?.gameMode != GameMode.Game)
                        return;
                    Workbenches.UiEvents.Push(new DispatchWorkbenchCatalogEvent
                    {
                        rulesOnly = true,
                        minimumScheduleMinutes = ScheduleLimitPolicy.EditMinimum
                    });
                });
            }
        }

        [SettingsUISection("BroadcastAudio", "BroadcastHint")]
        [SettingsUIMultilineText]
        public string BroadcastAudioHint => string.Empty;

        [SettingsUISection("BroadcastAudio", "BroadcastAction")]
        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(AtoGameOptions), nameof(HideAudioDelete))]
        [SettingsUIDisableByCondition(typeof(AtoGameOptions), nameof(DisableAudioDelete))]
        public bool DeleteUnregisteredAudio
        {
            set
            {
                AssetLifecycle lifecycle = AssetLifecycle.Instance;
                lifecycle?.Unregistered.Delete(lifecycle);
            }
        }

        [SettingsUISection("BroadcastAudio", "BroadcastFiles")]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(AtoGameOptions), nameof(GetUnregisteredAudio))]
        public string UnregisteredAudio => string.Empty;

        public LocalizedString GetUnregisteredAudio()
        {
            AssetLifecycle lifecycle = AssetLifecycle.Instance;
            return lifecycle == null ? LocalizedString.Id("RapidTransit.BroadcastAudio.Querying") : lifecycle.Unregistered.Text(lifecycle);
        }

        public bool DisableAudioDelete()
        {
            AssetLifecycle lifecycle = AssetLifecycle.Instance;
            return lifecycle == null || lifecycle.Unregistered.Disabled(lifecycle);
        }

        public bool HideAudioDelete() => AssetLifecycle.Instance?.Unregistered.HideDelete ?? false;

        public override void SetDefaults()
        {
            AutoOpenSelectionPanel = true;
            AllowOneMinuteLimits = false;
        }
    }
}
