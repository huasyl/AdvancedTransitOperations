using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Tools;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackChangeSource
    {
        private readonly EntityManager m_EntityManager;
        private readonly TrackChangeSourceSystem m_SourceSystem;
        private readonly TrackModelService m_TrackModel;
        private readonly List<TrackChangeCandidate> m_Candidates =
            new List<TrackChangeCandidate>(128);

        internal TrackChangeSource(
            EntityManager entityManager,
            TrackChangeSourceSystem sourceSystem,
            TrackModelService trackModel)
        {
            m_EntityManager = entityManager;
            m_SourceSystem = sourceSystem;
            m_TrackModel = trackModel;
        }

        internal void ConfirmChanges()
        {
            m_SourceSystem.DrainChanges(m_Candidates);
            if (m_Candidates.Count == 0)
                return;

            int submittedCount = 0;
            for (int i = 0; i < m_Candidates.Count; i++)
            {
                TrackChangeCandidate candidate = m_Candidates[i];
                if (candidate.IsDeleted)
                {
                    m_TrackModel.ConfirmLineDeleted(candidate.DeletedFact);
                    continue;
                }
                if (!TrySubmitLine(candidate.Line))
                    continue;

                m_Candidates[submittedCount++] = candidate;
            }

            for (int i = 0; i < submittedCount; i++)
            {
                TrackChangeCandidate candidate = m_Candidates[i];
                if (candidate.LayoutChanged)
                    m_TrackModel.MarkLayoutDirty(candidate.Line);
                else
                    m_TrackModel.MarkLineDirty(candidate.Line);
            }

            for (int i = 0; i < submittedCount; i++)
                m_TrackModel.ConfirmLineChange(m_Candidates[i].Line);

            m_Candidates.Clear();
        }

        internal void ResetPending()
        {
            m_SourceSystem.ResetPending();
            m_Candidates.Clear();
        }

        internal void Dispose()
        {
            m_Candidates.Clear();
        }

        private bool TrySubmitLine(Entity line)
        {
            if (line == Entity.Null
                || !m_EntityManager.Exists(line)
                || !m_EntityManager.HasComponent<TransportLine>(line)
                || m_EntityManager.HasComponent<Deleted>(line)
                || m_EntityManager.HasComponent<Temp>(line))
            {
                return false;
            }

            return TransportModeProfile.GetProfile(
                TransportModeResolver.Resolve(m_EntityManager, line)).Lifecycle == LifecycleKind.Rail;
        }
    }
}
