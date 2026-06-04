import { TRIGGER_OPTIONS } from "./broadcast-constants";
import {
  normalizeLangIndex,
  normalizeSlotHintEntry,
  formatVariableDisplayName,
  formatVariableSlotLabel,
} from "./broadcast-normalize";

function buildVariableLibrary(baseLibrary, slotHints, labels, turnbackPoints) {
  const normalizedHints = Array.isArray(slotHints)
    ? slotHints.map(normalizeSlotHintEntry).filter(Boolean)
    : [];
  const normalizedTurnbackPoints = Array.isArray(turnbackPoints)
    ? turnbackPoints
    : [];
  const turnbackDescription = normalizedTurnbackPoints
    .map((point) =>
      point?.resolved && point?.stationName
        ? point.stationName
        : labels.unresolvedTurnback,
    )
    .join(" / ");
  const maxSlotIndex = Math.max(
    1,
    ...normalizedHints.map((entry) => normalizeLangIndex(entry.langIndex)),
  );
  const slotHintByIndex = new Map(
    normalizedHints.map((entry) => [entry.langIndex, entry]),
  );
  const result = [];

  baseLibrary.forEach((variable) => {
    if (
      variable.id === "turnback_station" &&
      normalizedTurnbackPoints.length === 0
    ) {
      return;
    }

    for (let langIndex = 1; langIndex <= maxSlotIndex; langIndex += 1) {
      const hint = slotHintByIndex.get(langIndex);
      const slotLabel = formatVariableSlotLabel(langIndex, labels);
      const joinedLabels =
        Array.isArray(hint?.labels) && hint.labels.length > 0
          ? hint.labels.join(" / ")
          : "";
      const desc =
        variable.id === "turnback_station"
          ? turnbackDescription
          : joinedLabels
            ? `${slotLabel}: ${joinedLabels}`
            : slotLabel;
      result.push({
        ...variable,
        id: `${variable.id}__slot_${langIndex}`,
        langIndex,
        name: formatVariableDisplayName(variable.nameKey, langIndex, labels),
        desc,
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
    return (
      labels.dynamicVariable ||
      (node.descKey ? labels.t(node.descKey) : "") ||
      node.desc ||
      ""
    );
  }

  if (node.type === "asset") {
    return (
      labels.assetNode ||
      node.desc ||
      (node.descKey ? labels.t(node.descKey) : "")
    );
  }

  return node.desc || (node.descKey ? labels.t(node.descKey) : "");
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

export {
  buildVariableLibrary,
  resolveVariableNodeDisplayName,
  resolveRuleNodeKindLabel,
  resolveRuleTriggerLabel,
};
