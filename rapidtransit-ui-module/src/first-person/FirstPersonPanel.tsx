import React, { useEffect, useLayoutEffect, useRef, useState } from "react";
import { FirstPersonData, FirstPersonSignalData } from "./firstPersonBindings";

export type PanelContent =
  | { kind: "stop"; data: NonNullable<FirstPersonData["stop"]> }
  | { kind: "signal"; data: FirstPersonSignalData };

interface PanelProps {
  content: PanelContent | null;
  place: "center" | "left";
  children: (content: PanelContent) => React.ReactNode;
}

export function FirstPersonPanel({ content, place, children }: PanelProps) {
  const [shown, setShown] = useState<PanelContent | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const desiredRef = useRef<PanelContent | null>(null);
  const shownKindRef = useRef<PanelContent["kind"] | null>(null);
  const positionRef = useRef(100);
  const animationRef = useRef<number | null>(null);
  const timeoutRef = useRef<number | null>(null);
  const kind = content?.kind ?? null;

  const clearTimer = () => {
    if (timeoutRef.current != null) window.clearTimeout(timeoutRef.current);
    timeoutRef.current = null;
  };

  const settle = () => {
    clearTimer();
    const next = desiredRef.current;
    shownKindRef.current = next?.kind ?? null;
    setShown(next);
    if (next != null) slide(0);
  };

  const slide = (target: number) => {
    if (animationRef.current != null) cancelAnimationFrame(animationRef.current);
    animationRef.current = null;
    const panel = panelRef.current;
    if (!panel) return;
    const start = positionRef.current;
    if (start === target) {
      if (target === 100) settle();
      return;
    }
    const startTime = performance.now();
    const animate = (now: number) => {
      const progress = Math.min((now - startTime) / 375, 1);
      positionRef.current = progress < 1 ? start + (target - start) * progress : target;
      panel.style.transform = `translateY(${positionRef.current}%)`;
      if (progress < 1) {
        animationRef.current = requestAnimationFrame(animate);
      } else {
        animationRef.current = null;
        if (target === 100) settle();
      }
    };
    animationRef.current = requestAnimationFrame(animate);
  };

  useLayoutEffect(() => {
    desiredRef.current = content;
    if (content != null && shownKindRef.current === content.kind) setShown(content);
  }, [content]);

  useEffect(() => {
    clearTimer();
    const want = desiredRef.current;
    const showing = shownKindRef.current;
    if (want == null) {
      if (showing != null) {
        timeoutRef.current = window.setTimeout(() => {
          timeoutRef.current = null;
          slide(100);
        }, 2000);
      }
    } else if (want.kind === showing) slide(0);
    else if (showing == null) settle();
    else slide(100);
  }, [kind]);

  useEffect(() => {
    return () => {
      clearTimer();
      if (animationRef.current != null) cancelAnimationFrame(animationRef.current);
      animationRef.current = null;
    };
  }, []);

  const placeStyle: React.CSSProperties = place === "left"
    ? { justifyContent: "flex-start", paddingLeft: "10rem" }
    : { justifyContent: "center" };

  return (
    <div style={{ position: "fixed", top: "10rem", left: "0", width: "100%", display: "flex", flexDirection: "row", alignItems: "flex-start", overflow: "hidden", pointerEvents: "none", zIndex: 100, ...placeStyle }}>
      <div ref={panelRef} className="tool-options-panel_Se6" style={{ display: "flex", flexDirection: "column", padding: "0", transform: "translateY(100%)", transition: "none", pointerEvents: "none",
        fontFamily: "var(--fontFamily)" }}>
        {shown != null ? children(shown) : null}
      </div>
    </div>
  );
}
