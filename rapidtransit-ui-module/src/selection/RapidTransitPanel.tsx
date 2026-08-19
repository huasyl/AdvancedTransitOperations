import { useValue } from "cs2/api";
import { Panel, Scrollable } from "cs2/ui";
import React from "react";
import { activeLocale$, useLocalPanelOpen, panelDataJson$, devSightJson$, devSightVisible$, etaHotAvailable$, etaHotStatusJson$, etaSnapshotStatusJson$, setLocalPanelOpen, visible$ } from "./selectionBindings";
import { useT } from "./selectionI18n";
import { COLORS } from "./selectionStyles";
import { buildDetailRows, DevSightData, EtaHotStatusData, EtaSnapshotStatusData, formatAlertText, PanelData } from "./selectionViewModel";
import { ActionButton, ArrivalTimesRow, BypassToggleRow, DetailRow, DevSightBlock, LatinScheduleRows, PanelHeader, ScheduledTimeRow, SectionCard, VehicleInfoRow } from "./components";

export function RapidTransitPanel() {
  const t = useT();
  const open = useLocalPanelOpen();
  const activeLocale = useValue<string>(activeLocale$) || "";
  const isCjkLocale = /^(zh|ja)([-_]|$)/i.test(activeLocale);
  const visible = useValue<boolean>(visible$) === true;
  const panelDataJson = useValue<string>(panelDataJson$) || "";
  const devSightVisible = useValue<boolean>(devSightVisible$) === true;
  const devSightJson = useValue<string>(devSightJson$) || "";
  const etaHotAvailable = useValue<boolean>(etaHotAvailable$) === true;
  const etaHotStatusJson = useValue<string>(etaHotStatusJson$) || "";
  const etaSnapshotStatusJson = useValue<string>(etaSnapshotStatusJson$) || "";

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

  let etaHotStatus: EtaHotStatusData | null = null;
  let etaSnapshotStatus: EtaSnapshotStatusData | null = null;
  if (etaHotAvailable && etaHotStatusJson) {
    try {
      etaHotStatus = JSON.parse(etaHotStatusJson);
    } catch (error) {
      console.error("RapidTransit ETA hot status JSON parse failed", error);
    }
  }
  if (etaHotAvailable && etaSnapshotStatusJson) {
    try {
      etaSnapshotStatus = JSON.parse(etaSnapshotStatusJson);
      if (etaSnapshotStatus && etaSnapshotStatus.comparisonSummary) {
        const comparisonStatus = JSON.parse(etaSnapshotStatus.comparisonSummary);
        const selectedVehicleMatches = panelData?.mode === "vehicle"
          && String(panelData.entityId) === String(comparisonStatus.comparisonVehicleIndex);
        etaSnapshotStatus = selectedVehicleMatches ? { ...etaSnapshotStatus, ...comparisonStatus } : null;
      }
    } catch (error) {
      console.error("RapidTransit ETA snapshot status JSON parse failed", error);
    }
  }

  if (!open) {
    return null;
  }

  const hasData = !!(visible && panelData && panelData.entityId);
  const hasDevSight = !!(devSightVisible && devSightData && (devSightData.summaryText || devSightData.source));
  const etaHotDisabled = !!(etaHotStatus && (etaHotStatus.busy || etaHotStatus.hotBackendWorkerLost || etaHotStatus.etaWorkerLost));
  const etaRollbackDisabled = !!etaHotStatus?.busy;
  const mode = hasData && panelData && panelData.mode === "line" ? "line" : "vehicle";
  const canRequestEta = (hasData && mode === "vehicle") || !!etaSnapshotStatus?.comparisonVehicleId;
  const detailRows = hasData ? buildDetailRows(panelData, mode) : [];
  const isVehicle = mode === "vehicle";
  const showAhead = isVehicle && !!(
    panelData?.nextPassStationName
    || panelData?.nextStopStationName
    || (typeof panelData?.nextPlannedArrivalMinute === "number" && panelData.nextPlannedArrivalMinute >= 0)
  );
  const hasPlannedArrival = isVehicle
    && typeof panelData?.plannedArrivalMinute === "number"
    && panelData.plannedArrivalMinute >= 0;
  const hasActualArrival = isVehicle
    && typeof panelData?.actualArrivalMinute === "number"
    && panelData.actualArrivalMinute >= 0;
  const hasPlannedDeparture = isVehicle
    && typeof panelData?.plannedDepartureMinute === "number"
    && panelData.plannedDepartureMinute >= 0;
  const showArrivalPair = hasPlannedArrival && hasActualArrival;
  const showLatinSchedule = !isCjkLocale && (hasPlannedArrival || hasPlannedDeparture);
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
                {isVehicle ? (
                  <>
                    {!panelData.isManagedVehicle ? null : (
                      <>
                        {panelData.showCurrentStop || showAhead || panelData.showSchedule ? (
                          <SectionCard dense={true}>
                            <div style={{ display: "flex", flexDirection: "column" }}>
                            {panelData.showCurrentStop ? (
                              <div style={{ display: "flex", flexDirection: "column" }}>
                                <VehicleInfoRow label="currentStation" value={panelData.currentStationName} t={t} />
                                {isCjkLocale || !showLatinSchedule ? (
                                  <>
                                    {showArrivalPair ? (
                                      <div style={{ marginTop: "8rem" }}>
                                        <ArrivalTimesRow
                                          plannedArrivalMinute={panelData.plannedArrivalMinute}
                                          actualArrivalMinute={panelData.actualArrivalMinute}
                                          t={t}
                                        />
                                      </div>
                                    ) : null}
                                    {!showArrivalPair && hasPlannedArrival ? (
                                      <div style={{ marginTop: "8rem" }}>
                                        <ScheduledTimeRow label="arrival" minute={panelData.plannedArrivalMinute} t={t} />
                                      </div>
                                    ) : null}
                                    {!showArrivalPair && hasActualArrival ? (
                                      <div style={{ marginTop: "8rem" }}>
                                        <VehicleInfoRow label="actualArrival" value={panelData.actualArrivalMinute} valueKind="serviceMinute" level="secondary" t={t} />
                                      </div>
                                    ) : null}
                                    {hasPlannedDeparture ? (
                                      <div style={{ marginTop: "8rem" }}>
                                        <ScheduledTimeRow label="departure" minute={panelData.plannedDepartureMinute} t={t} />
                                      </div>
                                    ) : null}
                                    <div style={{ marginTop: "8rem" }}>
                                      <VehicleInfoRow label="stopped" value={panelData.stopDwellValue} level="secondary" t={t} />
                                    </div>
                                  </>
                                ) : (
                                  <div style={{ marginTop: "8rem" }}>
                                    <LatinScheduleRows
                                      plannedArrivalMinute={panelData.plannedArrivalMinute}
                                      actualArrivalMinute={panelData.actualArrivalMinute}
                                      plannedDepartureMinute={panelData.plannedDepartureMinute}
                                      stopDwellValue={panelData.stopDwellValue}
                                      t={t}
                                    />
                                  </div>
                                )}
                              </div>
                            ) : null}
                            {showAhead ? (
                              <div style={{ display: "flex", flexDirection: "column", marginTop: panelData.showCurrentStop ? "16rem" : "0" }}>
                                {panelData.nextPassStationName ? (
                                  <VehicleInfoRow label="nextPass" value={panelData.nextPassStationName} level="muted" t={t} />
                                ) : null}
                                {panelData.nextStopStationName ? (
                                  <div style={{ marginTop: panelData.nextPassStationName ? "8rem" : "0" }}>
                                    <VehicleInfoRow label="nextStopStation" value={panelData.nextStopStationName} t={t} />
                                  </div>
                                ) : null}
                                {typeof panelData.nextPlannedArrivalMinute === "number" && panelData.nextPlannedArrivalMinute >= 0 ? (
                                  <div style={{ marginTop: "8rem" }}>
                                    <ScheduledTimeRow label="arrival" minute={panelData.nextPlannedArrivalMinute} t={t} />
                                  </div>
                                ) : null}
                              </div>
                            ) : null}
                            {panelData.showSchedule ? (
                              <div style={{ display: "flex", flexDirection: "column", marginTop: panelData.showCurrentStop || showAhead ? "16rem" : "0" }}>
                                {panelData.currentSlotText ? <VehicleInfoRow label="currentSlot" value={panelData.currentSlotText} t={t} /> : null}
                                {panelData.targetSlotText ? (
                                  <div style={{ marginTop: panelData.currentSlotText ? "8rem" : "0" }}>
                                    <VehicleInfoRow label="targetSlot" value={panelData.targetSlotText} t={t} />
                                  </div>
                                ) : null}
                              </div>
                            ) : null}
                            </div>
                          </SectionCard>
                        ) : null}
                        {panelData.showWaitingForFastTrain ? (
                          <SectionCard compact={true}>
                            <div style={{ fontSize: "15rem", lineHeight: "22rem", fontWeight: 500, color: COLORS.title }}>
                              {t("waitingForFastTrain")}
                              {typeof panelData.waitingForFastTrainVehicleId === "number" && panelData.waitingForFastTrainVehicleId >= 0
                                ? `：#${panelData.waitingForFastTrainVehicleId}`
                                : ""}
                            </div>
                          </SectionCard>
                        ) : null}
                      </>
                    )}
                  </>
                ) : (
                  <SectionCard dense={true}>
                    {detailRows.map((row, index) => (
                      <DetailRow
                        key={(row.label || "detail") + ":" + index}
                        label={row.label}
                        value={row.value}
                        valueKind={row.valueKind}
                        dense={true}
                        t={t}
                      />
                    ))}
                  </SectionCard>
                )}
                {!isVehicle && panelData.showAlerts ? (
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
                        paddingBottom: "8rem"
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
            {etaHotAvailable ? (
              <SectionCard compact={true}>
                <div style={{ fontSize: "15rem", lineHeight: "22rem", fontWeight: 600, color: COLORS.titleAccent }}>
                  {t("etaHotTitle")}
                </div>
                <div style={{ fontSize: "13rem", lineHeight: "18rem", color: COLORS.text }}>
                  {`${t("etaHotBuild")}: ${etaHotStatus?.currentSource || "built-in"}/${etaHotStatus?.currentBuildId || "-"}`}
                </div>
                <div style={{ fontSize: "13rem", lineHeight: "18rem", color: COLORS.text }}>
                  {`${t("etaHotGeneration")}: ${etaHotStatus?.generation || 0}`}
                </div>
                <div style={{ fontSize: "13rem", lineHeight: "18rem", color: etaHotStatus?.workerLost ? "#ffd6d1" : COLORS.text }}>
                  {`${t("etaHotStatus")}: ${etaHotStatus?.etaWorkerLost ? t("etaWorkerLost") : etaHotStatus?.hotBackendWorkerLost ? t("etaHotWorkerLost") : etaHotStatus?.busy ? t("etaHotBusy") : etaHotStatus?.status || t("etaHotNone")}`}
                </div>
                {etaHotStatus && etaHotStatus.lastSmokeValue ? (
                  <div style={{ fontSize: "13rem", lineHeight: "18rem", color: COLORS.title }}>
                    {`${t("etaHotSmokeValue")}: ${etaHotStatus.lastSmokeValue}`}
                  </div>
                ) : null}
                {etaHotStatus?.lastError ? (
                  <div style={{ fontSize: "12rem", lineHeight: "18rem", color: "#ffd6d1", whiteSpace: "pre-wrap" }}>
                    {etaHotStatus.lastError}
                  </div>
                ) : null}
                <div style={{ fontSize: "13rem", lineHeight: "18rem", color: COLORS.text, marginTop: "12rem" }}>
                  {`${t("etaSnapshotStatus")}: ${etaSnapshotStatus?.state || t("etaHotNone")}${etaSnapshotStatus?.failure && etaSnapshotStatus.failure !== "None" ? ` / ${etaSnapshotStatus.failure}` : ""}`}
                </div>
                {etaSnapshotStatus?.ticket ? (
                  <div style={{ fontSize: "12rem", lineHeight: "18rem", color: COLORS.muted }}>
                    {`#${etaSnapshotStatus.ticket}`}
                  </div>
                ) : null}
                {etaSnapshotStatus?.predictorSource ? (
                  <>
                    <div style={{ fontSize: "12rem", lineHeight: "18rem", color: COLORS.text }}>
                      {`${etaSnapshotStatus.predictorSource}/${etaSnapshotStatus.predictorBuildId || "-"} · gen ${etaSnapshotStatus.predictorGeneration || 0}`}
                    </div>
                    <div style={{ fontSize: "12rem", lineHeight: "18rem", color: COLORS.text }}>
                      {typeof etaSnapshotStatus.etaGameMinutes === "number"
                        ? `ETA ≈ ${etaSnapshotStatus.etaGameMinutes.toFixed(2)} game min · arrival ${etaSnapshotStatus.arrival || 0}`
                        : `arrival ${etaSnapshotStatus.arrival || 0}`}
                    </div>
                  </>
                ) : null}
                {etaSnapshotStatus?.detail ? (
                  <div style={{ fontSize: "12rem", lineHeight: "18rem", color: "#ffd6d1" }}>{etaSnapshotStatus.detail}</div>
                ) : null}
                {canRequestEta ? (
                  <div style={{ marginTop: "12rem" }}>
                    <ActionButton action="requestEtaSnapshot" label={t("etaSnapshotRequest")} disabled={etaHotDisabled} />
                  </div>
                ) : null}
                {etaSnapshotStatus?.comparisonState ? (
                  <div style={{ marginTop: "12rem" }}>
                    <div style={{ fontSize: "13rem", lineHeight: "18rem", fontWeight: 600, color: COLORS.title }}>
                      {`${t("etaComparisonTitle")}: ${etaSnapshotStatus.comparisonState}${etaSnapshotStatus.comparisonValid === false && etaSnapshotStatus.comparisonInvalidReason ? ` / ${etaSnapshotStatus.comparisonInvalidReason}` : ""}`}
                    </div>
                    <div style={{ fontSize: "12rem", lineHeight: "18rem", color: COLORS.text }}>
                      {etaSnapshotStatus.comparisonActualArrival
                        ? `${t("etaComparisonPredicted")} ${etaSnapshotStatus.comparisonPredictedArrival || 0} · ${t("etaComparisonActual")} ${etaSnapshotStatus.comparisonActualArrival}`
                        : `${t("etaComparisonPredicted")} ${etaSnapshotStatus.comparisonPredictedArrival || 0} · ${(etaSnapshotStatus.comparisonFramesToOrPastPrediction || 0) >= 0 ? t("etaComparisonRemaining") : t("etaComparisonPast")} ${Math.abs(etaSnapshotStatus.comparisonFramesToOrPastPrediction || 0)}`}
                    </div>
                    {etaSnapshotStatus.comparisonActualArrival ? (
                      <>
                        <div style={{ fontSize: "12rem", lineHeight: "18rem", color: COLORS.text }}>{`${t("etaComparisonFinishDelta")}: ${etaSnapshotStatus.comparisonFinishDelta || 0}`}</div>
                        <div style={{ fontSize: "12rem", lineHeight: "18rem", color: COLORS.text }}>{`${t("etaComparisonPublishDelta")}: ${etaSnapshotStatus.comparisonPublishDelta || 0}`}</div>
                        <div style={{ fontSize: "12rem", lineHeight: "18rem", color: COLORS.text }}>{`${t("etaComparisonOriginDelta")}: ${etaSnapshotStatus.comparisonOriginDelta || 0}`}</div>
                        <div style={{ fontSize: "12rem", lineHeight: "18rem", color: COLORS.text }}>{`${t("etaComparisonPredictionDelta")}: ${etaSnapshotStatus.comparisonPredictionDelta || 0}`}</div>
                      </>
                    ) : null}
                  </div>
                ) : null}
                <div style={{ display: "flex", flexDirection: "row", alignItems: "center" }}>
                  <ActionButton action="requestEtaHotReloadLatest" label={t("etaHotReloadLatest")} disabled={etaHotDisabled} />
                  <ActionButton action="requestEtaHotSmoke" label={t("etaHotRunSmoke")} marginLeft="12rem" disabled={etaHotDisabled} />
                </div>
                <div style={{ display: "flex", flexDirection: "row", alignItems: "center" }}>
                  <ActionButton action="requestEtaHotRollback" label={t("etaHotRollback")} disabled={etaRollbackDisabled} />
                </div>
              </SectionCard>
            ) : null}
          </div>
        </Scrollable>
      </Panel>
    </div>
  );
}
