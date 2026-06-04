import { useEffect, useState } from "react";
import {
  resolveRuleNodeKindLabel,
  resolveRuleTriggerLabel,
  resolveVariableNodeDisplayName,
} from "../broadcast-rules";
import { AnimatedInlinePanel } from "./BroadcastAnimatedPanels";
import BroadcastAssetTray from "./BroadcastAssetTray";
import {
  CloseIcon,
  DatabaseIcon,
  DelayIcon,
  PauseIcon,
  PlayIcon,
  PlusIcon,
  SpeakerIcon,
} from "./BroadcastIcons";

function SequenceRule({
  rule,
  trayContext,
  trayCategory,
  removingRule,
  removingNodeIds,
  onToggleTray,
  onRemoveNode,
  onRemoveRule,
  onSetTrayCategory,
  onCloseTray,
  onAddAsset,
  onAddVariable,
  onAddDelay,
  trayRef,
  assetLibrary,
  variableLibrary,
  delayLibrary,
  previewingRuleId,
  onToggleRulePreview,
  labels,
  showPreview = true,
  showRemoveRule = true,
  children = null,
}) {
  const isTrayVisible = trayContext?.ruleId === rule.id;
  const [displayAction, setDisplayAction] = useState(null);

  useEffect(() => {
    if (isTrayVisible && trayContext?.action) {
      setDisplayAction(trayContext.action);
    }
  }, [isTrayVisible, trayContext]);

  return (
    <div className={`dw-bc-rule ${removingRule ? "is-removing" : ""}`}>
      <div className="dw-bc-rule-head">
        <div>
          <h2>
            {rule.title || (rule.titleKey ? labels.t(rule.titleKey) : "")}
          </h2>
          <div className="dw-bc-rule-meta">
            <p>
              {labels.triggerPrefix}
              {resolveRuleTriggerLabel(rule, labels)}
            </p>
            {showPreview ? (
              <button
                type="button"
                className="dw-bc-rule-preview"
                onClick={() => onToggleRulePreview(rule.id)}
              >
                <span className="dw-bc-rule-preview-icon-shell">
                  {previewingRuleId === rule.id ? <PauseIcon /> : <PlayIcon />}
                </span>
                <span>{labels.previewRule}</span>
              </button>
            ) : null}
          </div>
        </div>
        {showRemoveRule ? (
          <button
            type="button"
            className="dw-bc-link-muted"
            onClick={() => onRemoveRule(rule.id)}
          >
            {labels.removeRule}
          </button>
        ) : null}
      </div>

      <div className="dw-bc-node-flow">
        {rule.nodes.map((node, index) => {
          const isEditing =
            trayContext?.ruleId === rule.id && trayContext?.action === node.id;
          const isRemoving = removingNodeIds[`${rule.id}:${node.id}`];
          return (
            <div
              key={node.id}
              className={`dw-bc-node-flow-item dw-bc-page-enter-slide ${isRemoving ? "is-removing" : ""}`}
              style={{ animationDelay: `${index * 0.08}s` }}
            >
              <div className="dw-bc-node-meta">
                <span
                  className={`dw-bc-node-kind ${node.type === "variable" ? "is-variable" : ""}`}
                >
                  {node.type === "variable" ? (
                    <DatabaseIcon className="dw-bc-node-kind-icon is-variable" />
                  ) : node.type === "delay" ? (
                    <DelayIcon className="dw-bc-node-kind-icon is-delay" />
                  ) : (
                    <SpeakerIcon className="dw-bc-node-kind-icon is-asset" />
                  )}
                  {resolveRuleNodeKindLabel(node, labels)}
                </span>
                <div className="dw-bc-node-value-wrap">
                  <button
                    type="button"
                    className={`dw-bc-node-value ${node.type === "variable" ? "is-variable" : ""} ${isEditing ? "is-active" : ""}`}
                    onClick={() => onToggleTray(rule.id, node.id)}
                  >
                    {node.type === "variable"
                      ? resolveVariableNodeDisplayName(node, labels)
                      : node.name ||
                        (node.nameKey ? labels.t(node.nameKey) : "")}
                  </button>
                  <button
                    type="button"
                    className="dw-bc-node-remove"
                    onClick={() => onRemoveNode(rule.id, node.id)}
                  >
                    <CloseIcon />
                  </button>
                </div>
              </div>
              {index < rule.nodes.length - 1 ? (
                <div className="dw-bc-node-sep">/</div>
              ) : null}
            </div>
          );
        })}

        <div className="dw-bc-node-flow-item is-add-action">
          {rule.nodes.length > 0 ? (
            <div className="dw-bc-node-sep">/</div>
          ) : null}
          <button
            type="button"
            className={`dw-bc-node-add ${trayContext?.ruleId === rule.id && trayContext?.action === "add" ? "is-active" : ""}`}
            onClick={() => onToggleTray(rule.id, "add")}
          >
            <span className="dw-bc-inline-icon-shell">
              {trayContext?.ruleId === rule.id &&
              trayContext?.action === "add" ? (
                <CloseIcon />
              ) : (
                <PlusIcon />
              )}
            </span>
            <span className="dw-bc-inline-button-copy">
              {trayContext?.ruleId === rule.id && trayContext?.action === "add"
                ? labels.cancelAddNode
                : labels.addNode}
            </span>
          </button>
        </div>
      </div>

      {children}

      <AnimatedInlinePanel visible={isTrayVisible} panelRef={trayRef}>
        <div className="dw-bc-tray">
          <div className="dw-bc-tray-head">
            <span>
              {displayAction === "add"
                ? labels.addTrayTitle
                : labels.replaceTrayTitle}
            </span>
            <button
              type="button"
              className="dw-bc-icon-button"
              onClick={onCloseTray}
            >
              <CloseIcon />
            </button>
          </div>

          <BroadcastAssetTray
            rule={rule}
            trayCategory={trayCategory}
            assetLibrary={assetLibrary}
            variableLibrary={variableLibrary}
            delayLibrary={delayLibrary}
            labels={labels}
            onSetTrayCategory={onSetTrayCategory}
            onAddAsset={onAddAsset}
            onAddVariable={onAddVariable}
            onAddDelay={onAddDelay}
          />
        </div>
      </AnimatedInlinePanel>
    </div>
  );
}

export { SequenceRule };
