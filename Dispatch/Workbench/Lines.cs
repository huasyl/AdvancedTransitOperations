using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Lines
    {
        private readonly Action m_LoadPersist;
        private readonly Action m_LoadApplied;
        private readonly Func<List<WorkbenchLineRuntime>> m_Source;
        private readonly Func<string, AppliedLine, string> m_Kind;
        private readonly Func<Entity, string> m_Color;

        internal Lines(
            Action loadPersist,
            Action loadApplied,
            Func<List<WorkbenchLineRuntime>> source,
            Func<string, AppliedLine, string> kind,
            Func<Entity, string> color)
        {
            m_LoadPersist = loadPersist ?? throw new ArgumentNullException(nameof(loadPersist));
            m_LoadApplied = loadApplied ?? throw new ArgumentNullException(nameof(loadApplied));
            m_Source = source ?? throw new ArgumentNullException(nameof(source));
            m_Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            m_Color = color ?? throw new ArgumentNullException(nameof(color));
        }

        internal List<WorkbenchLineRuntime> All(IReadOnlyDictionary<string, AppliedLine> appliedLines)
        {
            m_LoadPersist();
            m_LoadApplied();

            List<WorkbenchLineRuntime> lines = m_Source() ?? new List<WorkbenchLineRuntime>();
            for (int i = 0; i < lines.Count; i++)
            {
                WorkbenchLineRuntime line = lines[i];
                if (line == null)
                    continue;

                line.Id = Drafts.Key(line.Id);
                AppliedLine applied = null;
                appliedLines?.TryGetValue(line.Id, out applied);
                string kind = m_Kind(line.Id, applied);
                line.Kind = string.IsNullOrEmpty(kind) ? "local" : kind;
                line.Color = m_Color(line.Entity);
            }

            return lines;
        }
    }
}
