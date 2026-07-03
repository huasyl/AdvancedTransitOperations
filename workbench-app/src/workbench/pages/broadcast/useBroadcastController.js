import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useNativeScheduleI18n } from "../../shared/workbench-i18n";
import { getWorkbenchApi } from "../../shared/workbench-api";
import {
  VARIABLE_LIBRARY,
  DELAY_LIBRARY,
  TRIGGER_OPTIONS,
  PLATFORM_TRIGGER_OPTIONS,
  RELEASE_HIDDEN_VEHICLE_TRIGGER_IDS,
  RELEASE_HIDDEN_PLATFORM_TRIGGER_IDS,
  LINE_OPTIONS,
  TAB_TRANSITION_MS,
  PAGE_ENTER_ANIMATION_MS,
  resolvePlatformUiTriggerId,
  resolvePlatformRuntimeTriggerId,
} from "./broadcast-constants";
import { normalizeLangIndex, normalizeRuleNode, cloneBroadcastRules, getBroadcastLocaleLanguageKey, resolveBroadcastLanguageLabel } from "./broadcast-normalize";
import { createEmptyExternalAssetBrowserState, buildBroadcastTrayAssetLibrary, extractBroadcastLanguageHint } from "./broadcast-assets";
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
import useBroadcastApplyOperation from "./useBroadcastApplyOperation";
import useBroadcastDraftStore from "./useBroadcastDraftStore";

export default function useBroadcastController({ pageEnterSequence = 0, activeTransportMode = "train" } = {}) {
  const { locale, t } = useNativeScheduleI18n();
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const broadcastApplyOperation = useBroadcastApplyOperation(workbenchApi);
  const draftStore = useBroadcastDraftStore();
  const delayLibrary = useMemo(() => DELAY_LIBRARY.map((delay) => ({ ...delay, name: t(delay.nameKey), desc: t(delay.descKey) })), [t]);
  const triggerOptions = useMemo(
    () =>
      TRIGGER_OPTIONS.filter(
        (option) => !RELEASE_HIDDEN_VEHICLE_TRIGGER_IDS.includes(option.id),
      ).map((option) => ({ ...option, label: t(option.labelKey) })),
    [t],
  );
  const platformTriggerOptions = useMemo(
    () =>
      PLATFORM_TRIGGER_OPTIONS.filter(
        (option) => !RELEASE_HIDDEN_PLATFORM_TRIGGER_IDS.includes(option.id),
      ).map((option) => ({ ...option, label: t(option.labelKey) })),
    [t],
  );
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
  const [pendingAssetDeletionNamesByMode, setPendingAssetDeletionNamesByMode] = useState({});
  const [previewingAssetName, setPreviewingAssetName] = useState("");
  const [previewingRuleId, setPreviewingRuleId] = useState("");
  const [broadcastPreviewVolume, setBroadcastPreviewVolume] = useState(80);
  const [broadcastAppliedVolume, setBroadcastAppliedVolume] = useState(80);
  const [broadcastLineDraftDirty, setBroadcastLineDraftDirty] = useState(false);
  const [broadcastVolumeDirty, setBroadcastVolumeDirty] = useState(false);
  const [broadcastLocalDraftDirty, setBroadcastLocalDraftDirty] = useState(false);
  const [isApplyingBroadcastConfig, setIsApplyingBroadcastConfig] = useState(false);
  const [broadcastApplyPhase, setBroadcastApplyPhase] = useState("");
  const [broadcastApplyError, setBroadcastApplyError] = useState("");
  const [isAssetExplorerOpen, setIsAssetExplorerOpen] = useState(false);
  const [shouldRenderAssetExplorer, setShouldRenderAssetExplorer] = useState(false);
  const [assetExplorerStage, setAssetExplorerStage] = useState("closed");
  const [externalAssetBrowser, setExternalAssetBrowser] = useState(createEmptyExternalAssetBrowserState());
  const [selectedExternalFiles, setSelectedExternalFiles] = useState([]);
  const [currentExternalPath, setCurrentExternalPath] = useState("");
  const [lineOptions, setLineOptions] = useState(fallbackLineOptions);
  const [selectedLineId, setSelectedLineId] = useState(fallbackLineOptions[0]?.id ?? LINE_OPTIONS[0].id);
  const [broadcastContentLineId, setBroadcastContentLineId] = useState(fallbackLineOptions[0]?.id ?? LINE_OPTIONS[0].id);
  const [platformCreateStationIds, setPlatformCreateStationIds] = useState([]);
  const [bindingSlotHints, setBindingSlotHints] = useState([]);
  const [lineDropdownOpen, setLineDropdownOpen] = useState(false);
  const [isCreatingRule, setIsCreatingRule] = useState(false);
  const [newRuleTitle, setNewRuleTitle] = useState("");
  const [newRuleTriggerId, setNewRuleTriggerId] = useState(
    TRIGGER_OPTIONS.find(
      (option) => !RELEASE_HIDDEN_VEHICLE_TRIGGER_IDS.includes(option.id),
    )?.id ?? TRIGGER_OPTIONS[0].id,
  );
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
  const broadcastContentLineIdRef = useRef(selectedLineId);
  const activeTransportModeRef = useRef(activeTransportMode);
  const pendingDeleteAllAssetsByModeRef = useRef({});
  const platformRuleTitleMemoryRef = useRef({});
  const platformRuleIdMemoryRef = useRef({});
  const dirtyPlatformStationIdsRef = useRef([]);
  const skipNextRulesSaveRef = useRef(false);
  const pendingAssetDeletionLookup = useMemo(
    () => pendingAssetDeletionNamesByMode[normalizeBroadcastMode(activeTransportMode)] || {},
    [activeTransportMode, pendingAssetDeletionNamesByMode],
  );
  const availableAssetLibrary = useMemo(
    () => catalogAssetLibrary.filter((asset) => asset?.name && !pendingAssetDeletionLookup[asset.name]),
    [catalogAssetLibrary, pendingAssetDeletionLookup],
  );
  const bindableAssetLibrary = useMemo(
    () => availableAssetLibrary.filter((asset) => !asset?.missing),
    [availableAssetLibrary],
  );
  const mappingAssetColumns = useMemo(() => splitIntoColumns(bindableAssetLibrary), [bindableAssetLibrary]);
  const mappingAssetOrderByName = useMemo(() => {
    const next = new Map();
    bindableAssetLibrary.forEach((asset, index) => {
      if (asset?.name) {
        next.set(asset.name, index);
      }
    });
    return next;
  }, [bindableAssetLibrary]);
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
  broadcastContentLineIdRef.current = broadcastContentLineId;
  activeTransportModeRef.current = activeTransportMode;
  const broadcastLabels = {
    t,
    sidebarTitle: t("broadcast.sidebar.title"),
    localAssets: t("broadcast.sidebar.localAssets"),
    assetFileName: t("broadcast.sidebar.fileName"),
    assetDuration: t("broadcast.sidebar.duration"),
    importAsset: t("broadcast.sidebar.import"),
    deleteAsset: t("broadcast.sidebar.deleteAsset"),
    deleteAllAssets: t("broadcast.sidebar.deleteAllAssets"),
    assetInUseCannotDelete: t("broadcast.sidebar.assetInUseCannotDelete"),
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
    mapClearAll: t("broadcast.mapping.clearAll"),
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
  const trayAssetLibrary = useMemo(() => buildBroadcastTrayAssetLibrary(bindableAssetLibrary, stations), [bindableAssetLibrary, stations]);
  const variableColumns = useMemo(() => splitIntoColumns(variableLibrary), [variableLibrary]);
  const broadcastVariableMappingIssue = useMemo(() => buildBroadcastVariableMappingIssue(rules, stations), [rules, stations]);
  const globalBroadcastVariableMappingIssue = useMemo(() => {
    const dirtyLineIds = draftStore.getDirtyLineIds(activeTransportMode);
    for (let index = 0; index < dirtyLineIds.length; index += 1) {
      const lineId = dirtyLineIds[index];
      const draft = draftStore.getLineDraft(lineId);
      if (!draft) {
        continue;
      }

      const issue = buildBroadcastVariableMappingIssue(draft.rules, draft.stationsForUi);
      if (issue) {
        return {
          ...issue,
          lineId,
        };
      }
    }

    return null;
  }, [activeTransportMode, draftStore]);

  function getActiveBroadcastLineId() {
    return selectedLineIdRef.current || selectedLineId || "";
  }

  function normalizeBroadcastMode(mode = "train") {
    return String(mode || "train").trim().toLowerCase() || "train";
  }

  function getPendingAssetDeletionLookup(mode = activeTransportMode) {
    return pendingAssetDeletionNamesByMode[normalizeBroadcastMode(mode)] || {};
  }

  function getPendingAssetDeletionNames(mode = activeTransportMode) {
    return Object.keys(getPendingAssetDeletionLookup(mode));
  }

  function hasPendingAssetDeletions(mode = activeTransportMode) {
    return getPendingAssetDeletionNames(mode).length > 0
      || isDeleteAllAssetsPending(mode);
  }

  function isDeleteAllAssetsPending(mode = activeTransportMode) {
    return Boolean(pendingDeleteAllAssetsByModeRef.current[normalizeBroadcastMode(mode)]);
  }

  function queuePendingAssetDeletions(assetNames, options = {}, mode = activeTransportMode) {
    const normalizedNames = Array.from(
      new Set(
        (Array.isArray(assetNames) ? assetNames : [])
          .map((assetName) => String(assetName || "").trim())
          .filter((assetName) => assetName),
      ),
    );
    if (normalizedNames.length === 0) {
      return;
    }

    const modeKey = normalizeBroadcastMode(mode);
    if (options?.deleteAll) {
      pendingDeleteAllAssetsByModeRef.current = {
        ...pendingDeleteAllAssetsByModeRef.current,
        [modeKey]: true,
      };
    }
    setPendingAssetDeletionNamesByMode((current) => {
      const nextModeEntries = { ...(current[modeKey] || {}) };
      let changed = false;
      normalizedNames.forEach((assetName) => {
        if (!nextModeEntries[assetName]) {
          nextModeEntries[assetName] = true;
          changed = true;
        }
      });
      if (!changed) {
        return current;
      }

      return {
        ...current,
        [modeKey]: nextModeEntries,
      };
    });
  }

  function clearPendingAssetDeletions(assetNames = null, mode = activeTransportMode) {
    const modeKey = normalizeBroadcastMode(mode);
    if (assetNames == null) {
      const nextDeleteAllModes = { ...pendingDeleteAllAssetsByModeRef.current };
      delete nextDeleteAllModes[modeKey];
      pendingDeleteAllAssetsByModeRef.current = nextDeleteAllModes;
    }
    setPendingAssetDeletionNamesByMode((current) => {
      const currentModeEntries = current[modeKey] || {};
      if (assetNames == null) {
        if (Object.keys(currentModeEntries).length === 0) {
          return current;
        }

        const next = { ...current };
        delete next[modeKey];
        return next;
      }

      const nextModeEntries = { ...currentModeEntries };
      let changed = false;
      (Array.isArray(assetNames) ? assetNames : []).forEach((assetName) => {
        const normalizedAssetName = String(assetName || "").trim();
        if (normalizedAssetName && nextModeEntries[normalizedAssetName]) {
          delete nextModeEntries[normalizedAssetName];
          changed = true;
        }
      });
      if (!changed) {
        return current;
      }

      const next = { ...current };
      if (Object.keys(nextModeEntries).length > 0) {
        next[modeKey] = nextModeEntries;
      } else {
        delete next[modeKey];
        const nextDeleteAllModes = { ...pendingDeleteAllAssetsByModeRef.current };
        delete nextDeleteAllModes[modeKey];
        pendingDeleteAllAssetsByModeRef.current = nextDeleteAllModes;
      }
      return next;
    });
  }

  function matchesBroadcastMode(payload, mode = activeTransportMode) {
    const payloadMode = String(payload?.mode || "").trim().toLowerCase();
    return payloadMode.length > 0 && payloadMode === normalizeBroadcastMode(mode);
  }

  function isCurrentBroadcastMode(mode) {
    return normalizeBroadcastMode(activeTransportModeRef.current) === normalizeBroadcastMode(mode);
  }

  function lineMatchesBroadcastMode(lineId, mode = activeTransportMode) {
    const modeKey = normalizeBroadcastMode(mode);
    const normalizedLineId = String(lineId || "");
    if (!normalizedLineId) {
      return false;
    }

    if (normalizedLineId.includes(":")) {
      return normalizedLineId.toLowerCase().startsWith(`${modeKey}:`);
    }

    return modeKey === "train";
  }

  function removeModeScopedLineState(current, mode = activeTransportMode) {
    const next = {};
    Object.entries(current || {}).forEach(([lineId, value]) => {
      if (!lineMatchesBroadcastMode(lineId, mode)) {
        next[lineId] = value;
      }
    });
    return next;
  }

  function clearTransientBroadcastUiEffects() {
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
    if (bodyPadRef.current) {
      bodyPadRef.current.style.transition = "";
      bodyPadRef.current.style.transform = "";
    }
  }

  function clearBroadcastFrontendState(mode = activeTransportMode) {
    clearTransientBroadcastUiEffects();
    const dirtyLineIds = draftStore.getDirtyLineIds(mode);
    if (dirtyLineIds.length > 0) {
      draftStore.clearLineDrafts(dirtyLineIds);
    }
    draftStore.clearVolumeDraft(null, mode);
    clearPendingAssetDeletions(null, mode);
    dirtyPlatformStationIdsRef.current = [];
    broadcastApplyOperation.resetApplyState();
    setStationBindingDraftsByLine((current) => removeModeScopedLineState(current, mode));
    setBindingLangDraftsByLine((current) => removeModeScopedLineState(current, mode));
    setDisambiguationNamesByLine((current) => removeModeScopedLineState(current, mode));
    setMappingBindFeedback(null);
    setTrayContext(null);
    setMappingTray(null);
    setIsCreatingRule(false);
    setNewRuleTitle("");
    setRemovingRuleIds({});
    setRemovingNodeIds({});
    setPreviewingAssetName("");
    setPreviewingRuleId("");
    setSelectedExternalFiles([]);
    setCurrentExternalPath("");
    setExternalAssetBrowser(createEmptyExternalAssetBrowserState());
    setIsAssetExplorerOpen(false);
    setShouldRenderAssetExplorer(false);
    setAssetExplorerStage("closed");
    setBroadcastLineDraftDirty(false);
    setBroadcastLocalDraftDirty(false);
    setBroadcastVolumeDirty(false);
  }

  async function stopBroadcastPreviewsForRestore(mode = activeTransportMode) {
    const stopPromises = [
      workbenchApi.stopBroadcastAssetPreview?.({ assetName: previewingAssetName || "", mode }),
      workbenchApi.stopBroadcastRulePreview?.({ ruleId: previewingRuleId || "", mode }),
    ].filter(Boolean);

    if (stopPromises.length === 0) {
      return;
    }

    const results = await Promise.allSettled(stopPromises);
    results.forEach((result) => {
      if (result.status === "rejected") {
        console.error("[RT Broadcast Workbench] stop preview during restore failed", result.reason);
      }
    });
  }

  async function restoreBroadcastFrontendStateFromBackend(mode = activeTransportMode, applyError = "") {
    let snapshot = null;
    let refreshError = null;

    await stopBroadcastPreviewsForRestore(mode);

    try {
      snapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineIdRef.current || "");
    } catch (error) {
      refreshError = error;
    }

    if (!snapshot) {
      try {
        snapshot = await workbenchApi.loadBroadcastSnapshot?.(selectedLineIdRef.current || "");
      } catch (error) {
        refreshError = refreshError || error;
      }
    }

    if (snapshot && matchesBroadcastMode(snapshot, mode) && isCurrentBroadcastMode(mode)) {
      clearBroadcastFrontendState(mode);
      applyBroadcastSnapshot(snapshot);
      setIsApplyingBroadcastConfig(false);
      setBroadcastApplyPhase("");
      setBroadcastApplyError(applyError || "");
      return true;
    }

    setIsApplyingBroadcastConfig(false);
    setBroadcastApplyPhase("");
    setBroadcastApplyError(
      applyError
        || (refreshError instanceof Error ? refreshError.message : t("broadcast.footer.applyFailedFallback")),
    );
    return false;
  }

  function cloneBroadcastStationsForDraft(source) {
    return (Array.isArray(source) ? source : []).map((station) => ({
      ...station,
      audios: Array.isArray(station?.audios) ? station.audios.map((audio) => ({ ...audio })) : [],
      conflictAssets: Array.isArray(station?.conflictAssets) ? station.conflictAssets.map((entry) => ({ ...entry })) : [],
    }));
  }

  function cloneBroadcastPlatformAnnouncementsForDraft(source) {
    return (Array.isArray(source) ? source : []).map((announcement) => ({
      ...announcement,
      nodes: Array.isArray(announcement?.nodes) ? announcement.nodes.map((node) => ({ ...node })) : [],
    }));
  }

  function buildCurrentBroadcastLineDraft(overrides = {}) {
    const stationsForUi = cloneBroadcastStationsForDraft(overrides.stationsForUi ?? overrides.stations ?? stations);
    return {
      rules: cloneBroadcastRules(overrides.rules ?? rules),
      stationBindings: stationsForUi.flatMap((station) =>
        (Array.isArray(station?.audios) ? station.audios : [])
          .filter((audio) => audio && audio.assetName)
          .map((audio, index) => ({
            stationId: station.id,
            lang: typeof audio.lang === "string" ? audio.lang : "",
            langIndex: normalizeLangIndex(audio.langIndex ?? index + 1),
            assetName: audio.assetName,
          })),
      ),
      platformAnnouncements: cloneBroadcastPlatformAnnouncementsForDraft(overrides.platformAnnouncements ?? platformAnnouncements),
      stationsForUi,
    };
  }

  function storeBroadcastLocalDraft(lineId, draft = null) {
    if (!lineId) {
      return;
    }

    draftStore.setLineDraft(lineId, draft || buildCurrentBroadcastLineDraft());
    setBroadcastLocalDraftDirty(draftStore.getDirtyLineIds(activeTransportMode).length > 0);
  }

  function getBroadcastLineDraftGeneration(lineId) {
    return draftStore.getLineDraftGeneration(lineId);
  }

  function isBroadcastLineLocalDraftDirty(lineId) {
    return Boolean(lineId && draftStore.getDirtyLineIds(activeTransportMode).includes(lineId));
  }

  function setSelectedLineLocalDraftDirty(lineId) {
    const isDirty = isBroadcastLineLocalDraftDirty(lineId);
    setBroadcastLocalDraftDirty(draftStore.getDirtyLineIds(activeTransportMode).length > 0);
    setBroadcastVolumeDirty(draftStore.hasVolumeDirty(activeTransportMode));
    return isDirty;
  }

  function markBroadcastDraftDirty(lineId = getActiveBroadcastLineId(), draft = null) {
    if (!lineId) {
      return;
    }

    draftStore.setLineDraft(lineId, draft || buildCurrentBroadcastLineDraft());
    if (lineId === selectedLineIdRef.current) {
      setBroadcastContentLineId(lineId);
    }
    setBroadcastLocalDraftDirty(draftStore.getDirtyLineIds(activeTransportMode).length > 0);
    setBroadcastVolumeDirty(draftStore.hasVolumeDirty(activeTransportMode));
    setBroadcastApplyError("");
    broadcastApplyOperation.resetApplyState();
  }

  function clearBroadcastLocalDraft(lineId) {
    if (!lineId) {
      return;
    }

    draftStore.clearLineDrafts([lineId]);
    setBroadcastLocalDraftDirty(draftStore.getDirtyLineIds(activeTransportMode).length > 0);
  }

  function applyBroadcastLocalDraft(lineId) {
    const draft = draftStore.getLineDraft(lineId);
    if (!draft) {
      return false;
    }

    skipNextRulesSaveRef.current = true;
    setRules(cloneBroadcastRules(draft.rules));
    setPlatformAnnouncements(cloneBroadcastPlatformAnnouncementsForDraft(draft.platformAnnouncements));
    setStations(cloneBroadcastStationsForDraft(draft.stationsForUi));
    setBroadcastPreviewVolume(draftStore.getVolumeDraft(broadcastAppliedVolume, activeTransportMode));
    setBroadcastContentLineId(lineId);
    setSelectedLineLocalDraftDirty(lineId);
    return true;
  }

  function handleBroadcastLineSelect(lineId) {
    setSelectedLineId(lineId);
    if (!applyBroadcastLocalDraft(lineId)) {
      setBroadcastContentLineId("");
      skipNextRulesSaveRef.current = true;
      setRules([]);
      setPlatformAnnouncements([]);
      setStations([]);
      setSelectedLineLocalDraftDirty(lineId);
    }
  }

  function applyBroadcastSnapshot(snapshot) {
    if (snapshot && !matchesBroadcastMode(snapshot)) {
      return;
    }

    const backendLines = extractBackendLineOptions(snapshot);
    const hasBackendLines = backendLines.length > 0;
    const nextLineOptions = hasBackendLines ? backendLines : hasBackendLineHydratedRef.current ? lineOptionsRef.current : fallbackLineOptions;
    const fallbackSelectedLineId = nextLineOptions[0]?.id ?? "";
    const preservedSelectedLineId = selectedLineIdRef.current && nextLineOptions.some((line) => line.id === selectedLineIdRef.current) ? selectedLineIdRef.current : "";
    const nextSelectedLineId =
      typeof snapshot?.selectedLineId === "string" && nextLineOptions.some((line) => line.id === snapshot.selectedLineId)
        ? snapshot.selectedLineId
        : preservedSelectedLineId || fallbackSelectedLineId;
    const preserveLocalDraft = isBroadcastLineLocalDraftDirty(nextSelectedLineId);

    if (hasBackendLines) {
      hasBackendLineHydratedRef.current = true;
      setLineOptions(nextLineOptions);
      lastHydratedLineIdRef.current = nextSelectedLineId;
    } else if (!hasBackendLineHydratedRef.current) {
      setLineOptions(nextLineOptions);
    }

    setSelectedLineId(nextSelectedLineId);
    setBroadcastLineDraftDirty(false);
    setBroadcastLocalDraftDirty(draftStore.getDirtyLineIds(activeTransportMode).length > 0);
    setBroadcastVolumeDirty(draftStore.hasVolumeDirty(activeTransportMode));
    if (!preserveLocalDraft) {
      setSelectedLineLocalDraftDirty(nextSelectedLineId);
    }
    const snapshotVolume = Number.isFinite(snapshot?.volume) ? snapshot.volume : 80;
    setBroadcastAppliedVolume(snapshotVolume);
    if (!draftStore.hasVolumeDirty(activeTransportMode)) {
      setBroadcastPreviewVolume(draftStore.getVolumeDraft(snapshotVolume, activeTransportMode));
    }
    setBroadcastWarnings(Array.isArray(snapshot?.warnings) ? snapshot.warnings.filter((warning) => typeof warning === "string" && warning) : []);
    if (!preserveLocalDraft) {
      setIsApplyingBroadcastConfig(false);
      setBroadcastApplyPhase("");
      setBroadcastApplyError("");
    }
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
    if (!preserveLocalDraft) {
      skipNextRulesSaveRef.current = hasBackendRules;
      hasBroadcastRulesHydratedRef.current = true;
      lastHydratedRulesLineIdRef.current = nextSelectedLineId;
      setRules(nextRules);
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
    }

    const nextCatalogAssetLibrary = Array.isArray(snapshot?.assets)
      ? snapshot.assets
          .filter((asset) => asset && typeof asset.name === "string" && asset.name)
          .map((asset) => ({
            name: asset.name,
            desc: asset.missing ? `${asset.desc || asset.extension || ""} [Missing file]` : asset.desc || asset.extension || "",
            length: asset.length || "",
            missing: Boolean(asset.missing || !asset.path),
          }))
      : [];
    setCatalogAssetLibrary(nextCatalogAssetLibrary);

    if (preserveLocalDraft) {
      applyBroadcastLocalDraft(nextSelectedLineId);
      return;
    }

    if (!Array.isArray(snapshot?.stations)) {
      setStations([]);
      setBroadcastContentLineId(nextSelectedLineId);
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
      const audios = backendAudios;
      const conflictAssets = sortBroadcastConflictAssets(
        snapshotConflicts,
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
    setBroadcastContentLineId(nextSelectedLineId);
    setPlatformCreateStationIds((current) => {
      const kept = current.filter((stationId) => nextStations.some((station) => station.id === stationId));
      return kept.length > 0 ? kept : nextStations[0]?.id ? [nextStations[0].id] : [];
    });
  }

  const {
    platformRules,
    getAvailablePlatformCreateStations,
    isPlatformStationOccupiedByTrigger,
    handleCreatePlatformRule,
    handleAddNodeToPlatformRule,
    handleRemovePlatformRuleNode,
    handleRemovePlatformRule,
    handleTogglePlatformRuleStation,
  } = useBroadcastPlatformRules({
    platformAnnouncements,
    stations,
    selectedLineId,
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
  });

  const {
    updateBindingLanguageDraft,
    updateDisambiguationNameDraft,
    getBindingLanguageDraft,
    getDisambiguationNameDraft,
    handleBindStation,
    handleRemoveStationAudio,
    handleClearAllStationBindings,
    handleDiscardConflict,
    handleResolveStationConflicts,
  } = useBroadcastStationBindings({
    stations,
    bindingLangDraftsByLine,
    disambiguationNamesByLine,
    mappingBindFeedback,
    fallbackLanguageKey,
    defaultBindingLanguageLabel,
    broadcastLabels,
    selectedLineIdRef,
    bodyScrollRef,
    bodyPadRef,
    mappingBindingListRef,
    mappingBindFeedbackTimerRef,
    mappingBindScrollFrameRef,
    mappingBindTransformCleanupRef,
    removeTimersRef,
    buildCurrentBroadcastLineDraft,
    markBroadcastDraftDirty,
    setBindingLangDraftsByLine,
    setDisambiguationNamesByLine,
    setMappingBindFeedback,
    setStations,
    setMappingTray,
  });

  const {
    handleImportAssetDirectory,
    handleAssetPreviewToggle,
    handleRulePreviewToggle,
    handleDeleteAsset,
    handleDeleteAllAssets,
    handleAutoBindStations,
    handleCloseAssetExplorer,
    handleExternalPathChange,
    resolveExternalFolderTargetPath,
    handleExternalBack,
    handleToggleExternalFile,
    handleToggleAllExternalFiles,
    handleImportSelectedExternalFiles,
    assetDeleteBlockedNames,
    deleteAllAssetsKey,
    showAssetDeleteBlocked,
  } = useBroadcastAssets({
    activeTransportMode,
    workbenchApi,
    isAssetExplorerOpen,
    shouldRenderAssetExplorer,
    currentExternalPath,
    currentExternalFolders,
    currentExternalFiles,
    externalAssetBrowser,
    selectedExternalFiles,
    previewingAssetName,
    previewingRuleId,
    availableAssetLibrary,
    bindableAssetLibrary,
    selectedLineIdRef,
    rules,
    stations,
    platformAnnouncements,
    defaultBindingLanguageLabel,
    draftStore,
    buildCurrentBroadcastLineDraft,
    markBroadcastDraftDirty,
    queuePendingAssetDeletions,
    setShouldRenderAssetExplorer,
    setAssetExplorerStage,
    setSelectedExternalFiles,
    setCurrentExternalPath,
    setExternalAssetBrowser,
    setIsAssetExplorerOpen,
    setPreviewingAssetName,
    setPreviewingRuleId,
    setBindingLangDraftsByLine,
    setDisambiguationNamesByLine,
    setStations,
    setRules,
    setPlatformAnnouncements,
    closeInlineMenus,
  });

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
    const normalizedVolume = Number.isFinite(Number(nextVolume))
      ? Math.max(0, Math.min(100, Math.round(Number(nextVolume))))
      : 80;
    setBroadcastPreviewVolume(normalizedVolume);
    draftStore.setVolumeDraft(normalizedVolume, activeTransportMode);
    setBroadcastVolumeDirty(true);
    setBroadcastLocalDraftDirty(draftStore.getDirtyLineIds(activeTransportMode).length > 0);
    setBroadcastApplyError("");
    broadcastApplyOperation.resetApplyState();
    return { success: true, volume: normalizedVolume, volumeDirty: true };
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
    hasBroadcastHydratedRef.current = false;
    hasBackendLineHydratedRef.current = false;
    hasBroadcastRulesHydratedRef.current = false;
    lastHydratedLineIdRef.current = "";
    lastHydratedRulesLineIdRef.current = "";
    skipNextRulesSaveRef.current = true;
    setPreviewingAssetName("");
    setPreviewingRuleId("");
    setSelectedExternalFiles([]);
    setExternalAssetBrowser(createEmptyExternalAssetBrowserState());
    setCatalogAssetLibrary([]);
    setIsApplyingBroadcastConfig(false);
    setBroadcastApplyPhase("");
    setBroadcastApplyError("");
    broadcastApplyOperation.resetApplyState();

    async function hydrateBroadcastSnapshot() {
      try {
        const requestMode = activeTransportMode;
        const snapshot = await workbenchApi.loadBroadcastSnapshot?.(selectedLineIdRef.current);
        if (disposed) {
          return;
        }

        if (!matchesBroadcastMode(snapshot, requestMode)) {
          return;
        }

        applyBroadcastSnapshot(snapshot);

        if (extractBackendLineOptions(snapshot).length === 0) {
          try {
            const refreshedSnapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineIdRef.current);
            if (!disposed && matchesBroadcastMode(refreshedSnapshot, requestMode) && extractBackendLineOptions(refreshedSnapshot).length > 0) {
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
  }, [activeTransportMode, workbenchApi]);

  useEffect(() => {
    let disposed = false;
    const unsubscribe = workbenchApi.onCatalogChanged?.((event) => {
      const requestMode = String(event?.mode || "").trim().toLowerCase();
      if (!requestMode || requestMode !== normalizeBroadcastMode(activeTransportMode)) {
        return;
      }

      async function refreshLines() {
        try {
          const snapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineIdRef.current || "");
          if (!disposed && isCurrentBroadcastMode(requestMode) && matchesBroadcastMode(snapshot, requestMode)) {
            applyBroadcastSnapshot(snapshot);
          }
        } catch (error) {
          if (!disposed) {
            console.error("[RT Broadcast Workbench] catalog refresh failed", error);
          }
        }
      }

      refreshLines();
    });

    return () => {
      disposed = true;
      unsubscribe?.();
    };
  }, [activeTransportMode, workbenchApi]);

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
        if (!disposed && matchesBroadcastMode(snapshot)) {
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
  }, [activeTransportMode, selectedLineId, workbenchApi]);

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
  }, [activeTransportMode, selectedLineId, workbenchApi]);

  useEffect(() => {
    if (!hasBroadcastRulesHydratedRef.current || !selectedLineId || selectedLineId !== lastHydratedRulesLineIdRef.current) {
      return undefined;
    }

    if (skipNextRulesSaveRef.current) {
      skipNextRulesSaveRef.current = false;
      return undefined;
    }

    markBroadcastDraftDirty();
    return undefined;
  }, [rules, selectedLineId]);

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

          if (!matchesBroadcastMode(snapshot)) {
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
  }, [activeTransportMode, pageEnterSequence, workbenchApi]);

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
    const lineId = getActiveBroadcastLineId();
    const nextNode = { ...nodeTemplate, id: actionId || `${Date.now()}-${Math.random().toString(36).slice(2, 6)}` };
    const nextRules = rules.map((rule) => {
      if (rule.id !== ruleId) {
        return rule;
      }

      if (actionId) {
        return {
          ...rule,
          nodes: rule.nodes.map((node) => (node.id === actionId ? nextNode : node)),
        };
      }

      return {
        ...rule,
        nodes: [...rule.nodes, nextNode],
      };
    });
    markBroadcastDraftDirty(lineId, buildCurrentBroadcastLineDraft({ rules: nextRules }));
    setTrayContext(null);
    const timer = window.setTimeout(() => {
      if (selectedLineIdRef.current === lineId) {
        setRules(nextRules);
      }
    }, 140);
    removeTimersRef.current.push(timer);
  }

  function handleRemoveNode(ruleId, nodeId) {
    const removalKey = `${ruleId}:${nodeId}`;
    if (removingNodeIds[removalKey]) {
      return;
    }

    const lineId = getActiveBroadcastLineId();
    const nextRules = rules.map((rule) => (rule.id === ruleId ? { ...rule, nodes: rule.nodes.filter((node) => node.id !== nodeId) } : rule));
    markBroadcastDraftDirty(lineId, buildCurrentBroadcastLineDraft({ rules: nextRules }));
    setRemovingNodeIds((current) => ({ ...current, [removalKey]: true }));
    if (trayContext?.action === nodeId) {
      setTrayContext(null);
    }

    const timer = window.setTimeout(() => {
      if (selectedLineIdRef.current === lineId) {
        setRules(nextRules);
      }
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

    const lineId = getActiveBroadcastLineId();
    const nextRules = rules.filter((rule) => rule.id !== ruleId);
    markBroadcastDraftDirty(lineId, buildCurrentBroadcastLineDraft({ rules: nextRules }));
    setRemovingRuleIds((current) => ({ ...current, [ruleId]: true }));
    if (trayContext?.ruleId === ruleId) {
      setTrayContext(null);
    }

    const timer = window.setTimeout(() => {
      if (selectedLineIdRef.current === lineId) {
        setRules(nextRules);
      }
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

    const lineId = getActiveBroadcastLineId();
    const nextRules = [
      ...rules,
      {
        id: Date.now().toString(),
        title: newRuleTitle.trim(),
        triggerId: newRuleTrigger.id,
        trigger: newRuleTrigger.label,
        nodes: [],
      },
    ];
    markBroadcastDraftDirty(lineId, buildCurrentBroadcastLineDraft({ rules: nextRules }));
    setRules(nextRules);
    setIsCreatingRule(false);
    setNewRuleTitle("");
    setNewRuleTriggerId(
      triggerOptions[0]?.id ??
        TRIGGER_OPTIONS.find(
          (option) => !RELEASE_HIDDEN_VEHICLE_TRIGGER_IDS.includes(option.id),
        )?.id ??
        TRIGGER_OPTIONS[0].id,
    );
    setTriggerDropdownOpen(false);
  }

  async function flushPendingAssetDeletions(mode = activeTransportMode) {
    const pendingAssetNames = getPendingAssetDeletionNames(mode);
    if (pendingAssetNames.length === 0) {
      return { deletedAssetNames: [], blockedAssetNames: [], error: "" };
    }

    const pendingAssetNameSet = new Set(pendingAssetNames);
    if (previewingAssetName && pendingAssetNameSet.has(previewingAssetName)) {
      try {
        await workbenchApi.stopBroadcastAssetPreview?.(previewingAssetName);
      } catch (error) {
        console.error("[RT Broadcast Workbench] stop asset preview before apply delete failed", error);
      }
    }

    if (isDeleteAllAssetsPending(mode)) {
      try {
        const result = await workbenchApi.deleteAllBroadcastAssets?.({ mode });
        if (result?.success) {
          setCatalogAssetLibrary((current) =>
            current.filter((asset) => !pendingAssetNameSet.has(asset?.name)),
          );
          setPreviewingAssetName((current) => (pendingAssetNameSet.has(current) ? "" : current));
          clearPendingAssetDeletions(null, mode);
          return { deletedAssetNames: pendingAssetNames, blockedAssetNames: [], error: "" };
        }

        const errorMessage = result?.error === "broadcast-asset-in-use"
          ? "Some assets are still referenced."
          : result?.error || "Delete all assets failed.";
        showAssetDeleteBlocked("", mode);
        return { deletedAssetNames: [], blockedAssetNames: pendingAssetNames, error: errorMessage };
      } catch (error) {
        console.error("[RT Broadcast Workbench] apply-time delete all assets failed", error);
        showAssetDeleteBlocked("", mode);
        return {
          deletedAssetNames: [],
          blockedAssetNames: pendingAssetNames,
          error: error instanceof Error ? error.message : "Delete all assets failed.",
        };
      }
    }

    const deletedAssetNames = [];
    const blockedAssetNames = [];
    const blockedErrors = [];
    for (let index = 0; index < pendingAssetNames.length; index += 1) {
      const assetName = pendingAssetNames[index];
      try {
        const result = await workbenchApi.deleteBroadcastAsset?.({ assetName, mode });
        if (result?.success) {
          deletedAssetNames.push(assetName);
        } else {
          blockedAssetNames.push(assetName);
          blockedErrors.push(
            result?.error === "broadcast-asset-in-use"
              ? "Some assets are still referenced."
              : result?.error || `Delete failed: ${assetName}`,
          );
        }
      } catch (error) {
        console.error("[RT Broadcast Workbench] apply-time asset delete failed", error);
        blockedAssetNames.push(assetName);
        blockedErrors.push(error instanceof Error ? error.message : `Delete failed: ${assetName}`);
      }
    }

    if (deletedAssetNames.length > 0) {
      const deletedAssetNameSet = new Set(deletedAssetNames);
      setCatalogAssetLibrary((current) => current.filter((asset) => !deletedAssetNameSet.has(asset?.name)));
      setPreviewingAssetName((current) => (deletedAssetNameSet.has(current) ? "" : current));
    }
    if (deletedAssetNames.length > 0) {
      clearPendingAssetDeletions(deletedAssetNames, mode);
    }
    blockedAssetNames.forEach((assetName) => {
      showAssetDeleteBlocked(assetName, mode);
    });
    return {
      deletedAssetNames,
      blockedAssetNames,
      error: blockedErrors[0] || "",
    };
  }

  function buildBroadcastApplyOperationRequest(mode = activeTransportMode) {
    return draftStore.buildApplyRequest(mode);
  }

  async function handleApplyBroadcastConfig() {
    if (
      isApplyingBroadcastConfig ||
      (!draftStore.hasDirty(activeTransportMode) && !hasPendingAssetDeletions(activeTransportMode)) ||
      (draftStore.hasDirty(activeTransportMode) && globalBroadcastVariableMappingIssue)
    ) {
      return;
    }

    const requestMode = activeTransportMode;
    const appliedLineIds = draftStore.getDirtyLineIds(requestMode);
    const appliedGenerationsByLine = appliedLineIds.reduce((result, lineId) => {
      result[lineId] = getBroadcastLineDraftGeneration(lineId);
      return result;
    }, {});
    const appliedVolumeGeneration = draftStore.getVolumeDraftGeneration(requestMode);
    const applyRequest = buildBroadcastApplyOperationRequest(requestMode);
    setIsApplyingBroadcastConfig(true);
    setBroadcastApplyPhase("applying");
    setBroadcastApplyError("");

    try {
      if (!draftStore.hasDirty(requestMode) && hasPendingAssetDeletions(requestMode)) {
        const deletionResult = await flushPendingAssetDeletions(requestMode);
        if (!isCurrentBroadcastMode(requestMode)) {
          return;
        }

        if (deletionResult.error) {
          await restoreBroadcastFrontendStateFromBackend(requestMode, deletionResult.error);
          return;
        }

        setIsApplyingBroadcastConfig(false);
        setBroadcastApplyPhase("");
        setBroadcastApplyError("");
        return;
      }

      const outcome = await broadcastApplyOperation.apply(applyRequest, requestMode, isCurrentBroadcastMode);
      const result = outcome?.result;
      if (outcome?.interrupted && !result?.success) {
        return;
      }

      if (result?.success && matchesBroadcastMode(result, requestMode)) {
        const committedLineIds = Array.isArray(result.appliedLineIds) ? result.appliedLineIds : [];
        const clearableLineIds = committedLineIds.filter((lineId) => getBroadcastLineDraftGeneration(lineId) === appliedGenerationsByLine[lineId]);
        if (clearableLineIds.length > 0) {
          dirtyPlatformStationIdsRef.current = [];
          draftStore.clearLineDrafts(clearableLineIds);
        }
        if (result.volumeApplied) {
          draftStore.clearVolumeDraft(appliedVolumeGeneration, requestMode);
        }
        const deletionResult = await flushPendingAssetDeletions(requestMode);

        if (!isCurrentBroadcastMode(requestMode)) {
          return;
        }

        if (deletionResult.error) {
          await restoreBroadcastFrontendStateFromBackend(requestMode, deletionResult.error);
          return;
        }

        if (result.volumeApplied) {
          if (!draftStore.hasVolumeDirty(requestMode)) {
            setBroadcastAppliedVolume(applyRequest.volume);
          }
        }

        setBroadcastLineDraftDirty(false);
        setBroadcastLocalDraftDirty(draftStore.getDirtyLineIds(requestMode).length > 0);
        setBroadcastVolumeDirty(draftStore.hasVolumeDirty(requestMode));
        setBroadcastWarnings(Array.isArray(result.warnings) ? result.warnings : []);
        setIsApplyingBroadcastConfig(false);
        setBroadcastApplyPhase("");
        setBroadcastApplyError(deletionResult.error || "");
        return;
      }

      if (!isCurrentBroadcastMode(requestMode)) {
        return;
      }

      await restoreBroadcastFrontendStateFromBackend(
        requestMode,
        result?.error || t("broadcast.footer.applyFailedFallback"),
      );
    } catch (error) {
      if (!isCurrentBroadcastMode(requestMode)) {
        return;
      }

      await restoreBroadcastFrontendStateFromBackend(
        requestMode,
        error instanceof Error ? error.message : t("broadcast.footer.applyFailedFallback"),
      );
    }
  }

  function handleLocateBroadcastMappingIssue() {
    if (!globalBroadcastVariableMappingIssue?.stationId) {
      return;
    }

    if (globalBroadcastVariableMappingIssue.lineId
      && globalBroadcastVariableMappingIssue.lineId !== selectedLineIdRef.current) {
      handleBroadcastLineSelect(globalBroadcastVariableMappingIssue.lineId);
    }

    setTrayContext(null);
    setActiveTab("mapping");
    setMappingTray(globalBroadcastVariableMappingIssue.stationId);
  }

  const broadcastOperationError = broadcastApplyOperation.applyState.phase === "error" ? broadcastApplyOperation.applyState.error : "";
  const broadcastDraftDirty = draftStore.hasDirty(activeTransportMode) || hasPendingAssetDeletions(activeTransportMode);
  const isBroadcastLineContentReady = Boolean(selectedLineId && broadcastContentLineId === selectedLineId);
  const isBroadcastConfigApplied = !broadcastDraftDirty;
  const broadcastFooterTone = broadcastApplyError || broadcastOperationError
    ? "error"
    : globalBroadcastVariableMappingIssue
      ? "warning"
      : isApplyingBroadcastConfig
        ? "pending"
        : broadcastDraftDirty
          ? "warning"
          : "applied";
  const broadcastFooterText = broadcastApplyError || broadcastOperationError
    ? broadcastApplyError || broadcastOperationError
    : globalBroadcastVariableMappingIssue
      ? broadcastLabels.footerStatusMappingRequired.replace("{station}", globalBroadcastVariableMappingIssue.stationName || "-")
      : isApplyingBroadcastConfig
        ? broadcastLabels.footerStatusApplying
        : broadcastDraftDirty
          ? broadcastLabels.footerStatusDirty
          : "";
  const broadcastApplyButtonLabel = isApplyingBroadcastConfig
    ? broadcastLabels.footerStatusApplying
    : globalBroadcastVariableMappingIssue
      ? broadcastLabels.footerLocateMapping
      : !broadcastDraftDirty
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
      vehicleRules: rules.filter(
        (rule) =>
          !RELEASE_HIDDEN_VEHICLE_TRIGGER_IDS.includes(rule?.triggerId || ""),
      ),
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
      assetDeleteBlockedNames,
      deleteAllAssetsKey,
    },
    preview: {
      previewingAssetName,
      previewingRuleId,
      broadcastPreviewVolume,
      isApplyingBroadcastConfig,
      isBroadcastLineContentReady,
      broadcastDraftDirty,
      isBroadcastConfigApplied,
      broadcastVariableMappingIssue: globalBroadcastVariableMappingIssue,
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
      setSelectedLineId: handleBroadcastLineSelect,
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
      handleClearAllStationBindings,
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
