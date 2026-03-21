using System;
using System.Collections.Generic;
using System.IO;
using Colossal;
using Colossal.Json;
using Game.SceneFlow;

namespace RapidTransitMod
{
    public static class I18n
    {
        public static void LoadAll(string localesPath)
        {
            try
            {
                DirectoryInfo directory = new DirectoryInfo(localesPath);
                Mod.log.Info("Loading locales from directory: " + directory.FullName);
                if (!directory.Exists)
                {
                    Mod.log.Info("Locales directory not found.");
                    return;
                }

                FileInfo[] files = directory.GetFiles("*.json", SearchOption.AllDirectories);
                foreach (FileInfo file in files)
                {
                    Mod.log.Info("Loading " + file.Name);
                    try
                    {
                        Variant dict = JSON.Load(File.ReadAllText(file.FullName));
                        string locale = file.Name.Replace(file.Extension, string.Empty);
                        GameManager.instance.localizationManager.AddSource(locale, new Locale(dict.Make<Dictionary<string, string>>()));
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Info("Failed locale file: " + file.Name + " error=" + ex.GetType().Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.log.Info("LoadAll locales failed: " + ex.GetType().Name);
            }
        }
    }
}
