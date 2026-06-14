import { bindValue, trigger } from "cs2/api";
import { useEffect, useState } from "react";

export const GROUP = "RapidTransitPanel";
export const visible$ = bindValue<boolean>(GROUP, "visible");
export const panelDataJson$ = bindValue<string>(GROUP, "panelDataJson");
export const devSightVisible$ = bindValue<boolean>(GROUP, "devSightVisible");
export const devSightJson$ = bindValue<string>(GROUP, "devSightJson");
export const activeLocale$ = bindValue<string>("app", "activeLocale");

let localPanelOpen = false;
const openSubscribers = new Set<(open: boolean) => void>();

export function setLocalPanelOpen(nextValue: boolean) {
  const nextOpen = nextValue === true;
  if (localPanelOpen === nextOpen) {
    return;
  }

  localPanelOpen = nextOpen;
  trigger(GROUP, "setPanelOpen", localPanelOpen);
  openSubscribers.forEach((callback) => callback(localPanelOpen));
}

export function useLocalPanelOpen() {
  const [open, setOpen] = useState(localPanelOpen);

  useEffect(() => {
    openSubscribers.add(setOpen);
    return () => {
      openSubscribers.delete(setOpen);
    };
  }, []);

  return open;
}
