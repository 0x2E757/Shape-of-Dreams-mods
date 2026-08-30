using System.Collections.Generic;

namespace MoreGemSlots
{
    // The game carries a full localization table, but it is keyed by its own content and offers
    // no way for a mod to register entries - DewLocalization exposes only lookups. What it does
    // expose is the selected language, on DewSave.profileMain.language, so a mod can simply keep
    // its own strings and pick by that.
    //
    // Language codes are the ones the game ships data for, one folder each under RawData.
    // Anything unrecognised, including a language added by a later patch, falls back to English.
    //
    // "Memory" is the game's own word for a skill, so the translations below use whatever term
    // each language's build uses for it rather than a literal rendering of "skill".
    internal static class Localization
    {
        public const string HeroFirst = "heroFirst";
        public const string HeroSecond = "heroSecond";
        public const string MemoryFirst = "memoryFirst";
        public const string MemorySecond = "memorySecond";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["en-US"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "Hero level for 1st slot",
                    [HeroSecond] = "Hero level for 2nd slot",
                    [MemoryFirst] = "Memory upgrades for 1st slot",
                    [MemorySecond] = "Memory upgrades for 2nd slot",
                },
                ["ru-RU"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "Уровень героя для 1-го слота",
                    [HeroSecond] = "Уровень героя для 2-го слота",
                    [MemoryFirst] = "Усилений воспоминания для 1-го слота",
                    [MemorySecond] = "Усилений воспоминания для 2-го слота",
                },
                ["de-DE"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "Heldenstufe für 1. Platz",
                    [HeroSecond] = "Heldenstufe für 2. Platz",
                    [MemoryFirst] = "Erinnerungs-Upgrades für 1. Platz",
                    [MemorySecond] = "Erinnerungs-Upgrades für 2. Platz",
                },
                ["es-MX"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "Nivel de héroe para la 1.ª ranura",
                    [HeroSecond] = "Nivel de héroe para la 2.ª ranura",
                    [MemoryFirst] = "Mejoras del recuerdo para la 1.ª ranura",
                    [MemorySecond] = "Mejoras del recuerdo para la 2.ª ranura",
                },
                ["fr-FR"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "Niveau du héros pour le 1er emplacement",
                    [HeroSecond] = "Niveau du héros pour le 2e emplacement",
                    [MemoryFirst] = "Améliorations du souvenir pour le 1er emplacement",
                    [MemorySecond] = "Améliorations du souvenir pour le 2e emplacement",
                },
                ["it-IT"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "Livello dell'eroe per il 1° slot",
                    [HeroSecond] = "Livello dell'eroe per il 2° slot",
                    [MemoryFirst] = "Potenziamenti del ricordo per il 1° slot",
                    [MemorySecond] = "Potenziamenti del ricordo per il 2° slot",
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "1つ目のスロットに必要なヒーローレベル",
                    [HeroSecond] = "2つ目のスロットに必要なヒーローレベル",
                    [MemoryFirst] = "1つ目のスロットに必要な記憶の強化回数",
                    [MemorySecond] = "2つ目のスロットに必要な記憶の強化回数",
                },
                ["ko-KR"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "첫 번째 슬롯 해금 영웅 레벨",
                    [HeroSecond] = "두 번째 슬롯 해금 영웅 레벨",
                    [MemoryFirst] = "첫 번째 슬롯 해금 기억 강화 횟수",
                    [MemorySecond] = "두 번째 슬롯 해금 기억 강화 횟수",
                },
                ["pl-PL"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "Poziom bohatera dla 1. gniazda",
                    [HeroSecond] = "Poziom bohatera dla 2. gniazda",
                    [MemoryFirst] = "Ulepszenia wspomnienia dla 1. gniazda",
                    [MemorySecond] = "Ulepszenia wspomnienia dla 2. gniazda",
                },
                ["pt-BR"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "Nível do herói para o 1º espaço",
                    [HeroSecond] = "Nível do herói para o 2º espaço",
                    [MemoryFirst] = "Melhorias da memória para o 1º espaço",
                    [MemorySecond] = "Melhorias da memória para o 2º espaço",
                },
                ["tr-TR"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "1. yuva için kahraman seviyesi",
                    [HeroSecond] = "2. yuva için kahraman seviyesi",
                    [MemoryFirst] = "1. yuva için anı yükseltmesi",
                    [MemorySecond] = "2. yuva için anı yükseltmesi",
                },
                ["zh-CN"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "解锁第 1 个槽位的英雄等级",
                    [HeroSecond] = "解锁第 2 个槽位的英雄等级",
                    [MemoryFirst] = "解锁第 1 个槽位的记忆强化次数",
                    [MemorySecond] = "解锁第 2 个槽位的记忆强化次数",
                },
                ["zh-TW"] = new Dictionary<string, string>
                {
                    [HeroFirst] = "解鎖第 1 個槽位的英雄等級",
                    [HeroSecond] = "解鎖第 2 個槽位的英雄等級",
                    [MemoryFirst] = "解鎖第 1 個槽位的記憶強化次數",
                    [MemorySecond] = "解鎖第 2 個槽位的記憶強化次數",
                },
            };

        private static readonly Shared.LanguageTable Table = new Shared.LanguageTable(Strings);

        public static string CurrentLanguage => Shared.LanguageTable.CurrentLanguage;

        public static string Get(string key) => Table.Get(key);
    }
}
