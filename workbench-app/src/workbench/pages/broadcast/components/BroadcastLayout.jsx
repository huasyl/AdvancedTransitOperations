import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { BroadcastAssetSidebar } from "./BroadcastAssetTray";
import BroadcastAssetExplorer from "./BroadcastAssetExplorer";
import BroadcastMappingPanel from "./BroadcastMappingPanel";
import { BroadcastPreviewVolumeControl } from "./BroadcastPreviewVolume";
import BroadcastRuleList from "./BroadcastRuleList";
import BroadcastToolbar from "./BroadcastToolbar";

export default function BroadcastLayout({ controller }) {
  const { toolbar, rules, mapping, assets, preview, overlay, refs, actions } =
    controller;
  const labels = toolbar.labels;
  const bodyMetricsKey = [
    toolbar.renderedTab,
    Boolean(rules.isCreatingRule),
    Boolean(rules.trayContext || mapping.mappingTray),
    mapping.stations.length,
    rules.platformRules.length,
    assets.availableAssetLibrary.length,
  ].join(":");

  return (
    <div
      ref={refs.pageRootRef}
      className={"dw-bc-page is-page-enter-" + overlay.pageEnterState}
      onClick={actions.handleRootClick}
    >
      <div className="dw-bc-shell">
        <div className="dw-bc-shell-entry dw-bc-page-enter-shell origin-bottom">
          <BroadcastAssetSidebar
            assets={assets}
            preview={preview}
            labels={labels}
            actions={actions}
          />

          <section className="dw-bc-main">
            <BroadcastToolbar toolbar={toolbar} refs={refs} actions={actions} />

            <WorkbenchScrollArea
              className="dw-bc-body"
              externalScrollRef={refs.bodyScrollRef}
              metricsKey={bodyMetricsKey}
            >
              <div className="dw-bc-body-pad" ref={refs.bodyPadRef}>
                <div className={"dw-bc-tab-panel is-" + toolbar.tabStage}>
                  <div className="dw-bc-scene-entry dw-bc-page-enter-scene origin-bottom">
                    {toolbar.renderedTab === "mapping" ? (
                      <BroadcastMappingPanel
                        toolbar={toolbar}
                        mapping={mapping}
                        refs={refs}
                        actions={actions}
                      />
                    ) : (
                      <BroadcastRuleList
                        toolbar={toolbar}
                        rules={rules}
                        refs={refs}
                        actions={actions}
                      />
                    )}
                  </div>
                </div>
              </div>
            </WorkbenchScrollArea>

            <footer className="dw-bc-footer">
              <div className="dw-bc-footer-left">
                <div
                  className={
                    "dw-bc-footer-status is-" + preview.broadcastFooterTone
                  }
                >
                  {preview.broadcastFooterText ? (
                    <>
                      <span
                        className={
                          "dw-bc-footer-dot is-" + preview.broadcastFooterTone
                        }
                      />
                      <span>{preview.broadcastFooterText}</span>
                    </>
                  ) : null}
                </div>
                <BroadcastPreviewVolumeControl
                  label={labels.previewVolume}
                  value={preview.broadcastPreviewVolume}
                  onCommit={actions.commitBroadcastPreviewVolume}
                />
              </div>
              <button
                type="button"
                className={
                  "dw-bc-primary-button is-footer " +
                  (preview.isBroadcastConfigApplied ? "is-applied" : "")
                }
                disabled={
                  preview.isApplyingBroadcastConfig ||
                  (!preview.broadcastDraftDirty &&
                    !preview.broadcastVariableMappingIssue) ||
                  (preview.isBroadcastConfigApplied &&
                    !preview.broadcastVariableMappingIssue)
                }
                onClick={
                  preview.broadcastVariableMappingIssue
                    ? actions.handleLocateBroadcastMappingIssue
                    : actions.handleApplyBroadcastConfig
                }
              >
                {preview.broadcastApplyButtonLabel}
              </button>
            </footer>

            <BroadcastAssetExplorer
              toolbar={toolbar}
              assets={assets}
              overlay={overlay}
              actions={actions}
            />
          </section>
        </div>
      </div>
      <div
        ref={refs.dropdownPortalHostRef}
        className="dw-demo-dropdown-portal-layer"
      />
    </div>
  );
}
