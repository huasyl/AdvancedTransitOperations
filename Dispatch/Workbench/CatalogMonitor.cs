using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class CatalogMonitor
    {
        private readonly Func<TransitMode, DispatchWorkbenchSnapshot> m_Meta;
        private readonly Func<TransitMode, List<WorkbenchLineRuntime>> m_Lines;
        private readonly Func<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>> m_Stations;
        private readonly Action<DispatchWorkbenchCatalogEvent> m_Push;
        private readonly Func<ulong> m_Version;
        private string m_TrainKey = string.Empty;
        private string m_SubwayKey = string.Empty;

        internal CatalogMonitor(
            Func<TransitMode, DispatchWorkbenchSnapshot> meta,
            Func<TransitMode, List<WorkbenchLineRuntime>> lines,
            Func<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>> stations,
            Action<DispatchWorkbenchCatalogEvent> push,
            Func<ulong> version)
        {
            m_Meta = meta ?? throw new ArgumentNullException(nameof(meta));
            m_Lines = lines ?? throw new ArgumentNullException(nameof(lines));
            m_Stations = stations ?? throw new ArgumentNullException(nameof(stations));
            m_Push = push ?? throw new ArgumentNullException(nameof(push));
            m_Version = version ?? throw new ArgumentNullException(nameof(version));
        }

        internal void Check()
        {
            Check(TransitMode.Train, ref m_TrainKey);
            Check(TransitMode.Subway, ref m_SubwayKey);
        }

        internal void Reset()
        {
            m_TrainKey = string.Empty;
            m_SubwayKey = string.Empty;
        }

        private void Check(TransitMode mode, ref string lastKey)
        {
            DispatchWorkbenchSnapshot snapshot = m_Meta(mode);
            string nextKey = BuildKey(snapshot, m_Lines(mode), m_Stations);
            if (string.IsNullOrEmpty(nextKey))
            {
                lastKey = string.Empty;
                return;
            }

            if (string.IsNullOrEmpty(lastKey))
            {
                lastKey = nextKey;
                return;
            }

            if (string.Equals(lastKey, nextKey, StringComparison.Ordinal))
            {
                return;
            }

            lastKey = nextKey;
            m_Push(new DispatchWorkbenchCatalogEvent
            {
                mode = TransitModeCodec.Format(mode),
                version = m_Version().ToString()
            });
        }

        private static string BuildKey(
            DispatchWorkbenchSnapshot snapshot,
            List<WorkbenchLineRuntime> runtimeLines,
            Func<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>> stations)
        {
            if (snapshot == null)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(snapshot.mode ?? string.Empty);
            sb.Append('|');

            foreach (DispatchWorkbenchLineDto line in (snapshot.lines ?? Array.Empty<DispatchWorkbenchLineDto>())
                .Where(line => line != null)
                .OrderBy(line => line.id ?? string.Empty, StringComparer.Ordinal))
            {
                sb.Append(line.id ?? string.Empty);
                sb.Append('~');
                sb.Append(line.name ?? string.Empty);
                sb.Append('~');
                sb.Append(line.kind ?? string.Empty);
                sb.Append('~');
                sb.Append(line.transportType ?? string.Empty);
                sb.Append('~');
                sb.Append(line.originStationId ?? string.Empty);
                sb.Append('~');
                sb.Append(line.originStationName ?? string.Empty);
                sb.Append('~');
                sb.Append(line.allowedDepotId ?? string.Empty);
                sb.Append('|');
            }

            sb.Append('#');

            foreach (WorkbenchLineRuntime line in (runtimeLines ?? new List<WorkbenchLineRuntime>())
                .Where(line => line != null)
                .OrderBy(line => line.Id ?? string.Empty, StringComparer.Ordinal))
            {
                sb.Append(line.Id ?? string.Empty);
                sb.Append('|');

                foreach (DispatchWorkbenchStationDto station in (stations(line) ?? new List<DispatchWorkbenchStationDto>())
                    .Where(station => station != null)
                    .OrderBy(station => station.order)
                    .ThenBy(station => station.id ?? string.Empty, StringComparer.Ordinal))
                {
                    sb.Append(station.id ?? string.Empty);
                    sb.Append('~');
                    sb.Append(station.name ?? string.Empty);
                    sb.Append('~');
                    sb.Append(station.order.ToString());
                    sb.Append('|');
                }
            }

            sb.Append('#');

            foreach (DispatchWorkbenchDepotDto depot in (snapshot.depots ?? Array.Empty<DispatchWorkbenchDepotDto>())
                .Where(depot => depot != null)
                .OrderBy(depot => depot.id ?? string.Empty, StringComparer.Ordinal))
            {
                sb.Append(depot.id ?? string.Empty);
                sb.Append('~');
                sb.Append(depot.name ?? string.Empty);
                sb.Append('~');
                sb.Append(depot.transportType ?? string.Empty);
                sb.Append('|');
            }

            return sb.ToString();
        }
    }
}
