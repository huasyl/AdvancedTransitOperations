import { useEffect, useMemo } from "react";
import { resolvePlatformRuntimeTriggerId, resolvePlatformUiTriggerId } from "./broadcast-constants";
import { normalizeRuleNode } from "./broadcast-normalize";

export default function useBroadcastPlatformRules(context) {
  const {
    platformAnnouncements,
    stations,
    selectedLineId,
    workbenchApi,
    hasBroadcastHydratedRef,
    skipNextPlatformAnnouncementsSaveRef,
    dirtyPlatformStationIdsRef,
    platformRuleTitleMemoryRef,
    platformRuleIdMemoryRef,
    setPlatformAnnouncements,
    setPlatformCreateStationIds,
    setIsCreatingRule,
    setNewRuleTitle,
    setNewRuleTriggerId,
    setTrayContext,
    setMappingTray,
    applyBroadcastSnapshot,
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
      if (!station || (!enabled && nodes.length === 0)) {
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
        trigger: platformTriggerOptions.find((option) => option.id === group.uiTriggerId)?.label || t("broadcast.platform.idleClear"),
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

  function markDirtyPlatformStations(stationIds) {
    const current = new Set(dirtyPlatformStationIdsRef.current);
    (Array.isArray(stationIds) ? stationIds : []).forEach((stationId) => {
      if (typeof stationId === "string" && stationId) {
        current.add(stationId);
      }
    });
    dirtyPlatformStationIdsRef.current = Array.from(current);
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
    markDirtyPlatformStations([station?.id]);
    setPlatformAnnouncements((current) => {
      const nextByKey = new Map(current.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
      nextByKey.set(nextKey, {
        ...nextAnnouncement,
        lineId: getActiveBroadcastLineId(),
        stationId: station.id,
        stationName: station.name,
        uiTriggerId: resolvePlatformUiTriggerId(nextAnnouncement?.uiTriggerId || nextAnnouncement?.triggerId),
        triggerId: resolvePlatformRuntimeTriggerId(nextAnnouncement?.uiTriggerId || nextAnnouncement?.triggerId),
        cooldownGameMinutes: 20,
      });
      return Array.from(nextByKey.values());
    });
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
    markDirtyPlatformStations(targetRule.stationIds);
    setPlatformAnnouncements((current) => {
      const nextByKey = new Map(current.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
      targetRule.stationIds.forEach((stationId) => {
        const station = stations.find((entry) => entry.id === stationId);
        if (station) {
          const announcement = buildPlatformAnnouncementForStation(station, nextRule);
          nextByKey.set(buildPlatformAnnouncementKey(stationId, announcement.uiTriggerId), announcement);
        }
      });

      return Array.from(nextByKey.values());
    });
  }

  async function savePlatformAnnouncement(station, triggerId = "platform_idle_clear", copyToAll = false) {
    const announcement = getPlatformAnnouncement(station, triggerId);
    const request = {
      lineId: getActiveBroadcastLineId(),
      stationId: station.id,
      stationName: station.name,
      title: typeof announcement.title === "string" ? announcement.title : "",
      uiTriggerId: resolvePlatformUiTriggerId(announcement?.uiTriggerId || announcement?.triggerId),
      enabled: Boolean(announcement.enabled),
      nodes: Array.isArray(announcement.nodes) ? announcement.nodes : [],
    };

    try {
      const result = copyToAll ? await workbenchApi.copyBroadcastPlatformAnnouncementToAllStations?.(request) : await workbenchApi.saveBroadcastPlatformAnnouncement?.(request);
      if (result?.snapshot) {
        applyBroadcastSnapshot(result.snapshot);
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] save platform announcement failed", error);
    }
  }

  async function persistPlatformAnnouncementForStation(station, source) {
    if (!station) {
      return null;
    }

    const result = await workbenchApi.saveBroadcastPlatformAnnouncement?.({
      lineId: getActiveBroadcastLineId(),
      stationId: station.id,
      stationName: station.name,
      title: typeof source?.title === "string" ? source.title : "",
      uiTriggerId: resolvePlatformUiTriggerId(source?.uiTriggerId || source?.triggerId),
      enabled: Boolean(source?.enabled),
      nodes: Array.isArray(source?.nodes) ? source.nodes : [],
    });
    return result?.snapshot || null;
  }

  async function savePlatformRule(rule, copyToAll = false) {
    const targetStations = copyToAll ? stations : stations.filter((station) => rule.stationIds.includes(station.id));
    if (!rule || targetStations.length === 0) {
      return;
    }

    try {
      let latestSnapshot = null;
      if (copyToAll) {
        const firstStation = targetStations[0];
        const result = await workbenchApi.copyBroadcastPlatformAnnouncementToAllStations?.({
          lineId: getActiveBroadcastLineId(),
          stationId: firstStation.id,
          stationName: firstStation.name,
          title: typeof rule.title === "string" ? rule.title : "",
          uiTriggerId: resolvePlatformUiTriggerId(rule?.uiTriggerId || rule?.triggerId),
          enabled: Boolean(rule.enabled),
          nodes: Array.isArray(rule.nodes) ? rule.nodes : [],
        });
        latestSnapshot = result?.snapshot || null;
      } else {
        for (let index = 0; index < targetStations.length; index += 1) {
          const station = targetStations[index];
          const result = await workbenchApi.saveBroadcastPlatformAnnouncement?.({
            lineId: getActiveBroadcastLineId(),
            stationId: station.id,
            stationName: station.name,
            title: typeof rule.title === "string" ? rule.title : "",
            uiTriggerId: resolvePlatformUiTriggerId(rule?.uiTriggerId || rule?.triggerId),
            enabled: Boolean(rule.enabled),
            nodes: Array.isArray(rule.nodes) ? rule.nodes : [],
          });
          latestSnapshot = result?.snapshot || latestSnapshot;
        }
      }

      if (latestSnapshot) {
        applyBroadcastSnapshot(latestSnapshot);
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] save platform rule failed", error);
    }
  }

  function handleCreatePlatformRule() {
    const targetStations = stations.filter((station) => platformCreateStationIds.includes(station.id) && !isPlatformStationOccupiedByTrigger(station.id, newRuleTriggerId));
    if (!newRuleTitle.trim() || targetStations.length === 0) {
      return;
    }

    setPlatformAnnouncements((current) => {
      const nextByKey = new Map(current.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
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

      return Array.from(nextByKey.values());
    });
    markDirtyPlatformStations(targetStations.map((station) => station.id));
    setIsCreatingRule(false);
    setNewRuleTitle("");
    setNewRuleTriggerId("platform_idle_clear");
    setPlatformCreateStationIds([]);
    setTrayContext(null);
  }

  function handleAddNodeToPlatformRule(ruleId, nodeTemplate) {
    const actionId = trayContext?.ruleId === ruleId && trayContext?.action && trayContext.action !== "add" ? trayContext.action : "";
    setTrayContext(null);
    const timer = window.setTimeout(() => {
      updatePlatformRule(ruleId, (current) => {
        if (actionId) {
          return {
            ...current,
            nodes: (Array.isArray(current.nodes) ? current.nodes : []).map((node) => (node.id === actionId ? { ...nodeTemplate, id: node.id } : node)),
          };
        }

        return {
          ...current,
          nodes: [...(Array.isArray(current.nodes) ? current.nodes : []), { ...nodeTemplate, id: `${Date.now()}-${Math.random().toString(36).slice(2, 6)}` }],
        };
      });
    }, 140);
    removeTimersRef.current.push(timer);
  }

  function handleRemovePlatformRuleNode(ruleId, nodeId) {
    const removalKey = `${ruleId}:${nodeId}`;
    if (removingNodeIds[removalKey]) {
      return;
    }

    setRemovingNodeIds((current) => ({ ...current, [removalKey]: true }));
    if (trayContext?.action === nodeId) {
      setTrayContext(null);
    }

    const timer = window.setTimeout(() => {
      updatePlatformRule(ruleId, (current) => ({
        ...current,
        nodes: (Array.isArray(current.nodes) ? current.nodes : []).filter((node) => node.id !== nodeId),
      }));
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

    setPlatformAnnouncements((current) => {
      const nextByKey = new Map(current.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
      targetRule.stationIds.forEach((stationId) => {
        const station = stations.find((entry) => entry.id === stationId);
        if (station) {
          nextByKey.set(buildPlatformAnnouncementKey(stationId, targetRule.triggerId), createEmptyPlatformAnnouncement(station, targetRule.triggerId));
        }
      });

      return Array.from(nextByKey.values());
    });
    markDirtyPlatformStations(targetRule.stationIds);
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
    setPlatformAnnouncements((current) => {
      const nextByKey = new Map(current.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
      const nextAnnouncement = isAssigned ? createEmptyPlatformAnnouncement(station, stableRule.triggerId) : buildPlatformAnnouncementForStation(station, stableRule);
      nextByKey.set(buildPlatformAnnouncementKey(stationId, stableRule.triggerId), nextAnnouncement);
      return Array.from(nextByKey.values());
    });
    markDirtyPlatformStations([stationId]);
  }

  useEffect(() => {
    if (!hasBroadcastHydratedRef.current || !selectedLineId) {
      return undefined;
    }

    if (skipNextPlatformAnnouncementsSaveRef.current) {
      skipNextPlatformAnnouncementsSaveRef.current = false;
      return undefined;
    }

    const dirtyStationIds = dirtyPlatformStationIdsRef.current.filter(Boolean);
    if (dirtyStationIds.length === 0) {
      return undefined;
    }

    const timer = window.setTimeout(async () => {
      const stationMap = new Map(stations.map((station) => [station.id, station]));
      const announcementMap = new Map(platformAnnouncements.map((entry) => [buildPlatformAnnouncementKey(entry.stationId, entry?.uiTriggerId || entry?.triggerId), entry]));
      dirtyPlatformStationIdsRef.current = [];

      try {
        let latestSnapshot = null;
        for (let index = 0; index < dirtyStationIds.length; index += 1) {
          const stationId = dirtyStationIds[index];
          const station = stationMap.get(stationId);
          if (!station) {
            continue;
          }

          for (const triggerId of ["platform_idle_clear", "approach_station"]) {
            const source = announcementMap.get(buildPlatformAnnouncementKey(stationId, triggerId)) || createEmptyPlatformAnnouncement(station, triggerId);
            latestSnapshot = (await persistPlatformAnnouncementForStation(station, source)) || latestSnapshot;
          }
        }

        if (latestSnapshot) {
          applyBroadcastSnapshot(latestSnapshot);
        }
      } catch (error) {
        console.error("[RT Broadcast Workbench] save platform announcements failed", error);
      }
    }, 180);

    return () => {
      window.clearTimeout(timer);
    };
  }, [platformAnnouncements, selectedLineId, stations, workbenchApi]);

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
