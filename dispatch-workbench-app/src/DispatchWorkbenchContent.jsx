import OverviewPage from "./pages/OverviewPage";
import SchedulePage from "./pages/SchedulePage";

// Shared page content for the EUIS workbench.
// Keep host-specific behavior in euis-entry.jsx / DispatchWorkbenchEuisApp.jsx,
// not in this content layer.
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
  if (activeTab === "overview") {
    return (
      <OverviewPage
        shellMode={shellMode}
        selectedLineId={selectedLineId}
        setSelectedLineId={setSelectedLineId}
        mergedView={mergedView}
        setMergedView={setMergedView}
        lines={lineOptions}
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
    />
  );
}



