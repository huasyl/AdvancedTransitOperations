import { useEffect } from "react";
import OverviewPage from "./pages/OverviewPage";
import SchedulePage from "./pages/SchedulePage";

// Shared page content for the workbench.
export default function DispatchWorkbenchContent({
  shellMode,
  activeTab,
  viewMode,
  setViewMode,
  selectedLineId,
  setSelectedLineId,
  mergedView,
  setMergedView,
  lineOptions,
  depotOptions,
  refreshWorkbenchMetadata,
  isReadonly,
  stationOptions,
  filteredTrips,
  selectedTrip,
  setSelectedTripId,
  overviewSideContext,
  windowValid,
  manualRows,
  setManualRows,
  autoRules,
  setAutoRules,
  stagedRows,
  selectedEditLine,
  setSelectedEditLine,
  validatedRows,
  combinedRows,
  scheduleSideContext,
  previewSummary,
  autoPreviewPlan,
  saveState,
  handleOverviewContextAction,
  handleScheduleContextAction,
  handleSelectedLineKindChange,
  handleAllowedDepotChange,
  handleApplyDraft,
  handleAddManualToStaged,
  handleAddAutoToStaged,
  handleClearStagedLine,
  handleRemoveStagedRow,
  handleOriginHoldLimitChange,
  handleMaxStationDwellChange,
}) {
  useEffect(() => {
    if (activeTab === "schedule") {
      refreshWorkbenchMetadata?.();
    }
  }, [activeTab]);
  if (activeTab === "overview") {
    return (
      <OverviewPage
        shellMode={shellMode}
        selectedLineId={selectedLineId}
        setSelectedLineId={setSelectedLineId}
        mergedView={mergedView}
        setMergedView={setMergedView}
        lines={lineOptions}
        onRefreshMetadata={refreshWorkbenchMetadata}
        stations={stationOptions}
        filteredTrips={filteredTrips}
        selectedTripId={selectedTrip?.id ?? ""}
        setSelectedTripId={setSelectedTripId}
        windowValid={windowValid}
      />
    );
  }

  return (
    <SchedulePage
      shellMode={shellMode}
      manualRows={manualRows}
      setManualRows={setManualRows}
      autoRules={autoRules}
      setAutoRules={setAutoRules}
      stagedRows={stagedRows}
      lines={lineOptions}
      depots={depotOptions}
      stationOptions={stationOptions}
      selectedEditLine={selectedEditLine}
      mergedView={mergedView}
      setSelectedEditLine={(lineId) => {
        setSelectedEditLine(lineId);
        setSelectedLineId(lineId);
      }}
      validatedRows={validatedRows}
      combinedRows={combinedRows}
      sideContext={scheduleSideContext}
      previewSummary={previewSummary}
      autoPreviewPlan={autoPreviewPlan}
      onContextAction={handleScheduleContextAction}
      onApplyDraft={handleApplyDraft}
      onAddManualToStaged={handleAddManualToStaged}
      onAddAutoToStaged={handleAddAutoToStaged}
      onClearStagedLine={handleClearStagedLine}
      onRemoveStagedRow={handleRemoveStagedRow}
      onSelectedLineKindChange={handleSelectedLineKindChange}
      onAllowedDepotChange={handleAllowedDepotChange}
      onOriginHoldLimitChange={handleOriginHoldLimitChange}
      onMaxStationDwellChange={handleMaxStationDwellChange}
      saveState={saveState}
      isReadonly={isReadonly}
    />
  );
}



