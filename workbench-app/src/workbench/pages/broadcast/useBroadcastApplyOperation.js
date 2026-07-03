import { useRef, useState } from "react";

const APPLY_OPERATION_STATUS_DELAY_MS = 120;
const APPLY_OPERATION_TOTAL_TIMEOUT_MS = 15000;

function waitForDelay(delayMs) {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

function isTerminalBroadcastApplyState(state) {
  return state === "completed" || state === "failed" || state === "missing" || state === "superseded";
}

export default function useBroadcastApplyOperation(workbenchApi) {
  const [applyState, setApplyState] = useState({
    phase: "idle",
    error: "",
    operationId: "",
    result: null,
  });
  const generationRef = useRef(0);

  function normalizeMode(mode = "train") {
    return String(mode || "train").trim().toLowerCase() || "train";
  }

  function matchesMode(payload, mode) {
    return normalizeMode(payload?.mode) === normalizeMode(mode);
  }

  async function apply(request, mode = "train", isCurrentMode = null) {
    const generation = generationRef.current + 1;
    generationRef.current = generation;
    setApplyState({
      phase: "applying",
      error: "",
      operationId: "",
      result: null,
    });

    try {
      const deadline = Date.now() + APPLY_OPERATION_TOTAL_TIMEOUT_MS;
      const startedOperation = await workbenchApi.startBroadcastApplyOperation?.(request);
      if (!startedOperation?.operationId) {
        throw new Error(startedOperation?.error || "broadcast-apply-operation-start-failed");
      }
      if (typeof isCurrentMode === "function" && !isCurrentMode(mode)) {
        return { interrupted: true, result: null, latestStatus: startedOperation };
      }
      if (!matchesMode(startedOperation, mode)) {
        return { interrupted: true, result: null, latestStatus: startedOperation };
      }

      let latestStatus = startedOperation;
      while (generationRef.current === generation && !isTerminalBroadcastApplyState(latestStatus?.state)) {
        if (Date.now() > deadline) {
          throw new Error("broadcast-apply-operation-timeout");
        }

        await waitForDelay(APPLY_OPERATION_STATUS_DELAY_MS);
        latestStatus = await workbenchApi.getBroadcastApplyOperationStatus?.(startedOperation.operationId);
        if (typeof isCurrentMode === "function" && !isCurrentMode(mode)) {
          return { interrupted: true, result: null, latestStatus };
        }
        if (latestStatus && !matchesMode(latestStatus, mode)) {
          return { interrupted: true, result: null, latestStatus };
        }
      }

      if (generationRef.current !== generation) {
        return { interrupted: true, result: null, latestStatus };
      }

      if (latestStatus?.state === "superseded") {
        setApplyState({
          phase: "idle",
          error: "",
          operationId: startedOperation.operationId,
          result: null,
        });
        return { interrupted: false, superseded: true, result: null, latestStatus };
      }

      if (!latestStatus || latestStatus.state === "missing" || latestStatus.state === "failed") {
        throw new Error(latestStatus?.error || "broadcast-apply-operation-failed");
      }

      const result = latestStatus.result || null;
      if (!result?.success) {
        throw new Error(result?.error || "broadcast-apply-failed");
      }
      if (typeof isCurrentMode === "function" && !isCurrentMode(mode)) {
        return { interrupted: true, result, latestStatus };
      }

      setApplyState({
        phase: "applied",
        error: "",
        operationId: startedOperation.operationId,
        result,
      });
      return { interrupted: false, superseded: false, result, latestStatus };
    } catch (error) {
      const message = error instanceof Error ? error.message : "broadcast-apply-failed";
      if (generationRef.current === generation
        && (typeof isCurrentMode !== "function" || isCurrentMode(mode))) {
        setApplyState({
          phase: "error",
          error: message,
          operationId: "",
          result: null,
        });
      }
      throw error;
    }
  }

  function resetApplyState() {
    if (applyState.phase === "applied" || applyState.phase === "error") {
      setApplyState({
        phase: "idle",
        error: "",
        operationId: "",
        result: null,
      });
    }
  }

  return {
    applyState,
    apply,
    resetApplyState,
  };
}
