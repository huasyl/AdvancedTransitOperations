using System;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Workbench
    {
        internal Workbench(
            Host host,
            Drafts drafts,
            DraftSync sync,
            Query query,
            Snapshot snapshot,
            Persist persist,
            Commands commands,
            Saves saves)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            Sync = sync ?? throw new ArgumentNullException(nameof(sync));
            Query = query ?? throw new ArgumentNullException(nameof(query));
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Persist = persist ?? throw new ArgumentNullException(nameof(persist));
            Commands = commands ?? throw new ArgumentNullException(nameof(commands));
            Saves = saves ?? throw new ArgumentNullException(nameof(saves));
        }

        internal Host Host { get; }
        internal Drafts Drafts { get; }
        internal DraftSync Sync { get; }
        internal Query Query { get; }
        internal Snapshot Snapshot { get; }
        internal Commands Commands { get; }
        internal Saves Saves { get; }
        internal Persist Persist { get; }

        internal string Load(ModeScope scope)
        {
            Ready();
            return Workbenches.Json.Write(
                Snapshot.Build(null, scope.Mode, Host.Version(), "game-backend"));
        }

        internal string Refresh(ModeScope scope, string preferredLineId)
        {
            Ready();
            return Workbenches.Json.Write(
                Snapshot.Build(
                    !string.IsNullOrEmpty(preferredLineId) ? preferredLineId : Drafts.Preferred(scope.Mode),
                    scope.Mode,
                    Host.Version(),
                    "game-backend"));
        }

        internal string Meta(ModeScope scope, string preferredLineId)
        {
            Ready();
            return Workbenches.Json.Write(
                Snapshot.Meta(
                    !string.IsNullOrEmpty(preferredLineId) ? preferredLineId : Drafts.Preferred(scope.Mode),
                    scope.Mode,
                    Host.Version(),
                    "game-backend"));
        }

        internal string Save(string requestJson)
        {
            Ready();
            return Saves.Save(requestJson);
        }

        internal string Start(string requestJson)
        {
            Ready();
            return Saves.Start(requestJson);
        }

        internal string Status(string operationId)
        {
            return Saves.Status(operationId);
        }

        internal void Reset()
        {
            Saves.Reset();
            Persist.Reset();
            Host.Reset();
        }

        private void Ready()
        {
            Persist.Load();
            Sync.Ready();
        }
    }
}
