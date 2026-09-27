#if DEBUG
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DevTools
{
    // Debug builds only: the commands DevServer accepts, one line in, text out. They exist so that
    // a test can be set up and read back without anyone clicking through the edit-skill screen -
    // put a memory in a slot, socket essences beside it, ask AreMyGemsCompatible what it thinks.
    //
    // Everything here runs on the main thread, handed over by DevServer, and everything that
    // changes the loadout is server-only for the same reason the panel's buttons are.
    internal static class DevCommands
    {
        private const string Help =
            "help                               this list\n" +
            "state                              every memory and essence on the hero\n" +
            "memory <slot> <St_Type> [level]    put a new memory in a slot; the old one is destroyed, its essences move over\n" +
            "gem <slot> <index> <Gem_Type> [q]  socket a new essence; whatever was in that slot, or anywhere of that type, is destroyed\n" +
            "cleargems [slot]                   destroy every essence, or those in one slot\n" +
            "slots <slot> <count>               set the essence slot count, as the panel rows do\n" +
            "verdict [slot]                     AreMyGemsCompatible's verdict for each essence\n" +
            "tooltip <slot> <index>             open that essence slot's tooltip, as hovering it does, and return its text\n" +
            "hide                               close the tooltip\n" +
            "screenshot [name]                  save a PNG of the screen at the end of this frame; returns its path\n" +
            "slots are Q W E R Identity Movement";

        public static string Run(string line)
        {
            var words = (line ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return Help;

            switch (words[0].ToLowerInvariant())
            {
                case "help": return Help;
                case "state": return State();
                case "memory": return Memory(words);
                case "gem": return Gem(words);
                case "cleargems": return ClearGems(words);
                case "slots": return Slots(words);
                case "verdict": return Verdict(words);
                case "tooltip": return Tooltip(words);
                case "hide": return Hide();
                case "screenshot": return Screenshot(words);
                default: return "unknown command '" + words[0] + "'\n" + Help;
            }
        }

        private static string State()
        {
            var hero = DevActions.LocalHero;
            if (hero == null || hero.Skill == null) return "no hero";

            var text = new StringBuilder(hero.GetType().Name);
            foreach (HeroSkillLocation where in Enum.GetValues(typeof(HeroSkillLocation)))
            {
                SkillTrigger skill;
                hero.Skill.TryGetSkill(where, out skill);
                text.Append('\n').Append(where).Append(": ").Append(skill != null ? skill.GetType().Name : "-")
                    .Append(" slots=").Append(hero.Skill.GetMaxGemCount(where));

                foreach (var pair in hero.Skill.gems.Where(p => p.Key.skill == where).OrderBy(p => p.Key.index))
                    text.Append(" [").Append(pair.Key.index).Append("] ").Append(pair.Value != null ? pair.Value.GetType().Name : "?");
            }
            return text.ToString();
        }

        private static string Memory(string[] words)
        {
            if (words.Length < 3) return "memory <slot> <St_Type> [level]";
            if (!DevActions.CanAct(out string reason)) return reason;
            if (!TryParseSlot(words[1], out var where)) return "no slot named " + words[1];

            var template = Find<SkillTrigger>(words[2]);
            if (template == null) return "no memory named " + words[2];
            int level = words.Length > 3 && int.TryParse(words[3], out var parsed) ? Mathf.Max(1, parsed) : 1;

            var hero = DevActions.LocalHero;
            SkillTrigger old;
            hero.Skill.TryGetSkill(where, out old);

            var skill = Dew.CreateSkillTrigger(template, hero.position, level, DevActions.LocalPlayer, null);

            // ignoreCanReplace, because a test memory is often another hero's own, which the
            // ordinary path refuses outright.
            hero.Skill.EquipSkill(where, skill, ignoreCanReplace: true);

            // The old one was dropped at the hero's feet by EquipSkill; a test run would otherwise
            // leave a trail of them.
            if (old != null && old != skill) old.Destroy();
            return where + ": " + skill.GetType().Name + " +" + (level - 1) + " (was " + (old != null ? old.GetType().Name : "empty") + ")";
        }

        private static string Gem(string[] words)
        {
            if (words.Length < 4) return "gem <slot> <index> <Gem_Type> [quality]";
            if (!DevActions.CanAct(out string reason)) return reason;
            if (!TryParseSlot(words[1], out var where)) return "no slot named " + words[1];
            if (!int.TryParse(words[2], out int index) || index < 0) return "bad index " + words[2];

            var template = Find<Gem>(words[3]);
            if (template == null) return "no essence named " + words[3];
            int quality = words.Length > 4 && int.TryParse(words[4], out var parsed) ? Mathf.Max(1, parsed) : 100;

            var hero = DevActions.LocalHero;
            var skills = hero.Skill;
            int max = skills.GetMaxGemCount(where);
            if (index >= max) return where + " has " + max + " slots; set more with 'slots " + where + " <count>'";

            // The game refuses a second essence of a type already worn, so that one goes too.
            var location = new GemLocation(where, index);
            foreach (var pair in skills.gems.ToList())
            {
                if (pair.Value == null) continue;
                if (!pair.Key.Equals(location) && pair.Value.GetType() != template.GetType()) continue;
                var removed = skills.UnequipGem(pair.Key, hero.agentPosition);
                if (removed != null) removed.Destroy();
            }

            var gem = Dew.CreateGem(template, hero.position, quality, DevActions.LocalPlayer, null);
            skills.EquipGem(location, gem);
            return where + "[" + index + "]: " + gem.GetType().Name + " q" + quality;
        }

        private static string ClearGems(string[] words)
        {
            if (!DevActions.CanAct(out string reason)) return reason;

            HeroSkillLocation where = default(HeroSkillLocation);
            bool one = words.Length > 1;
            if (one && !TryParseSlot(words[1], out where)) return "no slot named " + words[1];

            var hero = DevActions.LocalHero;
            int count = 0;
            foreach (var pair in hero.Skill.gems.ToList())
            {
                if (one && pair.Key.skill != where) continue;
                var removed = hero.Skill.UnequipGem(pair.Key, hero.agentPosition);
                if (removed != null) removed.Destroy();
                count++;
            }
            return "removed " + count;
        }

        private static string Slots(string[] words)
        {
            if (words.Length < 3) return "slots <slot> <count>";
            if (!TryParseSlot(words[1], out var where)) return "no slot named " + words[1];
            if (!int.TryParse(words[2], out int count)) return "bad count " + words[2];

            // Identity and Movement go through the panel's own action; the Q/W/E/R counts belong
            // to MoreGemSlots when it is loaded and to the game otherwise.
            if (where == HeroSkillLocation.Identity || where == HeroSkillLocation.Movement)
                return DevActions.SetGemSlots(where, count);

            if (!DevActions.CanAct(out string reason)) return reason;
            DevActions.LocalHero.Skill.SetMaxGemCount(where, count);
            return where + " slots -> " + count + " (MoreGemSlots, if loaded, may write its own count back)";
        }

        private static string Verdict(string[] words)
        {
            var hero = DevActions.LocalHero;
            if (hero == null || hero.Skill == null) return "no hero";

            HeroSkillLocation where = default(HeroSkillLocation);
            bool one = words.Length > 1;
            if (one && !TryParseSlot(words[1], out where)) return "no slot named " + words[1];

            var describe = FindDescribe();
            if (describe == null) return "AreMyGemsCompatible is not loaded, or has no Verdict.Describe";

            var text = new StringBuilder();
            foreach (var pair in hero.Skill.gems.OrderBy(p => p.Key.skill).ThenBy(p => p.Key.index))
            {
                if (one && pair.Key.skill != where) continue;
                var gem = pair.Value;
                string answer;
                try { answer = (string)describe.Invoke(null, new object[] { gem, gem != null ? gem.skill : null }); }
                catch (TargetInvocationException e) { answer = "threw " + e.InnerException; }

                if (text.Length > 0) text.Append('\n');
                text.Append(pair.Key.skill).Append('[').Append(pair.Key.index).Append("] ")
                    .Append(gem != null ? gem.GetType().Name : "?").Append(" in ")
                    .Append(gem != null && gem.skill != null ? gem.skill.GetType().Name : "-")
                    .Append(": ").Append(answer);
            }
            return text.Length > 0 ? text.ToString() : "no essences";
        }

        // The slot widget's own ShowTooltip, which is what hovering calls through
        // UI_TooltipManager - so every postfix on the way, AreMyGemsCompatible's warning line
        // included, runs exactly as it does for the mouse. The tooltip stays up until the manager
        // is next asked to update, which moving the mouse over the interface does.
        private static string Tooltip(string[] words)
        {
            if (words.Length < 3) return "tooltip <slot> <index>";
            if (!TryParseSlot(words[1], out var where)) return "no slot named " + words[1];
            if (!int.TryParse(words[2], out int index)) return "bad index " + words[2];

            var manager = SingletonBehaviour<UI_TooltipManager>.instance;
            if (manager == null) return "no tooltip manager";

            var slot = UnityEngine.Object.FindObjectsByType<UI_InGame_GemSlot>(FindObjectsSortMode.None)
                .FirstOrDefault(s => s != null && s.button != null && s.button.skillType == where && s.slotIndex == index);
            if (slot == null) return "no essence slot widget " + where + "[" + index + "] on screen";

            manager.Hide();
            slot.ShowTooltip(manager);
            if (!manager.isShowing) return "no tooltip - is the slot empty?";

            var text = new StringBuilder();
            foreach (var label in manager.GetComponentsInChildren<TMPro.TMP_Text>(false))
            {
                var plain = System.Text.RegularExpressions.Regex.Replace(label.text ?? string.Empty, "<[^>]+>", "").Trim();
                if (plain.Length == 0) continue;
                if (text.Length > 0) text.Append('\n');
                text.Append(plain);
            }
            return text.Length > 0 ? text.ToString() : "tooltip shown, but no text in it yet";
        }

        private static string Hide()
        {
            var manager = SingletonBehaviour<UI_TooltipManager>.instance;
            if (manager == null) return "no tooltip manager";
            manager.Hide();
            return "hidden";
        }

        // Written at the end of the frame by Unity, so the file appears a moment after the answer.
        // A tooltip fades in over a few frames, so a screenshot wants a separate command after the
        // one that opened it rather than the same one.
        private static string Screenshot(string[] words)
        {
            string name = words.Length > 1 ? words[1] : DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

            string directory = System.IO.Path.Combine(Application.persistentDataPath, "DevTools screenshots");
            System.IO.Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, name + ".png");

            ScreenCapture.CaptureScreenshot(path);
            return path;
        }

        // Found at call time rather than cached: the other mod can be reloaded underneath this one.
        private static MethodInfo FindDescribe()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("AreMyGemsCompatible.Verdict", false);
                var method = type?.GetMethod("Describe", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method != null) return method;
            }
            return null;
        }

        private static T Find<T>(string typeName) where T : UnityEngine.Object
        {
            // Checked first: GetByShortTypeName logs an error for a name it does not know.
            var database = DewResources.database;
            if (database == null || database.typeNameToType == null || !database.typeNameToType.ContainsKey(typeName)) return null;
            return DewResources.GetByShortTypeName<T>(typeName);
        }

        private static bool TryParseSlot(string text, out HeroSkillLocation where)
        {
            return Enum.TryParse(text, true, out where) && Enum.IsDefined(typeof(HeroSkillLocation), where);
        }
    }
}
#endif
