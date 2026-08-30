using System.Collections.Generic;

namespace AutoCast
{
    // The game carries a full localization table, but it is keyed by its own content and offers
    // no way for a mod to register entries - DewLocalization exposes only lookups. What it does
    // expose is the selected language, on DewSave.profileMain.language, so a mod can simply keep
    // its own strings and pick by that.
    //
    // Language codes are the ones the game ships data for, one folder each under RawData.
    // Anything unrecognised, including a language added by a later patch, falls back to English.
    //
    // Two sets of strings live here: the hover tooltip on the HUD control, and the row labels in
    // the settings window. The settings ones are applied in AutoCastConfig.BuildWidgets, because
    // ModConfig.LabelText takes a compile-time constant and so can only ever be one language.
    //
    // The tooltip wording deliberately never names the thing in the slot. The game calls it a
    // memory, but every language has its own term and getting one wrong reads worse than not
    // using the word at all. The settings labels do use it, since there they have room to be
    // precise and the game's own term is known.
    //
    // The state is in the tooltip title - "Autocast (on)" - so the bodies do not repeat it.
    internal static class Localization
    {
        public const string TooltipTitle = "tooltip.title";
        public const string StateOff = "state.off";
        public const string StateOn = "state.on";
        public const string StateLocked = "state.locked";
        public const string TooltipOff = "tooltip.off";
        public const string TooltipOn = "tooltip.on";
        public const string TooltipLocked = "tooltip.locked";

        public const string ShowQ = "settings.showQ";
        public const string ShowW = "settings.showW";
        public const string ShowE = "settings.showE";
        public const string ShowR = "settings.showR";
        public const string OnlyInCombat = "settings.onlyInCombat";
        public const string SkipHold = "settings.skipHold";
        public const string CastInterval = "settings.castInterval";
        public const string TargetDelay = "settings.targetDelay";

        private static readonly Dictionary<string, Dictionary<string, string>> Strings =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["en-US"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Autocast",
                    [StateOff] = "off",
                    [StateOn] = "on",
                    [StateLocked] = "unavailable",
                    [TooltipOff] = "Click to cast this automatically the moment its cooldown ends.",
                    [TooltipOn] = "Cast automatically the moment its cooldown ends.",
                    [TooltipLocked] = "This one charges while its key is held, so autocast cannot use it.",
                    [ShowQ] = "Show autocast on Q",
                    [ShowW] = "Show autocast on W",
                    [ShowE] = "Show autocast on E",
                    [ShowR] = "Show autocast on R",
                    [OnlyInCombat] = "Only act in combat",
                    [SkipHold] = "Skip hold-to-charge memories",
                    [CastInterval] = "Minimum seconds between casts",
                    [TargetDelay] = "Ignore enemies for their first (seconds)",
                },
                ["ru-RU"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Автокаст",
                    [StateOff] = "выключен",
                    [StateOn] = "включён",
                    [StateLocked] = "недоступен",
                    [TooltipOff] = "Нажмите, чтобы применять автоматически сразу после отката.",
                    [TooltipOn] = "Применяется автоматически сразу после отката.",
                    [TooltipLocked] = "Заряжается удержанием клавиши, поэтому автокаст с ним не работает.",
                    [ShowQ] = "Показывать автокаст на Q",
                    [ShowW] = "Показывать автокаст на W",
                    [ShowE] = "Показывать автокаст на E",
                    [ShowR] = "Показывать автокаст на R",
                    [OnlyInCombat] = "Действовать только в бою",
                    [SkipHold] = "Пропускать воспоминания с удержанием",
                    [CastInterval] = "Минимум секунд между применениями",
                    [TargetDelay] = "Игнорировать врагов первые (секунды)",
                },
                ["de-DE"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Auto-Wirken",
                    [StateOff] = "aus",
                    [StateOn] = "an",
                    [StateLocked] = "nicht verfügbar",
                    [TooltipOff] = "Klicken, um automatisch zu wirken, sobald die Abklingzeit endet.",
                    [TooltipOn] = "Wird automatisch gewirkt, sobald die Abklingzeit endet.",
                    [TooltipLocked] = "Lädt sich durch Halten der Taste auf und kann daher nicht automatisch gewirkt werden.",
                    [ShowQ] = "Auto-Wirken auf Q zeigen",
                    [ShowW] = "Auto-Wirken auf W zeigen",
                    [ShowE] = "Auto-Wirken auf E zeigen",
                    [ShowR] = "Auto-Wirken auf R zeigen",
                    [OnlyInCombat] = "Nur im Kampf aktiv",
                    [SkipHold] = "Erinnerungen mit Aufladung überspringen",
                    [CastInterval] = "Mindestsekunden zwischen Zaubern",
                    [TargetDelay] = "Gegner ignorieren für ihre ersten (Sekunden)",
                },
                ["es-MX"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Lanzamiento automático",
                    [StateOff] = "desactivado",
                    [StateOn] = "activado",
                    [StateLocked] = "no disponible",
                    [TooltipOff] = "Haz clic para lanzarlo en cuanto termine el enfriamiento.",
                    [TooltipOn] = "Se lanza automáticamente en cuanto termina el enfriamiento.",
                    [TooltipLocked] = "Se carga manteniendo la tecla, así que el lanzamiento automático no puede usarlo.",
                    [ShowQ] = "Mostrar lanzamiento automático en Q",
                    [ShowW] = "Mostrar lanzamiento automático en W",
                    [ShowE] = "Mostrar lanzamiento automático en E",
                    [ShowR] = "Mostrar lanzamiento automático en R",
                    [OnlyInCombat] = "Actuar solo en combate",
                    [SkipHold] = "Omitir recuerdos que se cargan",
                    [CastInterval] = "Segundos mínimos entre lanzamientos",
                    [TargetDelay] = "Ignorar enemigos durante sus primeros (segundos)",
                },
                ["fr-FR"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Lancement auto",
                    [StateOff] = "désactivé",
                    [StateOn] = "activé",
                    [StateLocked] = "indisponible",
                    [TooltipOff] = "Cliquez pour le lancer automatiquement dès la fin du temps de recharge.",
                    [TooltipOn] = "Lancé automatiquement dès la fin du temps de recharge.",
                    [TooltipLocked] = "Se charge en maintenant la touche, le lancement automatique ne peut donc pas le déclencher.",
                    [ShowQ] = "Afficher le lancement auto sur Q",
                    [ShowW] = "Afficher le lancement auto sur W",
                    [ShowE] = "Afficher le lancement auto sur E",
                    [ShowR] = "Afficher le lancement auto sur R",
                    [OnlyInCombat] = "Agir uniquement en combat",
                    [SkipHold] = "Ignorer les souvenirs à charge",
                    [CastInterval] = "Secondes minimum entre les lancements",
                    [TargetDelay] = "Ignorer les ennemis pendant leurs premières (secondes)",
                },
                ["it-IT"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Lancio automatico",
                    [StateOff] = "disattivato",
                    [StateOn] = "attivo",
                    [StateLocked] = "non disponibile",
                    [TooltipOff] = "Clicca per lanciarlo automaticamente appena finisce la ricarica.",
                    [TooltipOn] = "Viene lanciato automaticamente appena finisce la ricarica.",
                    [TooltipLocked] = "Si carica tenendo premuto il tasto, quindi il lancio automatico non può usarlo.",
                    [ShowQ] = "Mostra lancio automatico su Q",
                    [ShowW] = "Mostra lancio automatico su W",
                    [ShowE] = "Mostra lancio automatico su E",
                    [ShowR] = "Mostra lancio automatico su R",
                    [OnlyInCombat] = "Agisci solo in combattimento",
                    [SkipHold] = "Salta i ricordi a carica",
                    [CastInterval] = "Secondi minimi tra i lanci",
                    [TargetDelay] = "Ignora i nemici per i loro primi (secondi)",
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "オートキャスト",
                    [StateOff] = "オフ",
                    [StateOn] = "オン",
                    [StateLocked] = "使用不可",
                    [TooltipOff] = "クリックすると、クールダウンが明けた瞬間に自動で発動します。",
                    [TooltipOn] = "クールダウンが明けた瞬間に自動で発動します。",
                    [TooltipLocked] = "キーを長押しして溜めるため、オートキャストでは扱えません。",
                    [ShowQ] = "Q にオートキャストを表示",
                    [ShowW] = "W にオートキャストを表示",
                    [ShowE] = "E にオートキャストを表示",
                    [ShowR] = "R にオートキャストを表示",
                    [OnlyInCombat] = "戦闘中のみ動作",
                    [SkipHold] = "長押しで溜める記憶をスキップ",
                    [CastInterval] = "発動間隔の最小秒数",
                    [TargetDelay] = "出現直後の敵を無視する秒数",
                },
                ["ko-KR"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "자동 시전",
                    [StateOff] = "꺼짐",
                    [StateOn] = "켜짐",
                    [StateLocked] = "사용 불가",
                    [TooltipOff] = "클릭하면 재사용 대기시간이 끝나는 즉시 자동으로 시전합니다.",
                    [TooltipOn] = "재사용 대기시간이 끝나는 즉시 자동으로 시전합니다.",
                    [TooltipLocked] = "키를 누르고 있어야 충전되므로 자동 시전으로는 사용할 수 없습니다.",
                    [ShowQ] = "Q에 자동 시전 표시",
                    [ShowW] = "W에 자동 시전 표시",
                    [ShowE] = "E에 자동 시전 표시",
                    [ShowR] = "R에 자동 시전 표시",
                    [OnlyInCombat] = "전투 중에만 작동",
                    [SkipHold] = "충전형 기억 건너뛰기",
                    [CastInterval] = "시전 간 최소 초",
                    [TargetDelay] = "적 등장 후 무시할 초",
                },
                ["pl-PL"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Autorzucanie",
                    [StateOff] = "wyłączone",
                    [StateOn] = "włączone",
                    [StateLocked] = "niedostępne",
                    [TooltipOff] = "Kliknij, aby rzucać automatycznie zaraz po odnowieniu.",
                    [TooltipOn] = "Rzucane automatycznie zaraz po odnowieniu.",
                    [TooltipLocked] = "Ładuje się przytrzymaniem klawisza, więc autorzucanie go nie użyje.",
                    [ShowQ] = "Pokaż autorzucanie na Q",
                    [ShowW] = "Pokaż autorzucanie na W",
                    [ShowE] = "Pokaż autorzucanie na E",
                    [ShowR] = "Pokaż autorzucanie na R",
                    [OnlyInCombat] = "Działaj tylko w walce",
                    [SkipHold] = "Pomijaj wspomnienia z ładowaniem",
                    [CastInterval] = "Minimalne sekundy między rzuceniami",
                    [TargetDelay] = "Ignoruj wrogów przez ich pierwsze (sekundy)",
                },
                ["pt-BR"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Conjuração automática",
                    [StateOff] = "desligado",
                    [StateOn] = "ligado",
                    [StateLocked] = "indisponível",
                    [TooltipOff] = "Clique para conjurar automaticamente assim que a recarga acabar.",
                    [TooltipOn] = "Conjurado automaticamente assim que a recarga acaba.",
                    [TooltipLocked] = "Carrega enquanto a tecla é segurada, então a conjuração automática não pode usá-lo.",
                    [ShowQ] = "Mostrar conjuração automática em Q",
                    [ShowW] = "Mostrar conjuração automática em W",
                    [ShowE] = "Mostrar conjuração automática em E",
                    [ShowR] = "Mostrar conjuração automática em R",
                    [OnlyInCombat] = "Agir apenas em combate",
                    [SkipHold] = "Ignorar memórias de carga",
                    [CastInterval] = "Segundos mínimos entre conjurações",
                    [TargetDelay] = "Ignorar inimigos em seus primeiros (segundos)",
                },
                ["tr-TR"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "Otomatik kullanım",
                    [StateOff] = "kapalı",
                    [StateOn] = "açık",
                    [StateLocked] = "kullanılamaz",
                    [TooltipOff] = "Bekleme süresi biter bitmez otomatik kullanmak için tıklayın.",
                    [TooltipOn] = "Bekleme süresi biter bitmez otomatik kullanılır.",
                    [TooltipLocked] = "Tuş basılı tutularak dolduğu için otomatik kullanım bunu kullanamaz.",
                    [ShowQ] = "Q üzerinde otomatik kullanımı göster",
                    [ShowW] = "W üzerinde otomatik kullanımı göster",
                    [ShowE] = "E üzerinde otomatik kullanımı göster",
                    [ShowR] = "R üzerinde otomatik kullanımı göster",
                    [OnlyInCombat] = "Yalnızca savaşta çalış",
                    [SkipHold] = "Basılı tutmayla dolan anıları atla",
                    [CastInterval] = "Kullanımlar arası en az saniye",
                    [TargetDelay] = "Düşmanları ilk (saniye) boyunca yok say",
                },
                ["zh-CN"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "自动施放",
                    [StateOff] = "已关闭",
                    [StateOn] = "已开启",
                    [StateLocked] = "不可用",
                    [TooltipOff] = "点击后将在冷却结束的瞬间自动施放。",
                    [TooltipOn] = "将在冷却结束的瞬间自动施放。",
                    [TooltipLocked] = "需要长按蓄力，因此自动施放无法使用。",
                    [ShowQ] = "在 Q 上显示自动施放",
                    [ShowW] = "在 W 上显示自动施放",
                    [ShowE] = "在 E 上显示自动施放",
                    [ShowR] = "在 R 上显示自动施放",
                    [OnlyInCombat] = "仅在战斗中生效",
                    [SkipHold] = "跳过需要蓄力的记忆",
                    [CastInterval] = "施放之间的最短秒数",
                    [TargetDelay] = "忽略刚出现的敌人（秒）",
                },
                ["zh-TW"] = new Dictionary<string, string>
                {
                    [TooltipTitle] = "自動施放",
                    [StateOff] = "已關閉",
                    [StateOn] = "已開啟",
                    [StateLocked] = "不可用",
                    [TooltipOff] = "點擊後將在冷卻結束的瞬間自動施放。",
                    [TooltipOn] = "將在冷卻結束的瞬間自動施放。",
                    [TooltipLocked] = "需要長按蓄力，因此自動施放無法使用。",
                    [ShowQ] = "在 Q 上顯示自動施放",
                    [ShowW] = "在 W 上顯示自動施放",
                    [ShowE] = "在 E 上顯示自動施放",
                    [ShowR] = "在 R 上顯示自動施放",
                    [OnlyInCombat] = "僅在戰鬥中生效",
                    [SkipHold] = "跳過需要蓄力的記憶",
                    [CastInterval] = "施放之間的最短秒數",
                    [TargetDelay] = "忽略剛出現的敵人（秒）",
                },
            };

        private static readonly Shared.LanguageTable Table = new Shared.LanguageTable(Strings);

        public static string CurrentLanguage => Shared.LanguageTable.CurrentLanguage;

        public static string Get(string key) => Table.Get(key);

        // Every language the game ships puts a parenthetical after the noun, so one shape serves
        // all of them.
        public static string Title(string stateKey)
        {
            return Get(TooltipTitle) + " (" + Get(stateKey) + ")";
        }
    }
}
