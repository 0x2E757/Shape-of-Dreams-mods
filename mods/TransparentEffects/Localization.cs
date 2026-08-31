using System.Collections.Generic;

namespace TransparentEffects
{
    // The game carries a full localization table, but it is keyed by its own content and offers no
    // way for a mod to register entries - DewLocalization exposes only lookups. What it does
    // expose is the selected language, on DewSave.profileMain.language, so a mod can keep its own
    // strings and pick by that.
    //
    // Language codes are the ones the game ships data for, one folder each under RawData. Anything
    // unrecognised, including a language added by a later patch, falls back to English.
    //
    // Two strings, both settings rows, applied in TransparentEffectsConfig.BuildWidgets -
    // ModConfig.LabelText takes a compile-time constant and so can only ever be one language.
    //
    // The second row deliberately does not reuse the game's own wording for its "reduce other
    // players' effects" setting, even though that phrase exists in the table in every language.
    // The two are not the same control and they multiply rather than replace each other, so a row
    // that read identically would be read as the same switch moved somewhere new.
    internal static class Localization
    {
        public const string Mine = "settings.mine";
        public const string Others = "settings.others";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["en-US"] = new Dictionary<string, string>
                {
                    [Mine] = "Opacity of my own effects",
                    [Others] = "Opacity of other players' effects",
                },
                ["ru-RU"] = new Dictionary<string, string>
                {
                    [Mine] = "Непрозрачность моих эффектов",
                    [Others] = "Непрозрачность эффектов других игроков",
                },
                ["de-DE"] = new Dictionary<string, string>
                {
                    [Mine] = "Deckkraft meiner eigenen Effekte",
                    [Others] = "Deckkraft der Effekte anderer Spieler",
                },
                ["es-MX"] = new Dictionary<string, string>
                {
                    [Mine] = "Opacidad de mis propios efectos",
                    [Others] = "Opacidad de los efectos de otros jugadores",
                },
                ["fr-FR"] = new Dictionary<string, string>
                {
                    [Mine] = "Opacité de mes propres effets",
                    [Others] = "Opacité des effets des autres joueurs",
                },
                ["it-IT"] = new Dictionary<string, string>
                {
                    [Mine] = "Opacità dei miei effetti",
                    [Others] = "Opacità degli effetti degli altri giocatori",
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                    [Mine] = "自分のエフェクトの不透明度",
                    [Others] = "他プレイヤーのエフェクトの不透明度",
                },
                ["ko-KR"] = new Dictionary<string, string>
                {
                    [Mine] = "내 효과의 불투명도",
                    [Others] = "다른 플레이어 효과의 불투명도",
                },
                ["pl-PL"] = new Dictionary<string, string>
                {
                    [Mine] = "Krycie moich własnych efektów",
                    [Others] = "Krycie efektów innych graczy",
                },
                ["pt-BR"] = new Dictionary<string, string>
                {
                    [Mine] = "Opacidade dos meus próprios efeitos",
                    [Others] = "Opacidade dos efeitos de outros jogadores",
                },
                ["tr-TR"] = new Dictionary<string, string>
                {
                    [Mine] = "Kendi efektlerimin görünürlüğü",
                    [Others] = "Diğer oyuncuların efektlerinin görünürlüğü",
                },
                ["zh-CN"] = new Dictionary<string, string>
                {
                    [Mine] = "自己技能特效的不透明度",
                    [Others] = "其他玩家特效的不透明度",
                },
                ["zh-TW"] = new Dictionary<string, string>
                {
                    [Mine] = "自己技能特效的不透明度",
                    [Others] = "其他玩家特效的不透明度",
                },
            };

        private static readonly Shared.LanguageTable Table = new Shared.LanguageTable(Strings);

        public static string Get(string key) => Table.Get(key);
    }
}
