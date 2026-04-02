using System;
using System.Reflection;
using Game.Routes;
using Game.Simulation;
using HarmonyLib;
using Unity.Entities;

namespace RapidTransitMod
{
    internal static class TransportBoardingTailReadyPatches
    {
        private static readonly Type s_TrainTickJobType = AccessTools.Inner(typeof(TransportTrainAISystem), "TransportTrainTickJob");

        [HarmonyPatch]
        private static class TrainArePassengersReadyPatch
        {
            private static MethodBase TargetMethod()
            {
                return AccessTools.Method(s_TrainTickJobType, "ArePassengersReady", new[] { typeof(Entity) });
            }

            private static bool Prefix(Entity vehicleEntity, ref bool __result)
            {
                DepartureControlSystem system = DepartureControlSystem.Instance;
                if (system == null)
                    return true;

                __result = system.AreDeferredBoardingTailsIgnoredForReadyCheck(vehicleEntity);
                return false;
            }
        }

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
    }
}
