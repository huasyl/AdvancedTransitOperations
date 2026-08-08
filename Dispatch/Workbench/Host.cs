using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class UiPort
    {
        private readonly Action<DispatchWorkbenchSnapshot> m_Push;
        private readonly Func<Exception, string> m_Error;
        private readonly Action<string, Exception> m_Fault;
        private readonly Action<Action> m_Run;

        internal UiPort(
            Action<DispatchWorkbenchSnapshot> push,
            Func<Exception, string> error,
            Action<string, Exception> fault,
            Action<Action> run)
        {
            m_Push = push ?? throw new ArgumentNullException(nameof(push));
            m_Error = error ?? throw new ArgumentNullException(nameof(error));
            m_Fault = fault ?? throw new ArgumentNullException(nameof(fault));
            m_Run = run ?? throw new ArgumentNullException(nameof(run));
        }

        internal void Push(DispatchWorkbenchSnapshot snapshot)
        {
            m_Push(snapshot);
        }

        internal string Error(Exception ex)
        {
            return m_Error(ex);
        }

        internal void Fault(string scope, Exception ex)
        {
            m_Fault(scope, ex);
        }

        internal void Run(Action action)
        {
            m_Run(action);
        }
    }

    internal sealed class RunPort
    {
        private readonly Action<RuntimeFeatureSettingsDto> m_Features;
        private readonly Func<RuntimeFeatureSettingsDto, bool> m_SameFeatures;
        private readonly Action<IEnumerable<DispatchWorkbenchLineSettingDto>> m_LineCfg;
        private readonly Func<IEnumerable<DispatchWorkbenchLineSettingDto>, bool> m_SameLineCfg;
        private readonly Action<TransitMode, IEnumerable<DispatchWorkbenchLineSettingDto>> m_LineCfgForMode;
        private readonly Func<TransitMode, IEnumerable<DispatchWorkbenchLineSettingDto>, bool> m_SameLineCfgForMode;
        private readonly Func<TransitMode, IEnumerable<DispatchWorkbenchLineSettingDto>, IEnumerable<string>> m_ChangedDepotLines;
        private readonly Action<IEnumerable<string>> m_InvalidateDispatchTiming;
        private readonly Action m_ClearLineCfg;
        private readonly Action m_DropDepotCache;
        private readonly Func<RuntimeFeatureSettingsDto> m_FeatureDto;
        private readonly Func<IEnumerable<string>> m_Keys;
        private readonly Func<string, string> m_Kind;
        private readonly Action m_RefreshApplied;
        private readonly Action<IEnumerable<string>, List<WorkbenchLineRuntime>> m_ApplyDraft;
        private readonly Func<bool> m_CleanupInvalidApplied;
        private readonly Func<IEnumerable<WorkbenchLineRuntime>, IReadOnlyDictionary<string, string>> m_CollectRuntimeMissingLineReasons;
        private readonly Func<IEnumerable<string>, bool> m_RemoveDeletedLines;
        private readonly Func<IReadOnlyDictionary<string, string>, bool> m_CleanupRequestedLines;
        private readonly Func<IReadOnlyDictionary<string, string>, bool> m_CleanupConfirmedInvalidatedLines;
        private readonly Func<DispatchWorkbenchCleanupInfoDto> m_ConsumeCleanupInfo;
        private readonly Action m_Invalidate;

        internal RunPort(
            Action<RuntimeFeatureSettingsDto> features,
            Func<RuntimeFeatureSettingsDto, bool> sameFeatures,
            Action<IEnumerable<DispatchWorkbenchLineSettingDto>> lineCfg,
            Func<IEnumerable<DispatchWorkbenchLineSettingDto>, bool> sameLineCfg,
            Action<TransitMode, IEnumerable<DispatchWorkbenchLineSettingDto>> lineCfgForMode,
            Func<TransitMode, IEnumerable<DispatchWorkbenchLineSettingDto>, bool> sameLineCfgForMode,
            Func<TransitMode, IEnumerable<DispatchWorkbenchLineSettingDto>, IEnumerable<string>> changedDepotLines,
            Action<IEnumerable<string>> invalidateDispatchTiming,
            Action clearLineCfg,
            Action dropDepotCache,
            Func<RuntimeFeatureSettingsDto> featureDto,
            Func<IEnumerable<string>> keys,
            Func<string, string> kind,
            Action refreshApplied,
            Action<IEnumerable<string>, List<WorkbenchLineRuntime>> applyDraft,
            Func<bool> cleanupInvalidApplied,
            Func<IEnumerable<WorkbenchLineRuntime>, IReadOnlyDictionary<string, string>> collectRuntimeMissingLineReasons,
            Func<IEnumerable<string>, bool> removeDeletedLines,
            Func<IReadOnlyDictionary<string, string>, bool> cleanupRequestedLines,
            Func<IReadOnlyDictionary<string, string>, bool> cleanupConfirmedInvalidatedLines,
            Func<DispatchWorkbenchCleanupInfoDto> consumeCleanupInfo,
            Action invalidate)
        {
            m_Features = features ?? throw new ArgumentNullException(nameof(features));
            m_SameFeatures = sameFeatures ?? throw new ArgumentNullException(nameof(sameFeatures));
            m_LineCfg = lineCfg ?? throw new ArgumentNullException(nameof(lineCfg));
            m_SameLineCfg = sameLineCfg ?? throw new ArgumentNullException(nameof(sameLineCfg));
            m_LineCfgForMode = lineCfgForMode ?? throw new ArgumentNullException(nameof(lineCfgForMode));
            m_SameLineCfgForMode = sameLineCfgForMode ?? throw new ArgumentNullException(nameof(sameLineCfgForMode));
            m_ChangedDepotLines = changedDepotLines ?? throw new ArgumentNullException(nameof(changedDepotLines));
            m_InvalidateDispatchTiming = invalidateDispatchTiming ?? throw new ArgumentNullException(nameof(invalidateDispatchTiming));
            m_ClearLineCfg = clearLineCfg ?? throw new ArgumentNullException(nameof(clearLineCfg));
            m_DropDepotCache = dropDepotCache ?? throw new ArgumentNullException(nameof(dropDepotCache));
            m_FeatureDto = featureDto ?? throw new ArgumentNullException(nameof(featureDto));
            m_Keys = keys ?? throw new ArgumentNullException(nameof(keys));
            m_Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            m_RefreshApplied = refreshApplied ?? throw new ArgumentNullException(nameof(refreshApplied));
            m_ApplyDraft = applyDraft ?? throw new ArgumentNullException(nameof(applyDraft));
            m_CleanupInvalidApplied = cleanupInvalidApplied ?? throw new ArgumentNullException(nameof(cleanupInvalidApplied));
            m_CollectRuntimeMissingLineReasons = collectRuntimeMissingLineReasons ?? throw new ArgumentNullException(nameof(collectRuntimeMissingLineReasons));
            m_RemoveDeletedLines = removeDeletedLines ?? throw new ArgumentNullException(nameof(removeDeletedLines));
            m_CleanupRequestedLines = cleanupRequestedLines ?? throw new ArgumentNullException(nameof(cleanupRequestedLines));
            m_CleanupConfirmedInvalidatedLines = cleanupConfirmedInvalidatedLines ?? throw new ArgumentNullException(nameof(cleanupConfirmedInvalidatedLines));
            m_ConsumeCleanupInfo = consumeCleanupInfo ?? throw new ArgumentNullException(nameof(consumeCleanupInfo));
            m_Invalidate = invalidate ?? throw new ArgumentNullException(nameof(invalidate));
        }

        internal void Features(RuntimeFeatureSettingsDto settings)
        {
            m_Features(settings);
        }

        internal bool SameFeatures(RuntimeFeatureSettingsDto settings)
        {
            return m_SameFeatures(settings);
        }

        internal void LineCfg(IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            m_LineCfg(settings);
        }

        internal void LineCfg(ModeScope scope, IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            m_LineCfgForMode(scope.Mode, settings);
        }

        internal bool SameLineCfg(IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            return m_SameLineCfg(settings);
        }

        internal bool SameLineCfg(ModeScope scope, IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            return m_SameLineCfgForMode(scope.Mode, settings);
        }

        internal IEnumerable<string> ChangedDepotLines(
            ModeScope scope,
            IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            return m_ChangedDepotLines(scope.Mode, settings);
        }

        internal void InvalidateDispatchTiming(IEnumerable<string> lineIds)
        {
            m_InvalidateDispatchTiming(lineIds);
        }

        internal void ClearLineCfg()
        {
            m_ClearLineCfg();
        }

        internal void DropDepotCache()
        {
            m_DropDepotCache();
        }

        internal RuntimeFeatureSettingsDto FeatureDto()
        {
            return m_FeatureDto();
        }

        internal IEnumerable<string> Keys()
        {
            return m_Keys();
        }

        internal string Kind(string lineId)
        {
            return m_Kind(lineId);
        }

        internal void RefreshApplied()
        {
            m_RefreshApplied();
        }

        internal void ApplyDraft(IEnumerable<string> lineIds, List<WorkbenchLineRuntime> runtimeLines)
        {
            m_ApplyDraft(lineIds, runtimeLines);
        }

        internal bool CleanupInvalidApplied()
        {
            return m_CleanupInvalidApplied();
        }

        internal IReadOnlyDictionary<string, string> CollectRuntimeMissingLineReasons(
            IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            return m_CollectRuntimeMissingLineReasons(runtimeLines);
        }

        internal bool RemoveDeletedLines(IEnumerable<string> lineIds)
        {
            return m_RemoveDeletedLines(lineIds);
        }

        internal bool CleanupRequestedLines(IReadOnlyDictionary<string, string> reasons)
        {
            return m_CleanupRequestedLines(reasons);
        }

        internal bool CleanupConfirmedInvalidatedLines(IReadOnlyDictionary<string, string> reasons)
        {
            return m_CleanupConfirmedInvalidatedLines(reasons);
        }

        internal DispatchWorkbenchCleanupInfoDto ConsumeCleanupInfo()
        {
            return m_ConsumeCleanupInfo();
        }

        internal void Invalidate()
        {
            m_Invalidate();
        }
    }

    internal sealed class Host
    {
        private readonly Func<Entity> m_City;
        private readonly Func<int> m_Now;
        private readonly Action<string> m_Log;
        private readonly Func<Entity, string> m_Name;
        private readonly Func<List<WorkbenchLineRuntime>> m_Lines;
        private readonly Func<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>> m_Stations;
        private readonly Func<List<DispatchWorkbenchDepotDto>> m_Depots;
        private readonly Func<ulong> m_Version;
        private readonly Action m_Dirty;
        private readonly Action<string> m_Seed;
        private readonly Action m_SaveApplied;
        private readonly Action m_Reset;

        internal Host(
            EntityManager entityManager,
            Func<Entity> city,
            Func<int> now,
            Action<string> log,
            Func<Entity, string> name,
            Func<List<WorkbenchLineRuntime>> lines,
            Func<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>> stations,
            Func<List<DispatchWorkbenchDepotDto>> depots,
            Func<ulong> version,
            Action dirty,
            Action<string> seed,
            Action saveApplied,
            Action reset,
            UiPort ui,
            RunPort run)
        {
            EntityManager = entityManager;
            m_City = city ?? throw new ArgumentNullException(nameof(city));
            m_Now = now ?? throw new ArgumentNullException(nameof(now));
            m_Log = log ?? throw new ArgumentNullException(nameof(log));
            m_Name = name ?? throw new ArgumentNullException(nameof(name));
            m_Lines = lines ?? throw new ArgumentNullException(nameof(lines));
            m_Stations = stations ?? throw new ArgumentNullException(nameof(stations));
            m_Depots = depots ?? throw new ArgumentNullException(nameof(depots));
            m_Version = version ?? throw new ArgumentNullException(nameof(version));
            m_Dirty = dirty ?? throw new ArgumentNullException(nameof(dirty));
            m_Seed = seed ?? throw new ArgumentNullException(nameof(seed));
            m_SaveApplied = saveApplied ?? throw new ArgumentNullException(nameof(saveApplied));
            m_Reset = reset ?? throw new ArgumentNullException(nameof(reset));
            Ui = ui ?? throw new ArgumentNullException(nameof(ui));
            Run = run ?? throw new ArgumentNullException(nameof(run));
        }

        internal EntityManager EntityManager { get; }
        internal UiPort Ui { get; }
        internal RunPort Run { get; }

        internal Entity City()
        {
            return m_City();
        }

        internal int Now()
        {
            return m_Now();
        }

        internal void Log(string message)
        {
            m_Log(message);
        }

        internal string Name(Entity entity)
        {
            return m_Name(entity);
        }

        internal List<WorkbenchLineRuntime> Lines()
        {
            return m_Lines() ?? new List<WorkbenchLineRuntime>();
        }

        internal List<DispatchWorkbenchStationDto> Stations(WorkbenchLineRuntime line)
        {
            return m_Stations(line) ?? new List<DispatchWorkbenchStationDto>();
        }

        internal List<DispatchWorkbenchDepotDto> Depots()
        {
            return m_Depots() ?? new List<DispatchWorkbenchDepotDto>();
        }

        internal ulong Version()
        {
            return m_Version();
        }

        internal void Dirty()
        {
            m_Dirty();
        }

        internal void Seed(string lineId)
        {
            m_Seed(lineId);
        }

        internal void SaveApplied()
        {
            m_SaveApplied();
        }

        internal void Reset()
        {
            m_Reset();
        }
    }
}
