using System.Collections.Generic;

namespace MapAutoRoute
{
    // The three lines the mod can write into the map's node tooltip, in every language the game
    // ships data for - one folder each under RawData. Anything unrecognised falls back to English.
    //
    // The lookup itself is Shared.LanguageTable; only the strings are the mod's own. See
    // game-ui.md for why a mod cannot register entries with DewLocalization and has to keep its
    // own table.
    //
    // **The word for a hunter is the game's, not this file's.** It was read out of the game's own
    // content data rather than translated here: RawData/<lang>/achievements.json holds
    // ACH_WHOS_THE_PREY_NOW, whose description names them, and the answers are not what a
    // dictionary would have given - French says Traqueur rather than Chasseur, Portuguese
    // Perseguidor, Korean 추적자, Polish Tropiciel. A mod that invents its own word for something
    // the game already names reads as a mod that was translated by someone who had not played it.
    //
    // "Turn" is the mod's own word: the game has no player-facing term for a move between
    // locations, so each language gets the natural phrasing rather than a borrowed one.
    //
    // There is no singular form and there never needs to be. The line only appears where the game
    // has already written "too far to travel", which it does when the distance is more than one -
    // so a route is at least two rooms, always. Languages whose plurals fork at two and five
    // (Russian, Polish) are phrased so the number lands after a fixed genitive plural, which
    // agrees with anything.
    internal static class Localization
    {
        public const string Travel = "travel";
        public const string TravelHunted = "travel.hunted";
        public const string Prevented = "travel.prevented";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["en-US"] = new Dictionary<string, string>
                {
                    [Travel] = "(Travel in {0} turns)",
                    [TravelHunted] = "(Travel in {0} turns, may be caught by hunters)",
                    [Prevented] = "(Auto route prevented by Hunters)",
                },
                ["ru-RU"] = new Dictionary<string, string>
                {
                    [Travel] = "(Ходов в пути: {0})",
                    [TravelHunted] = "(Ходов в пути: {0}, могут перехватить охотники)",
                    [Prevented] = "(Автомаршрут перекрыт охотниками)",
                },
                ["de-DE"] = new Dictionary<string, string>
                {
                    [Travel] = "(Reise über {0} Züge)",
                    [TravelHunted] = "(Reise über {0} Züge, Jäger könnten dich abfangen)",
                    [Prevented] = "(Automatische Route von Jägern blockiert)",
                },
                ["es-MX"] = new Dictionary<string, string>
                {
                    [Travel] = "(Viaje de {0} turnos)",
                    [TravelHunted] = "(Viaje de {0} turnos, los cazadores podrían atraparte)",
                    [Prevented] = "(Ruta automática bloqueada por cazadores)",
                },
                ["fr-FR"] = new Dictionary<string, string>
                {
                    [Travel] = "(Voyage en {0} tours)",
                    [TravelHunted] = "(Voyage en {0} tours, les traqueurs peuvent vous rattraper)",
                    [Prevented] = "(Itinéraire automatique bloqué par les traqueurs)",
                },
                ["it-IT"] = new Dictionary<string, string>
                {
                    [Travel] = "(Viaggio in {0} turni)",
                    [TravelHunted] = "(Viaggio in {0} turni, i cacciatori potrebbero raggiungerti)",
                    [Prevented] = "(Percorso automatico bloccato dai cacciatori)",
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                    [Travel] = "（移動に{0}ターン）",
                    [TravelHunted] = "（移動に{0}ターン、ハンターに捕まる恐れあり）",
                    [Prevented] = "（自動ルートはハンターに阻まれています）",
                },
                ["ko-KR"] = new Dictionary<string, string>
                {
                    [Travel] = "({0}턴 이동)",
                    [TravelHunted] = "({0}턴 이동, 추적자에게 붙잡힐 수 있음)",
                    [Prevented] = "(추적자 때문에 자동 경로 사용 불가)",
                },
                ["pl-PL"] = new Dictionary<string, string>
                {
                    [Travel] = "(Tur w drodze: {0})",
                    [TravelHunted] = "(Tur w drodze: {0}, tropiciele mogą cię dopaść)",
                    [Prevented] = "(Trasa automatyczna zablokowana przez tropicieli)",
                },
                ["pt-BR"] = new Dictionary<string, string>
                {
                    [Travel] = "(Viagem de {0} turnos)",
                    [TravelHunted] = "(Viagem de {0} turnos, os perseguidores podem te alcançar)",
                    [Prevented] = "(Rota automática bloqueada pelos perseguidores)",
                },
                ["tr-TR"] = new Dictionary<string, string>
                {
                    [Travel] = "({0} turda yolculuk)",
                    [TravelHunted] = "({0} turda yolculuk, avcılar sizi yakalayabilir)",
                    [Prevented] = "(Otomatik rota avcılar tarafından engellendi)",
                },
                ["zh-CN"] = new Dictionary<string, string>
                {
                    [Travel] = "（移动需 {0} 回合）",
                    [TravelHunted] = "（移动需 {0} 回合，可能被追踪者抓住）",
                    [Prevented] = "（自动路线被追踪者阻断）",
                },
                ["zh-TW"] = new Dictionary<string, string>
                {
                    [Travel] = "（移動需 {0} 回合）",
                    [TravelHunted] = "（移動需 {0} 回合，可能被追蹤者抓住）",
                    [Prevented] = "（自動路線被追蹤者阻斷）",
                },
            };

        private static readonly Shared.LanguageTable Table = new Shared.LanguageTable(Strings);

        public static string Get(string key) => Table.Get(key);
    }
}
