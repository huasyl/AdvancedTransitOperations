using Game.UI.InGame;

namespace RapidTransitMod
{
    public sealed class DispatchWorkbenchNativePanel : TabbedGamePanel
    {
        public override bool blocking => true;

        public override LayoutPosition position => LayoutPosition.Center;

        public override bool retainProperties => true;
    }
}
