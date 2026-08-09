using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class RunHooks
    {
        private readonly FeatureGate m_Features;
        private readonly Func<LineConfig> m_LineCfg;
        private readonly Func<DepotResolver> m_Depots;
        private readonly LineView m_LineView;
        private readonly Func<AppliedTimetable> m_Applied;
        private readonly Action m_CatalogDirty;
        private readonly Action<IEnumerable<string>> m_InvalidateDispatchTiming;

        internal RunHooks(
            FeatureGate features,
            Func<LineConfig> lineCfg,
            Func<DepotResolver> depots,
            LineView lineView,
            Func<AppliedTimetable> applied,
            Action catalogDirty,
            Action<IEnumerable<string>> invalidateDispatchTiming)
        {
            m_Features = features ?? throw new ArgumentNullException(nameof(features));
            m_LineCfg = lineCfg ?? throw new ArgumentNullException(nameof(lineCfg));
            m_Depots = depots ?? throw new ArgumentNullException(nameof(depots));
            m_LineView = lineView ?? throw new ArgumentNullException(nameof(lineView));
            m_Applied = applied ?? throw new ArgumentNullException(nameof(applied));
            m_CatalogDirty = catalogDirty ?? throw new ArgumentNullException(nameof(catalogDirty));
            m_InvalidateDispatchTiming = invalidateDispatchTiming ?? throw new ArgumentNullException(nameof(invalidateDispatchTiming));
        }

        internal RunPort Port()
        {
            return new RunPort(
                settings => m_Features.Apply(settings),
                settings => m_Features.Same(settings),
                settings =>
                {
                    m_LineCfg().Apply(settings);
                    m_Depots().Clear();
                    m_LineView.Clear();
                    m_CatalogDirty();
                },
                settings => m_LineCfg().Same(settings),
                (mode, settings) =>
                {
                    m_LineCfg().Apply(mode, settings);
                    m_Depots().Clear();
                    m_LineView.Clear();
                    m_CatalogDirty();
                },
                (mode, settings) => m_LineCfg().Same(mode, settings),
                (mode, settings) => m_LineCfg().ChangedDepotLines(mode, settings),
                lineIds => m_InvalidateDispatchTiming(lineIds),
                () =>
                {
                    m_LineCfg().Clear();
                    m_Depots().Clear();
                    m_CatalogDirty();
                },
                () =>
                {
                    m_Depots().Clear();
                    m_CatalogDirty();
                },
                () => m_Features.Dto(),
                () => m_LineCfg().Keys(),
                lineId => m_LineView.Kind(lineId),
                () => m_Applied().RefreshCfg(),
                (draftKeys, runtimeLines) => m_Applied().ApplyDraft(draftKeys, runtimeLines),
                () => m_Applied().CleanupDeletedOrReplacedAppliedLines(saveChanges: false),
                runtimeLines => CollectRuntimeMissingLineReasons(runtimeLines),
                lineIds => m_Applied().RemoveDeletedLines(lineIds, saveChanges: false),
                reasons => m_Applied().CleanupRequestedLines(reasons, saveChanges: false),
                reasons => m_Applied().CleanupRequestedLines(reasons, saveChanges: true),
                () => m_Applied().ConsumeCleanupInfo(),
                () => m_LineView.Dirty());
        }

        private IReadOnlyDictionary<string, string> CollectRuntimeMissingLineReasons(
            IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            Dictionary<string, string> reasons =
                m_Applied().CollectRuntimeMissingLineReasons(runtimeLines);
            HashSet<string> runtimeLineIds = new HashSet<string>(
                (runtimeLines ?? Enumerable.Empty<WorkbenchLineRuntime>())
                    .Select(line => DraftStore.GetKey(line?.Id))
                    .Where(lineId => !string.IsNullOrEmpty(lineId) && !string.Equals(lineId, "__default__", StringComparison.Ordinal)),
                StringComparer.Ordinal);

            foreach (string lineId in m_LineCfg().Keys()
                .Select(DraftStore.GetKey)
                .Where(lineId => !string.IsNullOrEmpty(lineId) && !string.Equals(lineId, "__default__", StringComparison.Ordinal)))
            {
                if (!runtimeLineIds.Contains(lineId) && !reasons.ContainsKey(lineId))
                {
                    reasons[lineId] = "runtime-line-missing";
                }
            }

            return reasons;
        }
    }
}
