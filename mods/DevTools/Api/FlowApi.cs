#if DEBUG
using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace DevTools
{
    // Getting from one screen to the next without clicking: the title into a solo lobby, the
    // lobby into a run, a run's windows and conversations answered, the map travelled, the result
    // screen left. Each route calls what the matching button calls, found by reading the game.
    internal static class FlowApi
    {
        // How long before the client's own timeout a waiting route gives up and answers with
        // where it got to, so that the agent gets an answer rather than a 504.
        private const float Margin = 1.5f;

        private static float Deadline(Args a) => Time.realtimeSinceStartup + Mathf.Max(1f, a.Float("timeout", 30f) - Margin);

        // ----- title and lobby --------------------------------------------------------------

        [Route("GET", "/lobby", "The heroes that can be picked, difficulties, lucid dreams, and what is selected now. Lobby only.")]
        private static object Lobby(Args a)
        {
            var player = GameAccess.Player;
            var settings = NetworkedManagerBase<GameSettingsManager>.softInstance;
            var group = UnityEngine.Object.FindAnyObjectByType<UI_Lobby_DifficultyGroup>(FindObjectsInactive.Include);
            return new
            {
                inLobby = ManagerBase<PlayLobbyManager>.softInstance != null,
                heroes = Heroes(),
                selectedHero = player != null ? player.selectedHeroType : null,
                difficulty = settings != null ? settings.difficulty : null,
                difficulties = group != null ? group.difficulties : null,
                lucidDreams = settings != null ? settings.availableLucidDreams.ToArray() : null,
                activeLucidDreams = settings != null ? settings.activeLucidDreams.ToArray() : null,
            };
        }

        private static object Heroes() =>
            Dew.allHeroes.Where(t => Dew.IsHeroIncludedInGame(t.Name)).Select(t => new
            {
                type = t.Name,
                name = Describe.Loc(t.Name + "_Name", t.Name),
                available = HeroAvailable(t.Name),
            });

        private static bool HeroAvailable(string type)
        {
            var profile = DewSave.profileMain;
            return profile != null && profile.heroes.TryGetValue(type, out var unlock) && unlock.isAvailableInGame;
        }

        private static string ResolveHero(string wanted)
        {
            var types = Dew.allHeroes.Where(t => Dew.IsHeroIncludedInGame(t.Name)).Select(t => t.Name).ToList();
            var match = types.FirstOrDefault(t => t.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ??
                        types.FirstOrDefault(t => t.Equals("Hero_" + wanted, StringComparison.OrdinalIgnoreCase)) ??
                        types.FirstOrDefault(t => Describe.Loc(t + "_Name", t).Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new DevException("no hero '" + wanted + "' - GET /lobby lists them: " + string.Join(", ", types));
            if (!HeroAvailable(match)) throw new DevException(match + " is locked on this profile");
            return match;
        }

        [Route("POST", "/flow/start_solo", "From the title screen or the lobby, into a solo run: picks the hero, difficulty and lucid dreams, starts, and waits until the hero can move. Pass a long timeout (e.g. 180). Answers early if a message needs answering (then answer it and call again).",
               "hero? (type or name), difficulty? (e.g. diffNormal), lucid_dreams? [names], timeout")]
        private static IEnumerator StartSolo(Args a)
        {
            float deadline = Deadline(a);

            if (ManagerBase<PlayLobbyManager>.softInstance == null && NetworkedManagerBase<GameManager>.softInstance == null)
            {
                // The title's own start routine - profile, language, splash - has to have finished.
                while (!(ManagerBase<TitleManager>.softInstance != null && GameAccess.Ui != null && GameAccess.Ui.IsState("Title")))
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        yield return new Reply(Stage("title", "not on the title menu (UI state " + (GameAccess.Ui != null ? GameAccess.Ui.state : "none") +
                                                     ") - get there with /ui/click, or answer the message"));
                        yield break;
                    }
                    if (StateApi.Message() != null) { yield return new Reply(Stage("title", "a message is waiting")); yield break; }
                    yield return null;
                }
                ManagerBase<TitleManager>.instance.EnterSingleplayerShapeOfDreams();
            }

            if (NetworkedManagerBase<GameManager>.softInstance == null)
            {
                while (!(ManagerBase<PlayLobbyManager>.softInstance != null && DewPlayer.local != null && DewPlayer.local.isEveryInfoSet))
                {
                    if (Time.realtimeSinceStartup > deadline) { yield return new Reply(Stage("entering lobby", "still loading")); yield break; }
                    yield return null;
                }

                if (a.Has("hero"))
                {
                    string hero = ResolveHero(a.Str("hero"));
                    if (DewPlayer.local.selectedHeroType != hero)
                    {
                        DewPlayer.local.CmdSetHeroType(hero);
                        while (DewPlayer.local.selectedHeroType != hero)
                        {
                            if (Time.realtimeSinceStartup > deadline) { yield return new Reply(Stage("lobby", "hero did not take")); yield break; }
                            yield return null;
                        }
                    }
                }

                var settings = NetworkedManagerBase<GameSettingsManager>.instance;
                if (a.Has("difficulty")) settings.difficulty = a.Str("difficulty");
                if (a.Token("lucid_dreams") is Newtonsoft.Json.Linq.JArray dreams)
                {
                    settings.ClearLucidDreams();
                    foreach (var dream in dreams.Select(d => (string)d))
                    {
                        if (!settings.availableLucidDreams.Contains(dream))
                            throw new DevException("no lucid dream '" + dream + "' - GET /lobby lists them");
                        settings.AddLucidDream(dream);
                    }
                }

                ManagerBase<PlayLobbyManager>.instance.StartGame();
                yield return new WaitForSecondsRealtime(0.3f);
                if (StateApi.Message() != null) { yield return new Reply(Stage("starting", "a message is waiting - answer it and the run starts")); yield break; }
            }

            while (!InRun())
            {
                if (Time.realtimeSinceStartup > deadline) { yield return new Reply(Stage("starting", "not playing yet - call /flow/wait_playing")); yield break; }
                if (StateApi.Message() != null) { yield return new Reply(Stage("starting", "a message is waiting")); yield break; }
                yield return null;
            }
            yield return new Reply(Stage("playing", null));
        }

        private static bool InRun()
        {
            var ui = GameAccess.Ui;
            return NetworkedManagerBase<GameManager>.softInstance != null && ui is InGameUIManager && ui.IsState("Playing") &&
                   GameAccess.Hero != null;
        }

        private static object Stage(string stage, string note) => new
        {
            stage,
            note,
            scene = GameAccess.Scene,
            uiState = GameAccess.Ui != null ? GameAccess.Ui.state : null,
            message = StateApi.Message(),
        };

        [Route("POST", "/flow/wait_playing", "Wait until the hero is in a room and can be controlled (after travel, a zone change, a cutscene).", "timeout")]
        private static IEnumerator WaitPlaying(Args a)
        {
            float deadline = Deadline(a);
            while (!InRun() || (NetworkedManagerBase<ZoneManager>.softInstance?.isInAnyTransition ?? false))
            {
                if (Time.realtimeSinceStartup > deadline) { yield return new Reply(Stage("waiting", "not playing yet")); yield break; }
                if (StateApi.Message() != null) { yield return new Reply(Stage("waiting", "a message is waiting")); yield break; }
                yield return null;
            }
            yield return new Reply(Stage("playing", null));
        }

        [Route("POST", "/flow/result_continue", "On the result screen: ready up, which returns to the lobby.")]
        private static object ResultContinue(Args a)
        {
            var player = GameAccess.Player ?? throw new DevException("no local player");
            player.CmdSetIsReady(true);
            return new { ready = true };
        }

        [Route("POST", "/flow/to_title", "End the session and go back to the title screen - from a run (it is conceded, and a continue save kept if the game keeps one) or from the lobby.")]
        private static object ToTitle(Args a)
        {
            var network = DewNetworkManager.softInstance ?? throw new DevException("no session to end");
            network.EndSession();
            return new { ending = true };
        }

        [Route("POST", "/flow/quit", "Quit the game.")]
        private static object Quit(Args a)
        {
            Dew.QuitApplication();
            return new { quitting = true };
        }

        // ----- windows that wait for an answer ----------------------------------------------

        [Route("POST", "/message/answer", "Answer the modal message box. /state shows it and its buttons.", "button (Ok Yes No Cancel Custom0..3)")]
        private static object Answer(Args a)
        {
            var messages = ManagerBase<MessageManager>.softInstance;
            if (messages == null || !messages.isShowingMessage) throw new DevException("no message is showing");
            if (!Enum.TryParse(a.Str("button"), true, out DewMessageSettings.ButtonType button))
                throw new DevException("no button " + a.Str("button") + " - Ok Yes No Cancel Custom0 Custom1 Custom2 Custom3");
            messages.CloseMessage(button);
            return new { answered = button.ToString(), next = StateApi.Message() };
        }

        [Route("POST", "/conversation/advance", "Advance the conversation one line (finishing the typewriter first, as a click does).")]
        private static object Advance(Args a)
        {
            var ui = StateApi.ActiveConversation() ?? throw new DevException("no conversation");
            AccessTools.Method(typeof(UI_InGame_Conversations_Instance), "AdvanceConversation").Invoke(ui, null);
            return new { advanced = true };
        }

        [Route("POST", "/conversation/choose", "Pick a conversation choice by index (as /state lists them).", "index")]
        private static object Choose(Args a)
        {
            var ui = StateApi.ActiveConversation() ?? throw new DevException("no conversation");
            AccessTools.Method(typeof(UI_InGame_Conversations_Instance), "ChoiceClick").Invoke(ui, new object[] { a.Int("index") });
            return new { chose = a.Int("index") };
        }

        // Through the camera, which knows the cutscene that is playing: a room can hold more than one
        // director, and skipping one that is not playing does nothing. This is the skip button's own
        // handler.
        [Route("POST", "/cutscene/skip", "Skip the cutscene that is playing, as its skip button does.")]
        private static object SkipCutscene(Args a)
        {
            var cam = ManagerBase<CameraManager>.instance;
            if (cam == null || !cam.isPlayingCutscene || cam.currentCutsceneDirector == null) throw new DevException("no cutscene playing");
            var director = cam.currentCutsceneDirector;
            if (!director.enableSkip) throw new DevException("this cutscene cannot be skipped");
            cam.SkipCurrentCutscene();
            return new { skipped = true, director = director.name };
        }

        [Route("POST", "/menu", "Open or close the pause menu.", "open=true")]
        private static object Menu(Args a)
        {
            var menu = UnityEngine.Object.FindAnyObjectByType<UI_Common_MenuView>(FindObjectsInactive.Include) ?? throw new DevException("no menu here");
            if (a.Bool("open", true))
            {
                if (!menu.CanShowMenu()) throw new DevException("the menu cannot open right now");
                menu.ShowMenu();
            }
            else menu.HideMenu();
            return new { uiState = GameAccess.Ui != null ? GameAccess.Ui.state : null };
        }

        // ----- shrines and merchants --------------------------------------------------------
        //
        // A player chooses from a shrine or buys from a merchant in the window that interacting
        // with it opens, standing next to it. So do these: they refuse unless that window is open
        // for that object, and they call the window's own buttons - ClickChoice, ClickMerchandise,
        // ClickRefresh - rather than the network commands behind them, which the server would
        // accept from across the room.

        private static UI_InGame_FloatingWindow_Base OpenWindowFor(Actor actor)
        {
            var windows = ManagerBase<FloatingWindowManager>.softInstance;
            if (windows == null || windows.currentTarget == null || windows.currentTarget != actor)
                throw new DevException("its window is not open - /hero/interact with it first (that walks there and opens it)");

            var window = UnityEngine.Object.FindObjectsByType<UI_InGame_FloatingWindow_Base>(FindObjectsSortMode.None)
                .FirstOrDefault(w => w.isActiveAndEnabled && (w.target == actor || w.GetSupportedType().IsInstanceOfType(actor)));
            return window ?? throw new DevException("no window on screen for " + actor.GetType().Name);
        }

        private static object ClickInWindow(Actor actor, string[] methodNames, params object[] args)
        {
            var window = OpenWindowFor(actor);
            foreach (var name in methodNames)
            {
                var method = AccessTools.Method(window.GetType(), name, args.Select(x => x.GetType()).ToArray());
                if (method == null) continue;
                int from = CenterMessages.Count;
                method.Invoke(window, args);
                return new { window = window.GetType().Name, clicked = name, refused = CenterMessages.Since(from) };
            }
            throw new DevException(window.GetType().Name + " has none of " + string.Join(", ", methodNames));
        }

        [Route("POST", "/shrine/choose", "Take one of a shrine's offers, in the window interacting with it opened (/interactables lists the offers). What is chosen usually drops on the ground.",
               "id, index")]
        private static object ShrineChoose(Args a) =>
            ClickInWindow(GameAccess.RequireActor(a.Id("id")), new[] { "ClickChoice", "ClickChaosItem" }, a.Int("index"));

        [Route("POST", "/merchant/buy", "Buy from a merchant, in its shop window (interact first). Stock and prices are in /interactables. The window's own checks apply: money, stardust, souvenirs already owned.",
               "id, index")]
        private static object Buy(Args a) =>
            ClickInWindow(GameAccess.RequireActor(a.Id("id")), new[] { "ClickMerchandise" }, a.Int("index"));

        [Route("POST", "/merchant/refresh", "Reroll a merchant's stock from its shop window, at what the game charges.", "id")]
        private static object Refresh(Args a) =>
            ClickInWindow(GameAccess.RequireActor(a.Id("id")), new[] { "ClickRefresh" });

        // ----- the map ----------------------------------------------------------------------
        //
        // Travel is a click on the world map, and the world map is open only while the hero is at
        // an open exit rift and has used it - which is what /hero/interact with the exit does.
        // The click is the map's own TravelToNode, so its hunter warning, its "you left items
        // behind" question and its other checks all come up as they would for a player.

        [Route("POST", "/map/travel", "Click a node on the world map. The map has to be open: walk to the open exit rift and use it (/hero/interact with /state room.exitId). Then /flow/wait_playing. A question may come up first (/state message).",
               "node")]
        private static object Travel(Args a)
        {
            var zone = NetworkedManagerBase<ZoneManager>.softInstance ?? throw new DevException("not in a run");
            var ui = InGameUIManager.softInstance as InGameUIManager ?? throw new DevException("not in a run");
            if (ui.isWorldDisplayed != WorldDisplayStatus.Shown)
            {
                var exit = Rift_RoomExit.instance;
                throw new DevException(exit == null || !exit.isOpen
                    ? "the exit is not open - clear the room first"
                    : "the world map is not open - /hero/interact with the exit rift (id " + exit.netId + ") first");
            }

            int node = a.Int("node");
            if (node < 0 || node >= zone.nodes.Count) throw new DevException("no node " + node);
            if (!zone.IsNodeConnected(zone.currentNodeIndex, node)) throw new DevException("node " + node + " is not next to node " + zone.currentNodeIndex);

            var map = UnityEngine.Object.FindAnyObjectByType<UI_InGame_WorldMap>(FindObjectsInactive.Include) ?? throw new DevException("no world map on screen");
            int from = CenterMessages.Count;
            map.TravelToNode(node);
            return new
            {
                clicked = node,
                type = zone.nodes[node].type.ToString(),
                refused = CenterMessages.Since(from),
                message = StateApi.Message(),
            };
        }

        // ----- time -------------------------------------------------------------------------

        [Route("POST", "/cheat/time_scale", "Game speed - not something a player can change. 1 is normal; the game's own slow-motion effects multiply on top. Real-time waits (loading, lobby countdown) are unaffected.",
               "scale")]
        private static object TimeScale(Args a)
        {
            var timescale = NetworkedManagerBase<TimescaleManager>.softInstance ?? throw new DevException("no timescale manager here");
            timescale.desiredTimescale = Mathf.Clamp(a.Float("scale"), 0f, 20f);
            return new { desired = timescale.desiredTimescale };
        }

        [Route("POST", "/wait", "Let time pass: seconds of real time, or frames.", "seconds? | frames?")]
        private static IEnumerator Wait(Args a)
        {
            if (a.Has("frames"))
            {
                int frames = Mathf.Clamp(a.Int("frames"), 1, 100000);
                for (int i = 0; i < frames; i++) yield return null;
            }
            else
            {
                yield return new WaitForSecondsRealtime(Mathf.Clamp(a.Float("seconds", 1f), 0f, a.Float("timeout", 30f) - Margin));
            }
            yield return new Reply(new { frame = Time.frameCount });
        }
    }
}
#endif
