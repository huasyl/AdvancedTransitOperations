using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Game.SceneFlow;
using Game.UI.InGame;
using Game.UI.Localization;
using Game.UI.Menu;
using RapidTransitMod.Broadcasting.WorkbenchBackend;
using Unity.Entities;

namespace RapidTransitMod.Broadcasting
{
    internal sealed class UnregisteredAudio
    {
        private bool m_Open;
        private bool m_Queried;
        private bool m_ProtectionDirty = true;
        private bool m_TextDirty = true;
        private int m_Session;
        private int m_DeleteSession;
        private string m_Root;
        private string m_Locale;
        private string m_Error = string.Empty;
        private string m_State = "querying";
        private int m_Deleted;
        private int m_Failed;
        private int m_ProtectionVersion = -1;
        private List<string> m_Files = new List<string>();
        private AssetStore.Cleanup m_Cleanup;
        private Task<List<string>> m_Query;
        private HashSet<string> m_Protected;
        private LocalizedString m_Text = LocalizedString.Id("RapidTransit.BroadcastAudio.Querying");
        internal bool DeletedBatch { get; private set; }

        internal LocalizedString Text(AssetLifecycle lifecycle)
        {
            if (!m_Open && IsOptionsOpen())
            {
                m_Open = true;
                m_State = "querying";
                m_Error = string.Empty;
                m_TextDirty = true;
            }
            string locale = GameManager.instance?.localizationManager?.activeLocaleId;
            if (m_TextDirty || m_Locale != locale) RebuildText(locale);
            return m_Text;
        }

        internal bool Disabled(AssetLifecycle lifecycle)
            => !m_Open || !m_Queried || m_Query != null || m_Cleanup != null
                || m_Files.Count == 0 || lifecycle.StorageBusy || !AssetStore.CanClean;

        internal bool HideDelete => m_Queried && m_Query == null && m_Cleanup == null && m_Files.Count == 0
            && (m_State == "empty" || m_State == "completed") && m_Error.Length == 0;

        internal void Delete(AssetLifecycle lifecycle)
        {
            if (Disabled(lifecycle)) return;
            m_DeleteSession = m_Session;
            try { m_Cleanup = AssetStore.BeginManual(m_Files, m_Protected, Removed); }
            catch (Exception ex) { Fail(ex); return; }
            if (m_Cleanup == null) return;
            m_Deleted = 0;
            m_Failed = 0;
            m_Error = string.Empty;
            m_State = "deleting";
            m_ProtectionDirty = true;
            m_TextDirty = true;
        }

        internal void ProtectionChanged() => m_ProtectionDirty = true;

        internal void Close()
        {
            m_Open = false;
            m_Queried = false;
            m_Query = null;
            m_Files.Clear();
            m_Protected = null;
            m_ProtectionDirty = true;
            m_TextDirty = true;
            m_Session++;
        }

        internal void Update(AssetLifecycle lifecycle)
        {
            DeletedBatch = false;
            if (m_Open && !IsOptionsOpen()) Close();
            if (!m_Open && m_Cleanup == null) return;
            if (lifecycle.StorageBusy) return;
            if (!AssetStore.CanClean)
            {
                if (m_Cleanup != null) Finish();
                if (m_Open && (m_State != "failed" || m_Error != "delete-failed"))
                {
                    m_Query = null;
                    m_Queried = true;
                    m_State = "failed";
                    m_Error = "delete-failed";
                    m_TextDirty = true;
                }
                return;
            }
            if (m_ProtectionDirty || m_ProtectionVersion != AssetStore.ProtectionVersion)
            {
                m_Protected = lifecycle.ProtectedPaths();
                m_Cleanup?.UpdateProtection(m_Protected);
                m_ProtectionDirty = false;
                m_ProtectionVersion = AssetStore.ProtectionVersion;
                if (m_Files.RemoveAll(file => m_Protected.Contains(AssetStore.LegacyPath(file))) != 0)
                {
                    if (m_State == "ready" && m_Files.Count == 0) m_State = "empty";
                    m_TextDirty = true;
                }
            }
            if (m_Cleanup != null)
            {
                DeletedBatch = true;
                if (m_Cleanup.Step())
                {
                    bool saved = AssetStore.Flush();
                    if (m_DeleteSession == m_Session)
                    {
                        m_Deleted = m_Cleanup.Deleted;
                        m_Failed = m_Cleanup.Failed;
                        m_Error = saved ? m_Cleanup.Error : "delete-failed";
                    }
                    Finish();
                }
                return;
            }
            if (!m_Queried)
            {
                m_Queried = true;
                m_Root = AssetScope.RootDir();
                string root = m_Root;
                m_Query = Task.Run(() =>
                {
                    try { return AssetStore.EnumerateAudio(root); }
                    catch (Exception ex)
                    {
                        Mod.log.Info("Broadcast unregistered audio enumeration failed. Root: " + root + "\n" + ex.ToString());
                        return null;
                    }
                });
            }
            if (m_Query == null || !m_Query.IsCompleted) return;
            Task<List<string>> query = m_Query;
            m_Query = null;
            if (query.IsFaulted)
            {
                Fail(query.Exception);
                return;
            }
            m_Protected = lifecycle.ProtectedPaths();
            m_Files = query.Result;
            if (m_Files == null)
            {
                m_Files = new List<string>();
                m_State = "failed";
                m_Error = "delete-failed";
                m_TextDirty = true;
                return;
            }
            m_Files.RemoveAll(file => m_Protected.Contains(AssetStore.LegacyPath(file)));
            m_State = m_Files.Count == 0 ? "empty" : "ready";
            m_Error = string.Empty;
            m_TextDirty = true;
        }

        internal void Fail(Exception ex)
        {
            m_Query = null;
            m_Queried = true;
            m_Error = AssetStore.DeleteError(ex);
            m_State = "failed";
            m_Cleanup = null;
            m_TextDirty = true;
            Mod.log.Info("Broadcast unregistered audio processing failed.\n" + ex.ToString());
        }

        private void Finish()
        {
            m_Cleanup = null;
            if (m_DeleteSession != m_Session) return;
            m_State = "completed";
            m_TextDirty = true;
        }

        private void Removed(string path)
        {
            if (m_DeleteSession == m_Session) m_Files.Remove(path);
        }

        private void RebuildText(string locale)
        {
            var text = new StringBuilder();
            text.Append(Translate("Folder")).Append(' ').Append((m_Root ?? AssetScope.RootDir()).Replace('\\', '/'));
            text.Append('\n');
            text.Append(Translate(m_State == "querying" ? "Querying" : m_State == "deleting" ? "Deleting"
                : m_State == "empty" || m_State == "completed" && m_Files.Count == 0 && m_Error.Length == 0 ? "Empty"
                : m_State == "failed" || m_State == "completed" && m_Files.Count == 0 && m_Error.Length != 0 ? "Failed" : "List"));
            foreach (string file in m_Files)
                text.Append('\n').Append(file.Substring(m_Root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'));
            if (m_State == "completed")
                text.Append('\n').Append(Translate("Result").Replace("{deleted}", m_Deleted.ToString()).Replace("{failed}", m_Failed.ToString()));
            if (m_Error.Length != 0)
                text.Append('\n').Append(Translate(m_Error == "delete-access" ? "Access" : m_Error == "delete-busy" ? "Busy" : "Failed"));
            m_Text = LocalizedString.Value(text.ToString());
            m_Locale = locale;
            m_TextDirty = false;
        }

        private static string Translate(string key)
        {
            string id = "RapidTransit.BroadcastAudio." + key;
            var dictionary = GameManager.instance?.localizationManager?.activeDictionary;
            return dictionary != null && dictionary.TryGetValue(id, out string value) ? value : id;
        }

        private static bool IsOptionsOpen()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            return GameManager.instance.gameMode == Game.GameMode.Game
                ? world.GetExistingSystemManaged<GameScreenUISystem>()?.activeScreen == GameScreenUISystem.GameScreen.Options
                : world.GetExistingSystemManaged<MenuUISystem>()?.activeScreen == MenuUISystem.MenuScreen.Options;
        }
    }
}
