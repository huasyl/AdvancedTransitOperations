namespace RapidTransitMod.PassengerFlow
{
    internal static class Snapshot
    {
        internal const int SchemaVersion = 1;
        internal const int BucketMinutes = 15;
        private const uint SummaryLogIntervalFrames = 1800;

        internal static FlowSnapshotDto Build(ModeScope scope, State state, uint generatedAtFrame, Port port = null)
        {
            SnapshotRows rows = state != null
                ? state.Aggregates.BuildSnapshotRows(scope, state.Anchors)
                : SnapshotRows.Empty(scope);
            StationCatalogDto[] stationCatalog = state != null
                ? state.Anchors.BuildCatalog(port)
                : System.Array.Empty<StationCatalogDto>();

            FlowSnapshotDto snapshot = new FlowSnapshotDto
            {
                schemaVersion = SchemaVersion,
                mode = scope.Token,
                generatedAtFrame = generatedAtFrame,
                bucketMinutes = BucketMinutes,
                stationVolumes = rows.StationVolumes,
                sectionVolumes = rows.SectionVolumes,
                odFlows = rows.OdFlows,
                stationCatalog = stationCatalog,
                warnings = rows.Warnings
            };

            LogSummary(scope, state, snapshot, generatedAtFrame, port);
            return snapshot;
        }

        private static void LogSummary(ModeScope scope, State state, FlowSnapshotDto snapshot, uint frame, Port port)
        {
            if (state == null || port == null)
                return;

            if (state.LastSnapshotSummaryLogFrame != 0
                && frame < state.LastSnapshotSummaryLogFrame + SummaryLogIntervalFrames)
                return;

            state.LastSnapshotSummaryLogFrame = frame;
            int odCompleted = 0;
            OdFlowDto[] odFlows = snapshot.odFlows ?? System.Array.Empty<OdFlowDto>();
            for (int i = 0; i < odFlows.Length; i++)
                odCompleted += odFlows[i]?.completedCount ?? 0;

            string unknownOrigin = WarningCount(snapshot, Aggregates.WarningUnknownOriginAlighting).ToString();
            string transferExpired = WarningCount(snapshot, Aggregates.WarningTransferWindowExpired).ToString();
            string transferMismatch = WarningCount(snapshot, Aggregates.WarningTransferBoardStationMismatch).ToString();
            string overflow = WarningCount(snapshot, Aggregates.WarningPendingTransferOverflow).ToString();
            port.Log("[PassengerFlowSummary] mode=" + scope.Token
                + " stationRows=" + (snapshot.stationVolumes != null ? snapshot.stationVolumes.Length : 0).ToString()
                + " sectionRows=" + (snapshot.sectionVolumes != null ? snapshot.sectionVolumes.Length : 0).ToString()
                + " odRows=" + odFlows.Length.ToString()
                + " odCompleted=" + odCompleted.ToString()
                + " unknownOrigin=" + unknownOrigin
                + " transferExpired=" + transferExpired
                + " transferStationMismatch=" + transferMismatch
                + " pendingOverflow=" + overflow);
        }

        private static int WarningCount(FlowSnapshotDto snapshot, string code)
        {
            int count = 0;
            WarningDto[] warnings = snapshot.warnings ?? System.Array.Empty<WarningDto>();
            for (int i = 0; i < warnings.Length; i++)
            {
                WarningDto warning = warnings[i];
                if (warning != null && string.Equals(warning.code, code, System.StringComparison.Ordinal))
                    count += warning.count;
            }

            return count;
        }
    }
}
