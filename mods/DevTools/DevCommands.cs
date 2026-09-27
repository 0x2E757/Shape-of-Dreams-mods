#if DEBUG
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Mirror;
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
            "down on|spectate|off               a real knockout; then the spectate camera, as co-op starts it; then a revive\n" +
            "edit on|off                        open or close the edit-skill screen, as its key does\n" +
            "loadout                            what decides whether the edit-skill screen can be seen\n" +
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
                case "down": return Down(words);
                case "edit": return Edit(words);
                case "loadout": return Loadout();
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

        // Co-op's knockout in a game of one, in the order co-op does it. The spectate camera starts
        // only when another player is standing, so BuildWhileDown's fight with it could not be
        // seen alone.
        //
        //   down on        a real knockout: Kill, which the death interrupt turns into
        //                  Se_HeroKnockedOut (or a bleed-out first, on difficulties that have one)
        //                  - stun, invisibility, soul and all. Game over is switched off, since a
        //                  party of one with nobody standing is otherwise over in four seconds.
        //   down spectate  what CameraManager.LogicUpdateSpectation does three seconds later when
        //                  a teammate is standing: isSpectating, then the event. The one part that
        //                  cannot be real is the teammate - the camera stays on this hero.
        //   down off       Se_HeroKnockedOut.Revive, as a teammate reaching the soul does; the
        //                  camera ends the spectating on its next logic update.
        private static bool _gameOverWas = true;

        private static string Down(string[] words)
        {
            if (words.Length < 2) return "down on|spectate|off";
            if (!NetworkServer.active) return "not the server";

            var hero = DevActions.LocalHero;
            var game = NetworkedManagerBase<GameManager>.instance;
            var camera = ManagerBase<CameraManager>.instance;
            if (hero == null || game == null || camera == null) return "not in a run";

            switch (words[1].ToLowerInvariant())
            {
                case "on":
                    if (hero.isKnockedOut) return "already down";
                    _gameOverWas = game.isGameOverEnabled;
                    game.isGameOverEnabled = false;
                    hero.Kill();
                    return "killed; knocked out " + hero.isKnockedOut + " (a bleed-out, if any, comes first)";

                case "spectate":
                {
                    if (!hero.isKnockedOut) return "not knocked out yet";

                    // ChooseNextSpectationTarget moves the camera to a teammate first. With no
                    // teammate, the nearest other living entity stands in for one, so that
                    // whatever listens to the focus changing hears it too.
                    Entity stand = null;
                    float best = float.MaxValue;
                    foreach (var other in UnityEngine.Object.FindObjectsByType<Entity>(FindObjectsSortMode.None))
                    {
                        if (other == null || other == hero || other.IsNullInactiveDeadOrKnockedOut()) continue;
                        float distance = Vector3.Distance(other.position, hero.position);
                        if (distance < best) { best = distance; stand = other; }
                    }
                    if (stand != null) camera.SetFocusedEntity(stand);

                    AccessTools.PropertySetter(typeof(CameraManager), nameof(CameraManager.isSpectating))
                        .Invoke(camera, new object[] { true });
                    camera.onIsSpectatingChanged?.Invoke(true);
                    return "spectating " + camera.isSpectating + ", camera on " +
                           (camera.focusedEntity != null ? camera.focusedEntity.GetType().Name : "nothing");
                }

                case "off":
                    if (hero.Status.TryGetStatusEffect<Se_HeroKnockedOut>(out var knockout)) knockout.Revive();
                    else hero.Network_isKnockedOut = false;
                    game.isGameOverEnabled = _gameOverWas;
                    return "revived (the camera notices on its next logic update)";

                default:
                    return "down on|spectate|off";
            }
        }

        // The mode the key would set, without the key - so the gates in front of it are not what
        // this tests. What happens *after* it opens, which is where a spectating camera closes
        // it again, is.
        private static string Edit(string[] words)
        {
            var edit = ManagerBase<EditSkillManager>.instance;
            if (edit == null) return "no edit-skill manager";

            if (words.Length > 1 && words[1].Equals("off", StringComparison.OrdinalIgnoreCase)) edit.EndEdit();
            else edit.StartRegularEdit(endAfterAction: false);
            return "mode " + edit.mode;
        }

        private static string Loadout()
        {
            var text = new StringBuilder();
            var hero = DevActions.LocalHero;
            var camera = ManagerBase<CameraManager>.instance;
            var control = ManagerBase<ControlManager>.instance;
            var edit = ManagerBase<EditSkillManager>.instance;

            text.Append("knocked out ").Append(hero != null && hero.isKnockedOut)
                .Append(", spectating ").Append(camera != null && camera.isSpectating)
                .Append(", edit mode ").Append(edit != null ? edit.mode.ToString() : "-");
            if (control != null)
                text.Append("\ninput ").Append(control.shouldProcessCharacterInput)
                    .Append(", input allowing knocked out ").Append(control.shouldProcessCharacterInputAllowKnockedOut);

            // Everything between the buttons and the screen that can hide them.
            var buttons = UnityEngine.Object.FindAnyObjectByType<UI_InGame_SkillButtons>(FindObjectsInactive.Include);
            if (buttons == null) return text.Append("\nno skill buttons").ToString();

            for (var node = buttons.transform; node != null; node = node.parent)
            {
                var line = new StringBuilder();
                if (!node.gameObject.activeSelf) line.Append(" inactive");
                if (node.TryGetComponent<Canvas>(out var canvas)) line.Append(" canvas ").Append(canvas.enabled ? "on" : "OFF");
                if (node.TryGetComponent<CanvasGroup>(out var group)) line.Append(" alpha ").Append(group.alpha.ToString("0.##"));
                if (node.TryGetComponent<UI_InGame_VisibilityOnSpectate>(out var vis)) line.Append(" hideOnSpectate ").Append(vis.hideOnSpectate);
                if (line.Length > 0) text.Append('\n').Append(node.name).Append(':').Append(line);
            }

            // And everything under them that can hide part of the panel: a canvas switched off, a
            // group faded out. Not deactivated objects - over a thousand are, by design.
            int hidden = 0;
            foreach (var node in buttons.GetComponentsInChildren<Transform>(false))
            {
                if (node == buttons.transform) continue;
                string why = null;
                if (node.TryGetComponent<Canvas>(out var inner) && !inner.enabled) why = "canvas OFF";
                else if (node.TryGetComponent<CanvasGroup>(out var innerGroup) && innerGroup.alpha < 0.05f) why = "alpha " + innerGroup.alpha.ToString("0.##");
                if (why == null) continue;
                if (++hidden <= 25) text.Append("\n  under: ").Append(PathFrom(buttons.transform, node)).Append(' ').Append(why);
            }
            if (hidden > 25) text.Append("\n  under: ... ").Append(hidden - 25).Append(" more");

            var bar = UnityEngine.Object.FindAnyObjectByType<UI_InGame_SkillButtonsBottomBar>(FindObjectsInactive.Include);
            if (bar != null && bar.TryGetComponent<CanvasGroup>(out var barGroup))
                text.Append("\nbottom bar ").Append(bar.name).Append(" alpha ").Append(barGroup.alpha.ToString("0.##"));

            // Hidden on spectate anywhere, in case one sits over the bar from a sibling.
            foreach (var vis in UnityEngine.Object.FindObjectsByType<UI_InGame_VisibilityOnSpectate>(FindObjectsSortMode.None))
                if (vis.hideOnSpectate) text.Append("\nhidden on spectate: ").Append(vis.name);

            return text.ToString();
        }

        private static string PathFrom(Transform root, Transform node)
        {
            string path = node.name;
            for (var up = node.parent; up != null && up != root; up = up.parent) path = up.name + "/" + path;
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
