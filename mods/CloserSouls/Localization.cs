using System.Collections.Generic;

namespace CloserSouls
{
    // The game carries a full localization table, but it is keyed by its own content and offers no
    // way for a mod to register entries - DewLocalization exposes only lookups. What it does
    // expose is the selected language, on DewSave.profileMain.language, so a mod can keep its own
    // strings and pick by that.
    //
    // Language codes are the ones the game ships data for, one folder each under RawData. Anything
    // unrecognised, including a language added by a later patch, falls back to English.
    //
    // Three strings, all of them settings rows, applied in CloserSoulsConfig.BuildWidgets -
    // ModConfig.LabelText takes a compile-time constant and so can only ever be one language.
    //
    // All three are counts of rooms, and every language says that plainly. The word the game
    // itself uses for the thing being placed is the *soul*; these rows are about distance, so
    // they name the rooms and leave the soul to the setting's own title.
    internal static class Localization
    {
        public const string First = "settings.first";
        public const string Extra = "settings.extra";
        public const string Max = "settings.max";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["en-US"] = new Dictionary<string, string>
                {
                    [First] = "Rooms away on the first death",
                    [Extra] = "Extra rooms for each death after",
                    [Max] = "Never further away than",
                },
                ["ru-RU"] = new Dictionary<string, string>
                {
                    [First] = "Комнат до души при первой смерти",
                    [Extra] = "Прибавка комнат за каждую следующую",
                    [Max] = "Но не дальше, чем",
                },
                ["de-DE"] = new Dictionary<string, string>
                {
                    [First] = "Räume entfernt beim ersten Tod",
                    [Extra] = "Zusätzliche Räume je weiterem Tod",
                    [Max] = "Nie weiter entfernt als",
                },
                ["es-MX"] = new Dictionary<string, string>
                {
                    [First] = "Salas de distancia en la primera muerte",
                    [Extra] = "Salas extra por cada muerte siguiente",
                    [Max] = "Nunca más lejos de",
                },
                ["fr-FR"] = new Dictionary<string, string>
                {
                    [First] = "Salles d'écart à la première mort",
                    [Extra] = "Salles en plus à chaque mort suivante",
                    [Max] = "Jamais plus loin que",
                },
                ["it-IT"] = new Dictionary<string, string>
                {
                    [First] = "Stanze di distanza alla prima morte",
                    [Extra] = "Stanze in più per ogni morte successiva",
                    [Max] = "Mai più lontano di",
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                    [First] = "最初の死亡時に離れる部屋数",
                    [Extra] = "以降の死亡ごとに増える部屋数",
                    [Max] = "これ以上は離さない",
                },
                ["ko-KR"] = new Dictionary<string, string>
                {
                    [First] = "첫 사망 시 떨어진 방 수",
                    [Extra] = "이후 사망마다 늘어나는 방 수",
                    [Max] = "이보다 멀어지지 않음",
                },
                ["pl-PL"] = new Dictionary<string, string>
                {
                    [First] = "Pokoi dalej przy pierwszej śmierci",
                    [Extra] = "Dodatkowe pokoje za każdą kolejną",
                    [Max] = "Nigdy dalej niż",
                },
                ["pt-BR"] = new Dictionary<string, string>
                {
                    [First] = "Salas de distância na primeira morte",
                    [Extra] = "Salas extras a cada morte seguinte",
                    [Max] = "Nunca mais longe que",
                },
                ["tr-TR"] = new Dictionary<string, string>
                {
                    [First] = "İlk ölümde kaç oda uzakta",
                    [Extra] = "Sonraki her ölümde eklenen oda",
                    [Max] = "Asla şundan uzakta değil",
                },
                ["zh-CN"] = new Dictionary<string, string>
                {
                    [First] = "首次倒下时相隔的房间数",
                    [Extra] = "此后每次倒下增加的房间数",
                    [Max] = "最远不超过",
                },
                ["zh-TW"] = new Dictionary<string, string>
                {
                    [First] = "首次倒下時相隔的房間數",
                    [Extra] = "此後每次倒下增加的房間數",
                    [Max] = "最遠不超過",
                },
            };

        private static readonly Shared.LanguageTable Table = new Shared.LanguageTable(Strings);

        public static string Get(string key) => Table.Get(key);
    }
}
