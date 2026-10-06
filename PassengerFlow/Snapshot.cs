namespace RapidTransitMod.PassengerFlow
{
    internal static class Snapshot
    {
        internal const int SchemaVersion = 5;
        internal const int BucketMinutes = 15;
        private const uint SummaryLogIntervalFrames = 1800;

        internal static FlowSnapshotDto Build(State state, uint generatedAtFrame, FlowQuery query,
            Core.ClockSnapshot clock, Port port = null)
        {
            FlowOptions options = query.Options;
            SnapshotRows rows = state != null && !query.DirectoryOnly
                ? state.Aggregates.BuildSnapshotRows(state.Anchors, query, clock)
                : SnapshotRows.Empty();
            StationCatalogDto[] stationCatalog = options.includeStationCatalog && !query.DirectoryOnly && state != null
                ? state.Anchors.BuildCatalog(port)
                : System.Array.Empty<StationCatalogDto>();
            query.Groups.Apply(rows, query.SectionKind);
            foreach (StationCatalogDto station in stationCatalog)
            {
                station.stationGroupId = query.Groups.Id(station.stationId);
                station.stationName = query.Groups.Name(station.stationId, station.stationName);
            }
            bool lineStopsAvailable = false;
            LineStopDto[] lineStops = options.includeLineStops && port != null && state != null
                ? state.Anchors.BuildLineStops(port, query, out lineStopsAvailable)
                : System.Array.Empty<LineStopDto>();

            FlowSnapshotDto snapshot = new FlowSnapshotDto
            {
                schemaVersion = SchemaVersion,
                mode = query.ModeToken,
                view = query.View,
                day = query.Day,
                selectedDayIndex = query.SelectedDayIndex,
                sectionKind = options.includeSections ? (query.SectionKind == SectionKind.Stops ? "stops" : "track") : null,
                requestedFromBucket = query.RequestedFrom,
                requestedToBucket = query.RequestedTo,
                generatedAtFrame = generatedAtFrame,
                bucketMinutes = BucketMinutes,
                stationVolumes = rows.StationVolumes,
                sectionVolumes = rows.SectionVolumes,
                odFlows = rows.OdFlows,
                stationCatalog = stationCatalog,
                warnings = rows.Warnings,
                transferFlows = rows.TransferFlows,
                stationWaiting = rows.StationWaiting,
                lineTimeLoads = rows.LineTimeLoads,
                networkTimeLoads = rows.NetworkTimeLoads,
                currentDayIndex = clock.DayIndex,
                currentMinute = port != null ? clock.NowMinute : 0,
                samplingReady = port != null && port.IsReady && state != null
                    && state.LoadsInitialized && !state.RuntimeRestorePending,
                retainedFromBucket = query.RetainedFrom,
                retainedToBucket = query.RetainedTo,
                effectiveFromBucket = query.EffectiveFrom,
                effectiveToBucket = query.EffectiveTo,
                lineStops = lineStops,
                lineStopsAvailable = lineStopsAvailable,
                stationGroups = query.Groups.ToDtos(),
                summary = rows.Summary
            };

            if (!query.DirectoryOnly) LogSummary(state, snapshot, generatedAtFrame, port);
            return snapshot;
        }

        private static void LogSummary(State state, FlowSnapshotDto snapshot, uint frame, Port port)
        {
            if (state == null || port == null)
                return;

            if (state.LastSnapshotSummaryLogFrame != 0
                && frame < state.LastSnapshotSummaryLogFrame + SummaryLogIntervalFrames)
                return;

            state.LastSnapshotSummaryLogFrame = frame;
            long odCompleted = snapshot.summary != null ? snapshot.summary.completedOdCount : 0;
            if (snapshot.summary == null)
                for (int i = 0; i < snapshot.odFlows.Length; i++)
                    odCompleted += snapshot.odFlows[i].completedCount;

            string unknownOrigin = WarningCount(snapshot, Aggregates.WarningUnknownOriginAlighting).ToString();
            string transferExpired = WarningCount(snapshot, Aggregates.WarningTransferWindowExpired).ToString();
            string transferMismatch = WarningCount(snapshot, Aggregates.WarningTransferBoardStationMismatch).ToString();
            string overflow = WarningCount(snapshot, Aggregates.WarningPendingTransferOverflow).ToString();
            port.Log("[PassengerFlowSummary] mode=" + snapshot.mode
                + " stationRows=" + (snapshot.summary != null ? snapshot.summary.stationVolumes.Length : snapshot.stationVolumes.Length).ToString()
                + " sectionRows=" + (snapshot.summary != null ? snapshot.summary.sectionVolumes.Length : snapshot.sectionVolumes.Length).ToString()
                + " odRows=" + (snapshot.summary != null ? snapshot.summary.odFlows.Length : snapshot.odFlows.Length).ToString()
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
