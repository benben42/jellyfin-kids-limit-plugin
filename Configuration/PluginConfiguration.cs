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
        Presets = DefaultPresets();
        Users = new List<UserLimitConfig>();
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
    /// Gets or sets the reusable named limit presets.
    /// </summary>
    public List<Preset> Presets { get; set; }

    /// <summary>
    /// Gets or sets the per-user limit configuration. Users not present here (or with
    /// <see cref="UserLimitConfig.Enabled"/> = false) are treated as unlimited adults.
    /// </summary>
    public List<UserLimitConfig> Users { get; set; }

    private static List<Preset> DefaultPresets() => new()
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
