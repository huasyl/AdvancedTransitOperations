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
                if (chunkLength <= 0)
                {
                    chunkLength = 1;
                }

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
            int remaining = payload.Length - offset;
            if (remaining <= 0)
            {
                return 0;
            }

            int low = 1;
            int high = remaining;
            int best = 1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                int candidateLength = Fit(payload, offset, mid);
                if (candidateLength <= 0)
                {
                    high = mid - 1;
                    continue;
                }

                string candidate = payload.Substring(offset, candidateLength);
                FixedString4096Bytes fixedCandidate = candidate;
                if (fixedCandidate.ToString() == candidate)
                {
                    best = candidateLength;
                    low = candidateLength + 1;
                }
                else
                {
                    high = candidateLength - 1;
                }
            }

            return best;
        }

        private static int Fit(string payload, int offset, int proposedLength)
        {
            int endIndex = Math.Min(payload.Length, offset + proposedLength);
            if (endIndex <= offset)
            {
                return 0;
            }

            if (endIndex < payload.Length
                && char.IsHighSurrogate(payload[endIndex - 1])
                && char.IsLowSurrogate(payload[endIndex]))
            {
                endIndex--;
            }

            return endIndex - offset;
        }
    }
}
