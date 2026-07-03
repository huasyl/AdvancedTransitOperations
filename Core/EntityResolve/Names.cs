using System;
using System.Reflection;
using Game.UI;

namespace RapidTransitMod
{
    internal sealed class Names
    {
        private static readonly FieldInfo s_NameTypeField =
            typeof(NameSystem.Name).GetField("m_NameType", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_NameIdField =
            typeof(NameSystem.Name).GetField("m_NameID", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_NameArgsField =
            typeof(NameSystem.Name).GetField("m_NameArgs", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly NameSystem m_NameSystem;

        internal Names(NameSystem nameSystem)
        {
            m_NameSystem = nameSystem;
        }

        internal string Get(Unity.Entities.Entity entity)
        {
            return Lookup(entity);
        }

        internal string Lookup(Unity.Entities.Entity entity)
        {
            string translatedName = Translated(entity);
            if (!string.IsNullOrEmpty(translatedName))
            {
                return translatedName;
            }

            string renderedLabel = Rendered(entity);
            if (!string.IsNullOrEmpty(renderedLabel))
            {
                return renderedLabel;
            }

            return string.Empty;
        }

        internal string Translated(Unity.Entities.Entity entity)
        {
            try
            {
                return Translate(m_NameSystem.GetName(entity));
            }
            catch
            {
                return string.Empty;
            }
        }

        internal string Rendered(Unity.Entities.Entity entity)
        {
            try
            {
                return m_NameSystem.GetRenderedLabelName(entity);
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string Translate(NameSystem.Name name)
        {
            if (s_NameTypeField == null || s_NameIdField == null)
            {
                return string.Empty;
            }

            string nameId = s_NameIdField.GetValue(name) as string;
            if (string.IsNullOrEmpty(nameId))
            {
                return string.Empty;
            }

            NameSystem.NameType nameType = (NameSystem.NameType)s_NameTypeField.GetValue(name);
            switch (nameType)
            {
                case NameSystem.NameType.Custom:
                    return nameId;
                case NameSystem.NameType.Localized:
                    return Key(nameId);
                case NameSystem.NameType.Formatted:
                    return Format(nameId, s_NameArgsField?.GetValue(name) as string[]);
                default:
                    return nameId;
            }
        }

        internal static string Format(string nameId, string[] nameArgs)
        {
            string template = Key(nameId);
            if (string.IsNullOrEmpty(template) || nameArgs == null || nameArgs.Length < 2)
            {
                return template;
            }

            for (int i = 0; i + 1 < nameArgs.Length; i += 2)
            {
                string token = nameArgs[i] ?? string.Empty;
                string valueKey = nameArgs[i + 1] ?? string.Empty;
                string replacement = Key(valueKey);
                template = template.Replace("{" + token + "}", replacement);
            }

            return template;
        }

        internal static string Key(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            if (Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary != null
                && Game.SceneFlow.GameManager.instance.localizationManager.activeDictionary.TryGetValue(key, out string translated))
            {
                return translated;
            }

            return key;
        }
    }
}
