import { useMemo } from "react";
import {
  RELEASE_HIDDEN_PLATFORM_TRIGGER_IDS,
  resolvePlatformRuntimeTriggerId,
  resolvePlatformUiTriggerId,
} from "./broadcast-constants";
import { normalizeRuleNode } from "./broadcast-normalize";

export default function useBroadcastPlatformRules(context) {
  const {
    platformAnnouncements,
    stations,
    t,
    platformTriggerOptions,
    getActiveBroadcastLineId,
    platformCreateStationIds,
    newRuleTriggerId,
    newRuleTitle,
    trayContext,
    removeTimersRef,
    removingNodeIds,
    dirtyPlatformStationIdsRef,
    platformRuleTitleMemoryRef,
    platformRuleIdMemoryRef,
    markBroadcastDraftDirty,
    buildCurrentBroadcastLineDraft,
    setPlatformAnnouncements,
    setPlatformCreateStationIds,
    setIsCreatingRule,
    setNewRuleTitle,
    setNewRuleTriggerId,
    setTrayContext,
    setMappingTray,
    setRemovingNodeIds,
  } = context;

  const platformRules = useMemo(() => {
    const stationById = new Map(stations.map((station) => [station.id, station]));
    const groups = [];
    const groupByKey = new Map();
    platformAnnouncements.forEach((announcement) => {
      const station = stationById.get(announcement?.stationId);
      const nodes = Array.isArray(announcement?.nodes) ? announcement.nodes : [];
      const enabled = Boolean(announcement?.enabled);
      const uiTriggerId = announcement?.uiTriggerId || announcement?.triggerId || "platform_idle_clear";
      const signatureKey = `${enabled ? "1" : "0"}:${uiTriggerId}:${JSON.stringify(nodes)}`;
      const explicitTitle = typeof announcement?.title === "string" ? announcement.title.trim() : "";
      if (
        !station ||
        RELEASE_HIDDEN_PLATFORM_TRIGGER_IDS.includes(uiTriggerId) ||
        (!enabled && nodes.length === 0)
      ) {
        return;
      }

      if (explicitTitle) {
        platformRuleTitleMemoryRef.current[signatureKey] = explicitTitle;
      }

      const key = `${signatureKey}:${explicitTitle || platformRuleTitleMemoryRef.current[signatureKey] || ""}`;
      let group = groupByKey.get(key);
      if (!group) {
        group = {
          enabled,
          nodes,
          title: explicitTitle || platformRuleTitleMemoryRef.current[signatureKey] || "",
          uiTriggerId,
          stationIds: [],
        };
        groupByKey.set(key, group);
        groups.push(group);
      }
      group.stationIds.push(station.id);
    });

    return groups.map((group, index) => {
      const idSignature = `${group.enabled ? "1" : "0"}:${group.title || ""}:${group.uiTriggerId}:${JSON.stringify(group.nodes)}`;
      if (!platformRuleIdMemoryRef.current[idSignature]) {
        platformRuleIdMemoryRef.current[idSignature] = `platform-rule:${Date.now()}:${index}:${Math.random().toString(36).slice(2, 8)}`;
      }
      return {
        id: platformRuleIdMemoryRef.current[idSignature],
        title: group.title || t("broadcast.platform.title"),
        triggerId: group.uiTriggerId,
        trigger:
          platformTriggerOptions.find((option) => option.id === group.uiTriggerId)
            ?.label || "",
        enabled: group.enabled,
        stationIds: group.stationIds,
        nodes: group.nodes,
      };
    });
  }, [platformAnnouncements, platformTriggerOptions, stations, t]);
  const platformStationOccupancyByTrigger = useMemo(() => {
    const next = new Map();
    platformRules.forEach((rule) => {
      const triggerId = resolvePlatformUiTriggerId(rule?.triggerId);
      if (!triggerId || !Array.isArray(rule?.stationIds)) {
        return;
      }

      let stationMap = next.get(triggerId);
      if (!stationMap) {
        stationMap = new Map();
        next.set(triggerId, stationMap);
      }

      rule.stationIds.forEach((stationId) => {
        if (!stationId) {
          return;
        }

        let ruleIds = stationMap.get(stationId);
        if (!ruleIds) {
          ruleIds = new Set();
          stationMap.set(stationId, ruleIds);
        }
        ruleIds.add(rule.id);
      });
    });
    return next;
  }, [platformRules]);

  function markDirtyPlatformStations(stationIds, nextPlatformAnnouncements = platformAnnouncements) {
    const current = new Set(dirtyPlatformStationIdsRef.current);
    (Array.isArray(stationIds) ? stationIds : []).forEach((stationId) => {
      if (typeof stationId === "string" && stationId) {
        current.add(stationId);
      }
    });
    dirtyPlatformStationIdsRef.current = Array.from(current);
    markBroadcastDraftDirty(getActiveBroadcastLineId(), buildCurrentBroadcastLineDraft({ platformAnnouncements: nextPlatformAnnouncements }));
  }

  function isPlatformStationOccupiedByTrigger(stationId, triggerId, exceptRuleId = "") {
    const normalizedTriggerId = resolvePlatformUiTriggerId(triggerId);
    const ruleIds = platformStationOccupancyByTrigger.get(normalizedTriggerId)?.get(stationId);
    if (!ruleIds || ruleIds.size === 0) {
      return false;
    }

    if (!exceptRuleId) {
      return true;
    }

    for (const ruleId of ruleIds) {
      if (ruleId !== exceptRuleId) {
        return true;
      }
    }

    return false;
  }

  function getAvailablePlatformCreateStations(triggerId) {
    return stations.filter((station) => station && !isPlatformStationOccupiedByTrigger(station.id, triggerId));
  }

  function buildPlatformAnnouncementKey(stationId, triggerId) {
    return `${stationId || ""}:${resolvePlatformUiTriggerId(triggerId || "platform_idle_clear")}`;
  }

  function createEmptyPlatformAnnouncement(station, triggerId = "platform_idle_clear") {
    const resolvedTriggerId = resolvePlatformUiTriggerId(triggerId);
    return {
      lineId: getActiveBroadcastLineId(),
      stationId: station.id,
      stationName: station.name,
      title: "",
      uiTriggerId: resolvedTriggerId,
      enabled: false,
      triggerId: resolvePlatformRuntimeTriggerId(resolvedTriggerId),
      cooldownGameMinutes: 20,
      nodes: [],
    };
  }

  function getPlatformAnnouncement(station, triggerId = "platform_idle_clear") {
    const resolvedTriggerId = resolvePlatformUiTriggerId(triggerId);
    const existing = platformAnnouncements.find(
      (entry) => entry.stationId === station.id && resolvePlatformUiTriggerId(entry?.uiTriggerId || entry?.triggerId) === resolvedTriggerId,
    );
    return existing || createEmptyPlatformAnnouncement(station, resolvedTriggerId);
  }

  function updatePlatformAnnouncement(station, triggerId, updater) {
    const base = getPlatformAnnouncement(station, triggerId);
    const nextAnnouncement = typeof updater === "function" ? updater(base) : base;
    const nextKey = buildPlatformAnnouncementKey(station.id, nextAnnouncement?.uiTriggerId || nextAnnouncement?.triggerId || triggerId);
    const nextByKey = new Map(platformAnnouncements.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
    nextByKey.set(nextKey, {
      ...nextAnnouncement,
      lineId: getActiveBroadcastLineId(),
      stationId: station.id,
      stationName: station.name,
      uiTriggerId: resolvePlatformUiTriggerId(nextAnnouncement?.uiTriggerId || nextAnnouncement?.triggerId),
      triggerId: resolvePlatformRuntimeTriggerId(nextAnnouncement?.uiTriggerId || nextAnnouncement?.triggerId),
      cooldownGameMinutes: 20,
    });
    const nextPlatformAnnouncements = Array.from(nextByKey.values());
    markDirtyPlatformStations([station?.id], nextPlatformAnnouncements);
    setPlatformAnnouncements(nextPlatformAnnouncements);
  }

  function buildPlatformAnnouncementForStation(station, source) {
    const uiTriggerId = resolvePlatformUiTriggerId(source?.uiTriggerId || source?.triggerId);
    return {
      lineId: getActiveBroadcastLineId(),
      stationId: station.id,
      stationName: station.name,
      title: typeof source?.title === "string" ? source.title : "",
      uiTriggerId,
      enabled: Boolean(source?.enabled),
      triggerId: resolvePlatformRuntimeTriggerId(uiTriggerId),
      cooldownGameMinutes: 20,
      nodes: Array.isArray(source?.nodes) ? source.nodes : [],
    };
  }

  function updatePlatformRule(ruleId, updater) {
    const targetRule = platformRules.find((rule) => rule.id === ruleId);
    if (!targetRule) {
      return;
    }

    const nextRule = typeof updater === "function" ? updater(targetRule) : targetRule;
    const nextPlatformAnnouncements = buildPlatformAnnouncementsForRuleUpdate(platformAnnouncements, targetRule, nextRule);
    markDirtyPlatformStations(targetRule.stationIds, nextPlatformAnnouncements);
    setPlatformAnnouncements(nextPlatformAnnouncements);
  }

  function buildPlatformAnnouncementsForRuleUpdate(source, targetRule, nextRule) {
    const nextByKey = new Map((Array.isArray(source) ? source : []).map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
    targetRule.stationIds.forEach((stationId) => {
      const station = stations.find((entry) => entry.id === stationId);
      if (station) {
        const announcement = buildPlatformAnnouncementForStation(station, nextRule);
        nextByKey.set(buildPlatformAnnouncementKey(stationId, announcement.uiTriggerId), announcement);
      }
    });

    return Array.from(nextByKey.values());
  }

  function handleCreatePlatformRule() {
    const targetStations = stations.filter((station) => platformCreateStationIds.includes(station.id) && !isPlatformStationOccupiedByTrigger(station.id, newRuleTriggerId));
    if (!newRuleTitle.trim() || targetStations.length === 0) {
      return;
    }

    const nextByKey = new Map(platformAnnouncements.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
    const nodes = [];
    const signatureKey = `1:${newRuleTriggerId}:${JSON.stringify(nodes)}`;
    platformRuleTitleMemoryRef.current[signatureKey] = newRuleTitle.trim();
    targetStations.forEach((station) => {
      const announcement = buildPlatformAnnouncementForStation(station, {
        title: newRuleTitle.trim(),
        triggerId: newRuleTriggerId,
        enabled: true,
        nodes,
      });
      nextByKey.set(buildPlatformAnnouncementKey(station.id, announcement.uiTriggerId), announcement);
    });
    const nextPlatformAnnouncements = Array.from(nextByKey.values());
    setPlatformAnnouncements(nextPlatformAnnouncements);
    markDirtyPlatformStations(targetStations.map((station) => station.id), nextPlatformAnnouncements);
    setIsCreatingRule(false);
    setNewRuleTitle("");
    setNewRuleTriggerId(platformTriggerOptions[0]?.id || "approach_station");
    setPlatformCreateStationIds([]);
    setTrayContext(null);
  }

  function handleAddNodeToPlatformRule(ruleId, nodeTemplate) {
    const actionId = trayContext?.ruleId === ruleId && trayContext?.action && trayContext.action !== "add" ? trayContext.action : "";
    const lineId = getActiveBroadcastLineId();
    const targetRule = platformRules.find((rule) => rule.id === ruleId);
    const nextNode = { ...nodeTemplate, id: actionId || `${Date.now()}-${Math.random().toString(36).slice(2, 6)}` };
    const nextRule = targetRule
      ? {
          ...targetRule,
          nodes: actionId
            ? (Array.isArray(targetRule.nodes) ? targetRule.nodes : []).map((node) => (node.id === actionId ? nextNode : node))
            : [...(Array.isArray(targetRule.nodes) ? targetRule.nodes : []), nextNode],
        }
      : null;
    const nextPlatformAnnouncements =
      targetRule && nextRule ? buildPlatformAnnouncementsForRuleUpdate(platformAnnouncements, targetRule, nextRule) : platformAnnouncements;
    markBroadcastDraftDirty(lineId, buildCurrentBroadcastLineDraft({ platformAnnouncements: nextPlatformAnnouncements }));
    setTrayContext(null);
    const timer = window.setTimeout(() => {
      if (getActiveBroadcastLineId() !== lineId) {
        return;
      }

      setPlatformAnnouncements(nextPlatformAnnouncements);
    }, 140);
    removeTimersRef.current.push(timer);
  }

  function handleRemovePlatformRuleNode(ruleId, nodeId) {
    const removalKey = `${ruleId}:${nodeId}`;
    if (removingNodeIds[removalKey]) {
      return;
    }

    const lineId = getActiveBroadcastLineId();
    const targetRule = platformRules.find((rule) => rule.id === ruleId);
    const nextRule = targetRule
      ? {
          ...targetRule,
          nodes: (Array.isArray(targetRule.nodes) ? targetRule.nodes : []).filter((node) => node.id !== nodeId),
        }
      : null;
    const nextPlatformAnnouncements =
      targetRule && nextRule ? buildPlatformAnnouncementsForRuleUpdate(platformAnnouncements, targetRule, nextRule) : platformAnnouncements;
    markBroadcastDraftDirty(lineId, buildCurrentBroadcastLineDraft({ platformAnnouncements: nextPlatformAnnouncements }));
    setRemovingNodeIds((current) => ({ ...current, [removalKey]: true }));
    if (trayContext?.action === nodeId) {
      setTrayContext(null);
    }

    const timer = window.setTimeout(() => {
      if (getActiveBroadcastLineId() !== lineId) {
        setRemovingNodeIds((current) => {
          const next = { ...current };
          delete next[removalKey];
          return next;
        });
        return;
      }

      setPlatformAnnouncements(nextPlatformAnnouncements);
      setRemovingNodeIds((current) => {
        const next = { ...current };
        delete next[removalKey];
        return next;
      });
    }, 220);

    removeTimersRef.current.push(timer);
  }

  function handleRemovePlatformRule(ruleId) {
    const targetRule = platformRules.find((rule) => rule.id === ruleId);
    if (!targetRule) {
      return;
    }

    const nextByKey = new Map(platformAnnouncements.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
    targetRule.stationIds.forEach((stationId) => {
      const station = stations.find((entry) => entry.id === stationId);
      if (station) {
        nextByKey.set(buildPlatformAnnouncementKey(stationId, targetRule.triggerId), createEmptyPlatformAnnouncement(station, targetRule.triggerId));
      }
    });
    const nextPlatformAnnouncements = Array.from(nextByKey.values());
    setPlatformAnnouncements(nextPlatformAnnouncements);
    markDirtyPlatformStations(targetRule.stationIds, nextPlatformAnnouncements);
    if (trayContext?.ruleId === ruleId) {
      setTrayContext(null);
    }
  }

  function handleTogglePlatformRuleStation(ruleId, stationId) {
    const targetRule = platformRules.find((rule) => rule.id === ruleId);
    const station = stations.find((entry) => entry.id === stationId);
    if (!targetRule || !station) {
      return;
    }

    const isAssigned = targetRule.stationIds.includes(stationId);
    const rememberedTitle = typeof targetRule.title === "string" ? targetRule.title.trim() : "";
    const stableRule = {
      ...targetRule,
      title: rememberedTitle,
    };
    if (rememberedTitle) {
      const nodes = Array.isArray(targetRule.nodes) ? targetRule.nodes : [];
      const signatureKey = `${targetRule.enabled ? "1" : "0"}:${targetRule.triggerId || "platform_idle_clear"}:${JSON.stringify(nodes)}`;
      platformRuleTitleMemoryRef.current[signatureKey] = rememberedTitle;
    }
    const nextByKey = new Map(platformAnnouncements.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
    const nextAnnouncement = isAssigned ? createEmptyPlatformAnnouncement(station, stableRule.triggerId) : buildPlatformAnnouncementForStation(station, stableRule);
    nextByKey.set(buildPlatformAnnouncementKey(stationId, stableRule.triggerId), nextAnnouncement);
    const nextPlatformAnnouncements = Array.from(nextByKey.values());
    setPlatformAnnouncements(nextPlatformAnnouncements);
    markDirtyPlatformStations([stationId], nextPlatformAnnouncements);
  }

  return {
    platformRules,
    platformStationOccupancyByTrigger,
    getAvailablePlatformCreateStations,
    isPlatformStationOccupiedByTrigger,
    handleCreatePlatformRule,
    handleAddNodeToPlatformRule,
    handleRemovePlatformRuleNode,
    handleRemovePlatformRule,
    handleTogglePlatformRuleStation,
  };
}
