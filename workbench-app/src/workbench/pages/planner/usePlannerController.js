import { useEffect, useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { buildPlannerBaselineRows, buildPlannerLineSettingsForSave, buildPlannerPlanRefs, buildPlannerReplacementDraftBlocks, buildPlannerReplacementRows, normalizePlannerMergedViewForSave } from "./planner-import.js";
import { buildPlannerRequest, pickPlannerDraft } from "./planner-request.js";
import { formatDiagnosticMessage } from "./planner-format.js";
import { isTerminalPlannerJobState, timeToMinutes, waitForDelay, waitForMinimumDuration, waitForUiPaint } from "./planner-time.js";
import { buildForcedBypassOptions, buildLineCollections, buildRelatedLineOptionsForTarget, buildStationOptionsForLine, mapPlannerResultToDisplay } from "./planner-view-models.js";

export default function usePlannerController({ pageEnterSequence = 0, activeTransportMode = "train" } = {}) {
  const { t } = useNativeScheduleI18n();
  const plannerMode = String(activeTransportMode || "train").trim().toLowerCase() || "train";
  const dropdownPortalHostRef = useRef(null);
  const generateRunIdRef = useRef(0);
  const pageAliveRef = useRef(true);
  const activeModeRef = useRef(plannerMode);
  activeModeRef.current = plannerMode;
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

  const plannerInputMode = String(plannerInput?.mode || "").trim().toLowerCase();
  const scopedPlannerInput = !plannerInputMode || plannerInputMode === plannerMode ? plannerInput : null;
  const lineCollections = useMemo(() => buildLineCollections(scopedPlannerInput), [scopedPlannerInput]);
  const lineOptions = lineCollections.allLineOptions;
  const localLineOptions = lineCollections.localLineOptions;
  const expressLineOptions = lineCollections.expressLineOptions;
  const stationOptions = useMemo(
    () => buildStationOptionsForLine(scopedPlannerInput, virtualBaseLine),
    [scopedPlannerInput, virtualBaseLine]
  );
  const targetScopeLineId = expressSource === "virtual" ? virtualBaseLine : existingExpressLine;
  const adjustableLineOptions = useMemo(
    () => buildRelatedLineOptionsForTarget(scopedPlannerInput, targetScopeLineId, lineOptions)
      .filter((option) => expressSource === "virtual" || option.value !== targetScopeLineId),
    [expressSource, lineOptions, scopedPlannerInput, targetScopeLineId]
  );
  const readonlyConstraintLineOptions = useMemo(() => {
    const adjustableSet = new Set(adjustableLines);
    return adjustableLineOptions.filter((option) => !adjustableSet.has(option.value));
  }, [adjustableLineOptions, adjustableLines]);
  const forcedBypassOptions = useMemo(
    () => buildForcedBypassOptions(
      scopedPlannerInput,
      expressSource,
      virtualBaseLine,
      adjustableLines
    ),
    [adjustableLines, expressSource, scopedPlannerInput, virtualBaseLine]
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
        metrics: {
          expressSave: 0,
          baselineHighestCapacityConsumptionPercent: 0,
          optimizedHighestCapacityConsumptionPercent: 0,
          averageLocalWait: 0,
          affectedWaitTrips: 0,
          overtakes: 0
        },
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

  const plannerResultMode = String(plannerResult?.mode || plannerResult?.requestEcho?.mode || "").trim().toLowerCase();
  const plannerResultMatchesMode = !plannerResultMode || plannerResultMode === plannerMode;
  const scopedPlannerResult = plannerResultMatchesMode ? plannerResult : null;
  const liveDisplay = useMemo(() => mapPlannerResultToDisplay(scopedPlannerResult, scopedPlannerInput, t), [scopedPlannerInput, scopedPlannerResult, t]);
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
    || !scopedPlannerInput
    || lineOptions.length === 0
    || !!analysisTimeError;
  const importReferenceOnly = expressSource === "virtual" && !!scopedPlannerResult && !!activePlan?.rawPlan;
  const importDisabled = isGenerating
    || isImportingDraft
    || !scopedPlannerResult
    || !activePlan?.rawPlan
    || showGenericPlanError
    || importReferenceOnly
    || !!importedPlanId;

  function isCurrentPlannerRun(runId, mode) {
    return pageAliveRef.current
      && generateRunIdRef.current === runId
      && activeModeRef.current === mode;
  }

  useEffect(() => {
    return () => {
      pageAliveRef.current = false;
      generateRunIdRef.current += 1;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    const requestMode = plannerMode;
    const runId = generateRunIdRef.current + 1;
    generateRunIdRef.current = runId;
    setIsGenerating(false);
    setIsImportingDraft(false);
    setImportedPlanId("");
    setPlannerInput(null);
    setPlannerResult(null);
    setPlannerInitialized(false);

    async function loadPlannerInput() {
      try {
        const nextPlannerInput = await workbenchApi.loadPlannerContext?.({ mode: requestMode });
        if (cancelled || !isCurrentPlannerRun(runId, requestMode)) {
          return;
        }
        if (nextPlannerInput?.mode && nextPlannerInput.mode !== requestMode) {
          return;
        }
        setPlannerInput(nextPlannerInput || null);
        setPlannerLoadError("");
      } catch (error) {
        if (!cancelled && isCurrentPlannerRun(runId, requestMode)) {
          setPlannerLoadError(error?.message || "planner-load-failed");
        }
      }
    }
    loadPlannerInput();
    return () => {
      cancelled = true;
      generateRunIdRef.current += 1;
    };
  }, [pageEnterSequence, plannerMode, workbenchApi]);

  useEffect(() => {
    const unsubscribe = workbenchApi.onCatalogChanged?.((event) => {
      const requestMode = String(event?.mode || "").trim().toLowerCase();
      if (!requestMode || requestMode !== plannerMode) {
        return;
      }

      const runId = generateRunIdRef.current + 1;
      generateRunIdRef.current = runId;
      setIsGenerating(false);
      setIsImportingDraft(false);

      async function reloadPlannerInput() {
        try {
          const nextPlannerInput = await workbenchApi.loadPlannerContext?.({ mode: requestMode });
          if (!isCurrentPlannerRun(runId, requestMode)) {
            return;
          }
          if (nextPlannerInput?.mode && nextPlannerInput.mode !== requestMode) {
            return;
          }
          setPlannerInput(nextPlannerInput || null);
          setPlannerResult(null);
          setImportedPlanId("");
          setPlannerInitialized(false);
          setPlannerLoadError("");
        } catch (error) {
          if (isCurrentPlannerRun(runId, requestMode)) {
            setPlannerLoadError(error?.message || "planner-load-failed");
          }
        }
      }

      reloadPlannerInput();
    });

    return () => {
      unsubscribe?.();
    };
  }, [plannerMode, workbenchApi]);

  useEffect(() => {
    setPlannerInitialized(false);
  }, [scopedPlannerInput?.generatedAtFrame, scopedPlannerInput?.version]);

  useEffect(() => {
    if (!scopedPlannerInput || plannerInitialized) {
      return;
    }

    const canonicalizeLineId = lineCollections.canonicalizeLineId || ((lineId) => lineId || "");
    const draft = pickPlannerDraft(scopedPlannerInput);
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
    const nextAdjustableOptions = buildRelatedLineOptionsForTarget(scopedPlannerInput, nextTargetScopeLineId, lineOptions)
      .filter((option) => nextExpressSource === "virtual" || option.value !== nextTargetScopeLineId);
    const nextAdjustableOptionIds = new Set(nextAdjustableOptions.map((option) => option.value));
    const nextAdjustableSeeds = [...new Set([...mergedLocal, ...mergedExpress])]
      .filter((lineId) => nextAdjustableOptionIds.has(lineId));
    const nextAdjustable = nextAdjustableSeeds.length > 0
      ? nextAdjustableSeeds
      : nextAdjustableOptions.map((option) => option.value);
    const nextStationOptions = buildStationOptionsForLine(scopedPlannerInput, nextVirtualBaseLine);

    setAdjustableLines(nextAdjustable);
    setExpressSource(nextExpressSource);
    setVirtualBaseLine(nextVirtualBaseLine);
    setExistingExpressLine(nextExistingExpressLine);
    setExpressStops(nextStationOptions.map((option) => option.value));
    setPlannerInitialized(true);
  }, [expressLineOptions, lineCollections.canonicalizeLineId, lineOptions, localLineOptions, plannerInitialized, scopedPlannerInput]);

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
    const requestMode = plannerMode;
    const loadingStartedAt = Date.now();
    setIsGenerating(true);
    setPlannerLoadError("");
    setPlannerResult(null);
    await waitForUiPaint();
    try {
      const request = buildPlannerRequest({
        mode: requestMode,
        plannerInput: scopedPlannerInput,
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

      const isPlannerLab = typeof window !== "undefined" && window.__RT_PLANNER_LAB__ === true;
      let result = null;
      if (isPlannerLab) {
        result = await workbenchApi.runPlanner?.(request);
        if (!isCurrentPlannerRun(runId, requestMode)) {
          return;
        }
        if (!result) {
          throw new Error("planner-run-failed");
        }
      } else {
        const startedJob = await workbenchApi.startPlannerJob?.(request);
        if (!isCurrentPlannerRun(runId, requestMode)) {
          return;
        }
        if (!startedJob?.jobId) {
          throw new Error(startedJob?.error || "planner-job-start-failed");
        }
        if (startedJob.mode && startedJob.mode !== requestMode) {
          return;
        }

        let latestStatus = startedJob;
        while (isCurrentPlannerRun(runId, requestMode) && !isTerminalPlannerJobState(latestStatus?.state)) {
          await waitForDelay(120);
          latestStatus = await workbenchApi.getPlannerJobStatus?.(startedJob.jobId);
          if (latestStatus?.state !== "missing" && latestStatus?.mode && latestStatus.mode !== requestMode) {
            return;
          }
        }

        if (!isCurrentPlannerRun(runId, requestMode)) {
          return;
        }

        if (!latestStatus || latestStatus.state === "missing") {
          throw new Error(latestStatus?.error || "planner-job-not-found");
        }

        if (latestStatus.state === "failed") {
          throw new Error(latestStatus.error || "planner-job-failed");
        }

        result = latestStatus.result || null;
      }

      const resultMode = result?.mode || result?.requestEcho?.mode || "";
      if (resultMode && resultMode !== requestMode) {
        return;
      }
      setPlannerResult(result || null);
      if (!result?.success) {
        const diagnosticMessage = (result?.diagnostics || [])
          .map((item) => formatDiagnosticMessage(item, t))
          .filter(Boolean)
          .join(" / ");
        setPlannerLoadError(diagnosticMessage);
      }
    } catch (error) {
      if (isCurrentPlannerRun(runId, requestMode)) {
        setPlannerResult(null);
        setPlannerLoadError(error?.message || "planner-run-failed");
      }
    } finally {
      if (isCurrentPlannerRun(runId, requestMode)) {
        await waitForMinimumDuration(loadingStartedAt, 300);
        setIsGenerating(false);
      }
    }
  }

  async function handleWritePlanToDraft() {
    if (importDisabled) {
      return;
    }

    const requestMode = scopedPlannerResult?.requestEcho?.mode || plannerMode;
    const runId = generateRunIdRef.current;
    setIsImportingDraft(true);
    setPlannerLoadError("");
    await waitForUiPaint();
    try {
      if (!isCurrentPlannerRun(runId, requestMode)) {
        return;
      }

      const snapshot = await (workbenchApi.refreshSnapshot?.({ mode: requestMode }) || workbenchApi.loadSnapshot?.({ mode: requestMode }));
      if (!isCurrentPlannerRun(runId, requestMode)) {
        return;
      }
      const snapshotMode = String(snapshot?.mode || "").trim().toLowerCase();
      if (snapshotMode && snapshotMode !== requestMode) {
        return;
      }
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
        scopedPlannerResult?.requestEcho
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
        mode: requestMode,
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
        planRefs: buildPlannerPlanRefs(scopedPlannerResult, activePlan, replacementRows)
      };

      const result = await workbenchApi.saveNativeDraft?.(request);
      if (!isCurrentPlannerRun(runId, requestMode)) {
        return;
      }
      const resultMode = String(result?.mode || result?.snapshot?.mode || "").trim().toLowerCase();
      if (resultMode && resultMode !== requestMode) {
        return;
      }
      if (!result?.success) {
        setPlannerLoadError(t("planner.import.error.save"));
        return;
      }

      setImportedPlanId(activePlan?.id || activePlan?.rawPlan?.planId || "imported");
    } catch {
      if (isCurrentPlannerRun(runId, requestMode)) {
        setPlannerLoadError(t("planner.import.error.save"));
      }
    } finally {
      if (isCurrentPlannerRun(runId, requestMode)) {
        setIsImportingDraft(false);
      }
    }
  }

  return {
    sidebar: {
      adjustableLineOptions, adjustableLines, analysisEnd, analysisStart, analysisTimeError, dispatchInterval,
      dispatchMode, dispatchOptions, dispatchPhaseStart, dispatchTripsPerHour, existingExpressLine,
      expressLineOptions, expressSource, expressSourceOptions, expressStops, forcedBypassOptions, forcedOvertakes,
      generateDisabled, isGenerating, leftTab, lineOptions, localLineOptions, maxLocalShift, maxLocalWait,
      maxOvertakes, overtakesOptions, phaseAdjustmentRange, plannerInput: scopedPlannerInput, plannerLoadError,
      readonlyConstraintLineOptions, stationOptions, virtualBaseLine
    },
    result: {
      activePlan, activePlanId, displayResult: liveDisplay, importDisabled, importedPlanId, importReferenceOnly,
      isGenerating, isImportingDraft, planLabels, plannerResult: scopedPlannerResult, plans, showGenericPlanError
    },
    preview: {
      changedWindows: activePlan?.changedWindows || [],
      diagnostics: activePlan?.diagnostics || [],
      risks: activePlan?.risks || [],
      timetableRows
    },
    refs: {
      dropdownPortalHostRef
    },
    actions: {
      generate: handleGenerate,
      setActivePlanId, setAnalysisEnd, setAnalysisEndInvalid, setAnalysisStart, setAnalysisStartInvalid,
      setDispatchInterval, setDispatchMode, setDispatchPhaseStart, setDispatchTripsPerHour, setExistingExpressLine,
      setExpressSource, setLeftTab, setMaxLocalShift, setMaxLocalWait, setMaxOvertakes, setPhaseAdjustmentRange,
      setVirtualBaseLine,
      toggleAdjustableLine: (lineId) => setAdjustableLines((current) => toggleArrayValue(current, lineId)),
      toggleExpressStop: (stationId) => setExpressStops((current) => toggleArrayValue(current, stationId)),
      toggleForcedOvertake: (stationId) => setForcedOvertakes((current) => toggleArrayValue(current, stationId)),
      writePlanToDraft: handleWritePlanToDraft
    }
  };
}
