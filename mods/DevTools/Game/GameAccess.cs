using System;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DevTools
{
    // Thrown by anything that refuses a request, with the text the caller is shown. The server
    // turns it into a 400 with that text; the picker puts it on its status line.
    internal sealed class DevException : Exception
    {
        public readonly int Status;

        public DevException(string message, int status = 400) : base(message)
        {
            Status = status;
        }
    }

    // The handful of lookups everything else starts from.
    internal static class GameAccess
    {
        public static DewPlayer Player => DewPlayer.local;

        public static Hero Hero
        {
            get
            {
                var player = DewPlayer.local;
                return player != null ? player.hero : null;
            }
        }

        public static string Scene => SceneManager.GetActiveScene().name;

        // Whichever UIManager is live: the title's, the lobby's or the run's. Each scene has one.
        public static UIManager Ui => UIManager.softInstance;

        public static bool IsServer => NetworkServer.active;

        // Everything that spawns, levels or equips goes through the server: EntityStatus.level
        // throws off it, and spawning touches network objects. Solo is always the server.
        public static void RequireServer()
        {
            if (!NetworkServer.active) throw new DevException("host only - the server owns spawning, levels and loadouts");
        }

        public static Hero RequireHero()
        {
            var hero = Hero;
            if (hero == null) throw new DevException("no hero - not in a run");
            return hero;
        }

        public static Hero RequireLiveHero()
        {
            var hero = RequireHero();
            if (EntityCheck.IsNullInactiveDeadOrKnockedOut(hero)) throw new DevException("the hero is down");
            return hero;
        }

        public static T RequireManager<T>(T manager, string what) where T : UnityEngine.Object
        {
            if (manager == null) throw new DevException("no " + what + " here (scene " + Scene + ")");
            return manager;
        }

        // Actors are addressed by Mirror's netId: stable for the actor's life, the same on every
        // peer, and already on every networked object. A template that was never spawned - a
        // merchant's stock or a shrine's offer asked for by type - has no NetworkIdentity yet, and
        // Mirror's netId throws for it rather than answer 0.
        public static uint IdOf(Actor actor) => actor != null && actor.netIdentity != null ? actor.netId : 0u;

        public static Actor FindActor(uint netId)
        {
            if (netId == 0) return null;
            NetworkIdentity identity;
            if (NetworkServer.active) NetworkServer.spawned.TryGetValue(netId, out identity);
            else NetworkClient.spawned.TryGetValue(netId, out identity);
            return identity != null ? identity.GetComponent<Actor>() : null;
        }

        public static Actor RequireActor(uint netId)
        {
            var actor = FindActor(netId);
            if (actor == null) throw new DevException("no actor with id " + netId);
            return actor;
        }

        public static bool TryParseSlot(string text, out HeroSkillLocation slot)
        {
            slot = default;
            if (string.IsNullOrEmpty(text)) return false;

            switch (text.Trim().ToLowerInvariant())
            {
                case "id": case "identity": case "trait": slot = HeroSkillLocation.Identity; return true;
                case "mv": case "move": case "movement": case "dash": slot = HeroSkillLocation.Movement; return true;
            }
            return Enum.TryParse(text, true, out slot) && Enum.IsDefined(typeof(HeroSkillLocation), slot);
        }

        public static HeroSkillLocation ParseSlot(string text)
        {
            if (!TryParseSlot(text, out var slot)) throw new DevException("no slot named '" + text + "' - Q W E R Identity Movement");
            return slot;
        }

        // A point on the ground under (x, z): the hero's height when there is a hero, since the
        // game's own cursor maths intersects a plane at the focused entity's height too.
        public static Vector3 Ground(float x, float z, float? y = null)
        {
            float height = y ?? (Hero != null ? Hero.agentPosition.y : 0f);
            return new Vector3(x, height, z);
        }

        public static Camera Camera
        {
            get
            {
                var camera = Dew.mainCamera;
                return camera != null ? camera : Camera.main;
            }
        }

        // Screen position in pixels, origin bottom-left as Unity's Input has it, or null when the
        // point is behind the camera.
        public static Vector2? ToScreen(Vector3 world)
        {
            var camera = Camera;
            if (camera == null) return null;
            var p = camera.WorldToScreenPoint(world);
            if (p.z < 0f) return null;
            return new Vector2(p.x, p.y);
        }

        public static Vector3? ScreenToGround(Vector2 screen)
        {
            if (Camera == null) return null;
            try { return ControlManager.GetWorldPositionOnGroundFromScreenPoint(screen, false); }
            catch (Exception) { return null; }
        }

        public static string Rich(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            return System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "").Trim();
        }
    }
}
