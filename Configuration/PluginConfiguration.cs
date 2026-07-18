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
    /// <summary>Hard-block mode: drive the user's native access schedule (blocks all access).</summary>
    public const string ModeAccessSchedule = "AccessSchedule";

    /// <summary>Hard-block mode: disable the user's media-playback permission (browsing still works).</summary>
    public const string ModeDisablePlayback = "DisablePlayback";

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class
    /// with the shipped defaults described in the requirements.
    /// </summary>
    public PluginConfiguration()
    {
        MiddayStartMinutes = 12 * 60; // 12:00
        EveningStartMinutes = 18 * 60; // 18:00
        BonusApiToken = string.Empty;
        EnforceViaAccessSchedule = false;
        HardEnforcementMode = ModeAccessSchedule;

        CoinMinutes = 5;
        BankCapCoins = 24;
        MaxRedeemCoinsPerDay = 6;

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
    /// Gets or sets a value indicating whether the plugin should hard-enforce limits by
    /// driving each kid's native Jellyfin access schedule. When on, a user who is over
    /// their daily/window limit is blocked at the server (all access), which stops
    /// playback even on clients that ignore the Stop command (e.g. Android TV). The
    /// schedule is restored automatically when they are back under limit, granted bonus,
    /// or at local midnight. Off by default. Note: this blocks all Jellyfin access for
    /// that user while over limit, not just playback.
    /// </summary>
    public bool EnforceViaAccessSchedule { get; set; }

    /// <summary>
    /// Gets or sets how hard blocks are applied (both for automatic over-limit blocking
    /// and the parent "Stop now" hold). <see cref="ModeAccessSchedule"/> (default) blocks
    /// all Jellyfin access for the user via their native access schedule — the most
    /// forceful option, and the only one that also interrupts an in-flight direct-play
    /// stream on clients that ignore the Stop command. <see cref="ModeDisablePlayback"/>
    /// only turns off the user's media-playback permission: nothing new can start
    /// (including auto-play of the next episode) but the kid can still browse, which is
    /// gentler; a stubborn client may finish the item it is currently direct-playing.
    /// </summary>
    public string HardEnforcementMode { get; set; }

    /// <summary>
    /// Gets the effective hard-enforcement mode, mapping unknown/legacy values (configs
    /// saved before this setting existed deserialize it as null) to the default.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    public string ResolvedHardEnforcementMode =>
        string.Equals(HardEnforcementMode, ModeDisablePlayback, StringComparison.OrdinalIgnoreCase)
            ? ModeDisablePlayback
            : ModeAccessSchedule;

    /// <summary>
    /// Gets or sets a value indicating whether the parent should be notified (via the
    /// configured notification targets) when a kid keeps actively playing for more than
    /// <see cref="OverLimitAlertMinutes"/> after going over limit — i.e. the automatic
    /// stop appears to have failed (e.g. a client that ignores the Stop command with hard
    /// enforcement off). One alert per sitting. Default on.
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
}
