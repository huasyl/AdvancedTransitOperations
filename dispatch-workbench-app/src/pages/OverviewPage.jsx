import DiagramPanel from "../components/DiagramPanel";
import ViewModeForm from "../components/ViewModeForm";

export default function OverviewPage({
  shellMode,
  selectedLineId,
  setSelectedLineId,
  mergedView,
  setMergedView,
  lines,
  stations,
  filteredTrips,
  selectedTripId,
  setSelectedTripId,
  windowValid,
  onRefreshMetadata
}) {
  return (
    <div className={`dw-page-grid is-overview is-shell-${shellMode}`}>
      <div className="dw-col-left">
        <ViewModeForm
          viewMode="merged"
          setViewMode={() => {}}
          selectedLineId={selectedLineId}
          setSelectedLineId={setSelectedLineId}
          mergedView={mergedView}
          setMergedView={setMergedView}
          lines={lines}
          stations={stations}
          windowValid={windowValid}
          onRefreshMetadata={onRefreshMetadata}
        />
      </div>

      <div className="dw-col-main">
        <DiagramPanel
          lines={lines}
          stations={stations}
          trips={filteredTrips}
          mergedView={mergedView}
          selectedTripId={selectedTripId}
          setSelectedTripId={setSelectedTripId}
        />
      </div>
    </div>
  );
}
