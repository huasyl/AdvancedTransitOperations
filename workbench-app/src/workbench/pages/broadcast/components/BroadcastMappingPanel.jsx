import {
  formatBroadcastAssetDisplayName,
  extractBroadcastLanguageHint,
} from "../broadcast-assets";
import { sortBroadcastConflictAssets } from "../broadcast-bindings";
import { AnimatedInlinePanel } from "./BroadcastAnimatedPanels";
import { CloseIcon, SpeakerIcon } from "./BroadcastIcons";

export default function BroadcastMappingPanel({
  toolbar,
  mapping,
  refs,
  actions,
}) {
  const { labels, t, tabStage } = toolbar;
  const {
    stations,
    mappingTray,
    mappingAssetColumns,
    mappingAssetOrderByName,
    mappingBindFeedback,
    selectedLine,
    fallbackLanguageKey,
  } = mapping;
  const hasAnyStationBindings = stations.some(
    (station) =>
      (Array.isArray(station?.audios) && station.audios.length > 0) ||
      (Array.isArray(station?.conflictAssets) && station.conflictAssets.length > 0),
  );

  return (
    <div
      className={`dw-bc-mapping dw-bc-tab-scene dw-bc-tab-scene-mapping is-${tabStage}`}
    >
      <div className="dw-bc-mapping-head">
        <div>
          <h2>{labels.mappingTitle}</h2>
        </div>
        <div className="dw-bc-mapping-head-actions">
          <button
            type="button"
            className="dw-bc-secondary-button"
            onClick={actions.handleAutoBindStations}
          >
            {labels.autoBind}
          </button>
          <button
            type="button"
            className="dw-bc-secondary-button"
            disabled={!hasAnyStationBindings}
            onClick={actions.handleClearAllStationBindings}
          >
            {labels.mapClearAll}
          </button>
        </div>
      </div>

      <div className="dw-bc-map-table-head">
        <div className="dw-bc-map-col is-id">{labels.mapLineHead}</div>
        <div className="dw-bc-map-col is-name">{labels.mapStationHead}</div>
        <div className="dw-bc-map-col is-audio">{labels.mapAudioHead}</div>
        <div className="dw-bc-map-col is-status">{labels.mapStatusHead}</div>
      </div>

      {stations.map((station, index) => {
        const isMissing = station.status === "missing";
        const isConflict = station.status === "conflict";
        const isReady = station.status === "ready";
        const isMappingTrayVisible = mappingTray === station.id;
        const orderedConflictAssets = isConflict
          ? sortBroadcastConflictAssets(
              station.conflictAssets,
              station.name,
              fallbackLanguageKey,
              labels,
            )
          : [];
        return (
          <div key={station.id}>
            <div
              className="dw-bc-map-row dw-bc-page-enter-slide-up"
              style={{ animationDelay: `${index * 0.08}s` }}
            >
              <div
                className={`dw-bc-map-id dw-bc-map-col is-id ${isMissing ? "is-missing" : ""}`}
              >
                {selectedLine.label}
              </div>
              <div
                className={`dw-bc-map-name dw-bc-map-col is-name ${isMissing ? "is-missing" : ""} ${isConflict ? "is-conflict" : ""}`}
              >
                {station.name}
              </div>
              <div className="dw-bc-map-col is-audio">
                {isReady ? (
                  <div className="dw-bc-map-audio-stack">
                    {station.audios.map((audio) => (
                      <button
                        key={`${station.id}:${audio.lang}:${audio.assetName}`}
                        type="button"
                        className="dw-bc-map-audio-binding"
                        onClick={() => {
                          actions.setMappingTray(
                            isMappingTrayVisible ? null : station.id,
                          );
                          actions.setTrayContext(null);
                        }}
                      >
                        <span className="dw-bc-map-audio-lang">
                          {audio.lang}
                        </span>
                        <span className="dw-bc-map-audio-link-core">
                          <span className="dw-bc-map-audio-name">
                            {formatBroadcastAssetDisplayName(audio.assetName)}
                          </span>
                        </span>
                      </button>
                    ))}
                  </div>
                ) : isConflict ? (
                  <span className="dw-bc-map-conflict-note">
                    {t("broadcast.mapping.conflictPending", {
                      count: station.conflictAssets.length,
                    })}
                  </span>
                ) : (
                  <span className="dw-bc-map-missing">{labels.mapMissing}</span>
                )}
              </div>
              <div className="dw-bc-map-status dw-bc-map-col is-status">
                {isReady ? (
                  <span className="dw-bc-ready">
                    {t("broadcast.mapping.readyCount", {
                      count: station.audios.length,
                    })}
                  </span>
                ) : isConflict ? (
                  <button
                    type="button"
                    className="dw-bc-map-button is-conflict"
                    onClick={() => {
                      actions.setMappingTray(
                        isMappingTrayVisible ? null : station.id,
                      );
                      actions.setTrayContext(null);
                    }}
                  >
                    {t("broadcast.mapping.disambiguate", {
                      count: station.conflictAssets.length,
                    })}
                  </button>
                ) : (
                  <button
                    type="button"
                    className="dw-bc-map-button"
                    onClick={() => {
                      actions.setMappingTray(
                        isMappingTrayVisible ? null : station.id,
                      );
                      actions.setTrayContext(null);
                    }}
                  >
                    {labels.mapBindLanguageAudio}
                  </button>
                )}
              </div>
            </div>

            <AnimatedInlinePanel
              visible={isMappingTrayVisible}
              panelRef={refs.trayRef}
            >
              <div
                className={`dw-bc-tray ${isMissing ? "is-missing" : ""} ${isConflict ? "is-conflict" : ""}`}
              >
                <div className="dw-bc-tray-head">
                  <span>
                    {isConflict
                      ? t("broadcast.mapping.disambiguationTitle", {
                          station: station.name,
                          count: station.conflictAssets.length,
                        })
                      : t("broadcast.mapping.bindingTitle", {
                          station: station.name,
                        })}
                  </span>
                  <button
                    type="button"
                    className="dw-bc-icon-button"
                    onClick={() => actions.setMappingTray(null)}
                  >
                    <CloseIcon />
                  </button>
                </div>
                {isConflict ? (
                  <div className="dw-bc-map-disambiguation-list">
                    {station.audios.length > 0 ? (
                      <div className="dw-bc-map-binding-list">
                        <span className="dw-bc-map-binding-list-title">
                          {labels.mapCurrentBindings}
                        </span>
                        <div className="dw-bc-map-binding-tags">
                          {station.audios.map((audio) => (
                            <div
                              key={`${station.id}:${audio.lang}:${audio.assetName}`}
                              className="dw-bc-map-binding-tag"
                            >
                              <span className="dw-bc-map-binding-tag-lang">{`${audio.lang}:`}</span>
                              <span className="dw-bc-map-binding-tag-name">
                                {formatBroadcastAssetDisplayName(
                                  audio.assetName,
                                )}
                              </span>
                              <button
                                type="button"
                                className="dw-bc-map-binding-tag-remove"
                                onClick={() =>
                                  actions.handleRemoveStationAudio(
                                    station.id,
                                    audio.lang,
                                  )
                                }
                              >
                                <CloseIcon />
                              </button>
                            </div>
                          ))}
                        </div>
                      </div>
                    ) : null}
                    {orderedConflictAssets.map((entry, conflictIndex) => (
                      <div
                        key={`${station.id}:${entry.assetName}`}
                        className="dw-bc-map-disambiguation-item anim-stagger-slide-up"
                        style={{ animationDelay: `${conflictIndex * 0.05}s` }}
                      >
                        <div className="dw-bc-map-disambiguation-asset">
                          <SpeakerIcon />
                          <span>
                            {formatBroadcastAssetDisplayName(entry.assetName)}
                          </span>
                        </div>
                        <div className="dw-bc-map-disambiguation-controls">
                          <span>{labels.mapSuggestedLabel}</span>
                          <input
                            type="text"
                            value={actions.getDisambiguationNameDraft(
                              station.id,
                              entry.assetName,
                              extractBroadcastLanguageHint(
                                entry.assetName,
                                station.name,
                                fallbackLanguageKey,
                                labels,
                              ),
                            )}
                            placeholder={labels.mapLanguagePlaceholder}
                            onClick={(event) => event.stopPropagation()}
                            onChange={(event) =>
                              actions.updateDisambiguationNameDraft(
                                station.id,
                                entry.assetName,
                                event.target.value,
                              )
                            }
                          />
                          <button
                            type="button"
                            className="dw-bc-map-disambiguation-remove"
                            title={labels.mapIgnoreCandidate}
                            onClick={() =>
                              actions.handleDiscardConflict(
                                station.id,
                                entry.assetName,
                              )
                            }
                          >
                            <CloseIcon />
                          </button>
                        </div>
                      </div>
                    ))}
                    <div className="dw-bc-map-disambiguation-actions">
                      <button
                        type="button"
                        className="dw-bc-primary-button is-compact"
                        onClick={() =>
                          actions.handleResolveStationConflicts(station.id)
                        }
                      >
                        {labels.mapConfirmDisambiguation}
                      </button>
                    </div>
                  </div>
                ) : (
                  <>
                    {station.audios.length > 0 ? (
                      <div
                        className={`dw-bc-map-binding-list ${mappingBindFeedback?.stationId === station.id && mappingBindFeedback.phase === "chip" ? "is-bind-feedback" : ""}`}
                        ref={refs.mappingBindingListRef}
                      >
                        <span className="dw-bc-map-binding-list-title">
                          {labels.mapCurrentBindings}
                        </span>
                        <div className="dw-bc-map-binding-tags">
                          {station.audios.map((audio) => (
                            <div
                              key={`${station.id}:${audio.lang}:${audio.assetName}`}
                              className={`dw-bc-map-binding-tag ${mappingBindFeedback?.stationId === station.id && mappingBindFeedback.assetName === audio.assetName && mappingBindFeedback.lang === audio.lang && mappingBindFeedback.phase === "chip" ? "is-bind-feedback" : ""}`}
                            >
                              <span className="dw-bc-map-binding-tag-lang">{`${audio.lang}:`}</span>
                              <span className="dw-bc-map-binding-tag-name">
                                {formatBroadcastAssetDisplayName(
                                  audio.assetName,
                                )}
                              </span>
                              <button
                                type="button"
                                className="dw-bc-map-binding-tag-remove"
                                onClick={() =>
                                  actions.handleRemoveStationAudio(
                                    station.id,
                                    audio.lang,
                                  )
                                }
                              >
                                <CloseIcon />
                              </button>
                            </div>
                          ))}
                        </div>
                      </div>
                    ) : null}
                    <div className="dw-bc-map-binding-toolbar">
                      <label>{`${labels.mapLanguageLabel}:`}</label>
                      <input
                        type="text"
                        value={actions.getBindingLanguageDraft(station.id)}
                        placeholder={labels.mapLanguagePlaceholder}
                        onClick={(event) => event.stopPropagation()}
                        onChange={(event) =>
                          actions.updateBindingLanguageDraft(
                            station.id,
                            event.target.value,
                          )
                        }
                      />
                      <span>{labels.mapLanguageHint}</span>
                    </div>
                    <div className="dw-bc-tray-columns">
                      {mappingAssetColumns.map((column, columnIndex) => (
                        <div
                          key={`mapping-col-${columnIndex}`}
                          className="dw-bc-tray-column"
                        >
                          {column.map((asset) => (
                            <button
                              key={asset.name}
                              type="button"
                              className="dw-bc-tray-item anim-stagger-slide-up"
                              style={{
                                animationDelay: `${(mappingAssetOrderByName.get(asset.name) ?? 0) * 0.05}s`,
                              }}
                              onClick={() =>
                                actions.handleBindStation(
                                  station.id,
                                  asset.name,
                                )
                              }
                            >
                              <span>
                                {formatBroadcastAssetDisplayName(asset.name)}
                              </span>
                              <span>{asset.desc}</span>
                            </button>
                          ))}
                        </div>
                      ))}
                    </div>
                  </>
                )}
              </div>
            </AnimatedInlinePanel>
          </div>
        );
      })}
    </div>
  );
}
