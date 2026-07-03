import { useEffect, useMemo, useRef, useState } from "react";
import { getWorkbenchApi } from "../../shared/workbench-api";
import { runOverviewFeatureSettingsOperation } from "./overview-feature-settings-operation";
import { traceWorkbench } from "../../shared/workbench-trace";

function normalizeFeatureSettings(systems, sourceFeatureSettings = null) {
  if (sourceFeatureSettings && typeof sourceFeatureSettings === "object") {
    return {
      dispatchEnabled: sourceFeatureSettings.dispatchEnabled !== false,
      bypassEnabled: sourceFeatureSettings.bypassEnabled !== false,
      broadcastEnabled: sourceFeatureSettings.broadcastEnabled !== false,
      depotLockEnabled: sourceFeatureSettings.depotLockEnabled !== false
    };
  }

  const source = Array.isArray(systems) ? systems : [];
  return {
    dispatchEnabled: source.find((system) => system?.key === "dispatchEnabled")?.enabled !== false,
    bypassEnabled: source.find((system) => system?.key === "bypassEnabled")?.enabled !== false,
    broadcastEnabled: source.find((system) => system?.key === "broadcastEnabled")?.enabled !== false,
    depotLockEnabled: source.find((system) => system?.key === "depotLockEnabled")?.enabled !== false
  };
}

function applyFeatureSettings(systems, featureSettings) {
  const normalized = featureSettings || normalizeFeatureSettings(systems);
  return (Array.isArray(systems) ? systems : []).map((system) => ({
    ...system,
    enabled: normalized[system.key] !== false
  }));
}

export default function useOverviewFeatureSettings({ systems, featureSettings: sourceFeatureSettings, canEdit = true, onSaved }) {
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const [featureSettings, setFeatureSettings] = useState(() => normalizeFeatureSettings(systems, sourceFeatureSettings));
  const committedFeatureSettingsRef = useRef(normalizeFeatureSettings(systems, sourceFeatureSettings));
  const latestRunIdRef = useRef(0);

  useEffect(() => {
    const nextSettings = normalizeFeatureSettings(systems, sourceFeatureSettings);
    committedFeatureSettingsRef.current = nextSettings;
    setFeatureSettings(nextSettings);
  }, [sourceFeatureSettings, systems]);

  async function toggleFeature(featureKey) {
    if (!canEdit || !Object.prototype.hasOwnProperty.call(committedFeatureSettingsRef.current, featureKey)) {
      return;
    }

    const nextSettings = {
      ...featureSettings,
      [featureKey]: !featureSettings[featureKey]
    };
    const runId = latestRunIdRef.current + 1;
    latestRunIdRef.current = runId;
    setFeatureSettings(nextSettings);
    traceWorkbench("overview.feature.toggle", { featureKey, enabled: nextSettings[featureKey] });

    try {
      const operationResult = await runOverviewFeatureSettingsOperation(workbenchApi, {
        featureSettings: nextSettings
      }, {
        shouldContinue: () => latestRunIdRef.current === runId
      });

      if (latestRunIdRef.current !== runId || operationResult.interrupted || operationResult.superseded) {
        return;
      }

      const result = operationResult.result;
      if (!result?.success) {
        const message = Array.isArray(result?.errors) && result.errors.length > 0
          ? result.errors.join("; ")
          : "overview-feature-settings-save-failed";
        throw new Error(message);
      }

      committedFeatureSettingsRef.current = result.featureSettings || nextSettings;
      setFeatureSettings(committedFeatureSettingsRef.current);
      if (typeof onSaved === "function") {
        onSaved(committedFeatureSettingsRef.current);
      }
    } catch (error) {
      if (latestRunIdRef.current !== runId) {
        return;
      }

      setFeatureSettings(committedFeatureSettingsRef.current);
      traceWorkbench("overview.feature.toggle.error", {
        featureKey,
        message: error?.message || error
      });
    }
  }

  return {
    systems: applyFeatureSettings(systems, featureSettings),
    toggleFeature
  };
}
