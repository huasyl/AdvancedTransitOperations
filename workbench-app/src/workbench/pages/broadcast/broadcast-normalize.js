import {
  BROADCAST_LANGUAGE_ALIASES,
  BROADCAST_LANGUAGE_LABEL_KEYS,
  BROADCAST_LANGUAGE_DISPLAY_ALIASES,
  TRIGGER_OPTIONS,
} from "./broadcast-constants";

function normalizeLangIndex(value) {
  return Number.isFinite(Number(value)) && Number(value) > 0
    ? Math.round(Number(value))
    : 1;
}

function formatVariableSlotLabel(langIndex, labels) {
  return labels.t("broadcast.variable.slot", {
    index: String(normalizeLangIndex(langIndex)),
  });
}

function formatVariableDisplayName(nameKey, langIndex, labels) {
  const baseName = labels.t(nameKey || "");
  const slotLabel = formatVariableSlotLabel(langIndex, labels);
  if (!baseName) {
    return slotLabel;
  }

  if (baseName.endsWith("】")) {
    return `${baseName.slice(0, -1)}_${slotLabel}】`;
  }

  if (baseName.endsWith("]")) {
    return `${baseName.slice(0, -1)}_${slotLabel}]`;
  }

  return `${baseName}_${slotLabel}`;
}

function normalizeSlotHintEntry(entry) {
  if (!entry || typeof entry !== "object") {
    return null;
  }

  const langIndex = normalizeLangIndex(entry.langIndex);
  const labels = Array.isArray(entry.labels)
    ? entry.labels
        .filter((label) => typeof label === "string")
        .map((label) => label.trim())
        .filter(Boolean)
    : [];

  return {
    langIndex,
    labels: Array.from(new Set(labels)),
  };
}

function normalizeRuleNode(node) {
  if (!node || typeof node !== "object") {
    return null;
  }

  return {
    id: typeof node.id === "string" ? node.id : "",
    type: typeof node.type === "string" ? node.type : "",
    name: typeof node.name === "string" ? node.name : "",
    nameKey: typeof node.nameKey === "string" ? node.nameKey : "",
    desc: typeof node.desc === "string" ? node.desc : "",
    descKey: typeof node.descKey === "string" ? node.descKey : "",
    langIndex: normalizeLangIndex(node.langIndex),
    delaySeconds:
      Number.isFinite(Number(node.delaySeconds)) &&
      Number(node.delaySeconds) >= 0
        ? Number(node.delaySeconds)
        : 0,
  };
}

function normalizeBroadcastRule(rule) {
  if (!rule || typeof rule !== "object") {
    return null;
  }

  const normalizedNodes = Array.isArray(rule.nodes)
    ? rule.nodes
        .map(normalizeRuleNode)
        .filter((node) => node && node.id && node.type)
    : [];

  return {
    id: typeof rule.id === "string" ? rule.id : "",
    title: typeof rule.title === "string" ? rule.title : "",
    titleKey: typeof rule.titleKey === "string" ? rule.titleKey : "",
    triggerId: typeof rule.triggerId === "string" ? rule.triggerId : "",
    trigger: typeof rule.trigger === "string" ? rule.trigger : "",
    triggerKey: typeof rule.triggerKey === "string" ? rule.triggerKey : "",
    nodes: normalizedNodes,
  };
}

function cloneBroadcastRules(rules) {
  return Array.isArray(rules)
    ? rules.map(normalizeBroadcastRule).filter((rule) => rule && rule.id)
    : [];
}

function resolveRuleTriggerLabel(rule, labels) {
  if (!rule) {
    return "";
  }

  if (rule.triggerId) {
    const option = TRIGGER_OPTIONS.find((entry) => entry.id === rule.triggerId);
    if (option?.labelKey) {
      return labels.t(option.labelKey);
    }
  }

  if (rule.triggerKey) {
    return labels.t(rule.triggerKey);
  }

  return rule.trigger || "";
}

function normalizeBroadcastMatchKey(value) {
  if (typeof value !== "string" || !value.trim()) {
    return "";
  }

  const source = value
    .replace(/^.*[\\/]/, "")
    .replace(/\.[^.]+$/, "")
    .trim();
  if (!source) {
    return "";
  }

  let normalized = "";
  let lastWasSeparator = false;

  for (let index = 0; index < source.length; index += 1) {
    const ch = source[index].toLowerCase();
    const code = ch.charCodeAt(0);
    const isAsciiDigit = code >= 48 && code <= 57;
    const isAsciiLetter = code >= 97 && code <= 122;
    const isNonAsciiWord =
      code > 127 && !(/\s/.test(ch) || ch === "_" || ch === "-");
    if (isAsciiDigit || isAsciiLetter || isNonAsciiWord) {
      normalized += ch;
      lastWasSeparator = false;
      continue;
    }

    if (
      (/\s/u.test(ch) || ch === "_" || ch === "-") &&
      !lastWasSeparator &&
      normalized
    ) {
      normalized += " ";
      lastWasSeparator = true;
    }
  }

  return normalized.trim();
}

function getBroadcastLocaleLanguageKey(locale) {
  const normalizedLocale = String(locale || "").toLowerCase();
  if (normalizedLocale.startsWith("zh")) {
    return "zh";
  }
  if (normalizedLocale.startsWith("ja")) {
    return "ja";
  }
  return "en";
}

function resolveBroadcastLanguageLabel(languageKey, labels) {
  const translationKey = BROADCAST_LANGUAGE_LABEL_KEYS[languageKey];
  if (!translationKey) {
    return "";
  }

  return labels.t(translationKey);
}

function resolveBroadcastLanguageKeyFromLabel(value, labels) {
  const normalized = String(value || "").trim();
  if (!normalized) {
    return "";
  }

  const lowered = normalized.toLowerCase();
  const languageKeys = Object.keys(BROADCAST_LANGUAGE_LABEL_KEYS);
  for (let index = 0; index < languageKeys.length; index += 1) {
    const languageKey = languageKeys[index];
    if (
      languageKey === lowered ||
      BROADCAST_LANGUAGE_ALIASES[languageKey]?.includes(lowered) ||
      BROADCAST_LANGUAGE_DISPLAY_ALIASES[languageKey]?.includes(normalized) ||
      resolveBroadcastLanguageLabel(languageKey, labels) === normalized
    ) {
      return languageKey;
    }
  }

  return "";
}

export {
  normalizeLangIndex,
  formatVariableSlotLabel,
  formatVariableDisplayName,
  normalizeSlotHintEntry,
  normalizeRuleNode,
  normalizeBroadcastRule,
  cloneBroadcastRules,
  normalizeBroadcastMatchKey,
  getBroadcastLocaleLanguageKey,
  resolveBroadcastLanguageLabel,
  resolveBroadcastLanguageKeyFromLabel,
};
