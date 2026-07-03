export function chunkItemsBySize(items, chunkSize) {
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

export const TOP_PREVIEW_ROW_CACHE = new Map();
export const RULE_PREVIEW_ROW_CACHE = new Map();
export const PREVIEW_ROW_CACHE_LIMIT = 128;

export function rememberPreviewRows(cache, key, value) {
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

export function getTopPreviewMaxItemsPerRow(script, hasMeta) {
  const isLatin = script === "latin";
  if (isLatin) {
    return hasMeta ? 6 : 7;
  }

  return hasMeta ? 7 : 8;
}

export function getRulePreviewMaxItemsPerRow(script, showOffsetColumn) {
  const isLatin = script === "latin";
  if (isLatin) {
    return showOffsetColumn ? 10 : 11;
  }

  return showOffsetColumn ? 11 : 12;
}

export function getCachedTopPreviewRows(times, maxItemsPerRow) {
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

export function getCachedRulePreviewRows(entries, showSkipped, moveSkippedToEnd, maxItemsPerRow) {
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

export function DemoTopPreviewTimes({
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

export function DemoRuleTextPreviewTimes({
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
export function DemoSectionHeader({
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
