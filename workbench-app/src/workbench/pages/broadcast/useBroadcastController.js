import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import { getWorkbenchApi } from "../../shared/workbench-api";
import {
  VARIABLE_LIBRARY,
  DELAY_LIBRARY,
  TRIGGER_OPTIONS,
  PLATFORM_TRIGGER_OPTIONS,
  LINE_OPTIONS,
  TAB_TRANSITION_MS,
  PAGE_ENTER_ANIMATION_MS,
  resolvePlatformUiTriggerId,
  resolvePlatformRuntimeTriggerId,
} from "./broadcast-constants";
import { normalizeLangIndex, normalizeRuleNode, cloneBroadcastRules, getBroadcastLocaleLanguageKey, resolveBroadcastLanguageLabel } from "./broadcast-normalize";
import { createEmptyExternalAssetBrowserState, extractBroadcastLanguageHint } from "./broadcast-assets";
import {
  mergeBindingSlotHints,
  deriveBindingSlotHintsFromStations,
  deriveBroadcastStationStatus,
  sortBroadcastConflictAssets,
  buildBroadcastVariableMappingIssue,
} from "./broadcast-bindings";
import { buildVariableLibrary } from "./broadcast-rules";
import { extractBackendLineOptions, splitIntoColumns } from "./broadcast-view-models";
import { isTerminalBroadcastPreviewState } from "./broadcast-preview";
import { animateElementScrollTop } from "./components/BroadcastAnimatedPanels";
import useBroadcastPlatformRules from "./useBroadcastPlatformRules";
import useBroadcastStationBindings from "./useBroadcastStationBindings";
import useBroadcastAssets from "./useBroadcastAssets";

export default function useBroadcastController({ pageEnterSequence = 0 } = {}) {
  const { locale, t } = useNativeScheduleI18n();
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const delayLibrary = useMemo(() => DELAY_LIBRARY.map((delay) => ({ ...delay, name: t(delay.nameKey), desc: t(delay.descKey) })), [t]);
  const triggerOptions = useMemo(() => TRIGGER_OPTIONS.map((option) => ({ ...option, label: t(option.labelKey) })), [t]);
  const platformTriggerOptions = useMemo(() => PLATFORM_TRIGGER_OPTIONS.map((option) => ({ ...option, label: t(option.labelKey) })), [t]);
  const fallbackLineOptions = useMemo(() => LINE_OPTIONS.map((line) => ({ ...line, label: t(line.labelKey) })), [t]);
  const [activeTab, setActiveTab] = useState("sequence");
  const [renderedTab, setRenderedTab] = useState("sequence");
  const [tabStage, setTabStage] = useState("entered");
  const [pageEnterState, setPageEnterState] = useState("entered");
  const [rules, setRules] = useState([]);
  const [stations, setStations] = useState([]);
  const [turnbackPoints, setTurnbackPoints] = useState([]);
  const [platformAnnouncements, setPlatformAnnouncements] = useState([]);
  const [broadcastWarnings, setBroadcastWarnings] = useState([]);
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
  const [broadcastLineApplied, setBroadcastLineApplied] = useState(false);
  const [broadcastLineDraftDirty, setBroadcastLineDraftDirty] = useState(false);
  const [broadcastVolumeDirty, setBroadcastVolumeDirty] = useState(false);
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
  const [platformCreateStationIds, setPlatformCreateStationIds] = useState([]);
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
  const dropdownPortalHostRef = useRef(null);
  const removeTimersRef = useRef([]);
  const mappingBindFeedbackTimerRef = useRef(null);
  const mappingBindScrollFrameRef = useRef(0);
  const mappingBindTransformCleanupRef = useRef(null);
  const pageEnterTimerRef = useRef(null);
  const wasInlineTrayVisibleRef = useRef(false);
  const hasBroadcastHydratedRef = useRef(false);
  const hasBackendLineHydratedRef = useRef(false);
  const hasBroadcastRulesHydratedRef = useRef(false);
  const lastHydratedLineIdRef = useRef("");
  const lastHydratedRulesLineIdRef = useRef("");
  const lineOptionsRef = useRef(lineOptions);
  const selectedLineIdRef = useRef(selectedLineId);
  const platformRuleTitleMemoryRef = useRef({});
  const platformRuleIdMemoryRef = useRef({});
  const skipNextPlatformAnnouncementsSaveRef = useRef(false);
  const dirtyPlatformStationIdsRef = useRef([]);
  const skipNextRulesSaveRef = useRef(false);
  const availableAssetLibrary = catalogAssetLibrary;
  const mappingAssetColumns = useMemo(() => splitIntoColumns(availableAssetLibrary), [availableAssetLibrary]);
  const mappingAssetOrderByName = useMemo(() => {
    const next = new Map();
    availableAssetLibrary.forEach((asset, index) => {
      if (asset?.name) {
        next.set(asset.name, index);
      }
    });
    return next;
  }, [availableAssetLibrary]);
  const currentExternalFolders = Array.isArray(externalAssetBrowser?.folders) ? externalAssetBrowser.folders : [];
  const currentExternalFiles = Array.isArray(externalAssetBrowser?.files) ? externalAssetBrowser.files : [];
  const currentExternalAllowedExtensions =
    Array.isArray(externalAssetBrowser?.allowedExtensions) && externalAssetBrowser.allowedExtensions.length > 0 ? externalAssetBrowser.allowedExtensions : [".wav", ".mp3", ".ogg"];
  const selectedLine = lineOptions.find((line) => line.id === selectedLineId) ?? lineOptions[0];
  const availableBroadcastTriggerOptions = useMemo(() => {
    const usedTriggerIds = new Set(rules.map((rule) => (typeof rule?.triggerId === "string" ? rule.triggerId : "")));
    return triggerOptions.filter((option) => !usedTriggerIds.has(option.id));
  }, [rules, triggerOptions]);
  const newRuleTrigger = availableBroadcastTriggerOptions.find((option) => option.id === newRuleTriggerId) ?? availableBroadcastTriggerOptions[0] ?? null;
  const fallbackLanguageKey = useMemo(() => getBroadcastLocaleLanguageKey(locale), [locale]);
  const defaultBindingLanguageLabel = useMemo(() => resolveBroadcastLanguageLabel(fallbackLanguageKey, { t }), [fallbackLanguageKey, t]);
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
    platformTab: t("broadcast.tabs.platform"),
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
    warningTitle: t("broadcast.warning.title"),
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
    delayTab: t("broadcast.tray.tab.delay"),
  };
  const derivedBindingSlotHints = useMemo(() => deriveBindingSlotHintsFromStations(stations), [stations]);
  const effectiveBindingSlotHints = useMemo(() => mergeBindingSlotHints(derivedBindingSlotHints), [derivedBindingSlotHints]);
  const variableLibrary = useMemo(
    () => buildVariableLibrary(VARIABLE_LIBRARY, effectiveBindingSlotHints, broadcastLabels, turnbackPoints),
    [effectiveBindingSlotHints, broadcastLabels, turnbackPoints],
  );
  const platformTurnbackVariables = useMemo(() => variableLibrary.filter((variable) => variable?.nameKey === "broadcast.variable.turnback"), [variableLibrary]);
  const trayAssetLibrary = useMemo(() => buildBroadcastTrayAssetLibrary(availableAssetLibrary, stations), [availableAssetLibrary, stations]);
  const variableColumns = useMemo(() => splitIntoColumns(variableLibrary), [variableLibrary]);
  const broadcastVariableMappingIssue = useMemo(() => buildBroadcastVariableMappingIssue(rules, stations), [rules, stations]);

  function getActiveBroadcastLineId() {
    return selectedLineIdRef.current || selectedLineId || "";
  }

  function applyBroadcastSnapshot(snapshot) {
    const nextVolumeDirty = typeof snapshot?.volumeDirty === "boolean" ? snapshot.volumeDirty : false;
    const nextLineApplied = typeof snapshot?.lineApplied === "boolean" ? snapshot.lineApplied : Boolean(snapshot?.draftApplied);
    const nextLineDraftDirty = typeof snapshot?.lineDraftDirty === "boolean" ? snapshot.lineDraftDirty : Boolean(snapshot?.draftDirty) && !nextVolumeDirty;
    const backendLines = extractBackendLineOptions(snapshot);
    const hasBackendLines = backendLines.length > 0;
    const nextLineOptions = hasBackendLines ? backendLines : hasBackendLineHydratedRef.current ? lineOptionsRef.current : fallbackLineOptions;
    const fallbackSelectedLineId = nextLineOptions[0]?.id ?? "";
    const preservedSelectedLineId = selectedLineIdRef.current && nextLineOptions.some((line) => line.id === selectedLineIdRef.current) ? selectedLineIdRef.current : "";
    const nextSelectedLineId =
      typeof snapshot?.selectedLineId === "string" && nextLineOptions.some((line) => line.id === snapshot.selectedLineId)
        ? snapshot.selectedLineId
        : preservedSelectedLineId || fallbackSelectedLineId;

    if (hasBackendLines) {
      hasBackendLineHydratedRef.current = true;
      setLineOptions(nextLineOptions);
      lastHydratedLineIdRef.current = nextSelectedLineId;
    } else if (!hasBackendLineHydratedRef.current) {
      setLineOptions(nextLineOptions);
    }

    setSelectedLineId(nextSelectedLineId);
    setBroadcastLineApplied(nextLineApplied);
    setBroadcastLineDraftDirty(nextLineDraftDirty);
    setBroadcastVolumeDirty(nextVolumeDirty);
    const snapshotVolume = Number.isFinite(snapshot?.volume) ? snapshot.volume : 80;
    setBroadcastPreviewVolume(snapshotVolume);
    setBroadcastWarnings(Array.isArray(snapshot?.warnings) ? snapshot.warnings.filter((warning) => typeof warning === "string" && warning) : []);
    setIsApplyingBroadcastConfig(false);
    setBroadcastApplyError("");
    setTurnbackPoints(
      Array.isArray(snapshot?.turnbackPoints)
        ? snapshot.turnbackPoints.map((point) => ({
            index: Number.isFinite(Number(point?.index)) ? Number(point.index) : 0,
            stationId: typeof point?.stationId === "string" ? point.stationId : "",
            stationName: typeof point?.stationName === "string" ? point.stationName : "",
            resolved: Boolean(point?.resolved),
          }))
        : [],
    );

    const hasBackendRules = Array.isArray(snapshot?.rules);
    const nextRules = cloneBroadcastRules(hasBackendRules ? snapshot.rules : []);
    skipNextRulesSaveRef.current = hasBackendRules;
    hasBroadcastRulesHydratedRef.current = true;
    lastHydratedRulesLineIdRef.current = nextSelectedLineId;
    setRules(nextRules);
    skipNextPlatformAnnouncementsSaveRef.current = true;
    setPlatformAnnouncements(
      Array.isArray(snapshot?.platformAnnouncements)
        ? snapshot.platformAnnouncements
            .map((entry) => ({
              lineId: typeof entry?.lineId === "string" ? entry.lineId : nextSelectedLineId,
              stationId: typeof entry?.stationId === "string" ? entry.stationId : "",
              stationName: typeof entry?.stationName === "string" ? entry.stationName : "",
              title: typeof entry?.title === "string" ? entry.title : "",
              uiTriggerId: resolvePlatformUiTriggerId(entry?.uiTriggerId || entry?.triggerId),
              enabled: Boolean(entry?.enabled),
              triggerId: typeof entry?.triggerId === "string" ? entry.triggerId : "platform_idle_clear",
              cooldownGameMinutes: Number.isFinite(Number(entry?.cooldownGameMinutes)) ? Number(entry.cooldownGameMinutes) : 20,
              nodes: Array.isArray(entry?.nodes) ? entry.nodes.map(normalizeRuleNode).filter((node) => node && node.id) : [],
            }))
            .filter((entry) => entry.stationId)
        : [],
    );

    const nextCatalogAssetLibrary = Array.isArray(snapshot?.assets)
      ? snapshot.assets
          .filter((asset) => asset && typeof asset.name === "string" && asset.name)
          .map((asset) => ({
            name: asset.name,
            desc: asset.desc || asset.extension || "",
            length: asset.length || "",
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
          lang:
            typeof binding.lang === "string" && binding.lang
              ? binding.lang
              : previousAudioLangByStationAndAsset.get(`${binding.stationId}:${assetName}`) || defaultBindingLanguageLabel,
          langIndex: normalizeLangIndex(binding.langIndex),
          assetName,
        });
        stationBindingsByStationId.set(binding.stationId, currentBindings);
      });

    const lineDrafts = stationBindingDraftsByLine[nextSelectedLineId] || {};

    const nextStations = snapshot.stations.map((station) => {
      const backendAudios = Array.isArray(stationBindingsByStationId.get(station.id)) ? stationBindingsByStationId.get(station.id) : [];
      const snapshotConflicts = Array.isArray(station?.conflictAssets)
        ? station.conflictAssets
            .filter((entry) => entry && typeof entry.assetName === "string" && entry.assetName)
            .map((entry) => ({
              assetName: entry.assetName,
              suggestedLang:
                typeof entry.suggestedLang === "string" && entry.suggestedLang
                  ? entry.suggestedLang
                  : extractBroadcastLanguageHint(entry.assetName, station.name, fallbackLanguageKey, broadcastLabels),
            }))
        : [];
      const override = lineDrafts[station.id];
      const audios = Array.isArray(override?.audios) ? override.audios : backendAudios;
      const conflictAssets = sortBroadcastConflictAssets(
        Array.isArray(override?.conflictAssets) ? override.conflictAssets : snapshotConflicts,
        station.name,
        fallbackLanguageKey,
        broadcastLabels,
      );

      return {
        id: station.id,
        name: station.name,
        audios,
        conflictAssets,
        status: deriveBroadcastStationStatus(audios, conflictAssets),
      };
    });
    setStations(nextStations);
    setPlatformCreateStationIds((current) => {
      const kept = current.filter((stationId) => nextStations.some((station) => station.id === stationId));
      return kept.length > 0 ? kept : nextStations[0]?.id ? [nextStations[0].id] : [];
    });
  }

  useEffect(
    () => () => {
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
    },
    [],
  );

  async function commitBroadcastPreviewVolume(nextVolume) {
    try {
      const result = await workbenchApi.setBroadcastPreviewVolume?.(nextVolume);
      if (!result) {
        return null;
      }

      if (Number.isFinite(result.volume)) {
        setBroadcastPreviewVolume(result.volume);
      }
      if (typeof result.volumeDirty === "boolean") {
        setBroadcastVolumeDirty(result.volumeDirty);
      }
      return result;
    } catch (error) {
      console.error("[RT Broadcast Workbench] save preview volume failed", error);
      return null;
    }
  }

  useEffect(() => {
    const unsubscribe = workbenchApi.onBroadcastAssetPreviewStateChanged?.((payload) => {
      const assetName = payload?.assetName || "";
      const state = payload?.state || "";
      if (isTerminalBroadcastPreviewState(state)) {
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
      if (isTerminalBroadcastPreviewState(state)) {
        setPreviewingRuleId((current) => (ruleId && current && current !== ruleId ? current : ""));
      }
    });

    return () => {
      unsubscribe?.();
    };
  }, [workbenchApi]);

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
        if (!disposed) {
          console.error("[RT Broadcast Workbench] backend refresh failed", error);
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
    if (!hasBroadcastRulesHydratedRef.current || !selectedLineId || selectedLineId !== lastHydratedRulesLineIdRef.current) {
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
          rules: cloneBroadcastRules(rules),
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

      return computedStyle.visibility !== "hidden" && computedStyle.display !== "none" && rect.width > 0 && rect.height > 0;
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
    const isInlineTrayVisible = Boolean(trayContext || mappingTray);
    const justOpened = isInlineTrayVisible && !wasInlineTrayVisibleRef.current;
    wasInlineTrayVisibleRef.current = isInlineTrayVisible;

    if (!justOpened || !trayRef.current || typeof trayRef.current.scrollIntoView !== "function") {
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
      const targetRule = platformRules.find((rule) => rule.id === ruleId) || rules.find((rule) => rule.id === ruleId);
      const targetNode = targetRule?.nodes.find((node) => node.id === action);
      setTrayCategory(targetNode?.type === "variable" ? "variable" : targetNode?.type === "delay" ? "delay" : "asset");
    }
    setTrayContext({ ruleId, action });
  }

  function handleAddNodeToRule(ruleId, nodeTemplate) {
    const actionId = trayContext?.ruleId === ruleId && trayContext?.action && trayContext.action !== "add" ? trayContext.action : "";
    setTrayContext(null);
    const timer = window.setTimeout(() => {
      setRules((current) =>
        current.map((rule) => {
          if (rule.id !== ruleId) {
            return rule;
          }

          if (actionId) {
            return {
              ...rule,
              nodes: rule.nodes.map((node) => (node.id === actionId ? { ...nodeTemplate, id: node.id } : node)),
            };
          }

          return {
            ...rule,
            nodes: [...rule.nodes, { ...nodeTemplate, id: `${Date.now()}-${Math.random().toString(36).slice(2, 6)}` }],
          };
        }),
      );
    }, 140);
    removeTimersRef.current.push(timer);
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
      setRules((current) => current.map((rule) => (rule.id === ruleId ? { ...rule, nodes: rule.nodes.filter((node) => node.id !== nodeId) } : rule)));
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
    if (!newRuleTitle.trim() || !newRuleTrigger) {
      return;
    }

    setRules((current) => [
      ...current,
      {
        id: Date.now().toString(),
        title: newRuleTitle.trim(),
        triggerId: newRuleTrigger.id,
        trigger: newRuleTrigger.label,
        nodes: [],
      },
    ]);
    setIsCreatingRule(false);
    setNewRuleTitle("");
    setNewRuleTriggerId(TRIGGER_OPTIONS[0].id);
    setTriggerDropdownOpen(false);
  }

  async function handleApplyBroadcastConfig() {
    if (isApplyingBroadcastConfig || (broadcastDraftApplied && !broadcastDraftDirty) || broadcastVariableMappingIssue) {
      return;
    }

    setIsApplyingBroadcastConfig(true);
    setBroadcastApplyError("");

    try {
      const result = await workbenchApi.applyBroadcastConfig?.({
        lineId: selectedLineIdRef.current || "",
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

  const broadcastDraftDirty = broadcastLineDraftDirty || broadcastVolumeDirty;
  const broadcastDraftApplied = broadcastLineApplied && !broadcastLineDraftDirty && !broadcastVolumeDirty;
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
      ? broadcastLabels.footerStatusMappingRequired.replace("{station}", broadcastVariableMappingIssue.stationName || "-")
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

  return {
    toolbar: {
      labels: broadcastLabels,
      t,
      activeTab,
      renderedTab,
      tabStage,
      lineOptions,
      selectedLine,
      selectedLineId,
      lineDropdownOpen,
      triggerDropdownOpen,
      broadcastWarnings,
    },
    rules: {
      vehicleRules: rules,
      platformRules,
      availableBroadcastTriggerOptions,
      platformTriggerOptions,
      newRuleTrigger,
      newRuleTriggerId,
      newRuleTitle,
      isCreatingRule,
      trayContext,
      trayCategory,
      removingRuleIds,
      removingNodeIds,
      variableLibrary,
      platformTurnbackVariables,
      delayLibrary,
      trayAssetLibrary,
      platformCreateStationIds,
      stations,
      previewingRuleId,
    },
    mapping: {
      stations,
      mappingTray,
      mappingAssetColumns,
      mappingAssetOrderByName,
      mappingBindFeedback,
      selectedLine,
      fallbackLanguageKey,
      broadcastVariableMappingIssue,
    },
    assets: {
      availableAssetLibrary,
      externalAssetBrowser,
      selectedExternalFiles,
      currentExternalPath,
      currentExternalFolders,
      currentExternalFiles,
      currentExternalAllowedExtensions,
    },
    preview: {
      previewingAssetName,
      previewingRuleId,
      broadcastPreviewVolume,
      isApplyingBroadcastConfig,
      isBroadcastConfigApplied,
      broadcastVariableMappingIssue,
      broadcastFooterTone,
      broadcastFooterText,
      broadcastApplyButtonLabel,
    },
    overlay: {
      pageEnterState,
      shouldRenderAssetExplorer,
      assetExplorerStage,
    },
    refs: {
      pageRootRef,
      trayRef,
      bodyScrollRef,
      bodyPadRef,
      mappingBindingListRef,
      dropdownPortalHostRef,
    },
    actions: {
      handleRootClick,
      handleAssetPreviewToggle,
      handleRulePreviewToggle,
      handleDeleteAsset,
      handleDeleteAllAssets,
      handleImportAssetDirectory,
      handleCloseAssetExplorer,
      handleExternalPathChange,
      handleExternalBack,
      handleToggleExternalFile,
      handleToggleAllExternalFiles,
      handleImportSelectedExternalFiles,
      handleApplyBroadcastConfig,
      handleLocateBroadcastMappingIssue,
      commitBroadcastPreviewVolume,
      setActiveTab,
      setMappingTray,
      setTrayContext,
      setLineDropdownOpen,
      setTriggerDropdownOpen,
      setSelectedLineId,
      setIsCreatingRule,
      setNewRuleTriggerId,
      setNewRuleTitle,
      setPlatformCreateStationIds,
      handleCreateRule,
      toggleTray,
      setTrayCategory,
      handleRemoveNode,
      handleRemoveRule,
      handleAddNodeToRule,
      handleAutoBindStations,
      handleBindStation,
      handleRemoveStationAudio,
      handleDiscardConflict,
      handleResolveStationConflicts,
      updateBindingLanguageDraft,
      getBindingLanguageDraft,
      updateDisambiguationNameDraft,
      getDisambiguationNameDraft,
      handleRemovePlatformRuleNode,
      handleRemovePlatformRule,
      handleAddNodeToPlatformRule,
      handleTogglePlatformRuleStation,
      handleCreatePlatformRule,
      getAvailablePlatformCreateStations,
      isPlatformStationOccupiedByTrigger,
      resolveExternalFolderTargetPath,
    },
  };
}
