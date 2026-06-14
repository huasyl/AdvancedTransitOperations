import { useValue } from "cs2/api";
import React from "react";
import { activeLocale$ } from "../selection/selectionBindings";

export function RapidTransitNativeLocaleSync() {
  const activeLocale = useValue<string>(activeLocale$) || "";

  React.useEffect(() => {
    const nextLocale = typeof activeLocale === "string" ? activeLocale : "";
    window.__RT_NATIVE_SCHEDULE_LOCALE__ = nextLocale;
    window.dispatchEvent(new CustomEvent("rt-native-schedule-localechange", {
      detail: {
        locale: nextLocale
      }
    }));
  }, [activeLocale]);

  return null;
}
