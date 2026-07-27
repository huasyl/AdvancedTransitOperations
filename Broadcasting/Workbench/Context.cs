using System;
using System.Collections.Generic;
using Colossal.Core;
using Unity.Entities;
using RapidTransitMod.Broadcasting;

namespace RapidTransitMod.Broadcasting.WorkbenchBackend
{
    internal sealed class StationGroup
    {
        public string Key = string.Empty;
        public DispatchWorkbenchStationDto Representative;
        public List<string> StationIds = new List<string>();
        public Entity StopEntity;
        public Entity AnchorEntity;
    }

    internal sealed class Context
    {
        internal readonly State State = new State();
        internal readonly WorkbenchAccess WorkbenchAccess;
        internal Runtime Announcements;
        internal ModeScope CurrentScope = ModeScope.DefaultWorkbench;

        internal Workbench Workbench;
        internal Snapshot Snapshot;
        internal Drafts Drafts;
        internal Bindings Bindings;
        internal Rules Rules;
        internal Platforms Platforms;
        internal Assets Assets;
        internal Preview Preview;
        internal Persistence Persistence;
        internal Conflicts Conflicts;
        internal Apply Apply;
        internal SaveOperations SaveOperations;

        internal Context(WorkbenchAccess access)
        {
            WorkbenchAccess = access ?? throw new ArgumentNullException(nameof(access));
        }

        internal void Attach(Runtime runtime)
        {
            Announcements = runtime ?? throw new ArgumentNullException(nameof(runtime));
            LineMigration.SetInvalidator(runtime.ClearLineChecks);
        }
    }

    internal abstract class ModuleBase
    {
        protected const string DrivesToken = "__drives__";
        protected const string ManagedDirName = "BroadcastAssets";
        protected const float VolumeScalarMin = 5f;
        protected const float VolumeScalarMax = 60f;
        protected static readonly string[] s_Extensions = { ".wav", ".mp3", ".ogg" };

        protected readonly Context m_Ctx;

        protected ModuleBase(Context context)
        {
            m_Ctx = context ?? throw new ArgumentNullException(nameof(context));
        }

        protected State m_State => m_Ctx.State;
        protected WorkbenchAccess m_Access => m_Ctx.WorkbenchAccess;
        protected Runtime m_Announcements => m_Ctx.Announcements;
        protected EntityManager EntityManager => m_Access.EntityManager;
        protected TimedLogger log => m_Access.Log;
        protected ulong m_WorkbenchSnapshotVersion => m_Access.Version;
        protected ModeScope CurrentScope => m_Ctx.CurrentScope;
        protected BroadcastWorkbenchAssetState AssetState => m_State.AssetState(CurrentScope);
        protected List<BroadcastWorkbenchAssetDto> Catalog => AssetState.Catalog;
        protected string AssetFolder { get => AssetState.AssetDir; set => AssetState.AssetDir = value; }
        protected string BrowseFolder { get => AssetState.BrowseDir; set => AssetState.BrowseDir = value; }
        protected Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> DraftBindings => m_State.DraftBindings;
        protected Dictionary<string, List<BroadcastWorkbenchRuleDto>> DraftRules => m_State.DraftRules;
        protected Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> DraftPlatforms => m_State.DraftPlatforms;
        protected Dictionary<string, Dictionary<string, List<BroadcastWorkbenchStationBindingDto>>> AppliedBindings => m_State.AppliedBindings;
        protected Dictionary<string, List<BroadcastWorkbenchRuleDto>> AppliedRules => m_State.AppliedRules;
        protected Dictionary<string, Dictionary<string, BroadcastWorkbenchPlatformAnnouncementDto>> AppliedPlatforms => m_State.AppliedPlatforms;
        protected Dictionary<string, Dictionary<string, DispatchWorkbenchStationConflictDto[]>> PendingConflicts => m_State.PendingConflicts;
        protected HashSet<string> AppliedLines => m_State.AppliedLines;
        protected int DraftVol { get => m_State.GetDraftVolume(CurrentScope); set => m_State.SetDraftVolume(CurrentScope, value); }
        protected int AppliedVol { get => m_State.GetAppliedVolume(CurrentScope); set => m_State.SetAppliedVolume(CurrentScope, value); }

        protected IDisposable UseScope(ModeScope scope)
        {
            return new ScopeLease(m_Ctx, scope);
        }

        protected void IncrementWorkbenchSnapshotVersion() => m_Access.Next();
        protected void LoadWorkbench() => m_Access.Load();
        protected void SaveWorkbench() => m_Access.Save();
        protected void RunOnMainThread(Action action) => m_Access.Run(action);
        protected bool FeatureEnabled() => m_Access.Enabled;
        protected List<WorkbenchLineRuntime> Lines() => m_Access.Lines();
        protected static WorkbenchLineRuntime FindLine(List<WorkbenchLineRuntime> lines, string lineId)
        {
            if (lines == null || lines.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(lineId))
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    WorkbenchLineRuntime line = lines[i];
                    if (line != null
                        && string.Equals(line.Id, lineId, StringComparison.Ordinal))
                    {
                        return line;
                    }
                }
            }

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] != null)
                {
                    return lines[i];
                }
            }

            return null;
        }

        protected void LogException(string scope, Exception ex)
        {
            log.Info("[BroadcastWorkbenchException] " + scope + " -> "
                + m_Access.Error(ex));
        }

        private sealed class ScopeLease : IDisposable
        {
            private readonly Context m_Context;
            private readonly ModeScope m_Previous;

            internal ScopeLease(Context context, ModeScope scope)
            {
                m_Context = context;
                m_Previous = context.CurrentScope;
                context.CurrentScope = scope;
            }

            public void Dispose()
            {
                m_Context.CurrentScope = m_Previous;
            }
        }
    }
}
