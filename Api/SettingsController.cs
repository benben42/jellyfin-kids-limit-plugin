using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.KidsLimit.Api.Models;
using Jellyfin.Plugin.KidsLimit.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Api;

/// <summary>
/// Full plugin-settings REST API for the standalone parent page, so every option the
/// Jellyfin admin config page offers can also be managed from a phone with just the
/// parent token. Auth is the same shared secret as <see cref="KidsLimitController"/>.
/// </summary>
[ApiController]
[Route("KidsLimit")]
[AllowAnonymous]
[Produces("application/json")]
public class SettingsController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ILogger<SettingsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsController"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="logger">Logger.</param>
    public SettingsController(IUserManager userManager, ILogger<SettingsController> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    private PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets the full editable configuration plus the list of all Jellyfin users.</summary>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The settings snapshot.</returns>
    [HttpGet("settings")]
    public ActionResult<SettingsResponseDto> GetSettings([FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        var config = Config;
        return new SettingsResponseDto
        {
            MiddayStartMinutes = config.MiddayStartMinutes,
            EveningStartMinutes = config.EveningStartMinutes,
            BonusApiToken = config.BonusApiToken,
            PublicBaseUrl = config.PublicBaseUrl,
            EnforceViaAccessSchedule = config.EnforceViaAccessSchedule,
            HardEnforcementMode = config.ResolvedHardEnforcementMode,
            OverLimitAlertEnabled = config.OverLimitAlertEnabled,
            OverLimitAlertMinutes = config.OverLimitAlertMinutes,
            CoinMinutes = config.CoinMinutes,
            BankCapCoins = config.BankCapCoins,
            MaxRedeemCoinsPerDay = config.MaxRedeemCoinsPerDay,
            Presets = config.Presets,
            Users = config.Users,
            Chores = config.Chores,
            NotificationTargets = config.NotificationTargets,
            ReferenceItemIds = config.ReferenceItemIds,
            AllUsers = _userManager.GetUsers()
                .Select(u => new JellyfinUserDto
                {
                    UserId = u.Id.ToString("N", CultureInfo.InvariantCulture),
                    Name = u.Username,
                })
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    /// <summary>
    /// Replaces the plugin configuration with the posted settings (after sanitising) and
    /// persists it — the same store the Jellyfin admin config page writes to.
    /// </summary>
    /// <param name="dto">The settings to apply.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The saved settings snapshot (as <see cref="GetSettings"/> would return).</returns>
    [HttpPost("settings")]
    public ActionResult<SettingsResponseDto> SaveSettings(
        [FromBody] SettingsDto dto,
        [FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        var plugin = Plugin.Instance;
        if (plugin is null || dto is null)
        {
            return StatusCode(503, "Plugin not initialized.");
        }

        var config = plugin.Configuration;

        config.MiddayStartMinutes = Math.Clamp(dto.MiddayStartMinutes, 0, 1439);
        config.EveningStartMinutes = Math.Clamp(dto.EveningStartMinutes, config.MiddayStartMinutes, 1440);
        config.PublicBaseUrl = (dto.PublicBaseUrl ?? string.Empty).Trim();
        config.EnforceViaAccessSchedule = dto.EnforceViaAccessSchedule;
        config.HardEnforcementMode =
            string.Equals(dto.HardEnforcementMode, PluginConfiguration.ModeDisablePlayback, StringComparison.OrdinalIgnoreCase)
                ? PluginConfiguration.ModeDisablePlayback
                : PluginConfiguration.ModeAccessSchedule;
        config.OverLimitAlertEnabled = dto.OverLimitAlertEnabled;
        config.OverLimitAlertMinutes = Math.Clamp(dto.OverLimitAlertMinutes, 1, 24 * 60);
        config.CoinMinutes = Math.Max(1, dto.CoinMinutes);
        config.BankCapCoins = Math.Max(0, dto.BankCapCoins);
        config.MaxRedeemCoinsPerDay = Math.Max(0, dto.MaxRedeemCoinsPerDay);

        // An empty token would disable the API — and with it the very page this request
        // came from. Keep the existing token instead; changing it requires a new value.
        var newToken = (dto.BonusApiToken ?? string.Empty).Trim();
        if (newToken.Length > 0)
        {
            config.BonusApiToken = newToken;
        }

        config.Presets = SanitizePresets(dto.Presets);
        config.Users = SanitizeUsers(dto.Users);
        config.Chores = SanitizeChores(dto.Chores);
        config.NotificationTargets = SanitizeTargets(dto.NotificationTargets);
        config.ReferenceItemIds = (dto.ReferenceItemIds ?? new List<string>())
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        plugin.SaveConfiguration();
        _logger.LogInformation("KidsLimit: configuration updated via the parent settings page.");

        return GetSettings(token);
    }

    private static List<Preset> SanitizePresets(List<Preset>? presets)
    {
        var result = new List<Preset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in presets ?? new List<Preset>())
        {
            if (preset is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(preset.Id))
            {
                preset.Id = "preset-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
            }

            if (!seen.Add(Normalize(preset.Id)))
            {
                continue; // drop duplicate ids
            }

            preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "Preset" : preset.Name.Trim();
            preset.DailyCapMinutes = NonNegativeOrNull(preset.DailyCapMinutes);
            preset.SessionCapMinutes = NonNegativeOrNull(preset.SessionCapMinutes);
            preset.MorningCapMinutes = NonNegativeOrNull(preset.MorningCapMinutes);
            preset.AfternoonCapMinutes = NonNegativeOrNull(preset.AfternoonCapMinutes);
            preset.EveningCapMinutes = NonNegativeOrNull(preset.EveningCapMinutes);
            result.Add(preset);
        }

        return result;
    }

    private List<UserLimitConfig> SanitizeUsers(List<UserLimitConfig>? users)
    {
        var result = new List<UserLimitConfig>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in users ?? new List<UserLimitConfig>())
        {
            if (user is null || !Guid.TryParse(user.UserId, out var guid) || !seen.Add(Normalize(user.UserId)))
            {
                continue;
            }

            user.WarnMinutesBeforeLimit = Math.Max(0, user.WarnMinutesBeforeLimit);
            user.KidToken = (user.KidToken ?? string.Empty).Trim();
            user.DateOverrides = (user.DateOverrides ?? new List<DateOverride>())
                .Where(o => o is not null &&
                            !string.IsNullOrWhiteSpace(o.Date) &&
                            !string.IsNullOrWhiteSpace(o.PresetId))
                .ToList();

            // Refresh the cached display name (it's display-only; UserId is authoritative).
            var jellyfinUser = _userManager.GetUserById(guid);
            if (jellyfinUser is not null)
            {
                user.UserName = jellyfinUser.Username;
            }

            result.Add(user);
        }

        return result;
    }

    private static List<Chore> SanitizeChores(List<Chore>? chores)
    {
        var result = new List<Chore>();
        foreach (var chore in chores ?? new List<Chore>())
        {
            if (chore is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(chore.Id))
            {
                chore.Id = "chore-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
            }

            chore.Name = string.IsNullOrWhiteSpace(chore.Name) ? "Chore" : chore.Name.Trim();
            chore.Icon = (chore.Icon ?? string.Empty).Trim();
            chore.Clipart = ChoreClipart.Keys.Contains(chore.Clipart ?? string.Empty)
                ? chore.Clipart!
                : string.Empty;
            chore.Coins = Math.Max(1, chore.Coins);
            chore.MaxPerDay = Math.Max(0, chore.MaxPerDay);
            result.Add(chore);
        }

        return result;
    }

    private static List<NotificationTarget> SanitizeTargets(List<NotificationTarget>? targets)
    {
        var result = new List<NotificationTarget>();
        foreach (var target in targets ?? new List<NotificationTarget>())
        {
            if (target is null)
            {
                continue;
            }

            target.Type = (target.Type ?? NotificationTarget.TypeNtfy).Trim();
            target.Url = (target.Url ?? string.Empty).Trim();
            target.Token = (target.Token ?? string.Empty).Trim();
            target.UserKey = (target.UserKey ?? string.Empty).Trim();
            result.Add(target);
        }

        return result;
    }

    private static int? NonNegativeOrNull(int? value) =>
        value is null ? null : Math.Max(0, value.Value);

    private static string Normalize(string id) =>
        (id ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private bool Authorized(string? token)
    {
        var configured = Config.BonusApiToken;
        if (string.IsNullOrEmpty(configured))
        {
            return false; // No token configured => API disabled.
        }

        var provided = token;
        if (string.IsNullOrEmpty(provided) &&
            Request.Headers.TryGetValue("X-KidsLimit-Token", out var header))
        {
            provided = header.ToString();
        }

        return !string.IsNullOrEmpty(provided) &&
               string.Equals(provided, configured, StringComparison.Ordinal);
    }
}
