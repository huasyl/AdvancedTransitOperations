using System;
using System.Collections.Generic;
using RapidTransitMod.TrackModel;

namespace RapidTransitMod.PassengerFlow
{
    // 单次查询的只读投影，不持有分组生命周期或持久化状态。
    internal sealed class StationGroups
    {
        private readonly TramStationGroup[] m_Groups;
        private readonly Dictionary<string, TramStationGroup> m_ByStation = new Dictionary<string, TramStationGroup>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<int>> m_Indices = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        private readonly Dictionary<int, int> m_Canonical = new Dictionary<int, int>();

        internal StationGroups(TramStationGroup[] groups, Anchors anchors)
        {
            m_Groups = groups ?? Array.Empty<TramStationGroup>();
            foreach (TramStationGroup group in m_Groups)
            {
                var indices = new HashSet<int>();
                int canonical = -1;
                foreach (string member in group.Members)
                {
                    m_ByStation[member] = group;
                    if (anchors != null && anchors.TryGetIndex(member, out int index))
                    {
                        indices.Add(index);
                        if (canonical < 0) canonical = index;
                    }
                }
                m_Indices[group.GroupId] = indices;
                foreach (int index in indices) m_Canonical[index] = canonical;
            }
        }

        internal string Id(string stationId) => stationId != null && m_ByStation.TryGetValue(stationId, out TramStationGroup group)
            ? group.GroupId : stationId;

        internal string Name(string stationId, string fallback) => stationId != null && m_ByStation.TryGetValue(stationId, out TramStationGroup group)
            ? group.Name : fallback;

        internal int Canonical(int stationIndex) => m_Canonical.TryGetValue(stationIndex, out int index) ? index : stationIndex;

        internal int FirstIndex(string groupId)
        {
            if (!string.IsNullOrEmpty(groupId) && m_Indices.TryGetValue(Id(groupId), out HashSet<int> indices))
                foreach (int index in indices) return index;
            return -2;
        }

        internal HashSet<int> SelectionIndices(string groupId, int stationIndex)
        {
            if (!string.IsNullOrEmpty(groupId) && m_Indices.TryGetValue(Id(groupId), out HashSet<int> indices))
                return indices;
            return stationIndex >= 0 ? new HashSet<int> { stationIndex } : new HashSet<int>();
        }

        internal void Apply(SnapshotRows rows, SectionKind sectionKind)
        {
            foreach (StationVolumeDto row in rows.StationVolumes)
            {
                row.stationGroupId = Id(row.stationId);
                row.stationName = Name(row.stationId, row.stationName);
            }
            foreach (OdFlowDto row in rows.OdFlows)
            {
                row.originStationGroupId = Id(row.originStationId);
                row.destinationStationGroupId = Id(row.destinationStationId);
            }
            foreach (StationWaitingDto row in rows.StationWaiting) row.stationGroupId = Id(row.stationId);
            foreach (TransferFlowDto row in rows.TransferFlows)
            {
                row.fromStationGroupId = Id(row.fromStationId);
                row.toStationGroupId = Id(row.toStationId);
            }
            foreach (SectionVolumeDto row in rows.SectionVolumes) ApplySection(row, sectionKind);
            FlowSummaryDto summary = rows.Summary;
            if (summary == null) return;
            foreach (StationSummaryDto row in summary.stationVolumes) row.stationGroupId = Id(row.stationId);
            foreach (StationSummaryDto row in summary.stationTotals) row.stationGroupId = Id(row.stationId);
            foreach (StationSummaryDto row in summary.stationRanking) row.stationGroupId = Id(row.stationId);
            foreach (WaitingSummaryDto row in summary.stationWaiting) row.stationGroupId = Id(row.stationId);
            foreach (OdSummaryDto row in summary.odFlows)
            {
                row.originStationGroupId = Id(row.originStationId);
                row.destinationStationGroupId = Id(row.destinationStationId);
            }
            foreach (TransferSummaryDto row in summary.transferFlows)
            {
                row.fromStationGroupId = Id(row.fromStationId);
                row.toStationGroupId = Id(row.toStationId);
            }
            foreach (SectionSummaryDto row in summary.sectionVolumes) ApplySection(row, sectionKind);
        }

        private void ApplySection(SectionVolumeDto row, SectionKind kind)
        {
            row.fromStationGroupId = kind == SectionKind.Track ? row.fromStationId : Id(row.fromStationId);
            row.toStationGroupId = kind == SectionKind.Track ? row.toStationId : Id(row.toStationId);
        }

        private void ApplySection(SectionSummaryDto row, SectionKind kind)
        {
            row.fromStationGroupId = kind == SectionKind.Track ? row.fromStationId : Id(row.fromStationId);
            row.toStationGroupId = kind == SectionKind.Track ? row.toStationId : Id(row.toStationId);
        }

        internal StationGroupDto[] ToDtos()
        {
            var result = new StationGroupDto[m_Groups.Length];
            for (int i = 0; i < m_Groups.Length; i++)
            {
                TramStationGroup group = m_Groups[i];
                result[i] = new StationGroupDto { stationGroupId = group.GroupId, stationName = group.Name,
                    memberStationIds = group.Members };
            }
            return result;
        }
    }
}
