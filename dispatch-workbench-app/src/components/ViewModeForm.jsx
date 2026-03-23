import { useMemo, useRef, useState } from "react";
import { ChoiceButtons, ControlText, SafeControlText } from "./ChoiceButtons";
import { useI18n } from "../lib/i18n";
import TimeInput from "./TimeInput";

function LineDropdown({ value, onChange, onOpen, options = [], placeholder }) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef(null);
  const orderedOptionIdsRef = useRef([]);
  const orderedOptions = useMemo(() => {
    if (!Array.isArray(options) || options.length === 0) {
      orderedOptionIdsRef.current = [];
      return [];
    }

    const incomingById = new Map(options.map((option) => [option.value, option]));
    const currentIds = orderedOptionIdsRef.current.filter((id) => incomingById.has(id));
    const newIds = options
      .map((option) => option.value)
      .filter((id) => !currentIds.includes(id));
    const nextIds = [...currentIds, ...newIds];
    orderedOptionIdsRef.current = nextIds;
    return nextIds.map((id) => incomingById.get(id)).filter(Boolean);
  }, [options]);
  const selected = orderedOptions.find((option) => option.value === value) || null;

  return (
    <div
      className="dw-line-dropdown"
      ref={rootRef}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) {
          setOpen(false);
        }
      }}
    >
      <button
        type="button"
        className={`dw-line-dropdown-trigger ${open ? "is-open" : ""}`}
        onKeyDown={(event) => {
          if (event.key === "Escape") {
            setOpen(false);
          }
        }}
        onClick={() => { if (!open) { onOpen?.(); } setOpen((current) => !current); }}
        title={selected?.title || selected?.label || ""}
      >
        <ControlText>{selected?.label || placeholder || "-"}</ControlText>
        <span className="dw-line-dropdown-caret" aria-hidden="true">
          v
        </span>
      </button>
      {open ? (
        <div className="dw-line-dropdown-menu" role="listbox">
          {orderedOptions.map((option) => (
            <button
              key={option.value}
              type="button"
              className={`dw-line-dropdown-option ${option.value === value ? "is-active" : ""}`}
              title={option.title || option.label}
              onClick={() => {
                onChange?.(option.value);
                setOpen(false);
              }}
            >
              <ControlText>{option.label}</ControlText>
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}

function normalizeLineIds(lineIds, fallbackId) {
  const source = Array.isArray(lineIds) ? lineIds : fallbackId ? [fallbackId] : [];
  const next = [];
  const seen = new Set();
  for (const id of source) {
    if (!id || seen.has(id)) {
      continue;
    }
    seen.add(id);
    next.push(id);
  }
  return next;
}

export default function ViewModeForm({
  viewMode,
  setViewMode,
  selectedLineId,
  setSelectedLineId,
  mergedView,
  setMergedView,
  lines = [],
  stations = [],
  windowValid,
  onRefreshMetadata
}) {
  const { t } = useI18n();
  const windowStartRef = useRef(null);
  const windowEndRef = useRef(null);
  const localLineIds = normalizeLineIds(mergedView.localLineIds, mergedView.localLineId);
  const expressLineIds = normalizeLineIds(mergedView.expressLineIds, mergedView.expressLineId);
  const loopOptions = [
    { value: "loop", label: t("overview.form.loop.enabled") },
    { value: "turnback", label: t("overview.form.loop.disabled") }
  ];
  const allLineOptions = lines.map((line) => ({
    value: line.id,
    label: line.name,
    title: line.rawName || line.name
  }));
  const singleLineOptions = allLineOptions;
  const localLineOptions = allLineOptions;
  const expressLineOptions = allLineOptions;
  const turnbackOptions = stations.map((station) => ({
    value: station.id,
    label: station.name,
    title: station.rawName || station.name
  }));
  const originStation = stations[0] || null;
  const turnbackStation =
    stations.find((station) => station.id === mergedView.turnbackStationId) || null;
  const directionOptions = [
    {
      value: "up",
      label: turnbackStation
        ? t("overview.form.direction.toStation", { name: turnbackStation.name })
        : t("overview.form.direction.toTurnback")
    },
    {
      value: "down",
      label: originStation
        ? t("overview.form.direction.toStation", { name: originStation.name })
        : t("overview.form.direction.toOrigin")
    }
  ];

  function setLocalIds(nextIds) {
    setMergedView((current) => {
      const normalizedLocal = normalizeLineIds(nextIds, "");
      const currentExpress = normalizeLineIds(current.expressLineIds, current.expressLineId);
      const nextExpress = currentExpress.filter((id) => !normalizedLocal.includes(id));
      setSelectedLineId(normalizedLocal[0] || "");
      return {
        ...current,
        localLineIds: normalizedLocal,
        localLineId: normalizedLocal[0] || "",
        expressLineIds: nextExpress,
        expressLineId: nextExpress[0] || ""
      };
    });
  }

  function setExpressIds(nextIds) {
    setMergedView((current) => {
      const currentLocal = normalizeLineIds(current.localLineIds, current.localLineId);
      const normalizedExpress = normalizeLineIds(nextIds, "").filter(
        (id) => !currentLocal.includes(id)
      );
      return {
        ...current,
        expressLineIds: normalizedExpress,
        expressLineId: normalizedExpress[0] || ""
      };
    });
  }

  function focusAndSelectInput(inputRef) {
    const element = inputRef?.current;
    if (!element) {
      return;
    }
    element.focus();
    if (typeof element.select === "function") {
      setTimeout(() => element.select(), 0);
    }
  }

  function updateLineWidthScale(delta) {
    setMergedView((current) => {
      const currentScale = Number.isFinite(Number(current.lineWidthScale))
        ? Number(current.lineWidthScale)
        : 1;
      const nextScale = Math.max(0.7, Math.min(1.6, currentScale + delta));
      return {
        ...current,
        lineWidthScale: Math.round(nextScale * 100) / 100
      };
    });
  }

  return (
    <section className="dw-panel">
      <div className="dw-panel-header">
        <SafeControlText>{t("overview.form.view")}</SafeControlText>
      </div>
      <div className="dw-panel-body">
        <div className="dw-form-grid">
          <div className="dw-field">
            <label>
              <SafeControlText>{t("overview.form.localLine")}</SafeControlText>
            </label>
            <div className="dw-multi-line-editor is-local-group">
              <button
                type="button"
                className="dw-btn"
                onClick={() => {
                  const current = normalizeLineIds(localLineIds, "");
                  const used = new Set([...current, ...expressLineIds]);
                  const candidate = lines.find((line) => !used.has(line.id));
                  if (!candidate) {
                    return;
                  }
                  setLocalIds([...current, candidate.id]);
                }}
              >
                <ControlText>{t("overview.form.addLocalLine")}</ControlText>
              </button>
              {localLineIds.map((lineId, rowIndex) => (
                <div key={`local-line-${lineId}-${rowIndex}`} className="dw-multi-line-row">
                  <LineDropdown
                    options={localLineOptions.filter(
                      (option) =>
                        option.value === lineId ||
                        (!localLineIds.includes(option.value) &&
                          !expressLineIds.includes(option.value))
                    )}
                    value={lineId}
                    onOpen={onRefreshMetadata}
                    onChange={(value) => {
                      const next = [...localLineIds];
                      next[rowIndex] = value;
                      setLocalIds(next);
                    }}
                    placeholder={t("overview.form.localLine")}
                  />
                  <button
                    type="button"
                    className="dw-btn dw-btn-danger"
                    onClick={() => {
                      if (localLineIds.length <= 1) {
                        return;
                      }
                      const next = localLineIds.filter((_, index) => index !== rowIndex);
                      setLocalIds(next);
                    }}
                  >
                    <ControlText>{t("schedule.button.delete")}</ControlText>
                  </button>
                </div>
              ))}
            </div>
          </div>

          <div className="dw-field">
            <label>
              <SafeControlText>{t("overview.form.expressLine")}</SafeControlText>
            </label>
            <div className="dw-multi-line-editor is-express-group">
              <button
                type="button"
                className="dw-btn"
                onClick={() => {
                  const current = normalizeLineIds(expressLineIds, "");
                  const used = new Set([...current, ...localLineIds]);
                  const candidate = lines.find((line) => !used.has(line.id));
                  if (!candidate) {
                    return;
                  }
                  setExpressIds([...current, candidate.id]);
                }}
              >
                <ControlText>{t("overview.form.addExpressLine")}</ControlText>
              </button>
              {expressLineIds.length === 0 ? (
                <div className="dw-multi-line-empty">
                  <ControlText>{t("overview.form.expressNone")}</ControlText>
                </div>
              ) : null}
              {expressLineIds.map((lineId, rowIndex) => (
                <div key={`express-line-${lineId}-${rowIndex}`} className="dw-multi-line-row">
                  <LineDropdown
                    options={expressLineOptions.filter(
                      (option) =>
                        option.value === lineId ||
                        (!expressLineIds.includes(option.value) &&
                          !localLineIds.includes(option.value))
                    )}
                    value={lineId}
                    onOpen={onRefreshMetadata}
                    onChange={(value) => {
                      const next = [...expressLineIds];
                      next[rowIndex] = value;
                      setExpressIds(next);
                    }}
                    placeholder={t("overview.form.expressLine")}
                  />
                  <button
                    type="button"
                    className="dw-btn dw-btn-danger"
                    onClick={() => {
                      const next = expressLineIds.filter((_, index) => index !== rowIndex);
                      setExpressIds(next);
                    }}
                  >
                    <ControlText>{t("schedule.button.delete")}</ControlText>
                  </button>
                </div>
              ))}
            </div>
          </div>

          <div className="dw-field">
            <label>
              <SafeControlText>{t("overview.form.loop")}</SafeControlText>
            </label>
            <ChoiceButtons
              options={loopOptions}
              value={mergedView.isLoop ? "loop" : "turnback"}
              onChange={(value) =>
                setMergedView((current) => ({
                  ...current,
                  isLoop: value === "loop",
                  turnbackStationId: value === "loop" ? "" : current.turnbackStationId
                }))
              }
            />
          </div>

          <div className={`dw-field ${mergedView.isLoop ? "is-disabled" : ""}`}>
            <label>
              <SafeControlText>{t("overview.form.turnback")}</SafeControlText>
            </label>
            <LineDropdown
              options={turnbackOptions}
              value={mergedView.turnbackStationId}
              onChange={(value) =>
                setMergedView((current) => ({
                  ...current,
                  turnbackStationId: value
                }))
              }
              placeholder={t("overview.form.turnback")}
            />
          </div>

          {!mergedView.isLoop ? (
            <div className="dw-field">
              <label>
                <SafeControlText>{t("overview.form.direction")}</SafeControlText>
              </label>
              <ChoiceButtons
                className="is-vertical"
                options={directionOptions}
                value={mergedView.direction}
                onChange={(value) => setMergedView((current) => ({ ...current, direction: value }))}
              />
            </div>
          ) : null}

          <div className="dw-field dw-field-inline">
            <label>
              <SafeControlText>{t("overview.form.window")}</SafeControlText>
            </label>
            <div className="dw-inline-time-range">
              <TimeInput
                ref={windowStartRef}
                value={mergedView.windowStart}
                onChange={(event) => {
                  const nextValue = event.target.value;
                  setMergedView((current) => ({ ...current, windowStart: nextValue }));
                  if (String(nextValue).length >= 5) {
                    focusAndSelectInput(windowEndRef);
                  }
                }}
              />
              <span>
                <SafeControlText>{t("common.to")}</SafeControlText>
              </span>
              <TimeInput
                ref={windowEndRef}
                value={mergedView.windowEnd}
                onChange={(event) =>
                  setMergedView((current) => ({ ...current, windowEnd: event.target.value }))
                }
              />
            </div>
          </div>

          <div className="dw-field">
            <label>
              <SafeControlText>{t("overview.form.diagramDisplay")}</SafeControlText>
            </label>
            <div className="dw-diagram-display-controls">
              <button type="button" className="dw-btn" onClick={() => updateLineWidthScale(-0.2)}>
                <ControlText>{t("overview.form.lineWidthDown")}</ControlText>
              </button>
              <button type="button" className="dw-btn" onClick={() => updateLineWidthScale(0.2)}>
                <ControlText>{t("overview.form.lineWidthUp")}</ControlText>
              </button>
              <button
                type="button"
                className="dw-btn"
                onClick={() =>
                  setMergedView((current) => ({
                    ...current,
                    lineWidthScale: 1,
                    showStopAnchors: true
                  }))
                }
              >
                <ControlText>{t("overview.form.displayReset")}</ControlText>
              </button>
              <button
                type="button"
                className="dw-btn"
                onClick={() =>
                  setMergedView((current) => ({
                    ...current,
                    showStopAnchors: !current.showStopAnchors
                  }))
                }
              >
                <ControlText>
                  {mergedView.showStopAnchors
                    ? t("overview.form.hideStopAnchors")
                    : t("overview.form.showStopAnchors")}
                </ControlText>
              </button>
            </div>
          </div>
        </div>

        <div className="dw-chip-row">
          <span className={`dw-chip ${windowValid ? "" : "is-warn"}`}>
            <ControlText>
              {windowValid ? t("overview.form.windowValid") : t("overview.form.windowInvalid")}
            </ControlText>
          </span>
          <span className="dw-chip is-warn">
            <ControlText>{t("overview.form.mergedReadonly")}</ControlText>
          </span>
        </div>
      </div>
    </section>
  );
}
