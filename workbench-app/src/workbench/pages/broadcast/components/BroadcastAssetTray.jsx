import { useMemo } from "react";
import WorkbenchScrollArea from "../../../shared/WorkbenchScrollArea";
import { formatBroadcastAssetDisplayName } from "../broadcast-assets";
import {
  splitIntoColumns,
  splitIntoVerticalColumns,
} from "../broadcast-view-models";
import {
  DatabaseIcon,
  DelayIcon,
  PauseIcon,
  PlayIcon,
  PlusIcon,
  SpeakerIcon,
  TrashIcon,
} from "./BroadcastIcons";

export function BroadcastAssetSidebar({ assets, preview, labels, actions }) {
  return (
    <aside className="dw-bc-sidebar">
      <header className="dw-bc-sidebar-head">
        <h1>{labels.sidebarTitle}</h1>
      </header>

      <div className="dw-bc-sidebar-tools">
        <span>{labels.localAssets}</span>
        <div className="dw-bc-sidebar-icons">
          <button
            type="button"
            className={`dw-bc-sidebar-clear-button ${assets.assetDeleteBlockedNames?.[assets.deleteAllAssetsKey] ? "is-blocked" : ""}`}
            onClick={actions.handleDeleteAllAssets}
          >
            {assets.assetDeleteBlockedNames?.[assets.deleteAllAssetsKey] ? labels.assetInUseCannotDelete : labels.deleteAllAssets}
          </button>
        </div>
      </div>

      <div className="dw-bc-asset-head">
        <div>{labels.assetFileName}</div>
        <div>
          <span className="dw-bc-asset-time-head">{labels.assetDuration}</span>
          <span className="dw-bc-asset-delete-spacer" />
        </div>
      </div>

      <WorkbenchScrollArea
        className="dw-bc-asset-list"
        metricsKey={assets.availableAssetLibrary.length}
      >
        {assets.availableAssetLibrary.map((asset, index) => (
          (() => {
            const deleteBlocked = Boolean(assets.assetDeleteBlockedNames?.[asset.name]);
            const previewDisabled = Boolean(asset.missing);
            return (
          <div
            key={asset.name}
            className={`dw-bc-asset-row dw-bc-page-enter-slide ${deleteBlocked ? "is-delete-blocked" : ""}`}
            style={{ animationDelay: `${index * 0.05}s` }}
          >
            <button
              type="button"
              className={`dw-bc-asset-play ${preview.previewingAssetName === asset.name ? "is-previewing" : ""}`}
              onClick={() => {
                if (!previewDisabled) {
                  actions.handleAssetPreviewToggle(asset.name);
                }
              }}
              disabled={previewDisabled}
            >
              <span className="dw-bc-asset-play-icon-shell">
                {preview.previewingAssetName === asset.name ? (
                  <PauseIcon />
                ) : (
                  <PlayIcon />
                )}
              </span>
            </button>
            <div className="dw-bc-asset-copy-wrap">
              <div className="dw-bc-asset-copy">
                <div className="dw-bc-asset-name">
                  {formatBroadcastAssetDisplayName(asset.name)}
                </div>
                <div className="dw-bc-asset-desc">{asset.desc}</div>
              </div>
              <button
                type="button"
                className="dw-bc-asset-copy-button"
                onClick={() => {
                  if (!previewDisabled) {
                    actions.handleAssetPreviewToggle(asset.name);
                  }
                }}
                aria-label={asset.name}
                disabled={previewDisabled}
              />
            </div>
            <div className="dw-bc-asset-meta">
              <div className="dw-bc-asset-time">{asset.length}</div>
              <button
                type="button"
                className={`dw-bc-asset-delete ${deleteBlocked ? "is-blocked" : ""}`}
                onClick={() => actions.handleDeleteAsset(asset.name)}
                aria-label={`${labels.deleteAsset} ${asset.name}`}
              >
                {deleteBlocked ? labels.assetInUseCannotDelete : <TrashIcon />}
              </button>
            </div>
          </div>
            );
          })()
        ))}
      </WorkbenchScrollArea>

      <footer className="dw-bc-sidebar-foot">
        <button
          type="button"
          className="dw-bc-outline-button"
          onClick={actions.handleImportAssetDirectory}
        >
          <span className="dw-bc-inline-icon-shell dw-bc-outline-button-icon-shell">
            <PlusIcon />
          </span>
          <span className="dw-bc-inline-button-copy">{labels.importAsset}</span>
        </button>
      </footer>
    </aside>
  );
}

export default function BroadcastAssetTray({
  rule,
  trayCategory,
  assetLibrary,
  variableLibrary,
  delayLibrary,
  labels,
  onSetTrayCategory,
  onAddAsset,
  onAddVariable,
  onAddDelay,
}) {
  const assetColumns = useMemo(
    () => splitIntoVerticalColumns(assetLibrary),
    [assetLibrary],
  );
  const variableColumns = useMemo(
    () => splitIntoColumns(variableLibrary),
    [variableLibrary],
  );
  const delayColumns = useMemo(
    () => splitIntoColumns(delayLibrary),
    [delayLibrary],
  );

  return (
    <>
      <div className="dw-bc-tray-tabs">
        <button
          type="button"
          className={`dw-bc-tray-tab ${trayCategory === "asset" ? "is-active" : ""}`}
          onClick={() => onSetTrayCategory("asset")}
        >
          <SpeakerIcon className="dw-bc-tray-tab-icon is-asset" />
          {labels.assetTab}
        </button>
        <button
          type="button"
          className={`dw-bc-tray-tab is-variable ${trayCategory === "variable" ? "is-active" : ""}`}
          onClick={() => onSetTrayCategory("variable")}
        >
          <DatabaseIcon className="dw-bc-tray-tab-icon is-variable" />
          {labels.variableTab}
        </button>
        <button
          type="button"
          className={`dw-bc-tray-tab is-delay ${trayCategory === "delay" ? "is-active" : ""}`}
          onClick={() => onSetTrayCategory("delay")}
        >
          <DelayIcon className="dw-bc-tray-tab-icon is-delay" />
          {labels.delayTab}
        </button>
      </div>

      {trayCategory === "asset" ? (
        <div className="dw-bc-tray-columns">
          {assetColumns.map((column, columnIndex) => (
            <div key={`asset-col-${columnIndex}`} className="dw-bc-tray-column">
              {column.map((asset, rowIndex) => (
                <button
                  key={asset.name}
                  type="button"
                  className={`dw-bc-tray-item ${asset.isStationBound ? "is-station-bound" : "is-unbound-asset"} anim-stagger-slide-up`}
                  style={{ animationDelay: `${rowIndex * 0.05}s` }}
                  onClick={() => onAddAsset(rule.id, asset)}
                >
                  <span>{formatBroadcastAssetDisplayName(asset.name)}</span>
                  <span
                    className={
                      asset.isStationBound
                        ? "dw-bc-tray-item-note is-station-bound"
                        : "dw-bc-tray-item-note"
                    }
                  >
                    {asset.isStationBound ? labels.t("broadcast.assetType.station") : asset.desc}
                  </span>
                </button>
              ))}
            </div>
          ))}
        </div>
      ) : trayCategory === "variable" ? (
        <div className="dw-bc-tray-columns">
          {variableColumns.map((column, columnIndex) => (
            <div
              key={`variable-col-${columnIndex}`}
              className="dw-bc-tray-column"
            >
              {column.map((variable) => (
                <button
                  key={variable.id}
                  type="button"
                  className="dw-bc-tray-item is-variable anim-stagger-slide-up"
                  style={{
                    animationDelay: `${variableLibrary.findIndex((entry) => entry.id === variable.id) * 0.05}s`,
                  }}
                  onClick={() => onAddVariable(rule.id, variable)}
                >
                  <span>{variable.name}</span>
                  <span>{variable.desc}</span>
                </button>
              ))}
            </div>
          ))}
        </div>
      ) : (
        <div className="dw-bc-tray-columns">
          {delayColumns.map((column, columnIndex) => (
            <div key={`delay-col-${columnIndex}`} className="dw-bc-tray-column">
              {column.map((delay) => (
                <button
                  key={delay.name}
                  type="button"
                  className="dw-bc-tray-item is-delay anim-stagger-slide-up"
                  style={{
                    animationDelay: `${delayLibrary.findIndex((entry) => entry.name === delay.name) * 0.05}s`,
                  }}
                  onClick={() => onAddDelay(rule.id, delay)}
                >
                  <span>{delay.name}</span>
                  <span>{delay.desc}</span>
                </button>
              ))}
            </div>
          ))}
        </div>
      )}
    </>
  );
}
