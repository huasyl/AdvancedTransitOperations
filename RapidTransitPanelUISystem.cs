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
    public class RapidTransitPanelUISystem : UISystemBase
    {
        private const string kGroup = "RapidTransitPanel";
        private const int SelectionSettleFrames = 2;

        private SelectedInfoUISystem m_SelectedInfoUISystem = null!;
        private ValueBinding<bool> m_VisibleBinding = null!;
        private ValueBinding<string> m_PanelDataJsonBinding = null!;

        private bool m_PanelOpen;
        private bool m_LastVisible;
        private Entity m_PendingVehicle;
        private int m_PendingSelectionFrame = -1;
        private Entity m_LastVehicle;
        private ulong m_LastSnapshotVersion;
        private int m_LastPushFrame = -1;
        private DepartureControlSystem.SelectedPanelSnapshot m_LastSnapshot;

        public override GameMode gameMode => GameMode.Game;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SelectedInfoUISystem = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_PendingVehicle = Entity.Null;
            m_LastVehicle = Entity.Null;

            AddBinding(m_VisibleBinding = new ValueBinding<bool>(kGroup, "visible", initialValue: false));
            AddBinding(m_PanelDataJsonBinding = new ValueBinding<string>(kGroup, "panelDataJson", string.Empty));
            AddBinding(new TriggerBinding<bool>(kGroup, "setPanelOpen", SetPanelOpen));
            AddBinding(new TriggerBinding(kGroup, "requestVehicleRetire", RequestVehicleRetire));
            AddBinding(new TriggerBinding(kGroup, "requestVehicleReevaluate", RequestVehicleReevaluate));
            AddBinding(new TriggerBinding(kGroup, "requestDumpTrackModel", RequestDumpTrackModel));
            AddBinding(new TriggerBinding<bool>(kGroup, "setBypassStation", SetBypassStation));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (!m_PanelOpen)
            {
                ResetState(clearSnapshot: true);
                SetHidden();
                return;
            }

            DepartureControlSystem control = DepartureControlSystem.Instance;
            if (control == null)
            {
                ResetState(clearSnapshot: true);
                SetHidden();
                return;
            }

            int currentFrame = UnityEngine.Time.frameCount;
            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            bool isInspectableVehicle = selectedEntity != Entity.Null && control.ShouldDisplaySelectedVehicleInfo(selectedEntity);
            bool isInspectableLine = selectedEntity != Entity.Null && control.ShouldDisplaySelectedLineInfo(selectedEntity);

            if (!isInspectableVehicle && !isInspectableLine)
            {
                TrackPendingVehicle(selectedEntity, currentFrame);
                if (m_LastVisible && HasSelectionSettled(currentFrame))
                {
                    ResetState(clearSnapshot: true);
                    SetHidden();
                }
                return;
            }

            if (selectedEntity != m_PendingVehicle)
            {
                TrackPendingVehicle(selectedEntity, currentFrame);
                return;
            }

            if (!HasSelectionSettled(currentFrame))
                return;

            bool needsSelectionPush = !m_LastVisible || selectedEntity != m_LastVehicle;
            if (!needsSelectionPush && control.PanelDataVersion != m_LastSnapshotVersion)
            {
                needsSelectionPush = true;
            }
            if (needsSelectionPush)
            {
                TryPushSnapshot(control, selectedEntity, isInspectableVehicle, "refresh", currentFrame);
                return;
            }

            SetVisibleIfNeeded();
        }

        private bool TryPushSnapshot(DepartureControlSystem control, Entity entity, bool isVehicle, string dirtyReason, int currentFrame)
        {
            bool built = isVehicle
                ? control.TryBuildSelectedVehicleSnapshot(entity, out m_LastSnapshot)
                : control.TryBuildSelectedLineSnapshot(entity, out m_LastSnapshot);

            if (!built)
            {
                ResetState(clearSnapshot: true);
                SetHidden();
                return false;
            }

            m_PanelDataJsonBinding.Update(SerializeSnapshot(m_LastSnapshot));
            m_LastVehicle = entity;
            m_LastSnapshotVersion = control.PanelDataVersion;
            m_LastPushFrame = currentFrame;
            SetVisibleIfNeeded();
            return true;
        }

        private void TrackPendingVehicle(Entity vehicle, int currentFrame)
        {
            if (vehicle == m_PendingVehicle)
                return;

            m_PendingVehicle = vehicle;
            m_PendingSelectionFrame = currentFrame;
        }

        private bool HasSelectionSettled(int currentFrame)
        {
            return m_PendingSelectionFrame >= 0 && (currentFrame - m_PendingSelectionFrame) >= SelectionSettleFrames;
        }

        private static string SerializeSnapshot(DepartureControlSystem.SelectedPanelSnapshot snapshot)
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
            AppendJsonString(sb, "alertText", snapshot.AlertText);
            AppendJsonBool(sb, "showAlerts", snapshot.AlertText.Length > 0 && snapshot.AlertText != "None");
            AppendJsonBool(sb, "showRetireAction", snapshot.ShowRetireAction);
            AppendJsonBool(sb, "showReevaluateAction", snapshot.ShowReevaluateAction);
            AppendJsonBool(sb, "showDumpTrackModelAction", snapshot.ShowDumpTrackModelAction);
            AppendJsonBool(sb, "showActions", snapshot.ShowRetireAction || snapshot.ShowReevaluateAction || snapshot.ShowDumpTrackModelAction);
            AppendJsonBool(sb, "showBypassStationToggle", snapshot.ShowBypassStationToggle);
            AppendJsonBool(sb, "bypassStationChecked", snapshot.BypassStationChecked);
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

        private void ResetState(bool clearSnapshot)
        {
            m_PendingVehicle = Entity.Null;
            m_PendingSelectionFrame = -1;
            m_LastVehicle = Entity.Null;
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
            if (DepartureControlSystem.Instance == null)
                return;

            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity != Entity.Null)
            {
                DepartureControlSystem.Instance.RequestVehicleRetire(selectedEntity);
                m_LastVehicle = Entity.Null;
            }
        }

        private void RequestVehicleReevaluate()
        {
            if (DepartureControlSystem.Instance == null)
                return;

            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity != Entity.Null)
            {
                DepartureControlSystem.Instance.RequestVehicleReevaluate(selectedEntity);
                m_LastVehicle = Entity.Null;
            }
        }

        private void RequestDumpTrackModel()
        {
            if (DepartureControlSystem.Instance == null)
                return;

            DepartureControlSystem.Instance.RequestDumpTrackModelSnapshot();
            m_LastVehicle = Entity.Null;
            m_LastSnapshotVersion = 0;
        }

        private void SetBypassStation(bool enabled)
        {
            if (DepartureControlSystem.Instance == null)
                return;

            Entity selectedEntity = m_SelectedInfoUISystem.selectedEntity;
            if (selectedEntity != Entity.Null && DepartureControlSystem.Instance.RequestSetBypassStation(selectedEntity, enabled))
            {
                m_LastVehicle = Entity.Null;
                m_LastSnapshotVersion = 0;
            }
        }
    }
}
