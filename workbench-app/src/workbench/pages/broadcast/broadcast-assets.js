import {
  BROADCAST_LANGUAGE_ALIASES,
  EXTERNAL_ASSET_FILE_SYSTEM,
  DEFAULT_EXTERNAL_ASSET_PATH,
} from "./broadcast-constants";
import {
  normalizeBroadcastMatchKey,
  resolveBroadcastLanguageKeyFromLabel,
  resolveBroadcastLanguageLabel,
} from "./broadcast-normalize";

function createEmptyExternalAssetBrowserState() {
  return {
    rootPath: "",
    currentPath: "",
    parentPath: "",
    folders: [],
    files: [],
    allowedExtensions: [],
    error: "",
  };
}

function buildBroadcastTrayAssetLibrary(assets, stations) {
  const boundAssetNames = new Set();
  (Array.isArray(stations) ? stations : []).forEach((station) => {
    (Array.isArray(station?.audios) ? station.audios : []).forEach((audio) => {
      if (audio && typeof audio.assetName === "string" && audio.assetName) {
        boundAssetNames.add(audio.assetName);
      }
    });
  });

  return (Array.isArray(assets) ? assets : [])
    .map((asset, index) => ({
      ...asset,
      isStationBound: boundAssetNames.has(asset?.name),
      originalIndex: index,
    }))
    .sort((left, right) => {
      if (left.isStationBound !== right.isStationBound) {
        return left.isStationBound ? 1 : -1;
      }

      return left.originalIndex - right.originalIndex;
    })
    .map(({ originalIndex, ...asset }) => asset);
}

function formatBroadcastAssetDisplayName(value) {
  if (typeof value !== "string") {
    return "";
  }

  return value.replace(/\.[^.\\/]+$/, "");
}

function extractBroadcastLanguageKey(
  assetName,
  stationName,
  fallbackLanguageKey,
) {
  const normalizedAsset = normalizeBroadcastMatchKey(assetName);
  const normalizedStation = normalizeBroadcastMatchKey(stationName);
  const stationTokens = new Set(normalizedStation.split(" ").filter(Boolean));
  const genericTokens = new Set(["station", "audio", "voice", "stop"]);
  const stationIndex = normalizedStation
    ? normalizedAsset.indexOf(normalizedStation)
    : -1;
  const remainingSource =
    stationIndex >= 0
      ? `${normalizedAsset.slice(0, stationIndex)} ${normalizedAsset.slice(stationIndex + normalizedStation.length)}`
      : normalizedAsset;
  const remainingTokens = remainingSource
    .split(" ")
    .filter(
      (token) =>
        token && !stationTokens.has(token) && !genericTokens.has(token),
    );

  if (remainingTokens.length === 0) {
    return fallbackLanguageKey;
  }

  const alias = remainingTokens.join(" ");
  const languageKeys = Object.keys(BROADCAST_LANGUAGE_ALIASES);
  for (let index = 0; index < languageKeys.length; index += 1) {
    const languageKey = languageKeys[index];
    if (BROADCAST_LANGUAGE_ALIASES[languageKey]?.includes(alias)) {
      return languageKey;
    }
  }

  return fallbackLanguageKey;
}

function extractBroadcastLanguageHint(
  assetName,
  stationName,
  fallbackLanguageKey,
  labels,
) {
  const languageKey = extractBroadcastLanguageKey(
    assetName,
    stationName,
    fallbackLanguageKey,
  );
  return resolveBroadcastLanguageLabel(languageKey, labels);
}

export {
  createEmptyExternalAssetBrowserState,
  buildBroadcastTrayAssetLibrary,
  formatBroadcastAssetDisplayName,
  extractBroadcastLanguageKey,
  extractBroadcastLanguageHint,
  EXTERNAL_ASSET_FILE_SYSTEM,
  DEFAULT_EXTERNAL_ASSET_PATH,
};
