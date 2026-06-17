import { useValue } from "cs2/api";
import { Panel, Scrollable } from "cs2/ui";
import React from "react";
import { useLocalPanelOpen, panelDataJson$, devSightJson$, devSightVisible$, setLocalPanelOpen, visible$ } from "./selectionBindings";
import { useT } from "./selectionI18n";
import { COLORS } from "./selectionStyles";
import { buildDetailRows, DevSightData, formatAlertText, PanelData } from "./selectionViewModel";
import { ActionButton, BypassToggleRow, DetailRow, DevSightBlock, PanelHeader, SectionCard } from "./components";

export function RapidTransitPanel() {
  const t = useT();
  const open = useLocalPanelOpen();
  const visible = useValue<boolean>(visible$) === true;
  const panelDataJson = useValue<string>(panelDataJson$) || "";
  const devSightVisible = useValue<boolean>(devSightVisible$) === true;
  const devSightJson = useValue<string>(devSightJson$) || "";

  let panelData: PanelData | null = null;
  if (panelDataJson) {
    try {
      panelData = JSON.parse(panelDataJson);
    } catch (error) {
      console.error("RapidTransit panel JSON parse failed", error);
      panelData = null;
    }
  }

  let devSightData: DevSightData | null = null;
  if (devSightVisible && devSightJson) {
    try {
      devSightData = JSON.parse(devSightJson);
    } catch (error) {
      console.error("RapidTransit Dev-Sight JSON parse failed", error);
      devSightData = null;
    }
  }

  if (!open) {
    return null;
  }

  const hasData = !!(visible && panelData && panelData.entityId);
  const hasDevSight = !!(devSightVisible && devSightData && (devSightData.summaryText || devSightData.source));
  const mode = hasData && panelData && panelData.mode === "line" ? "line" : "vehicle";
  const detailRows = hasData ? buildDetailRows(panelData, mode) : [];
  const titleKey = mode === "line" ? "lineTitle" : "vehicleTitle";
  const actionButtons: Array<{ action: string; label: string }> = [];

  if (hasData && panelData) {
    if (panelData.showRetireAction) {
      actionButtons.push({ label: t("retire"), action: "requestVehicleRetire" });
    }
    if (panelData.showForceDepartAction) {
      actionButtons.push({ label: t("departNow"), action: "requestVehicleForceDepart" });
    }
    if (panelData.showLineSpawnAction) {
      actionButtons.push({ label: t("spawnOne"), action: "requestLineSpawn" });
    }
    if (panelData.showDumpTrackModelAction) {
      actionButtons.push({ label: t("dumpTrackModel"), action: "requestDumpTrackModel" });
    }
    if (panelData.showDumpPlannerInputAction) {
      actionButtons.push({ label: t("dumpPlannerInput"), action: "requestDumpPlannerInput" });
    }
    if (panelData.showDumpObservationAction) {
      actionButtons.push({ label: t("dumpObservation"), action: "requestDumpObservation" });
    }
    if (panelData.showDumpStationAnchorObservationAction) {
      actionButtons.push({ label: t("dumpStationAnchorObservation"), action: "requestDumpStationAnchorObservation" });
    }
  }

  const actionRows: Array<Array<{ action: string; label: string }>> = [];
  for (let i = 0; i < actionButtons.length; i += 2) {
    actionRows.push(actionButtons.slice(i, i + 2));
  }

  return (
    <div
      style={{
        position: "absolute",
        top: "59rem",
        right: "18rem",
        width: "315rem",
        maxWidth: "calc(100vw - 36rem)",
        pointerEvents: "auto",
        zIndex: 1000
      }}
    >
      <Panel
        header={<PanelHeader title={hasData ? t(titleKey) : t("panelTitle")} />}
        onClose={() => setLocalPanelOpen(false)}
        style={{
          width: "315rem",
          paddingTop: "6rem",
          maxWidth: "calc(100vw - 36rem)"
        }}
      >
        <Scrollable>
          <div
            style={{
              display: "flex",
              flexDirection: "column",
              padding: "0 12rem 12rem"
            }}
          >
            {hasDevSight ? (
              <DevSightBlock
                first={true}
                source={devSightData ? devSightData.source : undefined}
                summaryText={devSightData ? devSightData.summaryText : undefined}
              />
            ) : null}
            {hasData && panelData ? (
              <>
                <SectionCard first={!hasDevSight}>
                  <DetailRow
                    label={panelData.primaryLabelKey}
                    value={panelData.primaryValue}
                    valueKind={panelData.primaryValueKind}
                    t={t}
                    strong={true}
                  />
                </SectionCard>
                <SectionCard dense={true}>
                  {detailRows.map((row, index) => (
                    <DetailRow
                      key={(row.label || "detail") + ":" + index}
                      label={row.label}
                      value={row.value}
                      dense={true}
                      t={t}
                    />
                  ))}
                </SectionCard>
                {panelData.showAlerts ? (
                  <SectionCard alert={true}>
                    <div
                      style={{
                        fontSize: "15rem",
                        lineHeight: "22rem",
                        fontWeight: 500,
                        color: COLORS.title
                      }}
                    >
                      {formatAlertText(panelData.alertText, t)}
                    </div>
                  </SectionCard>
                ) : null}
                {panelData.showBypassStationToggle ? (
                  <SectionCard compact={true}>
                    <BypassToggleRow
                      label={t("bypassStation")}
                      checked={panelData.bypassStationChecked}
                    />
                  </SectionCard>
                ) : null}
                {panelData.showActions ? (
                  <SectionCard compact={true}>
                    <div
                      style={{
                        display: "flex",
                        flexDirection: "column",
                        justifyContent: "flex-start",
                        alignItems: "stretch",
                        paddingTop: "5rem"
                      }}
                    >
                      {actionRows.map((row, rowIndex) => (
                        <div
                          key={"action-row:" + rowIndex}
                          style={{
                            display: "flex",
                            flexDirection: "row",
                            justifyContent: "flex-start",
                            alignItems: "center",
                            marginTop: rowIndex > 0 ? "9rem" : "0"
                          }}
                        >
                          {row.map((button, buttonIndex) => (
                            <ActionButton
                              key={button.action}
                              label={button.label}
                              action={button.action}
                              marginLeft={buttonIndex > 0 ? "18rem" : "0"}
                            />
                          ))}
                        </div>
                      ))}
                    </div>
                  </SectionCard>
                ) : null}
              </>
            ) : !hasDevSight ? (
              <SectionCard first={true}>
                <div
                  style={{
                    fontSize: "15rem",
                    lineHeight: "23rem",
                    color: COLORS.text
                  }}
                >
                  {t("emptyHint")}
                </div>
              </SectionCard>
            ) : null}
          </div>
        </Scrollable>
      </Panel>
    </div>
  );
}
