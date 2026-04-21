import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useNativeScheduleI18n } from "./native-schedule-i18n";
import { getWorkbenchApi } from "./lib/workbench-api";

const ASSET_LIBRARY = [
  { name: "JR_Water_Crown.wav", descKey: "broadcast.assetType.prompt", length: "00:05" },
  { name: "Next_Station_Is.mp3", descKey: "broadcast.assetType.voice", length: "00:02" },
  { name: "Doors_Closing.mp3", descKey: "broadcast.assetType.voice", length: "00:03" },
  { name: "Station_Bantian.wav", descKey: "broadcast.assetType.station", length: "00:01" },
  { name: "Station_Gongye.wav", descKey: "broadcast.assetType.station", length: "00:01" }
];

const VARIABLE_LIBRARY = [
  { id: "current_station", nameKey: "broadcast.variable.current", descKey: "" },
  { id: "next_station", nameKey: "broadcast.variable.next", descKey: "" },
  { id: "terminal_station", nameKey: "broadcast.variable.terminal", descKey: "" }
];

const DELAY_LIBRARY = [
  { id: "delay_03", nameKey: "broadcast.delay.03", descKey: "broadcast.delay.label" },
  { id: "delay_05", nameKey: "broadcast.delay.05", descKey: "broadcast.delay.label" },
  { id: "delay_08", nameKey: "broadcast.delay.08", descKey: "broadcast.delay.label" },
  { id: "delay_10", nameKey: "broadcast.delay.10", descKey: "broadcast.delay.label" },
  { id: "delay_20", nameKey: "broadcast.delay.20", descKey: "broadcast.delay.label" }
];

const TRIGGER_OPTIONS = [
  { id: "approach_station", labelKey: "broadcast.trigger.approachStation" },
  { id: "stop_and_open", labelKey: "broadcast.trigger.stopAndOpen" },
  { id: "departure_close", labelKey: "broadcast.trigger.departureClose" },
  { id: "leave_station", labelKey: "broadcast.trigger.leaveStation" },
  { id: "mid_route", labelKey: "broadcast.trigger.midRoute" }
];

const LINE_OPTIONS = [
  { id: "line_1_main", labelKey: "broadcast.line.main" },
  { id: "airport_express", labelKey: "broadcast.line.airport" },
  { id: "loop_test", labelKey: "broadcast.line.loop" }
];

const EXTERNAL_ASSET_FILE_SYSTEM = {
  "C:\\Mods\\Audio\\": {
    folders: ["BGM", "SFX", "Voice_Packs"],
    files: [
      { id: "root_f1", name: "Global_Config_Ping.wav" }
    ]
  },
  "C:\\Mods\\Audio\\BGM\\": {
    folders: [],
    files: [
      { id: "bgm_1", name: "Ambient_City.wav" },
      { id: "bgm_2", name: "Menu_Theme.ogg" }
    ]
  },
  "C:\\Mods\\Audio\\SFX\\": {
    folders: ["Vehicles", "UI"],
    files: []
  },
  "C:\\Mods\\Audio\\SFX\\Vehicles\\": {
    folders: [],
    files: [
      { id: "veh_1", name: "Train_Whistle.ogg" },
      { id: "veh_2", name: "Bus_Brake.wav" }
    ]
  },
  "C:\\Mods\\Audio\\SFX\\UI\\": {
    folders: [],
    files: [
      { id: "ui_1", name: "Notification_Ping.mp3" }
    ]
  },
  "C:\\Mods\\Audio\\Voice_Packs\\": {
    folders: [],
    files: [
      { id: "vp_1", name: "Station_Jingmai.wav" },
      { id: "vp_2", name: "Next_Stop_Is.wav" }
    ]
  }
};

const DEFAULT_EXTERNAL_ASSET_PATH = "C:\\Mods\\Audio\\";

const TAB_TRANSITION_MS = 300;
const INLINE_PANEL_TRANSITION_MS = 650;
const INLINE_PANEL_EASING = "cubic-bezier(0.19, 1, 0.22, 1)";
const PAGE_ENTER_ANIMATION_MS = 850;

function splitIntoColumns(items, count = 2) {
  const columns = Array.from({ length: count }, () => []);
  items.forEach((item, index) => {
    columns[index % count].push(item);
  });
  return columns;
}

function SearchIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-tool-icon">
      <circle cx="11" cy="11" r="7" />
      <line x1="20" y1="20" x2="16.65" y2="16.65" />
    </svg>
  );
}

function FilterIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-tool-icon">
      <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
    </svg>
  );
}

function PlusIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-action-icon">
      <line x1="12" y1="5" x2="12" y2="19" />
      <line x1="5" y1="12" x2="19" y2="12" />
    </svg>
  );
}

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-close-icon">
      <line x1="18" y1="6" x2="6" y2="18" />
      <line x1="6" y1="6" x2="18" y2="18" />
    </svg>
  );
}

function PlayIcon() {
  return (
    <svg viewBox="0 0 24 24" className="dw-bc-icon dw-bc-play-icon is-fill">
      <polygon points="8 5 19 12 8 19 8 5" />
    </svg>
  );
}

function ArrowLeftIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <line x1="19" y1="12" x2="5" y2="12" />
      <polyline points="12 19 5 12 12 5" />
    </svg>
  );
}

function FolderIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <path d="M3 7h6l2 2h10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z" />
      <path d="M3 7V6a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v1" />
    </svg>
  );
}

function ReturnUpIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <polyline points="9 10 4 15 9 20" />
      <path d="M20 4v8a3 3 0 0 1-3 3H4" />
    </svg>
  );
}

function FileAudioIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <path d="M14 2H7a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V7z" />
      <polyline points="14 2 14 7 19 7" />
      <path d="M10 16a2 2 0 1 0 2 2v-5l4-1v4a2 2 0 1 0 2 2v-7l-8 2z" />
    </svg>
  );
}

function SquareIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <rect x="4" y="4" width="16" height="16" rx="1.5" ry="1.5" />
    </svg>
  );
}

function CheckSquareIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon ${className}`.trim()}>
      <rect x="4" y="4" width="16" height="16" rx="1.5" ry="1.5" />
      <polyline points="8 12 11 15 16 9" />
    </svg>
  );
}

function DatabaseIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon dw-bc-node-icon is-variable ${className}`.trim()}>
      <ellipse cx="12" cy="5" rx="8" ry="3" />
      <path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5" />
      <path d="M4 11v8c0 1.7 3.6 3 8 3s8-1.3 8-3v-8" />
    </svg>
  );
}

function SpeakerIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon dw-bc-node-icon ${className}`.trim()}>
      <polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5" />
      <path d="M15.5 8.5a5 5 0 0 1 0 7" />
      <path d="M18.5 5.5a9 9 0 0 1 0 13" />
    </svg>
  );
}

function DelayIcon({ className = "" }) {
  return (
    <svg viewBox="0 0 24 24" className={`dw-bc-icon dw-bc-node-icon is-delay ${className}`.trim()}>
      <circle cx="12" cy="12" r="8" />
      <path d="M12 8v4l2.5 2.5" />
    </svg>
  );
}

function AnimatedInlinePanel({ visible, className, panelRef, children }) {
  const contentRef = useRef(null);
  const outerRef = useRef(null);
  const [panelHeight, setPanelHeight] = useState(0);

  useLayoutEffect(() => {
    if (!contentRef.current) {
      return undefined;
    }

    function syncHeight() {
      if (!contentRef.current) {
        return;
      }
      setPanelHeight(contentRef.current.scrollHeight);
    }

    syncHeight();

    if (!visible || typeof ResizeObserver === "undefined") {
      return undefined;
    }

    const observer = new ResizeObserver(() => {
      syncHeight();
    });
    observer.observe(contentRef.current);

    return () => observer.disconnect();
  }, [children, visible]);

  useEffect(() => {
    if (!panelRef) {
      return undefined;
    }

    const node = outerRef.current;
    if (visible && node) {
      if (typeof panelRef === "function") {
        panelRef(node);
      } else {
        panelRef.current = node;
      }
      return undefined;
    }

    if (typeof panelRef === "function") {
      panelRef(null);
    } else if (panelRef.current === node) {
      panelRef.current = null;
    }

    return undefined;
  }, [panelRef, visible]);

  function handleOuterRef(node) {
    outerRef.current = node;
  }

  function handleContentRef(node) {
    contentRef.current = node;
  }

  return (
    <div
      ref={handleOuterRef}
      className={`dw-bc-animated-panel is-tray${visible ? " is-open" : " is-closed"}${className ? ` ${className}` : ""}`}
      style={{
        maxHeight: visible ? `${panelHeight}px` : "0px",
        overflow: "hidden",
        opacity: visible ? 1 : 0,
        pointerEvents: visible ? "auto" : "none",
        transition: `max-height ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}, opacity ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}`
      }}
    >
      <div
        className="dw-bc-animated-panel-inner"
        ref={handleContentRef}
        style={{
          opacity: visible ? 1 : 0,
          transform: visible ? "translateY(0)" : "translateY(-12px)",
          transition: `opacity ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}, transform ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}`
        }}
      >
        {children}
      </div>
    </div>
  );
}

function AnimatedFadePresence({ visible, className, children }) {
  const [shouldRender, setShouldRender] = useState(visible);
  const [stage, setStage] = useState(visible ? "entered" : "exited");

  useEffect(() => {
    let timer = null;
    let raf = null;

    if (visible) {
      setShouldRender(true);
      setStage("entering");
      raf = window.requestAnimationFrame(() => {
        setStage("entered");
      });
    } else if (shouldRender) {
      setStage("exiting");
      timer = window.setTimeout(() => {
        setShouldRender(false);
        setStage("exited");
      }, TAB_TRANSITION_MS);
    }

    return () => {
      if (raf) {
        window.cancelAnimationFrame(raf);
      }
      if (timer) {
        window.clearTimeout(timer);
      }
    };
  }, [shouldRender, visible]);

  if (!shouldRender) {
    return null;
  }

  return (
    <div className={`${className ? `${className} ` : ""}dw-bc-fade-presence is-${stage}`}>
      {children}
    </div>
  );
}

function BroadcastDropdown({
  open,
  setOpen,
  onSelect,
  options,
  value,
  label = "",
  className = "",
  triggerClassName = "",
  menuClassName = "",
  optionClassName = "",
  title = "",
  portalHostRef = null,
  menuWidth = null
}) {
  const triggerRef = useRef(null);
  const menuRef = useRef(null);
  const [menuRect, setMenuRect] = useState(null);

  useEffect(() => {
    if (!open) {
      return undefined;
    }

    function updateMenuRect() {
      const triggerElement = triggerRef.current;
      if (!(triggerElement instanceof HTMLElement)) {
        return;
      }

      const rect = triggerElement.getBoundingClientRect();
      setMenuRect({
        left: rect.left,
        top: rect.bottom,
        width: menuWidth || rect.width
      });
    }

    function handlePointerDown(event) {
      const triggerElement = triggerRef.current;
      const menuElement = menuRef.current;
      const target = event.target;
      if (
        (triggerElement && triggerElement.contains(target)) ||
        (menuElement && menuElement.contains(target))
      ) {
        return;
      }
      setOpen(false);
    }

    function handleKeyDown(event) {
      if (event.key === "Escape") {
        setOpen(false);
      }
    }

    updateMenuRect();
    window.addEventListener("resize", updateMenuRect);
    window.addEventListener("scroll", updateMenuRect, true);
    document.addEventListener("pointerdown", handlePointerDown, true);
    document.addEventListener("keydown", handleKeyDown, true);

    return () => {
      window.removeEventListener("resize", updateMenuRect);
      window.removeEventListener("scroll", updateMenuRect, true);
      document.removeEventListener("pointerdown", handlePointerDown, true);
      document.removeEventListener("keydown", handleKeyDown, true);
    };
  }, [menuWidth, open, setOpen]);

  const menuContent = open ? (
    <div
      ref={menuRef}
      className={`dw-demo-dropdown-menu ${portalHostRef?.current ? "is-portal" : ""} ${menuClassName}`.trim()}
      style={portalHostRef?.current && menuRect ? {
        left: `${menuRect.left}px`,
        top: `${menuRect.top}px`,
        width: `${menuRect.width}px`
      } : undefined}
    >
      {options.map((option) => (
        <button
          key={option.key || option.value || option.label}
          type="button"
          className={`dw-demo-dropdown-option ${option.active ? "is-active" : ""} ${optionClassName}`.trim()}
          onClick={() => {
            onSelect(option);
            setOpen(false);
          }}
        >
          {option.label}
        </button>
      ))}
    </div>
  ) : null;

  return (
    <div className={`dw-demo-field ${className}`.trim()} onClick={(event) => event.stopPropagation()}>
      {label ? (
        <label className="dw-demo-label">
          <span className="dw-demo-label-content">
            <span>{label}</span>
          </span>
        </label>
      ) : null}
      <div className="dw-demo-dropdown">
        <button
          ref={triggerRef}
          type="button"
          title={title || value}
          className={`dw-demo-input dw-demo-dropdown-trigger ${triggerClassName} ${open ? "is-open" : ""}`.trim()}
          onClick={() => setOpen((current) => !current)}
        >
          <span>{value}</span>
          <span className="dw-demo-dropdown-caret" aria-hidden="true">
            <svg viewBox="0 0 16 16" className="dw-demo-dropdown-caret-icon">
              <path d="M4.2 6.2 8 10l3.8-3.8" fill="none" stroke="#c7d4dc" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </span>
        </button>
        {!portalHostRef?.current ? menuContent : null}
      </div>
      {portalHostRef?.current && menuContent ? createPortal(menuContent, portalHostRef.current) : null}
    </div>
  );
}

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
  labels
}) {
  const isTrayVisible = trayContext?.ruleId === rule.id;
  const [displayAction, setDisplayAction] = useState(null);
  const assetColumns = splitIntoColumns(assetLibrary);
  const delayColumns = splitIntoColumns(delayLibrary);

  useEffect(() => {
    if (isTrayVisible && trayContext?.action) {
      setDisplayAction(trayContext.action);
    }
  }, [isTrayVisible, trayContext]);

  return (
    <div className={`dw-bc-rule ${removingRule ? "is-removing" : ""}`}>
      <div className="dw-bc-rule-head">
        <div>
          <h2>{rule.title}</h2>
          <p>{labels.triggerPrefix}{rule.trigger}</p>
        </div>
        <button type="button" className="dw-bc-link-muted" onClick={() => onRemoveRule(rule.id)}>{labels.removeRule}</button>
      </div>

      <div className="dw-bc-node-flow">
          {rule.nodes.map((node, index) => {
          const isEditing = trayContext?.ruleId === rule.id && trayContext?.action === node.id;
          const isRemoving = removingNodeIds[`${rule.id}:${node.id}`];
          return (
            <div key={node.id} className={`dw-bc-node-flow-item dw-bc-page-enter-slide ${isRemoving ? "is-removing" : ""}`} style={{ animationDelay: `${index * 0.08}s` }}>
              <div className="dw-bc-node-meta">
                <span className={`dw-bc-node-kind ${node.type === "variable" ? "is-variable" : ""}`}>
                  {node.type === "variable"
                    ? <DatabaseIcon className="dw-bc-node-kind-icon is-variable" />
                    : node.type === "delay"
                      ? <DelayIcon className="dw-bc-node-kind-icon is-delay" />
                      : <SpeakerIcon className="dw-bc-node-kind-icon is-asset" />}
                  {node.desc}
                </span>
                <div className="dw-bc-node-value-wrap">
                  <button
                    type="button"
                    className={`dw-bc-node-value ${node.type === "variable" ? "is-variable" : ""} ${isEditing ? "is-active" : ""}`}
                    onClick={() => onToggleTray(rule.id, node.id)}
                  >
                    {node.name}
                  </button>
                  <button type="button" className="dw-bc-node-remove" onClick={() => onRemoveNode(rule.id, node.id)}>
                    <CloseIcon />
                  </button>
                </div>
              </div>
              {index < rule.nodes.length - 1 ? <div className="dw-bc-node-sep">/</div> : null}
            </div>
          );
        })}

        <div className="dw-bc-node-flow-item is-add-action">
          {rule.nodes.length > 0 ? <div className="dw-bc-node-sep">/</div> : null}
          <button
            type="button"
            className={`dw-bc-node-add ${trayContext?.ruleId === rule.id && trayContext?.action === "add" ? "is-active" : ""}`}
            onClick={() => onToggleTray(rule.id, "add")}
          >
            <span className="dw-bc-inline-icon-shell">
              {trayContext?.ruleId === rule.id && trayContext?.action === "add" ? <CloseIcon /> : <PlusIcon />}
            </span>
            <span className="dw-bc-inline-button-copy">
              {trayContext?.ruleId === rule.id && trayContext?.action === "add" ? labels.cancelAddNode : labels.addNode}
            </span>
          </button>
        </div>
      </div>

      <AnimatedInlinePanel visible={isTrayVisible} panelRef={trayRef}>
        <div className="dw-bc-tray">
          <div className="dw-bc-tray-head">
            <span>{displayAction === "add" ? labels.addTrayTitle : labels.replaceTrayTitle}</span>
            <button type="button" className="dw-bc-icon-button" onClick={onCloseTray}>
              <CloseIcon />
            </button>
          </div>

          <div className="dw-bc-tray-tabs">
            <button type="button" className={`dw-bc-tray-tab ${trayCategory === "asset" ? "is-active" : ""}`} onClick={() => onSetTrayCategory("asset")}>
              <SpeakerIcon className="dw-bc-tray-tab-icon is-asset" />
              {labels.assetTab}
            </button>
            <button type="button" className={`dw-bc-tray-tab is-variable ${trayCategory === "variable" ? "is-active" : ""}`} onClick={() => onSetTrayCategory("variable")}>
              <DatabaseIcon className="dw-bc-tray-tab-icon is-variable" />
              {labels.variableTab}
            </button>
            <button type="button" className={`dw-bc-tray-tab is-delay ${trayCategory === "delay" ? "is-active" : ""}`} onClick={() => onSetTrayCategory("delay")}>
              <DelayIcon className="dw-bc-tray-tab-icon is-delay" />
              {labels.delayTab}
            </button>
          </div>

          {trayCategory === "asset" ? (
            <div className="dw-bc-tray-columns">
              {assetColumns.map((column, columnIndex) => (
                <div key={`asset-col-${columnIndex}`} className="dw-bc-tray-column">
                  {column.map((asset) => (
                    <button key={asset.name} type="button" className="dw-bc-tray-item anim-stagger-slide-up" style={{ animationDelay: `${assetLibrary.findIndex((entry) => entry.name === asset.name) * 0.05}s` }} onClick={() => onAddAsset(rule.id, asset)}>
                      <span>{asset.name}</span>
                      <span>{asset.desc}</span>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          ) : trayCategory === "variable" ? (
            <div className="dw-bc-tray-list">
              {variableLibrary.map((variable, index) => (
                <button key={variable.id} type="button" className="dw-bc-tray-item is-variable anim-stagger-slide-up" style={{ animationDelay: `${index * 0.05}s` }} onClick={() => onAddVariable(rule.id, variable)}>
                  <span>{variable.name}</span>
                </button>
              ))}
            </div>
          ) : (
            <div className="dw-bc-tray-columns">
              {delayColumns.map((column, columnIndex) => (
                <div key={`delay-col-${columnIndex}`} className="dw-bc-tray-column">
                  {column.map((delay) => (
                    <button key={delay.name} type="button" className="dw-bc-tray-item is-delay anim-stagger-slide-up" style={{ animationDelay: `${delayLibrary.findIndex((entry) => entry.name === delay.name) * 0.05}s` }} onClick={() => onAddDelay(rule.id, delay)}>
                      <span>{delay.name}</span>
                      <span>{delay.desc}</span>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          )}
        </div>
      </AnimatedInlinePanel>
    </div>
  );
}

export default function BroadcastWorkbenchPage({ pageEnterSequence = 0 }) {
  const { t } = useNativeScheduleI18n();
  const workbenchApi = useMemo(() => getWorkbenchApi(), []);
  const demoAssetLibrary = useMemo(
    () => ASSET_LIBRARY.map((asset) => ({ ...asset, desc: t(asset.descKey) })),
    [t]
  );
  const variableLibrary = useMemo(
    () => VARIABLE_LIBRARY.map((variable) => ({ ...variable, name: t(variable.nameKey), desc: variable.descKey ? t(variable.descKey) : "" })),
    [t]
  );
  const delayLibrary = useMemo(
    () => DELAY_LIBRARY.map((delay) => ({ ...delay, name: t(delay.nameKey), desc: t(delay.descKey) })),
    [t]
  );
  const triggerOptions = useMemo(
    () => TRIGGER_OPTIONS.map((option) => ({ ...option, label: t(option.labelKey) })),
    [t]
  );
  const fallbackLineOptions = useMemo(
    () => LINE_OPTIONS.map((line) => ({ ...line, label: t(line.labelKey) })),
    [t]
  );
  const defaultAsset = demoAssetLibrary[0];
  const nextStationAsset = demoAssetLibrary[1];
  const bantianAsset = demoAssetLibrary[3];
  const gongyeAsset = demoAssetLibrary[4];
  const [activeTab, setActiveTab] = useState("sequence");
  const [renderedTab, setRenderedTab] = useState("sequence");
  const [tabStage, setTabStage] = useState("entered");
  const [pageEnterState, setPageEnterState] = useState("entered");
  const [rules, setRules] = useState([
    {
      id: "r1",
      title: t("broadcast.rule.default.departureWarning"),
      trigger: t("broadcast.trigger.departureClose"),
      nodes: [
        { id: "1", type: "asset", name: defaultAsset.name, desc: defaultAsset.desc }
      ]
    },
    {
      id: "r2",
      title: t("broadcast.rule.default.afterDeparture"),
      trigger: t("broadcast.trigger.leaveStation"),
      nodes: [
        { id: "2", type: "asset", name: nextStationAsset.name, desc: nextStationAsset.desc },
        { id: "3", type: "variable", name: variableLibrary[1].name, desc: t("broadcast.node.dynamicVariable") }
      ]
    }
  ]);
  const [stations, setStations] = useState([
    { id: "L1-01", name: "工业区", audio: gongyeAsset.name, status: "ready" },
    { id: "L1-02", name: "坂田", audio: bantianAsset.name, status: "ready" },
    { id: "L1-03", name: "开发区", audio: null, status: "missing" }
  ]);
  const [trayContext, setTrayContext] = useState(null);
  const [trayCategory, setTrayCategory] = useState("asset");
  const [mappingTray, setMappingTray] = useState(null);
  const [catalogAssetLibrary, setCatalogAssetLibrary] = useState([]);
  const [hasCatalogHydrated, setHasCatalogHydrated] = useState(false);
  const [isAssetExplorerOpen, setIsAssetExplorerOpen] = useState(false);
  const [selectedExternalFiles, setSelectedExternalFiles] = useState([]);
  const [currentExternalPath, setCurrentExternalPath] = useState(DEFAULT_EXTERNAL_ASSET_PATH);
  const [lineOptions, setLineOptions] = useState(fallbackLineOptions);
  const [selectedLineId, setSelectedLineId] = useState(fallbackLineOptions[0]?.id ?? LINE_OPTIONS[0].id);
  const [lineDropdownOpen, setLineDropdownOpen] = useState(false);
  const [isCreatingRule, setIsCreatingRule] = useState(false);
  const [newRuleTitle, setNewRuleTitle] = useState("");
  const [newRuleTriggerId, setNewRuleTriggerId] = useState(TRIGGER_OPTIONS[0].id);
  const [triggerDropdownOpen, setTriggerDropdownOpen] = useState(false);
  const [removingRuleIds, setRemovingRuleIds] = useState({});
  const [removingNodeIds, setRemovingNodeIds] = useState({});
  const pageRootRef = useRef(null);
  const trayRef = useRef(null);
  const dropdownPortalHostRef = useRef(null);
  const removeTimersRef = useRef([]);
  const pageEnterTimerRef = useRef(null);
  const hasBroadcastHydratedRef = useRef(false);
  const lastHydratedLineIdRef = useRef("");
  const availableAssetLibrary = hasCatalogHydrated ? catalogAssetLibrary : demoAssetLibrary;
  const mappingAssetColumns = splitIntoColumns(availableAssetLibrary);
  const selectedLine = lineOptions.find((line) => line.id === selectedLineId) ?? lineOptions[0];
  const newRuleTrigger = triggerOptions.find((option) => option.id === newRuleTriggerId) ?? triggerOptions[0];
  const broadcastLabels = {
    sidebarTitle: t("broadcast.sidebar.title"),
    localAssets: t("broadcast.sidebar.localAssets"),
    assetFileName: t("broadcast.sidebar.fileName"),
    assetDuration: t("broadcast.sidebar.duration"),
    importAsset: t("broadcast.sidebar.import"),
    sequenceTab: t("broadcast.tabs.sequence"),
    mappingTab: t("broadcast.tabs.mapping"),
    lineLabel: t("broadcast.topbar.line"),
    createRule: t("broadcast.createRule.button"),
    createRuleTitle: t("broadcast.createRule.title"),
    ruleNameLabel: t("broadcast.createRule.name"),
    ruleNamePlaceholder: t("broadcast.createRule.namePlaceholder"),
    triggerLabel: t("broadcast.createRule.trigger"),
    saveRule: t("broadcast.createRule.save"),
    defaultRuleDepartureWarning: t("broadcast.rule.default.departureWarning"),
    defaultRuleAfterDeparture: t("broadcast.rule.default.afterDeparture"),
    mappingTitle: t("broadcast.mapping.title"),
    autoBind: t("broadcast.mapping.autoBind"),
    mapLineHead: t("broadcast.mapping.head.line"),
    mapStationHead: t("broadcast.mapping.head.station"),
    mapAudioHead: t("broadcast.mapping.head.audio"),
    mapStatusHead: t("broadcast.mapping.head.status"),
    mapMissing: t("broadcast.mapping.missing"),
    mapChooseAudio: t("broadcast.mapping.chooseAudio"),
    mapReady: t("broadcast.mapping.ready"),
    mapBindTitle: t("broadcast.mapping.bindTitle", { station: "{station}" }),
    applyConfig: t("broadcast.footer.apply"),
    removeRule: t("broadcast.rule.remove"),
    triggerPrefix: t("broadcast.rule.triggerPrefix"),
    dynamicVariable: t("broadcast.node.dynamicVariable"),
    delayNode: t("broadcast.node.delay"),
    addNode: t("broadcast.node.add"),
    cancelAddNode: t("broadcast.node.cancelAdd"),
    addTrayTitle: t("broadcast.tray.addTitle"),
    replaceTrayTitle: t("broadcast.tray.replaceTitle"),
    assetTab: t("broadcast.tray.tab.asset"),
    variableTab: t("broadcast.tray.tab.variable"),
    delayTab: t("broadcast.tray.tab.delay")
  };

  function applyBroadcastSnapshot(snapshot) {
    const sourceLines = Array.isArray(snapshot?.lines) ? snapshot.lines : [];
    const backendLines = Array.isArray(snapshot?.lines)
      ? sourceLines
        .filter((line) => line && typeof line.id === "string" && line.id)
        .map((line) => ({
          id: line.id,
          label: typeof line.name === "string" && line.name ? line.name : line.id
        }))
      : [];
    const nextLineOptions = backendLines.length > 0 ? backendLines : fallbackLineOptions;
    const fallbackSelectedLineId = nextLineOptions[0]?.id ?? "";
    const nextSelectedLineId =
      typeof snapshot?.selectedLineId === "string"
      && nextLineOptions.some((line) => line.id === snapshot.selectedLineId)
        ? snapshot.selectedLineId
        : fallbackSelectedLineId;

    setLineOptions(nextLineOptions);
    lastHydratedLineIdRef.current = nextSelectedLineId;
    setSelectedLineId(nextSelectedLineId);

    if (Array.isArray(snapshot?.assets)) {
      setCatalogAssetLibrary(
        snapshot.assets
          .filter((asset) => asset && typeof asset.name === "string" && asset.name)
          .map((asset) => ({
            name: asset.name,
            desc: asset.desc || asset.extension || "",
            length: asset.length || ""
          }))
      );
      setHasCatalogHydrated(true);
    }

    if (!Array.isArray(snapshot?.stations)) {
      return;
    }

    setStations((current) =>
      snapshot.stations.map((station) => {
        const existing = current.find((entry) => entry.id === station.id || entry.name === station.name);
        return {
          id: station.id,
          name: station.name,
          audio: existing?.audio ?? null,
          status: existing?.status ?? "missing"
        };
      })
    );
  }

  useEffect(() => () => {
    removeTimersRef.current.forEach((timer) => window.clearTimeout(timer));
    removeTimersRef.current = [];
  }, []);

  useEffect(() => {
    let disposed = false;

    async function hydrateBroadcastSnapshot() {
      try {
        const snapshot = await workbenchApi.loadSnapshot?.();
        if (!disposed) {
          applyBroadcastSnapshot(snapshot);
          hasBroadcastHydratedRef.current = true;
        }
      } catch (error) {
        if (!disposed) {
          console.error("[RT Broadcast Workbench] backend hydrate failed", error);
        }
      }
    }

    hydrateBroadcastSnapshot();
    const unsubscribe = workbenchApi.onBroadcastSnapshotChanged?.((snapshot) => {
      if (!disposed) {
        applyBroadcastSnapshot(snapshot);
      }
    });

    return () => {
      disposed = true;
      unsubscribe?.();
    };
  }, [workbenchApi]);

  useEffect(() => {
    if (!hasBroadcastHydratedRef.current) {
      return undefined;
    }

    if (!selectedLineId || selectedLineId === lastHydratedLineIdRef.current) {
      return undefined;
    }

    let disposed = false;

    async function refreshBroadcastSnapshot() {
      try {
        const snapshot = await workbenchApi.refreshBroadcastSnapshot?.(selectedLineId);
        if (!disposed) {
          applyBroadcastSnapshot(snapshot);
        }
      } catch (error) {
        try {
          const fallbackSnapshot = await workbenchApi.refreshSnapshot?.();
          if (!disposed) {
            applyBroadcastSnapshot(fallbackSnapshot);
          }
        } catch (fallbackError) {
          if (!disposed) {
            console.error("[RT Broadcast Workbench] backend refresh failed", error, fallbackError);
          }
        }
      }
    }

    refreshBroadcastSnapshot();

    return () => {
      disposed = true;
    };
  }, [selectedLineId, workbenchApi]);

  useLayoutEffect(() => {
    if (pageEnterSequence <= 0) {
      return undefined;
    }

    let outerRaf = 0;
    let innerRaf = 0;
    let cancelled = false;
    let attempts = 0;

    function isPageVisible() {
      const rootNode = pageRootRef.current;
      if (!(rootNode instanceof HTMLElement)) {
        return false;
      }

      const hostPage = rootNode.closest(".dw-native-workbench-page");
      const targetNode = hostPage instanceof HTMLElement ? hostPage : rootNode;
      const rect = targetNode.getBoundingClientRect();
      const computedStyle = window.getComputedStyle(targetNode);

      return (
        computedStyle.visibility !== "hidden" &&
        computedStyle.display !== "none" &&
        rect.width > 0 &&
        rect.height > 0
      );
    }

    function startWhenVisible() {
      outerRaf = window.requestAnimationFrame(() => {
        innerRaf = window.requestAnimationFrame(() => {
          if (cancelled) {
            return;
          }

          if (!isPageVisible() && attempts < 6) {
            attempts += 1;
            startWhenVisible();
            return;
          }

          setPageEnterState("playing");
          pageEnterTimerRef.current = window.setTimeout(() => {
            setPageEnterState("entered");
            pageEnterTimerRef.current = null;
          }, PAGE_ENTER_ANIMATION_MS);
        });
      });
    }

    if (pageEnterTimerRef.current) {
      window.clearTimeout(pageEnterTimerRef.current);
      pageEnterTimerRef.current = null;
    }

    setPageEnterState("armed");
    startWhenVisible();

    return () => {
      cancelled = true;
      if (outerRaf) {
        window.cancelAnimationFrame(outerRaf);
      }
      if (innerRaf) {
        window.cancelAnimationFrame(innerRaf);
      }
      if (pageEnterTimerRef.current) {
        window.clearTimeout(pageEnterTimerRef.current);
        pageEnterTimerRef.current = null;
      }
    };
  }, [pageEnterSequence]);

  useEffect(() => {
    if (!(trayContext || mappingTray) || !trayRef.current || typeof trayRef.current.scrollIntoView !== "function") {
      return;
    }

    const timer = window.setTimeout(() => {
      trayRef.current?.scrollIntoView({ block: "nearest" });
    }, 100);

    return () => window.clearTimeout(timer);
  }, [mappingTray, trayContext]);

  useEffect(() => {
    if (activeTab === renderedTab) {
      return undefined;
    }

    setTabStage("exiting");
    const timer = window.setTimeout(() => {
      setRenderedTab(activeTab);
      setTabStage("entering");
      const raf = window.requestAnimationFrame(() => {
        setTabStage("entered");
      });
      return () => window.cancelAnimationFrame(raf);
    }, TAB_TRANSITION_MS);

    return () => window.clearTimeout(timer);
  }, [activeTab, renderedTab]);

  function closeInlineMenus() {
    setTriggerDropdownOpen(false);
    setLineDropdownOpen(false);
  }

  function handleRootClick() {
    closeInlineMenus();
  }

  function toggleTray(ruleId, action) {
    closeInlineMenus();
    setMappingTray(null);
    if (trayContext?.ruleId === ruleId && trayContext?.action === action) {
      setTrayContext(null);
      return;
    }
    if (action === "add") {
      setTrayCategory("asset");
    } else {
      const targetRule = rules.find((rule) => rule.id === ruleId);
      const targetNode = targetRule?.nodes.find((node) => node.id === action);
      setTrayCategory(
        targetNode?.type === "variable"
          ? "variable"
          : targetNode?.type === "delay"
            ? "delay"
            : "asset"
      );
    }
    setTrayContext({ ruleId, action });
  }

  function handleAddNodeToRule(ruleId, nodeTemplate) {
    setRules((current) =>
      current.map((rule) => {
        if (rule.id !== ruleId) {
          return rule;
        }

        if (trayContext?.action && trayContext.action !== "add") {
          return {
            ...rule,
            nodes: rule.nodes.map((node) =>
              node.id === trayContext.action ? { ...nodeTemplate, id: node.id } : node
            )
          };
        }

        return {
          ...rule,
          nodes: [...rule.nodes, { ...nodeTemplate, id: `${Date.now()}-${Math.random().toString(36).slice(2, 6)}` }]
        };
      })
    );
    setTrayContext(null);
  }

  function handleRemoveNode(ruleId, nodeId) {
    const removalKey = `${ruleId}:${nodeId}`;
    if (removingNodeIds[removalKey]) {
      return;
    }

    setRemovingNodeIds((current) => ({ ...current, [removalKey]: true }));
    if (trayContext?.action === nodeId) {
      setTrayContext(null);
    }

    const timer = window.setTimeout(() => {
      setRules((current) =>
        current.map((rule) =>
          rule.id === ruleId ? { ...rule, nodes: rule.nodes.filter((node) => node.id !== nodeId) } : rule
        )
      );
      setRemovingNodeIds((current) => {
        const next = { ...current };
        delete next[removalKey];
        return next;
      });
    }, 220);

    removeTimersRef.current.push(timer);
  }

  function handleRemoveRule(ruleId) {
    if (removingRuleIds[ruleId]) {
      return;
    }

    setRemovingRuleIds((current) => ({ ...current, [ruleId]: true }));
    if (trayContext?.ruleId === ruleId) {
      setTrayContext(null);
    }

    const timer = window.setTimeout(() => {
      setRules((current) => current.filter((rule) => rule.id !== ruleId));
      setRemovingRuleIds((current) => {
        const next = { ...current };
        delete next[ruleId];
        return next;
      });
    }, 220);

    removeTimersRef.current.push(timer);
  }

  function handleCreateRule() {
    if (!newRuleTitle.trim()) {
      return;
    }

    setRules((current) => [
      ...current,
      {
        id: Date.now().toString(),
        title: newRuleTitle.trim(),
        trigger: newRuleTrigger.label,
        nodes: []
      }
    ]);
    setIsCreatingRule(false);
    setNewRuleTitle("");
    setNewRuleTriggerId(TRIGGER_OPTIONS[0].id);
    setTriggerDropdownOpen(false);
  }

  function handleBindStation(stationId, assetName) {
    setStations((current) =>
      current.map((station) => (station.id === stationId ? { ...station, audio: assetName, status: "ready" } : station))
    );
    setMappingTray(null);
  }

  function handleImportAssetDirectory() {
    setIsAssetExplorerOpen(true);
  }

  function handleCloseAssetExplorer() {
    setIsAssetExplorerOpen(false);
    setSelectedExternalFiles([]);
    setCurrentExternalPath(DEFAULT_EXTERNAL_ASSET_PATH);
  }

  function handleExternalPathChange(path) {
    if (EXTERNAL_ASSET_FILE_SYSTEM[path]) {
      setCurrentExternalPath(path);
    }
  }

  function handleExternalBack() {
    if (currentExternalPath === DEFAULT_EXTERNAL_ASSET_PATH) {
      return;
    }

    const parts = currentExternalPath.split("\\").filter(Boolean);
    if (parts.length <= 1) {
      return;
    }

    parts.pop();
    const nextPath = `${parts.join("\\")}\\`;
    if (EXTERNAL_ASSET_FILE_SYSTEM[nextPath]) {
      setCurrentExternalPath(nextPath);
    }
  }

  function handleToggleExternalFile(fileId) {
    setSelectedExternalFiles((current) =>
      current.includes(fileId)
        ? current.filter((id) => id !== fileId)
        : [...current, fileId]
    );
  }

  function handleToggleAllExternalFiles() {
    const currentViewFiles = EXTERNAL_ASSET_FILE_SYSTEM[currentExternalPath]?.files || [];
    const currentViewIds = currentViewFiles.map((file) => file.id);
    const allSelected = currentViewIds.length > 0 && currentViewIds.every((id) => selectedExternalFiles.includes(id));

    if (allSelected) {
      setSelectedExternalFiles((current) => current.filter((id) => !currentViewIds.includes(id)));
      return;
    }

    setSelectedExternalFiles((current) => Array.from(new Set([...current, ...currentViewIds])));
  }

  return (
    <div ref={pageRootRef} className={`dw-bc-page is-page-enter-${pageEnterState}`} onClick={handleRootClick}>
      <div className="dw-bc-shell">
        <div className="dw-bc-shell-entry dw-bc-page-enter-shell origin-bottom">
        <aside className="dw-bc-sidebar">
          <header className="dw-bc-sidebar-head">
            <h1>{broadcastLabels.sidebarTitle}</h1>
          </header>

          <div className="dw-bc-sidebar-tools">
            <span>{broadcastLabels.localAssets}</span>
          </div>

          <div className="dw-bc-asset-head">
            <div>{broadcastLabels.assetFileName}</div>
            <div>{broadcastLabels.assetDuration}</div>
          </div>

          <div className="dw-bc-asset-list">
            {availableAssetLibrary.map((asset, index) => (
              <div key={asset.name} className="dw-bc-asset-row dw-bc-page-enter-slide" style={{ animationDelay: `${index * 0.05}s` }}>
                <div className="dw-bc-asset-play"><PlayIcon /></div>
                <div className="dw-bc-asset-copy">
                  <div className="dw-bc-asset-name">{asset.name}</div>
                  <div className="dw-bc-asset-desc">{asset.desc}</div>
                </div>
                <div className="dw-bc-asset-time">{asset.length}</div>
              </div>
            ))}
          </div>

          <footer className="dw-bc-sidebar-foot">
            <button type="button" className="dw-bc-outline-button" onClick={handleImportAssetDirectory}>
              <span className="dw-bc-inline-icon-shell dw-bc-outline-button-icon-shell">
                <PlusIcon />
              </span>
              <span className="dw-bc-inline-button-copy">{broadcastLabels.importAsset}</span>
            </button>
          </footer>
        </aside>

        <section className="dw-bc-main">
          <div className="dw-bc-main-header">
            <div className="dw-bc-tabs">
              <button type="button" className={`dw-bc-tab ${activeTab === "sequence" ? "is-active" : ""}`} onClick={() => { setActiveTab("sequence"); setMappingTray(null); }}>
                {broadcastLabels.sequenceTab}
              </button>
              <button type="button" className={`dw-bc-tab ${activeTab === "mapping" ? "is-active" : ""}`} onClick={() => { setActiveTab("mapping"); setTrayContext(null); }}>
                {broadcastLabels.mappingTab}
              </button>
            </div>

            <div className="dw-bc-main-tools">
              <BroadcastDropdown
                open={lineDropdownOpen}
                setOpen={(next) => {
                setTriggerDropdownOpen(false);
                  setLineDropdownOpen(next);
                }}
                onSelect={(option) => {
                  setSelectedLineId(option.value);
                  setLineDropdownOpen(false);
                }}
                options={lineOptions.map((line) => ({
                  key: line.id,
                  value: line.id,
                  label: line.label,
                  active: line.id === selectedLineId
                }))}
                value={selectedLine.label}
                label={broadcastLabels.lineLabel}
                className="dw-bc-line-picker is-line"
                title={selectedLine.label}
                portalHostRef={dropdownPortalHostRef}
              />
            </div>
          </div>

          <div className="dw-bc-body">
            <div className="dw-bc-body-pad">
              <div className={`dw-bc-tab-panel is-${tabStage}`}>
                <div className="dw-bc-scene-entry dw-bc-page-enter-scene origin-bottom">
                {renderedTab === "sequence" ? (
                  <div className={`dw-bc-tab-scene dw-bc-tab-scene-sequence is-${tabStage}`}>
                  {rules.map((rule, index) => (
                    <div key={rule.id} className="dw-bc-page-enter-slide-up" style={{ animationDelay: `${index * 0.15}s` }}>
                      <SequenceRule
                        rule={rule}
                        trayContext={trayContext}
                        trayCategory={trayCategory}
                        removingRule={Boolean(removingRuleIds[rule.id])}
                        removingNodeIds={removingNodeIds}
                        onToggleTray={toggleTray}
                        onRemoveNode={handleRemoveNode}
                        onRemoveRule={handleRemoveRule}
                        onSetTrayCategory={setTrayCategory}
                        onCloseTray={() => setTrayContext(null)}
                        onAddAsset={(ruleId, asset) => handleAddNodeToRule(ruleId, { name: asset.name, desc: asset.desc, type: "asset" })}
                        onAddVariable={(ruleId, variable) => handleAddNodeToRule(ruleId, { name: variable.name, desc: broadcastLabels.dynamicVariable, type: "variable" })}
                        onAddDelay={(ruleId, delay) => handleAddNodeToRule(ruleId, { name: delay.name, desc: broadcastLabels.delayNode, type: "delay" })}
                        trayRef={trayRef}
                        assetLibrary={availableAssetLibrary}
                        variableLibrary={variableLibrary}
                        delayLibrary={delayLibrary}
                        labels={broadcastLabels}
                      />
                    </div>
                  ))}
                  <div className="dw-bc-create-block">
                    <div className={`dw-bc-create-button-shell ${isCreatingRule ? "is-hidden" : "is-visible"}`}>
                      <button type="button" className="dw-bc-create-button" onClick={() => { setIsCreatingRule(true); setTrayContext(null); setMappingTray(null); }}>
                        <span className="dw-bc-create-button-icon-shell">
                          <PlusIcon />
                        </span>
                        <span className="dw-bc-create-button-copy">{broadcastLabels.createRule}</span>
                      </button>
                    </div>
                    <AnimatedInlinePanel visible={isCreatingRule} className="dw-bc-create-panel">
                      <div className="dw-bc-create-form">
                        <button type="button" className="dw-bc-icon-button is-corner" onClick={() => { setIsCreatingRule(false); setTriggerDropdownOpen(false); }}>
                          <CloseIcon />
                        </button>
                        <h3>{broadcastLabels.createRuleTitle}</h3>
                        <div className="dw-bc-form-field">
                          <label>{broadcastLabels.ruleNameLabel}</label>
                          <input type="text" value={newRuleTitle} placeholder={broadcastLabels.ruleNamePlaceholder} onClick={(event) => event.stopPropagation()} onChange={(event) => setNewRuleTitle(event.target.value)} />
                        </div>
                        <div className="dw-bc-form-field is-dropdown">
                          <label>{broadcastLabels.triggerLabel}</label>
                          <BroadcastDropdown
                            open={triggerDropdownOpen}
                            setOpen={(next) => {
                              setLineDropdownOpen(false);
                              setTriggerDropdownOpen(next);
                            }}
                            onSelect={(option) => {
                              setNewRuleTriggerId(option.value);
                              setTriggerDropdownOpen(false);
                            }}
                            options={triggerOptions.map((option) => ({
                              key: option.id,
                              value: option.id,
                              label: option.label,
                              active: option.id === newRuleTriggerId
                            }))}
                            value={newRuleTrigger.label}
                            className="dw-bc-form-dropdown"
                            portalHostRef={dropdownPortalHostRef}
                          />
                        </div>
                        <button type="button" className="dw-bc-primary-button" onClick={handleCreateRule}>
                          {broadcastLabels.saveRule}
                        </button>
                      </div>
                    </AnimatedInlinePanel>
                  </div>
                  </div>
                ) : (
                  <div className={`dw-bc-mapping dw-bc-tab-scene dw-bc-tab-scene-mapping is-${tabStage}`}>
                    <div className="dw-bc-mapping-head">
                      <div>
                        <h2>{broadcastLabels.mappingTitle}</h2>
                      </div>
                      <button type="button" className="dw-bc-secondary-button">{broadcastLabels.autoBind}</button>
                    </div>

                    <div className="dw-bc-map-table-head">
                      <div className="dw-bc-map-col is-id">{broadcastLabels.mapLineHead}</div>
                      <div className="dw-bc-map-col is-name">{broadcastLabels.mapStationHead}</div>
                      <div className="dw-bc-map-col is-audio">{broadcastLabels.mapAudioHead}</div>
                      <div className="dw-bc-map-col is-status">{broadcastLabels.mapStatusHead}</div>
                    </div>

                    {stations.map((station, index) => {
                      const isMissing = station.status === "missing";
                      const isMappingTrayVisible = mappingTray === station.id;
                      return (
                        <div key={station.id}>
                          <div className="dw-bc-map-row dw-bc-page-enter-slide-up" style={{ animationDelay: `${index * 0.08}s` }}>
                            <div className={`dw-bc-map-id dw-bc-map-col is-id ${isMissing ? "is-missing" : ""}`}>{selectedLine.label}</div>
                            <div className={`dw-bc-map-name dw-bc-map-col is-name ${isMissing ? "is-missing" : ""}`}>{station.name}</div>
                            <div className="dw-bc-map-col is-audio">
                              {station.audio ? (
                                <button type="button" className="dw-bc-audio-link" onClick={() => { setMappingTray(isMappingTrayVisible ? null : station.id); setTrayContext(null); }}>
                                  <SpeakerIcon />
                                  {station.audio}
                                </button>
                              ) : (
                                <span className="dw-bc-map-missing">{broadcastLabels.mapMissing}</span>
                              )}
                            </div>
                            <div className="dw-bc-map-status dw-bc-map-col is-status">
                              {station.status === "ready" ? (
                                <span className="dw-bc-ready">{broadcastLabels.mapReady}</span>
                              ) : (
                                <button type="button" className="dw-bc-map-button" onClick={() => { setMappingTray(isMappingTrayVisible ? null : station.id); setTrayContext(null); }}>
                                  {broadcastLabels.mapChooseAudio}
                                </button>
                              )}
                            </div>
                          </div>

                          <AnimatedInlinePanel visible={isMappingTrayVisible} panelRef={trayRef}>
                            <div className={`dw-bc-tray ${isMissing ? "is-missing" : ""}`}>
                              <div className="dw-bc-tray-head">
                                <span>{t("broadcast.mapping.bindTitle", { station: station.name })}</span>
                                <button type="button" className="dw-bc-icon-button" onClick={() => setMappingTray(null)}>
                                  <CloseIcon />
                                </button>
                              </div>
                              <div className="dw-bc-tray-columns">
                                {mappingAssetColumns.map((column, columnIndex) => (
                                  <div key={`mapping-col-${columnIndex}`} className="dw-bc-tray-column">
                                    {column.map((asset) => (
                                      <button key={asset.name} type="button" className="dw-bc-tray-item anim-stagger-slide-up" style={{ animationDelay: `${availableAssetLibrary.findIndex((entry) => entry.name === asset.name) * 0.05}s` }} onClick={() => handleBindStation(station.id, asset.name)}>
                                        <span>{asset.name}</span>
                                        <span>{asset.desc}</span>
                                      </button>
                                    ))}
                                  </div>
                                ))}
                              </div>
                            </div>
                          </AnimatedInlinePanel>
                        </div>
                      );
                    })}
                  </div>
                )}
                </div>
              </div>
            </div>
          </div>

          <footer className="dw-bc-footer">
            <button type="button" className="dw-bc-primary-button is-footer">{broadcastLabels.applyConfig}</button>
          </footer>
        </section>
        </div>
        {isAssetExplorerOpen ? (
          <div className="dw-bc-import-overlay">
            <div className="dw-bc-import-head">
              <button type="button" className="dw-bc-import-back-button" onClick={handleCloseAssetExplorer}>
                <ArrowLeftIcon className="dw-bc-import-back-icon" />
              </button>
              <div className="dw-bc-import-head-copy">
                <span className="dw-bc-import-head-title">{broadcastLabels.importAsset}</span>
                <span className="dw-bc-import-head-subtitle">本地磁盘 / 音频库 / 扫描到 2 个新音效</span>
              </div>
            </div>

            <div className="dw-bc-import-toolbar">
              <button
                type="button"
                className={`dw-bc-import-parent-button ${currentExternalPath === DEFAULT_EXTERNAL_ASSET_PATH ? "is-disabled" : ""}`}
                onClick={handleExternalBack}
              >
                <span className="dw-bc-import-parent-icon-shell">
                  <ReturnUpIcon className="dw-bc-import-parent-icon" />
                </span>
                <span>返回上一层</span>
              </button>

              <div className="dw-bc-import-breadcrumbs">
                <span className="dw-bc-import-breadcrumb-icon-shell">
                  <FolderIcon className="dw-bc-import-breadcrumb-icon" />
                </span>
                <div className="dw-bc-import-breadcrumb-copy">
                  {currentExternalPath.split("\\").filter(Boolean).map((part, index, parts) => {
                    const buildPath = `${parts.slice(0, index + 1).join("\\")}\\`;
                    const isLast = index === parts.length - 1;
                    return (
                      <div key={buildPath} className="dw-bc-import-breadcrumb-part">
                        <button
                          type="button"
                          className={`dw-bc-import-breadcrumb-button ${isLast ? "is-current" : ""}`}
                          onClick={() => handleExternalPathChange(buildPath)}
                        >
                          {part}
                        </button>
                        {isLast ? null : <span className="dw-bc-import-breadcrumb-sep">\</span>}
                      </div>
                    );
                  })}
                </div>
              </div>

              <div className="dw-bc-import-toolbar-spacer" />

              <button type="button" className="dw-bc-import-select-all" onClick={handleToggleAllExternalFiles}>
                <span className="dw-bc-import-select-all-icon-shell">
                  {(EXTERNAL_ASSET_FILE_SYSTEM[currentExternalPath]?.files || []).length > 0
                  && (EXTERNAL_ASSET_FILE_SYSTEM[currentExternalPath]?.files || []).every((file) => selectedExternalFiles.includes(file.id))
                    ? <CheckSquareIcon className="dw-bc-import-select-all-icon is-checked" />
                    : <SquareIcon className="dw-bc-import-select-all-icon" />}
                </span>
                <span>全选本项目</span>
              </button>
            </div>

            <div className="dw-bc-import-body">
              <div className="dw-bc-import-grid">
                {(EXTERNAL_ASSET_FILE_SYSTEM[currentExternalPath]?.folders || []).map((folderName) => (
                  <button
                    key={folderName}
                    type="button"
                    className="dw-bc-import-card is-folder"
                    onClick={() => handleExternalPathChange(`${currentExternalPath}${folderName}\\`)}
                  >
                    <span className="dw-bc-import-card-icon-shell">
                      <FolderIcon className="dw-bc-import-card-folder-icon" />
                    </span>
                    <span className="dw-bc-import-card-name">{folderName}</span>
                  </button>
                ))}

                {(EXTERNAL_ASSET_FILE_SYSTEM[currentExternalPath]?.files || []).map((file) => {
                  const isSelected = selectedExternalFiles.includes(file.id);
                  return (
                    <button
                      key={file.id}
                      type="button"
                      className={`dw-bc-import-card is-file ${isSelected ? "is-selected" : ""}`}
                      onClick={() => handleToggleExternalFile(file.id)}
                    >
                      <span className="dw-bc-import-card-check-shell">
                        {isSelected
                          ? <CheckSquareIcon className="dw-bc-import-card-check-icon is-checked" />
                          : <SquareIcon className="dw-bc-import-card-check-icon" />}
                      </span>
                      <span className="dw-bc-import-card-icon-shell">
                        <FileAudioIcon className="dw-bc-import-card-file-icon" />
                      </span>
                      <span className="dw-bc-import-card-name">{file.name}</span>
                    </button>
                  );
                })}
              </div>
            </div>

            <div className="dw-bc-import-foot">
              <span className="dw-bc-import-foot-note">支持 .wav, .mp3, .ogg 格式文件</span>
              <div className="dw-bc-import-foot-actions">
                <button type="button" className="dw-bc-import-text-button" onClick={handleCloseAssetExplorer}>取消</button>
                <button type="button" className="dw-bc-primary-button" onClick={handleCloseAssetExplorer}>
                  导入选中项{selectedExternalFiles.length > 0 ? ` (${selectedExternalFiles.length})` : ""}
                </button>
              </div>
            </div>
          </div>
        ) : null}
      </div>
      <div ref={dropdownPortalHostRef} className="dw-demo-dropdown-portal-layer" />
    </div>
  );
}
