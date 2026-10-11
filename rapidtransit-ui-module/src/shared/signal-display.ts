export const STATUS_KEYS: Record<string, string> = {
  cooldown: "signalCooldown", submitted: "signalSubmitted", active: "signalActive",
  tramPriority: "signalTramPriority", otherPriority: "signalOtherPriority",
  noTarget: "signalNoTarget", detected: "signalDetected", progressPaused: "signalPaused",
  unavailable: "signalUnavailable"
};
export const LIGHT_COLORS: Record<string, string> = { red: "#ff6464", green: "#70e6a2", transition: "#f1c75b" };
export const LIGHT_KEYS: Record<string, string> = { red: "signalRed", green: "signalGreen", transition: "signalTransition" };
export const ARROWS: Record<string, string> = {
  straight: "M12 21V3M5 10l7-7 7 7",
  left: "M19 21V11a4 4 0 0 0-4-4H3m6-6L3 7l6 6",
  right: "M5 21V11a4 4 0 0 1 4-4h12m-6-6 6 6-6 6",
  gentleLeft: "M16 21v-6.34a4 4 0 0 0-1.172-2.828L6 3M6 10V3h7",
  gentleRight: "M8 21v-6.34a4 4 0 0 1 1.172-2.828L18 3M18 10V3h-7",
  uTurnLeft: "M18 21V9a6 6 0 0 0-12 0v8M2 13l4 4 4-4",
  uTurnRight: "M6 21V9a6 6 0 0 1 12 0v8m-4-4 4 4 4-4"
};
