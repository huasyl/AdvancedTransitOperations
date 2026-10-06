using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Workbenches
{
    internal static class Buffer
    {
        internal static void Ensure(EntityManager entityManager, Entity city)
        {
            if (city != Entity.Null && !entityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
            {
                entityManager.AddBuffer<WorkbenchTimetableStateElement>(city);
            }
        }

        internal static string Read(DynamicBuffer<WorkbenchTimetableStateElement> buffer)
        {
            if (buffer.Length == 0)
            {
                return string.Empty;
            }

            WorkbenchTimetableStateElement[] ordered = new WorkbenchTimetableStateElement[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
            {
                ordered[i] = buffer[i];
            }

            Array.Sort(ordered, (left, right) => left.m_ChunkIndex.CompareTo(right.m_ChunkIndex));
            return Join(ordered);
        }

        internal static void Write(
            DynamicBuffer<WorkbenchTimetableStateElement> buffer,
            List<string> chunks)
        {
            if (buffer.Length > 0)
            {
                buffer.Clear();
            }

            if (chunks == null || chunks.Count == 0)
            {
                return;
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                buffer.Add(new WorkbenchTimetableStateElement
                {
                    m_ChunkIndex = i,
                    m_PayloadChunk = new FixedString4096Bytes(chunks[i] ?? string.Empty)
                });
            }
        }

        internal static List<string> Split(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return new List<string>();
            }

            List<string> chunks = new List<string>();
            int offset = 0;
            while (offset < payload.Length)
            {
                int chunkLength = Fit(payload, offset);
                chunks.Add(payload.Substring(offset, chunkLength));
                offset += chunkLength;
            }

            return chunks;
        }

        internal static string Join(IReadOnlyList<WorkbenchTimetableStateElement> chunks)
        {
            if (chunks == null || chunks.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder payload = new StringBuilder();
            for (int i = 0; i < chunks.Count; i++)
            {
                payload.Append(chunks[i].m_PayloadChunk.ToString());
            }

            return payload.ToString();
        }

        internal static int Fit(string payload, int offset)
        {
            int capacity = default(FixedString4096Bytes).Capacity;
            int bytes = 0;
            int end = offset;
            while (end < payload.Length)
            {
                char character = payload[end];
                bool pair = char.IsHighSurrogate(character) && end + 1 < payload.Length
                    && char.IsLowSurrogate(payload[end + 1]);
                int size = pair ? 4 : character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                if (bytes + size > capacity)
                    break;
                bytes += size;
                end += pair ? 2 : 1;
            }
            return end - offset;
        }
    }
}
