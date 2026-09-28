using System;
using System.Globalization;
using Mirror;
using UnityEngine;

namespace ControlledMerge
{
    // The settings every copy of the mod in a party goes by: the host's.
    //
    // The numbers an essence produces are the host's (the server computes them), so a guest's
    // tooltip has to say what the host's settings make of them - the four cuts, whether merging by
    // choosing a slot is on, and whether AreMyGemsCompatible decides which copies count. A guest's
    // own settings would draw numbers that are not the ones in play.
    //
    // They travel in GameSettingsManager.customData, a SyncDictionary<string, string> the game
    // already syncs to every client for game modes' own settings ("GameMod_Limbo::depth"). A client
    // without the mod stores the entry and never reads it; nothing new is sent, so nobody is
    // disconnected - an unknown Mirror message is a disconnect in both directions.
    //
    // **The entry carries a time.** The game keeps customData in the host's preferred settings
    // (PlayLobbyManager) and puts it back in the next lobby, so an entry can outlive the mod: a host
    // who removed it would go on announcing settings nothing enforces. So the host writes
    // NetworkTime.time into it every few seconds - a clock Mirror keeps the same on every machine -
    // and a guest takes the entry only while that time is recent.
    internal static class HostSettings
    {
        public const string Key = "ControlledMerge::settings";

        // How often the host writes the entry again, and how old a guest lets it get.
        private const double RewriteEvery = 5.0;
        private const double StaleAfter = 20.0;

        public struct Values
        {
            public int TwoMemoriesCut;
            public int ThreeMemoriesCut;
            public int TwoCopiesCut;
            public int ThreeCopiesCut;
            public bool MergeByChoosingItsSlot;

            // Whether copies that can never fire are left out of the count - the host having
            // AreMyGemsCompatible loaded. A guest without it cannot tell which those are, and
            // counts every copy.
            public bool LeavesOutDeadCopies;
        }

        private static string _written;
        private static double _writtenAt = double.NegativeInfinity;

        // The settings in force, or false when there are none: this machine is a guest and the
        // host has no copy of the mod running. Such a host lets nobody wear two of a kind, so
        // nothing a guest's tooltip shows is cut.
        public static bool TryGet(out Values values)
        {
            values = default(Values);
            var config = ControlledMergeMod.Live;
            if (config == null) return false;

            if (NetworkServer.active || !NetworkClient.active)
            {
                values = FromConfig(config);
                return true;
            }

            var manager = NetworkedManagerBase<GameSettingsManager>.instance;
            if (manager == null || !manager.customData.TryGetValue(Key, out string text)) return false;
            return TryParse(text, out values);
        }

        // Host only: write the entry when the settings change, and again every few seconds.
        public static void Publish()
        {
            var config = ControlledMergeMod.Live;
            if (config == null || !NetworkServer.active) return;
            var manager = NetworkedManagerBase<GameSettingsManager>.instance;
            if (manager == null) return;

            double now = NetworkTime.time;
            string settings = Format(FromConfig(config));
            if (settings == _written && now - _writtenAt < RewriteEvery) return;

            manager.customData[Key] = settings + "|" + now.ToString("0.#", CultureInfo.InvariantCulture);
            _written = settings;
            _writtenAt = now;
        }

        // On unload, so a reload in the middle of a run does not leave a guest reading stale values
        // for the seconds before the next copy writes them again.
        public static void Withdraw()
        {
            _written = null;
            _writtenAt = double.NegativeInfinity;
            if (!NetworkServer.active) return;
            var manager = NetworkedManagerBase<GameSettingsManager>.instance;
            if (manager != null && manager.customData.ContainsKey(Key)) manager.customData.Remove(Key);
        }

#if DEBUG
        // For DevTools: what a guest would make of an entry.
        public static string Check(string text)
        {
            if (!TryParse(text, out var v)) return "rejected";
            return v.TwoMemoriesCut + "/" + v.ThreeMemoriesCut + "/" + v.TwoCopiesCut + "/" + v.ThreeCopiesCut +
                   " merge=" + v.MergeByChoosingItsSlot + " leavesOutDead=" + v.LeavesOutDeadCopies;
        }
#endif

        private static Values FromConfig(ControlledMergeConfig config)
        {
            return new Values
            {
                TwoMemoriesCut = config.twoMemoriesCut,
                ThreeMemoriesCut = config.threeMemoriesCut,
                TwoCopiesCut = config.twoCopiesCut,
                ThreeCopiesCut = config.threeCopiesCut,
                MergeByChoosingItsSlot = config.mergeByChoosingItsSlot,
                LeavesOutDeadCopies = Fit.Available,
            };
        }

        // "1|30|40|25|35|1|1", then "|<NetworkTime.time>". The leading 1 is the format.
        private static string Format(Values v)
        {
            return string.Join("|", "1",
                v.TwoMemoriesCut.ToString(CultureInfo.InvariantCulture),
                v.ThreeMemoriesCut.ToString(CultureInfo.InvariantCulture),
                v.TwoCopiesCut.ToString(CultureInfo.InvariantCulture),
                v.ThreeCopiesCut.ToString(CultureInfo.InvariantCulture),
                v.MergeByChoosingItsSlot ? "1" : "0",
                v.LeavesOutDeadCopies ? "1" : "0");
        }

        private static bool TryParse(string text, out Values values)
        {
            values = default(Values);
            if (string.IsNullOrEmpty(text)) return false;

            var parts = text.Split('|');
            if (parts.Length < 8 || parts[0] != "1") return false;

            try
            {
                double written = double.Parse(parts[7], CultureInfo.InvariantCulture);
                if (Math.Abs(NetworkTime.time - written) > StaleAfter) return false;

                values = new Values
                {
                    TwoMemoriesCut = Mathf.Clamp(int.Parse(parts[1], CultureInfo.InvariantCulture), 0, 100),
                    ThreeMemoriesCut = Mathf.Clamp(int.Parse(parts[2], CultureInfo.InvariantCulture), 0, 100),
                    TwoCopiesCut = Mathf.Clamp(int.Parse(parts[3], CultureInfo.InvariantCulture), 0, 100),
                    ThreeCopiesCut = Mathf.Clamp(int.Parse(parts[4], CultureInfo.InvariantCulture), 0, 100),
                    MergeByChoosingItsSlot = parts[5] == "1",
                    LeavesOutDeadCopies = parts[6] == "1",
                };
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }
}
