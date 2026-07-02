using Game;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.UI.Tooltip;
using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting;

namespace RapidTransitMod
{
    public partial class DevSightTooltipSystem : TooltipSystemBase
    {
        private static Type s_MoveItMitType;
        private static Type s_MoveItHoverManagerType;
        private static Type s_MoveItHoverHolderType;
        private static Type s_MoveItMVDefinitionType;
        private static Type s_MoveItSearcherType;
        private static Type s_MoveItFiltersType;
        private static Type s_MoveItSearcherResultType;
        private static Type s_MoveItMioCommonType;
        private static Type s_MoveItInteractionFlagsType;
        private static FieldInfo s_MoveItHoverField;
        private static PropertyInfo s_MoveItTopHoveredProperty;
        private static PropertyInfo s_MoveItHolderDefinitionProperty;
        private static FieldInfo s_MoveItDefinitionEntityField;
        private static FieldInfo s_MoveItDefinitionParentField;
        private static FieldInfo s_MoveItFilteringField;
        private static FieldInfo s_MoveItPointerPosField;
        private static FieldInfo s_MoveItRaycastSurfaceField;
        private static PropertyInfo s_MoveItIsManipulatingProperty;
        private static ConstructorInfo s_MoveItSearcherCtor;
        private static MethodInfo s_MoveItSearchRayMethod;
        private static FieldInfo s_MoveItSearcherResultsField;
        private static FieldInfo s_MoveItSearcherResultEntityField;
        private static MethodInfo s_MoveItFilterGetMaskMethod;
        private static MethodInfo s_MoveItRaycastSurfaceGetResultsMethod;
        private static FieldInfo s_MoveItMioCommonOwnerField;
        private static FieldInfo s_MoveItMioCommonFlagsField;
        private static MethodInfo s_EntityManagerGetComponentDataOpenMethod;

        private struct DevSightProbe
        {
            public Entity RawHitEntity;
            public Entity RaycastOwner;
            public Entity DirectOwner;
            public Entity NetEntity;
            public Entity FallbackTargetEntity;
            public bool NetHasSubLane;
            public int SubLaneCount;
            public int TrackSubLaneCount;
            public int TrainTrackSubLaneCount;
            public Entity FirstTrackSubLane;
            public Entity FirstTrainTrackSubLane;
            public Game.Net.TrackTypes FirstTrackTypes;
            public Game.Net.TrackTypes FirstTrainTrackTypes;
        }

        private struct DevSightPanelState
        {
            public bool Visible;
            public string Source;
            public string SummaryText;
        }

        private RaycastSystem m_RaycastSystem = null!;
        private ToolSystem m_ToolSystem = null!;
        private ToolRaycastSystem m_ToolRaycastSystem = null!;
        private DevSightRaycastCollectorSystem m_RaycastCollectorSystem = null!;
        private CameraUpdateSystem m_CameraUpdateSystem = null!;
        private bool m_ToggleArmed = true;
        private bool m_Enabled;
        private Entity m_LastMoveItEntity = Entity.Null;
        private static DevSightPanelState s_PanelState;

        public static bool TryGetPanelState(out string source, out string summaryText)
        {
            source = s_PanelState.Source ?? string.Empty;
            summaryText = s_PanelState.SummaryText ?? string.Empty;
            return s_PanelState.Visible && summaryText.Length > 0;
        }

        private static void SetPanelState(bool visible, string source, string summaryText)
        {
            s_PanelState = new DevSightPanelState
            {
                Visible = visible,
                Source = source ?? string.Empty,
                SummaryText = summaryText ?? string.Empty
            };
        }

        private static void ClearPanelState()
        {
            SetPanelState(false, string.Empty, string.Empty);
        }

        private static void EnsureMoveItReflection()
        {
            if (s_MoveItMitType != null)
                return;

            Assembly moveItAssembly = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly.GetName().Name == "MoveIt")
                {
                    moveItAssembly = assembly;
                    break;
                }
            }

            if (moveItAssembly == null)
                return;

            s_MoveItMitType = moveItAssembly.GetType("MoveIt.Tool.MIT");
            s_MoveItHoverManagerType = moveItAssembly.GetType("MoveIt.Managers.HoverManager");
            s_MoveItHoverHolderType = moveItAssembly.GetType("MoveIt.Managers.HoverHolder");
            s_MoveItMVDefinitionType = moveItAssembly.GetType("MoveIt.Moveables.MVDefinition");
            s_MoveItSearcherType = moveItAssembly.GetType("MoveIt.Searcher.Searcher");
            s_MoveItFiltersType = moveItAssembly.GetType("MoveIt.Searcher.Filters");
            s_MoveItSearcherResultType = moveItAssembly.GetType("MoveIt.Searcher.Result");
            s_MoveItMioCommonType = moveItAssembly.GetType("MoveIt.Overlays.MIO_Common");
            s_MoveItInteractionFlagsType = moveItAssembly.GetType("MoveIt.Tool.InteractionFlags");

            s_MoveItHoverField = s_MoveItMitType?.GetField("Hover", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItTopHoveredProperty = s_MoveItHoverManagerType?.GetProperty("TopHovered", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItHolderDefinitionProperty = s_MoveItHoverHolderType?.GetProperty("Definition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItDefinitionEntityField = s_MoveItMVDefinitionType?.GetField("m_Entity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItDefinitionParentField = s_MoveItMVDefinitionType?.GetField("m_Parent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItFilteringField = s_MoveItMitType?.GetField("Filtering", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItPointerPosField = s_MoveItMitType?.GetField("m_PointerPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItRaycastSurfaceField = s_MoveItMitType?.GetField("m_RaycastSurface", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItIsManipulatingProperty = s_MoveItMitType?.GetProperty("IsManipulating", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItSearcherCtor = s_MoveItSearcherType?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { s_MoveItFiltersType, typeof(bool), typeof(Unity.Mathematics.float3) }, null);
            s_MoveItSearchRayMethod = s_MoveItSearcherType?.GetMethod("SearchRay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItSearcherResultsField = s_MoveItSearcherType?.GetField("m_Results", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItSearcherResultEntityField = s_MoveItSearcherResultType?.GetField("m_Entity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItFilterGetMaskMethod = s_MoveItFilteringField?.FieldType.GetMethod("GetMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItRaycastSurfaceGetResultsMethod = s_MoveItRaycastSurfaceField?.FieldType.GetMethod("GetResults", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItMioCommonOwnerField = s_MoveItMioCommonType?.GetField("m_Owner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_MoveItMioCommonFlagsField = s_MoveItMioCommonType?.GetField("m_Flags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            MethodInfo[] entityManagerMethods = typeof(EntityManager).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < entityManagerMethods.Length; i++)
            {
                MethodInfo method = entityManagerMethods[i];
                if (method.Name == "GetComponentData"
                    && method.IsGenericMethodDefinition)
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Entity))
                    {
                        s_EntityManagerGetComponentDataOpenMethod = method;
                        break;
                    }
                }
            }
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_RaycastSystem = World.GetOrCreateSystemManaged<RaycastSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_ToolRaycastSystem = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            m_RaycastCollectorSystem = World.GetOrCreateSystemManaged<DevSightRaycastCollectorSystem>();
            m_CameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            bool modifierDown = Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftAlt);
            bool toggleDown = Input.GetKey(KeyCode.BackQuote);
            if (!toggleDown)
            {
                m_ToggleArmed = true;
            }
            else if (modifierDown && m_ToggleArmed)
            {
                m_ToggleArmed = false;
                m_Enabled = !m_Enabled;
                DevSightRaycastCollectorSystem.SetEnabled(m_Enabled);
                Mod.log.Info(m_Enabled ? "[DevSight] enabled" : "[DevSight] disabled");
                if (!m_Enabled)
                    ClearPanelState();
            }

            if (!m_Enabled)
            {
                ClearPanelState();
                return;
            }

            if (!m_CameraUpdateSystem.TryGetViewer(out var viewer))
            {
                SetPanelState(true, "viewer", "DevSight: no-viewer");
                return;
            }

            if (TryGetMoveItVisibleEntity(out Entity moveItVisibleEntity))
            {
                Entity resolvedEntity = ResolveTrackModelTargetFromOwnerChain(moveItVisibleEntity);

                if (resolvedEntity == Entity.Null)
                {
                    Entity netEntity = ResolveNetEntityFromOwnerChain(moveItVisibleEntity);

                    if (netEntity != Entity.Null && World.EntityManager.HasBuffer<Game.Net.SubLane>(netEntity))
                    {
                        DynamicBuffer<Game.Net.SubLane> subLanes = World.EntityManager.GetBuffer<Game.Net.SubLane>(netEntity, true);

                        for (int i = 0; i < subLanes.Length; i++)
                        {
                            Game.Net.SubLane subLane = subLanes[i];
                            if ((subLane.m_PathMethods & PathMethod.Track) == 0)
                                continue;

                            Entity laneEntity = subLane.m_SubLane;
                            if (World.EntityManager.HasComponent<Game.Net.TrackLane>(laneEntity))
                            {
                                if (TryGetTrackLaneType(laneEntity, out Game.Net.TrackTypes trackTypes)
                                    && (trackTypes & Game.Net.TrackTypes.Train) != 0)
                                {
                                    resolvedEntity = laneEntity;
                                    break;
                                }
                            }
                        }
                    }
                }

                Entity finalEntity = resolvedEntity != Entity.Null ? resolvedEntity : moveItVisibleEntity;
                m_LastMoveItEntity = finalEntity;

                DispatchRuntimeSystem moveItControl = DispatchRuntimeSystem.Instance;
                string summaryText = moveItControl != null
                    ? moveItControl.m_TrackModel.BuildDevSightTooltipSummary(finalEntity)
                    : "target  " + FormatEntity(moveItVisibleEntity);
                SetPanelState(true, "MoveIt visible", summaryText);
                return;
            }

            if (TryGetMoveItOverlayOwner(out Entity moveItOverlayOwner))
            {
                DispatchRuntimeSystem moveItControl = DispatchRuntimeSystem.Instance;
                string summaryText = moveItControl != null
                    ? moveItControl.m_TrackModel.BuildDevSightTooltipSummary(moveItOverlayOwner)
                    : "target  " + FormatEntity(moveItOverlayOwner);
                SetPanelState(true, "MoveIt overlay", summaryText);
                return;
            }

            if (TryGetMoveItSearcherEntity(out Entity moveItEntity))
            {
                DispatchRuntimeSystem moveItControl = DispatchRuntimeSystem.Instance;
                string summaryText = moveItControl != null
                    ? moveItControl.m_TrackModel.BuildDevSightTooltipSummary(moveItEntity)
                    : "target  " + FormatEntity(moveItEntity);
                SetPanelState(true, "MoveIt searcher", summaryText);
                return;
            }

            NativeArray<RaycastResult> moveItResults = default;
            if (IsMoveItActive())
                moveItResults = m_RaycastSystem.GetResult(m_ToolRaycastSystem);
            NativeArray<RaycastResult> collectorResults = m_RaycastSystem.GetResult(m_RaycastCollectorSystem);
            bool hasMoveItResult = moveItResults.IsCreated && moveItResults.Length > 0;
            bool hasCollectorResult = collectorResults.IsCreated && collectorResults.Length > 0;
            if (!hasMoveItResult && !hasCollectorResult)
            {
                SetPanelState(true, "raycast", "DevSight: no-raycast-result");
                return;
            }

            RaycastResult result = hasMoveItResult
                ? SelectBestResult(moveItResults)
                : SelectBestResult(collectorResults);
            DevSightProbe probe = ProbeTrackLane(result);
            string summary = BuildTooltipText(result, probe);
            SetPanelState(true, hasMoveItResult ? "MoveIt raw" : "collector", summary);
        }

        private bool TryGetMoveItOverlayOwner(out Entity entity)
        {
            entity = Entity.Null;
            EnsureMoveItReflection();
            if (!IsMoveItActive()
                || s_MoveItMioCommonType == null
                || s_MoveItMioCommonOwnerField == null
                || s_MoveItMioCommonFlagsField == null
                || s_EntityManagerGetComponentDataOpenMethod == null)
            {
                return false;
            }

            EntityQuery query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly(s_MoveItMioCommonType) }
            });
            NativeArray<Entity> overlayEntities = query.ToEntityArray(Allocator.Temp);
            try
            {
                int bestScore = int.MinValue;
                MethodInfo getMioCommonMethod = s_EntityManagerGetComponentDataOpenMethod.MakeGenericMethod(s_MoveItMioCommonType);
                for (int i = 0; i < overlayEntities.Length; i++)
                {
                    Entity overlayEntity = overlayEntities[i];
                    object boxedCommon = getMioCommonMethod.Invoke(EntityManager, new object[] { overlayEntity });
                    if (boxedCommon == null)
                        continue;

                    object flagsValue = s_MoveItMioCommonFlagsField.GetValue(boxedCommon);
                    int flags = flagsValue != null ? Convert.ToInt32(flagsValue) : 0;
                    int score = ScoreMoveItOverlayFlags(flags);
                    if (score <= 0)
                        continue;

                    object ownerValue = s_MoveItMioCommonOwnerField.GetValue(boxedCommon);
                    if (!(ownerValue is Entity owner) || owner == Entity.Null)
                        continue;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        entity = owner;
                    }
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                overlayEntities.Dispose();
            }

            return entity != Entity.Null;
        }

        private int ScoreMoveItOverlayFlags(int flags)
        {
            int score = 0;
            if ((flags & 0x10) != 0) // ToolHover
                score += 8;
            if ((flags & 0x1) != 0) // Hovering
                score += 6;
            if ((flags & 0x20) != 0) // ToolParentHover
                score += 4;
            if ((flags & 0x80) != 0) // ParentHovering
                score += 2;
            return score;
        }

        private bool TryGetMoveItVisibleEntity(out Entity entity)
        {
            entity = Entity.Null;
            EnsureMoveItReflection();

            if (!IsMoveItActive())
            {
                if (m_LastMoveItEntity != Entity.Null)
                    m_LastMoveItEntity = Entity.Null;
                return false;
            }

            if (s_MoveItHoverField == null
                || s_MoveItTopHoveredProperty == null
                || s_MoveItHolderDefinitionProperty == null
                || s_MoveItDefinitionEntityField == null)
            {
                if (m_LastMoveItEntity != Entity.Null)
                    m_LastMoveItEntity = Entity.Null;
                return false;
            }

            ToolBaseSystem activeTool = m_ToolSystem.activeTool;
            object hoverManager = s_MoveItHoverField.GetValue(activeTool);
            if (hoverManager == null)
            {
                if (m_LastMoveItEntity != Entity.Null)
                    m_LastMoveItEntity = Entity.Null;
                return false;
            }

            object topHovered = s_MoveItTopHoveredProperty.GetValue(hoverManager);
            if (topHovered == null)
            {
                if (m_LastMoveItEntity != Entity.Null)
                    m_LastMoveItEntity = Entity.Null;
                return false;
            }

            object definition = s_MoveItHolderDefinitionProperty.GetValue(topHovered);
            if (definition == null)
            {
                if (m_LastMoveItEntity != Entity.Null)
                    m_LastMoveItEntity = Entity.Null;
                return false;
            }

            object entityValue = s_MoveItDefinitionEntityField.GetValue(definition);
            if (entityValue is Entity definitionEntity && definitionEntity != Entity.Null)
            {
                entity = definitionEntity;
                if (entity != m_LastMoveItEntity)
                    m_LastMoveItEntity = entity;
                return true;
            }

            if (s_MoveItDefinitionParentField != null)
            {
                object parentValue = s_MoveItDefinitionParentField.GetValue(definition);
                if (parentValue is Entity parentEntity && parentEntity != Entity.Null)
                {
                    entity = parentEntity;
                    if (entity != m_LastMoveItEntity)
                        m_LastMoveItEntity = entity;
                    return true;
                }
            }

            if (m_LastMoveItEntity != Entity.Null)
            {
                m_LastMoveItEntity = Entity.Null;
            }
            return false;
        }

        private bool TryGetMoveItSearcherEntity(out Entity entity)
        {
            entity = Entity.Null;
            EnsureMoveItReflection();
            if (!IsMoveItActive()
                || s_MoveItSearcherCtor == null
                || s_MoveItSearchRayMethod == null
                || s_MoveItSearcherResultsField == null
                || s_MoveItSearcherResultEntityField == null
                || s_MoveItPointerPosField == null)
            {
                return false;
            }

            ToolBaseSystem activeTool = m_ToolSystem.activeTool;
            if (activeTool == null)
                return false;

            object filterMask = s_MoveItFiltersType != null
                ? Enum.ToObject(s_MoveItFiltersType, 0x10 | 0x20 | 0x40 | 0x80)
                : null;
            if (s_MoveItFilteringField != null && s_MoveItFilterGetMaskMethod != null)
            {
                object filtering = s_MoveItFilteringField.GetValue(activeTool);
                if (filtering != null)
                    filterMask = s_MoveItFilterGetMaskMethod.Invoke(filtering, Array.Empty<object>());
            }
            if (filterMask == null)
                return false;

            bool isManipulating = s_MoveItIsManipulatingProperty != null
                && s_MoveItIsManipulatingProperty.GetValue(activeTool) is bool moveItManipulating
                && moveItManipulating;
            object pointerPosValue = s_MoveItPointerPosField.GetValue(activeTool);
            if (!(pointerPosValue is Unity.Mathematics.float3 pointerPos))
                return false;

            object searcher = s_MoveItSearcherCtor.Invoke(new object[] { filterMask, isManipulating, pointerPos });
            try
            {
                NativeArray<RaycastResult> networkResults = m_RaycastSystem.GetResult(m_ToolRaycastSystem);
                object surfaceResults = default(NativeArray<RaycastResult>);
                if (s_MoveItRaycastSurfaceField != null && s_MoveItRaycastSurfaceGetResultsMethod != null)
                {
                    object raycastSurface = s_MoveItRaycastSurfaceField.GetValue(activeTool);
                    if (raycastSurface != null)
                        surfaceResults = s_MoveItRaycastSurfaceGetResultsMethod.Invoke(raycastSurface, Array.Empty<object>());
                }

                s_MoveItSearchRayMethod.Invoke(searcher, new object[]
                {
                    ToolRaycastSystem.CalculateRaycastLine(Camera.main),
                    networkResults,
                    surfaceResults,
                    true
                });

                object results = s_MoveItSearcherResultsField.GetValue(searcher);
                if (results == null)
                    return false;

                PropertyInfo lengthProperty = results.GetType().GetProperty("Length", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo getItemMethod = results.GetType().GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public);
                if (lengthProperty == null || getItemMethod == null)
                    return false;

                int length = (int)lengthProperty.GetValue(results);
                for (int i = 0; i < length; i++)
                {
                    object searcherResult = getItemMethod.Invoke(results, new object[] { i });
                    object entityValue = s_MoveItSearcherResultEntityField.GetValue(searcherResult);
                    if (entityValue is Entity searchEntity && searchEntity != Entity.Null)
                    {
                        entity = searchEntity;
                        return true;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (searcher is IDisposable disposable)
                    disposable.Dispose();
            }

            return false;
        }

        private bool IsMoveItActive()
        {
            EnsureMoveItReflection();
            ToolBaseSystem activeTool = m_ToolSystem?.activeTool;
            return activeTool != null && s_MoveItMitType != null && activeTool.GetType() == s_MoveItMitType;
        }

        private RaycastResult SelectBestResult(NativeArray<RaycastResult> results)
        {
            if (!results.IsCreated || results.Length == 0)
                return default;

            int bestIndex = 0;
            int bestScore = ScoreResult(results[0]);
            for (int i = 1; i < results.Length; i++)
            {
                int score = ScoreResult(results[i]);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestIndex = i;
            }

            return results[bestIndex];
        }

        private int ScoreResult(RaycastResult result)
        {
            int score = 0;
            if (ResolveTrackModelTargetFromOwnerChain(result.m_Hit.m_HitEntity) != Entity.Null)
                score += 8;
            if (ResolveTrackModelTargetFromOwnerChain(result.m_Owner) != Entity.Null)
                score += 6;
            if (ResolveNetEntityFromOwnerChain(result.m_Hit.m_HitEntity) != Entity.Null)
                score += 4;
            if (ResolveNetEntityFromOwnerChain(result.m_Owner) != Entity.Null)
                score += 2;
            return score;
        }

        private DevSightProbe ProbeTrackLane(RaycastResult result)
        {
            DevSightProbe probe = new DevSightProbe
            {
                RawHitEntity = result.m_Hit.m_HitEntity,
                RaycastOwner = result.m_Owner,
                DirectOwner = ResolveDirectOwner(result.m_Hit.m_HitEntity)
            };

            probe.FallbackTargetEntity = ResolveTrackModelTargetFromOwnerChain(probe.RawHitEntity);
            if (probe.FallbackTargetEntity == Entity.Null)
                probe.FallbackTargetEntity = ResolveTrackModelTargetFromOwnerChain(probe.RaycastOwner);
            if (probe.FallbackTargetEntity == Entity.Null)
                probe.FallbackTargetEntity = ResolveTrackModelTargetFromOwnerChain(probe.DirectOwner);

            probe.NetEntity = ResolveNetEntity(probe.RawHitEntity, probe.RaycastOwner, probe.DirectOwner);
            if (probe.NetEntity == Entity.Null)
                return probe;

            probe.NetHasSubLane = World.EntityManager.HasBuffer<Game.Net.SubLane>(probe.NetEntity);
            if (!probe.NetHasSubLane)
                return probe;

            DynamicBuffer<Game.Net.SubLane> subLanes = World.EntityManager.GetBuffer<Game.Net.SubLane>(probe.NetEntity, true);
            probe.SubLaneCount = subLanes.Length;
            for (int i = 0; i < subLanes.Length; i++)
            {
                Game.Net.SubLane subLane = subLanes[i];
                if ((subLane.m_PathMethods & PathMethod.Track) == 0)
                    continue;

                Entity laneEntity = subLane.m_SubLane;
                probe.TrackSubLaneCount++;
                if (probe.FirstTrackSubLane == Entity.Null)
                    probe.FirstTrackSubLane = laneEntity;

                if (!TryGetTrackLaneType(laneEntity, out Game.Net.TrackTypes trackTypes))
                    continue;

                if (probe.FirstTrackTypes == Game.Net.TrackTypes.None)
                    probe.FirstTrackTypes = trackTypes;

                if ((trackTypes & Game.Net.TrackTypes.Train) == 0)
                    continue;

                probe.TrainTrackSubLaneCount++;
                if (probe.FirstTrainTrackSubLane == Entity.Null)
                {
                    probe.FirstTrainTrackSubLane = laneEntity;
                    probe.FirstTrainTrackTypes = trackTypes;
                }
            }

            return probe;
        }

        private Entity ResolveNetEntity(Entity rawHitEntity, Entity raycastOwner, Entity directOwner)
        {
            Entity resolved = ResolveNetEntityFromOwnerChain(rawHitEntity);
            if (resolved != Entity.Null)
                return resolved;

            resolved = ResolveNetEntityFromOwnerChain(raycastOwner);
            if (resolved != Entity.Null)
                return resolved;

            resolved = ResolveNetEntityFromOwnerChain(directOwner);
            if (resolved != Entity.Null)
                return resolved;

            return Entity.Null;
        }

        private Entity ResolveNetEntityFromOwnerChain(Entity entity)
        {
            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (IsNetEntity(current))
                    return current;

                if (!World.EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = World.EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return Entity.Null;
        }

        private Entity ResolveTrackModelTargetFromOwnerChain(Entity entity)
        {
            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (IsTrackModelTargetEntity(current))
                    return current;

                if (!World.EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = World.EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return Entity.Null;
        }

        private Entity ResolveDirectOwner(Entity entity)
        {
            if (entity == Entity.Null || !World.EntityManager.HasComponent<Owner>(entity))
                return Entity.Null;

            return World.EntityManager.GetComponentData<Owner>(entity).m_Owner;
        }

        private bool IsNetEntity(Entity entity)
        {
            return entity != Entity.Null
                && (World.EntityManager.HasComponent<Game.Net.Edge>(entity)
                    || World.EntityManager.HasComponent<Game.Net.Node>(entity)
                    || World.EntityManager.HasBuffer<Game.Net.SubLane>(entity));
        }

        private bool IsTrackModelTargetEntity(Entity entity)
        {
            return entity != Entity.Null
                && (World.EntityManager.HasComponent<Game.Net.TrackLane>(entity)
                    || World.EntityManager.HasComponent<Game.Net.ConnectionLane>(entity)
                    || World.EntityManager.HasComponent<Game.Net.EdgeLane>(entity));
        }

        private string BuildTooltipText(RaycastResult result, DevSightProbe probe)
        {
            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            if (control != null
                && probe.NetEntity != Entity.Null
                && probe.NetHasSubLane
                && probe.TrainTrackSubLaneCount > 1)
            {
                return BuildNetBoundLaneText(control, probe);
            }

            Entity resolvedTarget = probe.FirstTrainTrackSubLane != Entity.Null
                ? probe.FirstTrainTrackSubLane
                : (probe.FirstTrackSubLane != Entity.Null
                    ? probe.FirstTrackSubLane
                    : (probe.FallbackTargetEntity != Entity.Null ? probe.FallbackTargetEntity : probe.NetEntity));

            if (resolvedTarget == Entity.Null)
            {
                return "state  no-target\n"
                    + "hit     " + DescribeEntity(probe.RawHitEntity) + "\n"
                    + "owner   " + DescribeEntity(probe.RaycastOwner) + "\n"
                    + "direct  " + DescribeEntity(probe.DirectOwner) + "\n"
                    + "chain   " + DescribeOwnerChain(probe.RawHitEntity);
            }

            if (control == null)
                return "target  " + FormatEntity(resolvedTarget) + "\ntrack model  unavailable";

            return control.m_TrackModel.BuildDevSightTooltipSummary(resolvedTarget);
        }

        private string BuildNetBoundLaneText(DispatchRuntimeSystem control, DevSightProbe probe)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("target  ").Append(FormatEntity(probe.NetEntity));
            sb.Append('\n').Append("net     ").Append(DescribeEntity(probe.NetEntity));

            DynamicBuffer<Game.Net.SubLane> subLanes = World.EntityManager.GetBuffer<Game.Net.SubLane>(probe.NetEntity, true);
            List<Entity> trainLanes = new List<Entity>();
            for (int i = 0; i < subLanes.Length; i++)
            {
                Game.Net.SubLane subLane = subLanes[i];
                if ((subLane.m_PathMethods & PathMethod.Track) == 0)
                    continue;

                Entity laneEntity = subLane.m_SubLane;
                if (laneEntity == Entity.Null
                    || !World.EntityManager.HasComponent<Game.Net.TrackLane>(laneEntity)
                    || !TryGetTrackLaneType(laneEntity, out Game.Net.TrackTypes trackTypes)
                    || (trackTypes & Game.Net.TrackTypes.Train) == 0)
                {
                    continue;
                }

                if (!trainLanes.Contains(laneEntity))
                    trainLanes.Add(laneEntity);
            }

            sb.Append('\n').Append("lanes   ").Append(trainLanes.Count);
            for (int i = 0; i < trainLanes.Count; i++)
            {
                Entity laneEntity = trainLanes[i];
                sb.Append('\n').Append("lane    ").Append(FormatEntity(laneEntity));
                sb.Append('\n').Append(control.m_TrackModel.BuildDevSightTooltipSummary(laneEntity));
            }

            return sb.ToString();
        }

        private string DescribeOwnerChain(Entity entity)
        {
            if (entity == Entity.Null)
                return "null";

            System.Text.StringBuilder sb = new System.Text.StringBuilder(128);
            Entity current = entity;
            for (int i = 0; i < 4 && current != Entity.Null; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");

                sb.Append(DescribeEntity(current));
                if (!World.EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = World.EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return sb.ToString();
        }

        private string DescribeEntity(Entity entity)
        {
            if (entity == Entity.Null)
                return "null";

            string kind = DescribeEntityKind(entity);
            return FormatEntity(entity) + ":" + kind;
        }

        private string DescribeEntityKind(Entity entity)
        {
            if (entity == Entity.Null)
                return "null";
            if (World.EntityManager.HasComponent<Game.Net.TrackLane>(entity))
                return "TrackLane";
            if (World.EntityManager.HasComponent<Game.Net.ConnectionLane>(entity))
                return "ConnectionLane";
            if (World.EntityManager.HasComponent<Game.Net.EdgeLane>(entity))
                return "EdgeLane";
            if (World.EntityManager.HasComponent<Game.Net.Edge>(entity))
                return "Edge";
            if (World.EntityManager.HasComponent<Game.Net.Node>(entity))
                return "Node";
            if (World.EntityManager.HasBuffer<Game.Net.SubLane>(entity))
                return "SubLaneOwner";
            if (World.EntityManager.HasComponent<Owner>(entity))
                return "Owner";
            return "Other";
        }

        private string FormatEntity(Entity entity)
        {
            if (entity == Entity.Null)
                return "null";

            return entity.Index + ":" + entity.Version;
        }

        private bool TryGetTrackLaneType(Entity laneEntity, out Game.Net.TrackTypes trackTypes)
        {
            trackTypes = Game.Net.TrackTypes.None;
            if (laneEntity == Entity.Null
                || !World.EntityManager.HasComponent<Game.Net.TrackLane>(laneEntity)
                || !World.EntityManager.HasComponent<PrefabRef>(laneEntity))
            {
                return false;
            }

            Entity prefab = World.EntityManager.GetComponentData<PrefabRef>(laneEntity).m_Prefab;
            if (prefab == Entity.Null || !World.EntityManager.HasComponent<TrackLaneData>(prefab))
                return false;

            trackTypes = World.EntityManager.GetComponentData<TrackLaneData>(prefab).m_TrackTypes;
            return true;
        }
    }
}
