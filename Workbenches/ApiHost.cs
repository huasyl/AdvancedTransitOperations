using System.Collections.Generic;
using Colossal.UI;
using Colossal.Core;
using Game;
using Game.SceneFlow;

namespace RapidTransitMod.Workbenches
{
    internal static class ApiHost
    {
        private const string HostKey = "rapidtransitmod";
        internal const string Prefix = "huasyl::rt.workbench.";

        private static bool Ready;
        private static bool HostReady;
        private static bool CallsReady;
        private static string Root = string.Empty;
        private static UIView ObservedView;

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
            ObserveView();
            Bind();
            return HostReady && CallsReady;
        }

        internal static void Dispose()
        {
            Calls.Unbind();

            if (ObservedView != null)
            {
                ObservedView.Listener.ReadyForBindings -= OnReadyForBindings;
                ObservedView = null;
            }

            Ready = false;
            HostReady = false;
            CallsReady = false;
        }

        internal static void RebindNow()
        {
            CallsReady = false;
            Host();
            ObserveView();
            Bind();
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

        private static void ObserveView()
        {
            var view = GameManager.instance?.userInterface?.view;
            if (view == null || ReferenceEquals(view, ObservedView))
            {
                return;
            }

            if (ObservedView != null)
            {
                ObservedView.Listener.ReadyForBindings -= OnReadyForBindings;
            }

            ObservedView = view;
            ObservedView.Listener.ReadyForBindings += OnReadyForBindings;
        }

        private static void OnReadyForBindings()
        {
            Mod.log.Info("DispatchWorkbench UI ready; rebinding API.");
            CallsReady = false;
            Bind();
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
