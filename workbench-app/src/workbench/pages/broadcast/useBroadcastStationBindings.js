import { normalizeLangIndex } from "./broadcast-normalize";
import { extractBroadcastLanguageHint } from "./broadcast-assets";
import { deriveBroadcastStationStatus, sortBroadcastConflictAssets } from "./broadcast-bindings";
import { animateScrollTopWithTransform } from "./components/BroadcastAnimatedPanels";

export default function useBroadcastStationBindings(context) {
  const {
    workbenchApi,
    stations,
    stationBindingDraftsByLine,
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
    setStationBindingDraftsByLine,
    setBindingLangDraftsByLine,
    setDisambiguationNamesByLine,
    setMappingBindFeedback,
    setStations,
    setMappingTray,
  } = context;

  function getActiveBroadcastLineId() {
    return selectedLineIdRef.current || "";
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
          conflictAssets: Array.isArray(nextConflictAssets) ? nextConflictAssets : [],
        },
      },
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
        [stationId]: value,
      },
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
        [draftKey]: value,
      },
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
            assetName: entry.assetName,
          })),
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
          [lineId]: nextLineDrafts,
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

    removeTimersRef.current.push(
      window.setTimeout(() => {
        setMappingBindFeedback((current) => (current?.token === token ? null : current));
      }, 3400),
    );
  }

  async function handleBindStation(stationId, assetName) {
    const targetStation = stations.find((station) => station.id === stationId);
    if (!targetStation || !assetName) {
      return;
    }

    const nextLang = (getBindingLanguageDraft(stationId) || "").trim() || defaultBindingLanguageLabel;
    const nextAudios = [...targetStation.audios.filter((entry) => entry.lang !== nextLang), { lang: nextLang, assetName }];
    const nextStation = {
      ...targetStation,
      audios: nextAudios,
      conflictAssets: [],
      status: deriveBroadcastStationStatus(nextAudios, []),
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
      status: deriveBroadcastStationStatus(nextAudios, []),
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
      status: deriveBroadcastStationStatus(targetStation.audios, nextConflictAssets),
    };

    setStations((current) => current.map((station) => (station.id === stationId ? nextStation : station)));
    persistStationBindingDraft(stationId, nextStation.audios, nextStation.conflictAssets);
  }

  async function handleResolveStationConflicts(stationId) {
    const targetStation = stations.find((station) => station.id === stationId);
    if (!targetStation || targetStation.conflictAssets.length === 0) {
      return;
    }

    const existingAudios = Array.isArray(targetStation.audios) ? targetStation.audios.filter((entry) => entry && entry.assetName) : [];
    const existingAssetNames = new Set(existingAudios.map((entry) => entry.assetName));
    const resolvedAudios = targetStation.conflictAssets
      .filter((entry) => entry && entry.assetName && !existingAssetNames.has(entry.assetName))
      .map((entry) => ({
        lang:
          (
            getDisambiguationNameDraft(stationId, entry.assetName, extractBroadcastLanguageHint(entry.assetName, targetStation.name, fallbackLanguageKey, broadcastLabels)) || ""
          ).trim() || defaultBindingLanguageLabel,
        assetName: entry.assetName,
      }));
    const nextAudios = [...existingAudios, ...resolvedAudios];
    const nextStation = {
      ...targetStation,
      audios: nextAudios,
      conflictAssets: [],
      status: deriveBroadcastStationStatus(nextAudios, []),
    };

    setStations((current) => current.map((station) => (station.id === stationId ? nextStation : station)));
    persistStationBindingDraft(stationId, nextStation.audios, nextStation.conflictAssets);
    setMappingTray(null);
    await syncStationBindings(stationId, nextStation.audios);
  }

  return {
    persistStationBindingDraft,
    updateBindingLanguageDraft,
    updateDisambiguationNameDraft,
    getBindingLanguageDraft,
    getDisambiguationNameDraft,
    syncStationBindings,
    scheduleMappingBindFeedback,
    handleBindStation,
    handleRemoveStationAudio,
    handleDiscardConflict,
    handleResolveStationConflicts,
  };
}
