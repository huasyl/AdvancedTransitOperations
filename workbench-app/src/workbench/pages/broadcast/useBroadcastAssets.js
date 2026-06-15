import { useEffect, useRef, useState } from "react";
import { IMPORT_OVERLAY_TRANSITION_MS } from "./broadcast-constants";
import { createEmptyExternalAssetBrowserState } from "./broadcast-assets";
import { deriveBroadcastStationStatus } from "./broadcast-bindings";

const ASSET_DELETE_BLOCKED_MS = 5000;
const DELETE_ALL_ASSETS_KEY = "__all__";

function getBroadcastMatchKey(value) {
  const source = String(value || "")
    .trim()
    .replace(/\.[^.\\/:]+$/, "")
    .toLowerCase();
  let result = "";
  let lastWasSeparator = false;
  for (let index = 0; index < source.length; index += 1) {
    const ch = source[index];
    const code = ch.charCodeAt(0);
    const isAsciiDigit = code >= 48 && code <= 57;
    const isAsciiLetter = code >= 97 && code <= 122;
    const isNonAsciiWord =
      code > 127 && !(ch === " " || ch === "_" || ch === "-");
    if (isAsciiDigit || isAsciiLetter || isNonAsciiWord) {
      result += ch;
      lastWasSeparator = false;
    } else if ((ch === " " || ch === "_" || ch === "-") && !lastWasSeparator && result) {
      result += " ";
      lastWasSeparator = true;
    }
  }
  return result.trim();
}

function compactBroadcastMatchKey(value) {
  return String(value || "").replace(/\s+/g, "");
}

function isBroadcastStationAssetMatch(assetName, stationName) {
  const stationKey = getBroadcastMatchKey(stationName);
  const assetKey = getBroadcastMatchKey(assetName);
  if (!stationKey || !assetKey) {
    return false;
  }

  if (assetKey.includes(stationKey)) {
    return true;
  }

  const compactStationKey = compactBroadcastMatchKey(stationKey);
  const compactAssetKey = compactBroadcastMatchKey(assetKey);
  return Boolean(compactStationKey && compactAssetKey && compactAssetKey.includes(compactStationKey));
}

export default function useBroadcastAssets(context) {
  const {
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
    rules,
    stations,
    platformAnnouncements,
    defaultBindingLanguageLabel,
    selectedLineIdRef,
    draftStore,
    buildCurrentBroadcastLineDraft,
    markBroadcastDraftDirty,
    setShouldRenderAssetExplorer,
    setAssetExplorerStage,
    setSelectedExternalFiles,
    setCurrentExternalPath,
    setExternalAssetBrowser,
    setIsAssetExplorerOpen,
    setPreviewingAssetName,
    setPreviewingRuleId,
    setCatalogAssetLibrary,
    setBindingLangDraftsByLine,
    setDisambiguationNamesByLine,
    setStations,
    setRules,
    setPlatformAnnouncements,
    closeInlineMenus,
  } = context;
  const [assetDeleteBlockedNames, setAssetDeleteBlockedNames] = useState({});
  const assetDeleteBlockedTimersRef = useRef({});

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

  useEffect(() => () => {
    Object.values(assetDeleteBlockedTimersRef.current).forEach((timer) => {
      window.clearTimeout(timer);
    });
    assetDeleteBlockedTimersRef.current = {};
  }, []);

  function showAssetDeleteBlocked(assetName) {
    const key = assetName || DELETE_ALL_ASSETS_KEY;
    if (assetDeleteBlockedTimersRef.current[key]) {
      window.clearTimeout(assetDeleteBlockedTimersRef.current[key]);
    }

    setAssetDeleteBlockedNames((current) => ({ ...current, [key]: true }));
    assetDeleteBlockedTimersRef.current[key] = window.setTimeout(() => {
      setAssetDeleteBlockedNames((current) => {
        const next = { ...current };
        delete next[key];
        return next;
      });
      delete assetDeleteBlockedTimersRef.current[key];
    }, ASSET_DELETE_BLOCKED_MS);
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
        rule: (Array.isArray(rules) ? rules : []).find((rule) => rule?.id === ruleId) || null,
      });
    } catch (error) {
      console.error("[RT Broadcast Workbench] play rule preview failed", error);
    }

    setPreviewingRuleId(ruleId);
  }

  function removeAssetFromUi(assetName) {
    if (!assetName) {
      return;
    }

    const activeLineId = selectedLineIdRef.current || "";
    const nextStations = (Array.isArray(stations) ? stations : []).map((station) => ({
      ...station,
      audios: station.audios.filter((entry) => entry.assetName !== assetName),
      conflictAssets: station.conflictAssets.filter((entry) => entry.assetName !== assetName),
      status: deriveBroadcastStationStatus(
        station.audios.filter((entry) => entry.assetName !== assetName),
        station.conflictAssets.filter((entry) => entry.assetName !== assetName),
      ),
    }));
    const nextRules = (Array.isArray(rules) ? rules : []).map((rule) => ({
      ...rule,
      nodes: rule.nodes.filter((node) => !(node.type === "asset" && node.name === assetName)),
    }));
    const nextPlatformAnnouncements = (Array.isArray(platformAnnouncements) ? platformAnnouncements : []).map((announcement) => ({
      ...announcement,
      nodes: (Array.isArray(announcement.nodes) ? announcement.nodes : []).filter((node) => !(node.type === "asset" && node.name === assetName)),
    }));
    setCatalogAssetLibrary((current) => current.filter((asset) => asset.name !== assetName));
    setStations(nextStations);
    setRules(nextRules);
    setPlatformAnnouncements(nextPlatformAnnouncements);
    draftStore.getDirtyLineIds().forEach((lineId) => {
      const draft = draftStore.getLineDraft(lineId);
      if (!draft) {
        return;
      }

      draftStore.setLineDraft(lineId, {
        ...draft,
        stationsForUi: (Array.isArray(draft.stationsForUi) ? draft.stationsForUi : []).map((station) => ({
          ...station,
          audios: (Array.isArray(station?.audios) ? station.audios : []).filter((entry) => entry.assetName !== assetName),
          conflictAssets: (Array.isArray(station?.conflictAssets) ? station.conflictAssets : []).filter((entry) => entry.assetName !== assetName),
        })),
        rules: (Array.isArray(draft.rules) ? draft.rules : []).map((rule) => ({
          ...rule,
          nodes: (Array.isArray(rule?.nodes) ? rule.nodes : []).filter((node) => !(node.type === "asset" && node.name === assetName)),
        })),
        platformAnnouncements: (Array.isArray(draft.platformAnnouncements) ? draft.platformAnnouncements : []).map((announcement) => ({
          ...announcement,
          nodes: (Array.isArray(announcement?.nodes) ? announcement.nodes : []).filter((node) => !(node.type === "asset" && node.name === assetName)),
        })),
      });
    });
    resetAssetPreviewState(assetName);
    if (activeLineId) {
      markBroadcastDraftDirty(activeLineId, buildCurrentBroadcastLineDraft({
        stationsForUi: nextStations,
        rules: nextRules,
        platformAnnouncements: nextPlatformAnnouncements,
      }));
    }
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
        if (result?.error === "broadcast-asset-in-use") {
          showAssetDeleteBlocked(assetName);
        }
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
        if (result?.error === "broadcast-asset-in-use") {
          showAssetDeleteBlocked(DELETE_ALL_ASSETS_KEY);
        }
        return;
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] delete all assets failed", error);
      return;
    }

    setCatalogAssetLibrary([]);
    const activeLineId = selectedLineIdRef.current || "";
    const nextStations = (Array.isArray(stations) ? stations : []).map((station) => ({
      ...station,
      audios: [],
      conflictAssets: [],
      status: "missing",
    }));
    const nextRules = (Array.isArray(rules) ? rules : []).map((rule) => ({
      ...rule,
      nodes: rule.nodes.filter((node) => node.type !== "asset"),
    }));
    const nextPlatformAnnouncements = (Array.isArray(platformAnnouncements) ? platformAnnouncements : []).map((announcement) => ({
      ...announcement,
      nodes: (Array.isArray(announcement.nodes) ? announcement.nodes : []).filter((node) => node.type !== "asset"),
    }));
    setStations(nextStations);
    setRules(nextRules);
    setPlatformAnnouncements(nextPlatformAnnouncements);
    draftStore.getDirtyLineIds().forEach((lineId) => {
      const draft = draftStore.getLineDraft(lineId);
      if (!draft) {
        return;
      }

      draftStore.setLineDraft(lineId, {
        ...draft,
        stationsForUi: (Array.isArray(draft.stationsForUi) ? draft.stationsForUi : []).map((station) => ({
          ...station,
          audios: [],
          conflictAssets: [],
        })),
        rules: (Array.isArray(draft.rules) ? draft.rules : []).map((rule) => ({
          ...rule,
          nodes: (Array.isArray(rule?.nodes) ? rule.nodes : []).filter((node) => node.type !== "asset"),
        })),
        platformAnnouncements: (Array.isArray(draft.platformAnnouncements) ? draft.platformAnnouncements : []).map((announcement) => ({
          ...announcement,
          nodes: (Array.isArray(announcement?.nodes) ? announcement.nodes : []).filter((node) => node.type !== "asset"),
        })),
      });
    });
    resetAssetPreviewState();
    if (activeLineId) {
      markBroadcastDraftDirty(activeLineId, buildCurrentBroadcastLineDraft({
        stationsForUi: nextStations,
        rules: nextRules,
        platformAnnouncements: nextPlatformAnnouncements,
      }));
    }
  }

  async function handleAutoBindStations() {
    if (!selectedLineIdRef.current) {
      return;
    }

    let changedCount = 0;
    const nextStations = (Array.isArray(stations) ? stations : []).map((station) => {
      if (!station?.id || (Array.isArray(station.audios) && station.audios.length > 0)) {
        return station;
      }

      const matches = (Array.isArray(availableAssetLibrary) ? availableAssetLibrary : []).filter((asset) =>
        asset?.name && isBroadcastStationAssetMatch(asset.name, station.name),
      );
      if (matches.length > 1) {
        const conflictAssets = matches
          .map((asset) => ({ assetName: asset.name, suggestedLang: "" }))
          .sort((left, right) => String(left.assetName || "").localeCompare(String(right.assetName || "")));
        const currentConflictKey = (Array.isArray(station.conflictAssets) ? station.conflictAssets : [])
          .map((entry) => entry?.assetName || "")
          .join("\n");
        const nextConflictKey = conflictAssets.map((entry) => entry.assetName || "").join("\n");
        const status = deriveBroadcastStationStatus(station.audios, conflictAssets);
        if (currentConflictKey === nextConflictKey && station.status === status) {
          return station;
        }

        changedCount += 1;
        return {
          ...station,
          conflictAssets,
          status,
        };
      }
      if (matches.length !== 1) {
        return station;
      }

      changedCount += 1;
      const audios = [
        {
          lang: defaultBindingLanguageLabel || "",
          langIndex: 1,
          assetName: matches[0].name,
        },
      ];
      return {
        ...station,
        audios,
        conflictAssets: [],
        status: deriveBroadcastStationStatus(audios, []),
      };
    });

    if (changedCount <= 0) {
      return;
    }

    setStations(nextStations);
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
    markBroadcastDraftDirty(selectedLineIdRef.current, buildCurrentBroadcastLineDraft({ stationsForUi: nextStations }));
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
    setSelectedExternalFiles((current) => (current.includes(fileId) ? current.filter((id) => id !== fileId) : [...current, fileId]));
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
        selectedPaths: selectedExternalFiles,
      });

      if (result?.success) {
        handleCloseAssetExplorer();
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] import external assets failed", error);
    }
  }

  return {
    loadExternalAssetBrowser,
    handleImportAssetDirectory,
    resetAssetPreviewState,
    handleAssetPreviewToggle,
    handleRulePreviewToggle,
    removeAssetFromUi,
    handleDeleteAsset,
    handleDeleteAllAssets,
    assetDeleteBlockedNames,
    deleteAllAssetsKey: DELETE_ALL_ASSETS_KEY,
    handleAutoBindStations,
    handleCloseAssetExplorer,
    handleExternalPathChange,
    resolveExternalFolderTargetPath,
    handleExternalBack,
    handleToggleExternalFile,
    handleToggleAllExternalFiles,
    handleImportSelectedExternalFiles,
  };
}
