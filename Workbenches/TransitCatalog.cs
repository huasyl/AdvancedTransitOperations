using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RapidTransitMod.Dispatch.Workbench;

namespace RapidTransitMod.Workbenches
{
    internal static class TransitCatalog
    {
        internal static string Refresh(string requestJson)
        {
            return Refresh(requestJson, "refreshTransitCatalog");
        }

        internal static string RefreshMetadata(string requestJson)
        {
            return Refresh(requestJson, "refreshMetadata");
        }

        private static string Refresh(string requestJson, string callName)
        {
            ModeScope scope = ModeRequest.ReadScope(requestJson, callName);
            string preferredLineId = scope.NormalizeLineId(ModeRequest.ReadPreferredLine(requestJson));
            ModRuntimeHostSystem runtime = ModRuntimeHostSystem.Instance;
            uint frame = runtime?.m_SimulationSystem != null ? runtime.m_SimulationSystem.frameIndex : 0u;

            if (runtime == null || runtime.m_WorkbenchCatalogCache == null)
                return Json.Write(Empty(scope, frame));

            return Json.Write(Build(runtime, scope, preferredLineId, frame));
        }

        private static TransitCatalogSnapshot Build(
            ModRuntimeHostSystem runtime,
            ModeScope scope,
            string preferredLineId,
            uint frame)
        {
            List<WorkbenchLineRuntime> runtimeLines = runtime.m_WorkbenchCatalogCache.RuntimeLines()
                .Where(line => line != null && scope.MatchesLineId(line.Id))
                .ToList();
            LineIds lineIds = runtime.m_WorkbenchBridge.Ids();
            DispatchWorkbenchLineDto[] lines = runtimeLines
                .Select(line => ToLineDto(runtime, lineIds, line))
                .Where(line => line != null)
                .ToArray();
            DispatchWorkbenchDepotDto[] depots = FilterDepots(
                runtime.m_WorkbenchCatalogCache.Depots(),
                runtimeLines);
            WorkbenchLineRuntime activeLine = ResolveActiveLine(runtimeLines, preferredLineId);

            return new TransitCatalogSnapshot
            {
                mode = scope.Token,
                selectedLineId = activeLine?.Id ?? preferredLineId ?? string.Empty,
                selectedEditLine = activeLine?.Id ?? preferredLineId ?? string.Empty,
                mergedView = new DispatchWorkbenchMergedView
                {
                    localLineIds = Array.Empty<string>(),
                    expressLineIds = Array.Empty<string>(),
                    isLoop = true,
                    turnbackStationId = string.Empty,
                    direction = "up"
                },
                lines = lines,
                depots = depots,
                stations = activeLine != null
                    ? runtime.m_WorkbenchCatalogCache.Stations(activeLine.Entity).ToArray()
                    : Array.Empty<DispatchWorkbenchStationDto>(),
                version = runtime.m_WorkbenchBridge != null
                    ? runtime.m_WorkbenchBridge.Version.ToString()
                    : string.Empty,
                sourceMode = "transit-catalog",
                generatedAtFrame = frame
            };
        }

        private static TransitCatalogSnapshot Empty(ModeScope scope, uint frame)
        {
            return new TransitCatalogSnapshot
            {
                mode = scope.Token,
                selectedLineId = string.Empty,
                selectedEditLine = string.Empty,
                mergedView = new DispatchWorkbenchMergedView(),
                lines = Array.Empty<DispatchWorkbenchLineDto>(),
                depots = Array.Empty<DispatchWorkbenchDepotDto>(),
                stations = Array.Empty<DispatchWorkbenchStationDto>(),
                version = string.Empty,
                sourceMode = "transit-catalog",
                generatedAtFrame = frame
            };
        }

        private static DispatchWorkbenchLineDto ToLineDto(
            ModRuntimeHostSystem runtime,
            LineIds lineIds,
            WorkbenchLineRuntime line)
        {
            if (line == null)
                return null;

            string lineId = line.Id ?? string.Empty;
            return new DispatchWorkbenchLineDto
            {
                id = lineId,
                sourceLineId = line.Entity.Index.ToString(),
                name = line.Name ?? string.Empty,
                kind = NormalizeKind(runtime.GetKind(lineId)),
                direction = "up",
                stationCount = line.StationCount,
                color = lineIds.Color(line.Entity),
                originStationId = line.OriginStationId ?? string.Empty,
                originStationName = line.OriginStationName ?? string.Empty,
                originHoldLimitMinutes = runtime.GetHold(lineId),
                maxStationDwellMinutes = runtime.GetDwell(lineId),
                transportType = line.TransportType ?? string.Empty,
                allowedDepotId = runtime.GetDepotId(lineId),
                dispatchSupported = line.DispatchSupported,
                unsupportedReason = line.UnsupportedReason ?? string.Empty,
                originStatus = line.OriginStatus ?? string.Empty,
                originMessageKey = line.OriginMessageKey ?? string.Empty
            };
        }

        private static DispatchWorkbenchDepotDto[] FilterDepots(
            List<DispatchWorkbenchDepotDto> depots,
            List<WorkbenchLineRuntime> runtimeLines)
        {
            HashSet<string> transportTypes = new HashSet<string>(
                (runtimeLines ?? new List<WorkbenchLineRuntime>())
                    .Where(line => line != null && !string.IsNullOrEmpty(line.TransportType))
                    .Select(line => line.TransportType),
                StringComparer.Ordinal);
            if (transportTypes.Count == 0)
                return Array.Empty<DispatchWorkbenchDepotDto>();

            return (depots ?? new List<DispatchWorkbenchDepotDto>())
                .Where(depot => depot != null
                    && !string.IsNullOrEmpty(depot.transportType)
                    && transportTypes.Contains(depot.transportType))
                .Select(depot => new DispatchWorkbenchDepotDto
                {
                    id = depot.id ?? string.Empty,
                    name = depot.name ?? string.Empty,
                    transportType = depot.transportType ?? string.Empty
                })
                .ToArray();
        }

        private static WorkbenchLineRuntime ResolveActiveLine(
            List<WorkbenchLineRuntime> lines,
            string preferredLineId)
        {
            if (lines == null || lines.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(preferredLineId))
            {
                WorkbenchLineRuntime exact = lines.FirstOrDefault(
                    line => string.Equals(line?.Id, preferredLineId, StringComparison.Ordinal));
                if (exact != null)
                    return exact;
            }

            return lines[0];
        }

        private static string NormalizeKind(string kind)
        {
            return string.Equals(kind, "express", StringComparison.Ordinal)
                ? "express"
                : "local";
        }
    }

    [DataContract]
    internal sealed class TransitCatalogSnapshot
    {
        [DataMember] public string mode = string.Empty;
        [DataMember] public string selectedLineId = string.Empty;
        [DataMember] public string selectedEditLine = string.Empty;
        [DataMember] public DispatchWorkbenchMergedView mergedView;
        [DataMember] public DispatchWorkbenchLineDto[] lines;
        [DataMember] public DispatchWorkbenchDepotDto[] depots;
        [DataMember] public DispatchWorkbenchStationDto[] stations;
        [DataMember] public string version = string.Empty;
        [DataMember] public string sourceMode = string.Empty;
        [DataMember] public uint generatedAtFrame;
    }
}
