using System.Collections.Generic;

namespace FaceTheCursor
{
    // The game carries a full localization table, but it is keyed by its own content and offers no
    // way for a mod to register entries - DewLocalization exposes only lookups. What it does
    // expose is the selected language, on DewSave.profileMain.language, so a mod can keep its own
    // strings and pick by that.
    //
    // Language codes are the ones the game ships data for, one folder each under RawData. Anything
    // unrecognised, including a language added by a later patch, falls back to English.
    //
    // Three strings, all of them settings rows, applied in FaceTheCursorConfig.BuildWidgets -
    // ModConfig.LabelText takes a compile-time constant and so can only ever be one language.
    //
    // **Nothing here is borrowed from the game, because there was nothing to borrow.**
    // DewLocalization.TryGetUIValue would hand over the game's own wording for a key that exists -
    // Shared.ConfigFieldWidgets takes Generic_On and Generic_Off that way, so its toggles read in
    // the player's language without this file's help. But the game has no setting about which way
    // the hero points and so no phrase for it in any language, and there is no way to search the
    // table for one: TryGetUIValue answers a key, and the keys are the game's own. So these three
    // rows are worded here, and worded as short sentences rather than as names, because "Moving"
    // on its own is a row that could mean anything.
    //
    // The pointer is a mouse cursor in every language that has a settled word for one, and the
    // third row avoids the word entirely - it is about a distance, and every language can say
    // "closer than this" without naming what is closer.
    internal static class Localization
    {
        public const string Standing = "settings.standing";
        public const string Moving = "settings.moving";
        public const string MinDistance = "settings.minDistance";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["en-US"] = new Dictionary<string, string>
                {
                    [Standing] = "Turn towards the cursor while standing still",
                    [Moving] = "Turn towards the cursor while moving",
                    [MinDistance] = "Ignore the cursor when closer than",
                },
                ["ru-RU"] = new Dictionary<string, string>
                {
                    [Standing] = "Поворачиваться к курсору, стоя на месте",
                    [Moving] = "Поворачиваться к курсору в движении",
                    [MinDistance] = "Игнорировать курсор ближе, чем",
                },
                ["de-DE"] = new Dictionary<string, string>
                {
                    [Standing] = "Im Stand zum Cursor drehen",
                    [Moving] = "In Bewegung zum Cursor drehen",
                    [MinDistance] = "Cursor ignorieren, wenn näher als",
                },
                ["es-MX"] = new Dictionary<string, string>
                {
                    [Standing] = "Girar hacia el cursor al estar quieto",
                    [Moving] = "Girar hacia el cursor en movimiento",
                    [MinDistance] = "Ignorar el cursor a menos de",
                },
                ["fr-FR"] = new Dictionary<string, string>
                {
                    [Standing] = "Pivoter vers le curseur à l'arrêt",
                    [Moving] = "Pivoter vers le curseur en mouvement",
                    [MinDistance] = "Ignorer le curseur en deçà de",
                },
                ["it-IT"] = new Dictionary<string, string>
                {
                    [Standing] = "Ruota verso il cursore da fermo",
                    [Moving] = "Ruota verso il cursore in movimento",
                    [MinDistance] = "Ignora il cursore sotto",
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                    [Standing] = "静止中もカーソルの方を向く",
                    [Moving] = "移動中もカーソルの方を向く",
                    [MinDistance] = "この距離より近いカーソルは無視",
                },
                ["ko-KR"] = new Dictionary<string, string>
                {
                    [Standing] = "정지 중에도 커서 방향을 봄",
                    [Moving] = "이동 중에도 커서 방향을 봄",
                    [MinDistance] = "이 거리보다 가까운 커서는 무시",
                },
                ["pl-PL"] = new Dictionary<string, string>
                {
                    [Standing] = "Obracaj się do kursora w miejscu",
                    [Moving] = "Obracaj się do kursora w ruchu",
                    [MinDistance] = "Ignoruj kursor bliżej niż",
                },
                ["pt-BR"] = new Dictionary<string, string>
                {
                    [Standing] = "Virar para o cursor quando parado",
                    [Moving] = "Virar para o cursor em movimento",
                    [MinDistance] = "Ignorar o cursor a menos de",
                },
                ["tr-TR"] = new Dictionary<string, string>
                {
                    [Standing] = "Dururken imlece dön",
                    [Moving] = "Hareket ederken imlece dön",
                    [MinDistance] = "Şundan yakın imleci yoksay",
                },
                ["zh-CN"] = new Dictionary<string, string>
                {
                    [Standing] = "静止时转向光标",
                    [Moving] = "移动时转向光标",
                    [MinDistance] = "忽略近于此距离的光标",
                },
                ["zh-TW"] = new Dictionary<string, string>
                {
                    [Standing] = "靜止時轉向游標",
                    [Moving] = "移動時轉向游標",
                    [MinDistance] = "忽略近於此距離的游標",
                },
            };

        private static readonly Shared.LanguageTable Table = new Shared.LanguageTable(Strings);

        public static string Get(string key) => Table.Get(key);
    }
}
