#if DEBUG
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace DevTools
{
    // The line of text the game puts in the middle of the screen when it refuses something - "not
    // enough dream dust", "this slot is locked", "the exit is not open". Every one goes through
    // InGameUIManager.ShowCenterMessageRaw, so they are recorded there, numbered, for routes that
    // call the game's own UI handlers to report why nothing happened.
    internal static class CenterMessages
    {
        private const int Keep = 50;
        private static readonly List<string> Recent = new List<string>();
        private static int _dropped;

        public static int Count => _dropped + Recent.Count;

        public static string[] Since(int count) =>
            Recent.Skip(System.Math.Max(0, count - _dropped)).ToArray();

        public static void Add(string text)
        {
            Recent.Add(GameAccess.Rich(text));
            while (Recent.Count > Keep) { Recent.RemoveAt(0); _dropped++; }
        }
    }

    [HarmonyPatch(typeof(InGameUIManager), nameof(InGameUIManager.ShowCenterMessageRaw))]
    internal static class CenterMessagePatch
    {
        private static void Prefix(string raw) => CenterMessages.Add(raw);
    }
}
#endif
