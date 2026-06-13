const OPERATION_START_TIMEOUT_MS = 5000;
const OPERATION_STATUS_TIMEOUT_MS = 5000;
const OPERATION_TOTAL_TIMEOUT_MS = 15000;

function waitForDelay(delayMs) {
  return new Promise((resolve) => window.setTimeout(resolve, delayMs));
}

function withTimeout(promise, timeoutMs, message) {
  return Promise.race([
    promise,
    new Promise((_, reject) => {
      window.setTimeout(() => reject(new Error(message)), timeoutMs);
    })
  ]);
}

function isTerminalOperationState(state) {
  return state === "completed" || state === "failed" || state === "missing" || state === "superseded";
}

export async function runOverviewFeatureSettingsOperation(
  workbenchApi,
  request,
  { shouldContinue = () => true } = {}
) {
  const operationDeadline = Date.now() + OPERATION_TOTAL_TIMEOUT_MS;
  const startedOperation = await withTimeout(
    workbenchApi.startOverviewFeatureSettingsOperation?.(request),
    OPERATION_START_TIMEOUT_MS,
    "overview-feature-settings-start-timeout"
  );

  if (!startedOperation?.operationId) {
    throw new Error(startedOperation?.error || "overview-feature-settings-start-failed");
  }

  let latestStatus = startedOperation;
  while (shouldContinue() && !isTerminalOperationState(latestStatus?.state)) {
    if (Date.now() > operationDeadline) {
      throw new Error("overview-feature-settings-timeout");
    }

    await waitForDelay(80);
    latestStatus = await withTimeout(
      workbenchApi.getOverviewFeatureSettingsOperationStatus?.(startedOperation.operationId),
      OPERATION_STATUS_TIMEOUT_MS,
      "overview-feature-settings-status-timeout"
    );
  }

  if (!shouldContinue()) {
    return { interrupted: true, superseded: false, result: null, latestStatus };
  }

  if (latestStatus?.state === "superseded") {
    return { interrupted: false, superseded: true, result: null, latestStatus };
  }

  if (!latestStatus || latestStatus.state === "missing" || latestStatus.state === "failed") {
    throw new Error(latestStatus?.error || "overview-feature-settings-failed");
  }

  return {
    interrupted: false,
    superseded: false,
    result: latestStatus.result || { success: false, errors: [], version: "", featureSettings: null },
    latestStatus
  };
}
