using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Persistence
{
    internal readonly struct PendingLineStructureRecord
    {
        internal readonly Entity Line;
        internal readonly string LineId;
        internal readonly string OldStopSig;
        internal readonly bool MissingOldBaseline;

        internal PendingLineStructureRecord(
            Entity line,
            string lineId,
            string oldStopSig,
            bool missingOldBaseline)
        {
            Line = line;
            LineId = lineId ?? string.Empty;
            OldStopSig = oldStopSig ?? string.Empty;
            MissingOldBaseline = missingOldBaseline;
        }
    }

    internal sealed class LineStructurePendingStore
    {
        internal const int CurrentVersion = 1;
        private readonly ModRuntimeHostSystem m_Runtime;

        internal LineStructurePendingStore(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal void Ensure()
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;
            if (!m_Runtime.EntityManager.HasBuffer<PendingLineStructureElement>(city))
                m_Runtime.EntityManager.AddBuffer<PendingLineStructureElement>(city);
        }

        internal void Save(IReadOnlyList<PendingLineStructureRecord> records)
        {
            Ensure();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<PendingLineStructureElement>(city))
            {
                return;
            }

            DynamicBuffer<PendingLineStructureElement> buffer =
                m_Runtime.EntityManager.GetBuffer<PendingLineStructureElement>(city);
            buffer.Clear();
            if (records == null)
                return;

            for (int i = 0; i < records.Count; i++)
            {
                PendingLineStructureRecord record = records[i];
                if (record.Line == Entity.Null || string.IsNullOrEmpty(record.LineId))
                    continue;

                buffer.Add(new PendingLineStructureElement
                {
                    m_Version = CurrentVersion,
                    m_LineEntity = record.Line,
                    m_LineId = record.LineId,
                    m_OldStopSig = record.OldStopSig,
                    m_MissingOldBaseline = record.MissingOldBaseline ? (byte)1 : (byte)0
                });
            }
        }

        internal List<PendingLineStructureRecord> Restore()
        {
            List<PendingLineStructureRecord> records = new List<PendingLineStructureRecord>();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<PendingLineStructureElement>(city))
            {
                return records;
            }

            DynamicBuffer<PendingLineStructureElement> buffer =
                m_Runtime.EntityManager.GetBuffer<PendingLineStructureElement>(city, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                PendingLineStructureElement element = buffer[i];
                if (element.m_Version != CurrentVersion
                    || element.m_LineEntity == Entity.Null
                    || string.IsNullOrEmpty(element.m_LineId.ToString())
                    || (element.m_MissingOldBaseline != 0 && element.m_MissingOldBaseline != 1))
                {
                    continue;
                }

                records.Add(new PendingLineStructureRecord(
                    element.m_LineEntity,
                    element.m_LineId.ToString(),
                    element.m_OldStopSig.ToString(),
                    element.m_MissingOldBaseline != 0));
            }
            return records;
        }
    }
}
