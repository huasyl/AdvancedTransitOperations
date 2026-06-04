export const NATIVE_SCHEDULE_PERSIST_KEY = "rtm.nativeSchedule.frontendDraft.v1";
export const NATIVE_SCHEDULE_PERSIST_SCHEMA_VERSION = 3;

export function readPersistedNativeScheduleState() {
  if (typeof window === "undefined" || !window.localStorage) {
    return null;
  }

  try {
    const raw = window.localStorage.getItem(NATIVE_SCHEDULE_PERSIST_KEY);
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

export function writePersistedNativeScheduleState(nextState) {
  if (typeof window === "undefined" || !window.localStorage) {
    return;
  }

  try {
    window.localStorage.setItem(
      NATIVE_SCHEDULE_PERSIST_KEY,
      JSON.stringify({
        schemaVersion: NATIVE_SCHEDULE_PERSIST_SCHEMA_VERSION,
        ...(nextState && typeof nextState === "object" ? nextState : {})
      })
    );
  } catch {}
}
