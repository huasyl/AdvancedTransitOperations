import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

const PORTAL_MARGIN = 12;
const PORTAL_PREFERRED_HEIGHT = 300;

// Shared dropdown base for native workbench pages.
// Positioning, open/close interaction, and the default trigger/menu typography live here,
// while each page still chooses its visual variant and any local class overrides.
export default function WorkbenchDropdown({
  label = "",
  value,
  options,
  onSelect,
  className = "",
  title = "",
  triggerClassName = "",
  menuClassName = "",
  optionClassName = "",
  triggerContent = null,
  portalHostRef = null,
  menuWidth = null,
  variant = "field",
  positioning = "local",
  open: controlledOpen,
  onOpenChange,
  onOpen,
  closeOnSelect = true
}) {
  const [uncontrolledOpen, setUncontrolledOpen] = useState(false);
  const open = typeof controlledOpen === "boolean" ? controlledOpen : uncontrolledOpen;
  const setOpen = typeof onOpenChange === "function" ? onOpenChange : setUncontrolledOpen;
  const [menuRect, setMenuRect] = useState(null);
  const triggerRef = useRef(null);
  const menuRef = useRef(null);
  const usePortal = positioning === "portal" && portalHostRef?.current instanceof HTMLElement;

  useLayoutEffect(() => {
    if (!open) {
      setMenuRect(null);
      return undefined;
    }

    function updateMenuRect() {
      const triggerElement = triggerRef.current;
      if (!(triggerElement instanceof HTMLElement)) {
        return;
      }

      const rect = triggerElement.getBoundingClientRect();
      const portalHostElement = usePortal ? portalHostRef.current : null;
      const portalRect = portalHostElement ? portalHostElement.getBoundingClientRect() : null;
      const viewportHeight = portalRect ? portalRect.height : window.innerHeight;
      const triggerTop = portalRect ? rect.top - portalRect.top : rect.top;
      const triggerBottom = portalRect ? rect.bottom - portalRect.top : rect.bottom;
      const spaceBelow = Math.max(0, viewportHeight - triggerBottom - PORTAL_MARGIN);
      const spaceAbove = Math.max(0, triggerTop - PORTAL_MARGIN);
      const openUp = spaceBelow < PORTAL_PREFERRED_HEIGHT && spaceAbove > spaceBelow;
      const maxHeight = Math.min(PORTAL_PREFERRED_HEIGHT, openUp ? spaceAbove : spaceBelow);
      setMenuRect({
        direction: openUp ? "up" : "down",
        left: portalRect ? rect.left - portalRect.left : rect.left,
        top: openUp ? "auto" : `${triggerBottom}px`,
        bottom: openUp ? `${viewportHeight - triggerTop}px` : "auto",
        width: menuWidth || rect.width,
        maxHeight
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
  }, [menuWidth, open, portalHostRef, setOpen, usePortal]);

  const menuContent = open ? (
    <div
      ref={menuRef}
      className={`dw-demo-dropdown-menu ${usePortal ? "is-portal" : ""} ${menuRect?.direction === "up" ? "is-open-up" : "is-open-down"} ${menuClassName}`.trim()}
      style={usePortal && menuRect ? {
        left: `${menuRect.left}px`,
        top: menuRect.top,
        bottom: menuRect.bottom,
        width: `${menuRect.width}px`,
        maxHeight: `${menuRect.maxHeight}px`
      } : usePortal ? {
        visibility: "hidden",
        pointerEvents: "none"
      } : undefined}
    >
      {options.map((option) => (
        <button
          key={option.key || option.value || option.label}
          type="button"
          className={`dw-demo-dropdown-option ${option.active ? "is-active" : ""} ${optionClassName}`.trim()}
          onClick={() => {
            onSelect(option.value ?? option);
            if (closeOnSelect) {
              setOpen(false);
            }
          }}
        >
          {option.content || option.label}
        </button>
      ))}
    </div>
  ) : null;

  return (
    <div className={`dw-demo-field ${className} dw-demo-dropdown-root is-${variant}`.trim()} onClick={(event) => event.stopPropagation()}>
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
          className={`dw-demo-input dw-demo-dropdown-trigger ${triggerClassName} ${open ? "is-open" : ""}`.trim()}
          title={title || value}
          onClick={() => {
            if (!open) {
              onOpen?.();
            }
            setOpen(!open);
          }}
        >
          {triggerContent || (
            <>
              <span>{value}</span>
              <span className="dw-demo-dropdown-caret" aria-hidden="true">
                <svg viewBox="0 0 16 16" className="dw-demo-dropdown-caret-icon">
                  <path d="M4.2 6.2 8 10l3.8-3.8" fill="none" stroke="#c7d4dc" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </span>
            </>
          )}
        </button>
        {!usePortal ? menuContent : null}
      </div>
      {usePortal && menuContent ? createPortal(menuContent, portalHostRef.current) : null}
    </div>
  );
}
