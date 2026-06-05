import { useRef, useState } from "react";
import { cloneBroadcastRules, normalizeLangIndex } from "./broadcast-normalize";

function cloneStationsForUi(source) {
  return (Array.isArray(source) ? source : []).map((station) => ({
    ...station,
    audios: Array.isArray(station?.audios) ? station.audios.map((audio) => ({ ...audio })) : [],
    conflictAssets: Array.isArray(station?.conflictAssets) ? station.conflictAssets.map((entry) => ({ ...entry })) : [],
  }));
}

function clonePlatformAnnouncements(source) {
  return (Array.isArray(source) ? source : []).map((announcement) => ({
    ...announcement,
    nodes: Array.isArray(announcement?.nodes) ? announcement.nodes.map((node) => ({ ...node })) : [],
  }));
}

function cloneStationBindings(source) {
  return (Array.isArray(source) ? source : []).map((binding) => ({
    stationId: typeof binding?.stationId === "string" ? binding.stationId : "",
    lang: typeof binding?.lang === "string" ? binding.lang : "",
    langIndex: normalizeLangIndex(binding?.langIndex),
    assetName: typeof binding?.assetName === "string" ? binding.assetName : "",
  }));
}

function buildStationBindingsFromStations(stationsForUi) {
  return (Array.isArray(stationsForUi) ? stationsForUi : []).flatMap((station) =>
    (Array.isArray(station?.audios) ? station.audios : [])
      .filter((audio) => audio && audio.assetName)
      .map((audio, index) => ({
        stationId: station.id,
        lang: typeof audio.lang === "string" ? audio.lang : "",
        langIndex: normalizeLangIndex(audio.langIndex ?? index + 1),
        assetName: audio.assetName,
      })),
  );
}

function cloneDraft(draft) {
  if (!draft) {
    return null;
  }

  const stationsForUi = cloneStationsForUi(draft.stationsForUi);
  return {
    rules: cloneBroadcastRules(draft.rules),
    stationBindings: cloneStationBindings(draft.stationBindings?.length ? draft.stationBindings : buildStationBindingsFromStations(stationsForUi)),
    platformAnnouncements: clonePlatformAnnouncements(draft.platformAnnouncements),
    stationsForUi,
  };
}

export default function useBroadcastDraftStore() {
  const [, setVersion] = useState(0);
  const lineDraftsRef = useRef({});
  const dirtyLineIdsRef = useRef(new Set());
  const lineGenerationsRef = useRef({});
  const volumeDraftRef = useRef(null);
  const volumeGenerationRef = useRef(0);

  function notify() {
    setVersion((value) => value + 1);
  }

  function getLineDraft(lineId) {
    return lineId ? lineDraftsRef.current[lineId] ?? null : null;
  }

  function setLineDraft(lineId, draft) {
    if (!lineId) {
      return;
    }

    const nextDraft = cloneDraft(draft);
    if (!nextDraft) {
      return;
    }

    lineDraftsRef.current = {
      ...lineDraftsRef.current,
      [lineId]: nextDraft,
    };
    dirtyLineIdsRef.current = new Set(dirtyLineIdsRef.current).add(lineId);
    lineGenerationsRef.current = {
      ...lineGenerationsRef.current,
      [lineId]: Number(lineGenerationsRef.current[lineId] || 0) + 1,
    };
    notify();
  }

  function patchLineDraft(lineId, patch) {
    if (!lineId) {
      return;
    }

    const current = getLineDraft(lineId) || {
      rules: [],
      stationBindings: [],
      platformAnnouncements: [],
      stationsForUi: [],
    };
    setLineDraft(lineId, { ...current, ...(patch || {}) });
  }

  function clearLineDrafts(lineIds) {
    const nextDrafts = { ...lineDraftsRef.current };
    const nextDirtyIds = new Set(dirtyLineIdsRef.current);
    let changed = false;

    (Array.isArray(lineIds) ? lineIds : []).forEach((lineId) => {
      if (!lineId) {
        return;
      }

      if (Object.prototype.hasOwnProperty.call(nextDrafts, lineId)) {
        delete nextDrafts[lineId];
        changed = true;
      }
      if (nextDirtyIds.delete(lineId)) {
        changed = true;
      }
    });

    if (!changed) {
      return;
    }

    lineDraftsRef.current = nextDrafts;
    dirtyLineIdsRef.current = nextDirtyIds;
    notify();
  }

  function setVolumeDraft(value) {
    const normalizedValue = Number.isFinite(Number(value)) ? Math.max(0, Math.min(100, Math.round(Number(value)))) : 80;
    volumeDraftRef.current = normalizedValue;
    volumeGenerationRef.current += 1;
    notify();
  }

  function clearVolumeDraft(expectedGeneration = null) {
    if (expectedGeneration != null && volumeGenerationRef.current !== expectedGeneration) {
      return;
    }

    if (volumeDraftRef.current == null) {
      return;
    }

    volumeDraftRef.current = null;
    notify();
  }

  function hasVolumeDirty() {
    return volumeDraftRef.current != null;
  }

  function getVolumeDraft(fallbackValue = 80) {
    return volumeDraftRef.current != null ? volumeDraftRef.current : fallbackValue;
  }

  function getDirtyLineIds() {
    return Array.from(dirtyLineIdsRef.current);
  }

  function hasDirty() {
    return dirtyLineIdsRef.current.size > 0 || hasVolumeDirty();
  }

  function getLineDraftGeneration(lineId) {
    return Number(lineGenerationsRef.current[lineId] || 0);
  }

  function getVolumeDraftGeneration() {
    return volumeGenerationRef.current;
  }

  function buildApplyRequest() {
    return {
      lines: getDirtyLineIds()
        .map((lineId) => {
          const draft = getLineDraft(lineId);
          if (!draft) {
            return null;
          }

          return {
            lineId,
            stationBindings: cloneStationBindings(draft.stationBindings),
            rules: cloneBroadcastRules(draft.rules),
            platformAnnouncements: clonePlatformAnnouncements(draft.platformAnnouncements).map((announcement) => ({
              ...announcement,
              lineId,
            })),
          };
        })
        .filter(Boolean),
      volume: hasVolumeDirty() ? volumeDraftRef.current : null,
      volumeDirty: hasVolumeDirty(),
    };
  }

  return {
    getLineDraft,
    setLineDraft,
    patchLineDraft,
    clearLineDrafts,
    setVolumeDraft,
    clearVolumeDraft,
    hasDirty,
    hasVolumeDirty,
    getVolumeDraft,
    buildApplyRequest,
    dirtyLineIds: getDirtyLineIds(),
    getDirtyLineIds,
    getLineDraftGeneration,
    getVolumeDraftGeneration,
  };
}
