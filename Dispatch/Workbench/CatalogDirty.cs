using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class CatalogDirty
    {
        private const uint CountGuardIntervalFrames = 90;
        private const uint PostDirtyRefreshWindowFrames = 120;
        private const uint PostDirtyProbeIntervalFrames = 30;

        private readonly EntityManager m_EntityManager;
        private readonly EntityQuery m_LineDirtyQuery;
        private readonly EntityQuery m_StopDirtyQuery;
        private readonly EntityQuery m_DepotDirtyQuery;
        private readonly EntityQuery m_ConnectedDirtyQuery;
        private readonly EntityQuery m_LineNameDirtyQuery;
        private readonly EntityQuery m_StopNameDirtyQuery;
        private readonly EntityQuery m_DepotNameDirtyQuery;
        private readonly EntityQuery m_BuildingNameDirtyQuery;
        private readonly EntityQuery m_LineCountQuery;
        private readonly EntityQuery m_DepotCountQuery;
        private readonly Action m_MarkDirty;
        private uint m_LastCountGuardFrame;
        private uint m_PostDirtyProbeUntilFrame;
        private uint m_NextPostDirtyProbeFrame;
        private int m_LastLineCount;
        private int m_LastDepotCount;
        private int m_LastLineSignature;
        private bool m_WasDirty;

        internal CatalogDirty(
            EntityManager entityManager,
            Action markDirty)
        {
            m_EntityManager = entityManager;
            m_MarkDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));

            m_LineDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<RouteWaypoint>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Disabled>(),
                    ComponentType.ReadOnly<Temp>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: false)
            });
            m_StopDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Routes.TransportStop>() },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_DepotDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.TransportDepot>() },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_ConnectedDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Connected>(),
                    ComponentType.ReadOnly<Waypoint>()
                },
                None = new[] { ComponentType.ReadOnly<Temp>() },
                Any = DirtyMarkers(includeBatchesUpdated: false)
            });
            m_LineNameDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<TransportLine>()
                },
                None = new[] { ComponentType.ReadOnly<Temp>() },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_StopNameDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<Game.Routes.TransportStop>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_DepotNameDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<Game.Buildings.TransportDepot>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_BuildingNameDirtyQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CustomName>(),
                    ComponentType.ReadOnly<Building>()
                },
                Any = DirtyMarkers(includeBatchesUpdated: true)
            });
            m_LineCountQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<RouteWaypoint>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Disabled>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
            m_DepotCountQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.TransportDepot>(),
                ComponentType.Exclude<Deleted>());
            ResetCountGuard(0);
        }

        internal void Reset()
        {
            m_WasDirty = false;
            m_PostDirtyProbeUntilFrame = 0;
            m_NextPostDirtyProbeFrame = 0;
            ResetCountGuard(0);
        }

        internal void Check(uint nowFrame)
        {
            bool lineDirty = IsLineDirty();
            bool dirty = lineDirty || IsNonLineDirty();
            bool lineCountChanged;
            bool countChanged = CountChanged(nowFrame, out lineCountChanged);
            if (countChanged)
            {
                dirty = true;
            }

            if (dirty)
            {
                if (!m_WasDirty || countChanged)
                {
                    m_MarkDirty();
                    if (lineDirty || lineCountChanged)
                    {
                        ArmPostDirtyProbe(nowFrame);
                    }
                }
                m_WasDirty = true;
            }
            else
            {
                m_WasDirty = false;
            }

            if (ProbeLineSignatureChanged(nowFrame))
            {
                m_MarkDirty();
            }
        }

        private bool IsLineDirty()
        {
            return !m_LineDirtyQuery.IsEmptyIgnoreFilter
                || !m_ConnectedDirtyQuery.IsEmptyIgnoreFilter
                || !m_LineNameDirtyQuery.IsEmptyIgnoreFilter;
        }

        private bool IsNonLineDirty()
        {
            return !m_StopDirtyQuery.IsEmptyIgnoreFilter
                || !m_DepotDirtyQuery.IsEmptyIgnoreFilter
                || !m_StopNameDirtyQuery.IsEmptyIgnoreFilter
                || !m_DepotNameDirtyQuery.IsEmptyIgnoreFilter
                || !m_BuildingNameDirtyQuery.IsEmptyIgnoreFilter;
        }

        private bool CountChanged(uint nowFrame, out bool lineCountChanged)
        {
            lineCountChanged = false;
            if (nowFrame - m_LastCountGuardFrame < CountGuardIntervalFrames)
            {
                return false;
            }

            m_LastCountGuardFrame = nowFrame;
            int lineCount = m_LineCountQuery.CalculateEntityCount();
            int depotCount = m_DepotCountQuery.CalculateEntityCount();
            lineCountChanged = lineCount != m_LastLineCount;
            bool depotCountChanged = depotCount != m_LastDepotCount;
            if (!lineCountChanged && !depotCountChanged)
            {
                return false;
            }

            m_LastLineCount = lineCount;
            m_LastDepotCount = depotCount;
            return true;
        }

        private void ResetCountGuard(uint nowFrame)
        {
            m_LastCountGuardFrame = nowFrame;
            m_LastLineCount = m_LineCountQuery.CalculateEntityCount();
            m_LastDepotCount = m_DepotCountQuery.CalculateEntityCount();
            m_LastLineSignature = ComputeLineSignature();
        }

        private void ArmPostDirtyProbe(uint nowFrame)
        {
            m_LastLineSignature = ComputeLineSignature();
            m_PostDirtyProbeUntilFrame = nowFrame + PostDirtyRefreshWindowFrames;
            m_NextPostDirtyProbeFrame = nowFrame + PostDirtyProbeIntervalFrames;
        }

        private bool ProbeLineSignatureChanged(uint nowFrame)
        {
            if (m_NextPostDirtyProbeFrame == 0 || nowFrame < m_NextPostDirtyProbeFrame)
            {
                return false;
            }

            if (nowFrame > m_PostDirtyProbeUntilFrame)
            {
                m_PostDirtyProbeUntilFrame = 0;
                m_NextPostDirtyProbeFrame = 0;
                return false;
            }

            m_NextPostDirtyProbeFrame = nowFrame + PostDirtyProbeIntervalFrames;
            int signature = ComputeLineSignature();
            if (signature == m_LastLineSignature)
            {
                return false;
            }

            m_LastLineSignature = signature;
            return true;
        }

        private int ComputeLineSignature()
        {
            List<int> parts = new List<int>();
            NativeArray<Entity> lines = m_LineCountQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    parts.Add(ComputeLinePart(lines[i]));
                }
            }
            finally
            {
                if (lines.IsCreated) lines.Dispose();
            }

            parts.Sort();
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + parts.Count;
                for (int i = 0; i < parts.Count; i++)
                {
                    hash = hash * 31 + parts[i];
                }
                return hash;
            }
        }

        private int ComputeLinePart(Entity line)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + line.Index;
                hash = hash * 31 + line.Version;
                if (m_EntityManager.HasBuffer<RouteWaypoint>(line))
                {
                    DynamicBuffer<RouteWaypoint> waypoints = m_EntityManager.GetBuffer<RouteWaypoint>(line, true);
                    hash = hash * 31 + waypoints.Length;
                    for (int i = 0; i < waypoints.Length; i++)
                    {
                        hash = HashWaypoint(hash, i, waypoints[i].m_Waypoint);
                    }
                }
                else
                {
                    hash = hash * 31;
                }

                hash = hash * 31 + (m_EntityManager.HasComponent<RouteNumber>(line)
                    ? m_EntityManager.GetComponentData<RouteNumber>(line).m_Number
                    : -1);

                if (m_EntityManager.HasComponent<PrefabRef>(line))
                {
                    Entity prefab = m_EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                    hash = hash * 31 + prefab.Index;
                    hash = hash * 31 + prefab.Version;
                }

                if (DispatchLineEligibility.TryGetTransportLineData(m_EntityManager, line, out TransportLineData lineData))
                {
                    hash = hash * 31 + (int)lineData.m_TransportType;
                    hash = hash * 31 + (lineData.m_PassengerTransport ? 1 : 0);
                    hash = hash * 31 + (lineData.m_CargoTransport ? 1 : 0);
                }

                return hash;
            }
        }

        private int HashWaypoint(int hash, int index, Entity waypoint)
        {
            unchecked
            {
                hash = hash * 31 + index;
                hash = hash * 31 + waypoint.Index;
                hash = hash * 31 + waypoint.Version;
                if (waypoint == Entity.Null || !m_EntityManager.Exists(waypoint))
                {
                    return hash;
                }

                if (m_EntityManager.HasComponent<Connected>(waypoint))
                {
                    Entity connected = m_EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                    hash = hash * 31 + connected.Index;
                    hash = hash * 31 + connected.Version;
                }

                if (m_EntityManager.HasComponent<RouteLane>(waypoint))
                {
                    RouteLane lane = m_EntityManager.GetComponentData<RouteLane>(waypoint);
                    hash = hash * 31 + lane.m_StartLane.Index;
                    hash = hash * 31 + lane.m_StartLane.Version;
                    hash = hash * 31 + lane.m_EndLane.Index;
                    hash = hash * 31 + lane.m_EndLane.Version;
                    hash = hash * 31 + (int)Math.Round(lane.m_StartCurvePos * 1000f);
                    hash = hash * 31 + (int)Math.Round(lane.m_EndCurvePos * 1000f);
                }

                return hash;
            }
        }

        private static ComponentType[] DirtyMarkers(bool includeBatchesUpdated)
        {
            if (!includeBatchesUpdated)
            {
                return new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Updated>()
                };
            }

            return new[]
            {
                ComponentType.ReadOnly<Created>(),
                ComponentType.ReadOnly<Deleted>(),
                ComponentType.ReadOnly<Updated>(),
                ComponentType.ReadOnly<BatchesUpdated>()
            };
        }
    }
}
