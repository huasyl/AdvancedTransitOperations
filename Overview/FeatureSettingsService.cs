using System;

namespace RapidTransitMod.Overview
{
    internal sealed class FeatureSettingsService
    {
        private readonly global::RapidTransitMod.FeatureGate m_Features;
        private readonly Action m_SaveFeatureSettings;
        private readonly Func<string> m_Version;

        internal FeatureSettingsService(
            global::RapidTransitMod.FeatureGate features,
            Action saveFeatureSettings,
            Func<string> version)
        {
            m_Features = features ?? throw new ArgumentNullException(nameof(features));
            m_SaveFeatureSettings = saveFeatureSettings ?? throw new ArgumentNullException(nameof(saveFeatureSettings));
            m_Version = version ?? throw new ArgumentNullException(nameof(version));
        }

        internal OverviewFeatureSettingsResultDto Apply(OverviewFeatureSettingsRequestDto request)
        {
            if (request?.featureSettings == null)
            {
                return new OverviewFeatureSettingsResultDto
                {
                    success = false,
                    errors = new[] { "feature-settings-missing" },
                    version = m_Version(),
                    featureSettings = m_Features.Dto()
                };
            }

            m_Features.Apply(request.featureSettings);
            m_SaveFeatureSettings();

            return new OverviewFeatureSettingsResultDto
            {
                success = true,
                errors = Array.Empty<string>(),
                version = m_Version(),
                featureSettings = m_Features.Dto()
            };
        }
    }
}
