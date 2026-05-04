import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { buildAutoStagedPlan, getLineKinds, hasMinimumDepartureGapForOrigin, MIN_DEPARTURE_INTERVAL_MINUTES } from "./lib/auto-schedule";
import { getWorkbenchApi } from "./lib/workbench-api";
import { minutesToTime, timeToMinutes } from "./lib/time";
import { useNativeScheduleI18n } from "./native-schedule-i18n";
import { validateManualRows } from "./lib/validation";
import WorkbenchDropdown from "./components/WorkbenchDropdown";
import WorkbenchScrollArea from "./components/WorkbenchScrollArea";

const NATIVE_SCHEDULE_PERSIST_KEY = "rtm.nativeSchedule.frontendDraft.v1";
const NATIVE_SCHEDULE_PERSIST_SCHEMA_VERSION = 3;
const MIN_LINE_SETTING_MINUTES = 5;

function DemoTextField({
  label,
  value,
  onCommit,
  className,
  placeholder,
  readOnly = false,
  inputRef,
  timeMode = false,
  nextInputRef,
  onDraftChange,
  errorText = "",
  preserveInvalidTime = false,
  reserveErrorSpace = false,
  suffix = ""
}) {
  const localInputRef = useRef(null);
  const resolvedInputRef = inputRef || localInputRef;
  const [draftValue, setDraftValue] = useState(String(value ?? ""));

  useEffect(() => {
    const nextValue = String(value ?? "");
    setDraftValue(nextValue);
  }, [value]);

  function commitCurrentValue() {
    if (readOnly || typeof onCommit !== "function") {
      return;
    }

    let nextValue = String(draftValue || "");
    if (timeMode) {
      nextValue = normalizeTimeInput(nextValue);
      if (!isValidTimeValue(nextValue)) {
        if (!preserveInvalidTime) {
          setDraftValue(String(value ?? ""));
        }
        return;
      }
      setDraftValue(nextValue);
    }

    onCommit(nextValue);
  }

  return (
    <div className={`dw-demo-field ${className || ""}`}>
      <label className="dw-demo-label">
        <span className="dw-demo-label-content">
          <span>{label}</span>
        </span>
      </label>
      <div className={`dw-demo-input-row ${suffix ? "has-suffix" : ""}`}>
        <input
          ref={resolvedInputRef}
          type="text"
          value={draftValue}
          placeholder={placeholder}
          readOnly={readOnly}
          className={`dw-demo-input ${suffix ? "has-suffix" : ""} ${readOnly ? "is-readonly" : ""} ${errorText ? "is-error" : ""}`}
          inputMode={timeMode ? "numeric" : "text"}
          maxLength={timeMode ? 5 : undefined}
          onChange={(event) => {
            const rawValue = event.currentTarget.value;
            const nextDraftValue = timeMode ? normalizeTimeInput(rawValue) : rawValue;
            setDraftValue(nextDraftValue);
            if (typeof onDraftChange === "function") {
              onDraftChange(nextDraftValue);
            }
          }}
          onPaste={(event) => {
            if (!timeMode) {
              return;
            }

            event.preventDefault();
            const pastedText = event.clipboardData?.getData("text") || "";
            const nextDraftValue = normalizeTimeInput(pastedText);
            setDraftValue(nextDraftValue);
            if (typeof onDraftChange === "function") {
              onDraftChange(nextDraftValue);
            }
          }}
          onBlur={commitCurrentValue}
          onKeyDown={(event) => {
            if (timeMode) {
              const allowedKeys = [
                "Backspace",
                "Delete",
                "Tab",
                "ArrowLeft",
                "ArrowRight",
                "ArrowUp",
                "ArrowDown",
                "Home",
                "End",
                "Enter"
              ];
              const isDigitKey = event.key >= "0" && event.key <= "9";
              const isCtrlCommand = event.ctrlKey || event.metaKey;
              if (!isDigitKey && !allowedKeys.includes(event.key) && !isCtrlCommand) {
                event.preventDefault();
                return;
              }
            }

            if (event.key === "Tab" && nextInputRef?.current) {
              event.preventDefault();
              commitCurrentValue();
              nextInputRef.current.focus();
              if (typeof nextInputRef.current.select === "function") {
                nextInputRef.current.select();
              }
              return;
            }

            if (event.key === "Enter") {
              commitCurrentValue();
              event.currentTarget.blur();
            }
          }}
        />
        {suffix ? <span className="dw-demo-input-suffix">{suffix}</span> : null}
      </div>
      <div className={`dw-demo-field-error-slot ${reserveErrorSpace ? "is-reserved" : ""} ${errorText ? "has-error" : ""}`}>
        {errorText ? <div className="dw-demo-field-error">{errorText}</div> : null}
      </div>
    </div>
  );
}

function DemoDisplayField({
  label,
  value,
  className
}) {
  return (
    <div className={`dw-demo-field ${className || ""}`}>
      <label className="dw-demo-label">
        <span className="dw-demo-label-content">
          <span>{label}</span>
        </span>
      </label>
      <div className="dw-demo-display-value" title={value || ""}>
        <span>{value}</span>
      </div>
    </div>
  );
}

function DemoEmptyScheduleIcon() {
  return (
    <svg viewBox="0 0 56 48" className="dw-demo-empty-icon">
      <rect x="12" y="10" width="32" height="28" rx="4" fill="none" stroke="#e6ecf0" strokeWidth="2.6" />
      <path d="M20 8v5" fill="none" stroke="#e6ecf0" strokeWidth="2.6" strokeLinecap="round" />
      <path d="M36 8v5" fill="none" stroke="#e6ecf0" strokeWidth="2.6" strokeLinecap="round" />
      <path d="M12 17h32" fill="none" stroke="#e6ecf0" strokeWidth="2.6" />
      <circle cx="20" cy="23" r="1.8" fill="#e6ecf0" />
      <path d="M25 23h11" fill="none" stroke="#e6ecf0" strokeWidth="2.6" strokeLinecap="round" />
      <circle cx="20" cy="29" r="1.8" fill="#e6ecf0" />
      <path d="M25 29h9" fill="none" stroke="#e6ecf0" strokeWidth="2.6" strokeLinecap="round" />
    </svg>
  );
}

function DemoPlayIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-demo-button-icon is-play" aria-hidden="true">
      <path d="M7 4.5 18 12 7 19.5Z" fill="currentColor" />
    </svg>
  );
}

function DemoAlertIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-demo-button-icon is-play" aria-hidden="true">
      <circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" strokeWidth="2.2" />
      <path d="M12 7.2v6.1" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
      <circle cx="12" cy="16.8" r="1.2" fill="currentColor" />
    </svg>
  );
}

function DemoAppliedStateIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-demo-button-icon is-play" aria-hidden="true">
      <path d="M6.6 12.4 10.4 16.2 17.8 8.8" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

function DemoImportLeftIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-demo-button-icon is-import" aria-hidden="true">
      <path d="M11 7 7 12l4 5" fill="none" stroke="#5ab4c5" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M17 7 13 12l4 5" fill="none" stroke="#5ab4c5" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" opacity="0.92" />
    </svg>
  );
}

function DemoOffsetField({
  label,
  direction,
  minutes,
  onDirectionChange,
  onMinutesChange,
  className,
  hint
}) {
  const { t } = useNativeScheduleI18n();

  return (
    <div className={`dw-demo-field dw-demo-offset-field ${className || ""}`}>
      <label className="dw-demo-label">{label}</label>
      <div className="dw-demo-offset-control">
        <div className="dw-demo-offset-toggle">
          <button
            type="button"
            className={`dw-demo-offset-toggle-button ${direction === "early" ? "is-active" : ""}`}
            onClick={() => onDirectionChange(direction === "early" ? "" : "early")}
          >
            {t("nativeSchedule.toggle.early")}
          </button>
          <button
            type="button"
            className={`dw-demo-offset-toggle-button ${direction === "late" ? "is-active" : ""}`}
            onClick={() => onDirectionChange(direction === "late" ? "" : "late")}
          >
            {t("nativeSchedule.toggle.late")}
          </button>
        </div>
        <div className="dw-demo-offset-minutes-wrap">
          <input
            type="text"
            value={minutes}
            className="dw-demo-input dw-demo-offset-minutes"
            inputMode="numeric"
            maxLength={2}
            onChange={(event) => {
              onMinutesChange(String(event.currentTarget.value || "").replace(/\D/g, "").slice(0, 2));
            }}
          />
          {!minutes ? <span className="dw-demo-offset-hint">{hint}</span> : null}
        </div>
      </div>
    </div>
  );
}

function chunkItemsBySize(items, chunkSize) {
  if (!Array.isArray(items) || items.length === 0) {
    return [];
  }

  const nextChunkSize = Math.max(1, Number(chunkSize) || items.length);
  const nextRows = [];

  for (let index = 0; index < items.length; index += nextChunkSize) {
    nextRows.push(items.slice(index, index + nextChunkSize));
  }

  return nextRows;
}

const TOP_PREVIEW_ROW_CACHE = new Map();
const RULE_PREVIEW_ROW_CACHE = new Map();
const PREVIEW_ROW_CACHE_LIMIT = 128;

function rememberPreviewRows(cache, key, value) {
  if (cache.has(key)) {
    const existingValue = cache.get(key);
    cache.delete(key);
    cache.set(key, existingValue);
    return existingValue;
  }

  cache.set(key, value);
  if (cache.size > PREVIEW_ROW_CACHE_LIMIT) {
    const firstKey = cache.keys().next().value;
    cache.delete(firstKey);
  }
  return value;
}

function getTopPreviewMaxItemsPerRow(script, hasMeta) {
  const isLatin = script === "latin";
  if (isLatin) {
    return hasMeta ? 6 : 7;
  }

  return hasMeta ? 7 : 8;
}

function getRulePreviewMaxItemsPerRow(script, showOffsetColumn) {
  const isLatin = script === "latin";
  if (isLatin) {
    return showOffsetColumn ? 10 : 11;
  }

  return showOffsetColumn ? 11 : 12;
}

function getCachedTopPreviewRows(times, maxItemsPerRow) {
  const previewTimes = Array.isArray(times) ? times : [];
  if (previewTimes.length === 0) {
    return [];
  }

  const cacheKey = `${maxItemsPerRow}|${previewTimes.join("|")}`;
  if (TOP_PREVIEW_ROW_CACHE.has(cacheKey)) {
    return rememberPreviewRows(TOP_PREVIEW_ROW_CACHE, cacheKey);
  }

  return rememberPreviewRows(
    TOP_PREVIEW_ROW_CACHE,
    cacheKey,
    chunkItemsBySize(previewTimes, maxItemsPerRow)
  );
}

function getCachedRulePreviewRows(entries, showSkipped, moveSkippedToEnd, maxItemsPerRow) {
  const sourceEntries = Array.isArray(entries) ? entries : [];
  const cacheKey = `${maxItemsPerRow}|${showSkipped ? 1 : 0}|${moveSkippedToEnd ? 1 : 0}|${sourceEntries.map((entry) => `${entry?.time || ""}:${entry?.skipped ? 1 : 0}`).join("|")}`;
  if (RULE_PREVIEW_ROW_CACHE.has(cacheKey)) {
    return rememberPreviewRows(RULE_PREVIEW_ROW_CACHE, cacheKey);
  }

  const previewEntries = !moveSkippedToEnd || sourceEntries.length <= 1
    ? sourceEntries
    : [
      ...sourceEntries.filter((entry) => !entry?.skipped),
      ...sourceEntries.filter((entry) => entry?.skipped)
    ];

  const keptEntries = showSkipped ? previewEntries.filter((entry) => !entry?.skipped) : previewEntries;
  const skippedEntries = showSkipped ? previewEntries.filter((entry) => entry?.skipped) : [];
  const nextRows = [
    ...chunkItemsBySize(keptEntries, maxItemsPerRow).map((rowEntries) => ({
      text: rowEntries.map((entry) => entry.time).join(" · "),
      isSkipped: false
    })),
    ...chunkItemsBySize(skippedEntries, maxItemsPerRow).map((rowEntries) => ({
      text: rowEntries.map((entry) => entry.time).join(" · "),
      isSkipped: true
    }))
  ];

  return rememberPreviewRows(RULE_PREVIEW_ROW_CACHE, cacheKey, nextRows);
}

function DemoTopPreviewTimes({
  times,
  maxItemsPerRow
}) {
  const previewTimes = Array.isArray(times) ? times : [];
  if (previewTimes.length === 0) {
    return <span className="dw-demo-preview-empty">--</span>;
  }

  const groupedRows = getCachedTopPreviewRows(previewTimes, maxItemsPerRow);

  return (
    <span className="dw-demo-preview-grouped">
      {groupedRows.map((rowTimes, rowIndex) => (
        <span key={`row-${rowIndex}`} className="dw-demo-preview-rule-row">
          {rowTimes.map((time, index) => (
            <span key={`${time}-${rowIndex}-${index}`} className="dw-demo-preview-item">
              {index > 0 ? <span className="dw-demo-preview-separator" aria-hidden="true">·</span> : null}
              <span className="dw-demo-preview-token">
                <span className="dw-demo-preview-time">{time}</span>
              </span>
            </span>
          ))}
        </span>
      ))}
    </span>
  );
}

function DemoRuleTextPreviewTimes({
  entries,
  showSkipped = false,
  moveSkippedToEnd = false,
  maxItemsPerRow
}) {
  const rowsToRender = getCachedRulePreviewRows(entries, showSkipped, moveSkippedToEnd, maxItemsPerRow);
  if (rowsToRender.length === 0) {
    return <span className="dw-demo-preview-empty">--</span>;
  }

  return (
    <span className="dw-demo-preview-grouped">
      {rowsToRender.map((row, rowIndex) => {
        return (
          <span key={`row-${rowIndex}`} className={`dw-demo-preview-rule-row is-text ${row.isSkipped ? "is-skipped-row" : ""}`}>
            <span className={`dw-demo-preview-rule-text-part ${row.isSkipped ? "is-skipped is-block" : ""}`}>{row.text}</span>
          </span>
        );
      })}
    </span>
  );
}

function SummaryBadge({ kind }) {
  const { t } = useNativeScheduleI18n();

  return (
    <span className={`dw-demo-badge ${kind === "express" ? "is-express" : "is-local"}`}>
      {getLocalizedLineType(kind, t, "compact")}
    </span>
  );
}

function normalizeTimeInput(rawValue) {
  const rawText = String(rawValue || "");
  const digitsOnly = rawText.replace(/\D/g, "").slice(0, 4);
  if (digitsOnly.length < 2) {
    return digitsOnly;
  }
  if (digitsOnly.length === 2) {
    return rawText.indexOf(":") >= 0 ? digitsOnly + ":" : digitsOnly;
  }

  return digitsOnly.slice(0, 2) + ":" + digitsOnly.slice(2);
}

function normalizeFrequencyInput(rawValue) {
  const source = String(rawValue || "");
  let result = "";
  let dotSeen = false;

  for (let index = 0; index < source.length; index += 1) {
    const char = source[index];
    if (char >= "0" && char <= "9") {
      result += char;
      continue;
    }

    if (char === "." && !dotSeen) {
      result += char;
      dotSeen = true;
    }
  }

  return result;
}

function isValidTimeValue(value) {
  const match = /^(\d{2}):(\d{2})$/.exec(String(value || "").trim());
  if (!match) {
    return false;
  }

  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  return Number.isFinite(hours) && Number.isFinite(minutes) && hours >= 0 && hours <= 23 && minutes >= 0 && minutes <= 59;
}

function parseFrequencyValue(value) {
  const numeric = Number(String(value || "").trim());
  if (!Number.isFinite(numeric) || numeric <= 0) {
    return 0;
  }

  return numeric;
}

const DEFAULT_DEPOT_OPTIONS = [
  { id: "any-depot", labelKey: "nativeSchedule.data.depot.any" },
  { id: "north-depot", labelKey: "nativeSchedule.data.depot.north" }
];

const DEFAULT_ORIGIN_OPTIONS = [
  { id: "origin-industrial", labelKey: "nativeSchedule.data.origin.industrial" }
];

const DEFAULT_LINE_OPTIONS = [
  {
    id: "line-local",
    corridorId: "industrial-corridor",
    nameKey: "nativeSchedule.data.line.local",
    kind: "local",
    transportType: "",
    color: "#5ab4c5",
    depotId: "any-depot",
    originId: "origin-industrial",
    originStationId: "origin-industrial",
    hold: "15",
    dwell: "6"
  },
  {
    id: "line-express",
    corridorId: "industrial-corridor",
    nameKey: "nativeSchedule.data.line.express",
    kind: "express",
    transportType: "",
    color: "#c084fc",
    depotId: "north-depot",
    originId: "origin-industrial",
    originStationId: "origin-industrial",
    hold: "10",
    dwell: "4"
  }
];

const DEPOT_OPTIONS = DEFAULT_DEPOT_OPTIONS.map((option) => ({ ...option }));
const ORIGIN_OPTIONS = DEFAULT_ORIGIN_OPTIONS.map((option) => ({ ...option }));
const LINE_OPTIONS = DEFAULT_LINE_OPTIONS.map((option) => ({ ...option }));

function cloneOptions(options) {
  return (Array.isArray(options) ? options : []).map((option) => ({ ...option }));
}

function replaceRuntimeOptions(target, nextOptions) {
  target.splice(0, target.length, ...cloneOptions(nextOptions));
}

function replaceRuntimeCatalog({ lines, depots, origins }) {
  replaceRuntimeOptions(LINE_OPTIONS, lines);
  replaceRuntimeOptions(DEPOT_OPTIONS, depots);
  replaceRuntimeOptions(ORIGIN_OPTIONS, origins);
}

function clampPositiveMinutes(value, fallbackValue) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric <= 0) {
    return fallbackValue;
  }

  return Math.max(MIN_LINE_SETTING_MINUTES, Math.min(120, Math.round(numeric)));
}

function buildNativeDepotOptions(snapshotDepots = []) {
  if (!Array.isArray(snapshotDepots) || snapshotDepots.length === 0) {
    return cloneOptions(DEFAULT_DEPOT_OPTIONS);
  }

  return snapshotDepots.map((depot, index) => ({
    id: depot?.id || `depot-${index + 1}`,
    label: depot?.name || depot?.id || `Depot ${index + 1}`,
    transportType: depot?.transportType || ""
  }));
}

function buildNativeLineOptions(snapshotLines = [], t) {
  if (!Array.isArray(snapshotLines) || snapshotLines.length === 0) {
    return cloneOptions(DEFAULT_LINE_OPTIONS);
  }

  return snapshotLines.map((line, index) => {
    const fallbackKey = line?.sourceLineId || line?.id || String(index + 1);
    const fallbackName = (line?.kind === "express" ? "Rapid " : "Local ") + fallbackKey;

    return {
      id: line?.id || `line-${index + 1}`,
      corridorId: line?.sourceLineId || line?.id || `corridor-${index + 1}`,
      name: line?.name || fallbackName,
      nameKey: "",
      kind: line?.kind === "express" ? "express" : "local",
      transportType: line?.transportType || "",
      color: line?.color || (line?.kind === "express" ? "#c084fc" : "#5ab4c5"),
      depotId: line?.allowedDepotId || "",
      originId: line?.originStationId || `origin-${index + 1}`,
      originStationId: line?.originStationId || `origin-${index + 1}`,
      originStationName: line?.originStationName || line?.originStationId || `Origin ${index + 1}`,
      hold: String(clampPositiveMinutes(line?.originHoldLimitMinutes, 20)),
      dwell: String(clampPositiveMinutes(line?.maxStationDwellMinutes, 10))
    };
  });
}

function buildNativeOriginOptions(lineOptions = []) {
  const seen = new Set();
  const origins = [];

  lineOptions.forEach((line, index) => {
    const originId = line?.originId || line?.originStationId || `origin-${index + 1}`;
    if (!originId || seen.has(originId)) {
      return;
    }

    seen.add(originId);
    origins.push({
      id: originId,
      label: line?.originStationName || line?.originName || line?.originId || originId
    });
  });

  return origins.length > 0 ? origins : cloneOptions(DEFAULT_ORIGIN_OPTIONS);
}

function overlayPersistedLineSettings(lineOptions = [], persistedLineSettings = []) {
  const settingsById = new Map(
    (Array.isArray(persistedLineSettings) ? persistedLineSettings : [])
      .filter((entry) => entry?.id)
      .map((entry) => [entry.id, entry])
  );

  return lineOptions.map((line) => {
    const persisted = settingsById.get(line.id);
    if (!persisted) {
      return line;
    }

    return {
      ...line,
      depotId: persisted.depotId || line.depotId,
      hold: String(clampPositiveMinutes(persisted.hold, Number(line.hold) || 20)),
      dwell: String(clampPositiveMinutes(persisted.dwell, Number(line.dwell) || 10))
    };
  });
}

function buildRuntimeCatalog(snapshot, metadataSnapshot, persistedState, t) {
  const sourceSnapshot =
    Array.isArray(snapshot?.lines) && snapshot.lines.length > 0
      ? snapshot
      : metadataSnapshot;

  const lineOptions = overlayPersistedLineSettings(
    buildNativeLineOptions(sourceSnapshot?.lines, t),
    persistedState?.lineSettings
  );
  const depotOptions = buildNativeDepotOptions(sourceSnapshot?.depots);
  const originOptions = buildNativeOriginOptions(lineOptions);

  return {
    lineOptions,
    depotOptions,
    originOptions
  };
}

function createNativeMergedViewForSave(selectedLineId, snapshotMergedView = null) {
  const sourceView =
    snapshotMergedView && typeof snapshotMergedView === "object"
      ? snapshotMergedView
      : {};

  return {
    localLineId: typeof sourceView.localLineId === "string" ? sourceView.localLineId : (selectedLineId || ""),
    expressLineId: typeof sourceView.expressLineId === "string" ? sourceView.expressLineId : "",
    localLineIds:
      Array.isArray(sourceView.localLineIds) && sourceView.localLineIds.length > 0
        ? sourceView.localLineIds.filter((lineId) => typeof lineId === "string" && lineId.length > 0)
        : (selectedLineId ? [selectedLineId] : []),
    expressLineIds:
      Array.isArray(sourceView.expressLineIds)
        ? sourceView.expressLineIds.filter((lineId) => typeof lineId === "string" && lineId.length > 0)
        : [],
    isLoop: typeof sourceView.isLoop === "boolean" ? sourceView.isLoop : true,
    turnbackStationId: typeof sourceView.turnbackStationId === "string" ? sourceView.turnbackStationId : "",
    direction: typeof sourceView.direction === "string" && sourceView.direction ? sourceView.direction : "up",
    windowStart: typeof sourceView.windowStart === "string" && sourceView.windowStart ? sourceView.windowStart : "06:00",
    windowEnd: typeof sourceView.windowEnd === "string" && sourceView.windowEnd ? sourceView.windowEnd : "06:30"
  };
}

function serializeNativeLineSettings(lines = LINE_OPTIONS) {
  return (Array.isArray(lines) ? lines : [])
    .filter((line) => line && typeof line === "object" && line.id)
    .map((line) => ({
      lineId: line.id,
      originHoldLimitMinutes: clampPositiveMinutes(line.hold, 20),
      maxStationDwellMinutes: clampPositiveMinutes(line.dwell, 10),
      allowedDepotId: line.depotId === "any-depot" ? "" : (line.depotId || ""),
      serviceKind: normalizeKind(line.kind)
    }));
}

function serializeNativeManualRows(rows = []) {
  return sortManualDraftRows(Array.isArray(rows) ? rows : [])
    .filter((row) => row?.lineId)
    .map((row, index) => ({
      id: String(row?.id || `manual-${index + 1}`),
      lineId: row.lineId,
      time: row?.time || "",
      kind: normalizeKind(row?.kind),
      offsetMode: row?.offsetMode || "none",
      offsetMinutes: row?.offsetMinutes === 0 ? "0" : String(row?.offsetMinutes || "")
    }));
}

function serializeNativeAutoRules(rows = []) {
  return sortAutoRuleRows(Array.isArray(rows) ? rows : [])
    .filter((rule) => rule?.lineId)
    .map((rule, index) => {
      const kind = normalizeKind(rule?.kind);
      const departuresPerHour = parseFrequencyValue(rule?.departuresPerHour);
      const expressOffsetMinutes = Math.max(0, Math.round(Math.abs(Number(rule?.expressOffsetMinutes) || 0)));
      return {
        id: String(rule?.id || `rule-${index + 1}`),
        lineId: rule.lineId,
        enabled: rule?.enabled !== false,
        start: rule?.start || "08:00",
        end: rule?.end || "10:00",
        kind,
        departuresPerHour,
        localPerHour: kind === "local" ? departuresPerHour : 0,
        expressPerHour: kind === "express" ? departuresPerHour : 0,
        expressOffsetMode: kind === "express" ? (rule?.expressOffsetMode || "after") : "after",
        expressOffsetMinutes: kind === "express" ? expressOffsetMinutes : 0
      };
    });
}

function serializeNativeStagedRows(rows = []) {
  return [...(Array.isArray(rows) ? rows : [])]
    .filter((row) => row?.lineId)
    .sort((left, right) => {
      const leftMinutes = timeToMinutes(left?.time) ?? 9999;
      const rightMinutes = timeToMinutes(right?.time) ?? 9999;
      if (leftMinutes !== rightMinutes) {
        return leftMinutes - rightMinutes;
      }

      if ((left?.lineId || "") !== (right?.lineId || "")) {
        return String(left?.lineId || "").localeCompare(String(right?.lineId || ""));
      }

      if (normalizeKind(left?.kind) !== normalizeKind(right?.kind)) {
        return normalizeKind(left?.kind).localeCompare(normalizeKind(right?.kind));
      }

      return String(left?.id || "").localeCompare(String(right?.id || ""));
    })
    .map((row, index) => ({
      id: String(row?.id || `staged-${index + 1}`),
      lineId: row.lineId,
      time: row?.time || "",
      kind: normalizeKind(row?.kind),
      source: row?.source || "manual",
      note: row?.note || ""
    }));
}

function mapSnapshotManualRows(rows = [], fallbackLineId = "") {
  return (Array.isArray(rows) ? rows : []).map((row, index) => ({
    id: row?.id || `manual-${index + 1}`,
    lineId: row?.lineId || fallbackLineId,
    serviceId: row?.lineId || fallbackLineId,
    time: row?.time || "",
    kind: row?.kind === "express" ? "express" : "local",
    offsetMode: row?.offsetMode || "none",
    offsetMinutes: row?.offsetMinutes === 0 ? "0" : String(row?.offsetMinutes || "")
  }));
}

function mapSnapshotAutoRules(rows = [], fallbackLineId = "") {
  return (Array.isArray(rows) ? rows : []).map((rule, index) => {
    const kind = rule?.kind === "express" ? "express" : "local";
    const departuresPerHour =
      Number(rule?.departuresPerHour) > 0
        ? Number(rule.departuresPerHour)
        : kind === "express"
          ? Number(rule?.expressPerHour) || 0
          : Number(rule?.localPerHour) || 0;

    return {
      id: rule?.id || `rule-${index + 1}`,
      lineId: rule?.lineId || fallbackLineId,
      serviceId: rule?.lineId || fallbackLineId,
      kind,
      enabled: rule?.enabled !== false,
      start: rule?.start || "08:00",
      end: rule?.end || "10:00",
      departuresPerHour,
      expressOffsetMode: rule?.expressOffsetMode || "after",
      expressOffsetMinutes: Number(rule?.expressOffsetMinutes) || 0,
      localPerHour: kind === "local" ? departuresPerHour : 0,
      expressPerHour: kind === "express" ? departuresPerHour : 0
    };
  });
}

function mapSnapshotSummaryRows(rows = []) {
  return (Array.isArray(rows) ? rows : []).map((row, index) => ({
    id: row?.id || `summary-${index + 1}`,
    serviceId: row?.lineId || "",
    lineId: row?.lineId || "",
    time: row?.time || "",
    kind: row?.kind === "express" ? "express" : "local",
    source: row?.source || "manual",
    note: row?.note || ""
  }));
}

function readPersistedNativeScheduleState() {
  if (typeof window === "undefined" || !window.localStorage) {
    return null;
  }

  try {
    const raw = window.localStorage.getItem(NATIVE_SCHEDULE_PERSIST_KEY);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object") {
      return null;
    }

    return Number(parsed.schemaVersion) === NATIVE_SCHEDULE_PERSIST_SCHEMA_VERSION
      ? parsed
      : null;
  } catch {
    return null;
  }
}

function writePersistedNativeScheduleState(nextState) {
  if (typeof window === "undefined" || !window.localStorage) {
    return;
  }

  try {
    window.localStorage.setItem(
      NATIVE_SCHEDULE_PERSIST_KEY,
      JSON.stringify({
        schemaVersion: NATIVE_SCHEDULE_PERSIST_SCHEMA_VERSION,
        ...(nextState && typeof nextState === "object" ? nextState : {})
      })
    );
  } catch {}
}

function normalizeKind(value) {
  if (value === "express") {
    return "express";
  }

  return "local";
}

function getLocalizedLineType(kind, t, variant = "regular") {
  const normalizedKind = normalizeKind(kind);
  if (variant === "compact") {
    return t(`nativeSchedule.type.${normalizedKind}.compact`);
  }

  return t(`nativeSchedule.type.${normalizedKind}`);
}

function getDepotOptionById(depotId) {
  return DEPOT_OPTIONS.find((depot) => depot.id === depotId) ?? null;
}

function getOriginOptionById(originId) {
  return ORIGIN_OPTIONS.find((origin) => origin.id === originId) ?? ORIGIN_OPTIONS[0];
}

function getLocalizedDepotLabel(depotId, t) {
  if (!depotId) {
    return t("nativeSchedule.data.depot.any");
  }

  const depot = getDepotOptionById(depotId);
  if (!depot) {
    return t("nativeSchedule.data.depot.any");
  }

  return depot.label || t(depot.labelKey);
}

function getLocalizedOriginLabel(originId, t) {
  const origin = getOriginOptionById(originId);
  if (!origin) {
    return "";
  }

  return origin.label || t(origin.labelKey);
}

function getLocalizedLineName(line, t) {
  if (line?.name) {
    return line.name;
  }

  return t(line?.nameKey || LINE_OPTIONS[0]?.nameKey || "nativeSchedule.data.line.local");
}

function directionFromOffsetMode(offsetMode) {
  return offsetMode === "before" ? "early" : "late";
}

function offsetModeFromDirection(direction) {
  return direction === "early" ? "before" : "after";
}

function getLineOptionById(lineId) {
  return LINE_OPTIONS.find((line) => line.id === lineId) ?? LINE_OPTIONS[0];
}

function getLineOptionByKind(kind, corridorId = "", fallbackToAny = true) {
  const normalizedKind = normalizeKind(kind);
  const exactMatch = LINE_OPTIONS.find((line) => line.kind === normalizedKind && (!corridorId || line.corridorId === corridorId));
  if (exactMatch) {
    return exactMatch;
  }

  if (!fallbackToAny) {
    return null;
  }

  return LINE_OPTIONS.find((line) => line.kind === normalizedKind) ?? LINE_OPTIONS[0];
}

function getReferenceLineIdsForLine(lineOption, kind) {
  if (normalizeKind(kind) !== "express") {
    return [lineOption.id];
  }

  return LINE_OPTIONS
    .filter((line) => line.corridorId === lineOption.corridorId && line.kind === "local")
    .map((line) => line.id);
}

function buildCombinedNote(noteType, t, values = {}) {
  if (noteType === "before") {
    return t("combined.note.beforePaired", values);
  }

  if (noteType === "after") {
    return t("combined.note.afterPaired", values);
  }

  if (noteType === "generated") {
    return t("combined.note.generated", values);
  }

  return t("combined.note.direct");
}

function createSummaryEntry({
  id,
  time,
  serviceId,
  kind,
  source = "manual",
  note = ""
}, t) {
  const fallbackOption = getLineOptionByKind(kind);
  const selectedOption = getLineOptionById(serviceId);
  const resolvedKind = normalizeKind(kind || selectedOption.kind || fallbackOption.kind);
  const lineOption =
    selectedOption.kind === resolvedKind
      ? selectedOption
      : getLineOptionByKind(resolvedKind) || selectedOption || fallbackOption;

  return {
    id,
    lineId: lineOption.id,
    serviceId: lineOption.id,
    lineNameKey: lineOption.nameKey,
    lineName: getLocalizedLineName(lineOption, t),
    lineColor: lineOption.color,
    time,
    kind: resolvedKind,
    source,
    note: note || t("combined.note.direct"),
    originId: lineOption.originId,
    originStationId: lineOption.originStationId,
    origin: getLocalizedOriginLabel(lineOption.originId, t)
  };
}

function getSummaryRowKey(row) {
  return `${row?.lineId || row?.serviceId || ""}|${normalizeKind(row?.kind)}|${row?.time || ""}`;
}

function getSummaryRowsSignature(rows) {
  return (Array.isArray(rows) ? rows : [])
    .map((row) => getSummaryRowKey(row))
    .sort()
    .join("||");
}

function normalizeSummaryEntries(rows, t) {
  const seen = new Set();

  return (Array.isArray(rows) ? rows : [])
    .map((row, index) => createSummaryEntry({
      id: row?.id || `summary-${index + 1}`,
      time: row?.time || "",
      serviceId: row?.serviceId || getLineOptionByKind(normalizeKind(row?.type)).id,
      kind: row?.kind || normalizeKind(row?.type),
      source: row?.source || "manual",
      note: row?.note || t("combined.note.direct")
    }, t))
    .filter((row) => {
      if (timeToMinutes(row.time) === null) {
        return false;
      }

      const key = `${row.lineId}|${row.kind}|${row.time}|${row.source}`;
      if (seen.has(key)) {
        return false;
      }

      seen.add(key);
      return true;
    });
}

const SUMMARY_ROWS = [
  { id: 1, serviceId: "line-local", time: "00:00", kind: "local", source: "manual" },
  { id: 2, serviceId: "line-local", time: "00:30", kind: "local", source: "manual" },
  { id: 3, serviceId: "line-express", time: "01:00", kind: "express", source: "auto" },
  { id: 4, serviceId: "line-local", time: "01:00", kind: "local", source: "manual" },
  { id: 5, serviceId: "line-local", time: "01:30", kind: "local", source: "manual" },
  { id: 6, serviceId: "line-local", time: "02:00", kind: "local", source: "manual" },
  { id: 7, serviceId: "line-express", time: "02:30", kind: "express", source: "auto" },
  { id: 8, serviceId: "line-local", time: "03:00", kind: "local", source: "manual" },
  { id: 9, serviceId: "line-express", time: "03:30", kind: "express", source: "auto" },
  { id: 10, serviceId: "line-local", time: "04:00", kind: "local", source: "manual" },
  { id: 11, serviceId: "line-local", time: "04:30", kind: "local", source: "manual" }
];

const INITIAL_AUTO_RULES = [
  {
    id: 1,
    lineId: "line-local",
    serviceId: "line-local",
    kind: "local",
    enabled: true,
    start: "00:00",
    end: "06:00",
    departuresPerHour: 2,
    expressOffsetMode: "after",
    expressOffsetMinutes: 0
  },
  {
    id: 2,
    lineId: "line-express",
    serviceId: "line-express",
    kind: "express",
    enabled: true,
    start: "06:00",
    end: "09:00",
    departuresPerHour: 4,
    expressOffsetMode: "after",
    expressOffsetMinutes: 5
  },
  {
    id: 3,
    lineId: "line-local",
    serviceId: "line-local",
    kind: "local",
    enabled: true,
    start: "09:00",
    end: "17:00",
    departuresPerHour: 3,
    expressOffsetMode: "after",
    expressOffsetMinutes: 0
  }
];

const INITIAL_MANUAL_DRAFTS = [
  {
    id: 1,
    lineId: "line-local",
    serviceId: "line-local",
    kind: "local",
    time: "12:20",
    offsetMode: "none",
    offsetMinutes: ""
  },
  {
    id: 2,
    lineId: "line-local",
    serviceId: "line-local",
    kind: "local",
    time: "12:30",
    offsetMode: "none",
    offsetMinutes: ""
  }
];

function buildSummaryRowsWithConflicts(rows, t, appliedRowKeySet = null) {
  const duplicateCounts = new Map();
  const lineKinds = new Map();
  const rowsWithMinutes = (Array.isArray(rows) ? rows : [])
    .map((row) => ({ row, minute: timeToMinutes(row.time) }))
    .filter((entry) => entry.minute !== null);

  rowsWithMinutes.forEach(({ row }) => {
    const duplicateKey = `${row.lineId}|${row.kind}|${row.time}`;
    duplicateCounts.set(duplicateKey, (duplicateCounts.get(duplicateKey) || 0) + 1);
    const kinds = lineKinds.get(row.lineId) ?? new Set();
    kinds.add(row.kind);
    lineKinds.set(row.lineId, kinds);
  });

  rowsWithMinutes.sort((left, right) => left.minute - right.minute);
  const tooCloseIds = new Set();
  for (let index = 1; index < rowsWithMinutes.length; index += 1) {
    const current = rowsWithMinutes[index];
    const previous = rowsWithMinutes[index - 1];
    if (!current.row.originStationId || current.row.originStationId !== previous.row.originStationId) {
      continue;
    }

    if (current.minute - previous.minute < MIN_DEPARTURE_INTERVAL_MINUTES) {
      tooCloseIds.add(current.row?.id);
      tooCloseIds.add(previous.row?.id);
    }
  }

  return rowsWithMinutes
    .map(({ row }) => {
      const duplicateKey = `${row.lineId}|${row.kind}|${row.time}`;
      const isDuplicate = (duplicateCounts.get(duplicateKey) || 0) > 1;
      const hasKindConflict = (lineKinds.get(row.lineId)?.size || 0) > 1;
      const isTooClose = tooCloseIds.has(row.id);
      const isConflict = isDuplicate || hasKindConflict || isTooClose;
      const conflictReasons = [];

      if (isDuplicate) {
        conflictReasons.push(formatConflictReason("duplicate", t, "compact"));
      }

      if (hasKindConflict) {
        conflictReasons.push(formatConflictReason("kind", t, "compact"));
      }

      if (isTooClose) {
        conflictReasons.push(formatConflictReason("gap", t, "compact", { minutes: MIN_DEPARTURE_INTERVAL_MINUTES }));
      }

      const lineOption = getLineOptionById(row.lineId);

      return {
        ...row,
        sourceLabel:
          row.source === "auto"
            ? t("schedule.source.auto")
            : row.source === "planner"
              ? t("schedule.source.planner")
              : t("schedule.source.manual"),
        note: row.note || t("combined.note.direct"),
        lineName: row.lineName || getLocalizedLineName(lineOption, t),
        origin: getLocalizedOriginLabel(row.originId || lineOption.originId, t),
        isApplied: appliedRowKeySet instanceof Set && appliedRowKeySet.has(getSummaryRowKey(row)),
        isConflict,
        conflictReasonLabel: conflictReasons.join("/"),
        isExpress: row.kind === "express"
      };
    })
    .sort((left, right) => {
      const leftMinutes = timeToMinutes(left.time) ?? 9999;
      const rightMinutes = timeToMinutes(right.time) ?? 9999;
      if (leftMinutes !== rightMinutes) {
        return leftMinutes - rightMinutes;
      }

      if (left.kind !== right.kind) {
        return left.kind.localeCompare(right.kind);
      }

      if (left.lineName !== right.lineName) {
        return left.lineName.localeCompare(right.lineName);
      }

      return left.source.localeCompare(right.source);
    });
}

function resolveOffsetMinutes(direction, minutesText) {
  const minutes = Number(minutesText);
  if (!Number.isFinite(minutes) || minutes <= 0) {
    return 0;
  }

  if (direction === "early") {
    return -minutes;
  }

  if (direction === "late") {
    return minutes;
  }

  return 0;
}

function formatOffsetLabel(direction, minutesText, t, variant = "regular") {
  const minutes = Math.abs(resolveOffsetMinutes(direction, minutesText));
  if (minutes === 0) {
    return t(variant === "compact" ? "nativeSchedule.offset.none.compact" : "nativeSchedule.offset.none");
  }

  const directionLabel =
    variant === "compact"
      ? t(direction === "early" ? "nativeSchedule.offset.direction.early.compact" : "nativeSchedule.offset.direction.late.compact")
      : t(direction === "early" ? "nativeSchedule.toggle.early" : "nativeSchedule.toggle.late");

  return t(variant === "compact" ? "nativeSchedule.offset.label.compact" : "nativeSchedule.offset.label", {
    direction: directionLabel,
    minutes
  });
}

function formatConflictReason(kind, t, variant = "regular", values = {}) {
  const suffix = variant === "compact" ? ".compact" : "";
  return t(`nativeSchedule.conflict.${kind}${suffix}`, values);
}

function buildPreviewMetaText(preview, hasKindConflict = false, t, { detailedSkipReason = false } = {}) {
  if (hasKindConflict) {
    return t("nativeSchedule.preview.meta.kindConflict");
  }

  if (!preview) {
    return "";
  }

  if (preview.reason === "invalid") {
    return t("nativeSchedule.preview.meta.invalidWindow");
  }

  if (preview.reason === "frequencyLimit") {
    return t("nativeSchedule.preview.meta.frequencyLimit");
  }

  if (preview.reason === "tripLimit") {
    return t("nativeSchedule.preview.meta.tripLimit");
  }

  if (preview.skippedCount > 0) {
    if (!detailedSkipReason) {
      return t("nativeSchedule.preview.meta.skipped", {
        count: preview.skippedCount
      });
    }

    const reasons = Array.isArray(preview.skipReasons) ? preview.skipReasons : [];
    const reasonText = reasons
      .map((reason) => t(`nativeSchedule.preview.reason.${reason}`))
      .filter(Boolean)
      .join(" / ");
    return t("nativeSchedule.preview.meta.skippedDetailed", {
      count: preview.skippedCount,
      reason: reasonText || t("nativeSchedule.preview.reason.gap")
    });
  }

  return "";
}

function sortManualDraftRows(rows) {
  return [...rows].sort((left, right) => {
    const leftMinutes = timeToMinutes(left.time) ?? 9999;
    const rightMinutes = timeToMinutes(right.time) ?? 9999;
    if (leftMinutes !== rightMinutes) {
      return leftMinutes - rightMinutes;
    }

    return String(left?.id ?? "").localeCompare(String(right?.id ?? ""));
  });
}

function sortAutoRuleRows(rows) {
  return [...rows].sort((left, right) => {
    const leftStart = timeToMinutes(left.start) ?? 9999;
    const rightStart = timeToMinutes(right.start) ?? 9999;
    if (leftStart !== rightStart) {
      return leftStart - rightStart;
    }

    const leftEnd = timeToMinutes(left.end) ?? 9999;
    const rightEnd = timeToMinutes(right.end) ?? 9999;
    if (leftEnd !== rightEnd) {
      return leftEnd - rightEnd;
    }

    return String(left?.id ?? "").localeCompare(String(right?.id ?? ""));
  });
}

function buildPlanLineOptions(lines = LINE_OPTIONS) {
  return lines
    .filter((line) => line && typeof line === "object")
    .map((line) => ({
      id: line.id || "",
      originStationId: line.originStationId || "",
      originStationName: line.originStationName || line.originId || ""
    }));
}

function DemoSectionHeader({
  title,
  applied = false,
  metrics
}) {
  const statusColor = applied ? "#87d59a" : "#5ab4c5";

  return (
    <div className={`dw-demo-section-header ${applied ? "is-applied" : ""}`}>
      <div className="dw-demo-section-title-wrap">
        <span className="dw-demo-section-status-icon" aria-hidden="true">
          <svg key={applied ? "applied" : "draft"} viewBox="0 0 24 24" className="dw-demo-section-status-svg">
            <circle cx="12" cy="12" r="9" fill="none" stroke={statusColor} strokeWidth="2" className="dw-demo-section-status-ring" />
            {applied ? (
              <path d="M8.2 12.4 10.8 15l5-5.4" fill="none" stroke={statusColor} strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" className="dw-demo-section-status-mark" />
            ) : (
              <>
                <path d="M12 7.4v5.6" fill="none" stroke={statusColor} strokeWidth="2.2" strokeLinecap="round" className="dw-demo-section-status-mark" />
                <circle cx="12" cy="16.7" r="1.2" fill={statusColor} className="dw-demo-section-status-dot" />
              </>
            )}
          </svg>
        </span>
        <div className="dw-demo-section-title">{title}</div>
      </div>
      <div className="dw-demo-summary-metrics">{metrics}</div>
    </div>
  );
}

function DemoPreviewTimes({
  times,
  entries = null,
  showSkipped = false,
  trimWrappedSeparators = false,
  moveSkippedToEnd = false,
  groupIntoRows = false,
  renderGroupedRowsAsText = false
}) {
  const previewEntries = useMemo(() => {
    const sourceEntries = Array.isArray(entries) && entries.length > 0
      ? entries
      : Array.isArray(times)
        ? times.map((time) => ({ time, skipped: false, reason: "" }))
        : [];

    if (!moveSkippedToEnd || sourceEntries.length <= 1) {
      return sourceEntries;
    }

    const keptEntries = [];
    const skippedEntries = [];

    sourceEntries.forEach((entry) => {
      if (entry?.skipped) {
        skippedEntries.push(entry);
      } else {
        keptEntries.push(entry);
      }
    });

    return [...keptEntries, ...skippedEntries];
  }, [entries, moveSkippedToEnd, times]);
  const itemRefs = useRef([]);
  const measureTokenRefs = useRef([]);
  const measureSeparatorRef = useRef(null);
  const groupContainerRef = useRef(null);
  const [groupContainerWidth, setGroupContainerWidth] = useState(0);
  const [wrappedRowIndexes, setWrappedRowIndexes] = useState([]);
  const [groupedRows, setGroupedRows] = useState([]);

  itemRefs.current.length = previewEntries.length;
  measureTokenRefs.current.length = previewEntries.length;

  useEffect(() => {
    if (!groupIntoRows || !renderGroupedRowsAsText) {
      setGroupContainerWidth((current) => (current === 0 ? current : 0));
      return undefined;
    }

    let resizeObserver = null;

    const updateContainerWidth = () => {
      const containerElement = groupContainerRef.current;
      if (!(containerElement instanceof HTMLElement)) {
        return;
      }

      const nextWidth = Math.round(containerElement.getBoundingClientRect().width || containerElement.clientWidth || 0);
      setGroupContainerWidth((current) => (current === nextWidth ? current : nextWidth));
    };

    updateContainerWidth();

    const containerElement = groupContainerRef.current;
    if (typeof ResizeObserver !== "undefined" && containerElement instanceof HTMLElement) {
      resizeObserver = new ResizeObserver(updateContainerWidth);
      resizeObserver.observe(containerElement);
    } else {
      window.addEventListener("resize", updateContainerWidth);
    }

    return () => {
      if (resizeObserver) {
        resizeObserver.disconnect();
      } else {
        window.removeEventListener("resize", updateContainerWidth);
      }
    };
  }, [groupIntoRows, renderGroupedRowsAsText]);

  useLayoutEffect(() => {
    if (!groupIntoRows) {
      setGroupedRows((current) => (current.length === 0 ? current : []));
      return undefined;
    }

    if (previewEntries.length === 0) {
      setGroupedRows((current) => (current.length === 0 ? current : []));
      return undefined;
    }

    if (renderGroupedRowsAsText) {
      const nextRows = showSkipped
        ? [
          ...chunkPreviewEntries(previewEntries.filter((entry) => !entry?.skipped), groupContainerWidth, RULE_PREVIEW_TOKEN_WIDTH, RULE_PREVIEW_SEPARATOR_WIDTH),
          ...chunkPreviewEntries(previewEntries.filter((entry) => entry?.skipped), groupContainerWidth, RULE_PREVIEW_TOKEN_WIDTH, RULE_PREVIEW_SEPARATOR_WIDTH)
        ]
        : chunkPreviewEntries(previewEntries, groupContainerWidth, RULE_PREVIEW_TOKEN_WIDTH, RULE_PREVIEW_SEPARATOR_WIDTH);

      setGroupedRows((current) => (
        current.length === nextRows.length &&
        current.every((row, rowIndex) => (
          row.length === nextRows[rowIndex].length &&
          row.every((entry, entryIndex) => entry === nextRows[rowIndex][entryIndex])
        ))
          ? current
          : nextRows
      ));
      return undefined;
    }

    let frameHandle = 0;
    let resizeObserver = null;

    const updateGroupedRows = () => {
      frameHandle = 0;
      const containerElement = groupContainerRef.current;
      if (!(containerElement instanceof HTMLElement)) {
        return;
      }

      const availableWidth = containerElement.clientWidth;
      if (availableWidth <= 0) {
        setGroupedRows((current) => (
          current.length === 1 && current[0]?.length === previewEntries.length
            ? current
            : [previewEntries]
        ));
        return;
      }

      const separatorElement = measureSeparatorRef.current;
      const separatorWidth = separatorElement instanceof HTMLElement
        ? Math.ceil(separatorElement.getBoundingClientRect().width || separatorElement.offsetWidth || 14)
        : 14;
      const indexedEntries = previewEntries.map((entry, index) => ({
        entry,
        index
      }));
      const groupIndexedEntries = (entriesToGroup) => {
        const nextRows = [];
        let currentRow = [];
        let currentRowWidth = 0;

        entriesToGroup.forEach(({ entry, index }) => {
          const tokenElement = measureTokenRefs.current[index];
          const tokenWidth = tokenElement instanceof HTMLElement
            ? Math.ceil(tokenElement.getBoundingClientRect().width || tokenElement.offsetWidth || 0)
            : 0;
          const entryWidth = tokenWidth + (currentRow.length > 0 ? separatorWidth : 0);

          if (currentRow.length > 0 && currentRowWidth + entryWidth > availableWidth) {
            nextRows.push(currentRow);
            currentRow = [entry];
            currentRowWidth = tokenWidth;
            return;
          }

          currentRow.push(entry);
          currentRowWidth += currentRow.length === 1 ? tokenWidth : entryWidth;
        });

        if (currentRow.length > 0) {
          nextRows.push(currentRow);
        }

        return nextRows;
      };

      let nextRows = [];
      if (renderGroupedRowsAsText && showSkipped) {
        const keptEntries = indexedEntries.filter(({ entry }) => !entry?.skipped);
        const skippedEntries = indexedEntries.filter(({ entry }) => entry?.skipped);
        nextRows = [
          ...groupIndexedEntries(keptEntries),
          ...groupIndexedEntries(skippedEntries)
        ];
      } else {
        nextRows = groupIndexedEntries(indexedEntries);
      }

      setGroupedRows((current) => {
        if (
          current.length === nextRows.length &&
          current.every((row, rowIndex) => (
            row.length === nextRows[rowIndex].length &&
            row.every((entry, entryIndex) => entry === nextRows[rowIndex][entryIndex])
          ))
        ) {
          return current;
        }
        return nextRows;
      });
    };

    const scheduleUpdate = () => {
      if (frameHandle !== 0) {
        window.cancelAnimationFrame(frameHandle);
      }
      frameHandle = window.requestAnimationFrame(updateGroupedRows);
    };

    scheduleUpdate();

    const containerElement = groupContainerRef.current;
    if (typeof ResizeObserver !== "undefined" && containerElement instanceof HTMLElement) {
      resizeObserver = new ResizeObserver(scheduleUpdate);
      resizeObserver.observe(containerElement);
    } else {
      window.addEventListener("resize", scheduleUpdate);
    }

    return () => {
      if (frameHandle !== 0) {
        window.cancelAnimationFrame(frameHandle);
      }
      if (resizeObserver) {
        resizeObserver.disconnect();
      } else {
        window.removeEventListener("resize", scheduleUpdate);
      }
    };
  }, [groupContainerWidth, groupIntoRows, previewEntries, renderGroupedRowsAsText, showSkipped]);

  useLayoutEffect(() => {
    if (groupIntoRows || !trimWrappedSeparators || previewEntries.length <= 1) {
      setWrappedRowIndexes((current) => (current.length === 0 ? current : []));
      return undefined;
    }

    let frameHandle = 0;
    let resizeObserver = null;

    const updateWrappedRows = () => {
      frameHandle = 0;
      const nextRowIndexes = [];
      let currentRowIndex = 0;
      let previousTop = null;

      previewEntries.forEach((_, index) => {
        const itemElement = itemRefs.current[index];
        if (!(itemElement instanceof HTMLElement)) {
          return;
        }

        const currentTop = itemElement.offsetTop;
        if (index > 0 && previousTop !== null && Math.abs(currentTop - previousTop) > 1) {
          currentRowIndex += 1;
        }
        nextRowIndexes.push(currentRowIndex);
        previousTop = currentTop;
      });

      setWrappedRowIndexes((current) => (
        current.length === nextRowIndexes.length && current.every((value, index) => value === nextRowIndexes[index])
          ? current
          : nextRowIndexes
      ));
    };

    const scheduleUpdate = () => {
      if (frameHandle !== 0) {
        window.cancelAnimationFrame(frameHandle);
      }
      frameHandle = window.requestAnimationFrame(updateWrappedRows);
    };

    scheduleUpdate();

    const containerElement = itemRefs.current[0]?.parentElement;
    if (typeof ResizeObserver !== "undefined" && containerElement instanceof HTMLElement) {
      resizeObserver = new ResizeObserver(scheduleUpdate);
      resizeObserver.observe(containerElement);
    } else {
      window.addEventListener("resize", scheduleUpdate);
    }

    return () => {
      if (frameHandle !== 0) {
        window.cancelAnimationFrame(frameHandle);
      }
      if (resizeObserver) {
        resizeObserver.disconnect();
      } else {
        window.removeEventListener("resize", scheduleUpdate);
      }
    };
  }, [previewEntries, trimWrappedSeparators]);

  if (previewEntries.length === 0) {
    return <span className="dw-demo-preview-empty">--</span>;
  }

  if (groupIntoRows) {
    const rowsToRender = groupedRows.length > 0 ? groupedRows : [previewEntries];

    return (
      <>
        <span className="dw-demo-preview-measure" aria-hidden="true">
          <span ref={measureSeparatorRef} className="dw-demo-preview-separator">·</span>
          {previewEntries.map((entry, index) => (
            <span
              key={`measure-${entry.time}-${index}-${entry.skipped ? "skipped" : "kept"}`}
              ref={(element) => {
                measureTokenRefs.current[index] = element;
              }}
              className={`dw-demo-preview-token ${showSkipped && entry.skipped ? "is-skipped" : ""}`}
            >
              <span className={`dw-demo-preview-time ${showSkipped && entry.skipped ? "is-skipped" : ""}`}>{entry.time}</span>
            </span>
          ))}
        </span>
        <span ref={groupContainerRef} className="dw-demo-preview-grouped">
          {rowsToRender.map((rowEntries, rowIndex) => {
            if (renderGroupedRowsAsText) {
              const rowText = rowEntries.map((entry) => entry.time).join(" · ");
              const isSkippedRow = showSkipped && rowEntries.every((entry) => entry?.skipped);

              return (
                <span key={`row-${rowIndex}`} className={`dw-demo-preview-rule-row is-text ${isSkippedRow ? "is-skipped-row" : ""}`}>
                  <span className={`dw-demo-preview-rule-text-part ${isSkippedRow ? "is-skipped is-block" : ""}`}>{rowText}</span>
                </span>
              );
            }

            return (
              <span key={`row-${rowIndex}`} className="dw-demo-preview-rule-row">
                {rowEntries.map((entry, index) => (
                  <span key={`${entry.time}-${rowIndex}-${index}-${entry.skipped ? "skipped" : "kept"}`} className="dw-demo-preview-item">
                    {index > 0 ? <span className="dw-demo-preview-separator" aria-hidden="true">·</span> : null}
                    <span
                      className={`dw-demo-preview-token ${showSkipped && entry.skipped ? "is-skipped" : ""}`}
                      title={showSkipped && entry.skipped && entry.reason ? entry.reason : undefined}
                    >
                      <span className={`dw-demo-preview-time ${showSkipped && entry.skipped ? "is-skipped" : ""}`}>{entry.time}</span>
                    </span>
                  </span>
                ))}
              </span>
            );
          })}
        </span>
      </>
    );
  }

  return (
    <>
      {previewEntries.map((entry, index) => {
        const currentRowIndex = wrappedRowIndexes[index] || 0;
        const previousRowIndex = index > 0 ? (wrappedRowIndexes[index - 1] || 0) : 0;
        const isRowStart = index > 0 && currentRowIndex !== previousRowIndex;

        return (
        <span
          key={`${entry.time}-${index}-${entry.skipped ? "skipped" : "kept"}`}
          className={`dw-demo-preview-item ${trimWrappedSeparators && currentRowIndex > 0 ? "is-wrapped-row" : ""} ${trimWrappedSeparators && isRowStart ? "is-row-start" : ""}`}
          ref={trimWrappedSeparators ? (element) => {
            itemRefs.current[index] = element;
          } : undefined}
        >
          {index > 0 ? <span className="dw-demo-preview-separator" aria-hidden="true">·</span> : null}
          <span
            className={`dw-demo-preview-token ${showSkipped && entry.skipped ? "is-skipped" : ""}`}
            title={showSkipped && entry.skipped && entry.reason ? entry.reason : undefined}
          >
            <span className={`dw-demo-preview-time ${showSkipped && entry.skipped ? "is-skipped" : ""}`}>{entry.time}</span>
          </span>
        </span>
        );
      })}
    </>
  );
}

function SummaryTable({
  rows,
  onRemoveRow,
  summaryScrollRef,
  summaryFilter,
  onSummaryFilterChange,
  dropdownPortalHostRef
}) {
  const { t, script } = useNativeScheduleI18n();
  const isLatin = script === "latin";

  return (
    <>
      <div className="dw-demo-summary-head">
        <div className="is-time">{t("nativeSchedule.summary.head.time")}</div>
        <WorkbenchDropdown
          value=""
          title={t("nativeSchedule.summary.filter.title")}
          options={[
            { value: "all", label: t("nativeSchedule.summary.filter.all"), active: summaryFilter === "all" },
            { value: "current", label: t("nativeSchedule.summary.filter.current"), active: summaryFilter === "current" },
            { value: "local", label: t("nativeSchedule.summary.filter.local"), active: summaryFilter === "local" },
            { value: "express", label: t("nativeSchedule.summary.filter.express"), active: summaryFilter === "express" }
          ]}
          onSelect={onSummaryFilterChange}
          className="is-line dw-demo-summary-head-line"
          variant="filter"
          positioning="portal"
          triggerClassName={`dw-demo-summary-head-filter-trigger ${summaryFilter !== "all" ? "is-filtered" : ""}`}
          menuClassName="dw-demo-summary-head-filter-menu"
          portalHostRef={dropdownPortalHostRef}
          menuWidth={144}
          triggerContent={(
            <>
              <span className="dw-demo-summary-head-filter-label">{t("nativeSchedule.summary.filter.label")}</span>
              <span className="dw-demo-summary-head-filter-caret" aria-hidden="true">
                <svg viewBox="0 0 16 16" className="dw-demo-summary-head-filter-icon">
                  <path className="dw-demo-summary-head-filter-path" d="M4.2 6.2 8 10l3.8-3.8" fill="none" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </span>
            </>
          )}
        />
        <div className="is-origin">{t("nativeSchedule.summary.head.origin")}</div>
        <div className="is-status">{t("nativeSchedule.summary.head.status")}</div>
        <div className="is-action" aria-hidden="true" />
      </div>

      <WorkbenchScrollArea className="dw-demo-summary-scroll" metricsKey={rows.length} externalScrollRef={summaryScrollRef}>
        <div className="dw-demo-summary-table">
          {rows.length === 0 ? (
            <div className="dw-demo-empty-state">
              <div className="dw-demo-empty-icon-wrap" aria-hidden="true">
                <DemoEmptyScheduleIcon />
              </div>
              <div className="dw-demo-empty-title">{t("nativeSchedule.summary.empty.title")}</div>
              <div className="dw-demo-empty-text">{t("nativeSchedule.summary.empty.body")}</div>
              <div className="dw-demo-empty-text">{t("nativeSchedule.summary.empty.next")}</div>
            </div>
          ) : (
            rows.map((row) => (
              <div key={row.id} className={`dw-demo-summary-row ${row.isConflict ? "is-conflict" : ""} ${row.isExpress ? "is-express" : ""}`}>
                <div className="is-time">{row.time}</div>
                <div className="is-line">
                  <span
                    className={`dw-demo-dot ${row.isConflict ? "is-conflict" : ""}`}
                    style={row.isConflict ? undefined : { backgroundColor: row.lineColor || undefined }}
                  />
                  <div className="dw-demo-line-meta">
                    <div className="dw-demo-line-meta-top">
                      <span className="dw-demo-line-name">{row.lineName}</span>
                      <SummaryBadge kind={row.kind} />
                    </div>
                  </div>
                </div>
                <div className="is-origin dw-demo-origin-cell">
                  {row.origin}
                </div>
                <div className="is-status">
                  {row.isConflict && isLatin ? (
                    <div className="dw-demo-status-stack is-conflict">
                      <span className="dw-demo-status-text is-conflict">
                        {t("nativeSchedule.summary.status.conflictTitle")}
                      </span>
                      <span className="dw-demo-status-subtext is-conflict">
                        {row.conflictReasonLabel || t("nativeSchedule.summary.status.conflictUnknown")}
                      </span>
                    </div>
                  ) : (
                    <span className={`dw-demo-status-text ${row.isConflict ? "is-conflict" : row.isApplied ? "is-applied" : "is-pending"}`}>
                      {row.isConflict
                        ? t("nativeSchedule.summary.status.conflict.compact", {
                          reason: row.conflictReasonLabel || t("nativeSchedule.summary.status.conflictUnknown")
                        })
                        : row.isApplied
                          ? t("nativeSchedule.summary.status.applied")
                          : t("nativeSchedule.summary.status.pending")}
                    </span>
                  )}
                </div>
                <div className="is-action">
                  <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => onRemoveRow(row.id)}>{t("nativeSchedule.summary.action.remove")}</button>
                </div>
              </div>
            ))
          )}
        </div>
      </WorkbenchScrollArea>
    </>
  );
}

function SummarySection({
  summaryStateLabel,
  hasAppliedSchedule,
  summaryRows,
  earliestStart,
  conflictCount,
  summaryFilter,
  onSummaryFilterChange,
  summaryScrollRef,
  onRemoveRow,
  onClearSummary,
  onApplySchedule,
  onLocateConflict,
  dropdownPortalHostRef
}) {
  const { t } = useNativeScheduleI18n();
  const hasConflicts = conflictCount > 0;

  return (
    <section className="dw-demo-left">
      <DemoSectionHeader
        title={summaryStateLabel}
        applied={hasAppliedSchedule}
        metrics={(
          <div className="dw-demo-summary-metrics-group">
            <span>{t("nativeSchedule.summary.metric.total", { count: summaryRows.length })}</span>
            <span>{t("nativeSchedule.summary.metric.earliest", { time: earliestStart })}</span>
            <span>{t("nativeSchedule.summary.metric.conflict", { count: conflictCount })}</span>
          </div>
        )}
      />

      <SummaryTable
        rows={summaryRows}
        summaryScrollRef={summaryScrollRef}
        summaryFilter={summaryFilter}
        onSummaryFilterChange={onSummaryFilterChange}
        onRemoveRow={onRemoveRow}
        dropdownPortalHostRef={dropdownPortalHostRef}
      />

      <div className="dw-demo-footer">
        <button type="button" className="dw-demo-flat-button is-muted" onClick={onClearSummary}>{t("nativeSchedule.summary.action.clear")}</button>
        <button
          type="button"
          className={`dw-demo-primary dw-demo-cta ${hasConflicts ? "is-conflict" : hasAppliedSchedule ? "is-applied" : ""}`}
          onClick={hasConflicts ? onLocateConflict : onApplySchedule}
        >
          <span className="dw-demo-button-content">
            <span className="dw-demo-button-icon-wrap" aria-hidden="true">
              {hasConflicts ? <DemoAlertIcon /> : hasAppliedSchedule ? <DemoAppliedStateIcon /> : <DemoPlayIcon />}
            </span>
            <span>{hasConflicts ? t("nativeSchedule.summary.action.locateConflict") : hasAppliedSchedule ? t("nativeSchedule.summary.action.applied") : t("nativeSchedule.summary.action.apply")}</span>
          </span>
        </button>
      </div>
    </section>
  );
}

function AutoRuleEditor({
  editorStart,
  editorEnd,
  autoFrequencyText,
  autoFrequencyPerHour,
  showOffsetField,
  autoOffsetDirection,
  autoOffsetMinutesText,
  liveAutoPreview,
  editorEndInputRef,
  frequencyInputRef,
  onEditorStartChange,
  onEditorEndChange,
  onAutoFrequencyChange,
  onAutoOffsetDirectionChange,
  onAutoOffsetMinutesChange,
  onAddAutoRule
}) {
  const { t, script } = useNativeScheduleI18n();
  const topPreviewMaxItemsPerRow = getTopPreviewMaxItemsPerRow(script, Boolean(liveAutoPreview.meta));

  return (
    <div className="dw-demo-rule-editor">
      <div className="dw-demo-rule-editor-row">
        <DemoTextField
          label={t("nativeSchedule.auto.field.start")}
          value={editorStart}
          onCommit={onEditorStartChange}
          onDraftChange={onEditorStartChange}
          className="is-window-start"
          timeMode
          preserveInvalidTime
          nextInputRef={editorEndInputRef}
        />
        <DemoTextField
          label={t("nativeSchedule.auto.field.end")}
          value={editorEnd}
          onCommit={onEditorEndChange}
          onDraftChange={onEditorEndChange}
          className="is-window-end"
          timeMode
          preserveInvalidTime
          inputRef={editorEndInputRef}
          nextInputRef={frequencyInputRef}
        />
        <DemoTextField
          label={t("nativeSchedule.auto.field.rate")}
          value={autoFrequencyText}
          onCommit={onAutoFrequencyChange}
          onDraftChange={onAutoFrequencyChange}
          className="is-rate"
          inputRef={frequencyInputRef}
        />
        {showOffsetField ? (
          <DemoOffsetField
            label={t("nativeSchedule.auto.field.offset")}
            direction={autoOffsetDirection}
            minutes={autoOffsetMinutesText}
            onDirectionChange={onAutoOffsetDirectionChange}
            onMinutesChange={onAutoOffsetMinutesChange}
            className="is-offset"
            hint={t("nativeSchedule.auto.field.offsetHint")}
          />
        ) : null}
      </div>

      <div className="dw-demo-preview-panel">
        <div className="dw-demo-preview-content">
          <div className="dw-demo-preview-inline is-window">
            <span className="dw-demo-preview-tag">{t("nativeSchedule.auto.preview.tag")}</span>
            <span className="dw-demo-preview-values-slot">
              <span className="dw-demo-preview-values"><DemoTopPreviewTimes times={liveAutoPreview.times} maxItemsPerRow={topPreviewMaxItemsPerRow} /></span>
            </span>
            {liveAutoPreview.meta ? <span className="dw-demo-preview-meta is-inline">{liveAutoPreview.meta}</span> : null}
          </div>
        </div>
        <div className="dw-demo-preview-spacer is-rate" />
        <div className="dw-demo-preview-spacer is-offset" />
        <button type="button" className="dw-demo-flat-button is-theme" onClick={onAddAutoRule}>{t("nativeSchedule.auto.button.add")}</button>
      </div>
    </div>
  );
}

function AutoRuleTable({
  autoRules,
  showOffsetColumn,
  onRemoveAutoRule
}) {
  const { t, script } = useNativeScheduleI18n();
  const rulePreviewMaxItemsPerRow = getRulePreviewMaxItemsPerRow(script, showOffsetColumn);

  return (
    <>
      <div className={`dw-demo-rule-list-head ${showOffsetColumn ? "is-with-offset" : "is-no-offset"}`}>
        <div className="is-window">{t("nativeSchedule.auto.table.window")}</div>
        <div className="is-rate">{t("nativeSchedule.auto.table.rate")}</div>
        {showOffsetColumn ? <div className="is-offset">{t("nativeSchedule.auto.table.offset")}</div> : null}
        <div className="is-action">{t("nativeSchedule.auto.table.action")}</div>
      </div>

      <WorkbenchScrollArea className="dw-demo-rule-list-scroll" metricsKey={autoRules.length}>
        {autoRules.map((rule) => (
          <div key={rule.id} className="dw-demo-rule-row">
            <div className={`dw-demo-rule-row-main ${showOffsetColumn ? "is-with-offset" : "is-no-offset"}`}>
              <div className="is-window">
                <span className="dw-demo-rule-window">{rule.windowLabel}</span>
              </div>
              <div className="is-rate">
                <span className="dw-demo-rule-rate">{rule.rateLabel}</span>
              </div>
              {showOffsetColumn ? (
                <div className="is-offset">
                  <span className="dw-demo-rule-offset">{rule.offsetLabel}</span>
                </div>
              ) : null}
              <div className="is-action">
                <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => onRemoveAutoRule(rule.id)}>{t("nativeSchedule.summary.action.remove")}</button>
              </div>
            </div>
            <div className="dw-demo-rule-preview">
              <span className="dw-demo-preview-values is-rule"><DemoRuleTextPreviewTimes entries={rule.previewEntries} showSkipped moveSkippedToEnd maxItemsPerRow={rulePreviewMaxItemsPerRow} /></span>
              {rule.previewMeta ? <span className="dw-demo-rule-preview-meta">{rule.previewMeta}</span> : null}
            </div>
          </div>
        ))}
      </WorkbenchScrollArea>
    </>
  );
}

function ManualDraftSection({
  manualInput,
  manualInputRef,
  manualDrafts,
  manualInputError,
  isAddManualDisabled,
  footerNote,
  onManualInputChange,
  onAddManualDraft,
  onRemoveManualDraft,
  onImportManualToSummary
}) {
  const { t } = useNativeScheduleI18n();

  return (
    <div className="dw-demo-right-body">
      <div className="dw-demo-rule-editor">
        <div className="dw-demo-rule-editor-row is-manual">
          <DemoTextField
            label={t("nativeSchedule.manual.field.departure")}
            value={manualInput}
            onCommit={onManualInputChange}
            onDraftChange={onManualInputChange}
            className="is-manual-time"
            inputRef={manualInputRef}
            timeMode
            errorText={manualInputError}
            preserveInvalidTime
            reserveErrorSpace
          />
          <button type="button" className="dw-demo-flat-button is-theme is-manual-add" onClick={onAddManualDraft} disabled={isAddManualDisabled}>{t("nativeSchedule.manual.button.add")}</button>
        </div>
      </div>

      <WorkbenchScrollArea className="dw-demo-rule-list-scroll" metricsKey={manualDrafts.length}>
        {manualDrafts.map((draft) => (
          <div key={draft.id} className="dw-demo-manual-row">
            <div className="dw-demo-manual-meta">
              <span className="dw-demo-manual-time">{draft.time}</span>
              {draft.validation?.status !== "ok" ? (
                <span className={`dw-demo-manual-validation is-${draft.validation.status}`}>{draft.validation.message}</span>
              ) : null}
            </div>
            <button type="button" className="dw-demo-link-danger dw-demo-row-action" onClick={() => onRemoveManualDraft(draft.id)}>{t("nativeSchedule.summary.action.remove")}</button>
          </div>
        ))}
      </WorkbenchScrollArea>

      <div className="dw-demo-footer">
        {footerNote ? <span className={`dw-demo-footer-note ${footerNote.tone ? `is-${footerNote.tone}` : ""}`}>{footerNote.text}</span> : <span />}
        <button type="button" className="dw-demo-primary dw-demo-cta is-secondary" onClick={onImportManualToSummary}>
          <span className="dw-demo-button-content">
            <span className="dw-demo-button-icon-wrap" aria-hidden="true">
              <DemoImportLeftIcon />
            </span>
            <span>{t("nativeSchedule.manual.button.import")}</span>
          </span>
        </button>
      </div>
    </div>
  );
}

function AutoRuleSection({
  editorStart,
  editorEnd,
  autoFrequencyText,
  autoFrequencyPerHour,
  selectedLineType,
  autoOffsetDirection,
  autoOffsetMinutesText,
  liveAutoPreview,
  autoRules,
  footerNote,
  editorEndInputRef,
  frequencyInputRef,
  onEditorStartChange,
  onEditorEndChange,
  onAutoFrequencyChange,
  onAutoOffsetDirectionChange,
  onAutoOffsetMinutesChange,
  onAddAutoRule,
  onRemoveAutoRule,
  onImportAutoToSummary
}) {
  const { t } = useNativeScheduleI18n();

  return (
    <div className="dw-demo-right-body">
      <AutoRuleEditor
        editorStart={editorStart}
        editorEnd={editorEnd}
        autoFrequencyText={autoFrequencyText}
        autoFrequencyPerHour={autoFrequencyPerHour}
        showOffsetField={selectedLineType === "express"}
        autoOffsetDirection={autoOffsetDirection}
        autoOffsetMinutesText={autoOffsetMinutesText}
        liveAutoPreview={liveAutoPreview}
        editorEndInputRef={editorEndInputRef}
        frequencyInputRef={frequencyInputRef}
        onEditorStartChange={onEditorStartChange}
        onEditorEndChange={onEditorEndChange}
        onAutoFrequencyChange={onAutoFrequencyChange}
        onAutoOffsetDirectionChange={onAutoOffsetDirectionChange}
        onAutoOffsetMinutesChange={onAutoOffsetMinutesChange}
        onAddAutoRule={onAddAutoRule}
      />

      <AutoRuleTable
        autoRules={autoRules}
        showOffsetColumn={selectedLineType === "express"}
        onRemoveAutoRule={onRemoveAutoRule}
      />

      <div className="dw-demo-footer">
        {footerNote ? <span className={`dw-demo-footer-note ${footerNote.tone ? `is-${footerNote.tone}` : ""}`}>{footerNote.text}</span> : <span />}
        <button type="button" className="dw-demo-primary dw-demo-cta is-secondary" onClick={onImportAutoToSummary}>
          <span className="dw-demo-button-content">
            <span className="dw-demo-button-icon-wrap" aria-hidden="true">
              <DemoImportLeftIcon />
            </span>
            <span>{t("nativeSchedule.auto.button.import")}</span>
          </span>
        </button>
      </div>
    </div>
  );
}

function NativeScheduleDemoPage({ registerHostActions }) {
  const { t } = useNativeScheduleI18n();
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const [activeRightTab, setActiveRightTab] = useState("auto");
  const [catalogRevision, setCatalogRevision] = useState(0);
  const dropdownPortalHostRef = useRef(null);
  const [selectedLineId, setSelectedLineId] = useState(LINE_OPTIONS[0]?.id || "");
  const [selectedLineType, setSelectedLineType] = useState(LINE_OPTIONS[0]?.kind || "local");
  const [selectedDepot, setSelectedDepot] = useState(LINE_OPTIONS[0]?.depotId || "");
  const [origin, setOrigin] = useState(LINE_OPTIONS[0]?.originId || "");
  const [holdMinutes, setHoldMinutes] = useState(LINE_OPTIONS[0]?.hold || "");
  const [dwellMinutes, setDwellMinutes] = useState(LINE_OPTIONS[0]?.dwell || "");
  const holdMinutesValue = Number(holdMinutes);
  const dwellMinutesValue = Number(dwellMinutes);
  const holdMinutesTooSmall =
    holdMinutes !== "" && Number.isFinite(holdMinutesValue) && holdMinutesValue < MIN_LINE_SETTING_MINUTES;
  const dwellMinutesTooSmall =
    dwellMinutes !== "" && Number.isFinite(dwellMinutesValue) && dwellMinutesValue < MIN_LINE_SETTING_MINUTES;
  const [summaryEntries, setSummaryEntries] = useState(() => normalizeSummaryEntries([], t));
  const [autoRules, setAutoRules] = useState([]);
  const [manualDrafts, setManualDrafts] = useState([]);
  const [manualInput, setManualInput] = useState("12:00");
  const [editorStart, setEditorStart] = useState("08:00");
  const [editorEnd, setEditorEnd] = useState("10:00");
  const [autoFrequencyText, setAutoFrequencyText] = useState("4");
  const [autoOffsetDirection, setAutoOffsetDirection] = useState("");
  const [autoOffsetMinutesText, setAutoOffsetMinutesText] = useState("");
  const [appliedSummarySignature, setAppliedSummarySignature] = useState("");
  const [appliedSummaryRowKeys, setAppliedSummaryRowKeys] = useState([]);
  const [summaryFilter, setSummaryFilter] = useState("all");
  const [panelMessage, setPanelMessage] = useState(null);
  const summaryScrollRef = useRef(null);
  const manualInputRef = useRef(null);
  const editorEndInputRef = useRef(null);
  const frequencyInputRef = useRef(null);
  const hasHydratedRuntimeRef = useRef(false);
  const lastHydratedSnapshotRef = useRef(null);
  const suppressNextSnapshotRef = useRef(false);
  const skipNextBackendSaveRef = useRef(false);

  const planLineOptions = useMemo(
    () => buildPlanLineOptions(LINE_OPTIONS),
    [catalogRevision]
  );

  const selectedLine = useMemo(
    () => LINE_OPTIONS.find((line) => line?.id === selectedLineId) ?? LINE_OPTIONS[0] ?? DEFAULT_LINE_OPTIONS[0],
    [catalogRevision, selectedLineId]
  );
  const availableDepots = useMemo(() => {
    if (!selectedLine?.transportType) {
      return DEPOT_OPTIONS;
    }

    return DEPOT_OPTIONS.filter((depot) => !depot?.transportType || depot.transportType === selectedLine.transportType);
  }, [catalogRevision, selectedLine]);
  const autoFrequencyPerHour = useMemo(
    () => parseFrequencyValue(autoFrequencyText),
    [autoFrequencyText]
  );
  const autoOffsetMinutes = useMemo(
    () => resolveOffsetMinutes(autoOffsetDirection, autoOffsetMinutesText),
    [autoOffsetDirection, autoOffsetMinutesText]
  );
  const currentKind = useMemo(() => normalizeKind(selectedLineType), [selectedLineType]);
  const normalizedManualInput = useMemo(
    () => normalizeTimeInput(String(manualInput || "").trim()),
    [manualInput]
  );
  const manualInputError = useMemo(() => {
    if (!normalizedManualInput) {
      return "";
    }

    if (normalizedManualInput.length < 5) {
      return "";
    }

    return isValidTimeValue(normalizedManualInput) ? "" : t("nativeSchedule.manual.inputError");
  }, [normalizedManualInput, t]);
  const isAddManualDisabled = !!manualInputError || !isValidTimeValue(normalizedManualInput);
  const currentManualDrafts = useMemo(
    () => sortManualDraftRows(manualDrafts.filter((draft) => draft?.serviceId === selectedLine.id)),
    [manualDrafts, selectedLine.id]
  );
  const validatedManualDrafts = useMemo(
    () => validateManualRows(currentManualDrafts, t),
    [currentManualDrafts, t]
  );
  const currentAutoRules = useMemo(
    () => sortAutoRuleRows(autoRules.filter((rule) => rule?.serviceId === selectedLine.id)),
    [autoRules, selectedLine.id]
  );
  const currentAutoPlan = useMemo(() => {
    if (currentAutoRules.length === 0) {
      return {
        retainedRows: summaryEntries,
        plannedRows: [],
        skippedCount: 0,
        previewsByRule: {},
        hasKindConflict: false
      };
    }

    return buildAutoStagedPlan({
      currentRows: summaryEntries,
      rowsForLine: currentAutoRules,
      selectedEditLine: selectedLine.id,
      referenceLineIds: getReferenceLineIdsForLine(selectedLine, currentKind),
      lineOptions: planLineOptions,
      replaceExistingAutoRows: false
    });
  }, [currentAutoRules, currentKind, planLineOptions, selectedLine, summaryEntries]);
  const renderedAutoRules = useMemo(
    () => currentAutoRules.map((rule) => {
      const preview = currentAutoPlan.previewsByRule[rule.id] || { times: [], entries: [], skippedCount: 0, skipReasons: [], reason: "" };
      return {
        ...rule,
        windowLabel: `${rule.start} - ${rule.end}`,
        rateLabel: t("nativeSchedule.preview.rateLabel.compact", { count: rule.departuresPerHour }),
        offsetLabel: rule.kind === "express" ? formatOffsetLabel(directionFromOffsetMode(rule.expressOffsetMode), String(rule.expressOffsetMinutes || ""), t, "compact") : t("nativeSchedule.offset.none.compact"),
        previewTimes: preview.times,
        previewEntries: preview.entries,
        previewMeta: buildPreviewMetaText(preview, currentAutoPlan.hasKindConflict, t, { detailedSkipReason: true })
      };
    }),
    [currentAutoPlan.hasKindConflict, currentAutoPlan.previewsByRule, currentAutoRules, t]
  );
  const liveAutoPreview = useMemo(() => {
    const previewRule = {
      id: "editor-preview",
      lineId: selectedLine.id,
      serviceId: selectedLine.id,
      kind: currentKind,
      enabled: true,
      start: editorStart,
      end: editorEnd,
      departuresPerHour: autoFrequencyPerHour,
      expressOffsetMode: offsetModeFromDirection(autoOffsetDirection),
      expressOffsetMinutes: currentKind === "express" ? Math.abs(autoOffsetMinutes) : 0
    };
    const plan = buildAutoStagedPlan({
      currentRows: summaryEntries,
      rowsForLine: [previewRule],
      selectedEditLine: selectedLine.id,
      referenceLineIds: getReferenceLineIdsForLine(selectedLine, currentKind),
      lineOptions: planLineOptions,
      replaceExistingAutoRows: false
    });
    const preview = plan.previewsByRule[previewRule.id] || { times: [], entries: [], skippedCount: 0, skipReasons: [], reason: "" };
    return {
      times: preview.times,
      entries: preview.entries,
      meta: buildPreviewMetaText(preview, plan.hasKindConflict, t)
    };
  }, [
    autoFrequencyPerHour,
    autoOffsetDirection,
    autoOffsetMinutes,
    currentKind,
    editorEnd,
    editorStart,
    selectedLine.id,
    selectedLine,
    summaryEntries,
    planLineOptions,
    t
  ]);
  const currentSummarySignature = useMemo(
    () => getSummaryRowsSignature(summaryEntries),
    [summaryEntries]
  );
  const appliedSummaryRowKeySet = useMemo(
    () => new Set(Array.isArray(appliedSummaryRowKeys) ? appliedSummaryRowKeys : []),
    [appliedSummaryRowKeys]
  );
  const hasAppliedSchedule = summaryEntries.length > 0 && currentSummarySignature === appliedSummarySignature;
  const summaryRows = useMemo(
    () => buildSummaryRowsWithConflicts(summaryEntries, t, appliedSummaryRowKeySet),
    [appliedSummaryRowKeySet, summaryEntries, t]
  );
  const visibleSummaryRows = useMemo(() => {
    if (summaryFilter === "current") {
      return summaryRows.filter((row) => row.serviceId === selectedLine.id);
    }

    if (summaryFilter === "local") {
      return summaryRows.filter((row) => row.kind === "local");
    }

    if (summaryFilter === "express") {
      return summaryRows.filter((row) => row.kind === "express");
    }

    return summaryRows;
  }, [selectedLine.id, summaryFilter, summaryRows]);
  const conflictCount = visibleSummaryRows.filter((row) => row.isConflict).length;
  const earliestStart = visibleSummaryRows[0]?.time || "--:--";
  const summaryStateLabel = hasAppliedSchedule ? t("nativeSchedule.summary.section.applied") : t("nativeSchedule.summary.section.pending");
  const autoFooterNote =
    panelMessage?.scope === "auto"
      ? panelMessage
      : currentAutoPlan.hasKindConflict
        ? { scope: "auto", tone: "error", text: t("nativeSchedule.message.auto.kindConflict") }
        : null;
  const manualFooterNote = panelMessage?.scope === "manual" ? panelMessage : null;

  function bumpCatalogRevision() {
    setCatalogRevision((current) => current + 1);
  }

  function serializePersistedLineSettings() {
    return LINE_OPTIONS
      .filter((line) => line && typeof line === "object")
      .map((line) => ({
        id: line.id || "",
        depotId: line.depotId || "",
        hold: line.hold || "",
        dwell: line.dwell || ""
      }));
  }

  function updateRuntimeLineOption(lineId, updates) {
    const nextIndex = LINE_OPTIONS.findIndex((line) => line?.id === lineId);
    if (nextIndex < 0) {
      return;
    }

    LINE_OPTIONS[nextIndex] = {
      ...LINE_OPTIONS[nextIndex],
      ...updates
    };
    bumpCatalogRevision();
  }

  function applyHydratedState(snapshot, metadataSnapshot = null) {
    lastHydratedSnapshotRef.current = snapshot ?? null;
    skipNextBackendSaveRef.current = true;
    const persistedState = readPersistedNativeScheduleState();
    const runtimeCatalog = buildRuntimeCatalog(
      snapshot,
      metadataSnapshot,
      null,
      t
    );
    replaceRuntimeCatalog({
      lines: runtimeCatalog.lineOptions,
      depots: runtimeCatalog.depotOptions,
      origins: runtimeCatalog.originOptions
    });
    bumpCatalogRevision();
    const sourceLineId =
      (snapshot?.selectedEditLine && runtimeCatalog.lineOptions.some((line) => line?.id === snapshot.selectedEditLine)
        ? snapshot.selectedEditLine
        : "") ||
      (snapshot?.selectedLineId && runtimeCatalog.lineOptions.some((line) => line?.id === snapshot.selectedLineId)
        ? snapshot.selectedLineId
        : "") ||
      runtimeCatalog.lineOptions[0]?.id ||
      DEFAULT_LINE_OPTIONS[0].id;
    const sourceLine =
      runtimeCatalog.lineOptions.find((line) => line?.id === sourceLineId) ??
      runtimeCatalog.lineOptions[0] ??
      DEFAULT_LINE_OPTIONS[0];

    const nextManualDrafts = mapSnapshotManualRows(snapshot?.manualRows, sourceLine.id);
    const nextAutoRules = mapSnapshotAutoRules(snapshot?.autoRules, sourceLine.id);
    const nextSummaryEntries = normalizeSummaryEntries(mapSnapshotSummaryRows(snapshot?.stagedRows), t);
    const nextSummarySignature = getSummaryRowsSignature(nextSummaryEntries);
    const currentSummaryRowKeys = nextSummaryEntries.map((row) => getSummaryRowKey(row));
    const previousAppliedRowKeySet = new Set(
      Array.isArray(appliedSummaryRowKeys) ? appliedSummaryRowKeys : []
    );
    const nextAppliedSummarySignature =
      snapshot?.rulesApplied || snapshot?.draftApplied
        ? nextSummarySignature
        : (appliedSummarySignature || "");
    const nextAppliedSummaryRowKeys =
      snapshot?.rulesApplied || snapshot?.draftApplied
        ? currentSummaryRowKeys
        : currentSummaryRowKeys.filter((rowKey) => previousAppliedRowKeySet.has(rowKey));

    setActiveRightTab((current) => (current === "manual" ? "manual" : "auto"));
    setSelectedLineId(sourceLine.id);
    setSelectedLineType(sourceLine.kind);
    setSelectedDepot(sourceLine.depotId);
    setOrigin(sourceLine.originId);
    setHoldMinutes(sourceLine.hold);
    setDwellMinutes(sourceLine.dwell);
    setSummaryEntries(nextSummaryEntries);
    setAutoRules(nextAutoRules);
    setManualDrafts(nextManualDrafts);
    setManualInput(typeof persistedState?.manualInput === "string" ? persistedState.manualInput : "12:00");
    setEditorStart(typeof persistedState?.editorStart === "string" ? persistedState.editorStart : "08:00");
    setEditorEnd(typeof persistedState?.editorEnd === "string" ? persistedState.editorEnd : "10:00");
    setAutoFrequencyText(typeof persistedState?.autoFrequencyText === "string" ? persistedState.autoFrequencyText : "4");
    setAutoOffsetDirection(typeof persistedState?.autoOffsetDirection === "string" ? persistedState.autoOffsetDirection : "");
    setAutoOffsetMinutesText(typeof persistedState?.autoOffsetMinutesText === "string" ? persistedState.autoOffsetMinutesText : "");
    setAppliedSummarySignature(nextAppliedSummarySignature);
    setAppliedSummaryRowKeys(nextAppliedSummaryRowKeys);
    setSummaryFilter(
      persistedState?.summaryFilter === "current" || persistedState?.summaryFilter === "local" || persistedState?.summaryFilter === "express"
        ? persistedState.summaryFilter
        : "all"
    );
    setPanelMessage(null);
    hasHydratedRuntimeRef.current = true;
  }

  useEffect(() => {
    let disposed = false;

    async function hydrateFromBackend({ forceRefresh = false } = {}) {
      try {
        const snapshot = forceRefresh
          ? await workbenchApi.refreshSnapshot?.()
          : await workbenchApi.loadSnapshot?.();
        let metadata = null;

        try {
          metadata = await workbenchApi.refreshMetadata?.();
        } catch {}

        if (!disposed) {
          applyHydratedState(snapshot, metadata);
        }
      } catch (error) {
        if (!disposed) {
          console.error("[RT Native Schedule] backend hydrate failed", error);
        }
      }
    }

    hydrateFromBackend();
    const unsubscribe = workbenchApi.onSnapshotChanged?.((snapshot) => {
      if (suppressNextSnapshotRef.current) {
        suppressNextSnapshotRef.current = false;
        return;
      }

      if (!disposed) {
        applyHydratedState(snapshot, null);
      }
    });

    return () => {
      disposed = true;
      unsubscribe?.();
    };
  }, [t, workbenchApi]);

  useEffect(() => {
    if (typeof registerHostActions !== "function") {
      return undefined;
    }

    registerHostActions({
      refreshData: async () => {
        const snapshot = await workbenchApi.refreshSnapshot?.();
        let metadata = null;

        try {
          metadata = await workbenchApi.refreshMetadata?.();
        } catch {}

        applyHydratedState(snapshot, metadata);
      }
    });

    return () => {
      registerHostActions(null);
    };
  }, [registerHostActions, t, workbenchApi]);

  useEffect(() => {
    if (!hasHydratedRuntimeRef.current) {
      return;
    }

    writePersistedNativeScheduleState({
      manualInput,
      editorStart,
      editorEnd,
      autoFrequencyText,
      autoOffsetDirection,
      autoOffsetMinutesText,
      summaryFilter
    });
  }, [
    autoFrequencyText,
    autoOffsetDirection,
    autoOffsetMinutesText,
    editorEnd,
    editorStart,
    manualInput,
    summaryFilter
  ]);

  async function saveNativeWorkbenchDraft({ applyDraft = false } = {}) {
    const request = {
      selectedLineId,
      selectedEditLine: selectedLineId,
      mergedView: createNativeMergedViewForSave(selectedLineId, lastHydratedSnapshotRef.current?.mergedView),
      manualRows: serializeNativeManualRows(manualDrafts),
      autoRules: serializeNativeAutoRules(autoRules),
      stagedRows: serializeNativeStagedRows(summaryEntries),
      lineSettings: serializeNativeLineSettings(LINE_OPTIONS),
      applyDraft,
      nativeScheduleWriter: true
    };

    suppressNextSnapshotRef.current = true;
    try {
      const result = await workbenchApi.saveNativeDraft?.(request);
      if (result?.snapshot) {
        applyHydratedState(result.snapshot, null);
      } else {
        suppressNextSnapshotRef.current = false;
      }

      return result;
    } catch (error) {
      suppressNextSnapshotRef.current = false;
      throw error;
    }
  }

  useEffect(() => {
    if (!hasHydratedRuntimeRef.current) {
      return undefined;
    }

    if (skipNextBackendSaveRef.current) {
      skipNextBackendSaveRef.current = false;
      return undefined;
    }

    const timeoutId = window.setTimeout(async () => {
      try {
        await saveNativeWorkbenchDraft({ applyDraft: false });
      } catch {}
    }, 400);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [
    autoRules,
    catalogRevision,
    manualDrafts,
    selectedLineId,
    summaryEntries,
    t,
    workbenchApi
  ]);

  function clearPanelMessage() {
    setPanelMessage(null);
  }

  function markLocalDataDirty() {
    clearPanelMessage();
  }

  function applySelectedLine(nextLine) {
    if (!nextLine) {
      return;
    }

    setSelectedLineId(nextLine.id);
    setSelectedLineType(nextLine.kind);
    setSelectedDepot(nextLine.depotId);
    setOrigin(nextLine.originId);
    setHoldMinutes(nextLine.hold);
    setDwellMinutes(nextLine.dwell);
  }

  function handleSelectLine(lineId) {
    const nextLine = LINE_OPTIONS.find((line) => line.id === lineId);
    if (!nextLine) {
      return;
    }

    clearPanelMessage();
    applySelectedLine(nextLine);
  }

  function handleLineTypeSelect(nextType) {
    if (nextType !== "local" && nextType !== "express") {
      return;
    }

    if (selectedLine.kind === nextType) {
      clearPanelMessage();
      return;
    }

    markLocalDataDirty();
    updateRuntimeLineOption(selectedLine.id, { kind: nextType });
    setSelectedLineType(nextType);
    setManualDrafts((current) => sortManualDraftRows(
      current.map((draft) => (
        draft?.lineId === selectedLine.id
          ? { ...draft, kind: nextType }
          : draft
      ))
    ));
    setAutoRules((current) => sortAutoRuleRows(
      current.map((rule) => (
        rule?.lineId === selectedLine.id
          ? { ...rule, kind: nextType }
          : rule
      ))
    ));
    setSummaryEntries((current) => normalizeSummaryEntries(
      current.map((row) => (
        row?.lineId === selectedLine.id || row?.serviceId === selectedLine.id
          ? { ...row, kind: nextType }
          : row
      )),
      t
    ));
  }

  function handleDepotChange(value) {
    markLocalDataDirty();
    setSelectedDepot(value);
    updateRuntimeLineOption(selectedLine.id, { depotId: value });
  }

  function handleHoldMinutesChange(value) {
    const numeric = Number(value);
    if (value !== "" && Number.isFinite(numeric) && numeric < MIN_LINE_SETTING_MINUTES) {
      setHoldMinutes(value);
      return;
    }
    markLocalDataDirty();
    setHoldMinutes(value);
    updateRuntimeLineOption(selectedLine.id, { hold: value });
  }

  function handleDwellMinutesChange(value) {
    const numeric = Number(value);
    if (value !== "" && Number.isFinite(numeric) && numeric < MIN_LINE_SETTING_MINUTES) {
      setDwellMinutes(value);
      return;
    }
    markLocalDataDirty();
    setDwellMinutes(value);
    updateRuntimeLineOption(selectedLine.id, { dwell: value });
  }

  function handleEditorStartChange(value) {
    clearPanelMessage();
    if (!value || (value.length === 5 && isValidTimeValue(value))) {
      setEditorStart(value);
    }
  }

  function handleEditorEndChange(value) {
    clearPanelMessage();
    if (!value || (value.length === 5 && isValidTimeValue(value))) {
      setEditorEnd(value);
    }
  }

  function handleAutoFrequencyChange(value) {
    clearPanelMessage();
    setAutoFrequencyText(normalizeFrequencyInput(value));
  }

  function handleManualInputChange(value) {
    clearPanelMessage();
    setManualInput(normalizeTimeInput(value));
  }

  function handleAutoOffsetDirectionChange(nextDirection) {
    clearPanelMessage();
    setAutoOffsetDirection(nextDirection);
  }

  function handleAutoOffsetMinutesChange(nextValue) {
    clearPanelMessage();
    setAutoOffsetMinutesText(nextValue);
  }

  function addAutoRule() {
    if (!isValidTimeValue(editorStart) || !isValidTimeValue(editorEnd)) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.auto.invalidWindow") });
      return;
    }

    if ((!Array.isArray(liveAutoPreview.entries) || liveAutoPreview.entries.length === 0) && liveAutoPreview.meta) {
      setPanelMessage({ scope: "auto", tone: "warning", text: liveAutoPreview.meta });
      return;
    }

    markLocalDataDirty();
    setAutoRules((current) => sortAutoRuleRows([
      ...current,
      {
        id: Date.now(),
        lineId: selectedLine.id,
        serviceId: selectedLine.id,
        kind: currentKind,
        enabled: true,
        start: editorStart,
        end: editorEnd,
        departuresPerHour: autoFrequencyPerHour,
        expressOffsetMode: offsetModeFromDirection(autoOffsetDirection),
        expressOffsetMinutes: currentKind === "express" ? Math.abs(autoOffsetMinutes) : 0
      }
    ]));
  }

  function removeAutoRule(ruleId) {
    markLocalDataDirty();
    setAutoRules((current) => current.filter((rule) => rule.id !== ruleId));
  }

  function addManualDraft() {
    if (isAddManualDisabled || !isValidTimeValue(normalizedManualInput)) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.message.manual.invalidTime") });
      return;
    }

    markLocalDataDirty();
    setManualDrafts((current) => sortManualDraftRows([
      ...current,
      {
        id: Date.now(),
        lineId: selectedLine.id,
        serviceId: selectedLine.id,
        kind: currentKind,
        time: normalizedManualInput,
        offsetMode: "none",
        offsetMinutes: ""
      }
    ]));
    setManualInput("");
    if (manualInputRef.current) {
      manualInputRef.current.value = "";
    }
  }

  function removeManualDraft(draftId) {
    markLocalDataDirty();
    setManualDrafts((current) => current.filter((draft) => draft.id !== draftId));
  }

  function removeSummaryRow(rowId) {
    markLocalDataDirty();
    setSummaryEntries((current) => current.filter((row) => row.id !== rowId));
  }

  function clearSummaryTable() {
    markLocalDataDirty();
    setSummaryEntries((current) => {
      if (summaryFilter === "current") {
        return current.filter((row) => row.serviceId !== selectedLine.id);
      }

      if (summaryFilter === "local") {
        return current.filter((row) => row.kind !== "local");
      }

      if (summaryFilter === "express") {
        return current.filter((row) => row.kind !== "express");
      }

      return [];
    });
  }

  function importManualToSummary() {
    const sortedDrafts = [...currentManualDrafts].sort((left, right) => (left.time || "").localeCompare(right.time || ""));
    const validatedRows = validateManualRows(sortedDrafts, t);
    const validRows = validatedRows.filter((row) => row.validation.status !== "error");
    const invalidRows = validatedRows.length - validRows.length;
    if (validRows.length === 0) {
      setPanelMessage({ scope: "manual", tone: "neutral", text: t("nativeSchedule.message.manual.noValid") });
      return;
    }

    const nextKinds = new Set(validRows.map((row) => row.kind));
    const existingKinds = getLineKinds(summaryEntries, selectedLine.id);
    const hasKindConflict = [...nextKinds].some((kind) => existingKinds.size > 0 && !existingKinds.has(kind));
    if (hasKindConflict) {
      setPanelMessage({ scope: "manual", tone: "error", text: t("nativeSchedule.message.manual.kindConflict") });
      return;
    }

    const selectedOriginStationId = selectedLine.originStationId || "";
    const occupiedRows = summaryEntries
      .map((row) => ({
        minute: timeToMinutes(row.time),
        originStationId: row.originStationId || ""
      }))
      .filter((row) => row.minute !== null);
    const importedRows = [];
    let blockedRows = 0;

    validRows.forEach((row) => {
      const candidateMinute = timeToMinutes(row.time);
      if (candidateMinute === null) {
        blockedRows += 1;
        return;
      }

      if (!hasMinimumDepartureGapForOrigin(candidateMinute, selectedOriginStationId, occupiedRows)) {
        blockedRows += 1;
        return;
      }

      occupiedRows.push({
        minute: candidateMinute,
        originStationId: selectedOriginStationId
      });
      importedRows.push(createSummaryEntry({
        id: `summary-manual-${selectedLine.id}-${row.id}`,
        time: row.time,
        serviceId: row.serviceId,
        kind: row.kind,
        source: "manual",
        note: buildCombinedNote("direct", t)
      }, t));
    });

    if (importedRows.length === 0) {
      setPanelMessage({
        scope: "manual",
        tone: "neutral",
        text: blockedRows > 0
          ? t("nativeSchedule.message.manual.blockedAll", { count: blockedRows })
          : t("nativeSchedule.message.manual.noValid")
      });
      return;
    }

    markLocalDataDirty();
    setSummaryEntries((current) => normalizeSummaryEntries([...current, ...importedRows], t));
    setPanelMessage({
      scope: "manual",
      tone: "neutral",
      text: blockedRows > 0 || invalidRows > 0
        ? t("nativeSchedule.message.manual.importedWithCounts", {
          count: importedRows.length,
          skipped: blockedRows,
          invalid: invalidRows
        })
        : t("nativeSchedule.message.manual.imported", { count: importedRows.length })
    });
  }

  function importAutoToSummary() {
    if (currentAutoRules.length === 0) {
      setPanelMessage({ scope: "auto", tone: "warning", text: t("nativeSchedule.message.auto.noRules") });
      return;
    }

    const plan = buildAutoStagedPlan({
      currentRows: summaryEntries,
      rowsForLine: currentAutoRules,
      selectedEditLine: selectedLine.id,
      referenceLineIds: getReferenceLineIdsForLine(selectedLine, currentKind),
      lineOptions: planLineOptions,
      replaceExistingAutoRows: false
    });
    if (plan.hasKindConflict) {
      setPanelMessage({ scope: "auto", tone: "error", text: t("nativeSchedule.message.auto.kindConflict") });
      return;
    }

    const importedRows = plan.plannedRows.map((row) => {
      const sourceRule = currentAutoRules.find((rule) => rule.id === row.ruleId);
      return createSummaryEntry({
        id: `summary-auto-${selectedLine.id}-${row.ruleId}-${row.generatedIndex}`,
        time: minutesToTime(row.timeMinutes),
        serviceId: sourceRule?.serviceId || selectedLine.id,
        kind: row.kind,
        source: "auto",
        note: buildCombinedNote(row.noteType, t, {
          minutes: row.offsetMinutes,
          start: row.start,
          end: row.end
        })
      }, t);
    });
    if (importedRows.length === 0) {
      const issuePreview = currentAutoRules
        .map((rule) => currentAutoPlan.previewsByRule[rule.id])
        .find((preview) => preview?.reason);
      setPanelMessage({
        scope: "auto",
        tone: "warning",
        text: issuePreview
          ? buildPreviewMetaText(issuePreview, false, t)
          : plan.skippedCount > 0
            ? t("nativeSchedule.message.auto.noTrips.skipped", { count: plan.skippedCount })
            : t("nativeSchedule.message.auto.noRules")
      });
      return;
    }

    markLocalDataDirty();
    setSummaryEntries((current) => normalizeSummaryEntries([...current, ...importedRows], t));
    setPanelMessage({
      scope: "auto",
      tone: "neutral",
      text: plan.skippedCount > 0
        ? t("nativeSchedule.message.auto.importedWithSkipped", { count: importedRows.length, skipped: plan.skippedCount })
        : t("nativeSchedule.message.auto.imported", { count: importedRows.length })
    });
  }

  async function handleApplySchedule() {
    try {
      const result = await saveNativeWorkbenchDraft({ applyDraft: true });
      if (!result?.success) {
        return;
      }
    } catch {}
  }

  function handleLocateConflict() {
    const scrollContainer = summaryScrollRef.current;
    const firstConflictRow = scrollContainer?.querySelector(".dw-demo-summary-row.is-conflict");
    if (!scrollContainer || !firstConflictRow) {
      return;
    }

    const containerRect = scrollContainer.getBoundingClientRect();
    const rowRect = firstConflictRow.getBoundingClientRect();
    const deltaTop = rowRect.top - containerRect.top;
    const nextScrollTop =
      scrollContainer.scrollTop + deltaTop - Math.max(0, Math.round((scrollContainer.clientHeight - firstConflictRow.clientHeight) / 2));
    scrollContainer.scrollTop = Math.max(0, nextScrollTop);
  }

  return (
    <div className="dw-demo-page-root">
      <div className="dw-demo-shell">
        <div className="dw-demo-topbar">
        <WorkbenchDropdown
          label={t("nativeSchedule.topbar.line")}
          value={getLocalizedLineName(selectedLine, t)}
          options={LINE_OPTIONS.map((line) => ({
            value: line?.id || "",
            label: getLocalizedLineName(line, t),
            active: line?.id === selectedLineId
          }))}
          onSelect={handleSelectLine}
          className="is-line"
          variant="field"
          positioning="portal"
          portalHostRef={dropdownPortalHostRef}
        />

        <div className="dw-demo-field is-kind">
          <label className="dw-demo-label">{t("nativeSchedule.topbar.kind")}</label>
          <div className="dw-demo-toggle-group">
            <button
              type="button"
              className={`dw-demo-toggle ${selectedLineType === "local" ? "is-active" : ""}`}
              onClick={() => handleLineTypeSelect("local")}
            >
              {t("nativeSchedule.type.local")}
            </button>
            <button
              type="button"
              className={`dw-demo-toggle ${selectedLineType === "express" ? "is-active is-express" : ""}`}
              onClick={() => handleLineTypeSelect("express")}
            >
              {t("nativeSchedule.type.express")}
            </button>
          </div>
        </div>

        <DemoDisplayField
          label={t("nativeSchedule.topbar.origin")}
          value={getLocalizedOriginLabel(origin, t)}
          className="is-origin"
        />
        <WorkbenchDropdown
          label={t("nativeSchedule.topbar.depot")}
          value={getLocalizedDepotLabel(selectedDepot, t)}
          options={[
            {
              value: "",
              label: t("nativeSchedule.data.depot.any"),
              active: !selectedDepot
            },
            ...availableDepots.map((depot) => ({
              value: depot?.id || "",
              label: depot.label || t(depot.labelKey),
              active: selectedDepot === depot?.id
            }))
          ]}
          onSelect={handleDepotChange}
          className="is-depot"
          variant="field"
          positioning="portal"
          portalHostRef={dropdownPortalHostRef}
        />

        <DemoTextField label={holdMinutesTooSmall ? "\u4e0d\u5f97\u5c0f\u4e8e5\u5206" : t("nativeSchedule.topbar.holdMinutes")} value={holdMinutes} onCommit={handleHoldMinutesChange} onDraftChange={setHoldMinutes} className={`is-hold${holdMinutesTooSmall ? " is-error" : ""}`} suffix={t("nativeSchedule.unit.minutes")} />
        <DemoTextField label={dwellMinutesTooSmall ? "\u4e0d\u5f97\u5c0f\u4e8e5\u5206" : t("nativeSchedule.topbar.dwellMinutes")} value={dwellMinutes} onCommit={handleDwellMinutesChange} onDraftChange={setDwellMinutes} className={`is-dwell${dwellMinutesTooSmall ? " is-error" : ""}`} suffix={t("nativeSchedule.unit.minutes")} />
        </div>
        <div className="dw-demo-main">
        <SummarySection
          summaryStateLabel={summaryStateLabel}
          hasAppliedSchedule={hasAppliedSchedule}
          summaryRows={visibleSummaryRows}
          earliestStart={earliestStart}
          conflictCount={conflictCount}
          summaryFilter={summaryFilter}
          onSummaryFilterChange={setSummaryFilter}
          summaryScrollRef={summaryScrollRef}
          onRemoveRow={removeSummaryRow}
          onClearSummary={clearSummaryTable}
          onApplySchedule={handleApplySchedule}
          onLocateConflict={handleLocateConflict}
          dropdownPortalHostRef={dropdownPortalHostRef}
        />

        <section className="dw-demo-right">
          <div className="dw-demo-tabs">
            <button
              type="button"
              className={`dw-demo-tab ${activeRightTab === "auto" ? "is-active" : ""}`}
              onClick={() => setActiveRightTab("auto")}
            >
              {t("nativeSchedule.tab.auto")}
            </button>
            <button
              type="button"
              className={`dw-demo-tab ${activeRightTab === "manual" ? "is-active" : ""}`}
              onClick={() => setActiveRightTab("manual")}
            >
              {t("nativeSchedule.tab.manual")}
            </button>
          </div>

          {activeRightTab === "auto" ? (
      <AutoRuleSection
        editorStart={editorStart}
        editorEnd={editorEnd}
        autoFrequencyText={autoFrequencyText}
        autoFrequencyPerHour={autoFrequencyPerHour}
        selectedLineType={selectedLineType}
        autoOffsetDirection={autoOffsetDirection}
              autoOffsetMinutesText={autoOffsetMinutesText}
              liveAutoPreview={liveAutoPreview}
              autoRules={renderedAutoRules}
              footerNote={autoFooterNote}
              editorEndInputRef={editorEndInputRef}
        frequencyInputRef={frequencyInputRef}
        onEditorStartChange={handleEditorStartChange}
        onEditorEndChange={handleEditorEndChange}
        onAutoFrequencyChange={handleAutoFrequencyChange}
        onAutoOffsetDirectionChange={handleAutoOffsetDirectionChange}
        onAutoOffsetMinutesChange={handleAutoOffsetMinutesChange}
        onAddAutoRule={addAutoRule}
              onRemoveAutoRule={removeAutoRule}
              onImportAutoToSummary={importAutoToSummary}
            />
          ) : (
            <ManualDraftSection
              manualInput={manualInput}
              manualInputRef={manualInputRef}
              manualDrafts={validatedManualDrafts}
              manualInputError={manualInputError}
              isAddManualDisabled={isAddManualDisabled}
              footerNote={manualFooterNote}
              onManualInputChange={handleManualInputChange}
              onAddManualDraft={addManualDraft}
              onRemoveManualDraft={removeManualDraft}
              onImportManualToSummary={importManualToSummary}
            />
          )}
        </section>
        </div>
      </div>
      <div ref={dropdownPortalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}

export default memo(NativeScheduleDemoPage);
