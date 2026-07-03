export const NATIVE_SCHEDULE_PERSIST_KEY = "rtm.nativeSchedule.frontendDraft.v1";
export const NATIVE_SCHEDULE_PERSIST_SCHEMA_VERSION = 3;

const DEFAULT_TRANSPORT_MODE = "train";

function normalizeTransportMode(mode) {
  const token = String(mode || "").trim().toLowerCase();
  return token || DEFAULT_TRANSPORT_MODE;
}

function getPersistKey(mode) {
  return `${NATIVE_SCHEDULE_PERSIST_KEY}.${normalizeTransportMode(mode)}`;
}

function readPersistedNativeScheduleStateFromKey(key) {
  if (typeof window === "undefined" || !window.localStorage) {
    return null;
  }

  try {
    const raw = window.localStorage.getItem(key);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object") {
      return null;
    }

    return Number(parsed.schemaVersion) === NATIVE_SCHEDULE_PERSIST_SCHEMA_VERSION
      ? parsed
      : null;
  } catch {
    return null;
  }
}

export function readPersistedNativeScheduleState(mode = DEFAULT_TRANSPORT_MODE) {
  const scopedState = readPersistedNativeScheduleStateFromKey(getPersistKey(mode));
  if (scopedState) {
    return scopedState;
  }

  return normalizeTransportMode(mode) === DEFAULT_TRANSPORT_MODE
    ? readPersistedNativeScheduleStateFromKey(NATIVE_SCHEDULE_PERSIST_KEY)
    : null;
}

export function writePersistedNativeScheduleState(nextState, mode = DEFAULT_TRANSPORT_MODE) {
  if (typeof window === "undefined" || !window.localStorage) {
    return;
  }

  try {
    window.localStorage.setItem(
      getPersistKey(mode),
      JSON.stringify({
        schemaVersion: NATIVE_SCHEDULE_PERSIST_SCHEMA_VERSION,
        mode: normalizeTransportMode(mode),
        ...(nextState && typeof nextState === "object" ? nextState : {})
      })
    );
  } catch {}
}
