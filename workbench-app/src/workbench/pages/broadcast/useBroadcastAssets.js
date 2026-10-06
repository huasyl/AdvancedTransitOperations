import { useEffect, useRef, useState } from "react";
import { IMPORT_OVERLAY_TRANSITION_MS } from "./broadcast-constants";
import { createEmptyExternalAssetBrowserState } from "./broadcast-assets";
import { deriveBroadcastStationStatus } from "./broadcast-bindings";

const ASSET_DELETE_BLOCKED_MS = 5000;
const DELETE_ALL_ASSETS_KEY = "__all__";

function normalizeAssetReferenceKey(value) {
  return String(value || "").trim().toLowerCase();
}

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
    rules,
    stations,
    platformAnnouncements,
    defaultBindingLanguageLabel,
    selectedLineIdRef,
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
    closeInlineMenus,
    getPendingAssetDeletionNames,
    setAssetOperationFeedback,
  } = context;
  const [assetDeleteBlockedNames, setAssetDeleteBlockedNames] = useState({});
  const assetDeleteBlockedTimersRef = useRef({});
  const [importConflicts, setImportConflicts] = useState([]);
  const [isImportingAssets, setIsImportingAssets] = useState(false);
  const importViewRef = useRef({ mode: activeTransportMode, sequence: 0 });
  const importItemsRef = useRef([]);
  const browserRequestRef = useRef(0);
  importViewRef.current.mode = activeTransportMode;

  useEffect(() => {
    importViewRef.current.sequence += 1;
    setImportConflicts([]);
    setIsImportingAssets(false);
    importItemsRef.current = [];
  }, [activeTransportMode, isAssetExplorerOpen]);

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

  function getDeleteBlockedKey(assetName, mode = activeTransportMode) {
    return `${String(mode || "train").trim().toLowerCase() || "train"}::${assetName || DELETE_ALL_ASSETS_KEY}`;
  }

  function showAssetDeleteBlocked(assetName, mode = activeTransportMode) {
    const key = getDeleteBlockedKey(assetName, mode);
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

  function hasAssetReferenceInNodes(nodes, assetNames) {
    return (Array.isArray(nodes) ? nodes : []).some((node) =>
      node
      && node.type === "asset"
      && assetNames.has(normalizeAssetReferenceKey(node.name)),
    );
  }

  function hasAssetReferenceInStations(stationsForUi, assetNames) {
    return (Array.isArray(stationsForUi) ? stationsForUi : []).some((station) => {
      const audios = Array.isArray(station?.audios) ? station.audios : [];
      return audios.some((entry) => assetNames.has(normalizeAssetReferenceKey(entry?.assetName)));
    });
  }

  function lineDraftReferencesAssets(draft, assetNames) {
    if (!draft) {
      return false;
    }

    return hasAssetReferenceInStations(draft.stationsForUi, assetNames)
      || (Array.isArray(draft.stationBindings) ? draft.stationBindings : []).some((binding) =>
        assetNames.has(normalizeAssetReferenceKey(binding?.assetName)),
      )
      || (Array.isArray(draft.rules) ? draft.rules : []).some((rule) => hasAssetReferenceInNodes(rule?.nodes, assetNames))
      || (Array.isArray(draft.platformAnnouncements) ? draft.platformAnnouncements : []).some((announcement) =>
        hasAssetReferenceInNodes(announcement?.nodes, assetNames),
      );
  }

  function hasFrontendAssetReferences(assetNames) {
    const normalizedAssetNames = new Set(
      (Array.isArray(assetNames) ? assetNames : [])
        .map((assetName) => normalizeAssetReferenceKey(assetName))
        .filter((assetName) => assetName),
    );
    if (normalizedAssetNames.size === 0) {
      return false;
    }

    const activeLineId = selectedLineIdRef.current || "";
    const dirtyLineIds = draftStore.getDirtyLineIds(activeTransportMode);
    const dirtyLineIdSet = new Set(dirtyLineIds);
    if (
      !dirtyLineIdSet.has(activeLineId)
      && lineDraftReferencesAssets(
        {
          stationsForUi: stations,
          rules,
          platformAnnouncements,
        },
        normalizedAssetNames,
      )
    ) {
      return true;
    }

    return dirtyLineIds.some((lineId) =>
      lineDraftReferencesAssets(draftStore.getLineDraft(lineId), normalizedAssetNames),
    );
  }

  async function loadExternalAssetBrowser(path = "") {
    const requestMode = activeTransportMode;
    const sequence = ++browserRequestRef.current;
    try {
      const browserSnapshot = await workbenchApi.loadBroadcastAssetBrowser?.({
        path: path || currentExternalPath || "",
        mode: activeTransportMode,
      });
      if (!browserSnapshot || sequence !== browserRequestRef.current || requestMode !== importViewRef.current.mode) {
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
        await workbenchApi.stopBroadcastAssetPreview?.({ assetName, mode: activeTransportMode });
      } catch (error) {
        console.error("[RT Broadcast Workbench] stop asset preview failed", error);
      }
      resetAssetPreviewState(assetName);
      return;
    }

    const requestMode = activeTransportMode;
    setPreviewingAssetName(assetName);
    try {
      const result = await workbenchApi.playBroadcastAssetPreview?.({ assetName, mode: requestMode });
      if (requestMode !== importViewRef.current.mode) return;
      if (!result?.success) {
        resetAssetPreviewState(assetName);
        setAssetOperationFeedback({ key: "broadcast.footer.previewFailed", tone: "error", error: result?.error || "" });
      }
    } catch (error) {
      if (requestMode !== importViewRef.current.mode) return;
      resetAssetPreviewState(assetName);
      setAssetOperationFeedback({ key: "broadcast.footer.previewFailed", tone: "error", error: error instanceof Error ? error.message : "" });
    }
  }

  async function handleRulePreviewToggle(ruleId) {
    if (!ruleId) {
      return;
    }

    if (previewingRuleId === ruleId) {
      try {
        await workbenchApi.stopBroadcastRulePreview?.({ ruleId, mode: activeTransportMode });
      } catch (error) {
        console.error("[RT Broadcast Workbench] stop rule preview failed", error);
      }
      setPreviewingRuleId("");
      return;
    }

    const requestMode = activeTransportMode;
    setPreviewingRuleId(ruleId);
    try {
      const result = await workbenchApi.playBroadcastRulePreview?.({
        lineId: selectedLineIdRef.current || "",
        ruleId,
        mode: requestMode,
        rule: (Array.isArray(rules) ? rules : []).find((rule) => rule?.id === ruleId) || null,
      });
      if (requestMode !== importViewRef.current.mode) return;
      if (!result?.success) {
        setPreviewingRuleId("");
        setAssetOperationFeedback({ key: "broadcast.footer.previewFailed", tone: "error", error: result?.error || "" });
      }
    } catch (error) {
      if (requestMode !== importViewRef.current.mode) return;
      setPreviewingRuleId("");
      setAssetOperationFeedback({ key: "broadcast.footer.previewFailed", tone: "error", error: error instanceof Error ? error.message : "" });
    }
  }

  function removeAssetFromUi(assetNames) {
    const assetNameSet = new Set(assetNames.map(normalizeAssetReferenceKey));
    function clearStationConflicts(station) {
      const current = Array.isArray(station?.conflictAssets) ? station.conflictAssets : [];
      const conflictAssets = current.filter((entry) => !assetNameSet.has(normalizeAssetReferenceKey(entry.assetName)));
      if (conflictAssets.length === current.length) {
        return station;
      }
      return {
        ...station,
        conflictAssets,
        status: deriveBroadcastStationStatus(station.audios, conflictAssets),
      };
    }

    const nextStations = (Array.isArray(stations) ? stations : []).map(clearStationConflicts);
    if (nextStations.some((station, index) => station !== stations[index])) {
      setStations(nextStations);
    }
    draftStore.getDirtyLineIds(activeTransportMode).forEach((lineId) => {
      const draft = draftStore.getLineDraft(lineId);
      if (!draft) {
        return;
      }

      const draftStations = Array.isArray(draft.stationsForUi) ? draft.stationsForUi : [];
      const nextDraftStations = draftStations.map(clearStationConflicts);
      if (nextDraftStations.some((station, index) => station !== draftStations[index])) {
        draftStore.setLineDraft(lineId, { ...draft, stationsForUi: nextDraftStations });
      }
    });
    if (assetNames.includes(previewingAssetName)) {
      resetAssetPreviewState(previewingAssetName);
    }
  }

  async function handleDeleteAsset(assetName) {
    if (!assetName) {
      return;
    }

    if (hasFrontendAssetReferences([assetName])) {
      showAssetDeleteBlocked(assetName);
      return;
    }

    try {
      if (previewingAssetName === assetName) {
        await workbenchApi.stopBroadcastAssetPreview?.({ assetName, mode: activeTransportMode });
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] stop asset preview before delete failed", error);
    }

    queuePendingAssetDeletions([assetName]);
    removeAssetFromUi([assetName]);
  }

  async function handleDeleteAllAssets() {
    const assetNames = (Array.isArray(availableAssetLibrary) ? availableAssetLibrary : [])
      .map((asset) => asset?.name || "")
      .filter((assetName) => assetName);
    if (assetNames.length === 0) {
      return;
    }

    if (hasFrontendAssetReferences(assetNames)) {
      showAssetDeleteBlocked(DELETE_ALL_ASSETS_KEY);
      return;
    }

    try {
      if (assetNames.includes(previewingAssetName)) {
        await workbenchApi.stopBroadcastAssetPreview?.({ assetName: previewingAssetName, mode: activeTransportMode });
      }
    } catch (error) {
      console.error("[RT Broadcast Workbench] stop asset preview before delete all failed", error);
    }

    queuePendingAssetDeletions(assetNames, { deleteAll: true });
    removeAssetFromUi(assetNames);
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

      const matches = (Array.isArray(bindableAssetLibrary) ? bindableAssetLibrary : []).filter((asset) =>
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
    browserRequestRef.current += 1;
    if (importConflicts.length > 0) {
      importItemsRef.current = importItemsRef.current.map((item) => item.status === "conflict" ? { ...item, status: "canceled" } : item);
      reportImportItems(importItemsRef.current);
    }
    importViewRef.current.sequence += 1;
    setImportConflicts([]);
    setIsImportingAssets(false);
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

  function reportImportItems(items, error = "") {
    const counts = { added: 0, existing: 0, replaced: 0, canceled: 0, failed: 0 };
    const failedNames = [];
    let pending = false;
    for (const item of items) {
      const status = item.status === "reused" ? "added" : item.status;
      if (status in counts) counts[status] += 1;
      if (status === "failed") failedNames.push(item.name);
      if (status === "pending-delete") pending = true;
      error = error || item.error || "";
    }
    setAssetOperationFeedback({
      key: pending ? "broadcast.import.pendingDelete" : "broadcast.import.result",
      tone: error || counts.failed ? "error" : pending ? "warning" : "applied",
      params: {
        ...counts,
        names: failedNames.join(", "),
      },
      error,
    });
  }

  async function importExternalAssets(selectedPaths, confirmations = []) {
    const requestMode = activeTransportMode;
    const sequence = ++importViewRef.current.sequence;
    setIsImportingAssets(true);
    try {
      const result = await workbenchApi.importBroadcastExternalAssets?.({
        currentPath: currentExternalPath,
        selectedPaths,
        confirmations,
        pendingDeleteNames: getPendingAssetDeletionNames(requestMode),
        mode: requestMode,
      });
      if (sequence !== importViewRef.current.sequence || requestMode !== importViewRef.current.mode) return;
      const items = Array.isArray(result?.items) ? result.items : [];
      const paths = new Set(items.map((item) => item.path));
      importItemsRef.current = [...importItemsRef.current.filter((item) => !paths.has(item.path)), ...items];
      const conflicts = items.filter((item) => item.status === "conflict");
      setImportConflicts(conflicts);
      reportImportItems(importItemsRef.current, result?.error || "");
      if (conflicts.length === 0 && result?.success) {
        handleCloseAssetExplorer();
      }
    } catch (error) {
      if (sequence === importViewRef.current.sequence && requestMode === importViewRef.current.mode) {
        reportImportItems(importItemsRef.current, error instanceof Error ? error.message : "Import failed.");
      }
    } finally {
      if (sequence === importViewRef.current.sequence) setIsImportingAssets(false);
    }
  }

  function handleImportSelectedExternalFiles() {
    if (isImportingAssets || selectedExternalFiles.length === 0) return;
    importItemsRef.current = [];
    importExternalAssets(selectedExternalFiles);
  }

  function handleConfirmImportReplacements() {
    if (isImportingAssets || importConflicts.length === 0) return;
    importExternalAssets(importConflicts.map((item) => item.path), importConflicts);
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
    assetDeleteBlockedNames: Object.entries(assetDeleteBlockedNames).reduce((result, [key, value]) => {
      if (!value) {
        return result;
      }

      const prefix = `${String(activeTransportMode || "train").trim().toLowerCase() || "train"}::`;
      if (!key.startsWith(prefix)) {
        return result;
      }

      result[key.slice(prefix.length)] = true;
      return result;
    }, {}),
    deleteAllAssetsKey: DELETE_ALL_ASSETS_KEY,
    showAssetDeleteBlocked,
    handleAutoBindStations,
    handleCloseAssetExplorer,
    handleExternalPathChange,
    resolveExternalFolderTargetPath,
    handleExternalBack,
    handleToggleExternalFile,
    handleToggleAllExternalFiles,
    handleImportSelectedExternalFiles,
    handleConfirmImportReplacements,
    importConflicts,
    isImportingAssets,
  };
}
