import { createEmptySnapshot } from "./workbench-defaults";

// Data source boundary.
// The real workbench only runs against the EUIS/backend snapshot path.
// Do not reintroduce browser mock fallback here.

const CALLS = {
  loadSnapshot: "suhua::rt.workbench.loadSnapshot",
  refreshSnapshot: "suhua::rt.workbench.refreshSnapshot",
  loadBroadcastSnapshot: "suhua::rt.workbench.loadBroadcastSnapshot",
  refreshBroadcastSnapshot: "suhua::rt.workbench.refreshBroadcastSnapshot",
  loadPassengerFlowSnapshot: "suhua::rt.workbench.loadPassengerFlowSnapshot",
  loadBroadcastBindingSlotHints: "suhua::rt.workbench.loadBroadcastBindingSlotHints",
  loadBroadcastAssetBrowser: "suhua::rt.workbench.loadBroadcastAssetBrowser",
  importBroadcastExternalAssets: "suhua::rt.workbench.importBroadcastExternalAssets",
  deleteBroadcastAsset: "suhua::rt.workbench.deleteBroadcastAsset",
  deleteAllBroadcastAssets: "suhua::rt.workbench.deleteAllBroadcastAssets",
  saveBroadcastStationBinding: "suhua::rt.workbench.saveBroadcastStationBinding",
  saveBroadcastStationBindings: "suhua::rt.workbench.saveBroadcastStationBindings",
  autoBindBroadcastStationMappings: "suhua::rt.workbench.autoBindBroadcastStationMappings",
  saveBroadcastRules: "suhua::rt.workbench.saveBroadcastRules",
  saveBroadcastPlatformAnnouncement: "suhua::rt.workbench.saveBroadcastPlatformAnnouncement",
  copyBroadcastPlatformAnnouncementToAllStations: "suhua::rt.workbench.copyBroadcastPlatformAnnouncementToAllStations",
  applyBroadcastConfig: "suhua::rt.workbench.applyBroadcastConfig",
  openBroadcastAssetDirectoryPicker: "suhua::rt.workbench.openBroadcastAssetDirectoryPicker",
  playBroadcastAssetPreview: "suhua::rt.workbench.playBroadcastAssetPreview",
  stopBroadcastAssetPreview: "suhua::rt.workbench.stopBroadcastAssetPreview",
  playBroadcastRulePreview: "suhua::rt.workbench.playBroadcastRulePreview",
  stopBroadcastRulePreview: "suhua::rt.workbench.stopBroadcastRulePreview",
  setBroadcastPreviewVolume: "suhua::rt.workbench.setBroadcastPreviewVolume",
  startBroadcastApplyOperation: "suhua::rt.workbench.startBroadcastApplyOperation",
  getBroadcastApplyOperationStatus: "suhua::rt.workbench.getBroadcastApplyOperationStatus",
  refreshMetadata: "suhua::rt.workbench.refreshMetadata",
  refreshTransitCatalog: "suhua::rt.workbench.refreshTransitCatalog",
  loadPlannerContext: "suhua::rt.workbench.loadPlannerContext",
  exportPlannerInput: "suhua::rt.workbench.exportPlannerInput",
  startPlannerJob: "suhua::rt.workbench.startPlannerJob",
  getPlannerJobStatus: "suhua::rt.workbench.getPlannerJobStatus",
  runPlanner: "suhua::rt.workbench.runPlanner",
  saveWorkbenchDraft: "suhua::rt.workbench.saveWorkbenchDraft",
  saveNativeWorkbenchDraft: "suhua::rt.workbench.saveNativeWorkbenchDraft",
  startNativeSaveOperation: "suhua::rt.workbench.startNativeSaveOperation",
  getNativeSaveOperationStatus: "suhua::rt.workbench.getNativeSaveOperationStatus",
  startOverviewFeatureSettingsOperation: "suhua::rt.workbench.startOverviewFeatureSettingsOperation",
  getOverviewFeatureSettingsOperationStatus: "suhua::rt.workbench.getOverviewFeatureSettingsOperationStatus",
  setWorkbenchHostState: "suhua::rt.workbench.setWorkbenchHostState",
  getLocale: "suhua::rt.workbench.getLocale"
};

const EVENTS = {
  snapshotChanged: "suhua::rt.workbench.onSnapshotChanged",
  catalog: "suhua::rt.workbench.onCatalog",
  lineInvalidated: "suhua::rt.workbench.onLineInvalidated",
  broadcastSnapshotChanged: "suhua::rt.workbench.onBroadcastSnapshotChanged",
  broadcastAssetPreviewStateChanged: "suhua::rt.workbench.onBroadcastAssetPreviewStateChanged",
  broadcastRulePreviewStateChanged: "suhua::rt.workbench.onBroadcastRulePreviewStateChanged"
};

const DEFAULT_TRANSPORT_MODE = "train";

function normalizeTransportMode(mode) {
  const token = String(mode || "").trim().toLowerCase();
  return token || DEFAULT_TRANSPORT_MODE;
}

export function setWorkbenchApiTransportMode(mode) {
  if (typeof window !== "undefined") {
    window.__RT_WORKBENCH_ACTIVE_TRANSPORT_MODE__ = normalizeTransportMode(mode);
  }
}

function getWorkbenchApiTransportMode() {
  if (typeof window === "undefined") {
    return DEFAULT_TRANSPORT_MODE;
  }

  return normalizeTransportMode(window.__RT_WORKBENCH_ACTIVE_TRANSPORT_MODE__);
}

function withMode(request = {}) {
  if (request && typeof request === "object" && !Array.isArray(request)) {
    return {
      ...request,
      mode: normalizeTransportMode(request.mode || getWorkbenchApiTransportMode())
    };
  }

  return {
    mode: getWorkbenchApiTransportMode()
  };
}

function requestJson(request = {}) {
  return JSON.stringify(withMode(request));
}

function plainRequestJson(request = {}) {
  if (request && typeof request === "object" && !Array.isArray(request)) {
    return JSON.stringify(request);
  }

  return JSON.stringify({});
}

function parsePayload(payload, fallbackValue) {
  if (!payload) {
    return fallbackValue;
  }

  if (typeof payload === "string") {
    try {
      return JSON.parse(payload);
    } catch (error) {
      return fallbackValue;
    }
  }

  return payload;
}

function getEngineCall() {
  if (typeof window === "undefined" || typeof window.engine?.call !== "function") {
    throw new Error("window.engine.call is unavailable in the current host UI context.");
  }

  return window.engine.call.bind(window.engine);
}

function createEmptyBroadcastSnapshot() {
  return {
    selectedLineId: "",
    lines: [],
    stations: [],
    stationBindings: [],
    rules: [],
    platformAnnouncements: [],
    assetDirectory: "",
    assets: [],
    version: "",
    sourceMode: "game-backend",
    lineApplied: false,
    lineDraftDirty: false,
    volumeDirty: false,
    draftApplied: false,
    draftDirty: false,
    warnings: [],
    volume: 80
  };
}

function createBroadcastDirectoryPickerResult() {
  return {
    success: false,
    pending: false,
    error: ""
  };
}

function createEmptyBroadcastAssetBrowser() {
  return {
    rootPath: "",
    currentPath: "",
    parentPath: "",
    folders: [],
    files: [],
    allowedExtensions: [],
    error: ""
  };
}

function createEmptyBroadcastBindingSlotHints() {
  return {
    success: false,
    error: "",
    slotHints: []
  };
}

function createEmptyPlannerInput() {
  return {
    lines: [],
    stations: [],
    segments: [],
    configuredBypassStations: [],
    candidateBypassStations: [],
    currentTrackScenario: {
      lines: [],
      sharedCorridors: []
    },
    observations: {
      stopDwell: [],
      traversalSlices: []
    },
    runtimeParams: {},
    drafts: []
  };
}

function createEmptyPassengerFlowSnapshot(mode = getWorkbenchApiTransportMode()) {
  return {
    schemaVersion: 1,
    mode: normalizeTransportMode(mode),
    generatedAtFrame: 0,
    bucketMinutes: 15,
    stationVolumes: [],
    sectionVolumes: [],
    odFlows: [],
    stationCatalog: [],
    warnings: []
  };
}

function createEmptyPlannerResult() {
  return {
    success: false,
    engineVersion: "",
    requestEcho: null,
    inputSummary: null,
    lineRoleSummary: null,
    defaultPlanId: "",
    plans: [],
    planSummaries: [],
    selectedPlan: null,
    diagnostics: [],
    performance: null
  };
}

function createEmptyPlannerJobStatus() {
  return {
    success: false,
    jobId: "",
    state: "missing",
    error: "",
    result: null
  };
}

function createEmptySaveOperationStatus() {
  return {
    success: false,
    operationId: "",
    state: "missing",
    error: "",
    result: null
  };
}

function createEmptyOverviewFeatureSettingsOperationStatus() {
  return {
    success: false,
    operationId: "",
    state: "missing",
    error: "",
    result: {
      success: false,
      errors: [],
      version: "",
      featureSettings: null
    }
  };
}

function createBroadcastImportResult() {
  return {
    success: false,
    importedCount: 0,
    error: ""
  };
}

function createBroadcastDeleteAssetResult() {
  return {
    success: false,
    error: ""
  };
}

function createBroadcastDeleteAllAssetsResult() {
  return {
    success: false,
    error: ""
  };
}

function createBroadcastStationBindingSaveResult() {
  return {
    success: false,
    error: ""
  };
}

function createBroadcastAutoBindStationMappingsResult() {
  return {
    success: false,
    boundCount: 0,
    error: ""
  };
}

function createBroadcastRulesSaveResult() {
  return {
    success: false,
    error: ""
  };
}

function createBroadcastPlatformAnnouncementSaveResult() {
  return {
    success: false,
    error: "",
    snapshot: null
  };
}

function createBroadcastApplyResult() {
  return {
    success: false,
    error: "",
    snapshot: null
  };
}

function createBroadcastAssetPreviewResult() {
  return {
    success: false,
    state: "",
    error: "",
    assetName: ""
  };
}

function createBroadcastRulePreviewResult() {
  return {
    success: false,
    state: "",
    error: "",
    ruleId: ""
  };
}

function createBroadcastVolumeResult() {
  return {
    success: false,
    error: "",
    volume: 80,
    volumeDirty: false,
    snapshot: null
  };
}

function createBroadcastApplyOperationStatus() {
  return {
    success: false,
    operationId: "",
    state: "missing",
    error: "",
    result: {
      success: false,
      error: "",
      version: "",
      appliedLineIds: [],
      volumeApplied: false,
      warnings: []
    }
  };
}

function createLiveApi() {
  return {
    async loadSnapshot(request = {}) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadSnapshot, requestJson(request));
      return parsePayload(payload, createEmptySnapshot());
    },
    async refreshSnapshot(request = {}) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.refreshSnapshot, requestJson(request));
      return parsePayload(payload, createEmptySnapshot());
    },
    async loadBroadcastSnapshot(selectedLineId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadBroadcastSnapshot, requestJson({ preferredLineId: selectedLineId || "" }));
      return parsePayload(payload, createEmptyBroadcastSnapshot());
    },
    async refreshBroadcastSnapshot(selectedLineId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.refreshBroadcastSnapshot, requestJson({ preferredLineId: selectedLineId || "" }));
      return parsePayload(payload, createEmptyBroadcastSnapshot());
    },
    async loadPassengerFlowSnapshot(request = {}) {
      const engineCall = getEngineCall();
      const scopedRequest = withMode(request);
      const payload = await engineCall(CALLS.loadPassengerFlowSnapshot, requestJson(scopedRequest));
      return parsePayload(payload, createEmptyPassengerFlowSnapshot(scopedRequest.mode));
    },
    async loadBroadcastBindingSlotHints(lineId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadBroadcastBindingSlotHints, requestJson({ lineId: lineId || "" }));
      return parsePayload(payload, createEmptyBroadcastBindingSlotHints());
    },
    async loadBroadcastAssetBrowser(path = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadBroadcastAssetBrowser, requestJson({ path: path || "" }));
      return parsePayload(payload, createEmptyBroadcastAssetBrowser());
    },
    async importBroadcastExternalAssets(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.importBroadcastExternalAssets, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastImportResult());
    },
    async deleteBroadcastAsset(requestOrAssetName = "") {
      const engineCall = getEngineCall();
      const request =
        requestOrAssetName && typeof requestOrAssetName === "object" && !Array.isArray(requestOrAssetName)
          ? requestOrAssetName
          : { assetName: requestOrAssetName || "" };
      const payload = await engineCall(CALLS.deleteBroadcastAsset, requestJson(request));
      return parsePayload(payload, createBroadcastDeleteAssetResult());
    },
    async deleteAllBroadcastAssets(request = {}) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.deleteAllBroadcastAssets, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastDeleteAllAssetsResult());
    },
    async saveBroadcastStationBinding(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveBroadcastStationBinding, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastStationBindingSaveResult());
    },
    async saveBroadcastStationBindings(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveBroadcastStationBindings, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastStationBindingSaveResult());
    },
    async autoBindBroadcastStationMappings(lineId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.autoBindBroadcastStationMappings, requestJson({ lineId: lineId || "" }));
      return parsePayload(payload, createBroadcastAutoBindStationMappingsResult());
    },
    async saveBroadcastRules(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveBroadcastRules, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastRulesSaveResult());
    },
    async saveBroadcastPlatformAnnouncement(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveBroadcastPlatformAnnouncement, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastPlatformAnnouncementSaveResult());
    },
    async copyBroadcastPlatformAnnouncementToAllStations(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.copyBroadcastPlatformAnnouncementToAllStations, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastPlatformAnnouncementSaveResult());
    },
    async applyBroadcastConfig(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.applyBroadcastConfig, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastApplyResult());
    },
    async openBroadcastAssetDirectoryPicker() {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.openBroadcastAssetDirectoryPicker, requestJson());
      return parsePayload(payload, createBroadcastDirectoryPickerResult());
    },
    async playBroadcastAssetPreview(assetName = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.playBroadcastAssetPreview, requestJson({ assetName: assetName || "" }));
      return parsePayload(payload, createBroadcastAssetPreviewResult());
    },
    async stopBroadcastAssetPreview(requestOrAssetName = "") {
      const engineCall = getEngineCall();
      const request =
        requestOrAssetName && typeof requestOrAssetName === "object" && !Array.isArray(requestOrAssetName)
          ? requestOrAssetName
          : { assetName: requestOrAssetName || "" };
      const payload = await engineCall(CALLS.stopBroadcastAssetPreview, requestJson(request));
      return parsePayload(payload, createBroadcastAssetPreviewResult());
    },
    async playBroadcastRulePreview(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.playBroadcastRulePreview, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastRulePreviewResult());
    },
    async stopBroadcastRulePreview(requestOrRuleId = "") {
      const engineCall = getEngineCall();
      const request =
        requestOrRuleId && typeof requestOrRuleId === "object" && !Array.isArray(requestOrRuleId)
          ? requestOrRuleId
          : { ruleId: requestOrRuleId || "" };
      const payload = await engineCall(CALLS.stopBroadcastRulePreview, requestJson(request));
      return parsePayload(payload, createBroadcastRulePreviewResult());
    },
    async setBroadcastPreviewVolume(volume = 80) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.setBroadcastPreviewVolume, requestJson({ volume: volume ?? 80 }));
      return parsePayload(payload, createBroadcastVolumeResult());
    },
    async startBroadcastApplyOperation(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.startBroadcastApplyOperation, requestJson(request ?? {}));
      return parsePayload(payload, createBroadcastApplyOperationStatus());
    },
    async getBroadcastApplyOperationStatus(operationId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.getBroadcastApplyOperationStatus, operationId || "");
      return parsePayload(payload, createBroadcastApplyOperationStatus());
    },
    async refreshMetadata(request = {}) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.refreshMetadata, requestJson(request));
      return parsePayload(payload, createEmptySnapshot());
    },
    async refreshTransitCatalog(request = {}) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.refreshTransitCatalog, requestJson(request));
      return parsePayload(payload, createEmptySnapshot());
    },
    async loadPlannerContext(request = {}) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadPlannerContext, requestJson(request));
      return parsePayload(payload, createEmptyPlannerInput());
    },
    async exportPlannerInput(request = {}) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.exportPlannerInput, requestJson(request));
      return parsePayload(payload, createEmptyPlannerInput());
    },
    async startPlannerJob(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.startPlannerJob, requestJson(request ?? {}));
      return parsePayload(payload, createEmptyPlannerJobStatus());
    },
    async getPlannerJobStatus(jobId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.getPlannerJobStatus, jobId || "");
      return parsePayload(payload, createEmptyPlannerJobStatus());
    },
    async runPlanner(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.runPlanner, requestJson(request ?? {}));
      return parsePayload(payload, createEmptyPlannerResult());
    },
    async saveDraft(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveWorkbenchDraft, requestJson(request ?? {}));
      return parsePayload(payload, {
        success: false,
        errors: [],
        warnings: [],
        version: "",
        snapshot: createEmptySnapshot()
      });
    },
    async saveNativeDraft(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveNativeWorkbenchDraft, requestJson(request ?? {}));
      return parsePayload(payload, {
        success: false,
        errors: [],
        warnings: [],
        version: "",
        snapshot: createEmptySnapshot()
      });
    },
    async startNativeSaveOperation(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.startNativeSaveOperation, requestJson(request ?? {}));
      return parsePayload(payload, createEmptySaveOperationStatus());
    },
    async getNativeSaveOperationStatus(operationId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.getNativeSaveOperationStatus, operationId || "");
      return parsePayload(payload, createEmptySaveOperationStatus());
    },
    async startOverviewFeatureSettingsOperation(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.startOverviewFeatureSettingsOperation, plainRequestJson(request ?? {}));
      return parsePayload(payload, createEmptyOverviewFeatureSettingsOperationStatus());
    },
    async getOverviewFeatureSettingsOperationStatus(operationId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.getOverviewFeatureSettingsOperationStatus, operationId || "");
      return parsePayload(payload, createEmptyOverviewFeatureSettingsOperationStatus());
    },
    async getLocale() {
      try {
        const engineCall = getEngineCall();
        const payload = await engineCall(CALLS.getLocale);
        return typeof payload === "string" ? payload : "";
      } catch {
        return "";
      }
    },
    async setHostState(request = {}) {
      try {
        const engineCall = getEngineCall();
        await engineCall(CALLS.setWorkbenchHostState, requestJson(request ?? {}));
      } catch {}
    },
    onSnapshotChanged(callback) {
      if (typeof window.engine.on !== "function") {
        return () => {};
      }

      const handler = (payload) => {
        const snapshot = parsePayload(payload, null);
        if (snapshot) {
          callback(snapshot);
        }
      };

      window.engine.on(EVENTS.snapshotChanged, handler);
      return () => {
        if (typeof window.engine.off === "function") {
          window.engine.off(EVENTS.snapshotChanged, handler);
        }
      };
    },
    onCatalogChanged(callback) {
      if (typeof window.engine.on !== "function") {
        return () => {};
      }

      const handler = (payload) => {
        const event = parsePayload(payload, null);
        if (event) {
          callback(event);
        }
      };

      window.engine.on(EVENTS.catalog, handler);
      return () => {
        if (typeof window.engine.off === "function") {
          window.engine.off(EVENTS.catalog, handler);
        }
      };
    },
    onLineInvalidated(callback) {
      if (typeof window.engine.on !== "function") {
        return () => {};
      }

      const handler = (payload) => {
        const event = parsePayload(payload, null);
        if (event) {
          callback(event);
        }
      };

      window.engine.on(EVENTS.lineInvalidated, handler);
      return () => {
        if (typeof window.engine.off === "function") {
          window.engine.off(EVENTS.lineInvalidated, handler);
        }
      };
    },
    onBroadcastSnapshotChanged(callback) {
      if (typeof window.engine.on !== "function") {
        return () => {};
      }

      const handler = (payload) => {
        const snapshot = parsePayload(payload, null);
        if (snapshot) {
          callback(snapshot);
        }
      };

      window.engine.on(EVENTS.broadcastSnapshotChanged, handler);
      return () => {
        if (typeof window.engine.off === "function") {
          window.engine.off(EVENTS.broadcastSnapshotChanged, handler);
        }
      };
    },
    onBroadcastAssetPreviewStateChanged(callback) {
      if (typeof window.engine.on !== "function") {
        return () => {};
      }

      const handler = (payload) => {
        const state = parsePayload(payload, null);
        if (state) {
          callback(state);
        }
      };

      window.engine.on(EVENTS.broadcastAssetPreviewStateChanged, handler);
      return () => {
        if (typeof window.engine.off === "function") {
          window.engine.off(EVENTS.broadcastAssetPreviewStateChanged, handler);
        }
      };
    },
    onBroadcastRulePreviewStateChanged(callback) {
      if (typeof window.engine.on !== "function") {
        return () => {};
      }

      const handler = (payload) => {
        const state = parsePayload(payload, null);
        if (state) {
          callback(state);
        }
      };

      window.engine.on(EVENTS.broadcastRulePreviewStateChanged, handler);
      return () => {
        if (typeof window.engine.off === "function") {
          window.engine.off(EVENTS.broadcastRulePreviewStateChanged, handler);
        }
      };
    }
  };
}

export function getWorkbenchApi() {
  return createLiveApi();
}

