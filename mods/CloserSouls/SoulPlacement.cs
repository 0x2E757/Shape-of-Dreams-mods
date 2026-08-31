using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace CloserSouls
{
    // One method decides where a knocked-out player's soul goes, and this replaces the two lines
    // of it that pick the node. Everything else about being down is untouched: the same modifier,
    // the same shrine, the same quest, the same revive.
    //
    // The game's own version, in full:
    //
    //     if (!disableQuest && zone.currentNode.type != WorldNodeType.ExitBoss
    //         && !zone.nodes.Any(n => n.modifiers.Any(m => m.type == "RoomMod_HeroSoul"
    //                                                   && m.clientData == victim.owner.guid))
    //         && Dew.GetAliveHeroCount() != 0)
    //     {
    //         zone.TryGetNodeIndexForNextGoal(new GetNodeIndexSettings {
    //             desiredDistance = GameManager.instance.difficulty.lostSoulDistance,
    //             avoidMainModifier = false, preferCloserToExit = true }, out var nodeIndex);
    //         zone.AddModifier<RoomMod_HeroSoul>(nodeIndex, victim.owner.guid);
    //     }
    //
    // The four guards are restated below rather than reimplemented around: when any of them fails,
    // the prefix hands the call straight back to the game, which then makes the same decision it
    // would have made alone. The mod only ever takes over the sentence that chooses a node.
    //
    // It runs twice per knockout at least - once from OnCreate, and again from
    // ClientEventOnRoomLoaded on every room load after it - and the third guard is what makes the
    // repeats free: a soul already on the map means there is nothing to place. So the count below
    // advances once per soul actually placed, which is once per knockout, and not once per call.
    [HarmonyPatch(typeof(Se_HeroKnockedOut), nameof(Se_HeroKnockedOut.CheckAndAddHeroSoul))]
    internal static class SoulPlacement
    {
        // Deaths already answered for, per player, in the region this is counting. Not saved and
        // not synced: it is the host's own tally of what it has done this region, and a host that
        // reloads a save starts the region over at the first death, which is the forgiving way to
        // be wrong about it.
        private static readonly Dictionary<string, int> Placed = new Dictionary<string, int>();

        // What the tally is *of*. The zone manager is compared by reference rather than asked for
        // an identity, because a new run brings a new one; the zone index moves within a run. A
        // change in either means a different map, and a different map means the walk starts again.
        private static ZoneManager _countedZone;
        private static int _countedZoneIndex = int.MinValue;

        public static void Forget()
        {
            Placed.Clear();
            _countedZone = null;
            _countedZoneIndex = int.MinValue;
        }

        private static bool Prefix(Se_HeroKnockedOut __instance)
        {
            var config = CloserSoulsMod.Live;
            if (config == null || !__instance.isServer) return true;

            var zone = NetworkedManagerBase<ZoneManager>.instance;
            if (zone == null || zone.nodes == null) return true;
            if (zone.currentNodeIndex < 0 || zone.currentNodeIndex >= zone.nodes.Count) return true;

            var victim = __instance.victim;
            if (victim == null || victim.owner == null) return true;
            string guid = victim.owner.guid;

            // The game's four guards, in its order. Handing the call back rather than returning
            // silently, so that if any of them ever grows a fifth clause the game still applies it.
            if (__instance.disableQuest) return true;
            if (zone.currentNode.type == WorldNodeType.ExitBoss) return true;
            if (zone.nodes.Any(n => n.modifiers.Any(m => m.type == "RoomMod_HeroSoul" && m.clientData == guid)))
                return true;
            if (Dew.GetAliveHeroCount() == 0) return true;

            int rooms = Advance(zone, guid, config);

            // Nought rooms away means the room the party is standing in, and
            // TryGetNodeIndexForNextGoal cannot answer that: its scoring takes 10000 off the
            // current node and another 10000 off any node already visited, so the node it returns
            // is by construction somewhere else. The current node has to be named directly.
            //
            // That is a supported thing to do rather than something smuggled past: AddModifier has
            // a branch for exactly this case - the node is current and the room is live - that
            // routes through RoomModifiers.HandleRuntimeAddition and spawns the modifier into the
            // room as it stands. Shrine_Chaos and Shrine_CorruptedChaos already put a
            // Shrine_HeroSoul into a live room by hand, so the shrine is not surprised to arrive
            // mid-fight either.
            if (rooms <= 0 && IsCurrentRoomLive())
            {
                zone.AddModifier<RoomMod_HeroSoul>(zone.currentNodeIndex, guid);
                return false;
            }

            // Either the player asked for a walk, or the room is between loads and the branch
            // above would have written a modifier that nothing is listening for. One room out is
            // the shortest honest answer to both.
            var settings = new GetNodeIndexSettings
            {
                desiredDistance = new Vector2Int(Mathf.Max(rooms, 1), Mathf.Max(rooms, 1)),
                avoidMainModifier = false,
                preferCloserToExit = true,
            };

            // The return value says whether the winning node scored above -5000, which is to say
            // whether any node was a real answer rather than the least bad of a bad set. The game
            // ignores it and places the soul regardless; so does this, for the same reason - a
            // soul somewhere imperfect is a run that continues.
            zone.TryGetNodeIndexForNextGoal(settings, out int nodeIndex);

            // The index itself is not something to take on trust, though. AddModifier indexes
            // nodes with it and the caller is inside a server-side knockout handler, so an index
            // out of range would not be a misplaced soul - it would be the knockout half-applied.
            if (nodeIndex < 0 || nodeIndex >= zone.nodes.Count) return true;

            zone.AddModifier<RoomMod_HeroSoul>(nodeIndex, guid);
            return false;
        }

        // How far out this player's next soul goes, and the count moves on by one. Only called
        // once the decision to place has been made, so the tally is of souls and not of calls.
        private static int Advance(ZoneManager zone, string guid, CloserSoulsConfig config)
        {
            if (!ReferenceEquals(_countedZone, zone) || _countedZoneIndex != zone.currentZoneIndex)
            {
                Placed.Clear();
                _countedZone = zone;
                _countedZoneIndex = zone.currentZoneIndex;
            }

            Placed.TryGetValue(guid, out int before);
            Placed[guid] = before + 1;

            int rooms = config.roomsAwayOnFirstDeath + config.extraRoomsPerDeath * before;
            return Mathf.Clamp(rooms, 0, config.maxRoomsAway);
        }

        // Whether the room the party is in is loaded and running. The current-node branch of
        // AddModifier asks the same three questions, and if the answer is no it stores the
        // modifier for the next time that node is entered - which for the node you are already
        // standing in means it would not appear until you came back to it.
        private static bool IsCurrentRoomLive()
        {
            var room = Room.softInstance;
            return room != null && room.modifiers != null && room.modifiers.isRoomActive;
        }
    }
}
