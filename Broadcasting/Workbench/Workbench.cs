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
            Apply = new Apply(m_Context);
            SaveOperations = new SaveOperations(m_Context);
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
            m_Context.Apply = Apply;
            m_Context.SaveOperations = SaveOperations;
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
        internal Apply Apply { get; }
        internal SaveOperations SaveOperations { get; }

        internal void Attach(Runtime runtime) => m_Context.Attach(runtime);

        internal void Reset() => SaveOperations.Reset();

        internal void StopPreview() => Preview.Stop();

        public string LoadBroadcastWorkbenchSnapshotJson(string requestJson)
            => Snapshot.LoadBroadcastWorkbenchSnapshotJson(requestJson);

        public string RefreshBroadcastWorkbenchSnapshotJson(string requestJson)
            => Snapshot.RefreshBroadcastWorkbenchSnapshotJson(requestJson);

        public string LoadBroadcastBindingSlotHintsJson(string requestJson)
            => Bindings.LoadBroadcastBindingSlotHintsJson(requestJson);

        public string LoadBroadcastAssetBrowserJson(string requestJson)
            => Assets.LoadBroadcastAssetBrowserJson(requestJson);

        public string SaveBroadcastRulesJson(string requestJson)
            => DisabledSaveRulesResult();

        public string SaveBroadcastPlatformAnnouncementJson(string requestJson)
            => DisabledSavePlatformResult();

        public string CopyBroadcastPlatformAnnouncementToAllStationsJson(string requestJson)
            => DisabledSavePlatformResult();

        public string ImportBroadcastExternalAssetsJson(string requestJson)
            => Assets.ImportBroadcastExternalAssetsJson(requestJson);

        public string SaveBroadcastStationBindingJson(string requestJson)
            => DisabledSaveBindingResult();

        public string SaveBroadcastStationBindingsJson(string requestJson)
            => DisabledSaveBindingResult();

        public string DeleteBroadcastAssetJson(string requestJson)
            => Assets.DeleteBroadcastAssetJson(requestJson);

        public string DeleteAllBroadcastAssetsJson(string requestJson)
            => Assets.DeleteAllBroadcastAssetsJson(requestJson);

        public string AutoBindBroadcastStationMappingsJson(string requestJson)
            => global::RapidTransitMod.Workbenches.Json.Write(new BroadcastWorkbenchAutoBindStationMappingsResult
            {
                success = false,
                boundCount = 0,
                error = "broadcast-backend-draft-disabled"
            });

        public string ApplyBroadcastConfigJson(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "applyBroadcastConfig", allowLegacyDefault: true);
            return global::RapidTransitMod.Workbenches.Json.Write(new BroadcastWorkbenchApplyResult
            {
                mode = scope.Token,
                success = false,
                error = "broadcast-backend-draft-disabled",
                snapshot = null
            });
        }

        public string OpenBroadcastAssetDirectoryPickerJson(string requestJson)
            => Assets.OpenBroadcastAssetDirectoryPickerJson(requestJson);

        public string PlayBroadcastAssetPreviewJson(string requestJson)
            => Preview.PlayBroadcastAssetPreviewJson(requestJson);

        public string PlayBroadcastRulePreviewJson(string requestJson)
            => Preview.PlayBroadcastRulePreviewJson(requestJson);

        public string StopBroadcastAssetPreviewJson(string requestJson)
            => Preview.StopBroadcastAssetPreviewJson(requestJson);

        public string StopBroadcastRulePreviewJson(string requestJson)
            => Preview.StopBroadcastRulePreviewJson(requestJson);

        public string SetBroadcastPreviewVolumeJson(string volumeJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(volumeJson, "setBroadcastPreviewVolume", allowLegacyDefault: true);
            int draftVolume = State.GetDraftVolume(scope);
            int appliedVolume = State.GetAppliedVolume(scope);
            return global::RapidTransitMod.Workbenches.Json.Write(new BroadcastWorkbenchVolumeResult
            {
                success = false,
                error = "broadcast-backend-draft-disabled",
                volume = draftVolume,
                volumeDirty = draftVolume != appliedVolume,
                snapshot = null
            });
        }

        public string StartBroadcastApplyOperationJson(string requestJson)
            => SaveOperations.Start(requestJson);

        public string GetBroadcastApplyOperationStatusJson(string operationId)
            => SaveOperations.Status(operationId);

        private static string DisabledSaveBindingResult()
            => global::RapidTransitMod.Workbenches.Json.Write(new BroadcastWorkbenchSaveStationBindingResult
            {
                success = false,
                error = "broadcast-backend-draft-disabled"
            });

        private static string DisabledSaveRulesResult()
            => global::RapidTransitMod.Workbenches.Json.Write(new BroadcastWorkbenchSaveRulesResult
            {
                success = false,
                error = "broadcast-backend-draft-disabled"
            });

        private static string DisabledSavePlatformResult()
            => global::RapidTransitMod.Workbenches.Json.Write(new BroadcastWorkbenchSavePlatformAnnouncementResult
            {
                success = false,
                error = "broadcast-backend-draft-disabled",
                snapshot = null
            });
    }
}
