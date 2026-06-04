import { useEffect } from "react";
import { IMPORT_OVERLAY_TRANSITION_MS } from "./broadcast-constants";
import { createEmptyExternalAssetBrowserState } from "./broadcast-assets";
import { deriveBroadcastStationStatus } from "./broadcast-bindings";

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
    selectedLineIdRef,
    setShouldRenderAssetExplorer,
    setAssetExplorerStage,
    setSelectedExternalFiles,
    setCurrentExternalPath,
    setExternalAssetBrowser,
    setIsAssetExplorerOpen,
    setPreviewingAssetName,
    setCatalogAssetLibrary,
    setStationBindingDraftsByLine,
    setBindingLangDraftsByLine,
    setDisambiguationNamesByLine,
    setStations,
    setRules,
    setPlatformAnnouncements,
    applyBroadcastSnapshot,
    closeInlineMenus,
  } = context;

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

    setCatalogAssetLibrary((current) => current.filter((asset) => asset.name !== assetName));
    setStations((current) =>
      current.map((station) => ({
        ...station,
        audios: station.audios.filter((entry) => entry.assetName !== assetName),
        conflictAssets: station.conflictAssets.filter((entry) => entry.assetName !== assetName),
        status: deriveBroadcastStationStatus(
          station.audios.filter((entry) => entry.assetName !== assetName),
          station.conflictAssets.filter((entry) => entry.assetName !== assetName),
        ),
      })),
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
            audios: Array.isArray(stationDraft.audios) ? stationDraft.audios.filter((entry) => entry.assetName !== assetName) : [],
            conflictAssets: Array.isArray(stationDraft.conflictAssets) ? stationDraft.conflictAssets.filter((entry) => entry.assetName !== assetName) : [],
          };
        });
        next[lineId] = nextLineDrafts;
      });
      return next;
    });
    setRules((current) =>
      current.map((rule) => ({
        ...rule,
        nodes: rule.nodes.filter((node) => !(node.type === "asset" && node.name === assetName)),
      })),
    );
    setPlatformAnnouncements((current) =>
      current.map((announcement) => ({
        ...announcement,
        nodes: (Array.isArray(announcement.nodes) ? announcement.nodes : []).filter((node) => !(node.type === "asset" && node.name === assetName)),
      })),
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
    setStations((current) =>
      current.map((station) => ({
        ...station,
        audios: [],
        conflictAssets: [],
        status: "missing",
      })),
    );
    setRules((current) =>
      current.map((rule) => ({
        ...rule,
        nodes: rule.nodes.filter((node) => node.type !== "asset"),
      })),
    );
    setPlatformAnnouncements((current) =>
      current.map((announcement) => ({
        ...announcement,
        nodes: (Array.isArray(announcement.nodes) ? announcement.nodes : []).filter((node) => node.type !== "asset"),
      })),
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
