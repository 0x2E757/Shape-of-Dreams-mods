using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AreMyGemsCompatible
{
    // What a memory is capable of: the four things an essence can wait for and never get.
    internal struct MemoryFacts
    {
        public bool DealsDamage;
        public bool Heals;
        public bool Shields;

        // False only for a passive memory - an identity - which PassiveMemory describes.
        public bool IsCast;

        // The elements the dump says this memory deals: "Fire damage" in the description, or the
        // element among its tags. Only part of the answer - see MemoryElements, which adds what
        // the memory's code and prefabs say.
        public ElementSet Elements;

        // A memory the dump does not mention at all - one a later patch adds, or one another mod
        // brings - is not "does nothing", it is "not known", and nothing is said about it.
        public bool IsKnown;
    }

    // The game writes a full dump of every memory and essence into RawData\<language>\, one folder
    // per shipped language, and the Readme there says plainly what it is for: wikis, guides and
    // community tooling. It carries the description, the scaling variables behind it by their
    // authored field names, the rarity, the tags and the slot a hero memory belongs to.
    //
    // **Only memories are read out of it, and only in en-US.**
    //
    // Memories, because there is nothing else to read. SkillTrigger subclasses are nearly empty -
    // St_C_IceBlock declares nothing but Mirror's generated stub - since a memory's behaviour
    // lives in its prefab, in the ability instances its TriggerConfigs point at. There is no code
    // to ask.
    //
    // The exception is a passive memory, which is what an identity is. There the prose is what is
    // unreliable - it describes the hero's stats and attacks, not the memory's own doing - and
    // there is code to ask, so PassiveMemory answers instead. The dump still decides whether the
    // memory is known at all.
    //
    // Essences are not read here at all, though the file beside this one describes them just as
    // fully, and that is a decision rather than an omission. An essence *does* have code, and its
    // code is exact where its description is not: a description covers the essence entire, damage
    // *taken* and stat bonuses and amplifications included, so Gem_E_Protection reads as a source
    // of damage on the strength of "reducing damage taken" and Gem_E_Overload reads as a source of
    // healing when all it does is multiply someone else's. GemTriggers answers that side by
    // reading IL, and gets it right for all ninety-five.
    //
    // en-US, because the answers below come out of prose, and prose is what a translation changes:
    // every locale would need its own vocabulary for damage, healing and barriers, would need it
    // re-checked on every patch, and would be silently wrong in whichever languages nobody tested.
    // English is the language the values were authored in and the one the field names are in. The
    // player's language decides only what the warning says, never whether there is one.
    internal static class MemoryData
    {
        private const string Language = "en-US";

        // Damage is the easy one: a memory that deals any says so, and the scaling variable behind
        // the number is named for it. dmg matches dmgFactor, which is the game's usual spelling.
        //
        // Except where damage is a stat or is suffered: "gain {2} Attack Damage" is the whole of
        // what Hero's Return says about damage, and it deals none - in a fight, an on-hit essence
        // in it never fired. Over the shipped dump this changes Hero's Return and The Killing Flow
        // and nothing else.
        private static readonly Regex DamageProse = new Regex(
            @"(?<!\battack\s)(?<!\bability\s)\bdamage\b(?!\s+(taken|you take|reduction))", RegexOptions.IgnoreCase);
        private static readonly Regex DamageVar = new Regex(@"dmg|damage", RegexOptions.IgnoreCase);

        // Healing is the one worth being careful about, and both directions cost something. The
        // word heal has to stop at a word boundary or "maximum health" reads as healing and every
        // damage memory looks like a healer; but the game also says "restores Health" and
        // "recovers Health" without ever using the word, so those are matched near the word Health
        // rather than anywhere. On the variable side the boundary cannot be used - healAmount and
        // healLostHealthRatio are both real - so heal is matched as a prefix and (?!th) keeps it
        // off summonHealth.
        private static readonly Regex HealProse = new Regex(
            @"\bheal(s|ed|ing)?\b|lifesteal|life steal|omnivamp|regenerat|(restor|recover)\w*[^.]{0,40}\bhealth\b",
            RegexOptions.IgnoreCase);
        private static readonly Regex HealVar = new Regex(@"heal(?!th)", RegexOptions.IgnoreCase);

        // The game's word for a shield is Barrier in prose and shield in the field names, and it
        // uses both.
        private static readonly Regex ShieldProse = new Regex(@"\bbarrier\b|\bshield(s|ed|ing)?\b", RegexOptions.IgnoreCase);
        private static readonly Regex ShieldVar = new Regex(@"shield|barrier", RegexOptions.IgnoreCase);

        // An element is named in prose right before the word damage - "deal an additional {2} Fire
        // damage" - and among the tags by the same four words the enum uses. Both are read, and
        // either is enough: tags say what a memory is, which for an element is also what it deals.
        private static readonly Regex ElementProse = new Regex(@"\b(Fire|Cold|Light|Dark)\s+damage\b", RegexOptions.IgnoreCase);

        // Rich text is stripped before matching. The rendered numbers carry <sprite=1>, <gradient>
        // and <color> markup, and a tag name is not something to match words inside.
        private static readonly Regex RichText = new Regex("<[^>]+>");

        private static Dictionary<string, MemoryFacts> _memories;
        private static bool _loaded;

        public static MemoryFacts Get(SkillTrigger skill)
        {
            if (skill == null) return default(MemoryFacts);

            var described = Get(skill.GetType().Name);
            if (!described.IsKnown) return described;

            return PassiveMemory.IsPassive(skill) ? PassiveMemory.Read(skill) : described;
        }

        public static MemoryFacts Get(string typeName)
        {
            Load();
            MemoryFacts facts;
            if (_memories != null && _memories.TryGetValue(typeName, out facts)) return facts;
            return default(MemoryFacts);
        }

        public static void Reset()
        {
            _loaded = false;
            _memories = null;
            PassiveMemory.Reset();
            MemoryElements.Reset();
            ElementChangers.Reset();
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            // Application.dataPath is <install>\Shape of Dreams_Data; RawData sits beside it.
            string root;
            try
            {
                root = Path.GetDirectoryName(Application.dataPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AreMyGemsCompatible] cannot resolve the install directory: " + e.Message);
                return;
            }
            if (string.IsNullOrEmpty(root)) return;

            string dir = Path.Combine(Path.Combine(root, "RawData"), Language);
            _memories = Read(Path.Combine(dir, "memories.json"));
        }

        private static Dictionary<string, MemoryFacts> Read(string path)
        {
            var root = ReadObject(path);
            if (root == null) return null;

            var result = new Dictionary<string, MemoryFacts>(root.Count);
            foreach (var entry in root)
            {
                var value = entry.Value as JObject;
                if (value == null) continue;

                string prose = Prose(value);
                string vars = Vars(value);

                result[entry.Key] = new MemoryFacts
                {
                    IsKnown = true,
                    IsCast = true,
                    DealsDamage = DamageProse.IsMatch(prose) || DamageVar.IsMatch(vars),
                    Heals = HealProse.IsMatch(prose) || HealVar.IsMatch(vars),
                    Shields = ShieldProse.IsMatch(prose) || ShieldVar.IsMatch(vars),
                    Elements = ElementsOf(prose, value),
                };
            }

            Debug.Log("[AreMyGemsCompatible] read " + result.Count + " memories from " + Language);
            return result;
        }

        private static JObject ReadObject(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Debug.LogWarning("[AreMyGemsCompatible] no game data at " + path + "; no warnings will be shown");
                    return null;
                }
                return JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AreMyGemsCompatible] could not read " + path + ": " + e.Message);
                return null;
            }
        }

        // rawDesc rather than description: the same sentence with {0} where the numbers go, which
        // is shorter and carries no rendered value that could read as a word.
        private static string Prose(JObject entry)
        {
            var raw = entry["rawDesc"];
            string text = raw != null ? (string)raw : null;
            if (string.IsNullOrEmpty(text))
            {
                var described = entry["description"];
                text = described != null ? (string)described : null;
            }
            return string.IsNullOrEmpty(text) ? string.Empty : RichText.Replace(text, " ");
        }

        private static ElementSet ElementsOf(string prose, JObject entry)
        {
            var found = ElementSet.None;
            foreach (Match match in ElementProse.Matches(prose)) found |= Element(match.Groups[1].Value);

            var tags = entry["tags"] as JArray;
            if (tags != null)
                foreach (var tag in tags)
                    found |= Element((string)tag);

            return found;
        }

        private static ElementSet Element(string word)
        {
            if (string.IsNullOrEmpty(word)) return ElementSet.None;
            ElementalType type;
            return Enum.TryParse(word, true, out type) && Enum.IsDefined(typeof(ElementalType), type)
                ? ElementGates.From(type)
                : ElementSet.None;
        }

        // The authored field names behind each number, which say what the number is for in a
        // language no translation touches.
        private static string Vars(JObject entry)
        {
            var list = entry["rawDescVars"] as JArray;
            if (list == null) return string.Empty;

            var builder = new System.Text.StringBuilder();
            foreach (var item in list)
            {
                var name = item["raw"];
                if (name == null) continue;
                builder.Append((string)name).Append(' ');
            }
            return builder.ToString();
        }
    }
}
