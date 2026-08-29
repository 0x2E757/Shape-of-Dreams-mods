using System.Collections.Generic;

namespace Shared
{
    // The game carries a full localization table, but it is keyed by its own content and offers no
    // way for a mod to register entries - DewLocalization exposes only lookups. What it does expose
    // is the selected language, on DewSave.profileMain.language, so a mod can keep its own strings
    // and pick by that.
    //
    // Language codes are the ones the game ships data for, one folder each under RawData. Anything
    // unrecognised, including a language added by a later patch, falls back to English.
    //
    // Only the lookup is shared. Each mod owns its own strings, which is the half that should never
    // be common to two mods.
    internal sealed class LanguageTable
    {
        private const string Fallback = "en-US";

        private readonly Dictionary<string, Dictionary<string, string>> _strings;

        public LanguageTable(Dictionary<string, Dictionary<string, string>> strings)
        {
            _strings = strings;
        }

        public static string CurrentLanguage
        {
            get
            {
                var profile = DewSave.profileMain;
                var language = profile != null ? profile.language : null;
                return string.IsNullOrEmpty(language) ? Fallback : language;
            }
        }

        public string Get(string key)
        {
            if (_strings.TryGetValue(CurrentLanguage, out var table) && table.TryGetValue(key, out var text))
                return text;
            if (_strings.TryGetValue(Fallback, out var english) && english.TryGetValue(key, out var fallback))
                return fallback;

            // The key itself, which is at least recognisable on screen as a missing string.
            return key;
        }
    }
}
