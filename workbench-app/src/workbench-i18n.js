import { createContext, createElement, useContext, useEffect, useMemo, useState } from "react";
import { getWorkbenchApi } from "./lib/workbench-api";
import { nativeScheduleTranslations } from "./workbench-translations";

const DEFAULT_LOCALE = "en-US";
const STORAGE_KEY = "rtm.nativeSchedule.locale";
const HOST_LOCALE_EVENT = "rt-native-schedule-localechange";

function isHostedUi() {
  return typeof window !== "undefined" && window.location?.protocol === "coui:";
}

function normalizeLocale(value) {
  const locale = String(value || "").trim();
  if (!locale) {
    return DEFAULT_LOCALE;
  }

  if (locale === "zh-CN" || locale === "ja-JP" || locale === "en-US") {
    return locale;
  }

  const normalized = locale.replace("_", "-").toLowerCase();
  if (normalized.startsWith("zh")) {
    return "zh-CN";
  }

  if (normalized.startsWith("ja")) {
    return "ja-JP";
  }

  return "en-US";
}

function resolveLocaleCode(localization) {
  const candidates = [];
  const pushValue = (value) => {
    if (typeof value === "string" && value.length > 0) {
      candidates.push(value.toLowerCase());
    }
  };
  const pushObject = (value) => {
    if (!value || typeof value !== "object") {
      return;
    }

    pushValue(value.locale);
    pushValue(value.localeCode);
    pushValue(value.language);
    pushValue(value.activeLocale);
    pushValue(value.activeLocaleCode);
    pushValue(value.activeLanguage);
    pushValue(value.id);
    pushValue(value.code);
    pushValue(value.name);
  };

  const l10n = typeof window !== "undefined" ? window["cs2/l10n"] : null;
  pushObject(localization);
  pushObject(l10n);
  pushObject(localization && localization.locale);
  pushObject(localization && localization.activeLocale);
  pushObject(l10n && l10n.locale);
  pushObject(l10n && l10n.activeLocale);
  pushValue(typeof document !== "undefined" && document.documentElement && document.documentElement.lang);
  pushValue(typeof document !== "undefined" && document.body && document.body.lang);
  pushValue(typeof Intl !== "undefined" && Intl.DateTimeFormat && Intl.DateTimeFormat().resolvedOptions().locale);
  pushValue(typeof navigator !== "undefined" && (navigator.language || navigator.userLanguage));
  if (typeof navigator !== "undefined" && Array.isArray(navigator.languages)) {
    for (let index = 0; index < navigator.languages.length; index += 1) {
      pushValue(navigator.languages[index]);
    }
  }

  for (let index = 0; index < candidates.length; index += 1) {
    const candidate = candidates[index];
    if (
      candidate.indexOf("zh") >= 0 ||
      candidate.indexOf("hans") >= 0 ||
      candidate.indexOf("hant") >= 0 ||
      candidate.indexOf("chinese") >= 0 ||
      candidate.indexOf("cn") >= 0 ||
      candidate.indexOf("chs") >= 0
    ) {
      return candidate;
    }
  }

  return candidates[0] || "";
}

function detectLocale() {
  if (typeof window !== "undefined") {
    const globalLocale = window.__RT_NATIVE_SCHEDULE_LOCALE__;
    if (typeof globalLocale === "string" && globalLocale.length > 0) {
      return normalizeLocale(globalLocale);
    }

    const query = String(window.location?.search || "");
    const match = /(?:^\?|&)(?:rt_native_schedule_locale|rt_locale)=([^&]+)/.exec(query);
    if (match?.[1]) {
      try {
        return normalizeLocale(decodeURIComponent(match[1].replace(/\+/g, " ")));
      } catch {
        return normalizeLocale(match[1]);
      }
    }

    if (!isHostedUi()) {
      try {
        const storedLocale = window.localStorage.getItem(STORAGE_KEY);
        if (storedLocale) {
          return normalizeLocale(storedLocale);
        }
      } catch {}
    }
  }

  if (typeof document !== "undefined" && document.documentElement.lang) {
    return normalizeLocale(document.documentElement.lang);
  }

  if (typeof navigator !== "undefined") {
    const browserLocale = navigator.languages?.[0] || navigator.language;
    if (browserLocale) {
      return normalizeLocale(browserLocale);
    }
  }

  return DEFAULT_LOCALE;
}

function formatMessage(template, vars) {
  return String(template).replace(/\{(\w+)\}/g, (_, key) => String(vars?.[key] ?? ""));
}

function getScript(locale) {
  const normalizedLocale = normalizeLocale(locale);
  return normalizedLocale === "zh-CN" || normalizedLocale === "ja-JP" ? "cjk" : "latin";
}

export function translateNativeSchedule(locale, key, vars) {
  const normalizedLocale = normalizeLocale(locale);
  const dictionary = nativeScheduleTranslations[normalizedLocale] ?? nativeScheduleTranslations[DEFAULT_LOCALE];
  const fallbackDictionary = nativeScheduleTranslations[DEFAULT_LOCALE];
  const template = dictionary[key] ?? fallbackDictionary[key] ?? key;
  return vars ? formatMessage(template, vars) : template;
}

const NativeScheduleI18nContext = createContext({
  locale: DEFAULT_LOCALE,
  script: getScript(DEFAULT_LOCALE),
  setLocale: () => {},
  t: (key, vars) => translateNativeSchedule(DEFAULT_LOCALE, key, vars)
});

export function NativeScheduleI18nProvider({ children }) {
  const [locale, setLocale] = useState(() => detectLocale());
  const resolvedLocale = locale;

  useEffect(() => {
    if (typeof document !== "undefined") {
      document.documentElement.lang = resolvedLocale;
    }

    if (typeof window !== "undefined" && !isHostedUi()) {
      try {
        window.localStorage.setItem(STORAGE_KEY, resolvedLocale);
      } catch {}
    }
  }, [resolvedLocale]);

  useEffect(() => {
    if (!isHostedUi()) {
      return undefined;
    }

    let disposed = false;
    const api = getWorkbenchApi();
    api.getLocale?.().then((hostLocale) => {
      if (!disposed && hostLocale) {
        setLocale(normalizeLocale(hostLocale));
      }
    });

    return () => {
      disposed = true;
    };
  }, []);

  useEffect(() => {
    if (typeof window === "undefined") {
      return undefined;
    }

    const handleHostLocaleChange = (event) => {
      const nextLocale = event?.detail?.locale || window.__RT_NATIVE_SCHEDULE_LOCALE__ || "";
      if (nextLocale) {
        setLocale(normalizeLocale(nextLocale));
      }
    };

    window.addEventListener(HOST_LOCALE_EVENT, handleHostLocaleChange);
    return () => {
      window.removeEventListener(HOST_LOCALE_EVENT, handleHostLocaleChange);
    };
  }, []);

  const value = useMemo(
    () => ({
      locale: resolvedLocale,
      script: getScript(resolvedLocale),
      setLocale: (nextLocale) => setLocale(normalizeLocale(nextLocale)),
      t: (key, vars) => translateNativeSchedule(resolvedLocale, key, vars)
    }),
    [resolvedLocale]
  );

  return createElement(NativeScheduleI18nContext.Provider, { value }, children);
}

export function useNativeScheduleI18n() {
  return useContext(NativeScheduleI18nContext);
}
