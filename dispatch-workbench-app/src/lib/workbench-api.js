import { createEmptySnapshot } from "./workbench-defaults";

// Data source boundary.
// The real workbench only runs against the EUIS/backend snapshot path.
// Do not reintroduce browser mock fallback here.

const CALLS = {
  loadSnapshot: "suhua::rt.workbench.loadSnapshot",
  refreshSnapshot: "suhua::rt.workbench.refreshSnapshot",
  loadBroadcastSnapshot: "suhua::rt.workbench.loadBroadcastSnapshot",
  refreshBroadcastSnapshot: "suhua::rt.workbench.refreshBroadcastSnapshot",
  loadBroadcastBindingSlotHints: "suhua::rt.workbench.loadBroadcastBindingSlotHints",
  loadBroadcastAssetBrowser: "suhua::rt.workbench.loadBroadcastAssetBrowser",
  importBroadcastExternalAssets: "suhua::rt.workbench.importBroadcastExternalAssets",
  deleteBroadcastAsset: "suhua::rt.workbench.deleteBroadcastAsset",
  deleteAllBroadcastAssets: "suhua::rt.workbench.deleteAllBroadcastAssets",
  saveBroadcastStationBinding: "suhua::rt.workbench.saveBroadcastStationBinding",
  saveBroadcastStationBindings: "suhua::rt.workbench.saveBroadcastStationBindings",
  autoBindBroadcastStationMappings: "suhua::rt.workbench.autoBindBroadcastStationMappings",
  saveBroadcastRules: "suhua::rt.workbench.saveBroadcastRules",
  applyBroadcastConfig: "suhua::rt.workbench.applyBroadcastConfig",
  openBroadcastAssetDirectoryPicker: "suhua::rt.workbench.openBroadcastAssetDirectoryPicker",
  playBroadcastAssetPreview: "suhua::rt.workbench.playBroadcastAssetPreview",
  stopBroadcastAssetPreview: "suhua::rt.workbench.stopBroadcastAssetPreview",
  playBroadcastRulePreview: "suhua::rt.workbench.playBroadcastRulePreview",
  stopBroadcastRulePreview: "suhua::rt.workbench.stopBroadcastRulePreview",
  setBroadcastPreviewVolume: "suhua::rt.workbench.setBroadcastPreviewVolume",
  refreshMetadata: "suhua::rt.workbench.refreshMetadata",
  saveWorkbenchDraft: "suhua::rt.workbench.saveWorkbenchDraft",
  saveNativeWorkbenchDraft: "suhua::rt.workbench.saveNativeWorkbenchDraft",
  getLocale: "suhua::rt.workbench.getLocale"
};

const EVENTS = {
  snapshotChanged: "suhua::rt.workbench.onSnapshotChanged",
  broadcastSnapshotChanged: "suhua::rt.workbench.onBroadcastSnapshotChanged",
  broadcastAssetPreviewStateChanged: "suhua::rt.workbench.onBroadcastAssetPreviewStateChanged",
  broadcastRulePreviewStateChanged: "suhua::rt.workbench.onBroadcastRulePreviewStateChanged"
};

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
    assetDirectory: "",
    assets: [],
    version: "",
    sourceMode: "game-backend",
    draftApplied: false,
    draftDirty: false,
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
    snapshot: null
  };
}

function createLiveApi() {
  return {
    async loadSnapshot() {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadSnapshot);
      return parsePayload(payload, createEmptySnapshot());
    },
    async refreshSnapshot() {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.refreshSnapshot);
      return parsePayload(payload, createEmptySnapshot());
    },
    async loadBroadcastSnapshot(selectedLineId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadBroadcastSnapshot, selectedLineId || "");
      return parsePayload(payload, createEmptyBroadcastSnapshot());
    },
    async refreshBroadcastSnapshot(selectedLineId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.refreshBroadcastSnapshot, selectedLineId || "");
      return parsePayload(payload, createEmptyBroadcastSnapshot());
    },
    async loadBroadcastBindingSlotHints(lineId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadBroadcastBindingSlotHints, lineId || "");
      return parsePayload(payload, createEmptyBroadcastBindingSlotHints());
    },
    async loadBroadcastAssetBrowser(path = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.loadBroadcastAssetBrowser, path || "");
      return parsePayload(payload, createEmptyBroadcastAssetBrowser());
    },
    async importBroadcastExternalAssets(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.importBroadcastExternalAssets, JSON.stringify(request ?? {}));
      return parsePayload(payload, createBroadcastImportResult());
    },
    async deleteBroadcastAsset(assetName = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.deleteBroadcastAsset, assetName || "");
      return parsePayload(payload, createBroadcastDeleteAssetResult());
    },
    async deleteAllBroadcastAssets() {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.deleteAllBroadcastAssets);
      return parsePayload(payload, createBroadcastDeleteAllAssetsResult());
    },
    async saveBroadcastStationBinding(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveBroadcastStationBinding, JSON.stringify(request ?? {}));
      return parsePayload(payload, createBroadcastStationBindingSaveResult());
    },
    async saveBroadcastStationBindings(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveBroadcastStationBindings, JSON.stringify(request ?? {}));
      return parsePayload(payload, createBroadcastStationBindingSaveResult());
    },
    async autoBindBroadcastStationMappings(lineId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.autoBindBroadcastStationMappings, lineId || "");
      return parsePayload(payload, createBroadcastAutoBindStationMappingsResult());
    },
    async saveBroadcastRules(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveBroadcastRules, JSON.stringify(request ?? {}));
      return parsePayload(payload, createBroadcastRulesSaveResult());
    },
    async applyBroadcastConfig(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.applyBroadcastConfig, JSON.stringify(request ?? {}));
      return parsePayload(payload, createBroadcastApplyResult());
    },
    async openBroadcastAssetDirectoryPicker() {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.openBroadcastAssetDirectoryPicker);
      return parsePayload(payload, createBroadcastDirectoryPickerResult());
    },
    async playBroadcastAssetPreview(assetName = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.playBroadcastAssetPreview, assetName || "");
      return parsePayload(payload, createBroadcastAssetPreviewResult());
    },
    async stopBroadcastAssetPreview(assetName = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.stopBroadcastAssetPreview, assetName || "");
      return parsePayload(payload, createBroadcastAssetPreviewResult());
    },
    async playBroadcastRulePreview(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.playBroadcastRulePreview, JSON.stringify(request ?? {}));
      return parsePayload(payload, createBroadcastRulePreviewResult());
    },
    async stopBroadcastRulePreview(ruleId = "") {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.stopBroadcastRulePreview, ruleId || "");
      return parsePayload(payload, createBroadcastRulePreviewResult());
    },
    async setBroadcastPreviewVolume(volume = 80) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.setBroadcastPreviewVolume, String(volume ?? 80));
      return parsePayload(payload, createBroadcastVolumeResult());
    },
    async refreshMetadata() {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.refreshMetadata);
      return parsePayload(payload, createEmptySnapshot());
    },
    async saveDraft(request) {
      const engineCall = getEngineCall();
      const payload = await engineCall(CALLS.saveWorkbenchDraft, JSON.stringify(request ?? {}));
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
      const payload = await engineCall(CALLS.saveNativeWorkbenchDraft, JSON.stringify(request ?? {}));
      return parsePayload(payload, {
        success: false,
        errors: [],
        warnings: [],
        version: "",
        snapshot: createEmptySnapshot()
      });
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

