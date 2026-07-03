import WorkbenchDropdown from "../../../shared/WorkbenchDropdown";
import { TRIGGER_OPTIONS } from "../broadcast-constants";
import { normalizeLangIndex } from "../broadcast-normalize";
import { AnimatedInlinePanel } from "./BroadcastAnimatedPanels";
import { CloseIcon, PlusIcon } from "./BroadcastIcons";
import { SequenceRule } from "./BroadcastSequenceRule";

function VehicleRuleList({ toolbar, rules, refs, actions }) {
  const { labels, tabStage, triggerDropdownOpen } = toolbar;

  return (
    <div className={`dw-bc-tab-scene dw-bc-tab-scene-sequence is-${tabStage}`}>
      {rules.vehicleRules.map((rule, index) => (
        <div
          key={rule.id}
          className="dw-bc-page-enter-slide-up"
          style={{ animationDelay: `${index * 0.15}s` }}
        >
          <SequenceRule
            rule={rule}
            trayContext={rules.trayContext}
            trayCategory={rules.trayCategory}
            removingRule={Boolean(rules.removingRuleIds[rule.id])}
            removingNodeIds={rules.removingNodeIds}
            previewingRuleId={rules.previewingRuleId}
            onToggleTray={actions.toggleTray}
            onToggleRulePreview={actions.handleRulePreviewToggle}
            onRemoveNode={actions.handleRemoveNode}
            onRemoveRule={actions.handleRemoveRule}
            onSetTrayCategory={actions.setTrayCategory}
            onCloseTray={() => actions.setTrayContext(null)}
            onAddAsset={(ruleId, asset) =>
              actions.handleAddNodeToRule(ruleId, {
                name: asset.name,
                desc: labels.assetNode,
                descKey: "broadcast.node.asset",
                type: "asset",
              })
            }
            onAddVariable={(ruleId, variable) =>
              actions.handleAddNodeToRule(ruleId, {
                name: variable.name,
                nameKey: variable.nameKey,
                desc: labels.dynamicVariable,
                descKey: "broadcast.node.dynamicVariable",
                type: "variable",
                langIndex: normalizeLangIndex(variable.langIndex),
              })
            }
            onAddDelay={(ruleId, delay) =>
              actions.handleAddNodeToRule(ruleId, {
                name: delay.name,
                desc: labels.delayNode,
                descKey: "broadcast.node.delay",
                type: "delay",
                delaySeconds: delay.delaySeconds || 0,
              })
            }
            trayRef={refs.trayRef}
            assetLibrary={rules.trayAssetLibrary}
            variableLibrary={rules.variableLibrary}
            delayLibrary={rules.delayLibrary}
            labels={labels}
          />
        </div>
      ))}
      <div className="dw-bc-create-block">
        <div
          className={`dw-bc-create-button-shell ${rules.isCreatingRule || rules.availableBroadcastTriggerOptions.length === 0 ? "is-hidden" : "is-visible"}`}
        >
          <button
            type="button"
            className="dw-bc-create-button"
            onClick={() => {
              actions.setIsCreatingRule(true);
              actions.setTrayContext(null);
              actions.setMappingTray(null);
              actions.setNewRuleTriggerId(
                rules.availableBroadcastTriggerOptions[0]?.id ||
                  TRIGGER_OPTIONS[0].id,
              );
            }}
          >
            <span className="dw-bc-create-button-icon-shell">
              <PlusIcon />
            </span>
            <span className="dw-bc-create-button-copy">
              {labels.createRule}
            </span>
          </button>
        </div>
        <AnimatedInlinePanel
          visible={rules.isCreatingRule}
          className="dw-bc-create-panel"
        >
          <div className="dw-bc-create-form">
            <button
              type="button"
              className="dw-bc-icon-button is-corner"
              onClick={() => {
                actions.setIsCreatingRule(false);
                actions.setTriggerDropdownOpen(false);
              }}
            >
              <CloseIcon />
            </button>
            <h3>{labels.createRuleTitle}</h3>
            <div className="dw-bc-form-field">
              <label>{labels.ruleNameLabel}</label>
              <input
                type="text"
                value={rules.newRuleTitle}
                placeholder={labels.ruleNamePlaceholder}
                onClick={(event) => event.stopPropagation()}
                onChange={(event) =>
                  actions.setNewRuleTitle(event.target.value)
                }
              />
            </div>
            <div className="dw-bc-form-field is-dropdown">
              <label>{labels.triggerLabel}</label>
              <WorkbenchDropdown
                open={triggerDropdownOpen}
                onOpenChange={(next) => {
                  actions.setLineDropdownOpen(false);
                  actions.setTriggerDropdownOpen(next);
                }}
                onSelect={(value) => {
                  actions.setNewRuleTriggerId(value);
                  actions.setTriggerDropdownOpen(false);
                }}
                options={rules.availableBroadcastTriggerOptions.map(
                  (option) => ({
                    key: option.id,
                    value: option.id,
                    label: option.label,
                    active: option.id === rules.newRuleTriggerId,
                  }),
                )}
                value={rules.newRuleTrigger?.label || ""}
                className="dw-bc-form-dropdown"
                variant="field"
                positioning="portal"
                portalHostRef={refs.dropdownPortalHostRef}
              />
            </div>
            <button
              type="button"
              className="dw-bc-primary-button"
              onClick={actions.handleCreateRule}
            >
              {labels.saveRule}
            </button>
          </div>
        </AnimatedInlinePanel>
      </div>
    </div>
  );
}

function PlatformRuleList({ toolbar, rules, refs, actions }) {
  const { labels, t, tabStage, triggerDropdownOpen } = toolbar;
  const {
    stations,
    platformRules,
    platformTriggerOptions,
    platformCreateStationIds,
  } = rules;

  return (
    <div
      className={`dw-bc-mapping dw-bc-tab-scene dw-bc-tab-scene-mapping is-${tabStage}`}
    >
      {platformRules.map((rule, index) => (
        <div
          key={rule.id}
          className="dw-bc-page-enter-slide-up"
          style={{ animationDelay: `${index * 0.15}s` }}
        >
          <SequenceRule
            rule={rule}
            trayContext={rules.trayContext}
            trayCategory={rules.trayCategory}
            removingRule={false}
            removingNodeIds={rules.removingNodeIds}
            previewingRuleId=""
            onToggleTray={actions.toggleTray}
            onToggleRulePreview={() => {}}
            onRemoveNode={actions.handleRemovePlatformRuleNode}
            onRemoveRule={actions.handleRemovePlatformRule}
            onSetTrayCategory={actions.setTrayCategory}
            onCloseTray={() => actions.setTrayContext(null)}
            onAddAsset={(ruleId, asset) =>
              actions.handleAddNodeToPlatformRule(ruleId, {
                name: asset.name,
                desc: labels.assetNode,
                descKey: "broadcast.node.asset",
                type: "asset",
              })
            }
            onAddVariable={(ruleId, variable) =>
              actions.handleAddNodeToPlatformRule(ruleId, {
                name: variable.name,
                nameKey: variable.nameKey,
                desc: labels.dynamicVariable,
                descKey: "broadcast.node.dynamicVariable",
                type: "variable",
                langIndex: normalizeLangIndex(variable.langIndex),
              })
            }
            onAddDelay={(ruleId, delay) =>
              actions.handleAddNodeToPlatformRule(ruleId, {
                name: delay.name,
                desc: labels.delayNode,
                descKey: "broadcast.node.delay",
                type: "delay",
                delaySeconds: delay.delaySeconds || 0,
              })
            }
            trayRef={refs.trayRef}
            assetLibrary={rules.trayAssetLibrary}
            variableLibrary={rules.platformTurnbackVariables}
            delayLibrary={rules.delayLibrary}
            labels={labels}
            showPreview={false}
          >
            <div className="dw-bc-platform-targets">
              <div className="dw-bc-platform-target-head">
                <span>{t("broadcast.platform.stationLabel")}</span>
              </div>
              <div className="dw-bc-platform-station-buttons">
                {stations.map((station) => {
                  const isActive = rule.stationIds.includes(station.id);
                  const isOccupied =
                    !isActive &&
                    actions.isPlatformStationOccupiedByTrigger(
                      station.id,
                      rule.triggerId,
                      rule.id,
                    );
                  return (
                    <button
                      key={`${rule.id}:${station.id}`}
                      type="button"
                      className={`dw-bc-platform-station-button ${isActive ? "is-active" : ""} ${isOccupied ? "is-disabled" : ""}`}
                      disabled={isOccupied}
                      onClick={() => {
                        if (!isOccupied) {
                          actions.handleTogglePlatformRuleStation(
                            rule.id,
                            station.id,
                          );
                        }
                      }}
                    >
                      {station.name}
                    </button>
                  );
                })}
              </div>
            </div>
          </SequenceRule>
        </div>
      ))}
      <div className="dw-bc-create-block">
        <div
          className={`dw-bc-create-button-shell ${rules.isCreatingRule ? "is-hidden" : "is-visible"}`}
        >
          <button
            type="button"
            className="dw-bc-create-button"
            onClick={() => {
              actions.setIsCreatingRule(true);
              actions.setTrayContext(null);
              actions.setMappingTray(null);
              actions.setNewRuleTriggerId("platform_idle_clear");
              actions.setPlatformCreateStationIds((current) => {
                const availableStations =
                  actions.getAvailablePlatformCreateStations(
                    "platform_idle_clear",
                  );
                const kept = current.filter((stationId) =>
                  availableStations.some((station) => station.id === stationId),
                );
                return kept.length > 0
                  ? kept
                  : availableStations[0]?.id
                    ? [availableStations[0].id]
                    : [];
              });
            }}
          >
            <span className="dw-bc-create-button-icon-shell">
              <PlusIcon />
            </span>
            <span className="dw-bc-create-button-copy">
              {labels.createRule}
            </span>
          </button>
        </div>
        <AnimatedInlinePanel
          visible={rules.isCreatingRule}
          className="dw-bc-create-panel"
        >
          <div className="dw-bc-create-form">
            <button
              type="button"
              className="dw-bc-icon-button is-corner"
              onClick={() => {
                actions.setIsCreatingRule(false);
                actions.setTriggerDropdownOpen(false);
              }}
            >
              <CloseIcon />
            </button>
            <h3>{labels.createRuleTitle}</h3>
            <div className="dw-bc-form-field">
              <label>{labels.ruleNameLabel}</label>
              <input
                type="text"
                value={rules.newRuleTitle}
                placeholder={labels.ruleNamePlaceholder}
                onClick={(event) => event.stopPropagation()}
                onChange={(event) =>
                  actions.setNewRuleTitle(event.target.value)
                }
              />
            </div>
            <div className="dw-bc-form-field is-dropdown">
              <label>{labels.triggerLabel}</label>
              <WorkbenchDropdown
                open={triggerDropdownOpen}
                onOpenChange={(next) => {
                  actions.setLineDropdownOpen(false);
                  actions.setTriggerDropdownOpen(next);
                }}
                onSelect={(value) => {
                  actions.setNewRuleTriggerId(value);
                  actions.setPlatformCreateStationIds((current) =>
                    current.filter(
                      (stationId) =>
                        !actions.isPlatformStationOccupiedByTrigger(
                          stationId,
                          value,
                        ),
                    ),
                  );
                  actions.setTriggerDropdownOpen(false);
                }}
                options={platformTriggerOptions.map((option) => ({
                  key: option.id,
                  value: option.id,
                  label: option.label,
                  active: option.id === rules.newRuleTriggerId,
                }))}
                value={
                  (
                    platformTriggerOptions.find(
                      (option) => option.id === rules.newRuleTriggerId,
                    ) ?? platformTriggerOptions[0]
                  )?.label || ""
                }
                className="dw-bc-form-dropdown"
                variant="field"
                positioning="portal"
                portalHostRef={refs.dropdownPortalHostRef}
              />
            </div>
            <div className="dw-bc-form-field">
              <label>{t("broadcast.platform.stationLabel")}</label>
              <div className="dw-bc-platform-station-buttons">
                {stations.map((station) => {
                  const isOccupied = actions.isPlatformStationOccupiedByTrigger(
                    station.id,
                    rules.newRuleTriggerId,
                  );
                  const isActive =
                    !isOccupied &&
                    platformCreateStationIds.includes(station.id);
                  return (
                    <button
                      key={`create-platform:${station.id}`}
                      type="button"
                      className={`dw-bc-platform-station-button ${isActive ? "is-active" : ""} ${isOccupied ? "is-disabled" : ""}`}
                      disabled={isOccupied}
                      onClick={() => {
                        if (!isOccupied) {
                          actions.setPlatformCreateStationIds((current) =>
                            current.includes(station.id)
                              ? current.filter((entry) => entry !== station.id)
                              : [...current, station.id],
                          );
                        }
                      }}
                    >
                      {station.name}
                    </button>
                  );
                })}
              </div>
            </div>
            <button
              type="button"
              className="dw-bc-primary-button"
              onClick={actions.handleCreatePlatformRule}
            >
              {labels.saveRule}
            </button>
          </div>
        </AnimatedInlinePanel>
      </div>
    </div>
  );
}

export default function BroadcastRuleList({ toolbar, rules, refs, actions }) {
  if (toolbar.renderedTab === "platform") {
    return (
      <PlatformRuleList
        toolbar={toolbar}
        rules={rules}
        refs={refs}
        actions={actions}
      />
    );
  }

  return (
    <VehicleRuleList
      toolbar={toolbar}
      rules={rules}
      refs={refs}
      actions={actions}
    />
  );
}
