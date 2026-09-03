using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace RapidTransitMod.Settings
{
    /// <summary>
    /// 注册到游戏原版“设置 → 模组”页面的玩家全局选项。
    /// 这不是工作台设置，不是调度功能配置，也不写入存档运行时缓存。
    /// </summary>
    [FileLocation("ModsSettings\\AdvancedTransitOperations\\GameOptions")]
    public sealed class GameOptions : ModSetting
    {
        public GameOptions(IMod mod)
            : base(mod)
        {
        }

        [SettingsUISection("General", "SelectionPanel")]
        public bool AutoOpenSelectionPanel { get; set; } = true;

        public override void SetDefaults()
        {
            AutoOpenSelectionPanel = true;
        }
    }
}
