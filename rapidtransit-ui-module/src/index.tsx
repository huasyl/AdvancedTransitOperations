import type { ModRegistrar } from "cs2/modding";
import { RapidTransitPanelRoot } from "./selection/RapidTransitPanelRoot";
import { RapidTransitTopButton } from "./top-buttons/RapidTransitTopButton";
import { registerRapidTransitWorkbenchPanel, registerRapidTransitWorkbenchPanelType, prewarmNativeWorkbenchScheduleLoader } from "./workbench-host/workbenchHost";
import { RapidTransitNativeLocaleSync } from "./workbench-host/RapidTransitNativeLocaleSync";

prewarmNativeWorkbenchScheduleLoader();

export default function register(mod: ModRegistrar) {
  mod.extend("game-ui/game/data-binding/game-bindings.ts", "GamePanelType", registerRapidTransitWorkbenchPanelType);
  mod.extend("game-ui/game/components/game-panel-renderer.tsx", "gamePanelComponents", registerRapidTransitWorkbenchPanel);
  mod.append("GameTopRight", RapidTransitTopButton);
  mod.append("Game", RapidTransitNativeLocaleSync);
  mod.append("Game", RapidTransitPanelRoot);
}

export const hasCSS = false;
