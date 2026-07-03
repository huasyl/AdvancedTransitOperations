import { useEffect, useLayoutEffect, useRef, useState } from "react";
import {
  INLINE_PANEL_EASING,
  INLINE_PANEL_TRANSITION_MS,
  TAB_TRANSITION_MS,
} from "../broadcast-constants";

function animateElementScrollTop(
  element,
  targetTop,
  duration = 260,
  frameRef = null,
) {
  if (!element) {
    return 0;
  }

  const startTop = element.scrollTop;
  const delta = targetTop - startTop;
  if (Math.abs(delta) < 24 || duration <= 0) {
    element.scrollTop = targetTop;
    if (frameRef) {
      frameRef.current = 0;
    }
    return 0;
  }

  const startTime =
    typeof performance !== "undefined" && typeof performance.now === "function"
      ? performance.now()
      : Date.now();
  const easeInOutCubic = (value) =>
    value < 0.5
      ? 4 * value * value * value
      : 1 - Math.pow(-2 * value + 2, 3) / 2;
  let frameId = 0;
  let lastAppliedTop = startTop;

  function tick(now) {
    const currentTime = typeof now === "number" ? now : Date.now();
    const progress = Math.min(1, (currentTime - startTime) / duration);
    const nextTop = startTop + delta * easeInOutCubic(progress);
    if (Math.abs(nextTop - lastAppliedTop) >= 0.75 || progress >= 1) {
      element.scrollTop = nextTop;
      lastAppliedTop = nextTop;
    }

    if (progress < 1) {
      frameId = window.requestAnimationFrame(tick);
      if (frameRef) {
        frameRef.current = frameId;
      }
      return;
    }

    element.scrollTop = targetTop;
    frameId = 0;
    if (frameRef) {
      frameRef.current = 0;
    }
  }

  frameId = window.requestAnimationFrame(tick);
  if (frameRef) {
    frameRef.current = frameId;
  }
  return frameId;
}

function animateScrollTopWithTransform(
  scrollElement,
  contentElement,
  targetTop,
  duration = 460,
  cleanupRef = null,
) {
  if (!scrollElement || !contentElement) {
    return;
  }

  if (cleanupRef?.current) {
    window.clearTimeout(cleanupRef.current);
    cleanupRef.current = null;
  }

  const startTop = scrollElement.scrollTop;
  const delta = targetTop - startTop;
  if (Math.abs(delta) < 24) {
    scrollElement.scrollTop = targetTop;
    contentElement.style.transition = "";
    contentElement.style.transform = "";
    return;
  }

  contentElement.style.transition = "none";
  contentElement.style.transform = `translateY(${delta}px)`;
  scrollElement.scrollTop = targetTop;

  window.requestAnimationFrame(() => {
    window.requestAnimationFrame(() => {
      contentElement.style.transition = `transform ${duration}ms cubic-bezier(0.16, 1, 0.3, 1)`;
      contentElement.style.transform = "translateY(0px)";

      if (cleanupRef) {
        cleanupRef.current = window.setTimeout(() => {
          cleanupRef.current = null;
          contentElement.style.transition = "";
          contentElement.style.transform = "";
        }, duration + 80);
      }
    });
  });
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
        transition: `max-height ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}, opacity ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}`,
      }}
    >
      <div
        className="dw-bc-animated-panel-inner"
        ref={handleContentRef}
        style={{
          opacity: visible ? 1 : 0,
          transform: visible ? "translateY(0)" : "translateY(-12px)",
          transition: `opacity ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}, transform ${INLINE_PANEL_TRANSITION_MS}ms ${INLINE_PANEL_EASING}`,
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
    <div
      className={`${className ? `${className} ` : ""}dw-bc-fade-presence is-${stage}`}
    >
      {children}
    </div>
  );
}

export {
  animateElementScrollTop,
  animateScrollTopWithTransform,
  AnimatedInlinePanel,
  AnimatedFadePresence,
};
