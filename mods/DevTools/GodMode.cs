using Mirror;
using UnityEngine;

namespace DevTools
{
    // Numbers large enough that a room stops being an obstacle, for when the thing being tested
    // is several rooms away and the rooms in between are only in the way.
    //
    // It is a granted StatBonus rather than anything written into the hero: EntityStatus keeps a
    // list of those and folds them into every stat calculation, and it hands the object back so
    // it can be taken away again - which makes switching this off exact rather than approximate.
    // Writing baseStats or finalStats instead would be undone by the next CalculateStats, and
    // would have no way back to the numbers the hero actually had.
    //
    // How the game reads the two kinds of number, from EntityStatus.CalculateStats:
    //
    //   flat        added to the base stat
    //   percentage  divided by a hundred and applied as a multiplier, so 300 means "+300%"
    //
    // Server-only, and not by convention: AddStatBonus refuses off the server and writes a
    // warning to the log, the same as everything else this panel does.
    internal static class GodMode
    {
        // Damage and health are what a test needs to stop caring about; haste and the two speeds
        // are what makes crossing a map fast. None of it grants invulnerability outright - a hero
        // with a million health can still be killed by something that ignores health, which is
        // worth still being able to watch happen.
        private const float Damage = 999999f;
        private const float Health = 999999f;
        private const float Haste = 500f;
        private const float SpeedPercentage = 300f;

        // The bonus, and the hero it was granted to. A room load hands back a different Hero
        // object and the old one takes its bonuses with it, so both are kept: a mismatch between
        // this and the hero being played is what asks for the grant to be made again.
        private static StatBonus _granted;
        private static Hero _grantedTo;

        public static bool IsApplied => _granted != null && _grantedTo != null;

        // Declarative, and called every frame with whatever the panel currently says. That is
        // what makes it survive the several things that would otherwise switch it off silently:
        // a room transition replacing the hero, a run starting after the toggle was set, the mod
        // being reloaded with the setting already true.
        public static void Apply(bool wanted)
        {
            if (!wanted)
            {
                Revoke();
                return;
            }

            if (!NetworkServer.active) return;

            var hero = DevActions.LocalHero;
            if (EntityCheck.IsNullInactiveDeadOrKnockedOut(hero)) return;
            if (hero == _grantedTo) return;

            Revoke();
            Grant(hero);
        }

        public static void Reset() => Revoke();

        private static void Grant(Hero hero)
        {
            var bonus = new StatBonus
            {
                attackDamageFlat = Damage,
                abilityPowerFlat = Damage,
                maxHealthFlat = Health,
                abilityHasteFlat = Haste,
                attackSpeedPercentage = SpeedPercentage,
                movementSpeedPercentage = SpeedPercentage,
            };

            _granted = hero.Status.AddStatBonus(bonus);
            _grantedTo = hero;

            // Raising the ceiling does not fill what is under it, and walking into the next room
            // on the health the hero had before is not what this is expected to mean.
            hero.Status.SetHealth(hero.Status.maxHealth);

            Debug.Log("[DevTools] god mode applied to " + hero.name);
        }

        private static void Revoke()
        {
            // Unity's null is the point of both checks: a hero destroyed by a room load compares
            // equal to null here, and its EntityStatus went with it, so there is nothing left to
            // take the bonus off - only the references to drop.
            if (_granted != null && _grantedTo != null && NetworkServer.active)
            {
                var status = _grantedTo.Status;
                if (status != null) status.RemoveStatBonus(_granted);
            }

            _granted = null;
            _grantedTo = null;
        }
    }
}
