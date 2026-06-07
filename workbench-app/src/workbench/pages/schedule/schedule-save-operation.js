export const SAVE_OPERATION_START_TIMEOUT_MS = 5000;
export const SAVE_OPERATION_STATUS_TIMEOUT_MS = 5000;
export const SAVE_OPERATION_TOTAL_TIMEOUT_MS = 15000;

export function waitForDelay(delayMs) {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

export function withTimeout(promise, timeoutMs, message) {
  return Promise.race([
    promise,
    new Promise((_, reject) => {
      window.setTimeout(() => reject(new Error(message)), timeoutMs);
    })
  ]);
}

export function isTerminalSaveOperationState(state) {
  return state === "completed" || state === "failed" || state === "missing" || state === "superseded";
}

function normalizeOperationMode(mode) {
  return String(mode || "").trim().toLowerCase();
}

function statusMatchesExpectedMode(status, expectedMode) {
  const expected = normalizeOperationMode(expectedMode);
  if (!expected) {
    return true;
  }

  const statusMode = normalizeOperationMode(status?.mode || status?.result?.mode);
  return !statusMode || statusMode === expected;
}

export async function runNativeSaveOperation(workbenchApi, request, { applyDraft = false, expectedMode = "", shouldContinue = () => true } = {}) {
  const operationDeadline = Date.now() + SAVE_OPERATION_TOTAL_TIMEOUT_MS;
  const startedOperation = await withTimeout(
    workbenchApi.startNativeSaveOperation?.(request),
    SAVE_OPERATION_START_TIMEOUT_MS,
    "save-operation-start-timeout"
  );
  if (!startedOperation?.operationId) {
    throw new Error(startedOperation?.error || "save-operation-start-failed");
  }
  if (!statusMatchesExpectedMode(startedOperation, expectedMode)) {
    return { interrupted: true, superseded: false, result: null, latestStatus: startedOperation };
  }

  let latestStatus = startedOperation;
  while (shouldContinue() && !isTerminalSaveOperationState(latestStatus?.state)) {
    if (Date.now() > operationDeadline) {
      throw new Error("save-operation-timeout");
    }

    await waitForDelay(applyDraft ? 50 : 120);
    latestStatus = await withTimeout(
      workbenchApi.getNativeSaveOperationStatus?.(startedOperation.operationId),
      SAVE_OPERATION_STATUS_TIMEOUT_MS,
      "save-operation-status-timeout"
    );
    if (!statusMatchesExpectedMode(latestStatus, expectedMode)) {
      return { interrupted: true, superseded: false, result: null, latestStatus };
    }
  }

  if (!shouldContinue()) {
    return { interrupted: true, superseded: false, result: null, latestStatus };
  }

  if (latestStatus?.state === "superseded") {
    return { interrupted: false, superseded: true, result: null, latestStatus };
  }

  if (!latestStatus || latestStatus.state === "missing" || latestStatus.state === "failed") {
    throw new Error(latestStatus?.error || "save-operation-failed");
  }

  return {
    interrupted: false,
    superseded: false,
    result: latestStatus.result || { success: false, errors: [], warnings: [], version: "", snapshot: null },
    latestStatus
  };
}
