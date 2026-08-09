import WorkbenchDropdown from "../../../shared/WorkbenchDropdown";

export default function BroadcastToolbar({ toolbar, refs, actions }) {
  const {
    labels,
    t,
    activeTab,
    lineDropdownOpen,
    lineOptions,
    selectedLine,
    selectedLineId,
    supportsPlatforms,
    broadcastWarnings,
  } = toolbar;

  return (
    <>
      <div className="dw-bc-main-header">
        <div className="dw-bc-tabs">
          <button
            type="button"
            className={`dw-bc-tab ${activeTab === "sequence" ? "is-active" : ""}`}
            onClick={() => {
              actions.setActiveTab("sequence");
              actions.setMappingTray(null);
            }}
          >
            {labels.sequenceTab}
          </button>
          <button
            type="button"
            className={`dw-bc-tab ${activeTab === "mapping" ? "is-active" : ""}`}
            onClick={() => {
              actions.refreshNames();
              actions.setActiveTab("mapping");
              actions.setTrayContext(null);
            }}
          >
            {labels.mappingTab}
          </button>
          {supportsPlatforms ? (
            <button
              type="button"
              className={`dw-bc-tab ${activeTab === "platform" ? "is-active" : ""}`}
              onClick={() => {
                actions.setActiveTab("platform");
                actions.setTrayContext(null);
                actions.setMappingTray(null);
              }}
            >
              {labels.platformTab}
            </button>
          ) : null}
        </div>

        <div className="dw-bc-main-tools">
          <WorkbenchDropdown
            open={lineDropdownOpen}
            onOpenChange={(next) => {
              actions.setTriggerDropdownOpen(false);
              if (next) {
                actions.refreshNames();
              }
              actions.setLineDropdownOpen(next);
            }}
            onSelect={(value) => {
              actions.setSelectedLineId(value);
              actions.setLineDropdownOpen(false);
            }}
            options={lineOptions.map((line) => ({
              key: line.id,
              value: line.id,
              label: line.label,
              active: line.id === selectedLineId,
            }))}
            value={selectedLine.label}
            label={labels.lineLabel}
            className="dw-bc-line-picker is-line"
            title={selectedLine.label}
            variant="field"
            positioning="portal"
            portalHostRef={refs.dropdownPortalHostRef}
          />
        </div>
      </div>

      {broadcastWarnings.length > 0 ? (
        <div className="dw-bc-warning-box">
          <div className="dw-bc-warning-title">{labels.warningTitle}</div>
          <div className="dw-bc-warning-list">
            {broadcastWarnings.map((warning, index) => (
              <div key={`warning:${index}`} className="dw-bc-warning-item">
                {warning}
              </div>
            ))}
          </div>
        </div>
      ) : null}
    </>
  );
}
