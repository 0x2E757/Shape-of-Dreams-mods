using System.Collections.Generic;
using UnityEngine;

namespace ControlledMerge
{
    // The game's localization table is keyed by its own content and offers no way for a mod to
    // register entries, but it does expose the selected language, on DewSave.profileMain.language,
    // so a mod can keep its own strings and pick by that. Anything unrecognised falls back to
    // English.
    //
    // The line under a description comes in three whole sentences - shared between memories,
    // doubled up in one, both - rather than assembled from parts, because the joining words and
    // the punctuation differ in every language. With both reasons it is a heading and a two-item
    // list, which a narrow tooltip wraps far better than one long line. Every count is two or more, and the copies
    // are given as "copies here: N" wherever a language would otherwise need a plural that changes
    // with the number.
    //
    // {0} is what is left of the essence, in percent. The memories' count and cut come next, then
    // the copies' - in the one-reason sentences, whichever of the two applies.
    internal static class Localization
    {
        public const string Both = "line.both";
        public const string Memories = "line.memories";
        public const string Copies = "line.copies";
        public const string Merges = "line.merge";
        public const string Short = "line.short";

        // Appended to a key for its wording when AreMyGemsCompatible is loaded: copies that can
        // never fire are then not counted, so what is counted is where the essence is *used*,
        // not merely where it is equipped. Languages whose wording was already neutral have no
        // such entry and keep theirs (see Word).
        public const string Used = ".used";

        public const string SettingTwoMemories = "settings.memories.two";
        public const string SettingThreeMemories = "settings.memories.three";
        public const string SettingTwoCopies = "settings.copies.two";
        public const string SettingThreeCopies = "settings.copies.three";
        public const string SettingMerge = "settings.merge";
        public const string SettingTooltip = "settings.tooltip";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["en-US"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Weakened to {0}%</b>:\n• Equipped in {1} memories: -{2}%\n• Equipped {3} copies in this memory: -{4}%",
                    [Memories] = "<b>Weakened to {0}%</b>: equipped in {1} memories (-{2}%).",
                    [Copies] = "<b>Weakened to {0}%</b>: equipped {1} copies in this memory (-{2}%).",
                    [Both + Used] = "<b>Weakened to {0}%</b>:\n• Used in {1} memories: -{2}%\n• Used {3} copies in this memory: -{4}%",
                    [Memories + Used] = "<b>Weakened to {0}%</b>: used in {1} memories (-{2}%).",
                    [Copies + Used] = "<b>Weakened to {0}%</b>: used {1} copies in this memory (-{2}%).",
                    [Short] = "Weakened by diminishing returns.",
                    [Merges] = "<b>Choosing this slot merges them</b>: quality from {0}% to {1}%.",
                    [SettingTwoMemories] = "Cut when equipped in 2 memories, %",
                    [SettingThreeMemories] = "Cut when equipped in 3 or more memories, %",
                    [SettingTwoMemories + Used] = "Cut when used in 2 memories, %",
                    [SettingThreeMemories + Used] = "Cut when used in 3 or more memories, %",
                    [SettingTwoCopies] = "Cut for 2 copies in one memory, %",
                    [SettingThreeCopies] = "Cut for 3 or more copies in one memory, %",
                    [SettingMerge] = "Merge by choosing a slot with the same essence",
                    [SettingTooltip] = "Add a line to the tooltip",
                },
                ["ru-RU"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Ослаблена до {0}%</b>:\n• стоит в {1} памятях: -{2}%\n• копий в этой памяти: {3}, -{4}%",
                    [Memories] = "<b>Ослаблена до {0}%</b>: стоит в {1} памятях (-{2}%).",
                    [Copies] = "<b>Ослаблена до {0}%</b>: копий в этой памяти: {1} (-{2}%).",
                    [Both + Used] = "<b>Ослаблена до {0}%</b>:\n• работает в {1} памятях: -{2}%\n• работающих копий в этой памяти: {3}, -{4}%",
                    [Memories + Used] = "<b>Ослаблена до {0}%</b>: работает в {1} памятях (-{2}%).",
                    [Copies + Used] = "<b>Ослаблена до {0}%</b>: работающих копий в этой памяти: {1} (-{2}%).",
                    [Short] = "Ослаблена из-за убывающей отдачи.",
                    [Merges] = "<b>Выбор этого слота объединит их</b>: качество с {0}% до {1}%.",
                    [SettingTwoMemories] = "Ослабление в 2 памятях, %",
                    [SettingThreeMemories] = "Ослабление в 3 и более памятях, %",
                    [SettingTwoCopies] = "Ослабление за 2 копии в одной памяти, %",
                    [SettingThreeCopies] = "Ослабление за 3 и более копий в одной памяти, %",
                    [SettingMerge] = "Объединять, если выбран слот с той же эссенцией",
                    [SettingTooltip] = "Добавлять строку в подсказку",
                },
                ["de-DE"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Abgeschwächt auf {0}%</b>:\n• in {1} Erinnerungen: -{2}%\n• {3} Kopien in dieser Erinnerung: -{4}%",
                    [Memories] = "<b>Abgeschwächt auf {0}%</b>: in {1} Erinnerungen (-{2}%).",
                    [Copies] = "<b>Abgeschwächt auf {0}%</b>: {1} Kopien in dieser Erinnerung (-{2}%).",
                    [Both + Used] = "<b>Abgeschwächt auf {0}%</b>:\n• in {1} Erinnerungen aktiv: -{2}%\n• {3} aktive Kopien in dieser Erinnerung: -{4}%",
                    [Memories + Used] = "<b>Abgeschwächt auf {0}%</b>: in {1} Erinnerungen aktiv (-{2}%).",
                    [Copies + Used] = "<b>Abgeschwächt auf {0}%</b>: {1} aktive Kopien in dieser Erinnerung (-{2}%).",
                    [Short] = "Durch abnehmenden Ertrag abgeschwächt.",
                    [Merges] = "<b>Dieser Slot vereint sie</b>: Qualität von {0}% auf {1}%.",
                    [SettingTwoMemories] = "Abzug in 2 Erinnerungen, %",
                    [SettingThreeMemories] = "Abzug in 3 oder mehr Erinnerungen, %",
                    [SettingTwoCopies] = "Abzug für 2 Kopien in einer Erinnerung, %",
                    [SettingThreeCopies] = "Abzug für 3 oder mehr Kopien in einer Erinnerung, %",
                    [SettingMerge] = "Vereinen durch Wahl eines Slots mit derselben Essenz",
                    [SettingTooltip] = "Zeile im Tooltip ergänzen",
                },
                ["es-MX"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Debilitada al {0}%</b>:\n• en {1} memorias: -{2}%\n• {3} copias en esta memoria: -{4}%",
                    [Memories] = "<b>Debilitada al {0}%</b>: en {1} memorias (-{2}%).",
                    [Copies] = "<b>Debilitada al {0}%</b>: {1} copias en esta memoria (-{2}%).",
                    [Both + Used] = "<b>Debilitada al {0}%</b>:\n• activa en {1} memorias: -{2}%\n• {3} copias activas en esta memoria: -{4}%",
                    [Memories + Used] = "<b>Debilitada al {0}%</b>: activa en {1} memorias (-{2}%).",
                    [Copies + Used] = "<b>Debilitada al {0}%</b>: {1} copias activas en esta memoria (-{2}%).",
                    [Short] = "Debilitada por rendimientos decrecientes.",
                    [Merges] = "<b>Elegir esta ranura las fusiona</b>: calidad de {0}% a {1}%.",
                    [SettingTwoMemories] = "Reducción en 2 memorias, %",
                    [SettingThreeMemories] = "Reducción en 3 o más memorias, %",
                    [SettingTwoCopies] = "Reducción por 2 copias en una memoria, %",
                    [SettingThreeCopies] = "Reducción por 3 o más copias en una memoria, %",
                    [SettingMerge] = "Fusionar al elegir una ranura con la misma esencia",
                    [SettingTooltip] = "Añadir una línea a la descripción",
                },
                ["fr-FR"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Affaiblie à {0}\u00A0%</b>\u00A0:\n• dans {1} souvenirs\u00A0: -{2}\u00A0%\n• {3} copies dans ce souvenir\u00A0: -{4}\u00A0%",
                    [Memories] = "<b>Affaiblie à {0}\u00A0%</b>\u00A0: dans {1} souvenirs (-{2}\u00A0%).",
                    [Copies] = "<b>Affaiblie à {0}\u00A0%</b>\u00A0: {1} copies dans ce souvenir (-{2}\u00A0%).",
                    [Both + Used] = "<b>Affaiblie à {0}\u00A0%</b>\u00A0:\n• active dans {1} souvenirs\u00A0: -{2}\u00A0%\n• {3} copies actives dans ce souvenir\u00A0: -{4}\u00A0%",
                    [Memories + Used] = "<b>Affaiblie à {0}\u00A0%</b>\u00A0: active dans {1} souvenirs (-{2}\u00A0%).",
                    [Copies + Used] = "<b>Affaiblie à {0}\u00A0%</b>\u00A0: {1} copies actives dans ce souvenir (-{2}\u00A0%).",
                    [Short] = "Affaiblie par les rendements décroissants.",
                    [Merges] = "<b>Choisir cet emplacement les fusionne</b>\u00A0: qualité de {0}\u00A0% à {1}\u00A0%.",
                    [SettingTwoMemories] = "Réduction dans 2 souvenirs, %",
                    [SettingThreeMemories] = "Réduction dans 3 souvenirs ou plus, %",
                    [SettingTwoCopies] = "Réduction pour 2 copies dans un souvenir, %",
                    [SettingThreeCopies] = "Réduction pour 3 copies ou plus dans un souvenir, %",
                    [SettingMerge] = "Fusionner en choisissant un emplacement avec la même essence",
                    [SettingTooltip] = "Ajouter une ligne à l'infobulle",
                },
                ["it-IT"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Indebolita al {0}%</b>:\n• in {1} ricordi: -{2}%\n• {3} copie in questo ricordo: -{4}%",
                    [Memories] = "<b>Indebolita al {0}%</b>: in {1} ricordi (-{2}%).",
                    [Copies] = "<b>Indebolita al {0}%</b>: {1} copie in questo ricordo (-{2}%).",
                    [Both + Used] = "<b>Indebolita al {0}%</b>:\n• attiva in {1} ricordi: -{2}%\n• {3} copie attive in questo ricordo: -{4}%",
                    [Memories + Used] = "<b>Indebolita al {0}%</b>: attiva in {1} ricordi (-{2}%).",
                    [Copies + Used] = "<b>Indebolita al {0}%</b>: {1} copie attive in questo ricordo (-{2}%).",
                    [Short] = "Indebolita dai rendimenti decrescenti.",
                    [Merges] = "<b>Scegliere questo slot le unisce</b>: qualità da {0}% a {1}%.",
                    [SettingTwoMemories] = "Riduzione in 2 ricordi, %",
                    [SettingThreeMemories] = "Riduzione in 3 o più ricordi, %",
                    [SettingTwoCopies] = "Riduzione per 2 copie in un ricordo, %",
                    [SettingThreeCopies] = "Riduzione per 3 o più copie in un ricordo, %",
                    [SettingMerge] = "Unisci scegliendo uno slot con la stessa essenza",
                    [SettingTooltip] = "Aggiungi una riga al tooltip",
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                    [Both] = "<b>効果が{0}%に低下</b>：\n• {1}個の記憶に装着：-{2}%\n• この記憶に{3}個：-{4}%",
                    [Memories] = "<b>効果が{0}%に低下</b>：{1}個の記憶に装着（-{2}%）。",
                    [Copies] = "<b>効果が{0}%に低下</b>：この記憶に{1}個（-{2}%）。",
                    [Both + Used] = "<b>効果が{0}%に低下</b>：\n• {1}個の記憶で有効：-{2}%\n• この記憶で{3}個が有効：-{4}%",
                    [Memories + Used] = "<b>効果が{0}%に低下</b>：{1}個の記憶で有効（-{2}%）。",
                    [Copies + Used] = "<b>効果が{0}%に低下</b>：この記憶で{1}個が有効（-{2}%）。",
                    [Short] = "収穫逓減により効果が低下。",
                    [Merges] = "<b>このスロットを選ぶと合成されます</b>：品質が{0}%から{1}%に。",
                    [SettingTwoMemories] = "2つの記憶に装着時の減少（%）",
                    [SettingThreeMemories] = "3つ以上の記憶に装着時の減少（%）",
                    [SettingTwoCopies] = "1つの記憶に2個の時の減少（%）",
                    [SettingThreeCopies] = "1つの記憶に3個以上の時の減少（%）",
                    [SettingMerge] = "同じエッセンスのスロットを選ぶと合成",
                    [SettingTooltip] = "ツールチップに一行追加",
                },
                ["ko-KR"] = new Dictionary<string, string>
                {
                    [Both] = "<b>효과가 {0}%로 감소</b>:\n• {1}개의 기억에 장착: -{2}%\n• 이 기억에 {3}개: -{4}%",
                    [Memories] = "<b>효과가 {0}%로 감소</b>: {1}개의 기억에 장착 (-{2}%).",
                    [Copies] = "<b>효과가 {0}%로 감소</b>: 이 기억에 {1}개 (-{2}%).",
                    [Both + Used] = "<b>효과가 {0}%로 감소</b>:\n• {1}개의 기억에서 작동: -{2}%\n• 이 기억에서 {3}개 작동: -{4}%",
                    [Memories + Used] = "<b>효과가 {0}%로 감소</b>: {1}개의 기억에서 작동 (-{2}%).",
                    [Copies + Used] = "<b>효과가 {0}%로 감소</b>: 이 기억에서 {1}개 작동 (-{2}%).",
                    [Short] = "수확 체감으로 효과 감소.",
                    [Merges] = "<b>이 슬롯을 선택하면 합쳐집니다</b>: 품질 {0}%에서 {1}%로.",
                    [SettingTwoMemories] = "기억 2개에 장착 시 감소, %",
                    [SettingThreeMemories] = "기억 3개 이상에 장착 시 감소, %",
                    [SettingTwoCopies] = "한 기억에 2개일 때 감소, %",
                    [SettingThreeCopies] = "한 기억에 3개 이상일 때 감소, %",
                    [SettingMerge] = "같은 정수가 있는 슬롯을 선택하면 합치기",
                    [SettingTooltip] = "툴팁에 한 줄 추가",
                },
                ["pl-PL"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Osłabiona do {0}%</b>:\n• w {1} wspomnieniach: -{2}%\n• kopii w tym wspomnieniu: {3}, -{4}%",
                    [Memories] = "<b>Osłabiona do {0}%</b>: w {1} wspomnieniach (-{2}%).",
                    [Copies] = "<b>Osłabiona do {0}%</b>: kopii w tym wspomnieniu: {1} (-{2}%).",
                    [Both + Used] = "<b>Osłabiona do {0}%</b>:\n• działa w {1} wspomnieniach: -{2}%\n• działających kopii w tym wspomnieniu: {3}, -{4}%",
                    [Memories + Used] = "<b>Osłabiona do {0}%</b>: działa w {1} wspomnieniach (-{2}%).",
                    [Copies + Used] = "<b>Osłabiona do {0}%</b>: działających kopii w tym wspomnieniu: {1} (-{2}%).",
                    [Short] = "Osłabiona przez malejące korzyści.",
                    [Merges] = "<b>Wybranie tego slotu je połączy</b>: jakość z {0}% do {1}%.",
                    [SettingTwoMemories] = "Osłabienie w 2 wspomnieniach, %",
                    [SettingThreeMemories] = "Osłabienie w 3 i więcej wspomnieniach, %",
                    [SettingTwoCopies] = "Osłabienie za 2 kopie w jednym wspomnieniu, %",
                    [SettingThreeCopies] = "Osłabienie za 3 i więcej kopii w jednym wspomnieniu, %",
                    [SettingMerge] = "Łącz po wybraniu slotu z tą samą esencją",
                    [SettingTooltip] = "Dodaj wiersz do podpowiedzi",
                },
                ["pt-BR"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Enfraquecida para {0}%</b>:\n• em {1} memórias: -{2}%\n• {3} cópias nesta memória: -{4}%",
                    [Memories] = "<b>Enfraquecida para {0}%</b>: em {1} memórias (-{2}%).",
                    [Copies] = "<b>Enfraquecida para {0}%</b>: {1} cópias nesta memória (-{2}%).",
                    [Both + Used] = "<b>Enfraquecida para {0}%</b>:\n• ativa em {1} memórias: -{2}%\n• {3} cópias ativas nesta memória: -{4}%",
                    [Memories + Used] = "<b>Enfraquecida para {0}%</b>: ativa em {1} memórias (-{2}%).",
                    [Copies + Used] = "<b>Enfraquecida para {0}%</b>: {1} cópias ativas nesta memória (-{2}%).",
                    [Short] = "Enfraquecida por retornos decrescentes.",
                    [Merges] = "<b>Escolher este encaixe as funde</b>: qualidade de {0}% para {1}%.",
                    [SettingTwoMemories] = "Redução em 2 memórias, %",
                    [SettingThreeMemories] = "Redução em 3 ou mais memórias, %",
                    [SettingTwoCopies] = "Redução por 2 cópias numa memória, %",
                    [SettingThreeCopies] = "Redução por 3 ou mais cópias numa memória, %",
                    [SettingMerge] = "Fundir ao escolher um encaixe com a mesma essência",
                    [SettingTooltip] = "Acrescentar uma linha à dica",
                },
                ["tr-TR"] = new Dictionary<string, string>
                {
                    [Both] = "<b>Etkisi: %{0}</b>:\n• {1} anıda takılı: -%{2}\n• bu anıda {3} kopya: -%{4}",
                    [Memories] = "<b>Etkisi: %{0}</b>: {1} anıda takılı (-%{2}).",
                    [Copies] = "<b>Etkisi: %{0}</b>: bu anıda {1} kopya (-%{2}).",
                    [Both + Used] = "<b>Etkisi: %{0}</b>:\n• {1} anıda etkin: -%{2}\n• bu anıda {3} etkin kopya: -%{4}",
                    [Memories + Used] = "<b>Etkisi: %{0}</b>: {1} anıda etkin (-%{2}).",
                    [Copies + Used] = "<b>Etkisi: %{0}</b>: bu anıda {1} etkin kopya (-%{2}).",
                    [Short] = "Azalan verim nedeniyle zayıfladı.",
                    [Merges] = "<b>Bu yuvayı seçmek onları birleştirir</b>: kalite %{0} yerine %{1} olur.",
                    [SettingTwoMemories] = "2 anıda azalma, %",
                    [SettingThreeMemories] = "3 veya daha fazla anıda azalma, %",
                    [SettingTwoCopies] = "Bir anıda 2 kopya için azalma, %",
                    [SettingThreeCopies] = "Bir anıda 3 veya daha fazla kopya için azalma, %",
                    [SettingMerge] = "Aynı özü taşıyan yuva seçilince birleştir",
                    [SettingTooltip] = "İpucuna bir satır ekle",
                },
                ["zh-CN"] = new Dictionary<string, string>
                {
                    [Both] = "<b>效果降至{0}%</b>：\n• 装备于{1}个记忆：-{2}%\n• 此记忆中有{3}个：-{4}%",
                    [Memories] = "<b>效果降至{0}%</b>：装备于{1}个记忆（-{2}%）。",
                    [Copies] = "<b>效果降至{0}%</b>：此记忆中有{1}个（-{2}%）。",
                    [Both + Used] = "<b>效果降至{0}%</b>：\n• 在{1}个记忆中生效：-{2}%\n• 此记忆中有{3}个生效：-{4}%",
                    [Memories + Used] = "<b>效果降至{0}%</b>：在{1}个记忆中生效（-{2}%）。",
                    [Copies + Used] = "<b>效果降至{0}%</b>：此记忆中有{1}个生效（-{2}%）。",
                    [Short] = "因收益递减而削弱。",
                    [Merges] = "<b>选择此槽位将合并它们</b>：品质从{0}%变为{1}%。",
                    [SettingTwoMemories] = "装备于2个记忆时的削减（%）",
                    [SettingThreeMemories] = "装备于3个及以上记忆时的削减（%）",
                    [SettingTwoCopies] = "同一记忆中有2个时的削减（%）",
                    [SettingThreeCopies] = "同一记忆中有3个及以上时的削减（%）",
                    [SettingMerge] = "选择装有相同精华的槽位时合并",
                    [SettingTooltip] = "在提示中添加一行",
                },
                ["zh-TW"] = new Dictionary<string, string>
                {
                    [Both] = "<b>效果降至{0}%</b>：\n• 裝備於{1}個記憶：-{2}%\n• 此記憶中有{3}個：-{4}%",
                    [Memories] = "<b>效果降至{0}%</b>：裝備於{1}個記憶（-{2}%）。",
                    [Copies] = "<b>效果降至{0}%</b>：此記憶中有{1}個（-{2}%）。",
                    [Both + Used] = "<b>效果降至{0}%</b>：\n• 在{1}個記憶中生效：-{2}%\n• 此記憶中有{3}個生效：-{4}%",
                    [Memories + Used] = "<b>效果降至{0}%</b>：在{1}個記憶中生效（-{2}%）。",
                    [Copies + Used] = "<b>效果降至{0}%</b>：此記憶中有{1}個生效（-{2}%）。",
                    [Short] = "因收益遞減而削弱。",
                    [Merges] = "<b>選擇此槽位將合併它們</b>：品質從{0}%變為{1}%。",
                    [SettingTwoMemories] = "裝備於2個記憶時的削減（%）",
                    [SettingThreeMemories] = "裝備於3個以上記憶時的削減（%）",
                    [SettingTwoCopies] = "同一記憶中有2個時的削減（%）",
                    [SettingThreeCopies] = "同一記憶中有3個以上時的削減（%）",
                    [SettingMerge] = "選擇裝有相同精華的槽位時合併",
                    [SettingTooltip] = "在提示中加入一行",
                },
            };

        private static readonly Shared.LanguageTable Table = new Shared.LanguageTable(Strings);

        public static string Get(string key)
        {
            return Table.Get(key);
        }

        // Get, but the ".used" wording when AreMyGemsCompatible decides what counts and the
        // current language has one - falling back to English's only when the plain key would too.
        public static string Word(string key)
        {
            if (Fit.Available)
            {
                string language = Shared.LanguageTable.CurrentLanguage;
                if (Strings.TryGetValue(language, out var table) && table.TryGetValue(key + Used, out var used)) return used;
                if (!(Strings.TryGetValue(language, out table) && table.ContainsKey(key)) &&
                    Strings["en-US"].TryGetValue(key + Used, out used)) return used;
            }
            return Get(key);
        }

        public static string Line(Share share)
        {
            int left = Percent(share.Factor);
            int memoryCut = 100 - Percent(share.MemoryFactor);
            int copyCut = 100 - Percent(share.CopyFactor);

            bool shared = share.MemoryFactor < 0.9999f;
            bool doubled = share.CopyFactor < 0.9999f;

            if (shared && doubled) return string.Format(Word(Both), left, share.Memories, memoryCut, share.Copies, copyCut);
            if (shared) return string.Format(Word(Memories), left, share.Memories, memoryCut);
            return string.Format(Word(Copies), left, share.Copies, copyCut);
        }

        public static string MergeLine(int before, int after)
        {
            return string.Format(Get(Merges), before, after);
        }

        // What follows a number in the details-key formula: each cut on its own, in the order Line
        // lists them - the memories' first, then the copies'. Not a sentence, so not a table entry:
        // only where the percent sign goes differs, and only in two languages.
        public static string Formula(Share share)
        {
            string text = string.Empty;
            if (share.MemoryFactor < 0.9999f) text += Times(share.MemoryFactor);
            if (share.CopyFactor < 0.9999f) text += (text.Length > 0 ? " " : string.Empty) + Times(share.CopyFactor);
            return text;
        }

        private static string Times(float factor)
        {
            int left = Percent(factor);
            switch (Shared.LanguageTable.CurrentLanguage)
            {
                case "tr-TR": return "× %" + left;
                case "fr-FR": return "× " + left + " %";
                default: return "× " + left + "%";
            }
        }

        // The colour of everything this mod adds about a cut.
        public static string Paint(string text) => "<color=#ffc46b>" + text + "</color>";

        private static int Percent(float factor) => Mathf.RoundToInt(factor * 100f);
    }
}
