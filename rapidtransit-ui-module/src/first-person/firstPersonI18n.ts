import { useLocalization } from "cs2/l10n";
import { SIGNAL_KEYS, SIGNAL_FALLBACKS } from "../shared/signal-i18n";

const KEYS: Record<string, string> = {
  ...SIGNAL_KEYS, stopped: "RapidTransit.Stopped", plannedDeparture: "RapidTransit.FirstPersonPlannedDeparture",
  minutes: "RapidTransit.UnitMinShort"
};
const FALLBACKS: Record<string, string> = {
  ...SIGNAL_FALLBACKS, stopped: "已停", plannedDeparture: "计划发出", minutes: "分"
};

export function useFirstPersonT() {
  const { translate } = useLocalization();
  return (key: string) => translate(KEYS[key] || key, FALLBACKS[key] || key) || FALLBACKS[key] || key;
}
