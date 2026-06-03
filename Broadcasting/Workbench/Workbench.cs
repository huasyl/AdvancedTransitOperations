using System;
using RapidTransitMod.Broadcasting;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class Workbench
    {
        private readonly Context m_Context;

        internal Workbench(WorkbenchAccess access)
        {
            m_Context = new Context(access ?? throw new ArgumentNullException(nameof(access)));
            RuntimeConfig = new RuntimeConfig(m_Context);
            Snapshot = new Snapshot(m_Context);
            Drafts = new Drafts(m_Context);
            Bindings = new Bindings(m_Context);
            Rules = new Rules(m_Context);
            Platforms = new Platforms(m_Context);
            Assets = new Assets(m_Context);
            Preview = new Preview(m_Context);
            Persistence = new Persistence(m_Context);
            Conflicts = new Conflicts(m_Context);
            m_Context.Workbench = this;
            m_Context.Snapshot = Snapshot;
            m_Context.Drafts = Drafts;
            m_Context.Bindings = Bindings;
            m_Context.Rules = Rules;
            m_Context.Platforms = Platforms;
            m_Context.Assets = Assets;
            m_Context.Preview = Preview;
            m_Context.Persistence = Persistence;
            m_Context.Conflicts = Conflicts;
        }

        internal State State => m_Context.State;
        internal RuntimeConfig RuntimeConfig { get; }
        internal Snapshot Snapshot { get; }
        internal Drafts Drafts { get; }
        internal Bindings Bindings { get; }
        internal Rules Rules { get; }
        internal Platforms Platforms { get; }
        internal Assets Assets { get; }
        internal Preview Preview { get; }
        internal Persistence Persistence { get; }
        internal Conflicts Conflicts { get; }

        internal void Attach(Runtime runtime) => m_Context.Attach(runtime);

        internal void StopPreview() => Preview.Stop();

        public string LoadBroadcastWorkbenchSnapshotJson(string preferredLineId)
            => Snapshot.LoadBroadcastWorkbenchSnapshotJson(preferredLineId);

        public string RefreshBroadcastWorkbenchSnapshotJson(string preferredLineId)
            => Snapshot.RefreshBroadcastWorkbenchSnapshotJson(preferredLineId);

        public string LoadBroadcastBindingSlotHintsJson(string lineId)
            => Bindings.LoadBroadcastBindingSlotHintsJson(lineId);

        public string LoadBroadcastAssetBrowserJson(string requestedPath)
            => Assets.LoadBroadcastAssetBrowserJson(requestedPath);

        public string SaveBroadcastRulesJson(string requestJson)
            => Rules.SaveBroadcastRulesJson(requestJson);

        public string SaveBroadcastPlatformAnnouncementJson(string requestJson)
            => Platforms.SaveBroadcastPlatformAnnouncementJson(requestJson);

        public string CopyBroadcastPlatformAnnouncementToAllStationsJson(string requestJson)
            => Platforms.CopyBroadcastPlatformAnnouncementToAllStationsJson(requestJson);

        public string ImportBroadcastExternalAssetsJson(string requestJson)
            => Assets.ImportBroadcastExternalAssetsJson(requestJson);

        public string SaveBroadcastStationBindingJson(string requestJson)
            => Bindings.SaveBroadcastStationBindingJson(requestJson);

        public string SaveBroadcastStationBindingsJson(string requestJson)
            => Bindings.SaveBroadcastStationBindingsJson(requestJson);

        public string DeleteBroadcastAssetJson(string requestJson)
            => Assets.DeleteBroadcastAssetJson(requestJson);

        public string DeleteAllBroadcastAssetsJson()
            => Assets.DeleteAllBroadcastAssetsJson();

        public string AutoBindBroadcastStationMappingsJson(string requestJson)
            => Conflicts.AutoBindBroadcastStationMappingsJson(requestJson);

        public string ApplyBroadcastConfigJson(string requestJson)
            => Drafts.ApplyBroadcastConfigJson(requestJson);

        public string OpenBroadcastAssetDirectoryPickerJson()
            => Assets.OpenBroadcastAssetDirectoryPickerJson();

        public string PlayBroadcastAssetPreviewJson(string assetName)
            => Preview.PlayBroadcastAssetPreviewJson(assetName);

        public string PlayBroadcastRulePreviewJson(string requestJson)
            => Preview.PlayBroadcastRulePreviewJson(requestJson);

        public string StopBroadcastAssetPreviewJson(string assetName)
            => Preview.StopBroadcastAssetPreviewJson(assetName);

        public string StopBroadcastRulePreviewJson(string ruleId)
            => Preview.StopBroadcastRulePreviewJson(ruleId);

        public string SetBroadcastPreviewVolumeJson(string volumeJson)
            => Preview.SetBroadcastPreviewVolumeJson(volumeJson);
    }
}
