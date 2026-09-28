using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DevTools
{
    internal enum CatalogKind
    {
        Essence,
        Memory,
    }

    // One essence or memory the game has. Everything but the type is read lazily: the names from
    // the localisation tables, which are keyed by type name and cost nothing, and the rest from
    // the "light" variant of the asset - the one the Collection screen and the loot pool load,
    // stripped of models and effects but still carrying rarity, the pool flag and the icon.
    internal sealed class CatalogEntry
    {
        public readonly CatalogKind kind;
        public readonly Type type;
        public readonly string typeName;

        private bool _loaded;
        private Rarity _rarity;
        private bool _excludedFromPool;
        private Sprite _icon;

        private string _nameLanguage;
        private string _name;
        private string _description;

        public CatalogEntry(CatalogKind kind, Type type)
        {
            this.kind = kind;
            this.type = type;
            typeName = type.Name;
        }

        public bool IsLoaded => _loaded;

        public Rarity Rarity { get { Load(); return _rarity; } }

        public Sprite Icon { get { Load(); return _icon; } }

        public string Name { get { Localize(); return _name; } }

        public string Description { get { Localize(); return _description; } }

        // Monster skills, heroes' own kits, anything the game keeps out of the pool or out of this
        // build. Listed on request, since spawning them is sometimes exactly the test.
        //
        // Reads only what is already loaded, so that filtering does not load everything at once;
        // an entry can turn hidden when its asset arrives.
        public bool IsHidden
        {
            get
            {
                if (kind == CatalogKind.Essence)
                    return !Dew.IsGemIncludedInGame(typeName) || Dew.IsExcludedFromPool(typeName) || _excludedFromPool;

                return typeName.StartsWith("St_M_", StringComparison.Ordinal) || !Dew.IsSkillIncludedInGame(typeName) ||
                       Dew.IsExcludedFromPool(typeName) || _excludedFromPool || (_loaded && _rarity == Rarity.Character);
            }
        }

        public void Load()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (kind == CatalogKind.Essence)
                {
                    var gem = DewResources.GetByType<Gem>(type, ResourceLoadSettings.Light);
                    if (gem == null) return;
                    _rarity = gem.rarity;
                    _excludedFromPool = gem.excludeFromPool;
                    _icon = gem.icon;
                }
                else
                {
                    var skill = DewResources.GetByType<SkillTrigger>(type, ResourceLoadSettings.Light);
                    if (skill == null) return;
                    _rarity = skill.rarity;
                    _excludedFromPool = skill.excludeFromPool;
                    if (skill.configs != null && skill.configs.Length > 0 && skill.configs[0] != null)
                        _icon = skill.configs[0].triggerIcon;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DevTools] could not load " + typeName + ": " + e.Message);
            }
        }

        private void Localize()
        {
            string language = DewSave.profileMain != null ? DewSave.profileMain.language : "";
            if (_name != null && _nameLanguage == language) return;
            _nameLanguage = language;

            try
            {
                if (kind == CatalogKind.Essence)
                {
                    string key = DewLocalization.GetGemKey(type);
                    _name = GameAccess.Rich(DewLocalization.GetGemName(key));
                    _description = GameAccess.Rich(DewLocalization.GetGemShortDescription(key));
                }
                else
                {
                    string key = DewLocalization.GetSkillKey(type);
                    _name = GameAccess.Rich(DewLocalization.GetSkillName(key, 0));
                    _description = GameAccess.Rich(DewLocalization.GetSkillShortDesc(key, 0));
                }
            }
            catch (Exception)
            {
                _name = null;
                _description = null;
            }

            // A missing entry comes back as "gems.!Key.name"; the type name says more than that.
            if (string.IsNullOrEmpty(_name) || _name.Contains(".!")) _name = typeName;
            if (_description != null && _description.Contains(".!")) _description = "";
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return typeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // Every essence and every memory in the game, from Dew.allGems and Dew.allSkills - the type
    // lists the game builds once by scanning its assemblies. A type without an asset behind it is
    // left out; there is nothing to spawn.
    internal static class Catalog
    {
        private static List<CatalogEntry> _essences;
        private static List<CatalogEntry> _memories;

        public static bool Ready => DewResources.database != null && Dew.allGems != null && Dew.allSkills != null;

        public static IReadOnlyList<CatalogEntry> Essences
        {
            get { Build(); return _essences ?? (IReadOnlyList<CatalogEntry>)Array.Empty<CatalogEntry>(); }
        }

        public static IReadOnlyList<CatalogEntry> Memories
        {
            get { Build(); return _memories ?? (IReadOnlyList<CatalogEntry>)Array.Empty<CatalogEntry>(); }
        }

        public static IReadOnlyList<CatalogEntry> Of(CatalogKind kind) => kind == CatalogKind.Essence ? Essences : Memories;

        private static void Build()
        {
            if (_essences != null || !Ready) return;

            var database = DewResources.database;
            bool HasAsset(Type type) => database.typeToGuid != null && database.typeToGuid.ContainsKey(type);

            _essences = Dew.allGems.Where(HasAsset).OrderBy(t => t.Name)
                           .Select(t => new CatalogEntry(CatalogKind.Essence, t)).ToList();
            _memories = Dew.allSkills.Where(HasAsset).OrderBy(t => t.Name)
                           .Select(t => new CatalogEntry(CatalogKind.Memory, t)).ToList();
        }

        public static CatalogEntry Find(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            return Essences.FirstOrDefault(e => e.typeName.Equals(typeName, StringComparison.OrdinalIgnoreCase)) ??
                   Memories.FirstOrDefault(e => e.typeName.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        }

        public static CatalogEntry Require(string typeName)
        {
            var entry = Find(typeName);
            if (entry == null) throw new DevException("no essence or memory named '" + typeName + "'");
            return entry;
        }

        // Loads every light asset in one go - what the Collection screen does on opening. Used by
        // the API, where a filter on rarity needs them all; the picker loads only what it draws.
        public static void LoadAll(CatalogKind kind)
        {
            foreach (var entry in Of(kind)) entry.Load();
        }

        public static Color RarityColor(Rarity rarity)
        {
            try { return Dew.GetRarityColor(rarity); }
            catch (Exception) { return Color.white; }
        }
    }
}
