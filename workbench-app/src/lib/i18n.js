import { createContext, createElement, useContext, useEffect, useMemo, useState } from "react";
import { translations as bundledTranslations } from "./translations";
import { getWorkbenchApi } from "./workbench-api";

const DEFAULT_LOCALE = "en-US";
const SUPPORTED_LOCALES = ["en-US", "zh-CN"];
const LOCALE_STORAGE_KEY = "rtm.locale";
const LOCALE_RESOURCE_MAP = {
  "zh-CN": "zh-HANS"
};

const runtimeTranslationCache = new Map();

function isHostedUi() {
  return typeof window !== "undefined" && window.location?.protocol === "coui:";
}

function normalizeLocale(value) {
  const locale = String(value || "").trim();
  if (!locale) {
    return DEFAULT_LOCALE;
  }

  if (SUPPORTED_LOCALES.includes(locale)) {
    return locale;
  }

  const normalized = locale.replace("_", "-").toLowerCase();
  if (normalized.startsWith("zh")) {
    return "zh-CN";
  }

  return "en-US";
}

function detectLocale() {
  if (typeof window !== "undefined") {
    const search = String(window.location?.search || "");
    let queryLocale = "";
    const match = /(?:^\?|&)rt_locale=([^&]+)/.exec(search);
    if (match?.[1]) {
      try {
        queryLocale = decodeURIComponent(match[1].replace(/\+/g, " "));
      } catch {
        queryLocale = match[1];
      }
    }
    if (queryLocale) {
      return normalizeLocale(queryLocale);
    }

    if (!isHostedUi()) {
      try {
        const storedLocale = window.localStorage.getItem(LOCALE_STORAGE_KEY);
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

function getLocaleResourceName(locale) {
  return LOCALE_RESOURCE_MAP[normalizeLocale(locale)] ?? normalizeLocale(locale);
}

function mergeCatalog(baseCatalog, locale, nextDictionary) {
  return {
    ...baseCatalog,
    [locale]: {
      ...(baseCatalog[locale] ?? {}),
      ...nextDictionary
    }
  };
}

function loadCatalogFromText(text, locale) {
  if (!text) {
    return null;
  }

  try {
    const parsed = JSON.parse(text);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      return null;
    }
    return { [locale]: parsed };
  } catch {
    return null;
  }
}

async function fetchLocaleCatalog(locale) {
  const resourceName = getLocaleResourceName(locale);
  const candidatePaths = [
    `./locales/${resourceName}.json`,
    `../locales/${resourceName}.json`,
    `/locales/${resourceName}.json`,
    `/UI/workbench-app/dist/locales/${resourceName}.json`,
    `/UI/workbench/locales/${resourceName}.json`
  ];

  if (typeof window === "undefined" || typeof window.fetch !== "function") {
    return null;
  }

  for (const candidate of candidatePaths) {
    try {
      const response = await window.fetch(candidate, { cache: "no-cache" });
      if (!response.ok) {
        continue;
      }

      const text = await response.text();
      const catalog = loadCatalogFromText(text, normalizeLocale(locale));
      if (catalog) {
        return catalog;
      }
    } catch {
      continue;
    }
  }

  return null;
}

function getCatalogForLocale(locale, catalog) {
  const normalized = normalizeLocale(locale);
  return catalog[normalized] ?? bundledTranslations[normalized] ?? bundledTranslations[DEFAULT_LOCALE];
}

function translateWithCatalog(locale, key, vars, catalog) {
  const dictionary = getCatalogForLocale(locale, catalog);
  const fallbackDictionary = bundledTranslations[DEFAULT_LOCALE];
  const template = dictionary[key] ?? fallbackDictionary[key] ?? key;
  return vars ? formatMessage(template, vars) : template;
}

export function translate(locale, key, vars) {
  return translateWithCatalog(locale, key, vars, bundledTranslations);
}

const I18nContext = createContext({
  locale: DEFAULT_LOCALE,
  setLocale: () => {},
  t: (key, vars) => translate(DEFAULT_LOCALE, key, vars)
});

export function WorkbenchI18nProvider({ children }) {
  const [locale, setLocale] = useState(() => detectLocale());
  const [catalog, setCatalog] = useState(() => bundledTranslations);

  useEffect(() => {
    if (typeof document !== "undefined") {
      document.documentElement.lang = locale;
    }

    if (typeof window !== "undefined" && !isHostedUi()) {
      try {
        window.localStorage.setItem(LOCALE_STORAGE_KEY, locale);
      } catch {}
    }
  }, [locale]);

  useEffect(() => {
    const normalizedLocale = normalizeLocale(locale);
    const cacheKey = getLocaleResourceName(normalizedLocale);

    if (runtimeTranslationCache.has(cacheKey)) {
      const cachedCatalog = runtimeTranslationCache.get(cacheKey);
      if (cachedCatalog) {
        setCatalog((current) => mergeCatalog(current, normalizedLocale, cachedCatalog));
      }
      return undefined;
    }

    let disposed = false;

    (async () => {
      const loadedCatalog = await fetchLocaleCatalog(normalizedLocale);
      if (disposed || !loadedCatalog) {
        return;
      }

      const nextDictionary = loadedCatalog[normalizedLocale];
      runtimeTranslationCache.set(cacheKey, nextDictionary);
      setCatalog((current) => mergeCatalog(current, normalizedLocale, nextDictionary));
    })();

    return () => {
      disposed = true;
    };
  }, [locale]);

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

  const value = useMemo(
    () => ({
      locale,
      setLocale: (nextLocale) => setLocale(normalizeLocale(nextLocale)),
      t: (key, vars) => translateWithCatalog(locale, key, vars, catalog)
    }),
    [catalog, locale]
  );

  return createElement(I18nContext.Provider, { value }, children);
}

export function useI18n() {
  return useContext(I18nContext);
}
