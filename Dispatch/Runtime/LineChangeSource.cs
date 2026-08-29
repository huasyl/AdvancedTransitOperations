using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Tools;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class LineChangeSource
    {
        private readonly EntityManager m_EntityManager;
        private readonly LineChangeSourceSystem m_SourceSystem;
        private readonly TrackModelService m_TrackModel;
        private readonly LineProfile m_LineProfile;
        private readonly LineStructureInvalidator m_LineStructureInvalidator;
        private readonly List<LineChangeCandidate> m_Candidates =
            new List<LineChangeCandidate>(128);

        internal LineChangeSource(
            EntityManager entityManager,
            LineChangeSourceSystem sourceSystem,
            TrackModelService trackModel,
            LineProfile lineProfile,
            LineStructureInvalidator lineStructureInvalidator)
        {
            m_EntityManager = entityManager;
            m_SourceSystem = sourceSystem;
            m_TrackModel = trackModel;
            m_LineProfile = lineProfile;
            m_LineStructureInvalidator = lineStructureInvalidator;
        }

        internal void ConfirmChanges()
        {
            m_SourceSystem.DrainChanges(m_Candidates);
            if (m_Candidates.Count == 0)
                return;

            int submittedCount = 0;
            for (int i = 0; i < m_Candidates.Count; i++)
            {
                LineChangeCandidate candidate = m_Candidates[i];
                if (candidate.IsDeleted)
                {
                    SubmitRoadDeleted(candidate);
                    continue;
                }
                if (!SubmitRailChanges(candidate))
                    continue;

                m_Candidates[submittedCount++] = candidate;
            }

            for (int i = 0; i < submittedCount; i++)
            {
                LineChangeCandidate candidate = m_Candidates[i];
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

        private bool SubmitRailChanges(LineChangeCandidate candidate)
        {
            return TrySubmitLine(candidate.Line);
        }

        private void SubmitRoadDeleted(LineChangeCandidate candidate)
        {
            TransitMode mode = candidate.DeletedFact.Mode;
            LifecycleKind lifecycle = TransportModeProfile.GetProfile(mode).Lifecycle;
            if (lifecycle == LifecycleKind.Rail)
            {
                m_TrackModel.ConfirmLineDeleted(candidate.DeletedFact);
                return;
            }

            if (lifecycle != LifecycleKind.Road
                || m_LineStructureInvalidator == null
                || m_LineProfile == null)
            {
                return;
            }

            m_LineProfile.TryReadRoadRoute(
                candidate.Line,
                out LineProfile.RoadRouteSnapshot oldRoute);
            if (!m_LineStructureInvalidator.RequestRoadDeleted(
                    candidate.Line,
                    candidate.DeletedFact.LineKey,
                    mode,
                    oldRoute))
            {
                return;
            }

            m_SourceSystem.AcknowledgeRoadDeleted(candidate.Line);
        }

    }
}
