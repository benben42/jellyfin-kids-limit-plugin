using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.KidsLimit.Configuration;

/// <summary>
/// Plugin configuration. This is serialized to XML by Jellyfin, therefore every
/// member must be XML-serializer friendly: no <see cref="Dictionary{TKey, TValue}"/>,
/// only primitives, nullable primitives, strings and <see cref="List{T}"/> of the same.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Default header for the "you are nearly out of time" warning.</summary>
    public const string DefaultWarnHeader = "⏰ Almost done";

    /// <summary>Default body for the warning. <c>{minutes}</c> is substituted.</summary>
    public const string DefaultWarnText = "⏰ {minutes} more minutes of TV, then it's time to stop. 📺";

    /// <summary>Default header shown as the daily limit is reached.</summary>
    public const string DefaultLimitHeader = "🛑 Time's up!";

    /// <summary>Default body shown as the daily limit is reached.</summary>
    public const string DefaultLimitText = "📺💤 No more TV right now. 🪙 Do a chore to earn more time!";

    /// <summary>Default header shown when an out-of-time kid presses play again.</summary>
    public const string DefaultBlockedHeader = "🛑 TV is sleeping";

    /// <summary>Default body shown when an out-of-time kid presses play again.</summary>
    public const string DefaultBlockedText = "📺💤 Not now. 🪙 Earn coins with a chore, then you can watch!";

    /// <summary>Default header for a parent's "Stop now".</summary>
    public const string DefaultParentStopHeader = "🛑 Stopped";

    /// <summary>Default body for a parent's "Stop now".</summary>
    public const string DefaultParentStopText = "📺💤 A grown-up turned the TV off. 🤗";

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class
    /// with the shipped defaults described in the requirements.
    /// </summary>
    public PluginConfiguration()
    {
        MiddayStartMinutes = 12 * 60; // 12:00
        EveningStartMinutes = 18 * 60; // 18:00
        BonusApiToken = string.Empty;

        WarnMessageHeader = DefaultWarnHeader;
        WarnMessageText = DefaultWarnText;
        LimitMessageHeader = DefaultLimitHeader;
        LimitMessageText = DefaultLimitText;
        BlockedMessageHeader = DefaultBlockedHeader;
        BlockedMessageText = DefaultBlockedText;
        ParentStopMessageHeader = DefaultParentStopHeader;
        ParentStopMessageText = DefaultParentStopText;
        MessageSeconds = 8;
        StopGraceSeconds = 6;

        CoinMinutes = 5;
        BankCapCoins = 24;
        MaxRedeemCoinsPerDay = 6;
        DefaultSpendCoins = 3;

        OverLimitAlertEnabled = true;
        OverLimitAlertMinutes = 3;

        // IMPORTANT: do NOT seed the built-in presets here. The XML serializer *appends*
        // to a collection that the constructor already populated instead of replacing it,
        // so seeding defaults in the constructor duplicated every built-in preset on each
        // deserialize (i.e. every server restart / plugin update). Start empty and let
        // Plugin.MigrateConfiguration seed the defaults exactly once on first run.
        Presets = new List<Preset>();
        Users = new List<UserLimitConfig>();
        Chores = new List<Chore>();
        ReferenceItemIds = new List<string>();
        NotificationTargets = new List<NotificationTarget>();
    }

    /// <summary>
    /// Gets or sets minutes after local midnight at which the morning window ends
    /// and the afternoon window begins. Default 12:00 (720).
    /// </summary>
    public int MiddayStartMinutes { get; set; }

    /// <summary>
    /// Gets or sets minutes after local midnight at which the afternoon window ends
    /// and the evening window begins. Default 18:00 (1080).
    /// </summary>
    public int EveningStartMinutes { get; set; }

    /// <summary>
    /// Gets or sets the shared secret used by the REST bonus/status endpoints.
    /// </summary>
    public string BonusApiToken { get; set; }

    /// <summary>
    /// Gets or sets the header of the on-screen warning sent shortly before the limit.
    /// </summary>
    public string WarnMessageHeader { get; set; }

    /// <summary>
    /// Gets or sets the body of the on-screen warning. <c>{minutes}</c> is replaced with
    /// the kid's configured warning lead time and <c>{name}</c> with their user name.
    /// </summary>
    public string WarnMessageText { get; set; }

    /// <summary>
    /// Gets or sets the header shown on the TV at the moment the limit is reached.
    /// </summary>
    public string LimitMessageHeader { get; set; }

    /// <summary>
    /// Gets or sets the body shown on the TV at the moment the limit is reached.
    /// Supports <c>{name}</c>.
    /// </summary>
    public string LimitMessageText { get; set; }

    /// <summary>
    /// Gets or sets the header shown when a kid who is already out of time presses play.
    /// </summary>
    public string BlockedMessageHeader { get; set; }

    /// <summary>
    /// Gets or sets the body shown when a kid who is already out of time presses play.
    /// Supports <c>{name}</c>.
    /// </summary>
    public string BlockedMessageText { get; set; }

    /// <summary>
    /// Gets or sets the header shown when a parent presses "Stop now".
    /// </summary>
    public string ParentStopMessageHeader { get; set; }

    /// <summary>
    /// Gets or sets the body shown when a parent presses "Stop now". Supports <c>{name}</c>.
    /// </summary>
    public string ParentStopMessageText { get; set; }

    /// <summary>
    /// Gets or sets how many seconds an on-screen message stays up. Default 8.
    /// </summary>
    public int MessageSeconds { get; set; }

    /// <summary>
    /// Gets or sets how many seconds to leave between showing the "time's up" message and
    /// actually stopping playback, so the message is read before the screen goes away.
    /// <para>
    /// This matters because the client only renders a DisplayMessage <b>while something is
    /// playing</b> — a message sent after the Stop lands on a dead player and is never
    /// seen. The grace is the whole reason the kid gets told why the TV stopped instead of
    /// it just vanishing. 0 disables it and stops immediately. Default 6.
    /// </para>
    /// </summary>
    public int StopGraceSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the parent should be notified (via the
    /// configured notification targets) when a kid keeps actively playing for more than
    /// <see cref="OverLimitAlertMinutes"/> after going over limit — i.e. the Stop command
    /// is being ignored. Since the plugin has no server-side fallback, this alert is the
    /// only thing that tells a parent enforcement has stopped working. One alert per
    /// sitting. Default on.
    /// </summary>
    public bool OverLimitAlertEnabled { get; set; }

    /// <summary>
    /// Gets or sets how many minutes of continued playback past the limit trigger the
    /// over-limit alert. Default 3.
    /// </summary>
    public int OverLimitAlertMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether one-time first-run seeding has happened.
    /// Configs saved before this flag existed deserialize it as <c>false</c>; the plugin
    /// migration then seeds the built-in presets (only if none are present) and flips this
    /// to <c>true</c> so they are never re-seeded again.
    /// </summary>
    public bool Initialized { get; set; }

    /// <summary>
    /// Gets or sets the reusable named limit presets.
    /// </summary>
    public List<Preset> Presets { get; set; }

    /// <summary>
    /// Gets or sets the per-user limit configuration. Users not present here (or with
    /// <see cref="UserLimitConfig.Enabled"/> = false) are treated as unlimited adults.
    /// </summary>
    public List<UserLimitConfig> Users { get; set; }

    /// <summary>
    /// Gets or sets how many minutes one reward coin is worth. Coins are the kid-facing
    /// unit of the rewards system (REWARDS.md). Default 5.
    /// </summary>
    public int CoinMinutes { get; set; }

    /// <summary>
    /// Gets or sets the maximum coins a wallet can hold; earning past it is clamped.
    /// Coins never expire, so this is the only anti-hoarding tool. 0 = unlimited.
    /// </summary>
    public int BankCapCoins { get; set; }

    /// <summary>
    /// Gets or sets the maximum coins that can be redeemed per local day, so a large
    /// bank cannot be spent in one sitting. 0 = unlimited.
    /// </summary>
    public int MaxRedeemCoinsPerDay { get; set; }

    /// <summary>
    /// Gets or sets how many coins the kid page's spend clock starts on. The child then
    /// only presses ▲/▼ when she wants something other than the usual amount, so the
    /// common case is a single "yes". Default 3 (= 15 minutes at the default
    /// <see cref="CoinMinutes"/>). Clamped at spend time to what she can actually afford
    /// today, so a value above <see cref="MaxRedeemCoinsPerDay"/> is harmless.
    /// </summary>
    public int DefaultSpendCoins { get; set; }

    /// <summary>
    /// Gets or sets the server's public base URL ("https://jellyfin.example.com" or
    /// "http://192.168.1.10:8096"), used to build the one-tap approve/decline links in
    /// chore-claim notifications and the copyable kid/parent page URLs. When empty, the
    /// URL of the request that triggered the notification is used, which works as long
    /// as the phone can reach the same address the kid's device used.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the push-notification destinations for rewards events (chore claims).
    /// Supports ntfy, Pushover, Gotify, Discord/Slack webhooks, Telegram, an Apprise API
    /// server, and generic JSON webhooks — see <see cref="NotificationTarget"/>.
    /// </summary>
    public List<NotificationTarget> NotificationTargets { get; set; }

    /// <summary>Gets or sets the parent-defined chores the kid can earn coins for.</summary>
    public List<Chore> Chores { get; set; }

    /// <summary>
    /// Gets or sets library item ids (movies/series) used as "reference titles" on the
    /// kid page: the wallet is visualised as posters ("Tom &amp; Jerry ×3") and redeeming
    /// is picking a poster.
    /// </summary>
    public List<string> ReferenceItemIds { get; set; }

    /// <summary>
    /// Builds the shipped built-in presets. Used for first-run seeding and for the
    /// "Restore built-in presets" action on the config page.
    /// </summary>
    /// <returns>A fresh list of the built-in presets.</returns>
    public static List<Preset> DefaultPresets() => new()
    {
        new Preset
        {
            Id = "school-day",
            Name = "School Day",
            DailyCapMinutes = 60,
            SessionCapMinutes = 45,
            MorningCapMinutes = 0,
            AfternoonCapMinutes = 30,
            EveningCapMinutes = 30,
        },
        new Preset
        {
            Id = "weekend",
            Name = "Weekend",
            DailyCapMinutes = 120,
            SessionCapMinutes = 60,
            MorningCapMinutes = 60,
            AfternoonCapMinutes = null,
            EveningCapMinutes = 60,
        },
        new Preset
        {
            Id = "holiday",
            Name = "Holiday",
            DailyCapMinutes = 150,
            SessionCapMinutes = 90,
            MorningCapMinutes = null,
            AfternoonCapMinutes = null,
            EveningCapMinutes = 60,
        },
        new Preset
        {
            Id = "recovery-day",
            Name = "Recovery Day",
            DailyCapMinutes = null,
            SessionCapMinutes = null,
            MorningCapMinutes = null,
            AfternoonCapMinutes = null,
            EveningCapMinutes = null,
        },
    };

    /// <summary>
    /// Builds the shipped chore set for a 6-year-old. Used for first-run seeding and for the
    /// "Add suggested chores" action on the config page (which mirrors this list client-side).
    /// <para>
    /// Coin values are calibrated against the defaults — a coin is 5 minutes and
    /// <see cref="MaxRedeemCoinsPerDay"/> is 6, so a full day of these chores earns more than
    /// can be spent in one day: the child chooses, rather than clearing a checklist. Each
    /// <see cref="Chore.Clipart"/> key is catalogued in <see cref="ChoreClipart"/>; keys whose
    /// art is not embedded yet fall back to the emoji until the picture lands.
    /// </para>
    /// </summary>
    /// <returns>A fresh list of the suggested chores.</returns>
    public static List<Chore> DefaultChores() => new()
    {
        new Chore { Id = "make-bed", Name = "Make your bed", Icon = "🛏️", Clipart = "make-bed", Coins = 1, MaxPerDay = 1 },
        new Chore { Id = "clothes-basket", Name = "Dirty clothes in the basket", Icon = "🧺", Clipart = "clothes-basket", Coins = 1, MaxPerDay = 1 },
        new Chore { Id = "plate-in-sink", Name = "Put your plate in the sink", Icon = "🍽️", Clipart = "plate-in-sink", Coins = 1, MaxPerDay = 3 },
        new Chore { Id = "tidy-toys", Name = "Tidy your room", Icon = "🧸", Clipart = "tidy-toys", Coins = 2, MaxPerDay = 1 },
        new Chore { Id = "unload-dishwasher", Name = "Unload the dishwasher", Icon = "🍴", Clipart = "unload-dishwasher", Coins = 2, MaxPerDay = 1 },
        new Chore { Id = "tidy-craft-table", Name = "Tidy the craft table", Icon = "✏️", Clipart = "tidy-craft-table", Coins = 2, MaxPerDay = 1 },
        new Chore { Id = "put-away-clothes", Name = "Put away your clean clothes", Icon = "👕", Clipart = "put-away-clothes", Coins = 2, MaxPerDay = 1 },
        new Chore { Id = "play-brother", Name = "Play with little brother", Icon = "👶", Clipart = "play-brother", Coins = 3, MaxPerDay = 1 },
        new Chore { Id = "read-to-brother", Name = "Read a picture book to your brother", Icon = "📖", Clipart = "read-to-brother", Coins = 2, MaxPerDay = 1 },
    };
}
