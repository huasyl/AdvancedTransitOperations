using System;
using Game.Routes;
using Game.Simulation;
using HarmonyLib;
using Unity.Entities;

namespace RapidTransitMod
{
    internal static class TransportBoardingTailReadyPatches
    {
        [HarmonyPatch(typeof(RouteUtils), nameof(RouteUtils.GetBoardingVehicle))]
        private static class RouteUtilsGetBoardingVehiclePatch
        {
            private static void Postfix(ref bool __result, ref Entity vehicle, ref bool testing, ref bool obsolete)
            {
                if (!__result || vehicle == Entity.Null || testing)
                    return;

                DepartureControlSystem system = DepartureControlSystem.Instance;
                if (system == null || !system.ShouldBlockNewBoardingForClosingVehicle(vehicle))
                    return;

                vehicle = Entity.Null;
                testing = false;
                obsolete = false;
                __result = false;
            }
        }

        [HarmonyPatch(typeof(ResidentAISystem), "OnUpdate")]
        private static class ResidentAISystemOnUpdatePatch
        {
            private static void Prefix(ResidentAISystem __instance)
            {
                DepartureControlSystem system = DepartureControlSystem.Instance;
                if (system == null)
                    return;

                system.ProcessForcedMidStopHardCloseTailCancels(processResidents: true, processPets: false);
            }
        }

        [HarmonyPatch(typeof(PetAISystem), "OnUpdate")]
        private static class PetAISystemOnUpdatePatch
        {
            private static void Prefix(PetAISystem __instance)
            {
                DepartureControlSystem system = DepartureControlSystem.Instance;
                if (system == null)
                    return;

                system.ProcessForcedMidStopHardCloseTailCancels(processResidents: false, processPets: true);
            }
        }
    }
}
