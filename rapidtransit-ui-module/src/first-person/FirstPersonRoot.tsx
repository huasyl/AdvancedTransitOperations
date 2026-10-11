import React, { useEffect, useMemo, useState } from "react";
import { createPortal } from "react-dom";
import { useValue } from "cs2/api";
import { dataJson$, parseData } from "./firstPersonBindings";
import { FirstPersonOverlay } from "./FirstPersonOverlay";

export function FirstPersonRoot() {
  const json = useValue(dataJson$);
  const data = useMemo(() => parseData(json), [json]);
  const visible = !!data?.active && !!data.vehicle;
  const [node, setNode] = useState<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!visible) return;
    const element = document.createElement("div");
    element.id = "ato-first-person-overlay";
    element.style.pointerEvents = "none";
    document.body.appendChild(element);
    setNode(element);
    return () => { setNode(null); element.remove(); };
  }, [visible]);

  return visible && data && node ? createPortal(<FirstPersonOverlay key={data.vehicle} data={data} />, node) : null;
}
