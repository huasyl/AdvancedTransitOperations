function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function getLineName(line, index) {
  return line?.name || line?.displayName || line?.id || `Line ${index + 1}`;
}

function getLineCode(line, index) {
  return line?.code || line?.number || String(index + 1).padStart(2, "0");
}

export function buildPassengerFlowViewModel(snapshot, metadataSnapshot) {
  const source = asArray(snapshot?.lines).length > 0 ? snapshot : metadataSnapshot;
  const lines = asArray(source?.lines).map((line, index) => ({
    id: line?.id || `line-${index + 1}`,
    code: getLineCode(line, index),
    name: getLineName(line, index),
    color: line?.color || (line?.kind === "express" ? "#c084fc" : "#5ab4c5")
  }));

  return {
    lines,
    systemTrend: asArray(source?.systemTrend),
    stationVolumes: asArray(source?.stationVolumes),
    sectionVolumes: asArray(source?.sectionVolumes),
    odFlows: asArray(source?.odFlows),
    warnings: asArray(source?.passengerFlowWarnings)
  };
}

export function filterPassengerFlow(viewModel, selectedLineId) {
  if (!selectedLineId || selectedLineId === "ALL") {
    return viewModel;
  }

  return {
    ...viewModel,
    stationVolumes: viewModel.stationVolumes.filter((entry) => entry?.lineId === selectedLineId),
    sectionVolumes: viewModel.sectionVolumes.filter((entry) => entry?.lineId === selectedLineId),
    odFlows: viewModel.odFlows.filter((entry) => entry?.lineId === selectedLineId)
  };
}
