import { createEmptySnapshot } from "./workbench-defaults";

// Data source boundary.
// The real workbench only runs against the EUIS/backend snapshot path.
// Do not reintroduce browser mock fallback here.

const CALLS = {
  loadSnapshot: "suhua::rt.workbench.loadSnapshot",
  refreshSnapshot: "suhua::rt.workbench.refreshSnapshot",
  refreshMetadata: "suhua::rt.workbench.refreshMetadata",
  saveWorkbenchDraft: "suhua::rt.workbench.saveWorkbenchDraft",
  getLocale: "suhua::rt.workbench.getLocale"
};

const EVENTS = {
  snapshotChanged: "suhua::rt.workbench.onSnapshotChanged"
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

function createLiveApi() {
  return {
    async loadSnapshot() {
      const payload = await window.engine.call(CALLS.loadSnapshot);
      return parsePayload(payload, createEmptySnapshot());
    },
    async refreshSnapshot() {
      const payload = await window.engine.call(CALLS.refreshSnapshot);
      return parsePayload(payload, createEmptySnapshot());
    },
    async refreshMetadata() {
      const payload = await window.engine.call(CALLS.refreshMetadata);
      return parsePayload(payload, createEmptySnapshot());
    },
    async saveDraft(request) {
      const payload = await window.engine.call(CALLS.saveWorkbenchDraft, JSON.stringify(request ?? {}));
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
        const payload = await window.engine.call(CALLS.getLocale);
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
    }
  };
}

function isHostedBackend() {
  return (
    typeof window !== "undefined" &&
    typeof window.engine?.call === "function" &&
    window.location?.protocol === "coui:"
  );
}

// The shipped workbench only supports the backend path.
export function getWorkbenchApi() {
  if (isHostedBackend()) {
    return createLiveApi();
  }

  throw new Error("Workbench API is unavailable outside EUIS. Use the in-game EUIS workbench.");
}

