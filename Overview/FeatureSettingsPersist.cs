using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Overview
{
    [System.Runtime.Serialization.DataContract]
    internal sealed class OverviewFeatureSettingsPersistentState
    {
        [System.Runtime.Serialization.DataMember]
        public global::RapidTransitMod.RuntimeFeatureSettingsDto featureSettings;
    }

    internal sealed class FeatureSettingsPersist
    {
        private readonly EntityManager m_EntityManager;
        private readonly Func<Entity> m_City;
        private readonly global::RapidTransitMod.FeatureGate m_Features;
        private bool m_Dirty;
        private bool m_RestoredDedicatedState;

        internal FeatureSettingsPersist(
            EntityManager entityManager,
            Func<Entity> city,
            global::RapidTransitMod.FeatureGate features)
        {
            m_EntityManager = entityManager;
            m_City = city ?? throw new ArgumentNullException(nameof(city));
            m_Features = features ?? throw new ArgumentNullException(nameof(features));
        }

        internal void Reset()
        {
            m_Dirty = false;
            m_RestoredDedicatedState = false;
        }

        internal void MarkDirty()
        {
            m_Dirty = true;
        }

        internal void SaveIfDirty()
        {
            if (!m_Dirty)
                return;

            Save();
            m_Dirty = false;
        }

        internal bool Restore()
        {
            Entity city = m_City();
            if (city == Entity.Null
                || !m_EntityManager.Exists(city)
                || !m_EntityManager.HasBuffer<OverviewFeatureSettingsStateElement>(city))
            {
                return false;
            }

            var buffer = m_EntityManager.GetBuffer<OverviewFeatureSettingsStateElement>(city, true);
            if (buffer.Length == 0)
            {
                return true;
            }

            try
            {
                string payload = FeatureSettingsBuffer.Read(buffer);
                OverviewFeatureSettingsPersistentState persisted =
                    string.IsNullOrEmpty(payload)
                        ? null
                        : global::RapidTransitMod.Workbenches.Json.Read<OverviewFeatureSettingsPersistentState>(payload);
                if (persisted?.featureSettings != null)
                {
                    m_Features.Apply(persisted.featureSettings);
                    m_RestoredDedicatedState = true;
                }

                return true;
            }
            catch (Exception ex)
            {
                Mod.log.Info("[OverviewFeatureSettingsPersist] Restore failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private void Save()
        {
            Entity city = m_City();
            if (city == Entity.Null || !m_EntityManager.Exists(city))
            {
                return;
            }

            FeatureSettingsBuffer.Ensure(m_EntityManager, city);
            if (!m_EntityManager.HasBuffer<OverviewFeatureSettingsStateElement>(city))
            {
                Mod.log.Info("[OverviewFeatureSettingsPersist] Save skipped: buffer unavailable");
                return;
            }

            OverviewFeatureSettingsPersistentState state = new OverviewFeatureSettingsPersistentState
            {
                featureSettings = m_Features.Dto()
            };
            string payload = global::RapidTransitMod.Workbenches.Json.Write(state);
            List<string> chunks = FeatureSettingsBuffer.Split(payload);
            var buffer = m_EntityManager.GetBuffer<OverviewFeatureSettingsStateElement>(city);
            FeatureSettingsBuffer.Write(buffer, chunks);
        }

        internal void MigrateLegacy(global::RapidTransitMod.RuntimeFeatureSettingsDto settings)
        {
            if (settings == null || m_RestoredDedicatedState)
            {
                return;
            }

            m_Features.Apply(settings);
            MarkDirty();
        }
    }
}
