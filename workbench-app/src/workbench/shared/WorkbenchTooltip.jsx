import { useEffect, useRef, useState } from "react";

const PREWARM_MS = 300;

export default function WorkbenchTooltip({ tooltip, delayTime = 200, children }) {
  const NativeTooltip = window["cs2/ui"]?.Tooltip;
  const anchorRef = useRef(null);
  const [warming, setWarming] = useState(false);

  useEffect(() => {
    if (!NativeTooltip || !tooltip) return undefined;
    const anchor = document.createElement("span");
    anchor.style.cssText = "position:fixed;left:-99999px;top:0;";
    document.body.appendChild(anchor);
    anchorRef.current = anchor;
    setWarming(true);
    const timer = window.setTimeout(() => {
      setWarming(false);
      anchor.remove();
    }, PREWARM_MS);
    return () => {
      window.clearTimeout(timer);
      anchor.remove();
    };
  }, [tooltip]);

  if (!NativeTooltip || !tooltip) return children;

  return <>
    <NativeTooltip tooltip={tooltip} delayTime={delayTime}>{children}</NativeTooltip>
    {warming ? <NativeTooltip tooltip={tooltip} forceVisible delayTime={0} anchorElRef={anchorRef}><span /></NativeTooltip> : null}
  </>;
}
