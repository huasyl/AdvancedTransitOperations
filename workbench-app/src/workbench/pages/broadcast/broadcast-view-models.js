function extractBackendLineOptions(snapshot) {
  const sourceLines = Array.isArray(snapshot?.lines) ? snapshot.lines : [];
  return sourceLines
    .filter((line) => line && typeof line.id === "string" && line.id)
    .map((line) => ({
      id: line.id,
      label: typeof line.name === "string" && line.name ? line.name : "--",
    }));
}

function splitIntoColumns(items, count = 2) {
  const columns = Array.from({ length: count }, () => []);
  items.forEach((item, index) => {
    columns[index % count].push(item);
  });
  return columns;
}

function splitIntoVerticalColumns(items, count = 2) {
  const sourceItems = Array.isArray(items) ? items : [];
  const rowsPerColumn = Math.ceil(sourceItems.length / count);
  return Array.from({ length: count }, (_, columnIndex) =>
    sourceItems.slice(
      columnIndex * rowsPerColumn,
      (columnIndex + 1) * rowsPerColumn,
    ),
  );
}

function buildBroadcastFooterView({
  broadcastApplyError,
  isApplyingBroadcastConfig,
  isBroadcastConfigApplied,
  broadcastVariableMappingIssue,
  labels,
}) {
  const tone = broadcastApplyError
    ? "error"
    : isApplyingBroadcastConfig
      ? "pending"
      : broadcastVariableMappingIssue
        ? "warning"
        : isBroadcastConfigApplied
          ? "ready"
          : "dirty";
  const text =
    broadcastApplyError ||
    (isApplyingBroadcastConfig
      ? labels.footerStatusApplying
      : broadcastVariableMappingIssue
        ? labels.footerStatusMappingRequired.replace(
            "{station}",
            broadcastVariableMappingIssue.stationName,
          )
        : isBroadcastConfigApplied
          ? ""
          : labels.footerStatusClean);
  const buttonLabel = isApplyingBroadcastConfig
    ? labels.footerStatusApplying
    : broadcastVariableMappingIssue
      ? labels.footerLocateMapping
      : isBroadcastConfigApplied
        ? labels.appliedConfig
        : labels.applyConfig;
  return { tone, text, buttonLabel };
}

export {
  extractBackendLineOptions,
  splitIntoColumns,
  splitIntoVerticalColumns,
  buildBroadcastFooterView,
};
