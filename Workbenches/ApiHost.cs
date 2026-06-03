using System.Collections.Generic;
using Colossal.Core;
using Game;
using Game.SceneFlow;

namespace RapidTransitMod.Workbenches
{
    internal static class ApiHost
    {
        private const string HostKey = "rapidtransitmod";
        internal const string Prefix = "suhua::rt.workbench.";

        private static bool Ready;
        private static bool HostReady;
        private static bool CallsReady;
        private static string Root = string.Empty;

        internal static void Init(string modRootPath)
        {
            if (!string.IsNullOrWhiteSpace(modRootPath) && string.IsNullOrWhiteSpace(Root))
            {
                Root = modRootPath;
            }

            Init();
        }

        internal static void Init()
        {
            if (Ready)
            {
                return;
            }

            Ready = true;
            MainThreadDispatcher.RegisterUpdater(Register);
        }

        private static bool Register()
        {
            Host();
            Bind();
            return HostReady && CallsReady;
        }

        private static void Host()
        {
            if (HostReady || string.IsNullOrWhiteSpace(Root))
            {
                return;
            }

            var uiSystem = GameManager.instance?.userInterface?.view?.uiSystem;
            if (uiSystem == null)
            {
                return;
            }

            uiSystem.AddHostLocation(
                HostKey,
                new HashSet<(string, int)> { (Root, 0) },
                true);
            HostReady = true;
            Mod.log.Info("DispatchWorkbench host location registered.");
        }

        private static void Bind()
        {
            if (CallsReady)
            {
                return;
            }

            if (!Calls.Bind())
            {
                return;
            }

            CallsReady = true;
            Mod.log.Info("DispatchWorkbench API bindings registered.");
        }
    }
}
