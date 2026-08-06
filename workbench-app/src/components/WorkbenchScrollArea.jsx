import { useEffect, useRef } from "react";

export default function WorkbenchScrollArea({
  className = "",
  metricsKey,
  children,
  externalScrollRef,
  hideDelayMs = 2200
}) {
  const scrollRef = useRef(null);
  const indicatorRef = useRef(null);
  const thumbRef = useRef(null);
  const hideTimerRef = useRef(0);
  const hoverRef = useRef(false);
  const indicatorActiveRef = useRef(false);
  const scrollMetricsRef = useRef({
    visible: false,
    thumbTop: 0,
    thumbHeight: 0
  });
  const dragRef = useRef({
    dragging: false,
    pointerId: null,
    pointerOffset: 0
  });

  function syncIndicatorState() {
    const indicatorElement = indicatorRef.current;
    if (!indicatorElement) {
      return;
    }

    indicatorElement.classList.toggle("is-visible", scrollMetricsRef.current.visible);
    indicatorElement.classList.toggle("is-active", indicatorActiveRef.current);
  }

  function applyScrollMetrics(nextMetrics) {
    const previousMetrics = scrollMetricsRef.current;
    if (
      previousMetrics.visible === nextMetrics.visible
      && previousMetrics.thumbTop === nextMetrics.thumbTop
      && previousMetrics.thumbHeight === nextMetrics.thumbHeight
    ) {
      return;
    }

    scrollMetricsRef.current = nextMetrics;

    const thumbElement = thumbRef.current;
    if (thumbElement) {
      thumbElement.style.height = `${nextMetrics.thumbHeight}px`;
      thumbElement.style.transform = `translateY(${nextMetrics.thumbTop}px)`;
    }

    syncIndicatorState();
  }

  function setIndicatorActive(nextActive) {
    if (indicatorActiveRef.current === nextActive) {
      return;
    }

    indicatorActiveRef.current = nextActive;
    syncIndicatorState();
  }

  useEffect(() => {
    const scrollElement = scrollRef.current;
    if (!scrollElement) {
      return undefined;
    }

    let frameId = 0;
    let timeoutId = 0;
    let resizeObserver = null;

    function showIndicatorTemporarily() {
      setIndicatorActive(true);

      if (hideTimerRef.current) {
        window.clearTimeout(hideTimerRef.current);
      }

      hideTimerRef.current = window.setTimeout(() => {
        if (!hoverRef.current && !dragRef.current.dragging) {
          setIndicatorActive(false);
        }
      }, hideDelayMs);
    }

    function updateMetrics() {
      frameId = 0;

      const clientHeight = scrollElement.clientHeight;
      const scrollHeight = scrollElement.scrollHeight;
      const scrollTop = scrollElement.scrollTop;
      const maxScroll = scrollHeight - clientHeight;

      if (clientHeight <= 0 || maxScroll <= 0) {
        applyScrollMetrics({
          visible: false,
          thumbTop: 0,
          thumbHeight: 0
        });
        return;
      }

      const thumbHeight = Math.max(24, Math.round((clientHeight * clientHeight) / scrollHeight));
      const maxThumbTop = Math.max(0, clientHeight - thumbHeight);
      const thumbTop = Math.round((scrollTop / maxScroll) * maxThumbTop);

      applyScrollMetrics({
        visible: true,
        thumbTop,
        thumbHeight
      });
    }

    function scheduleUpdate() {
      if (frameId) {
        return;
      }
      frameId = window.requestAnimationFrame(updateMetrics);
    }

    scheduleUpdate();
    timeoutId = window.setTimeout(updateMetrics, 80);

    if (typeof ResizeObserver === "function") {
      resizeObserver = new ResizeObserver(() => {
        scheduleUpdate();
      });
      resizeObserver.observe(scrollElement);
      if (scrollElement.firstElementChild) {
        resizeObserver.observe(scrollElement.firstElementChild);
      }
    }

    function handleScroll() {
      scheduleUpdate();
      showIndicatorTemporarily();
    }

    scrollElement.addEventListener("scroll", handleScroll);
    window.addEventListener("resize", scheduleUpdate);

    return () => {
      if (frameId) {
        window.cancelAnimationFrame(frameId);
      }
      if (timeoutId) {
        window.clearTimeout(timeoutId);
      }
      if (hideTimerRef.current) {
        window.clearTimeout(hideTimerRef.current);
      }
      if (resizeObserver) {
        resizeObserver.disconnect();
      }
      scrollElement.removeEventListener("scroll", handleScroll);
      window.removeEventListener("resize", scheduleUpdate);
    };
  }, [hideDelayMs, metricsKey]);

  function handleIndicatorMouseEnter() {
    hoverRef.current = true;
    setIndicatorActive(true);
    if (hideTimerRef.current) {
      window.clearTimeout(hideTimerRef.current);
    }
  }

  function handleIndicatorMouseLeave() {
    hoverRef.current = false;
    if (!dragRef.current.dragging) {
      setIndicatorActive(false);
    }
  }

  function applyDragPosition(clientY, pointerOffset) {
    const scrollElement = scrollRef.current;
    const indicatorElement = indicatorRef.current;
    if (!scrollElement || !indicatorElement) {
      return;
    }

    const indicatorRect = indicatorElement.getBoundingClientRect();
    const trackHeight = indicatorRect.height;
    const clientHeight = scrollElement.clientHeight;
    const scrollHeight = scrollElement.scrollHeight;
    const scrollRange = scrollHeight - clientHeight;

    if (clientHeight <= 0 || scrollRange <= 0) {
      applyScrollMetrics({
        visible: false,
        thumbTop: 0,
        thumbHeight: 0
      });
      return;
    }

    const thumbHeight = Math.max(24, Math.round((clientHeight * clientHeight) / scrollHeight));
    const maxThumbTop = Math.max(0, trackHeight - thumbHeight);
    const rawTop = clientY - indicatorRect.top - pointerOffset;
    const nextThumbTop = Math.max(0, Math.min(maxThumbTop, rawTop));
    const nextScrollTop = maxThumbTop <= 0 ? 0 : (nextThumbTop / maxThumbTop) * scrollRange;
    scrollElement.scrollTop = nextScrollTop;
    scrollElement.dispatchEvent(new Event("scroll"));

    applyScrollMetrics({
      visible: true,
      thumbTop: Math.round(nextThumbTop),
      thumbHeight
    });
  }

  function handleIndicatorMouseDown(event) {
    const indicatorElement = indicatorRef.current;
    const scrollMetrics = scrollMetricsRef.current;
    if (!indicatorElement || !scrollMetrics.visible) {
      return;
    }

    event.preventDefault();

    const thumbOffset = event.target === indicatorElement
      ? scrollMetrics.thumbHeight / 2
      : event.clientY - indicatorElement.getBoundingClientRect().top - scrollMetrics.thumbTop;

    dragRef.current = {
      dragging: true,
      pointerId: null,
      pointerOffset: thumbOffset
    };
    setIndicatorActive(true);

    applyDragPosition(event.clientY, thumbOffset);

    function handleMouseMove(moveEvent) {
      applyDragPosition(moveEvent.clientY, dragRef.current.pointerOffset);
    }

    function handleMouseUp() {
      dragRef.current.dragging = false;
      window.removeEventListener("mousemove", handleMouseMove);
      window.removeEventListener("mouseup", handleMouseUp);

      if (!hoverRef.current) {
        setIndicatorActive(false);
      }
    }

    window.addEventListener("mousemove", handleMouseMove);
    window.addEventListener("mouseup", handleMouseUp);
  }

  function handleIndicatorPointerDown(event) {
    const scrollElement = scrollRef.current;
    const indicatorElement = indicatorRef.current;
    const scrollMetrics = scrollMetricsRef.current;
    if (!scrollElement || !indicatorElement || !scrollMetrics.visible) {
      return;
    }

    event.preventDefault();
    if (typeof indicatorElement.setPointerCapture === "function") {
      indicatorElement.setPointerCapture(event.pointerId);
    }

    const thumbOffset = event.target === indicatorElement
      ? scrollMetrics.thumbHeight / 2
      : event.clientY - indicatorElement.getBoundingClientRect().top - scrollMetrics.thumbTop;

    dragRef.current = {
      dragging: true,
      pointerId: event.pointerId,
      pointerOffset: thumbOffset
    };
    setIndicatorActive(true);
    applyDragPosition(event.clientY, thumbOffset);
  }

  function handleIndicatorPointerMove(event) {
    const scrollElement = scrollRef.current;
    const indicatorElement = indicatorRef.current;
    if (!scrollElement || !indicatorElement || !dragRef.current.dragging || dragRef.current.pointerId !== event.pointerId) {
      return;
    }

    applyDragPosition(event.clientY, dragRef.current.pointerOffset);
  }

  function handleIndicatorPointerUp(event) {
    const indicatorElement = indicatorRef.current;
    if (dragRef.current.pointerId !== event.pointerId) {
      return;
    }

    if (indicatorElement && typeof indicatorElement.releasePointerCapture === "function") {
      indicatorElement.releasePointerCapture(event.pointerId);
    }

    dragRef.current.dragging = false;
    dragRef.current.pointerId = null;

    if (!hoverRef.current) {
      setIndicatorActive(false);
    }
  }

  return (
    <div className="dw-demo-scroll-shell">
      <div
        ref={(node) => {
          scrollRef.current = node;
          if (typeof externalScrollRef === "function") {
            externalScrollRef(node);
          } else if (externalScrollRef && typeof externalScrollRef === "object") {
            externalScrollRef.current = node;
          }
        }}
        className={`dw-demo-scroll-body ${className}`.trim()}
      >
        {children}
      </div>
      <div
        ref={indicatorRef}
        className="dw-demo-scroll-indicator"
        onMouseEnter={handleIndicatorMouseEnter}
        onMouseLeave={handleIndicatorMouseLeave}
        onMouseDown={handleIndicatorMouseDown}
        onPointerDown={handleIndicatorPointerDown}
        onPointerMove={handleIndicatorPointerMove}
        onPointerUp={handleIndicatorPointerUp}
      >
        <div
          ref={thumbRef}
          className="dw-demo-scroll-thumb"
          style={{
            height: "0px",
            transform: "translateY(0px)"
          }}
        />
      </div>
    </div>
  );
}
