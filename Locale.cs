using System.Collections.Generic;
using Colossal;

namespace RapidTransitMod
{
    public class Locale : IDictionarySource
    {
        private readonly Dictionary<string, string> m_LocaleDict;

        public Locale(Dictionary<string, string> localeDict)
        {
            m_LocaleDict = localeDict;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return m_LocaleDict;
        }

        public void Unload()
        {
        }
    }
}
