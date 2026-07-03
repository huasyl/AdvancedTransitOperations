using System.Text;
using Colossal.UI.Binding;
using Game;
using Game.SceneFlow;
using Game.UI;
using Game.UI.InGame;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting;

namespace RapidTransitMod
{
    public partial class RapidTransitPanelUISystem : UISystemBase
    {
        private const string kGroup = "RapidTransitPanel";
        private const int SelectionSettleFrames = 2;

        private SelectedInfoUISystem m_SelectedInfoUISystem = null!;
        private ValueBinding<bool> m_VisibleBinding = null!;
        private ValueBinding<string> m_PanelDataJsonBinding = null!;
        private ValueBinding<bool> m_DevSightVisibleBinding = null!;
        private ValueBinding<string> m_DevSightJsonBinding = null!;

        private bool m_PanelOpen;
        private bool m_LastVisible;
        private Entity m_PendingVehicle;
        private Entity m_PendingRoute;
        private int m_PendingSelectionFrame = -1;
        private Entity m_LastVehicle;
        private Entity m_LastRoute;
        private ulong m_LastSnapshotVersion;
        private int m_LastPushFrame = -1;
        private SelectPanel.Snapshot m_LastSnapshot;

        public override GameMode gameMode => GameMode.Game;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SelectedInfoUISystem = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_PendingVehicle = Entity.Null;
            m_PendingRoute = Entity.Null;
            m_LastVehicle = Entity.Null;
            m_LastRoute = Entity.Null;

            AddBinding(m_VisibleBinding = new ValueBinding<bool>(kGroup, "visible", initialValue: false));
            AddBinding(m_PanelDataJsonBinding = new ValueBinding<string>(kGroup, "panelDataJson", string.Empty));
            AddBinding(m_DevSightVisibleBinding = new ValueBinding<bool>(kGroup, "devSightVisible", initialValue: false));
            AddBinding(m_DevSightJsonBinding = new ValueBinding<string>(kGroup, "devSightJson", string.Empty));
            AddBinding(new TriggerBinding<bool>(kGroup, "setPanelOpen", SetPanelOpen));
            AddBinding(new TriggerBinding(kGroup, "requestVehicleRetire", RequestVehicleRetire));
            AddBinding(new TriggerBinding(kGroup, "requestVehicleForceDepart", RequestVehicleForceDepart));
#if RT_DEBUG_TOOLS
            AddBinding(new TriggerBinding(kGroup, "requestVehicleReevaluate", RequestVehicleReevaluate));
            AddBinding(new TriggerBinding(kGroup, "requestLineSpawn", RequestLineSpawn));
            AddBinding(new TriggerBinding(kGroup, "requestDumpTrackModel", RequestDumpTrackModel));
            AddBinding(new TriggerBinding(kGroup, "requestDumpPlannerInput", RequestDumpPlannerInput));
            AddBinding(new TriggerBinding(kGroup, "requestDumpObservation", RequestDumpObservation));
            AddBinding(new TriggerBinding(kGroup, "requestDumpStationAnchorObservation", RequestDumpStationAnchorObservation));
            AddBinding(new TriggerBinding(kGroup, "requestWorkbenchApiRebind", RequestWorkbenchApiRebind));
#endif
            AddBinding(new TriggerBinding<bool>(kGroup, "setBypassStation", SetBypassStation));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();

#if RT_DEBUG_TOOLS
            try
            {
                UpdateDevSightBindings();
            }
            catch (System.Exception ex)
            {
                ClearDevSightBindings();
                Mod.log.Info("[DevSightPanel] update failed: " + ex.GetType().Name + ": " + ex.Message);
            }
#endif

            if (!m_PanelOpen)
            {
                ResetState(clearSnapshot: true);
                SetHidden();
                return;
            }

            DispatchRuntimeSystem control = DispatchRuntimeSystem.Instance;
            if (control == null)
            {
                ResetState(clearSnapshot: true);
                SetHidden();
                return;
            }

            int currentFrame = UnityEngine.Time.frameCount;
            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            Entity selectedRoute = m_SelectedInfoUISystem.selectedRoute;
            SelectPanel panel = control.m_SelectPanel;
            bool isInspectableVehicle = selectedEntity != Entity.Null && panel.CanShowVehicle(selectedEntity);
            bool isInspectableLine = selectedEntity != Entity.Null && panel.CanShowLine(selectedEntity, selectedRoute);

            if (!isInspectableVehicle && !isInspectableLine)
            {
                TrackPendingSelection(selectedEntity, selectedRoute, currentFrame);
                if (HasSelectionSettled(currentFrame))
                {
                    ResetState(clearSnapshot: true);
                    SetHidden();
                }
                return;
            }

            if (selectedEntity != m_PendingVehicle || selectedRoute != m_PendingRoute)
            {
                TrackPendingSelection(selectedEntity, selectedRoute, currentFrame);
                return;
            }

            if (!HasSelectionSettled(currentFrame))
                return;

            bool needsSelectionPush = !m_LastVisible || selectedEntity != m_LastVehicle || selectedRoute != m_LastRoute;
            if (!needsSelectionPush && panel.PanelDataVersion != m_LastSnapshotVersion)
            {
                needsSelectionPush = true;
            }
            if (needsSelectionPush)
            {
                TryPushSnapshot(panel, selectedEntity, selectedRoute, isInspectableVehicle, "refresh", currentFrame);
                return;
            }

            SetVisibleIfNeeded();
        }

        private bool TryPushSnapshot(SelectPanel panel, Entity entity, Entity selectedRoute, bool isVehicle, string dirtyReason, int currentFrame)
        {
            bool built = isVehicle
                ? panel.TryVehicle(entity, out m_LastSnapshot)
                : panel.TryLine(entity, selectedRoute, out m_LastSnapshot);

            if (!built)
            {
                ResetState(clearSnapshot: true);
                SetHidden();
                return false;
            }

            m_PanelDataJsonBinding.Update(SerializeSnapshot(m_LastSnapshot));
            m_LastVehicle = entity;
            m_LastRoute = selectedRoute;
            m_LastSnapshotVersion = panel.PanelDataVersion;
            m_LastPushFrame = currentFrame;
            SetVisibleIfNeeded();
            return true;
        }

        private void TrackPendingSelection(Entity vehicle, Entity selectedRoute, int currentFrame)
        {
            if (vehicle == m_PendingVehicle && selectedRoute == m_PendingRoute)
                return;

            m_PendingVehicle = vehicle;
            m_PendingRoute = selectedRoute;
            m_PendingSelectionFrame = currentFrame;
        }

        private bool HasSelectionSettled(int currentFrame)
        {
            return m_PendingSelectionFrame >= 0 && (currentFrame - m_PendingSelectionFrame) >= SelectionSettleFrames;
        }

        private static string SerializeSnapshot(SelectPanel.Snapshot snapshot)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append('{');
            AppendJsonString(sb, "mode", snapshot.Mode);
            AppendJsonString(sb, "entityId", snapshot.EntityId);
            AppendJsonString(sb, "primaryLabelKey", snapshot.PrimaryLabelKey);
            AppendJsonString(sb, "primaryValue", snapshot.PrimaryValue);
            AppendJsonString(sb, "primaryValueKind", snapshot.PrimaryValueKind);
            AppendJsonString(sb, "detail1LabelKey", snapshot.Detail1LabelKey);
            AppendJsonString(sb, "detail1Value", snapshot.Detail1Value);
            AppendJsonString(sb, "detail2LabelKey", snapshot.Detail2LabelKey);
            AppendJsonString(sb, "detail2Value", snapshot.Detail2Value);
            AppendJsonString(sb, "detail3LabelKey", snapshot.Detail3LabelKey);
            AppendJsonString(sb, "detail3Value", snapshot.Detail3Value);
            AppendJsonString(sb, "detail4LabelKey", snapshot.Detail4LabelKey);
            AppendJsonString(sb, "detail4Value", snapshot.Detail4Value);
            AppendJsonString(sb, "detail5LabelKey", snapshot.Detail5LabelKey);
            AppendJsonString(sb, "detail5Value", snapshot.Detail5Value);
            AppendJsonString(sb, "detail6LabelKey", snapshot.Detail6LabelKey);
            AppendJsonString(sb, "detail6Value", snapshot.Detail6Value);
            AppendJsonString(sb, "detail7LabelKey", snapshot.Detail7LabelKey);
            AppendJsonString(sb, "detail7Value", snapshot.Detail7Value);
            AppendJsonString(sb, "detail8LabelKey", snapshot.Detail8LabelKey);
            AppendJsonString(sb, "detail8Value", snapshot.Detail8Value);
            AppendJsonString(sb, "alertText", snapshot.AlertText);
            AppendJsonBool(sb, "showAlerts", snapshot.AlertText.Length > 0 && snapshot.AlertText != "None");
            AppendJsonBool(sb, "showRetireAction", snapshot.ShowRetireAction);
            AppendJsonBool(sb, "showForceDepartAction", snapshot.ShowForceDepartAction);
            AppendJsonBool(sb, "showReevaluateAction", snapshot.ShowReevaluateAction);
            AppendJsonBool(sb, "showLineSpawnAction", snapshot.ShowLineSpawnAction);
            AppendJsonBool(sb, "showDumpTrackModelAction", snapshot.ShowDumpTrackModelAction);
            AppendJsonBool(sb, "showDumpPlannerInputAction", snapshot.ShowDumpPlannerInputAction);
            AppendJsonBool(sb, "showDumpObservationAction", snapshot.ShowDumpObservationAction);
            AppendJsonBool(sb, "showDumpStationAnchorObservationAction", snapshot.ShowDumpStationAnchorObservationAction);
            AppendJsonBool(sb, "showActions", snapshot.ShowRetireAction || snapshot.ShowForceDepartAction || snapshot.ShowReevaluateAction || snapshot.ShowLineSpawnAction || snapshot.ShowDumpTrackModelAction || snapshot.ShowDumpPlannerInputAction || snapshot.ShowDumpObservationAction || snapshot.ShowDumpStationAnchorObservationAction);
            AppendJsonBool(sb, "showBypassStationToggle", snapshot.ShowBypassStationToggle);
            AppendJsonBool(sb, "bypassStationChecked", snapshot.BypassStationChecked);
            if (sb[sb.Length - 1] == ',')
                sb.Length--;
            sb.Append('}');
            return sb.ToString();
        }

        private static string SerializeDevSight(string source, string summaryText)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append('{');
            AppendJsonString(sb, "source", source ?? string.Empty);
            AppendJsonString(sb, "summaryText", summaryText ?? string.Empty);
            if (sb[sb.Length - 1] == ',')
                sb.Length--;
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append("\":\"");
            AppendEscapedJson(sb, value ?? string.Empty);
            sb.Append("\",");
        }

        private static void AppendJsonBool(StringBuilder sb, string name, bool value)
        {
            sb.Append('"').Append(name).Append("\":").Append(value ? "true" : "false").Append(',');
        }

        private static void AppendEscapedJson(StringBuilder sb, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < ' ')
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }
        }

        private void ClearPanelData()
        {
            if (m_PanelDataJsonBinding.value.Length == 0)
                return;

            m_PanelDataJsonBinding.Update(string.Empty);
        }

        private void UpdateDevSightBindings()
        {
            bool visible = DevSightTooltipSystem.TryGetPanelState(out string source, out string summaryText);
            if (!visible)
            {
                ClearDevSightBindings();
                return;
            }

            string payload = SerializeDevSight(source, summaryText);
            if (!m_DevSightVisibleBinding.value)
                m_DevSightVisibleBinding.Update(true);

            if (m_DevSightJsonBinding.value != payload)
                m_DevSightJsonBinding.Update(payload);
        }

        private void ClearDevSightBindings()
        {
            if (m_DevSightVisibleBinding.value)
                m_DevSightVisibleBinding.Update(false);

            if (m_DevSightJsonBinding.value.Length > 0)
                m_DevSightJsonBinding.Update(string.Empty);
        }

        private void ResetState(bool clearSnapshot)
        {
            m_PendingVehicle = Entity.Null;
            m_PendingRoute = Entity.Null;
            m_PendingSelectionFrame = -1;
            m_LastVehicle = Entity.Null;
            m_LastRoute = Entity.Null;
            m_LastSnapshotVersion = 0;
            m_LastPushFrame = -1;

            if (clearSnapshot)
                ClearPanelData();
        }

        private void SetVisibleIfNeeded()
        {
            if (m_LastVisible)
                return;

            m_VisibleBinding.Update(true);
            m_LastVisible = true;
        }

        private void SetHidden()
        {
            if (!m_LastVisible)
                return;

            m_VisibleBinding.Update(false);
            m_LastVisible = false;
        }

        private void SetPanelOpen(bool open)
        {
            m_PanelOpen = open;
            if (!open)
            {
                ResetState(clearSnapshot: true);
                SetHidden();
            }
        }

        private void RequestVehicleRetire()
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity != Entity.Null)
            {
                DispatchRuntimeSystem.Instance.m_SelectPanel.Retire(selectedEntity);
                m_LastVehicle = Entity.Null;
            }
        }

        private void RequestVehicleForceDepart()
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity != Entity.Null)
            {
                DispatchRuntimeSystem.Instance.m_SelectPanel.Depart(selectedEntity);
                m_LastVehicle = Entity.Null;
                m_LastSnapshotVersion = 0;
            }
        }

#if RT_DEBUG_TOOLS
        private void RequestVehicleReevaluate()
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity != Entity.Null)
            {
                DispatchRuntimeSystem.Instance.m_SelectPanel.Recheck(selectedEntity);
                m_LastVehicle = Entity.Null;
            }
        }

        private void RequestDumpTrackModel()
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            DispatchRuntimeSystem.Instance.m_TrackModel.DumpTrackModelSnapshot();
            m_LastVehicle = Entity.Null;
            m_LastSnapshotVersion = 0;
        }

        private void RequestDumpPlannerInput()
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            DispatchRuntimeSystem.Instance.m_PlannerApi.Dump();
            m_LastVehicle = Entity.Null;
            m_LastSnapshotVersion = 0;
        }

        private void RequestDumpObservation()
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            DispatchRuntimeSystem.Instance.m_Observation.Dump();
            m_LastVehicle = Entity.Null;
            m_LastSnapshotVersion = 0;
        }

        private void RequestDumpStationAnchorObservation()
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            DispatchRuntimeSystem.Instance.m_StationAnchorDiagnostics.Dump();
            m_LastVehicle = Entity.Null;
            m_LastSnapshotVersion = 0;
        }

        private void RequestLineSpawn()
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            Entity selectedEntity = m_SelectedInfoUISystem.selectedRoute != Entity.Null
                ? m_SelectedInfoUISystem.selectedRoute
                : m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity != Entity.Null && DispatchRuntimeSystem.Instance.m_SelectPanel.Spawn(selectedEntity))
            {
                m_LastVehicle = Entity.Null;
                m_LastRoute = Entity.Null;
                m_LastSnapshotVersion = 0;
            }
        }

        private void RequestWorkbenchApiRebind()
        {
            Workbenches.ApiHost.RebindNow();
        }
#endif

        private void SetBypassStation(bool enabled)
        {
            if (DispatchRuntimeSystem.Instance == null)
                return;

            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity != Entity.Null && DispatchRuntimeSystem.Instance.m_SelectPanel.SetBypass(selectedEntity, enabled))
            {
                m_LastVehicle = Entity.Null;
                m_LastSnapshotVersion = 0;
            }
        }
    }
}
