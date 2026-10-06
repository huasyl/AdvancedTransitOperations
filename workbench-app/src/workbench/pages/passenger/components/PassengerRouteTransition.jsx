import { useEffect, useRef, useState } from "react";

const EASE = "cubic-bezier(0.19, 1, 0.22, 1)";

export default function PassengerRouteTransition({ routeKey, isActive, children }) {
  const [shown, setShown] = useState({ key: routeKey, children });
  const [moving, setMoving] = useState(false);
  const viewportRef = useRef(null), clipRef = useRef(null), contentRef = useRef(null);
  const latest = useRef({ key: routeKey, children });
  const displayed = useRef(children);
  latest.current = { key: routeKey, children };
  if (shown.key === routeKey) displayed.current = children;

  useEffect(() => {
    const viewport = viewportRef.current, clip = clipRef.current, content = contentRef.current;
    const frames = new Set(), timers = new Set();
    function frame(callback) {
      const id = window.requestAnimationFrame(() => { frames.delete(id); callback(); });
      frames.add(id);
    }
    function settle(callback) { frame(() => frame(callback)); }
    function later(callback, delay) {
      const id = window.setTimeout(() => { timers.delete(id); callback(); }, delay);
      timers.add(id);
    }
    function reset() {
      viewport.style.height = ""; viewport.style.transition = "";
      clip.style.height = ""; clip.style.visibility = ""; clip.style.transition = ""; clip.style.transform = "";
      content.style.transform = ""; content.style.transition = "";
    }
    if (!isActive || shown.key === routeKey) {
      reset(); setShown(latest.current); setMoving(false);
      return undefined;
    }
    setMoving(true);
    viewport.style.height = `${viewport.getBoundingClientRect().height}px`;
    const previousHeight = content.getBoundingClientRect().height;
    clip.style.height = `${previousHeight}px`;
    clip.style.transition = `transform 160ms ${EASE}`;
    content.style.transition = `transform 160ms ${EASE}`;
    settle(() => {
      clip.style.transform = `translateY(${-previousHeight}px)`;
      content.style.transform = `translateY(${previousHeight - 16}px)`;
      later(() => {
        clip.style.transition = "none"; content.style.transition = "none";
        clip.style.visibility = "hidden"; clip.style.height = "auto"; clip.style.transform = "";
        content.style.transform = "";
        setShown(latest.current);
        settle(() => {
          const height = content.getBoundingClientRect().height;
          clip.style.height = `${height}px`; clip.style.transform = `translateY(${-height}px)`;
          content.style.transform = `translateY(${height + 16}px)`;
          viewport.style.height = `${height}px`;
          settle(() => {
            clip.style.visibility = "visible";
            clip.style.transition = `transform 420ms ${EASE}`;
            content.style.transition = `transform 420ms ${EASE}`;
            clip.style.transform = "translateY(0px)";
            content.style.transform = "translateY(0px)";
            later(() => { reset(); setMoving(false); }, 440);
          });
        });
      }, 180);
    });
    return () => {
      frames.forEach((id) => window.cancelAnimationFrame(id));
      timers.forEach((id) => window.clearTimeout(id));
      reset();
    };
  }, [routeKey, isActive]);

  return <div ref={viewportRef} className={`passenger-route-transition${moving ? " is-moving" : ""}`}>
    <div ref={clipRef} className="passenger-route-clip"><div ref={contentRef}>
      {shown.key === routeKey ? children : displayed.current}
    </div></div>
  </div>;
}
