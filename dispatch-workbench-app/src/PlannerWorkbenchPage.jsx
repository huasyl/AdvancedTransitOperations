import { useEffect, useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "./native-schedule-i18n";
import { getWorkbenchApi } from "./lib/workbench-api";
import WorkbenchDropdown from "./components/WorkbenchDropdown";
import WorkbenchInput from "./components/WorkbenchInput";
import WorkbenchScrollArea from "./components/WorkbenchScrollArea";

function PlannerField({ label, note = "", children }) {
  return (
    <div className="dw-planner-field">
      <label className="dw-planner-field-label">{label}</label>
      {children}
      {note ? <div className="dw-planner-field-note">{note}</div> : null}
    </div>
  );
}

function PlannerCompactField({ label, children }) {
  return (
    <div className="dw-planner-compact-field">
      {children}
      <label className="dw-planner-compact-field-label">{label}</label>
    </div>
  );
}

function PlannerSidebarSection({ title, children }) {
  return (
    <section className="dw-planner-form-section">
      <div className="dw-planner-form-section-title">{title}</div>
      <div className="dw-planner-form-section-body">{children}</div>
    </section>
  );
}

function PlannerToggleRow({ options, value, onChange, className = "" }) {
  return (
    <div className={`dw-planner-toggle-row${className ? ` ${className}` : ""}`}>
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          className={`dw-planner-toggle-button ${value === option.value ? "is-active" : ""}`}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

function PlannerChoiceGrid({ options, value, onToggle, disabledValues = null, className = "" }) {
  const disabledSet = disabledValues instanceof Set ? disabledValues : new Set();
  return (
    <div className={`dw-bc-platform-station-buttons dw-planner-station-tray${className ? ` ${className}` : ""}`}>
      {options.map((option) => {
        const isActive = Array.isArray(value) ? value.includes(option.value) : value === option.value;
        const isDisabled = disabledSet.has(option.value);
        return (
          <button
            key={option.value}
            type="button"
            className={`dw-bc-platform-station-button ${isActive ? "is-active" : ""} ${isDisabled ? "is-disabled" : ""}`}
            disabled={isDisabled}
            onClick={() => {
              if (!isDisabled) {
                onToggle(option.value);
              }
            }}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

function PlannerMultiSelectDropdown({ value, options, onToggle, portalHostRef }) {
  const selectedValues = Array.isArray(value) ? value : [];
  const selectedLabels = selectedValues
    .map((selectedValue) => options.find((option) => option.value === selectedValue)?.label || selectedValue)
    .filter(Boolean);

  return (
    <WorkbenchDropdown
      value={selectedLabels.length > 0 ? selectedLabels.join(" / ") : "--"}
      onSelect={onToggle}
      options={options.map((option) => ({
        ...option,
        key: option.value,
        active: selectedValues.includes(option.value),
        content: (
          <span className="dw-planner-multi-option">
            <span className={`dw-planner-multi-check ${selectedValues.includes(option.value) ? "is-checked" : ""}`} aria-hidden="true">
            </span>
            <span className="dw-planner-multi-label">{option.label}</span>
          </span>
        )
      }))}
      className="dw-planner-dropdown-field dw-planner-multi-dropdown"
      variant="field"
      positioning="portal"
      portalHostRef={portalHostRef}
      closeOnSelect={false}
    />
  );
}

function normalizeTimeInput(rawValue) {
  const rawText = String(rawValue || "");
  const digitsOnly = rawText.replace(/\D/g, "").slice(0, 4);
  if (digitsOnly.length < 2) {
    return digitsOnly;
  }
  if (digitsOnly.length === 2) {
    return rawText.indexOf(":") >= 0 ? `${digitsOnly}:` : digitsOnly;
  }

  return `${digitsOnly.slice(0, 2)}:${digitsOnly.slice(2)}`;
}

function isValidTimeValue(value) {
  const match = /^(\d{2}):(\d{2})$/.exec(String(value || "").trim());
  if (!match) {
    return false;
  }

  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  return Number.isFinite(hours) && Number.isFinite(minutes) && hours >= 0 && hours <= 23 && minutes >= 0 && minutes <= 59;
}

function timeToMinutes(value) {
  if (!isValidTimeValue(value)) {
    return null;
  }

  const [hours, minutes] = String(value).split(":").map((part) => Number(part));
  return hours * 60 + minutes;
}

function PlannerInput({ value, onChange, suffix = "", placeholder = "", mode = "text" }) {
  return (
    <div className="dw-planner-input-shell">
      <WorkbenchInput
        className="dw-planner-input-core"
        value={value}
        inputMode={mode === "numeric" ? "numeric" : "text"}
        placeholder={placeholder}
        onChange={(event) => onChange(event.target.value)}
      />
      {suffix ? <span className="dw-planner-input-suffix">{suffix}</span> : null}
    </div>
  );
}

function PlannerTimeInput({ value, onCommit, placeholder = "", onInvalidChange = null }) {
  const [draftValue, setDraftValue] = useState(String(value || ""));
  const [isInvalid, setIsInvalid] = useState(false);

  useEffect(() => {
    setDraftValue(String(value || ""));
    setIsInvalid(false);
    onInvalidChange?.(false);
  }, [onInvalidChange, value]);

  function setInvalidState(nextInvalid) {
    setIsInvalid(nextInvalid);
    onInvalidChange?.(nextInvalid);
  }

  function commitCurrentValue() {
    const nextValue = normalizeTimeInput(draftValue);
    if (!isValidTimeValue(nextValue)) {
      setDraftValue(nextValue);
      setInvalidState(true);
      return;
    }

    setDraftValue(nextValue);
    setInvalidState(false);
    onCommit(nextValue);
  }

  return (
    <div className={`dw-planner-input-shell ${isInvalid ? "is-error" : ""}`}>
      <WorkbenchInput
        className="dw-planner-input-core"
        value={draftValue}
        inputMode="numeric"
        maxLength={5}
        placeholder={placeholder}
        onChange={(event) => {
          const nextValue = normalizeTimeInput(event.target.value);
          setDraftValue(nextValue);
          setInvalidState(nextValue.length === 5 && !isValidTimeValue(nextValue));
        }}
        onPaste={(event) => {
          event.preventDefault();
          const pastedText = event.clipboardData?.getData("text") || "";
          setDraftValue(normalizeTimeInput(pastedText));
        }}
        onBlur={commitCurrentValue}
        onFocus={(event) => {
          if (typeof event.target.select === "function") {
            setTimeout(() => event.target.select(), 0);
          }
        }}
        onKeyDown={(event) => {
          const allowedKeys = [
            "Backspace",
            "Delete",
            "Tab",
            "ArrowLeft",
            "ArrowRight",
            "ArrowUp",
            "ArrowDown",
            "Home",
            "End",
            "Enter"
          ];
          const isDigitKey = event.key >= "0" && event.key <= "9";
          const isCtrlCommand = event.ctrlKey || event.metaKey;
          if (!isDigitKey && !allowedKeys.includes(event.key) && !isCtrlCommand) {
            event.preventDefault();
            return;
          }

          if (event.key === "Enter") {
            commitCurrentValue();
            event.currentTarget.blur();
          }
        }}
      />
    </div>
  );
}

function PlannerMetric({ label, value, tone = "default" }) {
  return (
    <div className="dw-planner-metric">
      <div className="dw-planner-metric-label">{label}</div>
      <div className={`dw-planner-metric-value is-${tone}`}>{value}</div>
    </div>
  );
}

function parsePositiveInt(value, fallbackValue = 0) {
  const parsed = Number.parseInt(String(value || "").trim(), 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallbackValue;
}

function waitForUiPaint() {
  return new Promise((resolve) => {
    if (typeof window !== "undefined" && typeof window.requestAnimationFrame === "function") {
      window.requestAnimationFrame(() => {
        window.requestAnimationFrame(() => {
          window.setTimeout(resolve, 50);
        });
      });
      return;
    }
    setTimeout(resolve, 50);
  });
}

function waitForMinimumDuration(startedAt, minimumMs) {
  const elapsedMs = Date.now() - startedAt;
  const remainingMs = Math.max(0, minimumMs - elapsedMs);
  if (remainingMs <= 0) {
    return Promise.resolve();
  }
  return new Promise((resolve) => setTimeout(resolve, remainingMs));
}

function waitForDelay(delayMs) {
  return new Promise((resolve) => setTimeout(resolve, delayMs));
}

function isTerminalPlannerJobState(state) {
  return state === "completed" || state === "failed" || state === "missing";
}

function pickPlannerDraft(plannerInput) {
  const drafts = Array.isArray(plannerInput?.drafts) ? plannerInput.drafts : [];
  const validLineIds = new Set(
    (Array.isArray(plannerInput?.lines) ? plannerInput.lines : [])
      .map((line) => line?.id)
      .filter((lineId) => typeof lineId === "string" && lineId)
  );

  const preferredDrafts = drafts.filter((draft) =>
    validLineIds.has(draft?.lineKey)
    || validLineIds.has(draft?.selectedLineId)
  );
  const candidateDrafts = preferredDrafts.length > 0 ? preferredDrafts : drafts;

  return candidateDrafts
    .slice()
    .sort((left, right) =>
      (((right?.lineDraftRows || right?.stagedRows)?.length || 0) + (right?.trips?.length || 0))
      - (((left?.lineDraftRows || left?.stagedRows)?.length || 0) + (left?.trips?.length || 0))
    )[0] || null;
}

function buildLineCollections(plannerInput) {
  const lines = Array.isArray(plannerInput?.lines) ? plannerInput.lines : [];
  const lineById = new Map(lines
    .filter((line) => line && typeof line.id === "string" && line.id)
    .map((line) => [line.id, line]));
  const aliasToCanonicalLineId = new Map();
  lines.forEach((line) => {
    if (!line || typeof line.id !== "string" || !line.id) {
      return;
    }
    aliasToCanonicalLineId.set(line.id, line.id);
    if (Number.isInteger(line.entityIndex) && line.entityIndex >= 0) {
      aliasToCanonicalLineId.set(String(line.entityIndex), line.id);
    }
  });
  const allLineOptions = lines
    .filter((line) => line && typeof line.id === "string" && line.id)
    .map((line) => ({
      value: line.id,
      label: typeof line.name === "string" && line.name ? line.name : line.id
    }));
  const localLineOptions = allLineOptions.filter(({ value }) => {
    const line = lineById.get(value);
    const kind = String(line?.configuredKind || line?.kind || "local").toLowerCase();
    return kind !== "express";
  });
  const expressLineOptions = allLineOptions.filter(({ value }) => {
    const line = lineById.get(value);
    const kind = String(line?.configuredKind || line?.kind || "").toLowerCase();
    return kind === "express";
  });
  return {
    lineById,
    canonicalizeLineId(lineId) {
      return aliasToCanonicalLineId.get(lineId) || lineId || "";
    },
    allLineOptions,
    localLineOptions,
    expressLineOptions
  };
}

function buildStationOptionsForLine(plannerInput, lineId) {
  const stations = Array.isArray(plannerInput?.stations) ? plannerInput.stations : [];
  return stations
    .filter((station) => station && station.lineId === lineId)
    .sort((left, right) => Number(left?.order || 0) - Number(right?.order || 0))
    .map((station) => ({
      value: station.workbenchStationId || station.id,
      label: station.name || station.id,
      stationId: station.id,
      lineId: station.lineId
    }));
}

function buildRelatedLineOptionsForTarget(plannerInput, targetLineId, lineOptions) {
  const target = String(targetLineId || "");
  if (!target) {
    return [];
  }

  const optionByLineId = new Map((Array.isArray(lineOptions) ? lineOptions : []).map((option) => [option.value, option]));
  const relatedIds = new Set();
  const corridors = Array.isArray(plannerInput?.currentTrackScenario?.sharedCorridors)
    ? plannerInput.currentTrackScenario.sharedCorridors
    : [];
  corridors.forEach((corridor) => {
    if (!corridor
      || String(corridor.traversalRelation || "").toLowerCase() !== "samedirection"
      || corridor.hasMirroredContext
      || Number(corridor.orderedRun || 0) <= 0
      || Number(corridor.physicalOverlap || 0) <= 0) {
      return;
    }
    if (corridor.lineId === target && optionByLineId.has(corridor.otherLineId)) {
      relatedIds.add(corridor.otherLineId);
    }
    if (corridor.otherLineId === target && optionByLineId.has(corridor.lineId)) {
      relatedIds.add(corridor.lineId);
    }
  });

  if (optionByLineId.has(target)) {
    relatedIds.add(target);
  }

  return [...relatedIds]
    .map((lineId) => optionByLineId.get(lineId))
    .filter(Boolean)
    .sort((left, right) => String(left.label || "").localeCompare(String(right.label || "")));
}

function buildForcedBypassOptions(plannerInput, expressSource, virtualBaseLine, adjustableLineIds) {
  const relevantLineIds = expressSource === "virtual" && virtualBaseLine
    ? [virtualBaseLine]
    : (Array.isArray(adjustableLineIds) && adjustableLineIds.length > 0 ? adjustableLineIds : []);
  const relevantLineIdSet = new Set((Array.isArray(relevantLineIds) ? relevantLineIds : []).filter(Boolean));
  const grouped = new Map();
  const allStations = [
    ...(Array.isArray(plannerInput?.configuredBypassStations) ? plannerInput.configuredBypassStations : []),
    ...(Array.isArray(plannerInput?.candidateBypassStations) ? plannerInput.candidateBypassStations : [])
  ];
  allStations.forEach((station) => {
    if (!station || !relevantLineIdSet.has(station.lineId) || !station.stationId) {
      return;
    }
    const nextOrder = Number(station?.order || 0);
    const label = station.name || station.stationId;
    const buildingEntityIndex = Number(station?.buildingEntityIndex);
    const groupKey = Number.isFinite(buildingEntityIndex) && buildingEntityIndex >= 0
      ? `building:${buildingEntityIndex}`
      : `name:${String(label || "").trim().toLowerCase()}`;
    const existing = grouped.get(groupKey);
    if (existing) {
      existing.stationIds.push(station.stationId);
      existing.order = Math.min(existing.order, Number.isFinite(nextOrder) ? nextOrder : existing.order);
      return;
    }

    grouped.set(groupKey, {
      value: groupKey,
      label,
      stationIds: [station.stationId],
      order: Number.isFinite(nextOrder) ? nextOrder : 0
    });
  });
  return [...grouped.values()]
    .map((option) => ({
      ...option,
      stationIds: [...new Set(option.stationIds)]
    }))
    .sort((left, right) => {
      if (left.order !== right.order) {
        return left.order - right.order;
      }
      return String(left.label || "").localeCompare(String(right.label || ""));
    })
    .map(({ value, label, stationIds }) => ({ value, label, stationIds }));
}

function expandForcedBypassStationIds(selectedValues, forcedBypassOptions) {
  const optionByValue = new Map((Array.isArray(forcedBypassOptions) ? forcedBypassOptions : [])
    .map((option) => [option.value, option]));
  return [...new Set((Array.isArray(selectedValues) ? selectedValues : []).flatMap((value) => {
    const option = optionByValue.get(value);
    if (option && Array.isArray(option.stationIds) && option.stationIds.length > 0) {
      return option.stationIds;
    }
    return value ? [value] : [];
  }).filter(Boolean))];
}

function summarizePlanType(status) {
  if (status === "infeasible" || status === "blocked") {
    return "error";
  }
  if (status === "risk" || status === "fragile" || status === "needsAction") {
    return "warning";
  }
  return "optimal";
}

function summarizePlanTypeFromRisks(riskItems, fallbackStatus) {
  const displayedTypes = (riskItems || []).map((item) => resolveDisplayedRiskProblemType(item));
  if (displayedTypes.includes("hardCatchup")) {
    return "error";
  }
  if (displayedTypes.includes("lowMargin") || displayedTypes.includes("backgroundConstraint")) {
    return "warning";
  }
  return summarizePlanType(fallbackStatus);
}

function formatPlannerBadgeLabel(status, riskItems, t) {
  const displayedTypes = (riskItems || []).map((item) => resolveDisplayedRiskProblemType(item));
  if (displayedTypes.includes("hardCatchup")) {
    return formatRiskTypeLabel("hardCatchup", t);
  }
  if (displayedTypes.includes("lowMargin")) {
    return formatRiskTypeLabel("lowMargin", t);
  }
  if (displayedTypes.includes("backgroundConstraint")) {
    return formatRiskTypeLabel("backgroundConstraint", t);
  }
  return formatPlannerStatusLabel(status, t);
}

function formatNumberValue(value) {
  const numeric = Number(value || 0);
  if (!Number.isFinite(numeric)) {
    return "0";
  }
  const rounded = Math.round(numeric * 10) / 10;
  return String(rounded).replace(/\.0$/, "");
}

function formatMinutesLabel(value) {
  return `${formatNumberValue(value)}m`;
}

function joinDisplayValues(values, separator = " / ", emptyValue = "--") {
  const parts = (Array.isArray(values) ? values : [])
    .map((value) => String(value || "").trim())
    .filter(Boolean);
  return parts.length > 0 ? parts.join(separator) : emptyValue;
}

function joinUniqueDisplayValues(values, separator = " / ", emptyValue = "--") {
  const parts = (Array.isArray(values) ? values : [])
    .map((value) => String(value || "").trim())
    .filter(Boolean);
  const uniqueParts = [...new Set(parts)];
  return uniqueParts.length > 0 ? uniqueParts.join(separator) : emptyValue;
}

function buildPlannerResolvers(plannerInput, result, t) {
  const lineNameById = new Map();
  const stationNameById = new Map();
  const lines = Array.isArray(plannerInput?.lines) ? plannerInput.lines : [];
  const stations = Array.isArray(plannerInput?.stations) ? plannerInput.stations : [];
  const targetLineIds = new Set(result?.lineRoleSummary?.targetLineIds || result?.selectedPlan?.lineRoleSummary?.targetLineIds || []);
  const expressSourceMode = String(result?.requestEcho?.expressSourceMode || "").toLowerCase();

  lines.forEach((line) => {
    if (line && line.id) {
      lineNameById.set(line.id, line.name || line.id);
    }
  });
  stations.forEach((station) => {
    if (!station) {
      return;
    }
    const label = station.name || station.id || station.workbenchStationId || "";
    if (station.id) {
      stationNameById.set(station.id, label);
    }
    if (station.workbenchStationId) {
      stationNameById.set(station.workbenchStationId, label);
    }
  });

  return {
    resolveLineName(lineId, fallbackValue = "") {
      if (lineNameById.has(lineId)) {
        return lineNameById.get(lineId);
      }
      if (lineId && targetLineIds.has(lineId) && expressSourceMode === "virtual") {
        return t("planner.line.virtualExpress");
      }
      if (fallbackValue) {
        return fallbackValue;
      }
      return lineId || "--";
    },
    resolveStationName(stationId, fallbackValue = "") {
      if (fallbackValue) {
        return fallbackValue;
      }
      if (stationNameById.has(stationId)) {
        return stationNameById.get(stationId);
      }
      return stationId || "--";
    }
  };
}

function formatPlannerObjectiveTitle(objectiveId, fallbackValue, t) {
  switch (objectiveId) {
    case "balanced":
      return t("planner.objective.balanced");
    case "fastestExpress":
      return t("planner.objective.fastestExpress");
    case "minBypassStations":
      return t("planner.objective.minBypassStations");
    case "maxSystemEfficiency":
      return t("planner.objective.maxSystemEfficiency");
    default:
      return objectiveId || fallbackValue || t("planner.empty.noPlanGenerated");
  }
}

function formatPlannerStatusLabel(status, t) {
  switch (status) {
    case "feasible":
      return t("planner.badge.feasible");
    case "needsAction":
      return t("planner.riskState.actionable");
    case "blocked":
      return t("planner.riskState.blocked");
    case "fragile":
      return t("planner.badge.fragile");
    case "risk":
      return t("planner.badge.risk");
    case "infeasible":
      return t("planner.badge.infeasible");
    default:
      return t("planner.badge.risk");
  }
}

function formatRiskSeverityLabel(severityLevel, t) {
  switch (severityLevel) {
    case "high":
      return t("planner.riskLevel.high");
    case "medium":
      return t("planner.riskLevel.medium");
    case "low":
      return t("planner.riskLevel.low");
    case "fragile":
      return t("planner.riskLevel.fragile");
    default:
      return t("planner.badge.feasible");
  }
}

function shouldShowRiskCluster(risk) {
  const severity = String(risk?.severityLevel || "").trim();
  return severity === "high" || severity === "medium" || severity === "low" || severity === "fragile";
}

function shouldShowRiskEvent(event) {
  const status = String(event?.statusCode || "").trim();
  return status === "unresolved"
    || status === "fragile"
    || Number(event?.unresolvedRiskMinutes || 0) > 0
    || Number(event?.robustnessRiskMinutes || 0) > 0;
}

function formatRiskEventStatusLabel(event, fallbackSeverity, t) {
  const status = String(event?.statusCode || "").trim();
  if (status === "unresolved") {
    return t("planner.riskLevel.high");
  }
  if (status === "fragile") {
    return t("planner.riskLevel.fragile");
  }
  return formatRiskSeverityLabel(fallbackSeverity, t);
}

function formatRiskReasonLabel(reasonCode, t) {
  switch (reasonCode) {
    case "waitLimitExceeded":
      return t("planner.risk.reason.waitLimitExceeded");
    case "unresolvedConflict":
      return t("planner.risk.reason.unresolvedConflict");
    case "lowMargin":
      return t("planner.risk.reason.lowMargin");
    default:
      return "";
  }
}

function formatRiskTypeLabel(problemType, t) {
  switch (problemType) {
    case "hardCatchup":
      return t("planner.riskType.hardCatchup");
    case "lowMargin":
      return t("planner.riskType.lowMargin");
    case "backgroundConstraint":
      return t("planner.riskType.backgroundConstraint");
    default:
      return t("planner.riskType.lowMargin");
  }
}

function formatResolutionStateLabel(resolutionState, t) {
  switch (resolutionState) {
    case "resolved":
    case "handled":
      return t("planner.riskState.resolved");
    case "actionable":
    case "needsAction":
    case "fragile":
      return t("planner.riskState.actionable");
    case "blocked":
    case "unresolved":
      return t("planner.riskState.blocked");
    default:
      return t("planner.riskState.actionable");
  }
}

function resolveRiskStateTone(resolutionState) {
  switch (resolutionState) {
    case "resolved":
    case "handled":
      return "success";
    case "blocked":
    case "unresolved":
      return "error";
    default:
      return "warning";
  }
}

function resolveRiskTypeTone(problemType) {
  return problemType === "hardCatchup" ? "error" : "warning";
}

function resolveDisplayedRiskProblemType(item) {
  const problemType = item?.problemType || "";
  const plannedMinutes = Number(item?.plannedAdjustmentMinutes || 0);
  const requiredHoldMinutes = Number(item?.requiredHoldMinutes || 0);
  const requiredMarginMinutes = Number(item?.requiredMarginMinutes || 0);
  const unresolvedRiskMinutes = Number(item?.unresolvedRiskMinutes || 0);
  const robustnessRiskMinutes = Number(item?.robustnessRiskMinutes || 0);
  if (unresolvedRiskMinutes > 0 || requiredHoldMinutes > plannedMinutes) {
    return "hardCatchup";
  }

  if (problemType !== "hardCatchup") {
    return problemType;
  }

  const hardCatchupResolved = unresolvedRiskMinutes <= 0 && plannedMinutes >= requiredHoldMinutes;
  const marginStillShort = robustnessRiskMinutes > 0 || requiredMarginMinutes > plannedMinutes;
  return hardCatchupResolved && marginStillShort ? "lowMargin" : problemType;
}

function formatBlockReasonLabel(blockReasonCode, t) {
  switch (blockReasonCode) {
    case "noUsableBypassStation":
      return t("planner.blockReason.noUsableBypassStation");
    case "waitBudgetTooLow":
      return t("planner.blockReason.waitBudgetTooLow");
    case "needsBypassStation":
      return t("planner.blockReason.needsBypassStation");
    case "selectedBypassStationNotUsable":
      return t("planner.blockReason.selectedBypassStationNotUsable");
    case "offsetRangeTooSmall":
      return t("planner.blockReason.offsetRangeTooSmall");
    default:
      return "";
  }
}

function formatSuggestedOptionLabel(optionCode, t) {
  switch (optionCode) {
    case "maxLocalWaitMinutes":
      return t("planner.suggestedOption.maxLocalWaitMinutes");
    case "maxAdditionalBypassStations":
      return t("planner.suggestedOption.maxAdditionalBypassStations");
    case "forcedBypassStationIds":
      return t("planner.suggestedOption.forcedBypassStationIds");
    case "maxLocalRetimeMinutes":
      return t("planner.suggestedOption.maxLocalRetimeMinutes");
    case "maxOffsetMinutes":
      return t("planner.suggestedOption.maxOffsetMinutes");
    case "adjustableLineIds":
      return t("planner.suggestedOption.adjustableLineIds");
    default:
      return "";
  }
}

function formatPairRoleLabel(pairRole, t) {
  switch (pairRole) {
    case "target-fixed":
      return t("planner.pairRole.targetFixed");
    case "adjustable-fixed":
      return t("planner.pairRole.adjustableFixed");
    case "target-adjustable":
      return t("planner.pairRole.targetAdjustable");
    default:
      return t("planner.pairRole.targetAdjustable");
  }
}

function formatRiskItemSummary(item, resolvers, t) {
  const fromStationId = item?.catchupFromStationId || item?.fromStationId || "";
  const toStationId = item?.catchupToStationId || item?.toStationId || "";
  const fromStationName = fromStationId ? resolvers.resolveStationName(fromStationId) : "";
  const toStationName = toStationId ? resolvers.resolveStationName(toStationId) : "";
  const interval = [
    fromStationName,
    toStationName
  ].filter(Boolean).join(" - ") || "--";
  const originalProblemType = item?.problemType || "";
  const problemType = resolveDisplayedRiskProblemType(item);
  if (problemType === "hardCatchup") {
    return t("planner.risk.summary.hardCatchup", {
      interval,
      required: formatMinutesLabel(item?.requiredHoldMinutes),
      gap: formatMinutesLabel(item?.currentWorstCaseGapMinutes)
    });
  }
  if (problemType === "backgroundConstraint") {
    return t("planner.risk.summary.backgroundConstraint", {
      interval,
      role: formatPairRoleLabel(item?.pairRole, t),
      gap: formatMinutesLabel(item?.currentWorstCaseGapMinutes)
    });
  }
  const plannedMinutes = Number(item?.plannedAdjustmentMinutes || 0);
  const requiredMarginMinutes = Number(item?.requiredMarginMinutes || 0);
  const marginShortfall = Math.max(0, requiredMarginMinutes - plannedMinutes);
  if (originalProblemType === "hardCatchup") {
    return t("planner.risk.summary.hardCatchupMarginOnly", {
      interval,
      margin: formatMinutesLabel(marginShortfall),
      gap: formatMinutesLabel(item?.currentWorstCaseGapMinutes)
    });
  }
  return t("planner.risk.summary.lowMargin", {
    interval,
    margin: formatMinutesLabel(marginShortfall),
    gap: formatMinutesLabel(item?.currentWorstCaseGapMinutes)
  });
}

function formatRiskItemAction(item, resolvers, t) {
  const state = item?.resolutionState || "";
  const selectedBypassStationId = item?.selectedBypassStationId || "";
  const stationName = selectedBypassStationId ? resolvers.resolveStationName(selectedBypassStationId, "") : "";
  const plannedMinutes = Number(item?.plannedAdjustmentMinutes || 0);
  if ((state === "resolved" || state === "handled") && plannedMinutes > 0 && stationName) {
    return t("planner.risk.action.resolvedHold", {
      station: stationName,
      minutes: formatMinutesLabel(plannedMinutes)
    });
  }
  if (state === "resolved" || state === "handled") {
    return t("planner.risk.action.resolved");
  }

  const reason = formatBlockReasonLabel(item?.blockReasonCode, t);
  const options = joinDisplayValues((item?.suggestedOptionCodes || [])
    .map((optionCode) => formatSuggestedOptionLabel(optionCode, t))
    .filter(Boolean), " / ", "");
  if (reason && options) {
    return t("planner.risk.action.blockedWithOptions", { reason, options });
  }
  if (reason) {
    return reason;
  }
  if (options) {
    return t("planner.risk.action.suggestedOptions", { options });
  }
  return t("planner.empty.noSuggestedActions");
}

function formatRiskItemDetail(item, t) {
  const state = item?.resolutionState || "";
  if (state !== "blocked") {
    return "";
  }

  const plannedMinutes = Number(item?.plannedAdjustmentMinutes || 0);
  const requiredHoldMinutes = Number(item?.requiredHoldMinutes || 0);
  const requiredMarginMinutes = Number(item?.requiredMarginMinutes || 0);
  const hardShortfall = Math.max(0, requiredHoldMinutes - plannedMinutes);
  const marginShortfall = Math.max(0, requiredMarginMinutes - plannedMinutes);
  const parts = [];
  if (hardShortfall > 0) {
    parts.push(t("planner.risk.detail.blockedShortfall", {
      catchupTime: item?.catchupTime || "--",
      planned: formatMinutesLabel(plannedMinutes),
      shortfall: formatMinutesLabel(hardShortfall)
    }));
  }
  if (hardShortfall > 0 && marginShortfall > hardShortfall) {
    parts.push(t("planner.risk.detail.blockedMarginShortfall", {
      shortfall: formatMinutesLabel(marginShortfall)
    }));
  }
  return parts.join(" ");
}

function mapRiskItemToDisplay(item, itemIndex, resolvers, t) {
  const problemType = resolveDisplayedRiskProblemType(item);
  const resolutionState = item?.resolutionState || "";
  const stateTone = resolveRiskStateTone(resolutionState);
  const typeTone = resolveRiskTypeTone(problemType);
  const displayTone = stateTone === "success" ? stateTone : typeTone;
  return {
    id: item?.riskId || `risk-item-${itemIndex}`,
    status: formatRiskTypeLabel(problemType, t),
    stateLabel: formatResolutionStateLabel(resolutionState, t),
    typeToneClass: `is-${typeTone}`,
    stateToneClass: `is-${displayTone}`,
    itemToneClass: `is-${displayTone}`,
    lineSrc: resolvers.resolveLineName(item?.yieldingLineId),
    lineDest: resolvers.resolveLineName(item?.priorityLineId),
    interval: [
      item?.catchupFromStationId || item?.fromStationId
        ? resolvers.resolveStationName(item?.catchupFromStationId || item?.fromStationId)
        : "",
      item?.catchupToStationId || item?.toStationId
        ? resolvers.resolveStationName(item?.catchupToStationId || item?.toStationId)
        : ""
    ].filter(Boolean).join(" - "),
    tripPair: t("planner.risk.tripPair", {
      yieldingTrip: formatTripDescriptor(
        resolvers.resolveLineName(item?.yieldingLineId),
        item?.yieldingDepartTime,
        item?.yieldingTripId
      ),
      priorityTrip: formatTripDescriptor(
        resolvers.resolveLineName(item?.priorityLineId),
        item?.priorityDepartTime,
        item?.priorityTripId
      )
    }),
    summary: formatRiskItemSummary(item, resolvers, t),
    detail: formatRiskItemDetail(item, t),
    action: formatRiskItemAction(item, resolvers, t),
    warning: displayTone === "error",
    events: []
  };
}

function formatTripDescriptor(lineName, departTime, tripId) {
  const label = [departTime, lineName].filter(Boolean).join(" ");
  return label || tripId || "--";
}

function formatActionSummary(action, resolvers, t) {
  const actionType = action?.actionType || action?.type || "";
  switch (actionType) {
    case "expressOffset":
      return t("planner.actionSummary.expressOffset", {
        delta: formatNumberValue(action?.deltaOffsetMinutes || action?.deltaMinutes)
      });
    case "bypassSet":
      return t("planner.actionSummary.bypassSet", {
        stationNames: joinDisplayValues((action?.stationIds || []).map((stationId) => resolvers.resolveStationName(stationId)))
      });
    case "retime":
      return t("planner.actionSummary.retime", {
        lineName: resolvers.resolveLineName(action?.affectedLineId),
        delta: formatNumberValue(action?.deltaMinutes)
      });
    case "predictedHold":
      return t("planner.actionSummary.predictedHold", {
        lineName: resolvers.resolveLineName(action?.affectedLineId),
        delta: formatNumberValue(action?.deltaMinutes)
      });
    default:
      return "";
  }
}

function formatRecommendedActionCode(actionCode, risk, resolvers, t) {
  switch (actionCode) {
    case "addBypassStation":
      return t("planner.recommendedAction.addBypassStation");
    case "relaxWaitLimit":
      return t("planner.recommendedAction.relaxWaitLimit");
    case "shiftExpressOffset":
      return t("planner.recommendedAction.shiftExpressOffset");
    case "addBuffer":
      return t("planner.recommendedAction.addBuffer");
    case "retimeLocalTrip":
      return t("planner.recommendedAction.retimeLocalTrip");
    case "keepCurrentPlan":
      return t("planner.recommendedAction.keepCurrentPlan");
    case "preferBypassStation":
      return t("planner.recommendedAction.preferBypassStation", {
        station: resolvers.resolveStationName(risk?.recommendedBypassStationId)
      });
    default:
      return "";
  }
}

function formatIssueMessage(issue, clusterById, resolvers, t) {
  const issueType = issue?.type || "";
  const cluster = clusterById.get(issue?.clusterId || "") || null;
  switch (issueType) {
    case "unresolvedConflict":
      return t("planner.issueMessage.unresolvedConflict", {
        priorityLine: resolvers.resolveLineName(issue?.priorityLineId),
        from: resolvers.resolveStationName(cluster?.fromStationId),
        to: resolvers.resolveStationName(cluster?.toStationId),
        yieldingLine: resolvers.resolveLineName(issue?.yieldingLineId),
        station: resolvers.resolveStationName(issue?.recommendedBypassStationId)
      });
    case "waitLimitExceeded":
      return t("planner.issueMessage.waitLimitExceeded", {
        yieldingLine: resolvers.resolveLineName(issue?.yieldingLineId),
        required: formatNumberValue(issue?.requiredHoldMinutes),
        budget: formatNumberValue(issue?.holdBudgetMinutes)
      });
    case "robustnessWeak":
      return t("planner.issueMessage.robustnessWeak", {
        risk: formatNumberValue(issue?.riskMinutes)
      });
    case "fixedLineAffected":
      return t("planner.issueMessage.fixedLineAffected", {
        lineNames: joinDisplayValues((issue?.lineIds || []).map((lineId) => resolvers.resolveLineName(lineId)))
      });
    case "originDepartureGap":
      return t("planner.issueMessage.originDepartureGap", {
        lineNames: joinDisplayValues((issue?.lineIds || []).map((lineId) => resolvers.resolveLineName(lineId))),
        gap: formatNumberValue(5 - Number(issue?.severityMinutes || 0))
      });
    default:
      return "";
  }
}

function formatDiagnosticMessage(diagnostic, t) {
  const code = String(diagnostic?.code || "").trim();
  switch (code) {
    case "NO_LOCAL_LINES":
      return t("planner.diagnosticCode.noLocalLines");
    case "VIRTUAL_BASE_LINE_MISSING":
      return t("planner.diagnosticCode.virtualBaseLineMissing");
    case "NO_WORKING_ROWS":
      return t("planner.diagnosticCode.noWorkingRows");
    case "BACKEND_ANALYSIS_READY":
      return "";
    default:
      return code || "";
  }
}

function translatePreviewStatus(row, t) {
  const statusCode = String(row?.statusCode || "").trim();
  switch (statusCode) {
    case "firstDeparture":
      return t("planner.previewStatus.first");
    case "expressPass":
      return t("planner.previewStatus.through");
    case "express":
      return t("planner.previewStatus.express");
    case "normal":
      return t("planner.previewStatus.normal");
    case "delayedByBypass":
      return t("planner.previewStatus.wait", { minutes: formatNumberValue(row?.statusMinutes || row?.deltaMinutes) });
    case "pending":
      return t("planner.previewStatus.pending");
    case "remove":
      return t("planner.previewStatus.remove");
    default:
      break;
  }
  if (row?.deltaMinutes > 0) {
    return t("planner.previewStatus.wait", { minutes: formatNumberValue(row.deltaMinutes) });
  }
  if (row?.kind === "express") {
    return t("planner.previewStatus.express");
  }
  return "--";
}

function formatChangedWindow(window, resolvers, t) {
  const resolvedLineNames = Array.isArray(window?.lineIds)
    ? window.lineIds.map((lineId, index) => resolvers.resolveLineName(lineId, window?.lineNames?.[index] || ""))
    : [];
  const lineNames = joinDisplayValues(resolvedLineNames, " / ", "")
    || joinDisplayValues(window?.lineNames, " / ", "");
  const rows = Array.isArray(window?.rowDiffs) ? window.rowDiffs : [];
  return {
    id: window?.windowId || "",
    title: lineNames && window?.fromTime && window?.toTime
      ? t("planner.change.windowTitle", {
        lineNames,
        fromTime: window.fromTime,
        toTime: window.toTime
      })
      : t("planner.empty.noLocalAdjustments"),
    rows: rows.map((row) => {
      const timeText = row?.beforeTime && row?.afterTime
        ? t("planner.change.rowTime", {
          beforeTime: row.beforeTime,
          afterTime: row.afterTime
        })
        : "";
      let reason = "";
      if (Number(row?.scheduleShiftMinutes || 0) !== 0) {
        reason = t("planner.change.rowShift", {
          scheduleShift: formatNumberValue(row.scheduleShiftMinutes)
        });
      } else if (Number(row?.predictedDelayMinutes || 0) !== 0) {
        reason = t("planner.change.rowDelay", {
          predictedDelay: formatNumberValue(row.predictedDelayMinutes)
        });
      } else if (Number(row?.totalDeltaMinutes || 0) !== 0) {
        reason = t("planner.change.rowTotal", {
          totalDelta: formatNumberValue(row.totalDeltaMinutes)
        });
      }
      return {
        id: row?.tripId || "",
        line: resolvers.resolveLineName(row?.lineId),
        timeText,
        summary: reason
      };
    })
  };
}

function formatPreviewRowInfo(rowDiff, t) {
  if (!rowDiff) {
    return "";
  }

  const parts = [];
  if (Number(rowDiff?.scheduleShiftMinutes || 0) !== 0) {
    parts.push(t("planner.change.rowShift", {
      scheduleShift: formatNumberValue(rowDiff.scheduleShiftMinutes)
    }));
  }
  if (Number(rowDiff?.predictedDelayMinutes || 0) !== 0) {
    parts.push(t("planner.change.rowDelay", {
      predictedDelay: formatNumberValue(rowDiff.predictedDelayMinutes)
    }));
  }
  if (Number(rowDiff?.totalDeltaMinutes || 0) !== 0) {
    parts.push(t("planner.change.rowTotal", {
      totalDelta: formatNumberValue(rowDiff.totalDeltaMinutes)
    }));
  }

  return parts.join(" / ");
}

function buildCondensedTimetableRows(timetableRows, changedWindows, resolvers, t) {
  const orderedRows = Array.isArray(timetableRows) ? timetableRows : [];
  const rowDiffs = (Array.isArray(changedWindows) ? changedWindows : [])
    .flatMap((window) => Array.isArray(window?.rowDiffs) ? window.rowDiffs : []);
  const rowDiffByTripId = new Map(
    rowDiffs
      .filter((row) => row?.tripId)
      .map((row) => [row.tripId, row])
  );
  const changedTripIds = new Set(rowDiffByTripId.keys());

  function mapPreviewRow(row) {
    const rowDiff = rowDiffByTripId.get(row.tripId) || null;
    return {
      id: row.tripId,
      time: rowDiff?.beforeTime && rowDiff?.afterTime
        ? t("planner.change.rowTime", {
          beforeTime: rowDiff.beforeTime,
          afterTime: rowDiff.afterTime
        })
        : row.time,
      line: resolvers.resolveLineName(row.lineId, row.lineName || ""),
      type: row.kind === "express" ? t("nativeSchedule.type.express") : t("nativeSchedule.type.local"),
      dotTone: row.kind === "express" ? "express" : "local",
      station: resolvers.resolveStationName(row.originStationId),
      status: translatePreviewStatus(row, t),
      info: formatPreviewRowInfo(rowDiff, t),
      warning: Number(row.deltaMinutes || 0) > 0 || Number(rowDiff?.totalDeltaMinutes || 0) !== 0
    };
  }

  if (orderedRows.length === 0) {
    return [];
  }

  if (changedTripIds.size === 0) {
    return orderedRows.map(mapPreviewRow);
  }

  const condensedRows = [];
  let skippedCount = 0;

  function flushSkipped() {
    if (skippedCount <= 0) {
      return;
    }
    condensedRows.push({
      id: `skip-${condensedRows.length}`,
      skip: true,
      message: t("planner.table.skip", {
        count: skippedCount
      })
    });
    skippedCount = 0;
  }

  for (let index = 0; index < orderedRows.length; index += 1) {
    const row = orderedRows[index];
    if (!changedTripIds.has(row?.tripId)) {
      skippedCount += 1;
      continue;
    }

    flushSkipped();
    condensedRows.push(mapPreviewRow(row));
  }

  flushSkipped();
  return condensedRows;
}

function mapPlannerResultToDisplay(result, plannerInput, t) {
  const selectedPlan = result?.selectedPlan;
  const rawPlanDetails = Array.isArray(result?.plans) && result.plans.length > 0
    ? result.plans
    : (selectedPlan ? [selectedPlan] : []);
  const seenObjectiveIds = new Set();
  const planDetails = rawPlanDetails.filter((plan) => {
    const objectiveId = String(plan?.objectiveId || "").trim();
    if (!objectiveId) {
      return true;
    }
    if (seenObjectiveIds.has(objectiveId)) {
      return false;
    }
    seenObjectiveIds.add(objectiveId);
    return true;
  });
  if (planDetails.length === 0) {
    return { plans: [], activePlanId: "" };
  }

  const resolvers = buildPlannerResolvers(plannerInput, result, t);
  const summaryById = new Map((result.planSummaries || []).map((plan) => [plan.planId, plan]));
  const plans = planDetails.map((plan, planIndex) => {
    const summary = summaryById.get(plan.planId) || {};
    const riskClusters = Array.isArray(plan.riskClusters) ? plan.riskClusters : [];
    const riskItems = Array.isArray(plan.riskItems) ? plan.riskItems : [];
    const structuredActions = Array.isArray(plan.structuredScheduleActions) ? plan.structuredScheduleActions : [];
    const affectedWaitTripIds = new Set();
    structuredActions.forEach((action) => {
      if ((action?.actionType || action?.type) !== "predictedHold") {
        return;
      }
      (action?.affectedTripIds || action?.tripIds || []).forEach((tripId) => {
        if (tripId) {
          affectedWaitTripIds.add(tripId);
        }
      });
    });
    const affectedWaitTripCount = affectedWaitTripIds.size;
    const localWaitMinutes = Number(summary.localWaitMinutes ?? plan.metrics?.localWaitMinutes ?? 0);
    const problemIssues = Array.isArray(plan.problemIssues) ? plan.problemIssues : [];
    const timetableRows = Array.isArray(plan.timetablePreviewRows) ? plan.timetablePreviewRows : [];
    const changedWindows = Array.isArray(plan.changedWindows) ? plan.changedWindows : [];
    const clusterById = new Map(riskClusters.map((riskCluster) => [riskCluster.clusterId, riskCluster]));
    const issueMessages = problemIssues
      .map((issue) => formatIssueMessage(issue, clusterById, resolvers, t))
      .filter(Boolean);
    const fallbackDiagnostics = (plan.diagnostics || [])
      .map((item) => formatDiagnosticMessage(item, t))
      .filter(Boolean);
    const combinedDiagnostics = [...new Set([...issueMessages, ...fallbackDiagnostics])];
    const badgeStatus = summary.status || plan.status || "risk";
    const primaryRiskItems = Array.isArray(riskItems) ? riskItems : [];

    return {
      id: plan.planId || `planner-plan-${planIndex}`,
      rawPlan: plan,
      title: formatPlannerObjectiveTitle(plan.objectiveId, "", t),
      type: summarizePlanTypeFromRisks(primaryRiskItems, badgeStatus),
      badgeLabel: formatPlannerBadgeLabel(badgeStatus, primaryRiskItems, t),
      metrics: {
        expressSave: Number(summary.expressSavedMinutes ?? plan.metrics?.expressSavedMinutes ?? 0),
        localWait: localWaitMinutes,
        averageLocalWait: affectedWaitTripCount > 0
          ? Number((localWaitMinutes / affectedWaitTripCount).toFixed(1))
          : 0,
        affectedWaitTrips: affectedWaitTripCount,
        overtakes: Number(summary.addedBypassStationCount ?? plan.metrics?.addedBypassStationCount ?? 0)
      },
      stations: Array.isArray(plan.selectedBypassStationIds) && plan.selectedBypassStationIds.length > 0
        ? joinUniqueDisplayValues(plan.selectedBypassStationIds.map((stationId) => resolvers.resolveStationName(stationId)))
        : "--",
      diagnostics: combinedDiagnostics,
      risks: riskItems.length > 0 ? riskItems.map((riskItem, riskItemIndex) =>
        mapRiskItemToDisplay(riskItem, riskItemIndex, resolvers, t)
      ) : riskClusters.filter(shouldShowRiskCluster).map((risk, riskIndex) => {
        const actionMessages = structuredActions
          .filter((action) =>
            (action?.clusterIds || []).includes(risk.clusterId)
            || (action?.reasonClusterIds || []).includes(risk.clusterId))
          .map((action) => formatActionSummary(action, resolvers, t))
          .filter(Boolean);
        const recommendedActionMessages = (risk?.recommendedActionCodes || [])
          .map((actionCode) => formatRecommendedActionCode(actionCode, risk, resolvers, t))
          .filter(Boolean);
        const representativeEvents = (Array.isArray(risk?.representativeEvents) ? risk.representativeEvents : [])
          .filter(shouldShowRiskEvent);
        return {
          id: risk.clusterId || `risk-${riskIndex}`,
          status: formatRiskSeverityLabel(risk.severityLevel, t),
          lineSrc: resolvers.resolveLineName(risk.yieldingLineId),
          lineDest: resolvers.resolveLineName(risk.priorityLineId),
          interval: [
            resolvers.resolveStationName(risk.fromStationId),
            resolvers.resolveStationName(risk.toStationId)
          ].filter(Boolean).join(" - "),
          catchups: Number(risk.catchupCount || 0),
          severity: formatMinutesLabel(risk.maxSeverityMinutes),
          suggestion: actionMessages.length > 0
            ? actionMessages.join(" / ")
            : (recommendedActionMessages.length > 0
              ? recommendedActionMessages.join(" / ")
              : t("planner.empty.noSuggestedActions")),
          warning: risk.severityLevel === "high",
          events: representativeEvents.map((event, eventIndex) => ({
            id: event?.eventId || `${risk.clusterId || "risk"}-event-${eventIndex}`,
            status: formatRiskEventStatusLabel(event, risk.severityLevel, t),
            warning: String(event?.statusCode || "") === "unresolved" || Number(event?.unresolvedRiskMinutes || 0) > 0,
            tripPair: t("planner.risk.tripPair", {
              yieldingTrip: formatTripDescriptor(
                resolvers.resolveLineName(event?.yieldingLineId),
                event?.yieldingDepartTime,
                event?.yieldingTripId
              ),
              priorityTrip: formatTripDescriptor(
                resolvers.resolveLineName(event?.priorityLineId),
                event?.priorityDepartTime,
                event?.priorityTripId
              )
            }),
            interval: [
              resolvers.resolveStationName(event?.catchupFromStationId || event?.fromStationId),
              resolvers.resolveStationName(event?.catchupToStationId || event?.toStationId)
            ].filter(Boolean).join(" - "),
            catchupTime: event?.catchupTime || "",
            waitStation: resolvers.resolveStationName(event?.selectedBypassStationId),
            required: formatMinutesLabel(event?.requiredHoldMinutes),
            budget: formatMinutesLabel(event?.holdBudgetMinutes),
            planned: Number.isFinite(Number(event?.plannedAdjustmentMinutes))
              ? t("planner.risk.plannedAdjustmentValue", {
                planned: formatNumberValue(event?.plannedAdjustmentMinutes)
              })
              : "",
            reason: formatRiskReasonLabel(event?.reasonCode, t)
          }))
        };
      }),
      changedWindows: changedWindows.map((window) => formatChangedWindow(window, resolvers, t)),
      timetableRows: buildCondensedTimetableRows(timetableRows, changedWindows, resolvers, t)
    };
  });

  return {
    plans,
    activePlanId: result?.defaultPlanId || result?.selectedPlan?.planId || plans[0]?.id || ""
  };
}

function normalizePlannerMergedViewForSave(snapshot, fallbackSelectedLineId = "") {
  const sourceView =
    snapshot?.mergedView && typeof snapshot.mergedView === "object"
      ? snapshot.mergedView
      : {};

  return {
    localLineId: typeof sourceView.localLineId === "string" ? sourceView.localLineId : (fallbackSelectedLineId || ""),
    expressLineId: typeof sourceView.expressLineId === "string" ? sourceView.expressLineId : "",
    localLineIds:
      Array.isArray(sourceView.localLineIds) && sourceView.localLineIds.length > 0
        ? sourceView.localLineIds.filter((lineId) => typeof lineId === "string" && lineId.length > 0)
        : (fallbackSelectedLineId ? [fallbackSelectedLineId] : []),
    expressLineIds:
      Array.isArray(sourceView.expressLineIds)
        ? sourceView.expressLineIds.filter((lineId) => typeof lineId === "string" && lineId.length > 0)
        : [],
    isLoop: typeof sourceView.isLoop === "boolean" ? sourceView.isLoop : true,
    turnbackStationId: typeof sourceView.turnbackStationId === "string" ? sourceView.turnbackStationId : "",
    direction: typeof sourceView.direction === "string" && sourceView.direction ? sourceView.direction : "up",
    windowStart: typeof sourceView.windowStart === "string" && sourceView.windowStart ? sourceView.windowStart : "06:00",
    windowEnd: typeof sourceView.windowEnd === "string" && sourceView.windowEnd ? sourceView.windowEnd : "06:30"
  };
}

function buildPlannerLineSettingsForSave(lines = []) {
  return (Array.isArray(lines) ? lines : [])
    .filter((line) => line && typeof line === "object" && line.id)
    .map((line) => ({
      lineId: line.id,
      originHoldLimitMinutes: Number(line?.originHoldLimitMinutes) || 20,
      maxStationDwellMinutes: Number(line?.maxStationDwellMinutes) || 10,
      allowedDepotId: line?.allowedDepotId || "",
      serviceKind: line?.kind === "express" ? "express" : "local"
    }));
}

function normalizePlannerRow(row, index, importedNote, prefix) {
  return {
    id: String(row?.id || row?.tripId || `${prefix}-${index + 1}`),
    lineId: String(row?.lineId || ""),
    time: String(row?.time || row?.afterTime || ""),
    kind: row?.kind === "express" ? "express" : "local",
    source: String(row?.source || "planner"),
    note: importedNote || row?.note || ""
  };
}

function buildPlannerReplacementRows(planDetail, importedNote) {
  return (Array.isArray(planDetail?.plannerReplacementRows) ? planDetail.plannerReplacementRows : [])
    .map((row, index) => normalizePlannerRow(row, index, importedNote, "planner-replacement"))
    .filter((row) => row.lineId && row.time)
    .map((row) => ({ ...row, source: "planner" }));
}

function buildPlannerBaselineRows(planDetail) {
  return (Array.isArray(planDetail?.plannerBaselineRows) ? planDetail.plannerBaselineRows : [])
    .map((row, index) => normalizePlannerRow(row, index, "", "planner-baseline"))
    .filter((row) => row.lineId && row.time);
}

function buildPlannerImportContract(plannerResult, activePlan, importedRows) {
  const rawPlan = activePlan?.rawPlan;
  if (!rawPlan || !rawPlan.planId) {
    return null;
  }

  return {
    draftKey: String(plannerResult?.requestEcho?.draftKey || ""),
    importedFrom: "planner-ui",
    importedPlanId: String(rawPlan.planId || ""),
    importedObjectiveId: String(rawPlan.objectiveId || ""),
    importedLineIds: [...new Set((Array.isArray(importedRows) ? importedRows : []).map((row) => row?.lineId).filter(Boolean))],
    requestEcho: plannerResult?.requestEcho || null,
    plan: rawPlan
  };
}

function buildPlannerPlanRefs(plannerResult, activePlan, importedRows) {
  const contract = buildPlannerImportContract(plannerResult, activePlan, importedRows);
  if (!contract) {
    return [];
  }

  return [...new Set((Array.isArray(importedRows) ? importedRows : []).map((row) => row?.lineId).filter(Boolean))]
    .map((lineId) => ({
      lineId,
      contract: {
        ...contract,
        draftKey: lineId
      }
    }));
}

function buildPlannerStagedRowKey(row) {
  const kind = row?.kind === "express" ? "express" : "local";
  const rowId = String(row?.id || "");
  return rowId
    ? `${row?.lineId || ""}|${kind}|${rowId}|${row?.time || ""}`
    : `${row?.lineId || ""}|${kind}|${row?.time || ""}`;
}

function getSnapshotLineDraftRowsByLineId(snapshot) {
  const rowsByLineId = new Map();
  if (Array.isArray(snapshot?.lineDraftRowsByLineId)) {
    snapshot.lineDraftRowsByLineId.forEach((block) => {
      const lineId = String(block?.lineId || "");
      if (!lineId) {
        return;
      }

      rowsByLineId.set(
        lineId,
        (Array.isArray(block?.lineDraftRows) ? block.lineDraftRows : [])
          .map((row, index) => normalizePlannerRow(row, index, row?.note || "", "current-draft"))
          .filter((row) => row.lineId && row.time)
      );
    });
    return rowsByLineId;
  }

  (Array.isArray(snapshot?.lineDraftRows) ? snapshot.lineDraftRows : [])
    .map((row, index) => normalizePlannerRow(row, index, row?.note || "", "current-draft"))
    .filter((row) => row.lineId && row.time)
    .forEach((row) => {
      if (!rowsByLineId.has(row.lineId)) {
        rowsByLineId.set(row.lineId, []);
      }
      rowsByLineId.get(row.lineId).push(row);
    });
  return rowsByLineId;
}

function isPlannerRowInsideWindow(row, windowStartMinutes, windowEndMinutes) {
  const minutes = timeToMinutes(row?.time);
  return minutes != null && minutes >= windowStartMinutes && minutes < windowEndMinutes;
}

function buildPlannerReplacementDraftBlocks(snapshot, baselineRows, replacementRows, requestEcho) {
  const startMinutes = timeToMinutes(requestEcho?.windowStart);
  const endMinutes = timeToMinutes(requestEcho?.windowEnd);
  if (startMinutes == null || endMinutes == null || endMinutes <= startMinutes) {
    return null;
  }

  const affectedLineIds = [...new Set(replacementRows.map((row) => row.lineId).filter(Boolean))];
  if (affectedLineIds.length === 0) {
    return null;
  }

  const affectedLineSet = new Set(affectedLineIds);
  const rowsByLineId = getSnapshotLineDraftRowsByLineId(snapshot);

  for (const lineId of affectedLineIds) {
    const currentRows = rowsByLineId.get(lineId) || [];
    const currentKeys = currentRows
      .filter((row) => isPlannerRowInsideWindow(row, startMinutes, endMinutes))
      .map(buildPlannerStagedRowKey)
      .sort();
    const baselineRowsInWindow = baselineRows
      .filter((row) => row.lineId === lineId && isPlannerRowInsideWindow(row, startMinutes, endMinutes));
    const baselineKeys = baselineRowsInWindow
      .map(buildPlannerStagedRowKey)
      .sort();
    const baselineIsRuntimeOnly = baselineRowsInWindow.length > 0
      && baselineRowsInWindow.every((row) => row?.source === "tripDerived");
    if (currentKeys.length === 0 && baselineIsRuntimeOnly) {
      continue;
    }
    if (currentKeys.length !== baselineKeys.length || currentKeys.some((key, index) => key !== baselineKeys[index])) {
      return null;
    }
  }

  return affectedLineIds.map((lineId) => {
    const currentRows = rowsByLineId.get(lineId) || [];
    const preservedRows = currentRows
      .filter((row) => !isPlannerRowInsideWindow(row, startMinutes, endMinutes));
    const insertedRows = replacementRows
      .filter((row) => row.lineId === lineId && isPlannerRowInsideWindow(row, startMinutes, endMinutes));
    const lineDraftRows = [...preservedRows, ...insertedRows]
      .sort((left, right) => {
        const leftMinutes = timeToMinutes(left?.time) ?? 9999;
        const rightMinutes = timeToMinutes(right?.time) ?? 9999;
        if (leftMinutes !== rightMinutes) {
          return leftMinutes - rightMinutes;
        }

        return String(left?.id || "").localeCompare(String(right?.id || ""));
      })
      .map((row, index) => ({
        id: String(row?.id || `planner-${lineId}-${index + 1}`),
        lineId,
        time: row.time,
        kind: row.kind === "express" ? "express" : "local",
        source: row.source || "planner",
        note: row.note || ""
      }));
    return { lineId, lineDraftRows };
  }).filter((block) => affectedLineSet.has(block.lineId));
}

function buildPlannerRequest(params) {
  const {
    plannerInput,
    analysisStart,
    analysisEnd,
    adjustableLines,
    expressSource,
    virtualBaseLine,
    existingExpressLine,
    expressStops,
    dispatchMode,
    dispatchInterval,
    dispatchPhaseStart,
    dispatchTripsPerHour,
    phaseAdjustmentRange,
    maxOvertakes,
    maxLocalShift,
    maxLocalWait,
    forcedOvertakes,
    forcedBypassOptions
  } = params;

  const lineCollections = buildLineCollections(plannerInput);
  const canonicalizeLineId = lineCollections.canonicalizeLineId || ((lineId) => lineId || "");
  const normalizedAdjustableLineIds = adjustableLines
    .map(canonicalizeLineId)
    .filter(Boolean);
  const request = {
    draftKey: pickPlannerDraft(plannerInput)?.lineKey || "",
    windowStart: analysisStart,
    windowEnd: analysisEnd,
    adjustableLineIds: [...new Set(normalizedAdjustableLineIds)],
    expressSourceMode: expressSource === "existing" ? "existing" : "virtual",
    expressLineId: expressSource === "existing" ? canonicalizeLineId(existingExpressLine) : "",
    virtualExpressBaseLineId: expressSource === "virtual" ? canonicalizeLineId(virtualBaseLine) : "",
    expressStopStationIds: expressSource === "virtual" ? expressStops.filter(Boolean) : [],
    departureMode: dispatchMode,
    expressTripsPerHour: dispatchMode === "frequency" ? parsePositiveInt(dispatchTripsPerHour, 0) : 0,
    intervalMinutes: dispatchMode === "interval" || dispatchMode === "phase" || dispatchMode === "reinterval"
      ? parsePositiveInt(dispatchInterval, 0)
      : 0,
    phaseTime: dispatchMode === "phase" ? dispatchPhaseStart : "",
    expressOffsetMinutes: 0,
    maxOffsetMinutes: expressSource === "existing" && dispatchMode === "shift"
      ? parsePositiveInt(phaseAdjustmentRange, 0)
      : 0,
    offsetStepMinutes: expressSource === "existing" && dispatchMode === "shift" ? 2 : 0,
    maxLocalRetimeMinutes: parsePositiveInt(maxLocalShift, 0),
    maxLocalWaitMinutes: parsePositiveInt(maxLocalWait, 0),
    maxAdditionalBypassStations: parsePositiveInt(maxOvertakes, 0),
    forcedBypassStationIds: expandForcedBypassStationIds(forcedOvertakes, forcedBypassOptions)
  };

  if (expressSource === "existing" && !lineCollections.expressLineOptions.some((option) => option.value === request.expressLineId)) {
    request.expressLineId = lineCollections.expressLineOptions[0]?.value || "";
  }

  return request;
}

function PlannerPlanTabs({ plans, activeId, onChange, labels }) {
  return (
    <div className="dw-planner-plan-tabs">
      {plans.map((plan) => {
        const isActive = activeId === plan.id;
        return (
          <button
            key={plan.id}
            type="button"
            className={`dw-planner-plan-tab ${isActive ? "is-active" : ""}`}
            onClick={() => onChange(plan.id)}
          >
            <span className="dw-planner-plan-tab-title">{plan.title}</span>
            <span className={`dw-planner-plan-badge is-${plan.type}`}>
              {plan.badgeLabel || (plan.type === "optimal"
                ? labels.feasible
                : plan.type === "warning"
                  ? labels.risk
                  : labels.infeasible)}
            </span>
          </button>
        );
      })}
    </div>
  );
}

export default function PlannerWorkbenchPage({ pageEnterSequence = 0 }) {
  const { t } = useNativeScheduleI18n();
  const dropdownPortalHostRef = useRef(null);
  const generateRunIdRef = useRef(0);
  const pageAliveRef = useRef(true);
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const [analysisStart, setAnalysisStart] = useState("05:00");
  const [analysisEnd, setAnalysisEnd] = useState("09:00");
  const [analysisStartInvalid, setAnalysisStartInvalid] = useState(false);
  const [analysisEndInvalid, setAnalysisEndInvalid] = useState(false);
  const [leftTab, setLeftTab] = useState("service");
  const [adjustableLines, setAdjustableLines] = useState([]);
  const [expressSource, setExpressSource] = useState("virtual");
  const [virtualBaseLine, setVirtualBaseLine] = useState("");
  const [existingExpressLine, setExistingExpressLine] = useState("");
  const [expressStops, setExpressStops] = useState([]);
  const [dispatchMode, setDispatchMode] = useState("interval");
  const [dispatchInterval, setDispatchInterval] = useState("30");
  const [dispatchPhaseStart, setDispatchPhaseStart] = useState("05:00");
  const [dispatchTripsPerHour, setDispatchTripsPerHour] = useState("2");
  const [phaseAdjustmentRange, setPhaseAdjustmentRange] = useState("4");
  const [maxOvertakes, setMaxOvertakes] = useState("2");
  const [maxLocalShift, setMaxLocalShift] = useState("4");
  const [maxLocalWait, setMaxLocalWait] = useState("6");
  const [forcedOvertakes, setForcedOvertakes] = useState([]);
  const [activePlanId, setActivePlanId] = useState("p1");
  const [isGenerating, setIsGenerating] = useState(false);
  const [isImportingDraft, setIsImportingDraft] = useState(false);
  const [importedPlanId, setImportedPlanId] = useState("");
  const [plannerInput, setPlannerInput] = useState(null);
  const [plannerResult, setPlannerResult] = useState(null);
  const [plannerLoadError, setPlannerLoadError] = useState("");
  const [plannerInitialized, setPlannerInitialized] = useState(false);

  const lineCollections = useMemo(() => buildLineCollections(plannerInput), [plannerInput]);
  const lineOptions = lineCollections.allLineOptions;
  const localLineOptions = lineCollections.localLineOptions;
  const expressLineOptions = lineCollections.expressLineOptions;
  const stationOptions = useMemo(
    () => buildStationOptionsForLine(plannerInput, virtualBaseLine),
    [plannerInput, virtualBaseLine]
  );
  const targetScopeLineId = expressSource === "virtual" ? virtualBaseLine : existingExpressLine;
  const adjustableLineOptions = useMemo(
    () => buildRelatedLineOptionsForTarget(plannerInput, targetScopeLineId, lineOptions)
      .filter((option) => expressSource === "virtual" || option.value !== targetScopeLineId),
    [expressSource, lineOptions, plannerInput, targetScopeLineId]
  );
  const readonlyConstraintLineOptions = useMemo(() => {
    const adjustableSet = new Set(adjustableLines);
    return adjustableLineOptions.filter((option) => !adjustableSet.has(option.value));
  }, [adjustableLineOptions, adjustableLines]);
  const forcedBypassOptions = useMemo(
    () => buildForcedBypassOptions(
      plannerInput,
      expressSource,
      virtualBaseLine,
      adjustableLines
    ),
    [adjustableLines, expressSource, plannerInput, virtualBaseLine]
  );

  const expressSourceOptions = useMemo(
    () => ([
      { value: "virtual", label: t("planner.expressSource.virtual") },
      { value: "existing", label: t("planner.expressSource.existing") }
    ]),
    [t]
  );

  const virtualDispatchOptions = useMemo(
    () => ([
      { value: "interval", label: t("planner.dispatch.virtual.interval") },
      { value: "frequency", label: t("planner.dispatch.virtual.frequency") },
      { value: "phase", label: t("planner.dispatch.virtual.phase") }
    ]),
    [t]
  );

  const existingDispatchOptions = useMemo(
    () => ([
      { value: "existing", label: t("planner.dispatch.existing.keep") },
      { value: "shift", label: t("planner.dispatch.existing.shift") }
    ]),
    [t]
  );

  const overtakesOptions = useMemo(
    () => ([
      { value: "0", label: t("planner.bypassCount.0") },
      { value: "1", label: t("planner.bypassCount.1") },
      { value: "2", label: t("planner.bypassCount.2") },
      { value: "3", label: t("planner.bypassCount.3") }
    ]),
    [t]
  );

  const mockPlans = useMemo(
    () => ([
      {
        id: "mock-plan",
        title: t("planner.objective.balanced"),
        type: "optimal",
        badgeLabel: t("planner.badge.feasible"),
        metrics: { expressSave: 0, localWait: 0, overtakes: 0 },
        stations: "--",
        diagnostics: [t("planner.empty.noPlanGenerated")],
        risks: [],
        changedWindows: [],
        timetableRows: []
      }
    ]),
    [t]
  );

  const planLabels = useMemo(
    () => ({
      feasible: t("planner.badge.feasible"),
      risk: t("planner.badge.risk"),
      infeasible: t("planner.badge.infeasible")
    }),
    [t]
  );

  const liveDisplay = useMemo(() => mapPlannerResultToDisplay(plannerResult, plannerInput, t), [plannerInput, plannerResult, t]);
  const plans = liveDisplay.plans.length > 0 ? liveDisplay.plans : mockPlans;
  const activePlan = plans.find((plan) => plan.id === activePlanId) || plans[0];
  const timetableRows = Array.isArray(activePlan?.timetableRows) ? activePlan.timetableRows : [];
  const showGenericPlanError = activePlan?.type === "error" && (activePlan?.risks || []).length === 0;

  const analysisStartMinutes = timeToMinutes(analysisStart);
  const analysisEndMinutes = timeToMinutes(analysisEnd);
  const dispatchOptions = expressSource === "virtual" ? virtualDispatchOptions : existingDispatchOptions;
  const analysisTimeError = analysisStartInvalid || analysisEndInvalid
    ? t("validation.error.timeFormat")
    : analysisStartMinutes !== null && analysisEndMinutes !== null && analysisEndMinutes <= analysisStartMinutes
      ? t("nativeSchedule.message.auto.invalidWindow")
      : "";
  const generateDisabled = isGenerating
    || !plannerInput
    || lineOptions.length === 0
    || !!analysisTimeError;
  const importReferenceOnly = expressSource === "virtual" && !!plannerResult && !!activePlan?.rawPlan;
  const importDisabled = isGenerating
    || isImportingDraft
    || !plannerResult
    || !activePlan?.rawPlan
    || showGenericPlanError
    || importReferenceOnly
    || !!importedPlanId;

  useEffect(() => {
    return () => {
      pageAliveRef.current = false;
      generateRunIdRef.current += 1;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    async function loadPlannerInput() {
      try {
        const nextPlannerInput = await workbenchApi.loadPlannerContext?.();
        if (cancelled) {
          return;
        }
        setPlannerInput(nextPlannerInput || null);
        setPlannerLoadError("");
      } catch (error) {
        if (!cancelled) {
          setPlannerLoadError(error?.message || "planner-load-failed");
        }
      }
    }
    loadPlannerInput();
    return () => {
      cancelled = true;
    };
  }, [pageEnterSequence, workbenchApi]);

  useEffect(() => {
    setPlannerInitialized(false);
  }, [plannerInput?.generatedAtFrame, plannerInput?.version]);

  useEffect(() => {
    if (!plannerInput || plannerInitialized) {
      return;
    }

    const canonicalizeLineId = lineCollections.canonicalizeLineId || ((lineId) => lineId || "");
    const draft = pickPlannerDraft(plannerInput);
    const mergedLocal = Array.isArray(draft?.mergedView?.localLineIds)
      ? draft.mergedView.localLineIds.map(canonicalizeLineId).filter(Boolean)
      : [];
    const mergedExpress = Array.isArray(draft?.mergedView?.expressLineIds)
      ? draft.mergedView.expressLineIds.map(canonicalizeLineId).filter(Boolean)
      : [];
    const nextExpressSource = mergedExpress.length > 0 && expressLineOptions.length > 0 ? "existing" : "virtual";
    const nextVirtualBaseLine = mergedLocal[0] || localLineOptions[0]?.value || "";
    const nextExistingExpressLine = mergedExpress[0] || expressLineOptions[0]?.value || "";
    const nextTargetScopeLineId = nextExpressSource === "virtual" ? nextVirtualBaseLine : nextExistingExpressLine;
    const nextAdjustableOptions = buildRelatedLineOptionsForTarget(plannerInput, nextTargetScopeLineId, lineOptions)
      .filter((option) => nextExpressSource === "virtual" || option.value !== nextTargetScopeLineId);
    const nextAdjustableOptionIds = new Set(nextAdjustableOptions.map((option) => option.value));
    const nextAdjustableSeeds = [...new Set([...mergedLocal, ...mergedExpress])]
      .filter((lineId) => nextAdjustableOptionIds.has(lineId));
    const nextAdjustable = nextAdjustableSeeds.length > 0
      ? nextAdjustableSeeds
      : nextAdjustableOptions.map((option) => option.value);
    const nextStationOptions = buildStationOptionsForLine(plannerInput, nextVirtualBaseLine);

    setAdjustableLines(nextAdjustable);
    setExpressSource(nextExpressSource);
    setVirtualBaseLine(nextVirtualBaseLine);
    setExistingExpressLine(nextExistingExpressLine);
    setExpressStops(nextStationOptions.map((option) => option.value));
    setPlannerInitialized(true);
  }, [expressLineOptions, lineCollections.canonicalizeLineId, lineOptions, localLineOptions, plannerInitialized, plannerInput]);

  useEffect(() => {
    const canonicalizeLineId = lineCollections.canonicalizeLineId || ((lineId) => lineId || "");
    const allowedLineIds = new Set(lineOptions.map((option) => option.value));
    setVirtualBaseLine((current) => {
      const next = canonicalizeLineId(current);
      return allowedLineIds.has(next) ? next : "";
    });
    setExistingExpressLine((current) => {
      const next = canonicalizeLineId(current);
      return allowedLineIds.has(next) ? next : "";
    });
  }, [lineCollections.canonicalizeLineId, lineOptions]);

  useEffect(() => {
    const canonicalizeLineId = lineCollections.canonicalizeLineId || ((lineId) => lineId || "");
    const allowedLineIds = new Set(adjustableLineOptions.map((option) => option.value));
    setAdjustableLines((current) => {
      const next = [...new Set((Array.isArray(current) ? current : [])
        .map(canonicalizeLineId)
        .filter((lineId) => lineId && allowedLineIds.has(lineId)))];
      return next.length === current.length && next.every((value, index) => value === current[index]) ? current : next;
    });
  }, [adjustableLineOptions, lineCollections.canonicalizeLineId]);

  useEffect(() => {
    if (virtualBaseLine && localLineOptions.some((option) => option.value === virtualBaseLine)) {
      return;
    }
    if (localLineOptions.length > 0) {
      setVirtualBaseLine(localLineOptions[0].value);
    }
  }, [localLineOptions, virtualBaseLine]);

  useEffect(() => {
    if (existingExpressLine && expressLineOptions.some((option) => option.value === existingExpressLine)) {
      return;
    }
    if (expressLineOptions.length > 0) {
      setExistingExpressLine(expressLineOptions[0].value);
    }
  }, [existingExpressLine, expressLineOptions]);

  useEffect(() => {
    const allowedStops = new Set(stationOptions.map((option) => option.value));
    setExpressStops((current) => current.filter((stationId) => allowedStops.has(stationId)));
  }, [stationOptions]);

  useEffect(() => {
    const allowedForced = new Set(forcedBypassOptions.map((option) => option.value));
    setForcedOvertakes((current) => current.filter((stationId) => allowedForced.has(stationId)));
  }, [forcedBypassOptions]);

  useEffect(() => {
    if (!liveDisplay.activePlanId) {
      return;
    }
    setActivePlanId(liveDisplay.activePlanId);
  }, [liveDisplay.activePlanId]);

  useEffect(() => {
    setImportedPlanId("");
    setIsImportingDraft(false);
  }, [plannerResult]);

  useEffect(() => {
    if (expressSource === "virtual" && !["interval", "frequency", "phase"].includes(dispatchMode)) {
      setDispatchMode("interval");
    }
    if (expressSource === "existing" && !["existing", "shift"].includes(dispatchMode)) {
      setDispatchMode("existing");
    }
  }, [dispatchMode, expressSource]);

  function toggleArrayValue(currentValues, nextValue) {
    return currentValues.includes(nextValue)
      ? currentValues.filter((value) => value !== nextValue)
      : [...currentValues, nextValue];
  }

  async function handleGenerate() {
    const runId = generateRunIdRef.current + 1;
    generateRunIdRef.current = runId;
    const loadingStartedAt = Date.now();
    setIsGenerating(true);
    setPlannerLoadError("");
    setPlannerResult(null);
    await waitForUiPaint();
    try {
      const request = buildPlannerRequest({
        plannerInput,
        analysisStart,
        analysisEnd,
        adjustableLines,
        expressSource,
        virtualBaseLine,
        existingExpressLine,
        expressStops,
        dispatchMode,
        dispatchInterval,
        dispatchPhaseStart,
        dispatchTripsPerHour,
        phaseAdjustmentRange,
        maxOvertakes,
        maxLocalShift,
        maxLocalWait,
        forcedOvertakes,
        forcedBypassOptions
      });

      const startedJob = await workbenchApi.startPlannerJob?.(request);
      if (!startedJob?.jobId) {
        throw new Error(startedJob?.error || "planner-job-start-failed");
      }

      let latestStatus = startedJob;
      while (pageAliveRef.current && generateRunIdRef.current === runId && !isTerminalPlannerJobState(latestStatus?.state)) {
        await waitForDelay(120);
        latestStatus = await workbenchApi.getPlannerJobStatus?.(startedJob.jobId);
      }

      if (!pageAliveRef.current || generateRunIdRef.current !== runId) {
        return;
      }

      if (!latestStatus || latestStatus.state === "missing") {
        throw new Error(latestStatus?.error || "planner-job-not-found");
      }

      if (latestStatus.state === "failed") {
        throw new Error(latestStatus.error || "planner-job-failed");
      }

      const result = latestStatus.result || null;
      setPlannerResult(result || null);
      if (!result?.success) {
        const diagnosticMessage = (result?.diagnostics || [])
          .map((item) => formatDiagnosticMessage(item, t))
          .filter(Boolean)
          .join(" / ");
        setPlannerLoadError(diagnosticMessage);
      }
    } catch (error) {
      if (pageAliveRef.current && generateRunIdRef.current === runId) {
        setPlannerResult(null);
        setPlannerLoadError(error?.message || "planner-run-failed");
      }
    } finally {
      if (pageAliveRef.current && generateRunIdRef.current === runId) {
        await waitForMinimumDuration(loadingStartedAt, 300);
        setIsGenerating(false);
      }
    }
  }

  async function handleWritePlanToDraft() {
    if (importDisabled) {
      return;
    }

    setIsImportingDraft(true);
    setPlannerLoadError("");
    await waitForUiPaint();
    try {
      const snapshot = await (workbenchApi.refreshSnapshot?.() || workbenchApi.loadSnapshot?.());
      if (!snapshot || !Array.isArray(snapshot.lines) || snapshot.lines.length === 0) {
        setPlannerLoadError(t("planner.import.error.snapshot"));
        return;
      }

      const replacementRows = buildPlannerReplacementRows(activePlan.rawPlan, t("combined.note.planner"));
      const baselineRows = buildPlannerBaselineRows(activePlan.rawPlan);
      if (replacementRows.length === 0 || baselineRows.length === 0) {
        setPlannerLoadError(t("planner.import.error.empty"));
        return;
      }

      const runtimeLineIdSet = new Set(snapshot.lines.map((line) => line?.id).filter(Boolean));
      const unsupportedLineIds = [...new Set(replacementRows
        .map((row) => row.lineId)
        .filter((lineId) => !runtimeLineIdSet.has(lineId)))];
      if (unsupportedLineIds.length > 0) {
        setPlannerLoadError(t("planner.import.error.unsupported"));
        return;
      }

      const lineDraftRowsByLineId = buildPlannerReplacementDraftBlocks(
        snapshot,
        baselineRows,
        replacementRows,
        plannerResult?.requestEcho
      );
      if (!lineDraftRowsByLineId || lineDraftRowsByLineId.length === 0) {
        setPlannerLoadError(t("planner.import.error.save"));
        return;
      }

      const fallbackSelectedLineId = snapshot.selectedLineId || snapshot.lines[0]?.id || "";
      const mergedViewForSave = normalizePlannerMergedViewForSave(snapshot, fallbackSelectedLineId);
      const importedLocalLineIds = replacementRows
        .filter((row) => row?.kind !== "express")
        .map((row) => row?.lineId)
        .filter(Boolean);
      const importedExpressLineIds = replacementRows
        .filter((row) => row?.kind === "express")
        .map((row) => row?.lineId)
        .filter(Boolean);
      mergedViewForSave.localLineIds = [...new Set([...(mergedViewForSave.localLineIds || []), ...importedLocalLineIds])];
      mergedViewForSave.expressLineIds = [...new Set([...(mergedViewForSave.expressLineIds || []), ...importedExpressLineIds])];
      mergedViewForSave.localLineId = mergedViewForSave.localLineIds[0] || mergedViewForSave.localLineId || "";
      mergedViewForSave.expressLineId = mergedViewForSave.expressLineIds[0] || mergedViewForSave.expressLineId || "";
      const saveScopeLineIds = new Set([
        fallbackSelectedLineId,
        snapshot.selectedEditLine || fallbackSelectedLineId,
        ...(mergedViewForSave.localLineIds || []),
        ...(mergedViewForSave.expressLineIds || [])
      ].filter(Boolean));
      const request = {
        selectedLineId: fallbackSelectedLineId,
        selectedEditLine: snapshot.selectedEditLine || fallbackSelectedLineId,
        mergedView: mergedViewForSave,
        manualRows: Array.isArray(snapshot.manualRows)
          ? snapshot.manualRows.filter((row) => saveScopeLineIds.has(row?.lineId))
          : [],
        autoRules: Array.isArray(snapshot.autoRules)
          ? snapshot.autoRules.filter((rule) => saveScopeLineIds.has(rule?.lineId))
          : [],
        lineDraftRowsByLineId,
        lineSettings: buildPlannerLineSettingsForSave(snapshot.lines),
        applyDraft: false,
        nativeScheduleWriter: true,
        planRefs: buildPlannerPlanRefs(plannerResult, activePlan, replacementRows)
      };

      const result = await workbenchApi.saveNativeDraft?.(request);
      if (!result?.success) {
        setPlannerLoadError(t("planner.import.error.save"));
        return;
      }

      setImportedPlanId(activePlan?.id || activePlan?.rawPlan?.planId || "imported");
    } catch {
      setPlannerLoadError(t("planner.import.error.save"));
    } finally {
      setIsImportingDraft(false);
    }
  }

  return (
    <div className="dw-planner-page">
      <div className="dw-planner-shell">
        <section className="dw-planner-sidebar">
          <div className="dw-planner-sidebar-tabs">
            <button
              type="button"
              className={`dw-planner-sidebar-tab ${leftTab === "service" ? "is-active" : ""}`}
              onClick={() => setLeftTab("service")}
            >
              {t("planner.left.service")}
            </button>
            <button
              type="button"
              className={`dw-planner-sidebar-tab ${leftTab === "constraints" ? "is-active" : ""}`}
              onClick={() => setLeftTab("constraints")}
            >
              {t("planner.left.constraints")}
            </button>
          </div>

          <WorkbenchScrollArea className="dw-planner-sidebar-scroll" metricsKey={`${leftTab}:${expressSource}:${dispatchMode}`}>
            <div className="dw-planner-sidebar-content">
              {leftTab === "service" ? (
                <>
                  <PlannerSidebarSection title={t("planner.group.analysis")}>
                    <div className="dw-planner-split-row is-balanced-pair">
                      <div className="dw-planner-split-cell">
                        <PlannerField label={t("planner.field.analysisStart")}>
                          <PlannerTimeInput
                            value={analysisStart}
                            onCommit={setAnalysisStart}
                            onInvalidChange={setAnalysisStartInvalid}
                            placeholder="05:00"
                          />
                        </PlannerField>
                      </div>
                      <div className="dw-planner-split-cell">
                        <PlannerField label={t("planner.field.analysisEnd")}>
                          <PlannerTimeInput
                            value={analysisEnd}
                            onCommit={setAnalysisEnd}
                            onInvalidChange={setAnalysisEndInvalid}
                            placeholder="09:00"
                          />
                        </PlannerField>
                      </div>
                    </div>
                    <div className={`dw-planner-field-error-slot is-reserved ${analysisTimeError ? "has-error" : ""}`}>
                      {analysisTimeError ? <div className="dw-planner-field-error">{analysisTimeError}</div> : null}
                    </div>
                    {plannerLoadError ? (
                      <div className="dw-planner-field-error">{plannerLoadError}</div>
                    ) : null}
                  </PlannerSidebarSection>

                  <PlannerSidebarSection title={t("planner.group.planTarget")}>
                    <PlannerToggleRow
                      options={expressSourceOptions}
                      value={expressSource}
                      onChange={setExpressSource}
                    />

                    {expressSource === "virtual" ? (
                      <PlannerField label={t("planner.field.basedOnLine")}>
                        <WorkbenchDropdown
                          value={localLineOptions.find((option) => option.value === virtualBaseLine)?.label || ""}
                          onSelect={setVirtualBaseLine}
                          options={localLineOptions.map((option) => ({
                            ...option,
                            key: option.value,
                            active: option.value === virtualBaseLine
                          }))}
                          className="dw-planner-dropdown-field"
                          variant="field"
                          positioning="portal"
                          portalHostRef={dropdownPortalHostRef}
                        />
                      </PlannerField>
                    ) : (
                      <PlannerField label={t("planner.field.targetExpressLine")}>
                        <WorkbenchDropdown
                          value={expressLineOptions.find((option) => option.value === existingExpressLine)?.label || ""}
                          onSelect={setExistingExpressLine}
                          options={expressLineOptions.map((option) => ({
                            ...option,
                            key: option.value,
                            active: option.value === existingExpressLine
                          }))}
                          className="dw-planner-dropdown-field"
                          variant="field"
                          positioning="portal"
                          portalHostRef={dropdownPortalHostRef}
                        />
                      </PlannerField>
                    )}

                    {expressSource === "virtual" ? (
                      <PlannerField label={t("planner.field.rapidStops")}>
                        <PlannerMultiSelectDropdown
                          options={stationOptions}
                          value={expressStops}
                          onToggle={(stationId) => setExpressStops((current) => toggleArrayValue(current, stationId))}
                          portalHostRef={dropdownPortalHostRef}
                        />
                      </PlannerField>
                    ) : null}
                  </PlannerSidebarSection>

                  <PlannerSidebarSection title={t("planner.group.dispatch")}>
                    <PlannerField label={t("planner.field.dispatchMode")}>
                      <WorkbenchDropdown
                        value={dispatchOptions.find((option) => option.value === dispatchMode)?.label || ""}
                        onSelect={setDispatchMode}
                        options={dispatchOptions}
                        className="dw-planner-dropdown-field"
                        variant="field"
                        positioning="portal"
                        portalHostRef={dropdownPortalHostRef}
                      />
                    </PlannerField>

                    {expressSource === "virtual" && dispatchMode === "interval" ? (
                      <PlannerCompactField label={t("planner.field.rapidInterval")}>
                        <PlannerInput
                          value={dispatchInterval}
                          onChange={setDispatchInterval}
                          suffix={t("nativeSchedule.unit.minutes")}
                          mode="numeric"
                        />
                      </PlannerCompactField>
                    ) : null}

                    {expressSource === "virtual" && dispatchMode === "frequency" ? (
                      <PlannerCompactField label={t("planner.field.tripsPerHour")}>
                        <PlannerInput
                          value={dispatchTripsPerHour}
                          onChange={setDispatchTripsPerHour}
                          suffix={t("planner.unit.tripsPerHour")}
                          mode="numeric"
                        />
                      </PlannerCompactField>
                    ) : null}

                    {expressSource === "virtual" && dispatchMode === "phase" ? (
                      <div className="dw-planner-split-row is-balanced-pair">
                        <div className="dw-planner-split-cell">
                          <PlannerCompactField label={t("planner.field.firstDeparturePhase")}>
                            <PlannerTimeInput
                              value={dispatchPhaseStart}
                              onCommit={setDispatchPhaseStart}
                              placeholder="05:00"
                            />
                          </PlannerCompactField>
                        </div>
                        <div className="dw-planner-split-cell">
                          <PlannerCompactField label={t("planner.field.rapidInterval")}>
                            <PlannerInput
                              value={dispatchInterval}
                              onChange={setDispatchInterval}
                              suffix={t("nativeSchedule.unit.minutes")}
                              mode="numeric"
                            />
                          </PlannerCompactField>
                        </div>
                      </div>
                    ) : null}

                    {expressSource === "existing" && dispatchMode === "shift" ? (
                      <PlannerCompactField label={t("planner.field.phaseAdjustmentRange")}>
                        <PlannerInput
                          value={phaseAdjustmentRange}
                          onChange={setPhaseAdjustmentRange}
                          suffix={t("nativeSchedule.unit.minutes")}
                          mode="numeric"
                        />
                      </PlannerCompactField>
                    ) : null}

                    {expressSource === "existing" && dispatchMode === "reinterval" ? (
                      <PlannerCompactField label={t("planner.field.rapidInterval")}>
                        <PlannerInput
                          value={dispatchInterval}
                          onChange={setDispatchInterval}
                          suffix={t("nativeSchedule.unit.minutes")}
                          mode="numeric"
                        />
                      </PlannerCompactField>
                    ) : null}
                  </PlannerSidebarSection>
                </>
              ) : (
                <>
                  <PlannerSidebarSection title={t("planner.group.adjustment")}>
                    <PlannerField label={t("planner.field.adjustableLines")}>
                      <PlannerMultiSelectDropdown
                        options={adjustableLineOptions}
                        value={adjustableLines}
                        onToggle={(lineId) => setAdjustableLines((current) => toggleArrayValue(current, lineId))}
                        portalHostRef={dropdownPortalHostRef}
                      />
                    </PlannerField>

                    <PlannerField label={t("planner.field.backgroundConstraintLines")}>
                      <div className="dw-planner-readonly-lines">
                        {readonlyConstraintLineOptions.length > 0 ? (
                          readonlyConstraintLineOptions.map((option) => (
                            <span key={option.value} className="dw-planner-readonly-line">{option.label}</span>
                          ))
                        ) : (
                          <span className="dw-planner-readonly-empty">{t("planner.empty.noConstraintLines")}</span>
                        )}
                      </div>
                    </PlannerField>

                    <div className="dw-planner-split-row is-balanced-pair">
                      <div className="dw-planner-split-cell">
                        <PlannerField label={t("planner.field.maxShift")}>
                          <PlannerInput
                            value={maxLocalShift}
                            onChange={setMaxLocalShift}
                            suffix={t("nativeSchedule.unit.minutes")}
                            mode="numeric"
                          />
                        </PlannerField>
                      </div>
                      <div className="dw-planner-split-cell">
                        <PlannerField label={t("planner.field.maxWait")}>
                          <PlannerInput
                            value={maxLocalWait}
                            onChange={setMaxLocalWait}
                            suffix={t("nativeSchedule.unit.minutes")}
                            mode="numeric"
                          />
                        </PlannerField>
                      </div>
                    </div>
                  </PlannerSidebarSection>

                  <PlannerSidebarSection title={t("planner.group.bypassRules")}>
                    <PlannerField label={t("planner.field.maxBypass")}>
                      <PlannerToggleRow
                        options={overtakesOptions}
                        value={maxOvertakes}
                        onChange={setMaxOvertakes}
                        className="is-compact"
                      />
                    </PlannerField>

                    <PlannerField label={t("planner.field.forcedStations")}>
                      <PlannerChoiceGrid
                        options={forcedBypassOptions}
                        value={forcedOvertakes}
                        onToggle={(stationId) => setForcedOvertakes((current) => toggleArrayValue(current, stationId))}
                      />
                    </PlannerField>
                  </PlannerSidebarSection>
                </>
              )}
            </div>
          </WorkbenchScrollArea>

          <footer className="dw-planner-sidebar-foot">
            <button
              type="button"
              className={`dw-planner-primary-button dw-planner-generate-button ${isGenerating ? "is-disabled" : ""}`}
              onClick={handleGenerate}
              disabled={generateDisabled}
            >
              {isGenerating ? t("planner.button.generating") : t("planner.button.generate")}
            </button>
          </footer>
        </section>

        <section className="dw-planner-main">
          <PlannerPlanTabs plans={plans} activeId={activePlanId} onChange={setActivePlanId} labels={planLabels} />

          {isGenerating ? (
            <div className="dw-planner-loading">
              <div className="dw-planner-spinner" aria-hidden="true" />
              <div className="dw-planner-loading-title">{t("planner.loading.title")}</div>
              <div className="dw-planner-loading-body">{t("planner.loading.body")}</div>
            </div>
          ) : (
            <>
              <WorkbenchScrollArea className="dw-planner-main-scroll" metricsKey={activePlanId}>
                <div className="dw-planner-main-content">
                  {showGenericPlanError ? (
                    <div className="dw-planner-error-state">
                      <div className="dw-planner-error-title">{t("planner.error.title")}</div>
                      <div className="dw-planner-diagnostics-list">
                        {activePlan.diagnostics.map((item) => (
                          <div key={item} className="dw-planner-diagnostics-item">
                            {item}
                          </div>
                        ))}
                      </div>
                    </div>
                  ) : (
                    <>
                      <section className="dw-planner-section">
                        <div className="dw-planner-card-head">{t("planner.overview.title")}</div>
                        <div className="dw-planner-section-rule" />
                        <div className="dw-planner-metrics-row">
                          <PlannerMetric label={t("planner.metrics.expressSave")} value={`${activePlan?.metrics?.expressSave ?? 0}m`} tone="success" />
                          <PlannerMetric label={t("planner.metrics.localWait")} value={`${activePlan?.metrics?.localWait ?? 0}m`} tone={activePlan?.type === "warning" ? "warning" : "default"} />
                          <PlannerMetric label={t("planner.metrics.averageLocalWait")} value={`${activePlan?.metrics?.averageLocalWait ?? 0}m`} tone="default" />
                          <PlannerMetric label={t("planner.metrics.affectedWaitTrips")} value={`${activePlan?.metrics?.affectedWaitTrips ?? 0}`} />
                          <PlannerMetric label={t("planner.metrics.bypassCount")} value={`${activePlan?.metrics?.overtakes ?? 0}`} />
                        </div>
                      </section>

                      <section className="dw-planner-section">
                        <div className="dw-planner-card-head">{t("planner.changes.title")}</div>
                        <div className="dw-planner-section-rule" />
                        {(activePlan?.changedWindows || []).length > 0 ? (
                          <div className="dw-planner-window-list">
                            {(activePlan?.changedWindows || []).map((window) => (
                              <div key={window.id} className="dw-planner-window-item">
                                <div className="dw-planner-window-head">
                                  <div className="dw-planner-window-title">{window.title}</div>
                                </div>
                                <div className="dw-planner-window-rows">
                                  {(window.rows || []).map((row) => (
                                    <div key={row.id} className="dw-planner-window-row">
                                      <div className="dw-planner-window-row-line">{row.line}</div>
                                      <div className="dw-planner-window-row-summary">
                                        {[row.timeText, row.summary].filter(Boolean).join(" / ")}
                                      </div>
                                    </div>
                                  ))}
                                </div>
                              </div>
                            ))}
                          </div>
                        ) : (
                          <div className="dw-planner-inline-note">{t("planner.empty.noLocalAdjustments")}</div>
                        )}
                      </section>

                      <section className="dw-planner-section">
                        <div className="dw-planner-card-head">{t("planner.riskZones.title")}</div>
                        <div className="dw-planner-section-rule" />
                        <div className="dw-planner-inline-summary">
                          <span className="dw-planner-inline-summary-label">{t("planner.risk.selectedBypass")}</span>
                          <span className="dw-planner-inline-summary-value">{activePlan?.stations || "--"}</span>
                        </div>
                        {(activePlan?.risks || []).length > 0 ? (
                          <div className="dw-planner-risk-list">
                            {(activePlan?.risks || []).map((risk) => (
                              <div key={risk.id} className={`dw-planner-risk-item ${risk.itemToneClass || (risk.warning ? "is-error" : "is-warning")}`}>
                                <div className="dw-planner-risk-head">
                                  <span className={`dw-planner-risk-badge ${risk.typeToneClass || (risk.warning ? "is-error" : "is-warning")}`}>{risk.status}</span>
                                  {risk.stateLabel ? (
                                    <span className={`dw-planner-risk-badge ${risk.stateToneClass || (risk.warning ? "is-error" : "is-warning")}`}>{risk.stateLabel}</span>
                                  ) : null}
                                  <span className="dw-planner-risk-route">
                                    <span className="dw-planner-risk-route-source">{risk.lineSrc}</span>
                                    <span className="dw-planner-risk-route-arrow">→</span>
                                    <span className="dw-planner-risk-route-dest">{risk.lineDest}</span>
                                  </span>
                                </div>
                                {risk.summary ? (
                                  risk.tripPair ? (
                                    <div className="dw-planner-risk-trip">{risk.tripPair}</div>
                                  ) : null
                                ) : null}
                                {risk.summary ? (
                                  <div className="dw-planner-risk-summary">{risk.summary}</div>
                                ) : null}
                                {risk.detail ? (
                                  <div className="dw-planner-risk-detail">{risk.detail}</div>
                                ) : null}
                                {!risk.summary && (risk.events || []).length === 0 ? (
                                  <div className="dw-planner-risk-range">
                                    <span className="dw-planner-risk-label">{t("planner.risk.range")}</span>
                                    <span>{risk.interval}</span>
                                  </div>
                                ) : null}
                                {!risk.summary && (risk.events || []).length > 0 ? (
                                  <div className="dw-planner-risk-events">
                                    {(risk.events || []).map((event) => (
                                      <div key={event.id} className="dw-planner-risk-event">
                                        <div className="dw-planner-risk-event-head">
                                          <span className={`dw-planner-risk-badge ${event.warning ? "is-error" : "is-warning"}`}>{event.status}</span>
                                          <span>{event.tripPair}</span>
                                        </div>
                                        <div className="dw-planner-risk-range">
                                          <span className="dw-planner-risk-label">{t("planner.risk.range")}</span>
                                          <span>{event.interval || risk.interval}</span>
                                        </div>
                                        {event.waitStation ? (
                                          <div className="dw-planner-risk-range">
                                            <span className="dw-planner-risk-label">{t("planner.risk.waitStation")}</span>
                                            <span>{event.waitStation}</span>
                                          </div>
                                        ) : null}
                                        <div className="dw-planner-risk-stats">
                                          {event.catchupTime ? <span>{t("planner.risk.catchupTime")} <span className="dw-planner-risk-stat-value">{event.catchupTime}</span></span> : null}
                                          <span>{t("planner.risk.requiredAdjustment")} <span className="dw-planner-risk-stat-value">{event.required}</span></span>
                                          {event.planned ? <span>{t("planner.risk.plannedAdjustment")} <span className="dw-planner-risk-stat-value">{event.planned}</span></span> : null}
                                          <span>{t("planner.risk.waitLimit")} <span className="dw-planner-risk-stat-value">{event.budget}</span></span>
                                        </div>
                                        {event.reason ? (
                                          <div className="dw-planner-risk-action">
                                            <span className="dw-planner-risk-action-label">{t("planner.risk.reasonLabel")}</span>
                                            <span>{event.reason}</span>
                                          </div>
                                        ) : null}
                                      </div>
                                    ))}
                                  </div>
                                ) : null}
                                {!risk.summary && (risk.events || []).length === 0 ? (
                                  <div className="dw-planner-risk-stats">
                                    <span>{t("planner.risk.catchups")} <span className="dw-planner-risk-stat-value">{risk.catchups}</span></span>
                                    <span>{t("planner.risk.maxGap")} <span className="dw-planner-risk-stat-value">{risk.severity}</span></span>
                                  </div>
                                ) : null}
                                <div className="dw-planner-risk-action">
                                  <span className="dw-planner-risk-action-label">{t("planner.risk.actionLabel")}</span>
                                  <span>{risk.action || risk.suggestion}</span>
                                </div>
                              </div>
                            ))}
                          </div>
                        ) : (
                          <div className="dw-planner-inline-note">{t("planner.empty.noRiskZones")}</div>
                        )}
                      </section>

                    </>
                  )}
                </div>
              </WorkbenchScrollArea>

              {activePlan?.type !== "error" ? (
                <footer className="dw-planner-main-foot">
                  <button
                    type="button"
                    className={`dw-planner-primary-button ${importDisabled ? "is-disabled" : ""}`}
                    onClick={handleWritePlanToDraft}
                    disabled={importDisabled}
                  >
                    {importReferenceOnly ? t("planner.footer.referenceOnly") : t("planner.footer.apply")}
                  </button>
                </footer>
              ) : null}
            </>
          )}
        </section>
      </div>
      <div ref={dropdownPortalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}
