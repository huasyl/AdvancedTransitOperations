function isTerminalBroadcastPreviewState(state) {
  return state === "ended" || state === "stopped" || state === "error";
}

function normalizeBroadcastPreviewVolumeResult(result) {
  return result && Number.isFinite(result.volume) ? result.volume : null;
}

export {
  isTerminalBroadcastPreviewState,
  normalizeBroadcastPreviewVolumeResult,
};
