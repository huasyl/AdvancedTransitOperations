import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "./native-schedule-i18n";
import { getWorkbenchApi } from "./lib/workbench-api";
import WorkbenchDropdown from "./components/WorkbenchDropdown";
import WorkbenchScrollArea from "./components/WorkbenchScrollArea";

const VARIABLE_LIBRARY = [
  { id: "current_station", nameKey: "broadcast.variable.current", descKey: "" },
  { id: "next_station", nameKey: "broadcast.variable.next", descKey: "" },
  { id: "terminal_station", nameKey: "broadcast.variable.terminal", descKey: "" },
  { id: "turnback_station", nameKey: "broadcast.variable.turnback", descKey: "" }
];

const DELAY_LIBRARY = [
  { id: "delay_03", nameKey: "broadcast.delay.03", descKey: "broadcast.delay.label", delaySeconds: 0.3 },
  { id: "delay_05", nameKey: "broadcast.delay.05", descKey: "broadcast.delay.label", delaySeconds: 0.5 },
  { id: "delay_08", nameKey: "broadcast.delay.08", descKey: "broadcast.delay.label", delaySeconds: 0.8 },
  { id: "delay_10", nameKey: "broadcast.delay.10", descKey: "broadcast.delay.label", delaySeconds: 1 },
  { id: "delay_20", nameKey: "broadcast.delay.20", descKey: "broadcast.delay.label", delaySeconds: 2 }
];

const BROADCAST_LANGUAGE_ALIASES = {
  en: ["eng", "en", "english"],
  zh: ["zh", "cn", "chi", "chs", "cht", "chinese", "mandarin"],
  ja: ["ja", "jp", "jpn", "japanese"],
  ko: ["ko", "kr", "kor", "korean"],
  yue: ["yue", "cantonese"],
  fr: ["fr", "fre", "fra", "french"],
  de: ["de", "ger", "deu", "german"],
  es: ["es", "spa", "spanish"],
  ru: ["ru", "rus", "russian"],
  pt: ["pt", "por", "portuguese"],
  th: ["th", "tha", "thai"],
  ar: ["ar", "ara", "arabic"]
};

const BROADCAST_LANGUAGE_LABEL_KEYS = {
  en: "broadcast.language.short.en",
  zh: "broadcast.language.short.zh",
  ja: "broadcast.language.short.ja",
  ko: "broadcast.language.short.ko",
  yue: "broadcast.language.short.yue",
  fr: "broadcast.language.short.fr",
  de: "broadcast.language.short.de",
  es: "broadcast.language.short.es",
  ru: "broadcast.language.short.ru",
  pt: "broadcast.language.short.pt",
  th: "broadcast.language.short.th",
  ar: "broadcast.language.short.ar"
};

const BROADCAST_LANGUAGE_DISPLAY_ALIASES = {
  en: ["英", "eng"],
  zh: ["中", "chi"],
  ja: ["日", "jpn"],
  ko: ["韩", "韓", "kor"],
  yue: ["粤"],
  fr: ["法", "仏", "fre"],
  de: ["德", "独", "ger"],
  es: ["西", "spa"],
  ru: ["俄", "露", "rus"],
  pt: ["葡", "por"],
  th: ["泰", "tha"],
  ar: ["阿", "ara"]
};

const TRIGGER_OPTIONS = [
  { id: "approach_station", labelKey: "broadcast.trigger.approachStation" },
  { id: "stop_and_open", labelKey: "broadcast.trigger.stopAndOpen" },
  { id: "leave_station", labelKey: "broadcast.trigger.leaveStation" },
  { id: "mid_route", labelKey: "broadcast.trigger.midRoute" },
  { id: "bypass_waiting", labelKey: "broadcast.trigger.bypassWaiting" }
];

const LINE_OPTIONS = [
  { id: "line_1_main", labelKey: "broadcast.line.main" },
  { id: "airport_express", labelKey: "broadcast.line.airport" },
  { id: "loop_test", labelKey: "broadcast.line.loop" }
];

const EXTERNAL_ASSET_FILE_SYSTEM = {
  "C:\\Mods\\Audio\\": {
    folders: ["BGM", "SFX", "Voice_Packs"],
    files: [
      { id: "root_f1", name: "Global_Config_Ping.wav" }
    ]
  },
  "C:\\Mods\\Audio\\BGM\\": {
    folders: [],
    files: [
      { id: "bgm_1", name: "Ambient_City.wav" },
      { id: "bgm_2", name: "Menu_Theme.ogg" }
    ]
  },
  "C:\\Mods\\Audio\\SFX\\": {
    folders: ["Vehicles", "UI"],
    files: []
  },
  "C:\\Mods\\Audio\\SFX\\Vehicles\\": {
    folders: [],
    files: [
      { id: "veh_1", name: "Train_Whistle.ogg" },
      { id: "veh_2", name: "Bus_Brake.wav" }
    ]
  },
  "C:\\Mods\\Audio\\SFX\\UI\\": {
    folders: [],
    files: [
      { id: "ui_1", name: "Notification_Ping.mp3" }
    ]
  },
  "C:\\Mods\\Audio\\Voice_Packs\\": {
    folders: [],
    files: [
      { id: "vp_1", name: "Station_Jingmai.wav" },
      { id: "vp_2", name: "Next_Stop_Is.wav" }
    ]
  }
};

const DEFAULT_EXTERNAL_ASSET_PATH = "C:\\Mods\\Audio\\";
const TAB_TRANSITION_MS = 300;
const INLINE_PANEL_TRANSITION_MS = 650;
const INLINE_PANEL_EASING = "cubic-bezier(0.19, 1, 0.22, 1)";
const PAGE_ENTER_ANIMATION_MS = 850;
const IMPORT_OVERLAY_TRANSITION_MS = 350;

function createEmptyExternalAssetBrowserState() {
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

function extractBackendLineOptions(snapshot) {
  const sourceLines = Array.isArray(snapshot?.lines) ? snapshot.lines : [];
  return sourceLines
    .filter((line) => line && typeof line.id === "string" && line.id)
    .map((line) => ({
      id: line.id,
      label: typeof line.name === "string" && line.name ? line.name : line.id
    }));
}

function splitIntoColumns(items, count = 2) {
  const columns = Array.from({ length: count }, () => []);
  items.forEach((item, index) => {
    columns[index % count].push(item);
  });
  return columns;
}

function splitIntoVerticalColumns(items, count = 2) {
  const sourceItems = Array.isArray(items) ? items : [];
  const rowsPerColumn = Math.ceil(sourceItems.length / count);
  return Array.from({ length: count }, (_, columnIndex) =>
    sourceItems.slice(columnIndex * rowsPerColumn, (columnIndex + 1) * rowsPerColumn)
  );
}

function normalizeLangIndex(value) {
  return Number.isFinite(Number(value)) && Number(value) > 0
    ? Math.round(Number(value))
    : 1;
}

function formatVariableSlotLabel(langIndex, labels) {
  return labels.t("broadcast.variable.slot", { index: String(normalizeLangIndex(langIndex)) });
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
    labels: Array.from(new Set(labels))
  };
}

function mergeBindingSlotHints(...collections) {
  const labelsBySlot = new Map();
  collections.forEach((items) => {
    (Array.isArray(items) ? items : []).forEach((entry) => {
      const normalized = normalizeSlotHintEntry(entry);
      if (!normalized) {
        return;
      }

      if (!labelsBySlot.has(normalized.langIndex)) {
        labelsBySlot.set(normalized.langIndex, new Set());
      }

      normalized.labels.forEach((label) => labelsBySlot.get(normalized.langIndex)?.add(label));
    });
  });

  return Array.from(labelsBySlot.entries())
    .sort((left, right) => left[0] - right[0])
    .map(([langIndex, labels]) => ({
      langIndex,
      labels: Array.from(labels).sort((left, right) => left.localeCompare(right))
    }));
}

function deriveBindingSlotHintsFromStations(stations) {
  const labelsBySlot = new Map();
  (Array.isArray(stations) ? stations : []).forEach((station) => {
    const orderedAudios = (Array.isArray(station?.audios) ? station.audios : [])
      .filter((audio) => audio && typeof audio.assetName === "string" && audio.assetName)
      .slice()
      .sort((left, right) => normalizeLangIndex(left?.langIndex) - normalizeLangIndex(right?.langIndex));
    orderedAudios.forEach((audio) => {
      const langIndex = normalizeLangIndex(audio?.langIndex);
      const label = typeof audio?.lang === "string" ? audio.lang.trim() : "";
      if (!label) {
        return;
      }

      if (!labelsBySlot.has(langIndex)) {
        labelsBySlot.set(langIndex, new Set());
      }

      labelsBySlot.get(langIndex)?.add(label);
    });
  });

  return Array.from(labelsBySlot.entries())
    .sort((left, right) => left[0] - right[0])
    .map(([langIndex, labels]) => ({
      langIndex,
      labels: Array.from(labels).sort((left, right) => left.localeCompare(right))
    }));
}

function buildBroadcastTrayAssetLibrary(assets, stations) {
  const boundAssetNames = new Set();
  (Array.isArray(stations) ? stations : []).forEach((station) => {
    (Array.isArray(station?.audios) ? station.audios : []).forEach((audio) => {
      if (audio && typeof audio.assetName === "string" && audio.assetName) {
        boundAssetNames.add(audio.assetName);
      }
    });
  });

  return (Array.isArray(assets) ? assets : [])
    .map((asset, index) => ({
      ...asset,
      isStationBound: boundAssetNames.has(asset?.name),
      originalIndex: index
    }))
    .sort((left, right) => {
      if (left.isStationBound !== right.isStationBound) {
        return left.isStationBound ? 1 : -1;
      }

      return left.originalIndex - right.originalIndex;
    })
    .map(({ originalIndex, ...asset }) => asset);
}

function buildVariableLibrary(baseLibrary, slotHints, labels, turnbackPoints) {
  const normalizedHints = Array.isArray(slotHints)
    ? slotHints.map(normalizeSlotHintEntry).filter(Boolean)
    : [];
  const normalizedTurnbackPoints = Array.isArray(turnbackPoints) ? turnbackPoints : [];
  const turnbackDescription = normalizedTurnbackPoints
    .map((point) => (point?.resolved && point?.stationName ? point.stationName : labels.unresolvedTurnback))
    .join(" / ");
  const maxSlotIndex = Math.max(
    1,
    ...normalizedHints.map((entry) => normalizeLangIndex(entry.langIndex))
  );
  const slotHintByIndex = new Map(normalizedHints.map((entry) => [entry.langIndex, entry]));
  const result = [];

  baseLibrary.forEach((variable) => {
    if (variable.id === "turnback_station" && normalizedTurnbackPoints.length === 0) {
      return;
    }

    for (let langIndex = 1; langIndex <= maxSlotIndex; langIndex += 1) {
      const hint = slotHintByIndex.get(langIndex);
      const slotLabel = formatVariableSlotLabel(langIndex, labels);
      const joinedLabels = Array.isArray(hint?.labels) && hint.labels.length > 0
        ? hint.labels.join(" / ")
        : "";
      const desc = variable.id === "turnback_station"
        ? turnbackDescription
        : (joinedLabels ? `${slotLabel}: ${joinedLabels}` : slotLabel);
      result.push({
        ...variable,
        id: `${variable.id}__slot_${langIndex}`,
        langIndex,
        name: formatVariableDisplayName(variable.nameKey, langIndex, labels),
        desc
      });
    }
  });

  return result;
}

function resolveVariableNodeDisplayName(node, labels) {
  if (!node || node.type !== "variable" || !node.nameKey) {
    return node?.name || "";
  }

  return formatVariableDisplayName(node.nameKey, node.langIndex, labels);
}

function resolveRuleNodeKindLabel(node, labels) {
  if (!node) {
    return "";
  }

  if (node.type === "variable") {
    return labels.dynamicVariable || (node.descKey ? labels.t(node.descKey) : "") || node.desc || "";
  }

  if (node.type === "asset") {
    return labels.assetNode || node.desc || (node.descKey ? labels.t(node.descKey) : "");
  }

  return node.desc || (node.descKey ? labels.t(node.descKey) : "");
}

function animateElementScrollTop(element, targetTop, duration = 260, frameRef = null) {
  if (!element) {
    return 0;
  }

  const startTop = element.scrollTop;
  const delta = targetTop - startTop;
  if (Math.abs(delta) < 24 || duration <= 0) {
    element.scrollTop = targetTop;
    if (frameRef) {
      frameRef.current = 0;
    }
    return 0;
  }

  const startTime = typeof performance !== "undefined" && typeof performance.now === "function"
    ? performance.now()
    : Date.now();
  const easeInOutCubic = (value) => (
    value < 0.5
      ? 4 * value * value * value
      : 1 - Math.pow(-2 * value + 2, 3) / 2
  );
  let frameId = 0;
  let lastAppliedTop = startTop;

  function tick(now) {
    const currentTime = typeof now === "number" ? now : Date.now();
    const progress = Math.min(1, (currentTime - startTime) / duration);
    const nextTop = startTop + delta * easeInOutCubic(progress);
    if (Math.abs(nextTop - lastAppliedTop) >= 0.75 || progress >= 1) {
      element.scrollTop = nextTop;
      lastAppliedTop = nextTop;
    }

    if (progress < 1) {
      frameId = window.requestAnimationFrame(tick);
      if (frameRef) {
        frameRef.current = frameId;
      }
      return;
    }

    element.scrollTop = targetTop;
    frameId = 0;
    if (frameRef) {
      frameRef.current = 0;
    }
  }

  frameId = window.requestAnimationFrame(tick);
  if (frameRef) {
    frameRef.current = frameId;
  }
  return frameId;
}

function animateScrollTopWithTransform(scrollElement, contentElement, targetTop, duration = 460, cleanupRef = null) {
  if (!scrollElement || !contentElement) {
    return;
  }

  if (cleanupRef?.current) {
    window.clearTimeout(cleanupRef.current);
    cleanupRef.current = null;
  }

  const startTop = scrollElement.scrollTop;
  const delta = targetTop - startTop;
  if (Math.abs(delta) < 24) {
    scrollElement.scrollTop = targetTop;
    contentElement.style.transition = "";
    contentElement.style.transform = "";
    return;
  }

  contentElement.style.transition = "none";
  contentElement.style.transform = `translateY(${delta}px)`;
  scrollElement.scrollTop = targetTop;

  window.requestAnimationFrame(() => {
    window.requestAnimationFrame(() => {
      contentElement.style.transition = `transform ${duration}ms cubic-bezier(0.16, 1, 0.3, 1)`;
      contentElement.style.transform = "translateY(0px)";

      if (cleanupRef) {
        cleanupRef.current = window.setTimeout(() => {
          cleanupRef.current = null;
          contentElement.style.transition = "";
          contentElement.style.transform = "";
        }, duration + 80);
      }
    });
  });
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
      Number.isFinite(Number(node.delaySeconds)) && Number(node.delaySeconds) >= 0
        ? Number(node.delaySeconds)
        : 0
  };
}

function normalizeBroadcastRule(rule) {
  if (!rule || typeof rule !== "object") {
    return null;
  }

  const normalizedNodes = Array.isArray(rule.nodes)
    ? rule.nodes.map(normalizeRuleNode).filter((node) => node && node.id && node.type)
    : [];

  return {
    id: typeof rule.id === "string" ? rule.id : "",
    title: typeof rule.title === "string" ? rule.title : "",
    titleKey: typeof rule.titleKey === "string" ? rule.titleKey : "",
    triggerId: typeof rule.triggerId === "string" ? rule.triggerId : "",
    trigger: typeof rule.trigger === "string" ? rule.trigger : "",
    triggerKey: typeof rule.triggerKey === "string" ? rule.triggerKey : "",
    nodes: normalizedNodes
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
    const isNonAsciiWord = code > 127 && !(/\s/.test(ch) || ch === "_" || ch === "-");
    if (isAsciiDigit || isAsciiLetter || isNonAsciiWord) {
      normalized += ch;
      lastWasSeparator = false;
      continue;
    }

    if ((/\s/u.test(ch) || ch === "_" || ch === "-") && !lastWasSeparator && normalized) {
      normalized += " ";
      lastWasSeparator = true;
    }
  }

  return normalized.trim();
}

function formatBroadcastAssetDisplayName(value) {
  if (typeof value !== "string") {
    return "";
  }

  return value.replace(/\.[^.\\/]+$/, "");
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
    if (languageKey === lowered
      || BROADCAST_LANGUAGE_ALIASES[languageKey]?.includes(lowered)
      || BROADCAST_LANGUAGE_DISPLAY_ALIASES[languageKey]?.includes(normalized)
      || resolveBroadcastLanguageLabel(languageKey, labels) === normalized) {
      return languageKey;
    }
  }

  return "";
}

function extractBroadcastLanguageKey(assetName, stationName, fallbackLanguageKey) {
  const normalizedAsset = normalizeBroadcastMatchKey(assetName);
  const normalizedStation = normalizeBroadcastMatchKey(stationName);
  const stationTokens = new Set(normalizedStation.split(" ").filter(Boolean));
  const genericTokens = new Set(["station", "audio", "voice", "stop"]);
  const stationIndex = normalizedStation ? normalizedAsset.indexOf(normalizedStation) : -1;
  const remainingSource = stationIndex >= 0
    ? `${normalizedAsset.slice(0, stationIndex)} ${normalizedAsset.slice(stationIndex + normalizedStation.length)}`
    : normalizedAsset;
  const remainingTokens = remainingSource
    .split(" ")
    .filter((token) => token && !stationTokens.has(token) && !genericTokens.has(token));

  if (remainingTokens.length === 0) {
    return fallbackLanguageKey;
  }

  const alias = remainingTokens.join(" ");
  const languageKeys = Object.keys(BROADCAST_LANGUAGE_ALIASES);
  for (let index = 0; index < languageKeys.length; index += 1) {
    const languageKey = languageKeys[index];
    if (BROADCAST_LANGUAGE_ALIASES[languageKey]?.includes(alias)) {
      return languageKey;
    }
  }

  return fallbackLanguageKey;
}

function extractBroadcastLanguageHint(assetName, stationName, fallbackLanguageKey, labels) {
  const languageKey = extractBroadcastLanguageKey(assetName, stationName, fallbackLanguageKey);
  return resolveBroadcastLanguageLabel(languageKey, labels);
}

function resolveBroadcastConflictLanguageKey(entry, stationName, fallbackLanguageKey, labels) {
  const suggestedKey = resolveBroadcastLanguageKeyFromLabel(entry?.suggestedLang, labels);
  if (suggestedKey) {
    return suggestedKey;
  }

  return extractBroadcastLanguageKey(entry?.assetName || "", stationName, fallbackLanguageKey);
}

function deriveBroadcastStationStatus(audios, conflictAssets) {
  if (Array.isArray(conflictAssets) && conflictAssets.length > 0) {
    return "conflict";
  }

  if (Array.isArray(audios) && audios.length > 0) {
    return "ready";
  }

  return "missing";
}

function sortBroadcastConflictAssets(conflictAssets, stationName, fallbackLanguageKey, labels) {
  const entries = Array.isArray(conflictAssets) ? [...conflictAssets] : [];
  const resolveSuggestedLabel = (entry) => (
    typeof entry?.suggestedLang === "string" && entry.suggestedLang
      ? entry.suggestedLang
      : extractBroadcastLanguageHint(entry?.assetName || "", stationName, fallbackLanguageKey, labels)
  );

  entries.sort((left, right) => {
    const leftPriority = resolveBroadcastConflictLanguageKey(left, stationName, fallbackLanguageKey, labels) === fallbackLanguageKey ? 0 : 1;
    const rightPriority = resolveBroadcastConflictLanguageKey(right, stationName, fallbackLanguageKey, labels) === fallbackLanguageKey ? 0 : 1;
    if (leftPriority !== rightPriority) {
      return leftPriority - rightPriority;
    }

    return String(left?.assetName || "").localeCompare(String(right?.assetName || ""));
  });

  return entries.map((entry) => ({
    ...entry,
    suggestedLang: resolveSuggestedLabel(entry)
  }));
}

function collectBroadcastVariableSlotRequirements(rules) {
  const slotIndexes = new Set();
  (Array.isArray(rules) ? rules : []).forEach((rule) => {
    (Array.isArray(rule?.nodes) ? rule.nodes : []).forEach((node) => {
      if (node?.type !== "variable" || !node?.nameKey) {
        return;
      }

      slotIndexes.add(normalizeLangIndex(node.langIndex));
    });
  });
  return Array.from(slotIndexes).sort((left, right) => left - right);
}

function collectBroadcastStationSlotIndexes(station) {
  const slotIndexes = new Set();
  const orderedAudios = (Array.isArray(station?.audios) ? station.audios : [])
    .filter((audio) => audio && typeof audio.assetName === "string" && audio.assetName)
    .slice()
    .sort((left, right) => normalizeLangIndex(left?.langIndex) - normalizeLangIndex(right?.langIndex));
  orderedAudios.forEach((audio, index) => {
    if (!audio || typeof audio.assetName !== "string" || !audio.assetName) {
      return;
    }

    slotIndexes.add(index + 1);
  });
  return slotIndexes;
}

function buildBroadcastVariableMappingIssue(rules, stations) {
  const requiredSlots = collectBroadcastVariableSlotRequirements(rules);
  if (requiredSlots.length === 0) {
    return null;
  }

  const stationList = Array.isArray(stations) ? stations : [];
  for (let index = 0; index < stationList.length; index += 1) {
    const station = stationList[index];
    if (!station || !station.id) {
      continue;
    }

    if (Array.isArray(station.conflictAssets) && station.conflictAssets.length > 0) {
      return {
        type: "conflict",
        stationId: station.id,
        stationName: station.name || "",
        requiredSlots
      };
    }

    const availableSlots = collectBroadcastStationSlotIndexes(station);
    const missingSlot = requiredSlots.find((slot) => !availableSlots.has(slot));
    if (missingSlot) {
      return {
        type: "missing",
        stationId: station.id,
        stationName: station.name || "",
        requiredSlots,
        missingSlot
      };
    }
  }

  return null;
}

function SearchIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-tool-icon">
      <circle cx="11" cy="11" r="7" />
      <line x1="20" y1="20" x2="16.65" y2="16.65" />
    </svg>
  );
}

function FilterIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-tool-icon">
      <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
    </svg>
  );
}

function PlusIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-action-icon">
      <line x1="12" y1="5" x2="12" y2="19" />
      <line x1="5" y1="12" x2="19" y2="12" />
    </svg>
  );
}

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-close-icon">
      <line x1="18" y1="6" x2="6" y2="18" />
      <line x1="6" y1="6" x2="18" y2="18" />
    </svg>
  );
}

function TrashIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-action-icon">
      <path d="M4 7h16" />
      <path d="M9 7V5h6v2" />
      <path d="M7 7l1 12h8l1-12" />
      <line x1="10" y1="10" x2="10" y2="16" />
      <line x1="14" y1="10" x2="14" y2="16" />
    </svg>
  );
}

function VolumeIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5" />
      <path d="M15 9a4 4 0 0 1 0 6" />
      <path d="M18 6a8 8 0 0 1 0 12" />
    </svg>
  );
}

function PlayIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-play-icon is-fill">
      <polygon points="8 5 19 12 8 19 8 5" />
    </svg>
  );
}

function PauseIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-play-icon is-fill">
      <rect x="7" y="5" width="3.5" height="14" rx="0.8" />
      <rect x="13.5" y="5" width="3.5" height="14" rx="0.8" />
    </svg>
  );
}

function ArrowLeftIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <line x1="19" y1="12" x2="5" y2="12" />
      <polyline points="12 19 5 12 12 5" />
    </svg>
  );
}

function FolderIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <path d="M3 7h6l2 2h10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z" />
      <path d="M3 7V6a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v1" />
    </svg>
  );
}

function ReturnUpIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <polyline points="9 10 4 15 9 20" />
      <path d="M20 4v8a3 3 0 0 1-3 3H4" />
    </svg>
  );
}

function FileAudioIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <path d="M14 2H7a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V7z" />
      <polyline points="14 2 14 7 19 7" />
      <path d="M10 16a2 2 0 1 0 2 2v-5l4-1v4a2 2 0 1 0 2 2v-7l-8 2z" />
    </svg>
  );
}

function SquareIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <rect x="4" y="4" width="16" height="16" rx="1.5" ry="1.5" />
    </svg>
  );
}

function CheckSquareIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <rect x="4" y="4" width="16" height="16" rx="1.5" ry="1.5" />
      <polyline points="8 12 11 15 16 9" />
    </svg>
  );
}

function DatabaseIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon dw-bc-node-icon is-variable ${className}`.trim()}>
      <ellipse cx="12" cy="5" rx="8" ry="3" />
      <path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5" />
      <path d="M4 11v8c0 1.7 3.6 3 8 3s8-1.3 8-3v-8" />
    </svg>
  );
}

function SpeakerIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon dw-bc-node-icon ${className}`.trim()}>
      <polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5" />
      <path d="M15.5 8.5a5 5 0 0 1 0 7" />
      <path d="M18.5 5.5a9 9 0 0 1 0 13" />
    </svg>
  );
}

function DelayIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon dw-bc-node-icon is-delay ${className}`.trim()}>
      <circle cx="12" cy="12" r="8" />
      <path d="M12 8v4l2.5 2.5" />
    </svg>
  );
}

function ImportCardName({ children }) {
  const textRef = useRef(null);
  const [displayText, setDisplayText] = useState(String(children || ""));

  useLayoutEffect(() => {
    const node = textRef.current;
    if (!(node instanceof HTMLElement)) {
      return undefined;
    }

    function fitsWithinTwoLines(text, width, style, maxHeight) {
      const measureNode = document.createElement("div");
      measureNode.style.position = "absolute";
      measureNode.style.visibility = "hidden";
      measureNode.style.pointerEvents = "none";
      measureNode.style.left = "-99999px";
      measureNode.style.top = "0";
      measureNode.style.width = `${width}px`;
      measureNode.style.fontFamily = style.fontFamily;
      measureNode.style.fontSize = style.fontSize;
      measureNode.style.fontWeight = style.fontWeight;
      measureNode.style.lineHeight = style.lineHeight;
      measureNode.style.letterSpacing = style.letterSpacing;
      measureNode.style.whiteSpace = "normal";
      measureNode.style.overflowWrap = "anywhere";
      measureNode.style.wordBreak = "break-word";
      measureNode.textContent = text;
      document.body.appendChild(measureNode);
      const fits = measureNode.scrollHeight <= maxHeight + 1;
      document.body.removeChild(measureNode);
      return fits;
    }

    function syncTruncation() {
      const sourceText = String(children || "");
      const computedStyle = window.getComputedStyle(node);
      const availableWidth = Math.max(0, Math.floor(node.clientWidth));
      const maxHeight = Math.max(48, Math.ceil(node.clientHeight || 48));

      if (!sourceText || availableWidth <= 0) {
        setDisplayText(sourceText);
        return;
      }

      if (fitsWithinTwoLines(sourceText, availableWidth, computedStyle, maxHeight)) {
        setDisplayText(sourceText);
        return;
      }

      let low = 0;
      let high = sourceText.length;
      let best = "...";

      while (low <= high) {
        const middle = Math.floor((low + high) / 2);
        const candidate = `${sourceText.slice(0, middle).trimEnd()}...`;
        if (fitsWithinTwoLines(candidate, availableWidth, computedStyle, maxHeight)) {
          best = candidate;
          low = middle + 1;
        } else {
          high = middle - 1;
        }
      }

      setDisplayText(best);
    }

    syncTruncation();

    if (typeof ResizeObserver === "undefined") {
      return undefined;
    }

    const observer = new ResizeObserver(() => {
      syncTruncation();
    });
    observer.observe(node);

    return () => observer.disconnect();
  }, [children]);

  return (
    <span ref={textRef} className="dw-bc-import-card-name" title={String(children || "")}>
      {displayText}
    </span>
  );
}

function AnimatedInlinePanel({ visible, className, panelRef, children }) {
  const contentRef = useRef(null);
  const outerRef = useRef(null);
  const [panelHeight, setPanelHeight] = useState(0);

  useLayoutEffect(() => {
    if (!contentRef.current) {
      return undefined;
    }

    function syncHeight() {
      if (!contentRef.current) {
        return;
      }
      setPanelHeight(contentRef.current.scrollHeight);
    }

    syncHeight();

    if (!visible || typeof ResizeObserver === "undefined") {
      return undefined;
    }

    const observer = new ResizeObserver(() => {
      syncHeight();
    });
    observer.observe(contentRef.current);

    return () => observer.disconnect();
  }, [children, visible]);

  useEffect(() => {
    if (!panelRef) {
      return undefined;
    }

    const node = outerRef.current;
    if (visible && node) {
      if (typeof panelRef === "function") {
        panelRef(node);
      } else {
        panelRef.current = node;
      }
      return undefined;
    }

    if (typeof panelRef === "function") {
      panelRef(null);
    } else if (panelRef.current === node) {
      panelRef.current = null;
    }

    return undefined;
  }, [panelRef, visible]);

  function handleOuterRef(node) {
    outerRef.current = node;
  }

  function handleContentRef(node) {
    contentRef.current = node;
  }

  return (
    <div
      ref={handleOuterRef}
      className={`dw-bc-animated-panel is-tray${visible ? " is-open" : " is-closed"}${className ? ` ${className}` : ""}`}
      style={{
        maxHeight: visible ? `${panelHeight}px` : "0px",
        overflow: "hidden",
        opacity: visible ? 1 : 0,
        pointerEvents: visible ? "auto" : "none",
        transition: `max-height ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}, opacity ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}`
      }}
    >
      <div
        className="dw-bc-animated-panel-inner"
        ref={handleContentRef}
        style={{
          opacity: visible ? 1 : 0,
          transform: visible ? "translateY(0)" : "translateY(-12px)",
          transition: `opacity ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}, transform ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}`
        }}
      >
        {children}
      </div>
    </div>
  );
}

function AnimatedFadePresence({ visible, className, children }) {
  const [shouldRender, setShouldRender] = useState(visible);
  const [stage, setStage] = useState(visible ? "entered" : "exited");

  useEffect(() => {
    let timer = null;
    let raf = null;

    if (visible) {
      setShouldRender(true);
      setStage("entering");
      raf = window.requestAnimationFrame(() => {
        setStage("entered");
      });
    } else if (shouldRender) {
      setStage("exiting");
      timer = window.setTimeout(() => {
        setShouldRender(false);
        setStage("exited");
      }, TAB_TRANSITION_MS);
    }

    return () => {
      if (raf) {
        window.cancelAnimationFrame(raf);
      }
      if (timer) {
        window.clearTimeout(timer);
      }
    };
  }, [shouldRender, visible]);

  if (!shouldRender) {
    return null;
  }

  return (
    <div className={`${className ? `${className} ` : ""}dw-bc-fade-presence is-${stage}`}>
      {children}
    </div>
  );
}

function SequenceRule({
  rule,
  trayContext,
  trayCategory,
  removingRule,
  removingNodeIds,
  onToggleTray,
  onRemoveNode,
  onRemoveRule,
  onSetTrayCategory,
  onCloseTray,
  onAddAsset,
  onAddVariable,
  onAddDelay,
  trayRef,
  assetLibrary,
  variableLibrary,
  delayLibrary,
  previewingRuleId,
  onToggleRulePreview,
  labels
}) {
  const isTrayVisible = trayContext?.ruleId === rule.id;
  const [displayAction, setDisplayAction] = useState(null);
  const assetColumns = splitIntoVerticalColumns(assetLibrary);
  const variableColumns = splitIntoColumns(variableLibrary);
  const delayColumns = splitIntoColumns(delayLibrary);

  useEffect(() => {
    if (isTrayVisible && trayContext?.action) {
      setDisplayAction(trayContext.action);
    }
  }, [isTrayVisible, trayContext]);

  return (
    <div className={`dw-bc-rule ${removingRule ? "is-removing" : ""}`}>
        <div className="dw-bc-rule-head">
        <div>
          <h2>{rule.title || (rule.titleKey ? labels.t(rule.titleKey) : "")}</h2>
          <div className="dw-bc-rule-meta">
            <p>{labels.triggerPrefix}{resolveRuleTriggerLabel(rule, labels)}</p>
            <button type="button" className="dw-bc-rule-preview" onClick={() => onToggleRulePreview(rule.id)}>
              <span className="dw-bc-rule-preview-icon-shell">
                {previewingRuleId === rule.id ? <PauseIcon /> : <PlayIcon />}
              </span>
              <span>{labels.previewRule}</span>
            </button>
          </div>
        </div>
        <button type="button" className="dw-bc-link-muted" onClick={() => onRemoveRule(rule.id)}>{labels.removeRule}</button>
      </div>

      <div className="dw-bc-node-flow">
          {rule.nodes.map((node, index) => {
          const isEditing = trayContext?.ruleId === rule.id && trayContext?.action === node.id;
          const isRemoving = removingNodeIds[`${rule.id}:${node.id}`];
          return (
            <div key={node.id} className={`dw-bc-node-flow-item dw-bc-page-enter-slide ${isRemoving ? "is-removing" : ""}`} style={{ animationDelay: `${index * 0.08}s` }}>
              <div className="dw-bc-node-meta">
                <span className={`dw-bc-node-kind ${node.type === "variable" ? "is-variable" : ""}`}>
                  {node.type === "variable"
                    ? <DatabaseIcon className="dw-bc-node-kind-icon is-variable" />
                    : node.type === "delay"
                      ? <DelayIcon className="dw-bc-node-kind-icon is-delay" />
                      : <SpeakerIcon className="dw-bc-node-kind-icon is-asset" />}
                  {resolveRuleNodeKindLabel(node, labels)}
                </span>
                <div className="dw-bc-node-value-wrap">
                  <button
                    type="button"
                    className={`dw-bc-node-value ${node.type === "variable" ? "is-variable" : ""} ${isEditing ? "is-active" : ""}`}
                    onClick={() => onToggleTray(rule.id, node.id)}
                  >
                    {node.type === "variable"
                      ? resolveVariableNodeDisplayName(node, labels)
                      : (node.name || (node.nameKey ? labels.t(node.nameKey) : ""))}
                  </button>
                  <button type="button" className="dw-bc-node-remove" onClick={() => onRemoveNode(rule.id, node.id)}>
                    <CloseIcon />
                  </button>
                </div>
              </div>
              {index < rule.nodes.length - 1 ? <div className="dw-bc-node-sep">/</div> : null}
            </div>
          );
        })}

        <div className="dw-bc-node-flow-item is-add-action">
          {rule.nodes.length > 0 ? <div className="dw-bc-node-sep">/</div> : null}
          <button
            type="button"
            className={`dw-bc-node-add ${trayContext?.ruleId === rule.id && trayContext?.action === "add" ? "is-active" : ""}`}
            onClick={() => onToggleTray(rule.id, "add")}
          >
            <span className="dw-bc-inline-icon-shell">
              {trayContext?.ruleId === rule.id && trayContext?.action === "add" ? <CloseIcon /> : <PlusIcon />}
            </span>
            <span className="dw-bc-inline-button-copy">
              {trayContext?.ruleId === rule.id && trayContext?.action === "add" ? labels.cancelAddNode : labels.addNode}
            </span>
          </button>
        </div>
      </div>

      <AnimatedInlinePanel visible={isTrayVisible} panelRef={trayRef}>
        <div className="dw-bc-tray">
          <div className="dw-bc-tray-head">
            <span>{displayAction === "add" ? labels.addTrayTitle : labels.replaceTrayTitle}</span>
            <button type="button" className="dw-bc-icon-button" onClick={onCloseTray}>
              <CloseIcon />
            </button>
          </div>

          <div className="dw-bc-tray-tabs">
            <button type="button" className={`dw-bc-tray-tab ${trayCategory === "asset" ? "is-active" : ""}`} onClick={() => onSetTrayCategory("asset")}>
              <SpeakerIcon className="dw-bc-tray-tab-icon is-asset" />
              {labels.assetTab}
            </button>
            <button type="button" className={`dw-bc-tray-tab is-variable ${trayCategory === "variable" ? "is-active" : ""}`} onClick={() => onSetTrayCategory("variable")}>
              <DatabaseIcon className="dw-bc-tray-tab-icon is-variable" />
              {labels.variableTab}
            </button>
            <button type="button" className={`dw-bc-tray-tab is-delay ${trayCategory === "delay" ? "is-active" : ""}`} onClick={() => onSetTrayCategory("delay")}>
              <DelayIcon className="dw-bc-tray-tab-icon is-delay" />
              {labels.delayTab}
            </button>
          </div>

          {trayCategory === "asset" ? (
            <div className="dw-bc-tray-columns">
              {assetColumns.map((column, columnIndex) => (
                <div key={`asset-col-${columnIndex}`} className="dw-bc-tray-column">
                  {column.map((asset, rowIndex) => (
                    <button key={asset.name} type="button" className={`dw-bc-tray-item ${asset.isStationBound ? "is-station-bound" : "is-unbound-asset"} anim-stagger-slide-up`} style={{ animationDelay: `${rowIndex * 0.05}s` }} onClick={() => onAddAsset(rule.id, asset)}>
                      <span>{formatBroadcastAssetDisplayName(asset.name)}</span>
                      <span className={asset.isStationBound ? "dw-bc-tray-item-note is-station-bound" : "dw-bc-tray-item-note"}>
                        {asset.isStationBound ? "站名" : asset.desc}
                      </span>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          ) : trayCategory === "variable" ? (
            <div className="dw-bc-tray-columns">
              {variableColumns.map((column, columnIndex) => (
                <div key={`variable-col-${columnIndex}`} className="dw-bc-tray-column">
                  {column.map((variable) => (
                    <button
                      key={variable.id}
                      type="button"
                      className="dw-bc-tray-item is-variable anim-stagger-slide-up"
                      style={{ animationDelay: `${variableLibrary.findIndex((entry) => entry.id === variable.id) * 0.05}s` }}
                      onClick={() => onAddVariable(rule.id, variable)}
                    >
                      <span>{variable.name}</span>
                      <span>{variable.desc}</span>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          ) : (
            <div className="dw-bc-tray-columns">
              {delayColumns.map((column, columnIndex) => (
                <div key={`delay-col-${columnIndex}`} className="dw-bc-tray-column">
                  {column.map((delay) => (
                    <button key={delay.name} type="button" className="dw-bc-tray-item is-delay anim-stagger-slide-up" style={{ animationDelay: `${delayLibrary.findIndex((entry) => entry.name === delay.name) * 0.05}s` }} onClick={() => onAddDelay(rule.id, delay)}>
                      <span>{delay.name}</span>
                      <span>{delay.desc}</span>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          )}
        </div>
      </AnimatedInlinePanel>
    </div>
  );
}

export default function BroadcastWorkbenchPage({ pageEnterSequence = 0 }) {
  const { locale, t } = useNativeScheduleI18n();
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const delayLibrary = useMemo(
    () => DELAY_LIBRARY.map((delay) => ({ ...delay, name: t(delay.nameKey), desc: t(delay.descKey) })),
    [t]
  );
  const triggerOptions = useMemo(
    () => TRIGGER_OPTIONS.map((option) => ({ ...option, label: t(option.labelKey) })),
    [t]
  );
  const fallbackLineOptions = useMemo(
    () => LINE_OPTIONS.map((line) => ({ ...line, label: t(line.labelKey) })),
    [t]
  );
  const [activeTab, setActiveTab] = useState("sequence");
  const [renderedTab, setRenderedTab] = useState("sequence");
  const [tabStage, setTabStage] = useState("entered");
  const [pageEnterState, setPageEnterState] = useState("entered");
  const [rules, setRules] = useState([]);
  const [stations, setStations] = useState([]);
  const [turnbackPoints, setTurnbackPoints] = useState([]);
  const [trayContext, setTrayContext] = useState(null);
  const [trayCategory, setTrayCategory] = useState("asset");
  const [mappingTray, setMappingTray] = useState(null);
  const [stationBindingDraftsByLine, setStationBindingDraftsByLine] = useState({});
  const [bindingLangDraftsByLine, setBindingLangDraftsByLine] = useState({});
  const [disambiguationNamesByLine, setDisambiguationNamesByLine] = useState({});
  const [mappingBindFeedback, setMappingBindFeedback] = useState(null);
  const [catalogAssetLibrary, setCatalogAssetLibrary] = useState([]);
  const [previewingAssetName, setPreviewingAssetName] = useState("");
  const [previewingRuleId, setPreviewingRuleId] = useState("");
  const [broadcastPreviewVolume, setBroadcastPreviewVolume] = useState(80);
  const [isDraggingBroadcastVolume, setIsDraggingBroadcastVolume] = useState(false);
  const [broadcastDraftApplied, setBroadcastDraftApplied] = useState(false);
  const [broadcastDraftDirty, setBroadcastDraftDirty] = useState(false);
  const [isApplyingBroadcastConfig, setIsApplyingBroadcastConfig] = useState(false);
  const [broadcastApplyError, setBroadcastApplyError] = useState("");
  const [isAssetExplorerOpen, setIsAssetExplorerOpen] = useState(false);
  const [shouldRenderAssetExplorer, setShouldRenderAssetExplorer] = useState(false);
  const [assetExplorerStage, setAssetExplorerStage] = useState("closed");
  const [externalAssetBrowser, setExternalAssetBrowser] = useState(createEmptyExternalAssetBrowserState());
  const [selectedExternalFiles, setSelectedExternalFiles] = useState([]);
  const [currentExternalPath, setCurrentExternalPath] = useState("");
  const [lineOptions, setLineOptions] = useState(fallbackLineOptions);
  const [selectedLineId, setSelectedLineId] = useState(fallbackLineOptions[0]?.id ?? LINE_OPTIONS[0].id);
  const [bindingSlotHints, setBindingSlotHints] = useState([]);
  const [lineDropdownOpen, setLineDropdownOpen] = useState(false);
  const [isCreatingRule, setIsCreatingRule] = useState(false);
  const [newRuleTitle, setNewRuleTitle] = useState("");
  const [newRuleTriggerId, setNewRuleTriggerId] = useState(TRIGGER_OPTIONS[0].id);
  const [triggerDropdownOpen, setTriggerDropdownOpen] = useState(false);
  const [removingRuleIds, setRemovingRuleIds] = useState({});
  const [removingNodeIds, setRemovingNodeIds] = useState({});
  const pageRootRef = useRef(null);
  const trayRef = useRef(null);
  const bodyScrollRef = useRef(null);
  const bodyPadRef = useRef(null);
  const mappingBindingListRef = useRef(null);
  const previewVolumeTrackRef = useRef(null);
  const dropdownPortalHostRef = useRef(null);
  const removeTimersRef = useRef([]);
  const mappingBindFeedbackTimerRef = useRef(null);
  const mappingBindScrollFrameRef = useRef(0);
  const mappingBindTransformCleanupRef = useRef(null);
  const pageEnterTimerRef = useRef(null);
  const previewVolumeCommitTimerRef = useRef(null);
  const pendingPreviewVolumeRef = useRef(80);
  const hasBroadcastHydratedRef = useRef(false);
  const hasBackendLineHydratedRef = useRef(false);
  const hasBroadcastRulesHydratedRef = useRef(false);
  const lastHydratedLineIdRef = useRef("");
  const lastHydratedRulesLineIdRef = useRef("");
  const lineOptionsRef = useRef(lineOptions);
  const selectedLineIdRef = useRef(selectedLineId);
  const skipNextRulesSaveRef = useRef(false);
  const availableAssetLibrary = catalogAssetLibrary;
  const mappingAssetColumns = splitIntoColumns(availableAssetLibrary);
  const currentExternalFolders = Array.isArray(externalAssetBrowser?.folders) ? externalAssetBrowser.folders : [];
  const currentExternalFiles = Array.isArray(externalAssetBrowser?.files) ? externalAssetBrowser.files : [];
  const currentExternalAllowedExtensions = Array.isArray(externalAssetBrowser?.allowedExtensions) && externalAssetBrowser.allowedExtensions.length > 0
    ? externalAssetBrowser.allowedExtensions
    : [".wav", ".mp3", ".ogg"];
  const selectedLine = lineOptions.find((line) => line.id === selectedLineId) ?? lineOptions[0];
  const newRuleTrigger = triggerOptions.find((option) => option.id === newRuleTriggerId) ?? triggerOptions[0];
  const fallbackLanguageKey = useMemo(
    () => getBroadcastLocaleLanguageKey(locale),
    [locale]
  );
  const defaultBindingLanguageLabel = useMemo(
    () => resolveBroadcastLanguageLabel(fallbackLanguageKey, { t }),
    [fallbackLanguageKey, t]
  );
  lineOptionsRef.current = lineOptions;
  selectedLineIdRef.current = selectedLineId;
  const broadcastLabels = {
    t,
    sidebarTitle: t("broadcast.sidebar.title"),
    localAssets: t("broadcast.sidebar.localAssets"),
    assetFileName: t("broadcast.sidebar.fileName"),
    assetDuration: t("broadcast.sidebar.duration"),
    importAsset: t("broadcast.sidebar.import"),
    deleteAsset: t("broadcast.sidebar.deleteAsset"),
    deleteAllAssets: t("broadcast.sidebar.deleteAllAssets"),
    sequenceTab: t("broadcast.tabs.sequence"),
    mappingTab: t("broadcast.tabs.mapping"),
    lineLabel: t("broadcast.topbar.line"),
    createRule: t("broadcast.createRule.button"),
    createRuleTitle: t("broadcast.createRule.title"),
    ruleNameLabel: t("broadcast.createRule.name"),
    ruleNamePlaceholder: t("broadcast.createRule.namePlaceholder"),
    triggerLabel: t("broadcast.createRule.trigger"),
    saveRule: t("broadcast.createRule.save"),
    defaultRuleAfterDeparture: t("broadcast.rule.default.afterDeparture"),
    mappingTitle: t("broadcast.mapping.title"),
    autoBind: t("broadcast.mapping.autoBind"),
    mapLineHead: t("broadcast.mapping.head.line"),
    mapStationHead: t("broadcast.mapping.head.station"),
    mapAudioHead: t("broadcast.mapping.head.audio"),
    mapStatusHead: t("broadcast.mapping.head.status"),
    mapMissing: t("broadcast.mapping.missing"),
    mapChooseAudio: t("broadcast.mapping.chooseAudio"),
    mapReady: t("broadcast.mapping.ready"),
    mapReadyCount: t("broadcast.mapping.readyCount", { count: "{count}" }),
    mapConflictPending: t("broadcast.mapping.conflictPending", { count: "{count}" }),
    mapDisambiguate: t("broadcast.mapping.disambiguate", { count: "{count}" }),
    mapBindTitle: t("broadcast.mapping.bindTitle", { station: "{station}" }),
    mapDisambiguationTitle: t("broadcast.mapping.disambiguationTitle", { station: "{station}", count: "{count}" }),
    mapBindingTitle: t("broadcast.mapping.bindingTitle", { station: "{station}" }),
    mapCurrentBindings: t("broadcast.mapping.currentBindings"),
    mapLanguageLabel: t("broadcast.mapping.languageLabel"),
    mapLanguagePlaceholder: t("broadcast.mapping.languagePlaceholder"),
    mapLanguageHint: t("broadcast.mapping.languageHint"),
    mapBoundFeedback: t("broadcast.mapping.boundFeedback"),
    mapSystemLanguage: t("broadcast.mapping.systemLanguage"),
    mapSuggestedLabel: t("broadcast.mapping.suggestedLabel"),
    mapIgnoreCandidate: t("broadcast.mapping.ignoreCandidate"),
    mapConfirmDisambiguation: t("broadcast.mapping.confirmDisambiguation"),
    mapBindLanguageAudio: t("broadcast.mapping.bindLanguageAudio"),
    variableSlot: t("broadcast.variable.slot", { index: "{index}" }),
    unresolvedTurnback: t("broadcast.variable.unresolvedTurnback"),
    previewRule: t("broadcast.rule.preview"),
    applyConfig: t("broadcast.footer.apply"),
    appliedConfig: t("broadcast.footer.applied"),
    footerStatusApplied: t("broadcast.footer.statusApplied"),
    footerStatusDirty: t("broadcast.footer.statusDirty"),
    footerStatusClean: t("broadcast.footer.statusClean"),
    footerStatusApplying: t("broadcast.footer.statusApplying"),
    footerStatusMappingRequired: t("broadcast.footer.statusMappingRequired", { station: "{station}" }),
    footerLocateMapping: t("broadcast.footer.locateMapping"),
    previewVolume: t("broadcast.footer.previewVolume"),
    removeRule: t("broadcast.rule.remove"),
    triggerPrefix: t("broadcast.rule.triggerPrefix"),
    assetNode: t("broadcast.node.asset"),
    dynamicVariable: t("broadcast.node.dynamicVariable"),
    delayNode: t("broadcast.node.delay"),
    addNode: t("broadcast.node.add"),
    cancelAddNode: t("broadcast.node.cancelAdd"),
    addTrayTitle: t("broadcast.tray.addTitle"),
    replaceTrayTitle: t("broadcast.tray.replaceTitle"),
    assetTab: t("broadcast.tray.tab.asset"),
    variableTab: t("broadcast.tray.tab.variable"),
    delayTab: t("broadcast.tray.tab.delay")
  };
  const derivedBindingSlotHints = useMemo(
    () => deriveBindingSlotHintsFromStations(stations),
    [stations]
  );
  const effectiveBindingSlotHints = useMemo(
    () => mergeBindingSlotHints(derivedBindingSlotHints),
    [derivedBindingSlotHints]
  );
  const variableLibrary = useMemo(
    () => buildVariableLibrary(VARIABLE_LIBRARY, effectiveBindingSlotHints, broadcastLabels, turnbackPoints),
    [effectiveBindingSlotHints, broadcastLabels, turnbackPoints]
  );
  const trayAssetLibrary = useMemo(
    () => buildBroadcastTrayAssetLibrary(availableAssetLibrary, stations),
    [availableAssetLibrary, stations]
  );
  const variableColumns = useMemo(
    () => splitIntoColumns(variableLibrary),
    [variableLibrary]
  );
  const broadcastVariableMappingIssue = useMemo(
    () => buildBroadcastVariableMappingIssue(rules, stations),
    [rules, stations]
  );

  function getActiveBroadcastLineId() {
    return selectedLineIdRef.current || selectedLineId || "";
  }

  function getPrimaryStationAssetName(audios) {
    if (!Array.isArray(audios) || audios.length === 0) {
      return "";
    }

    const systemBinding = audios.find((entry) => entry.lang === defaultBindingLanguageLabel && entry.assetName);
    return systemBinding?.assetName || audios[0]?.assetName || "";
  }

  function persistStationBindingDraft(stationId, nextAudios, nextConflictAssets) {
    const lineId = getActiveBroadcastLineId();
    if (!lineId || !stationId) {
      return;
    }

    setStationBindingDraftsByLine((current) => ({
      ...current,
      [lineId]: {
        ...(current[lineId] || {}),
        [stationId]: {
          audios: Array.isArray(nextAudios) ? nextAudios : [],
          conflictAssets: Array.isArray(nextConflictAssets) ? nextConflictAssets : []
        }
      }
    }));
  }

  function updateBindingLanguageDraft(stationId, value) {
    const lineId = getActiveBroadcastLineId();
    if (!lineId || !stationId) {
      return;
    }

    setBindingLangDraftsByLine((current) => ({
      ...current,
      [lineId]: {
        ...(current[lineId] || {}),
        [stationId]: value
      }
    }));
  }

  function updateDisambiguationNameDraft(stationId, assetName, value) {
    const lineId = getActiveBroadcastLineId();
    if (!lineId || !stationId || !assetName) {
      return;
    }

    const draftKey = `${stationId}:${assetName}`;
    setDisambiguationNamesByLine((current) => ({
      ...current,
      [lineId]: {
        ...(current[lineId] || {}),
        [draftKey]: value
      }
    }));
  }

  function getBindingLanguageDraft(stationId) {
    const lineId = getActiveBroadcastLineId();
    const lineDrafts = bindingLangDraftsByLine[lineId];
    if (lineDrafts && Object.prototype.hasOwnProperty.call(lineDrafts, stationId)) {
      return lineDrafts[stationId];
    }
    return defaultBindingLanguageLabel;
  }

  function getDisambiguationNameDraft(stationId, assetName, fallbackValue = "") {
    const lineId = getActiveBroadcastLineId();
    const draftKey = `${stationId}:${assetName}`;
    const lineDrafts = disambiguationNamesByLine[lineId];
    if (lineDrafts && Object.prototype.hasOwnProperty.call(lineDrafts, draftKey)) {
      return lineDrafts[draftKey];
    }
    return fallbackValue;
  }

  async function syncStationBindings(stationId, audios) {
    const lineId = getActiveBroadcastLineId();
    if (!lineId || !stationId) {
      return;
    }

    try {
      await workbenchApi.saveBroadcastStationBindings?.({
        lineId,
        stationId,
        bindings: (Array.isArray(audios) ? audios : [])
          .filter((entry) => entry && typeof entry.assetName === "string" && entry.assetName)
          .map((entry, index) => ({
            lang: typeof entry.lang === "string" ? entry.lang : "",
            langIndex: normalizeLangIndex(entry?.langIndex ?? index + 1),
            assetName: entry.assetName
          }))
      });
      setStationBindingDraftsByLine((current) => {
        const lineDrafts = current[lineId];
        if (!lineDrafts || !Object.prototype.hasOwnProperty.call(lineDrafts, stationId)) {
          return current;
        }

        const nextLineDrafts = { ...lineDrafts };
        delete nextLineDrafts[stationId];

        if (Object.keys(nextLineDrafts).length === 0) {
          const next = { ...current };
          delete next[lineId];
          return next;
        }

        return {
          ...current,
          [lineId]: nextLineDrafts
        };
      });
    } catch (error) {
      console.error("[RT Broadcast Workbench] sync station binding failed", error);
    }
  }

  function scheduleMappingBindFeedback(stationId, assetName, lang) {
    if (mappingBindFeedbackTimerRef.current) {
      window.clearTimeout(mappingBindFeedbackTimerRef.current);
      mappingBindFeedbackTimerRef.current = null;
    }
    if (mappingBindScrollFrameRef.current) {
      window.cancelAnimationFrame(mappingBindScrollFrameRef.current);
      mappingBindScrollFrameRef.current = 0;
    }
    if (mappingBindTransformCleanupRef.current) {
      window.clearTimeout(mappingBindTransformCleanupRef.current);
      mappingBindTransformCleanupRef.current = null;
    }
    if (bodyPadRef.current) {
      bodyPadRef.current.style.transition = "";
      bodyPadRef.current.style.transform = "";
    }

    const token = `${stationId}:${assetName}:${lang}:${Date.now()}`;
    setMappingBindFeedback({ stationId, assetName, lang, token, phase: "chip" });

    mappingBindFeedbackTimerRef.current = window.setTimeout(() => {
      mappingBindScrollFrameRef.current = window.requestAnimationFrame(() => {
        mappingBindScrollFrameRef.current = 0;
        const scrollElement = bodyScrollRef.current;
        const contentElement = bodyPadRef.current;
        const bindingListElement = mappingBindingListRef.current;
        if (!scrollElement || !contentElement || !bindingListElement) {
          return;
        }

        const scrollRect = scrollElement.getBoundingClientRect();
        const bindingRect = bindingListElement.getBoundingClientRect();
        const targetTop = Math.max(0, scrollElement.scrollTop + bindingRect.top - scrollRect.top - 12);
        animateScrollTopWithTransform(scrollElement, contentElement, targetTop, 420, mappingBindTransformCleanupRef);
      });
    }, 16);

    removeTimersRef.current.push(window.setTimeout(() => {
      setMappingBindFeedback((current) => (current?.token === token ? null : current));
    }, 3400));
  }

  function applyBroadcastSnapshot(snapshot) {
    const backendLines = extractBackendLineOptions(snapshot);
    const hasBackendLines = backendLines.length > 0;
    const nextLineOptions = hasBackendLines
      ? backendLines
      : hasBackendLineHydratedRef.current
        ? lineOptionsRef.current
        : fallbackLineOptions;
    const fallbackSelectedLineId = nextLineOptions[0]?.id ?? "";
    const preservedSelectedLineId =
      selectedLineIdRef.current && nextLineOptions.some((line) => line.id === selectedLineIdRef.current)
        ? selectedLineIdRef.current
        : "";
    const nextSelectedLineId =
      typeof snapshot?.selectedLineId === "string"
      && nextLineOptions.some((line) => line.id === snapshot.selectedLineId)
        ? snapshot.selectedLineId
        : (preservedSelectedLineId || fallbackSelectedLineId);

    if (hasBackendLines) {
      hasBackendLineHydratedRef.current = true;
      setLineOptions(nextLineOptions);
      lastHydratedLineIdRef.current = nextSelectedLineId;
    } else if (!hasBackendLineHydratedRef.current) {
      setLineOptions(nextLineOptions);
    }

    setSelectedLineId(nextSelectedLineId);
    setBroadcastDraftApplied(Boolean(snapshot?.draftApplied));
    setBroadcastDraftDirty(Boolean(snapshot?.draftDirty));
    setBroadcastPreviewVolume(Number.isFinite(snapshot?.volume) ? snapshot.volume : 80);
    setIsApplyingBroadcastConfig(false);
    setBroadcastApplyError("");
    setTurnbackPoints(
      Array.isArray(snapshot?.turnbackPoints)
        ? snapshot.turnbackPoints.map((point) => ({
          index: Number.isFinite(Number(point?.index)) ? Number(point.index) : 0,
          stationId: typeof point?.stationId === "string" ? point.stationId : "",
          stationName: typeof point?.stationName === "string" ? point.stationName : "",
          resolved: Boolean(point?.resolved)
        }))
        : []
    );

    const hasBackendRules = Array.isArray(snapshot?.rules);
    const nextRules = cloneBroadcastRules(hasBackendRules ? snapshot.rules : []);
    skipNextRulesSaveRef.current = hasBackendRules;
    hasBroadcastRulesHydratedRef.current = true;
    lastHydratedRulesLineIdRef.current = nextSelectedLineId;
    setRules(nextRules);

    const nextCatalogAssetLibrary = Array.isArray(snapshot?.assets)
      ? snapshot.assets
        .filter((asset) => asset && typeof asset.name === "string" && asset.name)
        .map((asset) => ({
          name: asset.name,
          desc: asset.desc || asset.extension || "",
          length: asset.length || ""
        }))
      : [];
    setCatalogAssetLibrary(nextCatalogAssetLibrary);

    if (!Array.isArray(snapshot?.stations)) {
      return;
    }

    const assetNameSet = new Set(nextCatalogAssetLibrary.map((asset) => asset.name));
    const stationBindingsByStationId = new Map();
    const previousAudioLangByStationAndAsset = new Map();
    if (nextSelectedLineId && nextSelectedLineId === selectedLineIdRef.current) {
      stations.forEach((station) => {
        (Array.isArray(station?.audios) ? station.audios : []).forEach((audio) => {
          if (station?.id && audio?.assetName && audio?.lang) {
            previousAudioLangByStationAndAsset.set(`${station.id}:${audio.assetName}`, audio.lang);
          }
        });
      });
    }
    (Array.isArray(snapshot?.stationBindings) ? snapshot.stationBindings : [])
      .filter((binding) => binding && typeof binding.stationId === "string" && binding.stationId)
      .forEach((binding) => {
        const assetName = typeof binding.assetName === "string" ? binding.assetName : "";
        if (!assetName || !assetNameSet.has(assetName)) {
          return;
        }

        const currentBindings = stationBindingsByStationId.get(binding.stationId) || [];
        currentBindings.push({
          lang: typeof binding.lang === "string" && binding.lang
            ? binding.lang
            : (previousAudioLangByStationAndAsset.get(`${binding.stationId}:${assetName}`) || defaultBindingLanguageLabel),
          langIndex: normalizeLangIndex(binding.langIndex),
          assetName
        });
        stationBindingsByStationId.set(binding.stationId, currentBindings);
      });

    const lineDrafts = stationBindingDraftsByLine[nextSelectedLineId] || {};

    setStations(
      snapshot.stations.map((station) => {
        const backendAudios = Array.isArray(stationBindingsByStationId.get(station.id))
          ? stationBindingsByStationId.get(station.id)
          : [];
        const snapshotConflicts = Array.isArray(station?.conflictAssets)
          ? station.conflictAssets
            .filter((entry) => entry && typeof entry.assetName === "string" && entry.assetName)
            .map((entry) => ({
              assetName: entry.assetName,
              suggestedLang:
                typeof entry.suggestedLang === "string" && entry.suggestedLang
                  ? entry.suggestedLang
                  : extractBroadcastLanguageHint(entry.assetName, station.name, fallbackLanguageKey, broadcastLabels)
            }))
          : [];
        const override = lineDrafts[station.id];
        const audios = Array.isArray(override?.audios) ? override.audios : backendAudios;
        const conflictAssets = sortBroadcastConflictAssets(
          Array.isArray(override?.conflictAssets) ? override.conflictAssets : snapshotConflicts,
          station.name,
          fallbackLanguageKey,
          broadcastLabels
        );

        return {
          id: station.id,
          name: station.name,
          audios,
          conflictAssets,
          status: deriveBroadcastStationStatus(audios, conflictAssets)
        };
      })
    );
  }

  useEffect(() => () => {
    removeTimersRef.current.forEach((timer) => window.clearTimeout(timer));
    removeTimersRef.current = [];
    if (mappingBindFeedbackTimerRef.current) {
      window.clearTimeout(mappingBindFeedbackTimerRef.current);
      mappingBindFeedbackTimerRef.current = null;
    }
    if (mappingBindScrollFrameRef.current) {
      window.cancelAnimationFrame(mappingBindScrollFrameRef.current);
      mappingBindScrollFrameRef.current = 0;
    }
    if (mappingBindTransformCleanupRef.current) {
      window.clearTimeout(mappingBindTransformCleanupRef.current);
      mappingBindTransformCleanupRef.current = null;
    }
    if (previewVolumeCommitTimerRef.current) {
      window.clearTimeout(previewVolumeCommitTimerRef.current);
      previewVolumeCommitTimerRef.current = null;
    }
  }, []);

  function commitBroadcastPreviewVolume(nextVolume, immediate = false) {
    pendingPreviewVolumeRef.current = nextVolume;
    if (previewVolumeCommitTimerRef.current) {
      window.clearTimeout(previewVolumeCommitTimerRef.current);
      previewVolumeCommitTimerRef.current = null;
    }

    if (immediate) {
      workbenchApi.setBroadcastPreviewVolume?.(pendingPreviewVolumeRef.current);
      return;
    }

    previewVolumeCommitTimerRef.current = window.setTimeout(() => {
      previewVolumeCommitTimerRef.current = null;
      workbenchApi.setBroadcastPreviewVolume?.(pendingPreviewVolumeRef.current);
    }, 120);
  }

  useEffect(() => {
    const unsubscribe = workbenchApi.onBroadcastAssetPreviewStateChanged?.((payload) => {
      const assetName = payload?.assetName || "";
      const state = payload?.state || "";
      if (state === "ended" || state === "stopped" || state === "error") {
        setPreviewingAssetName((current) => (assetName && current && current !== assetName ? current : ""));
      }
    });

    return () => {
      unsubscribe?.();
    };
  }, [workbenchApi]);

  useEffect(() => {
    const unsubscribe = workbenchApi.onBroadcastRulePreviewStateChanged?.((payload) => {
      const ruleId = payload?.ruleId || "";
      const state = payload?.state || "";
      if (state === "ended" || state === "stopped" || state === "error") {
        setPreviewingRuleId((current) => (ruleId && current && current !== ruleId ? current : ""));
      }
    });

    return () => {
      unsubscribe?.();
    };
  }, [workbenchApi]);

  useEffect(() => {
    if (!isDraggingBroadcastVolume) {
      return undefined;
    }

    function updateVolumeFromClientX(clientX) {
      const track = previewVolumeTrackRef.current;
      if (!(track instanceof HTMLElement)) {
        return;
      }

      const rect = track.getBoundingClientRect();
      if (rect.width <= 0) {
        return;
      }

      const progress = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
      const nextVolume = Math.round(progress * 100);
      setBroadcastPreviewVolume(nextVolume);
      commitBroadcastPreviewVolume(nextVolume);
    }

    function handleMouseMove(event) {
      updateVolumeFromClientX(event.clientX);
    }

    function handleMouseUp() {
      commitBroadcastPreviewVolume(pendingPreviewVolumeRef.current, true);
      setIsDraggingBroadcastVolume(false);
    }

    window.addEventListener("mousemove", handleMouseMove);
    window.addEventListener("mouseup", handleMouseUp);
    return () => {
      window.removeEventListener("mousemove", handleMouseMove);
      window.removeEventListener("mouseup", handleMouseUp);
    };
  }, [isDraggingBroadcastVolume, workbenchApi]);

  useEffect(() => {
    let disposed = false;

    async function hydrateBroadcastSnapshot() {
      try {
        const snapshot = await workbenchApi.loadBroadcastSnapshot?.(selectedLineIdRef.current);
        if (disposed) {
          return;
        }

        applyBroadcastSnapshot(snapshot);

        if (extractBackendLineOptions(snapshot).length === 0) {
          try {
            const refreshedSnapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineIdRef.current);
            if (!disposed && extractBackendLineOptions(refreshedSnapshot).length > 0) {
              applyBroadcastSnapshot(refreshedSnapshot);
            }
          } catch (refreshError) {
            if (!disposed) {
              console.error("[RT Broadcast Workbench] backend hydrate refresh failed", refreshError);
            }
          }
        }

        hasBroadcastHydratedRef.current = true;
      } catch (error) {
        if (!disposed) {
          console.error("[RT Broadcast Workbench] backend hydrate failed", error);
        }
      }
    }

    hydrateBroadcastSnapshot();
    const unsubscribe = workbenchApi.onBroadcastSnapshotChanged?.((snapshot) => {
      if (!disposed) {
        applyBroadcastSnapshot(snapshot);
      }
    });

    return () => {
      disposed = true;
      unsubscribe?.();
    };
  }, [workbenchApi]);

  useEffect(() => {
    if (!hasBroadcastHydratedRef.current) {
      return undefined;
    }

    if (!selectedLineId || selectedLineId === lastHydratedLineIdRef.current) {
      return undefined;
    }

    let disposed = false;

    async function refreshBroadcastSnapshot() {
      try {
        const snapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineId);
        if (!disposed) {
          applyBroadcastSnapshot(snapshot);
        }
      } catch (error) {
        try {
          const fallbackSnapshot = await workbenchApi.refreshSnapshot?.();
          if (!disposed) {
            applyBroadcastSnapshot(fallbackSnapshot);
          }
        } catch (fallbackError) {
          if (!disposed) {
            console.error("[RT Broadcast Workbench] backend refresh failed", error, fallbackError);
          }
        }
      }
    }

    refreshBroadcastSnapshot();

    return () => {
      disposed = true;
    };
  }, [selectedLineId, workbenchApi]);

  useEffect(() => {
    const lineId = selectedLineId || selectedLineIdRef.current || "";
    if (!lineId) {
      setBindingSlotHints([]);
      return undefined;
    }

    let disposed = false;

    async function refreshBindingSlotHints() {
      try {
        const result = await workbenchApi.loadBroadcastBindingSlotHints?.(lineId);
        if (!disposed) {
          setBindingSlotHints(Array.isArray(result?.slotHints) ? result.slotHints : []);
        }
      } catch (error) {
        if (!disposed) {
          console.error("[RT Broadcast Workbench] load binding slot hints failed", error);
        }
      }
    }

    refreshBindingSlotHints();

    return () => {
      disposed = true;
    };
  }, [selectedLineId, workbenchApi]);

  useEffect(() => {
    if (!hasBroadcastRulesHydratedRef.current
      || !selectedLineId
      || selectedLineId !== lastHydratedRulesLineIdRef.current) {
      return undefined;
    }

    if (skipNextRulesSaveRef.current) {
      skipNextRulesSaveRef.current = false;
      return undefined;
    }

    const timer = window.setTimeout(async () => {
      try {
        await workbenchApi.saveBroadcastRules?.({
          lineId: selectedLineId,
          rules: cloneBroadcastRules(rules)
        });
      } catch (error) {
        console.error("[RT Broadcast Workbench] save rules failed", error);
      }
    }, 180);

    return () => {
      window.clearTimeout(timer);
    };
  }, [rules, selectedLineId, workbenchApi]);

  useEffect(() => {
    if (pageEnterSequence <= 0) {
      return undefined;
    }

    let disposed = false;
    const retryDelays = [0, 120, 360, 720];

    async function refreshBroadcastLinesOnEnter() {
      for (let index = 0; index < retryDelays.length; index += 1) {
        const delay = retryDelays[index];
        if (delay > 0) {
          await new Promise((resolve) => {
            window.setTimeout(resolve, delay);
          });
        }

        if (disposed) {
          return;
        }

        try {
          const snapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineIdRef.current || "");
          if (disposed || !snapshot) {
            return;
          }

          applyBroadcastSnapshot(snapshot);
          if (extractBackendLineOptions(snapshot).length > 0) {
            return;
          }
        } catch (error) {
          if (!disposed && index === retryDelays.length - 1) {
            console.error("[RT Broadcast Workbench] page-enter refresh failed", error);
          }
        }
      }
    }

    refreshBroadcastLinesOnEnter();

    return () => {
      disposed = true;
    };
  }, [pageEnterSequence, workbenchApi]);

  useLayoutEffect(() => {
    if (pageEnterSequence <= 0) {
      return undefined;
    }

    let outerRaf = 0;
    let innerRaf = 0;
    let cancelled = false;
    let attempts = 0;

    function isPageVisible() {
      const rootNode = pageRootRef.current;
      if (!(rootNode instanceof HTMLElement)) {
        return false;
      }

      const hostPage = rootNode.closest(".dw-native-workbench-page");
      const targetNode = hostPage instanceof HTMLElement ? hostPage : rootNode;
      const rect = targetNode.getBoundingClientRect();
      const computedStyle = window.getComputedStyle(targetNode);

      return (
        computedStyle.visibility !== "hidden" &&
        computedStyle.display !== "none" &&
        rect.width > 0 &&
        rect.height > 0
      );
    }

    function startWhenVisible() {
      outerRaf = window.requestAnimationFrame(() => {
        innerRaf = window.requestAnimationFrame(() => {
          if (cancelled) {
            return;
          }

          if (!isPageVisible() && attempts < 6) {
            attempts += 1;
            startWhenVisible();
            return;
          }

          setPageEnterState("playing");
          pageEnterTimerRef.current = window.setTimeout(() => {
            setPageEnterState("entered");
            pageEnterTimerRef.current = null;
          }, PAGE_ENTER_ANIMATION_MS);
        });
      });
    }

    if (pageEnterTimerRef.current) {
      window.clearTimeout(pageEnterTimerRef.current);
      pageEnterTimerRef.current = null;
    }

    setPageEnterState("armed");
    startWhenVisible();

    return () => {
      cancelled = true;
      if (outerRaf) {
        window.cancelAnimationFrame(outerRaf);
      }
      if (innerRaf) {
        window.cancelAnimationFrame(innerRaf);
      }
      if (pageEnterTimerRef.current) {
        window.clearTimeout(pageEnterTimerRef.current);
        pageEnterTimerRef.current = null;
      }
    };
  }, [pageEnterSequence]);

  useEffect(() => {
    if (!(trayContext || mappingTray) || !trayRef.current || typeof trayRef.current.scrollIntoView !== "function") {
      return;
    }

    const timer = window.setTimeout(() => {
      trayRef.current?.scrollIntoView({ block: "nearest" });
    }, 100);

    return () => window.clearTimeout(timer);
  }, [mappingTray, trayContext]);

  useEffect(() => {
    if (activeTab === renderedTab) {
      return undefined;
    }

    setTabStage("exiting");
    const timer = window.setTimeout(() => {
      setRenderedTab(activeTab);
      setTabStage("entering");
      const raf = window.requestAnimationFrame(() => {
        setTabStage("entered");
      });
      return () => window.cancelAnimationFrame(raf);
    }, TAB_TRANSITION_MS);

    return () => window.clearTimeout(timer);
  }, [activeTab, renderedTab]);

  useEffect(() => {
    let timer = null;
    let raf = null;

    if (isAssetExplorerOpen) {
      setShouldRenderAssetExplorer(true);
      setAssetExplorerStage("entering");
      raf = window.requestAnimationFrame(() => {
        setAssetExplorerStage("entered");
      });
    } else if (shouldRenderAssetExplorer) {
      setAssetExplorerStage("exiting");
      timer = window.setTimeout(() => {
        setShouldRenderAssetExplorer(false);
        setAssetExplorerStage("closed");
        setSelectedExternalFiles([]);
        setCurrentExternalPath("");
        setExternalAssetBrowser(createEmptyExternalAssetBrowserState());
      }, IMPORT_OVERLAY_TRANSITION_MS);
    }

    return () => {
      if (raf) {
        window.cancelAnimationFrame(raf);
      }
      if (timer) {
        window.clearTimeout(timer);
      }
    };
  }, [isAssetExplorerOpen, shouldRenderAssetExplorer]);

  function closeInlineMenus() {
    setTriggerDropdownOpen(false);
    setLineDropdownOpen(false);
  }

  function handleRootClick() {
    closeInlineMenus();
  }

  function toggleTray(ruleId, action) {
    closeInlineMenus();
    setMappingTray(null);
    if (trayContext?.ruleId === ruleId && trayContext?.action === action) {
      setTrayContext(null);
      return;
    }
    if (action === "add") {
      setTrayCategory("asset");
    } else {
      const targetRule = rules.find((rule) => rule.id === ruleId);
      const targetNode = targetRule?.nodes.find((node) => node.id === action);
      setTrayCategory(
        targetNode?.type === "variable"
          ? "variable"
          : targetNode?.type === "delay"
            ? "delay"
            : "asset"
      );
    }
    setTrayContext({ ruleId, action });
  }

  function handleAddNodeToRule(ruleId, nodeTemplate) {
    setRules((current) =>
      current.map((rule) => {
        if (rule.id !== ruleId) {
          return rule;
        }

        if (trayContext?.action && trayContext.action !== "add") {
          return {
            ...rule,
            nodes: rule.nodes.map((node) =>
              node.id === trayContext.action ? { ...nodeTemplate, id: node.id } : node
            )
          };
        }

        return {
          ...rule,
          nodes: [...rule.nodes, { ...nodeTemplate, id: `${Date.now()}-${Math.random().toString(36).slice(2, 6)}` }]
        };
      })
    );
    setTrayContext(null);
  }

  function handleRemoveNode(ruleId, nodeId) {
    const removalKey = `${ruleId}:${nodeId}`;
    if (removingNodeIds[removalKey]) {
      return;
    }

    setRemovingNodeIds((current) => ({ ...current, [removalKey]: true }));
    if (trayContext?.action === nodeId) {
      setTrayContext(null);
    }

    const timer = window.setTimeout(() => {
      setRules((current) =>
        current.map((rule) =>
          rule.id === ruleId ? { ...rule, nodes: rule.nodes.filter((node) => node.id !== nodeId) } : rule
        )
      );
      setRemovingNodeIds((current) => {
        const next = { ...current };
        delete next[removalKey];
        return next;
      });
    }, 220);

    removeTimersRef.current.push(timer);
  }

  function handleRemoveRule(ruleId) {
    if (removingRuleIds[ruleId]) {
      return;
    }

    setRemovingRuleIds((current) => ({ ...current, [ruleId]: true }));
    if (trayContext?.ruleId === ruleId) {
      setTrayContext(null);
    }

    const timer = window.setTimeout(() => {
      setRules((current) => current.filter((rule) => rule.id !== ruleId));
      setRemovingRuleIds((current) => {
        const next = { ...current };
        delete next[ruleId];
        return next;
      });
    }, 220);

    removeTimersRef.current.push(timer);
  }

  function handleCreateRule() {
    if (!newRuleTitle.trim()) {
      return;
    }

    setRules((current) => [
      ...current,
      {
        id: Date.now().toString(),
        title: newRuleTitle.trim(),
        triggerId: newRuleTrigger.id,
        trigger: newRuleTrigger.label,
        nodes: []
      }
    ]);
    setIsCreatingRule(false);
    setNewRuleTitle("");
    setNewRuleTriggerId(TRIGGER_OPTIONS[0].id);
    setTriggerDropdownOpen(false);
  }

  async function handleBindStation(stationId, assetName) {
    const targetStation = stations.find((station) => station.id === stationId);
    if (!targetStation || !assetName) {
      return;
    }

    const nextLang = (getBindingLanguageDraft(stationId) || "").trim() || defaultBindingLanguageLabel;
    const nextAudios = [
      ...targetStation.audios.filter((entry) => entry.lang !== nextLang),
      { lang: nextLang, assetName }
    ];
    const nextStation = {
      ...targetStation,
      audios: nextAudios,
      conflictAssets: [],
      status: deriveBroadcastStationStatus(nextAudios, [])
    };

    setStations((current) => current.map((station) => (station.id === stationId ? nextStation : station)));
    persistStationBindingDraft(stationId, nextStation.audios, nextStation.conflictAssets);
    updateBindingLanguageDraft(stationId, nextLang);
    scheduleMappingBindFeedback(stationId, assetName, nextLang);
    await syncStationBindings(stationId, nextStation.audios);
    setMappingTray(stationId);
  }

  async function handleRemoveStationAudio(stationId, lang) {
    const targetStation = stations.find((station) => station.id === stationId);
    if (!targetStation) {
      return;
    }

    const nextAudios = targetStation.audios.filter((entry) => entry.lang !== lang);
    const nextStation = {
      ...targetStation,
      audios: nextAudios,
      conflictAssets: [],
      status: deriveBroadcastStationStatus(nextAudios, [])
    };

    setStations((current) => current.map((station) => (station.id === stationId ? nextStation : station)));
    persistStationBindingDraft(stationId, nextStation.audios, nextStation.conflictAssets);
    await syncStationBindings(stationId, nextStation.audios);
    setMappingTray(null);
  }

  function handleDiscardConflict(stationId, assetName) {
    const targetStation = stations.find((station) => station.id === stationId);
    if (!targetStation) {
      return;
    }

    const nextConflictAssets = targetStation.conflictAssets.filter((entry) => entry.assetName !== assetName);
    const nextStation = {
      ...targetStation,
      conflictAssets: nextConflictAssets,
      status: deriveBroadcastStationStatus(targetStation.audios, nextConflictAssets)
    };

    setStations((current) => current.map((station) => (station.id === stationId ? nextStation : station)));
    persistStationBindingDraft(stationId, nextStation.audios, nextStation.conflictAssets);
  }

  async function handleResolveStationConflicts(stationId) {
    const targetStation = stations.find((station) => station.id === stationId);
    if (!targetStation || targetStation.conflictAssets.length === 0) {
      return;
    }

    const existingAudios = Array.isArray(targetStation.audios)
      ? targetStation.audios.filter((entry) => entry && entry.assetName)
      : [];
    const existingAssetNames = new Set(existingAudios.map((entry) => entry.assetName));
    const resolvedAudios = targetStation.conflictAssets
      .filter((entry) => entry && entry.assetName && !existingAssetNames.has(entry.assetName))
      .map((entry) => ({
        lang: (
          getDisambiguationNameDraft(
            stationId,
            entry.assetName,
            extractBroadcastLanguageHint(entry.assetName, targetStation.name, fallbackLanguageKey, broadcastLabels)
          ) || ""
        ).trim() || defaultBindingLanguageLabel,
        assetName: entry.assetName
      }));
    const nextAudios = [...existingAudios, ...resolvedAudios];
    const nextStation = {
      ...targetStation,
      audios: nextAudios,
      conflictAssets: [],
      status: deriveBroadcastStationStatus(nextAudios, [])
    };

    setStations((current) => current.map((station) => (station.id === stationId ? nextStation : station)));
    persistStationBindingDraft(stationId, nextStation.audios, nextStation.conflictAssets);
    setMappingTray(null);
    await syncStationBindings(stationId, nextStation.audios);
  }

  async function loadExternalAssetBrowser(path = "") {
    try {
      const browserSnapshot = await workbenchApi.loadBroadcastAssetBrowser?.(path || currentExternalPath || "");
      if (!browserSnapshot) {
        return;
      }

      setExternalAssetBrowser(browserSnapshot);
      setCurrentExternalPath(browserSnapshot.currentPath || "");
    } catch (error) {
      console.error("[RT Broadcast Workbench] load asset browser failed", error);
    }
  }

  function handleImportAssetDirectory() {
    closeInlineMenus();
    setIsAssetExplorerOpen(true);
    loadExternalAssetBrowser(currentExternalPath);
  }

  function resetAssetPreviewState(assetName = "") {
    setPreviewingAssetName((current) => (assetName && current && current !== assetName ? current : ""));
  }

  async function handleAssetPreviewToggle(assetName) {
    if (!assetName) {
      return;
    }

    if (previewingAssetName === assetName) {
      try {
        await workbenchApi.stopBroadcastAssetPreview?.(assetName);
      } catch (error) {
        console.error("[RT Broadcast Workbench] stop asset preview failed", error);
      }
      resetAssetPreviewState(assetName);
      return;
    }

    try {
      await workbenchApi.playBroadcastAssetPreview?.(assetName);
    } catch (error) {
      console.error("[RT Broadcast Workbench] play asset preview failed", error);
    }

    setPreviewingAssetName(assetName);
  }

  async function handleRulePreviewToggle(ruleId) {
    if (!ruleId) {
      return;
    }

    if (previewingRuleId === ruleId) {
      try {
        await workbenchApi.stopBroadcastRulePreview?.(ruleId);
      } catch (error) {
        console.error("[RT Broadcast Workbench] stop rule preview failed", error);
      }
      setPreviewingRuleId("");
      return;
    }

    try {
      await workbenchApi.playBroadcastRulePreview?.({
        lineId: selectedLineIdRef.current || "",
        ruleId,
        volume: broadcastPreviewVolume
      });
    } catch (error) {
      console.error("[RT Broadcast Workbench] play rule preview failed", error);
    }

    setPreviewingRuleId(ruleId);
  }

  function handleBroadcastPreviewVolumeMouseDown(event) {
    const track = previewVolumeTrackRef.current;
    if (!(track instanceof HTMLElement)) {
      return;
    }

    const rect = track.getBoundingClientRect();
    if (rect.width <= 0) {
      return;
    }

    const progress = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
    const nextVolume = Math.round(progress * 100);
    setBroadcastPreviewVolume(nextVolume);
    commitBroadcastPreviewVolume(nextVolume);
    setIsDraggingBroadcastVolume(true);
  }

  function removeAssetFromUi(assetName) {
    if (!assetName) {
      return;
    }

    setCatalogAssetLibrary((current) => current.filter((asset) => asset.name !== assetName));
    setStations((current) =>
      current.map((station) =>
        ({
          ...station,
          audios: station.audios.filter((entry) => entry.assetName !== assetName),
          conflictAssets: station.conflictAssets.filter((entry) => entry.assetName !== assetName),
          status: deriveBroadcastStationStatus(
            station.audios.filter((entry) => entry.assetName !== assetName),
            station.conflictAssets.filter((entry) => entry.assetName !== assetName)
          )
        })
      )
    );
    setStationBindingDraftsByLine((current) => {
      const next = { ...current };
      Object.keys(next).forEach((lineId) => {
        const lineDrafts = next[lineId];
        if (!lineDrafts) {
          return;
        }

        const nextLineDrafts = { ...lineDrafts };
        Object.keys(nextLineDrafts).forEach((stationId) => {
          const stationDraft = nextLineDrafts[stationId];
          if (!stationDraft) {
            return;
          }

          nextLineDrafts[stationId] = {
            audios: Array.isArray(stationDraft.audios)
              ? stationDraft.audios.filter((entry) => entry.assetName !== assetName)
              : [],
            conflictAssets: Array.isArray(stationDraft.conflictAssets)
              ? stationDraft.conflictAssets.filter((entry) => entry.assetName !== assetName)
              : []
          };
        });
        next[lineId] = nextLineDrafts;
      });
      return next;
    });
    setRules((current) =>
      current.map((rule) => ({
        ...rule,
        nodes: rule.nodes.filter((node) => !(node.type === "asset" && node.name === assetName))
      }))
    );
    resetAssetPreviewState(assetName);
  }

  async function handleDeleteAsset(assetName) {
    if (!assetName) {
      return;
    }

    try {
      if (previewingAssetName === assetName) {
        await workbenchApi.stopBroadcastAssetPreview?.(assetName);
      }
      const result = await workbenchApi.deleteBroadcastAsset?.(assetName);
      if (!result?.success) {
        return;
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] delete asset failed", error);
      return;
    }

    removeAssetFromUi(assetName);
  }

  async function handleDeleteAllAssets() {
    try {
      if (previewingAssetName) {
        await workbenchApi.stopBroadcastAssetPreview?.(previewingAssetName);
      }
      const result = await workbenchApi.deleteAllBroadcastAssets?.();
      if (!result?.success) {
        return;
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] delete all assets failed", error);
      return;
    }

    setCatalogAssetLibrary([]);
    setStationBindingDraftsByLine({});
    setStations((current) => current.map((station) => ({
      ...station,
      audios: [],
      conflictAssets: [],
      status: "missing"
    })));
    setRules((current) =>
      current.map((rule) => ({
        ...rule,
        nodes: rule.nodes.filter((node) => node.type !== "asset")
      }))
    );
    resetAssetPreviewState();
  }

  async function handleAutoBindStations() {
    if (!selectedLineIdRef.current) {
      return;
    }

    try {
      await workbenchApi.autoBindBroadcastStationMappings?.(selectedLineIdRef.current);
      setStationBindingDraftsByLine((current) => {
        const next = { ...current };
        delete next[selectedLineIdRef.current];
        return next;
      });
      setBindingLangDraftsByLine((current) => {
        const next = { ...current };
        delete next[selectedLineIdRef.current];
        return next;
      });
      setDisambiguationNamesByLine((current) => {
        const next = { ...current };
        delete next[selectedLineIdRef.current];
        return next;
      });
      const refreshedSnapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineIdRef.current);
      if (refreshedSnapshot) {
        applyBroadcastSnapshot(refreshedSnapshot);
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] auto bind station mappings failed", error);
    }
  }

  function handleCloseAssetExplorer() {
    setIsAssetExplorerOpen(false);
  }

  function handleExternalPathChange(path) {
    loadExternalAssetBrowser(path);
  }

  function resolveExternalFolderTargetPath(folderName) {
    if (!currentExternalPath) {
      return folderName;
    }

    return `${currentExternalPath}${currentExternalPath.endsWith("\\") ? "" : "\\"}${folderName}\\`;
  }

  function handleExternalBack() {
    if (!externalAssetBrowser?.parentPath) {
      return;
    }

    loadExternalAssetBrowser(externalAssetBrowser.parentPath);
  }

  function handleToggleExternalFile(fileId) {
    setSelectedExternalFiles((current) =>
      current.includes(fileId)
        ? current.filter((id) => id !== fileId)
        : [...current, fileId]
    );
  }

  function handleToggleAllExternalFiles() {
    const currentViewIds = currentExternalFiles.map((file) => file.id);
    const allSelected = currentViewIds.length > 0 && currentViewIds.every((id) => selectedExternalFiles.includes(id));

    if (allSelected) {
      setSelectedExternalFiles((current) => current.filter((id) => !currentViewIds.includes(id)));
      return;
    }

    setSelectedExternalFiles((current) => Array.from(new Set([...current, ...currentViewIds])));
  }

  async function handleImportSelectedExternalFiles() {
    if (selectedExternalFiles.length === 0) {
      return;
    }

    try {
      const result = await workbenchApi.importBroadcastExternalAssets?.({
        currentPath: currentExternalPath,
        selectedPaths: selectedExternalFiles
      });

      if (result?.success) {
        handleCloseAssetExplorer();
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] import external assets failed", error);
    }
  }

  async function handleApplyBroadcastConfig() {
    if (isApplyingBroadcastConfig || broadcastDraftApplied && !broadcastDraftDirty || broadcastVariableMappingIssue) {
      return;
    }

    setIsApplyingBroadcastConfig(true);
    setBroadcastApplyError("");

    try {
      const result = await workbenchApi.applyBroadcastConfig?.({
        lineId: selectedLineIdRef.current || ""
      });

      if (result?.success) {
        if (result.snapshot) {
          applyBroadcastSnapshot(result.snapshot);
          return;
        }

        const refreshedSnapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineIdRef.current || "");
        applyBroadcastSnapshot(refreshedSnapshot);
        return;
      }

      setIsApplyingBroadcastConfig(false);
      setBroadcastApplyError(result?.error || "Apply failed");
    } catch (error) {
      setIsApplyingBroadcastConfig(false);
      setBroadcastApplyError(error instanceof Error ? error.message : "Apply failed");
    }
  }

  function handleLocateBroadcastMappingIssue() {
    if (!broadcastVariableMappingIssue?.stationId) {
      return;
    }

    setTrayContext(null);
    setActiveTab("mapping");
    setMappingTray(broadcastVariableMappingIssue.stationId);
  }

  const isBroadcastConfigApplied = broadcastDraftApplied && !broadcastDraftDirty;
  const broadcastFooterTone = broadcastApplyError
    ? "error"
    : broadcastVariableMappingIssue
      ? "warning"
    : isBroadcastConfigApplied
      ? "applied"
      : broadcastDraftDirty
        ? "warning"
        : "neutral";
  const broadcastFooterText = broadcastApplyError
    ? broadcastApplyError
    : broadcastVariableMappingIssue
      ? broadcastLabels.footerStatusMappingRequired
        .replace("{station}", broadcastVariableMappingIssue.stationName || "-")
    : isApplyingBroadcastConfig
      ? broadcastLabels.footerStatusApplying
      : isBroadcastConfigApplied
        ? broadcastLabels.footerStatusApplied
        : broadcastDraftDirty
          ? broadcastLabels.footerStatusDirty
          : broadcastLabels.footerStatusClean;
  const broadcastApplyButtonLabel = isApplyingBroadcastConfig
    ? broadcastLabels.footerStatusApplying
    : broadcastVariableMappingIssue
      ? broadcastLabels.footerLocateMapping
    : isBroadcastConfigApplied
      ? broadcastLabels.appliedConfig
      : broadcastLabels.applyConfig;

  return (
    <div ref={pageRootRef} className={`dw-bc-page is-page-enter-${pageEnterState}`} onClick={handleRootClick}>
      <div className="dw-bc-shell">
        <div className="dw-bc-shell-entry dw-bc-page-enter-shell origin-bottom">
        <aside className="dw-bc-sidebar">
          <header className="dw-bc-sidebar-head">
            <h1>{broadcastLabels.sidebarTitle}</h1>
          </header>

          <div className="dw-bc-sidebar-tools">
            <span>{broadcastLabels.localAssets}</span>
            <div className="dw-bc-sidebar-icons">
              <button type="button" className="dw-bc-sidebar-clear-button" onClick={handleDeleteAllAssets}>
                {broadcastLabels.deleteAllAssets}
              </button>
            </div>
          </div>

          <div className="dw-bc-asset-head">
            <div>{broadcastLabels.assetFileName}</div>
            <div>
              <span className="dw-bc-asset-time-head">{broadcastLabels.assetDuration}</span>
              <span className="dw-bc-asset-delete-spacer" />
            </div>
          </div>

          <WorkbenchScrollArea className="dw-bc-asset-list" metricsKey={availableAssetLibrary.length}>
            {availableAssetLibrary.map((asset, index) => (
              <div key={asset.name} className="dw-bc-asset-row dw-bc-page-enter-slide" style={{ animationDelay: `${index * 0.05}s` }}>
                <button
                  type="button"
                  className={`dw-bc-asset-play ${previewingAssetName === asset.name ? "is-previewing" : ""}`}
                  onClick={() => handleAssetPreviewToggle(asset.name)}
                >
                  <span className="dw-bc-asset-play-icon-shell">
                    {previewingAssetName === asset.name ? <PauseIcon /> : <PlayIcon />}
                  </span>
                </button>
                <div className="dw-bc-asset-copy-wrap">
                  <div className="dw-bc-asset-copy">
                    <div className="dw-bc-asset-name">{formatBroadcastAssetDisplayName(asset.name)}</div>
                    <div className="dw-bc-asset-desc">{asset.desc}</div>
                  </div>
                  <button
                    type="button"
                    className="dw-bc-asset-copy-button"
                    onClick={() => handleAssetPreviewToggle(asset.name)}
                    aria-label={asset.name}
                  />
                </div>
                <div className="dw-bc-asset-meta">
                  <div className="dw-bc-asset-time">{asset.length}</div>
                  <button type="button" className="dw-bc-asset-delete" onClick={() => handleDeleteAsset(asset.name)} aria-label={`${broadcastLabels.deleteAsset} ${asset.name}`}>
                    <TrashIcon />
                  </button>
                </div>
              </div>
            ))}
          </WorkbenchScrollArea>

          <footer className="dw-bc-sidebar-foot">
            <button type="button" className="dw-bc-outline-button" onClick={handleImportAssetDirectory}>
              <span className="dw-bc-inline-icon-shell dw-bc-outline-button-icon-shell">
                <PlusIcon />
              </span>
              <span className="dw-bc-inline-button-copy">{broadcastLabels.importAsset}</span>
            </button>
          </footer>
        </aside>

        <section className="dw-bc-main">
          <div className="dw-bc-main-header">
            <div className="dw-bc-tabs">
              <button type="button" className={`dw-bc-tab ${activeTab === "sequence" ? "is-active" : ""}`} onClick={() => { setActiveTab("sequence"); setMappingTray(null); }}>
                {broadcastLabels.sequenceTab}
              </button>
              <button type="button" className={`dw-bc-tab ${activeTab === "mapping" ? "is-active" : ""}`} onClick={() => { setActiveTab("mapping"); setTrayContext(null); }}>
                {broadcastLabels.mappingTab}
              </button>
            </div>

            <div className="dw-bc-main-tools">
              <WorkbenchDropdown
                open={lineDropdownOpen}
                onOpenChange={(next) => {
                setTriggerDropdownOpen(false);
                  setLineDropdownOpen(next);
                }}
                onSelect={(value) => {
                  setSelectedLineId(value);
                  setLineDropdownOpen(false);
                }}
                options={lineOptions.map((line) => ({
                  key: line.id,
                  value: line.id,
                  label: line.label,
                  active: line.id === selectedLineId
                }))}
                value={selectedLine.label}
                label={broadcastLabels.lineLabel}
                className="dw-bc-line-picker is-line"
                title={selectedLine.label}
                variant="field"
                positioning="portal"
                portalHostRef={dropdownPortalHostRef}
              />
            </div>
          </div>

          <WorkbenchScrollArea className="dw-bc-body" externalScrollRef={bodyScrollRef} metricsKey={`${renderedTab}:${rules.length}:${stations.length}:${Boolean(isCreatingRule)}:${mappingTray || ""}:${trayContext?.ruleId || ""}:${trayContext?.action || ""}`}>
            <div className="dw-bc-body-pad" ref={bodyPadRef}>
              <div className={`dw-bc-tab-panel is-${tabStage}`}>
                <div className="dw-bc-scene-entry dw-bc-page-enter-scene origin-bottom">
                {renderedTab === "sequence" ? (
                  <div className={`dw-bc-tab-scene dw-bc-tab-scene-sequence is-${tabStage}`}>
                  {rules.map((rule, index) => (
                    <div key={rule.id} className="dw-bc-page-enter-slide-up" style={{ animationDelay: `${index * 0.15}s` }}>
                      <SequenceRule
                        rule={rule}
                        trayContext={trayContext}
                        trayCategory={trayCategory}
                        removingRule={Boolean(removingRuleIds[rule.id])}
                        removingNodeIds={removingNodeIds}
                        previewingRuleId={previewingRuleId}
                        onToggleTray={toggleTray}
                        onToggleRulePreview={handleRulePreviewToggle}
                        onRemoveNode={handleRemoveNode}
                        onRemoveRule={handleRemoveRule}
                        onSetTrayCategory={setTrayCategory}
                        onCloseTray={() => setTrayContext(null)}
                        onAddAsset={(ruleId, asset) => handleAddNodeToRule(ruleId, { name: asset.name, desc: broadcastLabels.assetNode, descKey: "broadcast.node.asset", type: "asset" })}
                        onAddVariable={(ruleId, variable) => handleAddNodeToRule(ruleId, {
                          name: variable.name,
                          nameKey: variable.nameKey,
                          desc: broadcastLabels.dynamicVariable,
                          descKey: "broadcast.node.dynamicVariable",
                          type: "variable",
                          langIndex: normalizeLangIndex(variable.langIndex)
                        })}
                        onAddDelay={(ruleId, delay) => handleAddNodeToRule(ruleId, { name: delay.name, desc: broadcastLabels.delayNode, descKey: "broadcast.node.delay", type: "delay", delaySeconds: delay.delaySeconds || 0 })}
                        trayRef={trayRef}
                        assetLibrary={trayAssetLibrary}
                        variableLibrary={variableLibrary}
                        delayLibrary={delayLibrary}
                        labels={broadcastLabels}
                      />
                    </div>
                  ))}
                  <div className="dw-bc-create-block">
                    <div className={`dw-bc-create-button-shell ${isCreatingRule ? "is-hidden" : "is-visible"}`}>
                      <button type="button" className="dw-bc-create-button" onClick={() => { setIsCreatingRule(true); setTrayContext(null); setMappingTray(null); }}>
                        <span className="dw-bc-create-button-icon-shell">
                          <PlusIcon />
                        </span>
                        <span className="dw-bc-create-button-copy">{broadcastLabels.createRule}</span>
                      </button>
                    </div>
                    <AnimatedInlinePanel visible={isCreatingRule} className="dw-bc-create-panel">
                      <div className="dw-bc-create-form">
                        <button type="button" className="dw-bc-icon-button is-corner" onClick={() => { setIsCreatingRule(false); setTriggerDropdownOpen(false); }}>
                          <CloseIcon />
                        </button>
                        <h3>{broadcastLabels.createRuleTitle}</h3>
                        <div className="dw-bc-form-field">
                          <label>{broadcastLabels.ruleNameLabel}</label>
                          <input type="text" value={newRuleTitle} placeholder={broadcastLabels.ruleNamePlaceholder} onClick={(event) => event.stopPropagation()} onChange={(event) => setNewRuleTitle(event.target.value)} />
                        </div>
                        <div className="dw-bc-form-field is-dropdown">
                          <label>{broadcastLabels.triggerLabel}</label>
                          <WorkbenchDropdown
                            open={triggerDropdownOpen}
                            onOpenChange={(next) => {
                              setLineDropdownOpen(false);
                              setTriggerDropdownOpen(next);
                            }}
                            onSelect={(value) => {
                              setNewRuleTriggerId(value);
                              setTriggerDropdownOpen(false);
                            }}
                            options={triggerOptions.map((option) => ({
                              key: option.id,
                              value: option.id,
                              label: option.label,
                              active: option.id === newRuleTriggerId
                            }))}
                            value={newRuleTrigger.label}
                            className="dw-bc-form-dropdown"
                            variant="field"
                            positioning="portal"
                            portalHostRef={dropdownPortalHostRef}
                          />
                        </div>
                        <button type="button" className="dw-bc-primary-button" onClick={handleCreateRule}>
                          {broadcastLabels.saveRule}
                        </button>
                      </div>
                    </AnimatedInlinePanel>
                  </div>
                  </div>
                ) : (
                  <div className={`dw-bc-mapping dw-bc-tab-scene dw-bc-tab-scene-mapping is-${tabStage}`}>
                    <div className="dw-bc-mapping-head">
                      <div>
                        <h2>{broadcastLabels.mappingTitle}</h2>
                      </div>
                      <button type="button" className="dw-bc-secondary-button" onClick={handleAutoBindStations}>{broadcastLabels.autoBind}</button>
                    </div>

                    <div className="dw-bc-map-table-head">
                      <div className="dw-bc-map-col is-id">{broadcastLabels.mapLineHead}</div>
                      <div className="dw-bc-map-col is-name">{broadcastLabels.mapStationHead}</div>
                      <div className="dw-bc-map-col is-audio">{broadcastLabels.mapAudioHead}</div>
                      <div className="dw-bc-map-col is-status">{broadcastLabels.mapStatusHead}</div>
                    </div>

                    {stations.map((station, index) => {
                      const isMissing = station.status === "missing";
                      const isConflict = station.status === "conflict";
                      const isReady = station.status === "ready";
                      const isMappingTrayVisible = mappingTray === station.id;
                      return (
                        <div key={station.id}>
                          <div className="dw-bc-map-row dw-bc-page-enter-slide-up" style={{ animationDelay: `${index * 0.08}s` }}>
                            <div className={`dw-bc-map-id dw-bc-map-col is-id ${isMissing ? "is-missing" : ""}`}>{selectedLine.label}</div>
                            <div className={`dw-bc-map-name dw-bc-map-col is-name ${isMissing ? "is-missing" : ""} ${isConflict ? "is-conflict" : ""}`}>{station.name}</div>
                            <div className="dw-bc-map-col is-audio">
                              {isReady ? (
                                <div className="dw-bc-map-audio-stack">
                                  {station.audios.map((audio) => (
                                    <button
                                      key={`${station.id}:${audio.lang}:${audio.assetName}`}
                                      type="button"
                                      className="dw-bc-map-audio-binding"
                                      onClick={() => { setMappingTray(isMappingTrayVisible ? null : station.id); setTrayContext(null); }}
                                    >
                                      <span className="dw-bc-map-audio-lang">{audio.lang}</span>
                                      <span className="dw-bc-map-audio-link-core">
                                        <span className="dw-bc-map-audio-name">{formatBroadcastAssetDisplayName(audio.assetName)}</span>
                                      </span>
                                    </button>
                                  ))}
                                </div>
                              ) : isConflict ? (
                                <span className="dw-bc-map-conflict-note">{t("broadcast.mapping.conflictPending", { count: station.conflictAssets.length })}</span>
                              ) : (
                                <span className="dw-bc-map-missing">{broadcastLabels.mapMissing}</span>
                              )}
                            </div>
                            <div className="dw-bc-map-status dw-bc-map-col is-status">
                              {isReady ? (
                                <span className="dw-bc-ready">{t("broadcast.mapping.readyCount", { count: station.audios.length })}</span>
                              ) : isConflict ? (
                                <button type="button" className="dw-bc-map-button is-conflict" onClick={() => { setMappingTray(isMappingTrayVisible ? null : station.id); setTrayContext(null); }}>
                                  {t("broadcast.mapping.disambiguate", { count: station.conflictAssets.length })}
                                </button>
                              ) : (
                                <button type="button" className="dw-bc-map-button" onClick={() => { setMappingTray(isMappingTrayVisible ? null : station.id); setTrayContext(null); }}>
                                  {broadcastLabels.mapBindLanguageAudio}
                                </button>
                              )}
                            </div>
                          </div>

                          <AnimatedInlinePanel visible={isMappingTrayVisible} panelRef={trayRef}>
                            <div className={`dw-bc-tray ${isMissing ? "is-missing" : ""} ${isConflict ? "is-conflict" : ""}`}>
                              <div className="dw-bc-tray-head">
                                <span>{isConflict ? t("broadcast.mapping.disambiguationTitle", { station: station.name, count: station.conflictAssets.length }) : t("broadcast.mapping.bindingTitle", { station: station.name })}</span>
                                <button type="button" className="dw-bc-icon-button" onClick={() => setMappingTray(null)}>
                                  <CloseIcon />
                                </button>
                              </div>
                              {isConflict ? (
                                <div className="dw-bc-map-disambiguation-list">
                                  {station.audios.length > 0 ? (
                                    <div className="dw-bc-map-binding-list">
                                      <span className="dw-bc-map-binding-list-title">{broadcastLabels.mapCurrentBindings}</span>
                                      <div className="dw-bc-map-binding-tags">
                                        {station.audios.map((audio) => (
                                          <div key={`${station.id}:${audio.lang}:${audio.assetName}`} className="dw-bc-map-binding-tag">
                                            <span className="dw-bc-map-binding-tag-lang">{`${audio.lang}:`}</span>
                                            <span className="dw-bc-map-binding-tag-name">{formatBroadcastAssetDisplayName(audio.assetName)}</span>
                                          </div>
                                        ))}
                                      </div>
                                    </div>
                                  ) : null}
                                  {station.conflictAssets.map((entry, conflictIndex) => (
                                    <div key={`${station.id}:${entry.assetName}`} className="dw-bc-map-disambiguation-item anim-stagger-slide-up" style={{ animationDelay: `${conflictIndex * 0.05}s` }}>
                                      <div className="dw-bc-map-disambiguation-asset">
                                        <SpeakerIcon />
                                        <span>{formatBroadcastAssetDisplayName(entry.assetName)}</span>
                                      </div>
                                      <div className="dw-bc-map-disambiguation-controls">
                                        <span>{broadcastLabels.mapSuggestedLabel}</span>
                                        <input
                                          type="text"
                                          value={getDisambiguationNameDraft(
                                            station.id,
                                            entry.assetName,
                                            extractBroadcastLanguageHint(entry.assetName, station.name, fallbackLanguageKey, broadcastLabels)
                                          )}
                                          placeholder={broadcastLabels.mapLanguagePlaceholder}
                                          onClick={(event) => event.stopPropagation()}
                                          onChange={(event) => updateDisambiguationNameDraft(station.id, entry.assetName, event.target.value)}
                                        />
                                        <button type="button" className="dw-bc-map-disambiguation-remove" title={broadcastLabels.mapIgnoreCandidate} onClick={() => handleDiscardConflict(station.id, entry.assetName)}>
                                          <CloseIcon />
                                        </button>
                                      </div>
                                    </div>
                                  ))}
                                  <div className="dw-bc-map-disambiguation-actions">
                                    <button type="button" className="dw-bc-primary-button is-compact" onClick={() => handleResolveStationConflicts(station.id)}>
                                      {broadcastLabels.mapConfirmDisambiguation}
                                    </button>
                                  </div>
                                </div>
                              ) : (
                                <>
                                  {station.audios.length > 0 ? (
                                    <div className={`dw-bc-map-binding-list ${mappingBindFeedback?.stationId === station.id && mappingBindFeedback.phase === "chip" ? "is-bind-feedback" : ""}`} ref={mappingBindingListRef}>
                                      <span className="dw-bc-map-binding-list-title">{broadcastLabels.mapCurrentBindings}</span>
                                      <div className="dw-bc-map-binding-tags">
                                        {station.audios.map((audio) => (
                                          <div key={`${station.id}:${audio.lang}:${audio.assetName}`} className={`dw-bc-map-binding-tag ${mappingBindFeedback?.stationId === station.id && mappingBindFeedback.assetName === audio.assetName && mappingBindFeedback.lang === audio.lang && mappingBindFeedback.phase === "chip" ? "is-bind-feedback" : ""}`}>
                                            <span className="dw-bc-map-binding-tag-lang">{`${audio.lang}:`}</span>
                                            <span className="dw-bc-map-binding-tag-name">{formatBroadcastAssetDisplayName(audio.assetName)}</span>
                                            <button type="button" className="dw-bc-map-binding-tag-remove" onClick={() => handleRemoveStationAudio(station.id, audio.lang)}>
                                              <CloseIcon />
                                            </button>
                                          </div>
                                        ))}
                                      </div>
                                    </div>
                                  ) : null}
                                  <div className="dw-bc-map-binding-toolbar">
                                    <label>{`${broadcastLabels.mapLanguageLabel}:`}</label>
                                    <input
                                      type="text"
                                      value={getBindingLanguageDraft(station.id)}
                                      placeholder={broadcastLabels.mapLanguagePlaceholder}
                                      onClick={(event) => event.stopPropagation()}
                                      onChange={(event) => updateBindingLanguageDraft(station.id, event.target.value)}
                                    />
                                    <span>{broadcastLabels.mapLanguageHint}</span>
                                  </div>
                                  <div className="dw-bc-tray-columns">
                                    {mappingAssetColumns.map((column, columnIndex) => (
                                      <div key={`mapping-col-${columnIndex}`} className="dw-bc-tray-column">
                                        {column.map((asset) => (
                                          <button key={asset.name} type="button" className="dw-bc-tray-item anim-stagger-slide-up" style={{ animationDelay: `${availableAssetLibrary.findIndex((entry) => entry.name === asset.name) * 0.05}s` }} onClick={() => handleBindStation(station.id, asset.name)}>
                                            <span>{formatBroadcastAssetDisplayName(asset.name)}</span>
                                            <span>{asset.desc}</span>
                                          </button>
                                        ))}
                                      </div>
                                    ))}
                                  </div>
                                </>
                              )}
                            </div>
                          </AnimatedInlinePanel>
                        </div>
                      );
                    })}
                  </div>
                )}
                </div>
              </div>
            </div>
          </WorkbenchScrollArea>

          <footer className="dw-bc-footer">
            <div className="dw-bc-footer-left">
              <div className={`dw-bc-footer-status is-${broadcastFooterTone}`}>
                <span className={`dw-bc-footer-dot is-${broadcastFooterTone}`} />
                <span>{broadcastFooterText}</span>
              </div>
              <div className="dw-bc-preview-volume">
                <span className="dw-bc-preview-volume-label">{broadcastLabels.previewVolume}</span>
                <span className="dw-bc-preview-volume-icon-shell">
                  <VolumeIcon className="dw-bc-preview-volume-icon" />
                </span>
                <div
                  ref={previewVolumeTrackRef}
                  className="dw-bc-preview-volume-track"
                  onMouseDown={handleBroadcastPreviewVolumeMouseDown}
                >
                  <div className="dw-bc-preview-volume-fill" style={{ width: `${broadcastPreviewVolume}%` }} />
                  <div className="dw-bc-preview-volume-thumb" style={{ left: `${broadcastPreviewVolume}%` }} />
                </div>
                <span className="dw-bc-preview-volume-value">{`${broadcastPreviewVolume}%`}</span>
              </div>
            </div>
            <button
              type="button"
              className={`dw-bc-primary-button is-footer ${isBroadcastConfigApplied ? "is-applied" : ""}`}
              disabled={isApplyingBroadcastConfig || isBroadcastConfigApplied && !broadcastVariableMappingIssue}
              onClick={broadcastVariableMappingIssue ? handleLocateBroadcastMappingIssue : handleApplyBroadcastConfig}
            >
              {broadcastApplyButtonLabel}
            </button>
          </footer>
          {shouldRenderAssetExplorer ? (
            <div className={`dw-bc-import-overlay is-${assetExplorerStage}`}>
              <div className="dw-bc-import-head">
                <button type="button" className="dw-bc-import-back-button" onClick={handleCloseAssetExplorer}>
                  <ArrowLeftIcon className="dw-bc-import-back-icon" />
                </button>
                <div className="dw-bc-import-head-copy">
                  <span className="dw-bc-import-head-title">{broadcastLabels.importAsset}</span>
                </div>
              </div>

              <div className="dw-bc-import-toolbar">
                <button
                  type="button"
                  className={`dw-bc-import-parent-button ${!externalAssetBrowser?.parentPath ? "is-disabled" : ""}`}
                  onClick={handleExternalBack}
                >
                  <span className="dw-bc-import-parent-icon-shell">
                    <ReturnUpIcon className="dw-bc-import-parent-icon" />
                  </span>
                  <span>{t("broadcast.import.parent")}</span>
                </button>

                <div className="dw-bc-import-breadcrumbs">
                  <span className="dw-bc-import-breadcrumb-icon-shell">
                    <FolderIcon className="dw-bc-import-breadcrumb-icon" />
                  </span>
                  <div className="dw-bc-import-breadcrumb-copy">
                    {currentExternalPath.split("\\").filter(Boolean).map((part, index, parts) => {
                      const buildPath = `${parts.slice(0, index + 1).join("\\")}\\`;
                      const isLast = index === parts.length - 1;
                      return (
                        <div key={buildPath} className="dw-bc-import-breadcrumb-part">
                          <button
                            type="button"
                            className={`dw-bc-import-breadcrumb-button ${isLast ? "is-current" : ""}`}
                            onClick={() => handleExternalPathChange(buildPath)}
                          >
                            {part}
                          </button>
                          {isLast ? null : <span className="dw-bc-import-breadcrumb-sep">\</span>}
                        </div>
                      );
                    })}
                  </div>
                </div>

                <div className="dw-bc-import-toolbar-spacer" />

                <button type="button" className="dw-bc-import-select-all" onClick={handleToggleAllExternalFiles}>
                  <span className="dw-bc-import-select-all-icon-shell">
                    {currentExternalFiles.length > 0
                    && currentExternalFiles.every((file) => selectedExternalFiles.includes(file.id))
                      ? <CheckSquareIcon className="dw-bc-import-select-all-icon is-checked" />
                      : <SquareIcon className="dw-bc-import-select-all-icon" />}
                  </span>
                  <span>{t("broadcast.import.selectAllCurrentDirectory")}</span>
                </button>
              </div>

              <WorkbenchScrollArea className="dw-bc-import-body" metricsKey={`${currentExternalPath}:${currentExternalFolders.length}:${currentExternalFiles.length}`}>
                <div key={currentExternalPath} className="dw-bc-import-grid-scene">
                  <div className="dw-bc-import-grid">
                    {currentExternalFolders.map((folderName) => (
                      <button
                        key={folderName}
                        type="button"
                        className="dw-bc-import-card is-folder"
                        onClick={() => handleExternalPathChange(resolveExternalFolderTargetPath(folderName))}
                      >
                        <span className="dw-bc-import-card-icon-shell">
                          <FolderIcon className="dw-bc-import-card-folder-icon" />
                        </span>
                        <ImportCardName>{folderName}</ImportCardName>
                      </button>
                    ))}

                    {currentExternalFiles.map((file) => {
                      const isSelected = selectedExternalFiles.includes(file.id);
                      return (
                        <button
                          key={file.id}
                          type="button"
                          className={`dw-bc-import-card is-file ${isSelected ? "is-selected" : ""}`}
                          onClick={() => handleToggleExternalFile(file.id)}
                        >
                          <span className="dw-bc-import-card-check-shell">
                            {isSelected
                              ? <CheckSquareIcon className="dw-bc-import-card-check-icon is-checked" />
                              : <SquareIcon className="dw-bc-import-card-check-icon" />}
                          </span>
                          <span className="dw-bc-import-card-icon-shell">
                            <FileAudioIcon className="dw-bc-import-card-file-icon" />
                          </span>
                          <ImportCardName>{file.name}</ImportCardName>
                        </button>
                      );
                    })}
                  </div>
                </div>
              </WorkbenchScrollArea>

              <div className="dw-bc-import-foot">
                <span className="dw-bc-import-foot-note">
                  {t("broadcast.import.supportedFormats", { formats: currentExternalAllowedExtensions.join(", ") })}
                </span>
                <div className="dw-bc-import-foot-actions">
                  <button type="button" className="dw-bc-import-text-button" onClick={handleCloseAssetExplorer}>{t("broadcast.import.cancel")}</button>
                  <button type="button" className="dw-bc-primary-button" onClick={handleImportSelectedExternalFiles}>
                    {t("broadcast.import.confirm")}{selectedExternalFiles.length > 0 ? ` (${selectedExternalFiles.length})` : ""}
                  </button>
                </div>
              </div>
            </div>
          ) : null}
        </section>
        </div>
      </div>
      <div ref={dropdownPortalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}
