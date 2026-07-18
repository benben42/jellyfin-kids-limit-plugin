using System.Collections.Generic;
using Jellyfin.Plugin.KidsLimit.Configuration;

namespace Jellyfin.Plugin.KidsLimit.Api.Models;

/// <summary>
/// The full editable plugin configuration as exchanged with the standalone parent page
/// (<c>GET/POST /KidsLimit/settings</c>). Mirrors <see cref="PluginConfiguration"/> but is
/// bound from JSON, so it can reuse the config collection types directly.
/// </summary>
public class SettingsDto
{
    /// <summary>Gets or sets minutes after local midnight at which the afternoon window begins.</summary>
    public int MiddayStartMinutes { get; set; }

    /// <summary>Gets or sets minutes after local midnight at which the evening window begins.</summary>
    public int EveningStartMinutes { get; set; }

    /// <summary>
    /// Gets or sets the parent shared secret. On save, an empty value is ignored (the
    /// existing token is kept) so a parent cannot accidentally lock themselves out of the
    /// very page they are saving from.
    /// </summary>
    public string BonusApiToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the server's public base URL used in notification links.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether over-limit kids are hard-blocked server-side.</summary>
    public bool EnforceViaAccessSchedule { get; set; }

    /// <summary>Gets or sets the hard-block mode (AccessSchedule / DisablePlayback).</summary>
    public string HardEnforcementMode { get; set; } = PluginConfiguration.ModeAccessSchedule;

    /// <summary>Gets or sets a value indicating whether the "auto-stop failed" parent alert is on.</summary>
    public bool OverLimitAlertEnabled { get; set; }

    /// <summary>Gets or sets the over-limit grace minutes before the alert fires.</summary>
    public int OverLimitAlertMinutes { get; set; }

    /// <summary>Gets or sets how many minutes one reward coin is worth.</summary>
    public int CoinMinutes { get; set; }

    /// <summary>Gets or sets the wallet bank cap in coins (0 = unlimited).</summary>
    public int BankCapCoins { get; set; }

    /// <summary>Gets or sets the max coins redeemable per local day (0 = unlimited).</summary>
    public int MaxRedeemCoinsPerDay { get; set; }

    /// <summary>Gets or sets the reusable limit presets.</summary>
    public List<Preset> Presets { get; set; } = new();

    /// <summary>Gets or sets the per-user limit configuration.</summary>
    public List<UserLimitConfig> Users { get; set; } = new();

    /// <summary>Gets or sets the chores the kids can earn coins for.</summary>
    public List<Chore> Chores { get; set; } = new();

    /// <summary>Gets or sets the push-notification destinations.</summary>
    public List<NotificationTarget> NotificationTargets { get; set; } = new();

    /// <summary>Gets or sets the reference title item ids for the kid page watch tiles.</summary>
    public List<string> ReferenceItemIds { get; set; } = new();
}

/// <summary>
/// What <c>GET /KidsLimit/settings</c> returns: the editable settings plus the read-only
/// list of all Jellyfin users, so the parent page can enable limits for a kid that has no
/// per-user config entry yet. <see cref="AllUsers"/> is ignored on POST.
/// </summary>
public class SettingsResponseDto : SettingsDto
{
    /// <summary>Gets or sets every Jellyfin server user (id + name), for the kids editor.</summary>
    public List<JellyfinUserDto> AllUsers { get; set; } = new();
}

/// <summary>A Jellyfin server user as listed for the settings kids editor.</summary>
public class JellyfinUserDto
{
    /// <summary>Gets or sets the user id (Guid "N").</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name.</summary>
    public string Name { get; set; } = string.Empty;
}
