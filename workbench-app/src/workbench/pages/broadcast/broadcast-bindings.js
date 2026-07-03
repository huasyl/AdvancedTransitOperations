import {
  normalizeLangIndex,
  normalizeSlotHintEntry,
  resolveBroadcastLanguageKeyFromLabel,
} from "./broadcast-normalize";
import {
  extractBroadcastLanguageHint,
  extractBroadcastLanguageKey,
} from "./broadcast-assets";

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

      normalized.labels.forEach((label) =>
        labelsBySlot.get(normalized.langIndex)?.add(label),
      );
    });
  });

  return Array.from(labelsBySlot.entries())
    .sort((left, right) => left[0] - right[0])
    .map(([langIndex, labels]) => ({
      langIndex,
      labels: Array.from(labels).sort((left, right) =>
        left.localeCompare(right),
      ),
    }));
}

function deriveBindingSlotHintsFromStations(stations) {
  const labelsBySlot = new Map();
  (Array.isArray(stations) ? stations : []).forEach((station) => {
    const orderedAudios = (Array.isArray(station?.audios) ? station.audios : [])
      .filter(
        (audio) =>
          audio && typeof audio.assetName === "string" && audio.assetName,
      )
      .slice()
      .sort(
        (left, right) =>
          normalizeLangIndex(left?.langIndex) -
          normalizeLangIndex(right?.langIndex),
      );
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
      labels: Array.from(labels).sort((left, right) =>
        left.localeCompare(right),
      ),
    }));
}

function resolveBroadcastConflictLanguageKey(
  entry,
  stationName,
  fallbackLanguageKey,
  labels,
) {
  const suggestedKey = resolveBroadcastLanguageKeyFromLabel(
    entry?.suggestedLang,
    labels,
  );
  if (suggestedKey) {
    return suggestedKey;
  }

  return extractBroadcastLanguageKey(
    entry?.assetName || "",
    stationName,
    fallbackLanguageKey,
  );
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

function sortBroadcastConflictAssets(
  conflictAssets,
  stationName,
  fallbackLanguageKey,
  labels,
) {
  const entries = Array.isArray(conflictAssets) ? [...conflictAssets] : [];
  const resolveSuggestedLabel = (entry) =>
    typeof entry?.suggestedLang === "string" && entry.suggestedLang
      ? entry.suggestedLang
      : extractBroadcastLanguageHint(
          entry?.assetName || "",
          stationName,
          fallbackLanguageKey,
          labels,
        );

  entries.sort((left, right) => {
    const leftPriority =
      resolveBroadcastConflictLanguageKey(
        left,
        stationName,
        fallbackLanguageKey,
        labels,
      ) === fallbackLanguageKey
        ? 0
        : 1;
    const rightPriority =
      resolveBroadcastConflictLanguageKey(
        right,
        stationName,
        fallbackLanguageKey,
        labels,
      ) === fallbackLanguageKey
        ? 0
        : 1;
    if (leftPriority !== rightPriority) {
      return leftPriority - rightPriority;
    }

    return String(left?.assetName || "").localeCompare(
      String(right?.assetName || ""),
    );
  });

  return entries.map((entry) => ({
    ...entry,
    suggestedLang: resolveSuggestedLabel(entry),
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
    .filter(
      (audio) =>
        audio && typeof audio.assetName === "string" && audio.assetName,
    )
    .slice()
    .sort(
      (left, right) =>
        normalizeLangIndex(left?.langIndex) -
        normalizeLangIndex(right?.langIndex),
    );
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

    if (
      Array.isArray(station.conflictAssets) &&
      station.conflictAssets.length > 0
    ) {
      return {
        type: "conflict",
        stationId: station.id,
        stationName: station.name || "",
        requiredSlots,
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
        missingSlot,
      };
    }
  }

  return null;
}

export {
  mergeBindingSlotHints,
  deriveBindingSlotHintsFromStations,
  resolveBroadcastConflictLanguageKey,
  deriveBroadcastStationStatus,
  sortBroadcastConflictAssets,
  collectBroadcastVariableSlotRequirements,
  collectBroadcastStationSlotIndexes,
  buildBroadcastVariableMappingIssue,
};
