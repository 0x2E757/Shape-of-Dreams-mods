using System.Collections.Generic;

namespace AreMyGemsCompatible
{
    // The game carries a full localization table, but it is keyed by its own content and offers no
    // way for a mod to register entries - DewLocalization exposes only lookups. What it does
    // expose is the selected language, on DewSave.profileMain.language, so a mod can keep its own
    // strings and pick by that.
    //
    // Language codes are the ones the game ships data for, one folder each under RawData. Anything
    // unrecognised, including a language added by a later patch, falls back to English.
    //
    // **This is the only place in the mod a language other than English appears.** What is decided
    // - whether a memory ever deals damage, heals or grants a barrier - is read out of
    // RawData\en-US and nowhere else, because the answer comes from prose and prose is exactly
    // what a translation changes. These strings are the sentence shown afterwards, and translating
    // a sentence cannot change a verdict.
    //
    // One line per way an essence can starve. The combinations are spelled out rather than
    // assembled from parts, because "neither deals damage nor heals" is a different sentence in
    // every language and not a list with a joining word.
    internal static class Localization
    {
        public const string NoDamage = "warn.damage";
        public const string NoHeal = "warn.heal";
        public const string NoShield = "warn.shield";
        public const string NoHealOrShield = "warn.heal.shield";
        public const string NoDamageOrHeal = "warn.damage.heal";
        public const string Never = "warn.generic";

        public const string SettingBadge = "settings.badge";
        public const string SettingTooltip = "settings.tooltip";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["en-US"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Never triggers here</b>: this memory deals no damage.",
                    [NoHeal] = "<b>Never triggers here</b>: this memory does no healing.",
                    [NoShield] = "<b>Never triggers here</b>: this memory grants no barrier.",
                    [NoHealOrShield] = "<b>Never triggers here</b>: this memory neither heals nor grants a barrier.",
                    [NoDamageOrHeal] = "<b>Never triggers here</b>: this memory neither deals damage nor heals.",
                    [Never] = "<b>Never triggers here</b>: this memory never does what it waits for.",
                    [SettingBadge] = "Mark the slot",
                    [SettingTooltip] = "Add a line to the tooltip",
                },
                ["ru-RU"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Здесь не сработает</b>: эта память не наносит урона.",
                    [NoHeal] = "<b>Здесь не сработает</b>: эта память не лечит.",
                    [NoShield] = "<b>Здесь не сработает</b>: эта память не даёт барьера.",
                    [NoHealOrShield] = "<b>Здесь не сработает</b>: эта память не лечит и не даёт барьера.",
                    [NoDamageOrHeal] = "<b>Здесь не сработает</b>: эта память не наносит урона и не лечит.",
                    [Never] = "<b>Здесь не сработает</b>: эта память никогда не делает того, чего эссенция ждёт.",
                    [SettingBadge] = "Отмечать слот",
                    [SettingTooltip] = "Добавлять строку в подсказку",
                },
                ["de-DE"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Löst hier nie aus</b>: Diese Erinnerung verursacht keinen Schaden.",
                    [NoHeal] = "<b>Löst hier nie aus</b>: Diese Erinnerung heilt nicht.",
                    [NoShield] = "<b>Löst hier nie aus</b>: Diese Erinnerung gewährt keine Barriere.",
                    [NoHealOrShield] = "<b>Löst hier nie aus</b>: Diese Erinnerung heilt nicht und gewährt keine Barriere.",
                    [NoDamageOrHeal] = "<b>Löst hier nie aus</b>: Diese Erinnerung verursacht keinen Schaden und heilt nicht.",
                    [Never] = "<b>Löst hier nie aus</b>: Diese Erinnerung tut nie, worauf die Essenz wartet.",
                    [SettingBadge] = "Slot markieren",
                    [SettingTooltip] = "Zeile im Tooltip ergänzen",
                },
                ["es-MX"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Nunca se activa aquí</b>: esta memoria no inflige daño.",
                    [NoHeal] = "<b>Nunca se activa aquí</b>: esta memoria no cura.",
                    [NoShield] = "<b>Nunca se activa aquí</b>: esta memoria no otorga barrera.",
                    [NoHealOrShield] = "<b>Nunca se activa aquí</b>: esta memoria ni cura ni otorga barrera.",
                    [NoDamageOrHeal] = "<b>Nunca se activa aquí</b>: esta memoria ni inflige daño ni cura.",
                    [Never] = "<b>Nunca se activa aquí</b>: esta memoria nunca hace lo que la esencia espera.",
                    [SettingBadge] = "Marcar la ranura",
                    [SettingTooltip] = "Añadir una línea a la descripción",
                },
                ["fr-FR"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Ne se déclenche jamais ici</b>&#160;: ce souvenir n'inflige aucun dégât.",
                    [NoHeal] = "<b>Ne se déclenche jamais ici</b>&#160;: ce souvenir ne soigne pas.",
                    [NoShield] = "<b>Ne se déclenche jamais ici</b>&#160;: ce souvenir n'accorde pas de barrière.",
                    [NoHealOrShield] = "<b>Ne se déclenche jamais ici</b>&#160;: ce souvenir ne soigne pas et n'accorde pas de barrière.",
                    [NoDamageOrHeal] = "<b>Ne se déclenche jamais ici</b>&#160;: ce souvenir n'inflige aucun dégât et ne soigne pas.",
                    [Never] = "<b>Ne se déclenche jamais ici</b>&#160;: ce souvenir ne fait jamais ce que l'essence attend.",
                    [SettingBadge] = "Marquer l'emplacement",
                    [SettingTooltip] = "Ajouter une ligne à l'infobulle",
                },
                ["it-IT"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Non si attiva mai qui</b>: questo ricordo non infligge danni.",
                    [NoHeal] = "<b>Non si attiva mai qui</b>: questo ricordo non cura.",
                    [NoShield] = "<b>Non si attiva mai qui</b>: questo ricordo non concede barriere.",
                    [NoHealOrShield] = "<b>Non si attiva mai qui</b>: questo ricordo non cura né concede barriere.",
                    [NoDamageOrHeal] = "<b>Non si attiva mai qui</b>: questo ricordo non infligge danni né cura.",
                    [Never] = "<b>Non si attiva mai qui</b>: questo ricordo non fa mai ciò che l'essenza attende.",
                    [SettingBadge] = "Segna lo slot",
                    [SettingTooltip] = "Aggiungi una riga al tooltip",
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>ここでは発動しません</b>：この記憶はダメージを与えません。",
                    [NoHeal] = "<b>ここでは発動しません</b>：この記憶は回復しません。",
                    [NoShield] = "<b>ここでは発動しません</b>：この記憶はバリアを付与しません。",
                    [NoHealOrShield] = "<b>ここでは発動しません</b>：この記憶は回復もバリア付与もしません。",
                    [NoDamageOrHeal] = "<b>ここでは発動しません</b>：この記憶はダメージも回復も与えません。",
                    [Never] = "<b>ここでは発動しません</b>：この記憶はエッセンスが待つ動作を行いません。",
                    [SettingBadge] = "スロットに印を付ける",
                    [SettingTooltip] = "ツールチップに一行追加",
                },
                ["ko-KR"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>여기서는 발동하지 않습니다</b>: 이 기억은 피해를 주지 않습니다.",
                    [NoHeal] = "<b>여기서는 발동하지 않습니다</b>: 이 기억은 회복시키지 않습니다.",
                    [NoShield] = "<b>여기서는 발동하지 않습니다</b>: 이 기억은 보호막을 주지 않습니다.",
                    [NoHealOrShield] = "<b>여기서는 발동하지 않습니다</b>: 이 기억은 회복도 보호막도 주지 않습니다.",
                    [NoDamageOrHeal] = "<b>여기서는 발동하지 않습니다</b>: 이 기억은 피해도 회복도 주지 않습니다.",
                    [Never] = "<b>여기서는 발동하지 않습니다</b>: 이 기억은 정수가 기다리는 일을 하지 않습니다.",
                    [SettingBadge] = "슬롯에 표시",
                    [SettingTooltip] = "툴팁에 한 줄 추가",
                },
                ["pl-PL"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Nigdy się tu nie uruchomi</b>: to wspomnienie nie zadaje obrażeń.",
                    [NoHeal] = "<b>Nigdy się tu nie uruchomi</b>: to wspomnienie nie leczy.",
                    [NoShield] = "<b>Nigdy się tu nie uruchomi</b>: to wspomnienie nie daje bariery.",
                    [NoHealOrShield] = "<b>Nigdy się tu nie uruchomi</b>: to wspomnienie ani nie leczy, ani nie daje bariery.",
                    [NoDamageOrHeal] = "<b>Nigdy się tu nie uruchomi</b>: to wspomnienie ani nie zadaje obrażeń, ani nie leczy.",
                    [Never] = "<b>Nigdy się tu nie uruchomi</b>: to wspomnienie nigdy nie robi tego, na co czeka esencja.",
                    [SettingBadge] = "Oznacz slot",
                    [SettingTooltip] = "Dodaj wiersz do podpowiedzi",
                },
                ["pt-BR"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Nunca ativa aqui</b>: esta memória não causa dano.",
                    [NoHeal] = "<b>Nunca ativa aqui</b>: esta memória não cura.",
                    [NoShield] = "<b>Nunca ativa aqui</b>: esta memória não concede barreira.",
                    [NoHealOrShield] = "<b>Nunca ativa aqui</b>: esta memória não cura nem concede barreira.",
                    [NoDamageOrHeal] = "<b>Nunca ativa aqui</b>: esta memória não causa dano nem cura.",
                    [Never] = "<b>Nunca ativa aqui</b>: esta memória nunca faz o que a essência espera.",
                    [SettingBadge] = "Marcar o encaixe",
                    [SettingTooltip] = "Acrescentar uma linha à dica",
                },
                ["tr-TR"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>Burada hiç tetiklenmez</b>: bu anı hasar vermez.",
                    [NoHeal] = "<b>Burada hiç tetiklenmez</b>: bu anı iyileştirme yapmaz.",
                    [NoShield] = "<b>Burada hiç tetiklenmez</b>: bu anı bariyer vermez.",
                    [NoHealOrShield] = "<b>Burada hiç tetiklenmez</b>: bu anı ne iyileştirir ne de bariyer verir.",
                    [NoDamageOrHeal] = "<b>Burada hiç tetiklenmez</b>: bu anı ne hasar verir ne de iyileştirir.",
                    [Never] = "<b>Burada hiç tetiklenmez</b>: bu anı özün beklediği şeyi hiç yapmaz.",
                    [SettingBadge] = "Yuvayı işaretle",
                    [SettingTooltip] = "İpucuna bir satır ekle",
                },
                ["zh-CN"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>在此永不触发</b>：该记忆不造成伤害。",
                    [NoHeal] = "<b>在此永不触发</b>：该记忆不进行治疗。",
                    [NoShield] = "<b>在此永不触发</b>：该记忆不提供护盾。",
                    [NoHealOrShield] = "<b>在此永不触发</b>：该记忆既不治疗也不提供护盾。",
                    [NoDamageOrHeal] = "<b>在此永不触发</b>：该记忆既不造成伤害也不治疗。",
                    [Never] = "<b>在此永不触发</b>：该记忆从不做精华所等待的事。",
                    [SettingBadge] = "标记槽位",
                    [SettingTooltip] = "在提示中添加一行",
                },
                ["zh-TW"] = new Dictionary<string, string>
                {
                    [NoDamage] = "<b>在此永不觸發</b>：該記憶不造成傷害。",
                    [NoHeal] = "<b>在此永不觸發</b>：該記憶不進行治療。",
                    [NoShield] = "<b>在此永不觸發</b>：該記憶不提供護盾。",
                    [NoHealOrShield] = "<b>在此永不觸發</b>：該記憶既不治療也不提供護盾。",
                    [NoDamageOrHeal] = "<b>在此永不觸發</b>：該記憶既不造成傷害也不治療。",
                    [Never] = "<b>在此永不觸發</b>：該記憶從不做精華所等待的事。",
                    [SettingBadge] = "標記槽位",
                    [SettingTooltip] = "在提示中加入一行",
                },
            };

        private static readonly Shared.LanguageTable Table = new Shared.LanguageTable(Strings);

        public static string Get(string key)
        {
            return Table.Get(key);
        }

        // Which sentence a set of unmet needs deserves. Damage-or-shield and the three-way case
        // cannot arise from anything the game ships and fall to the generic line rather than
        // inventing a sentence for every combination.
        public static string ForNeeds(SlotNeed needs)
        {
            switch (needs)
            {
                case SlotNeed.Damage: return Get(NoDamage);
                case SlotNeed.Heal: return Get(NoHeal);
                case SlotNeed.Shield: return Get(NoShield);
                case SlotNeed.Heal | SlotNeed.Shield: return Get(NoHealOrShield);
                case SlotNeed.Damage | SlotNeed.Heal: return Get(NoDamageOrHeal);
                default: return Get(Never);
            }
        }
    }
}
