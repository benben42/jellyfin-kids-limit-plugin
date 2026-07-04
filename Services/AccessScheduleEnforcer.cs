using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Jellyfin.Plugin.KidsLimit.State;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Services;

/// <summary>
/// Hard-enforces limits by driving each kid's native Jellyfin access schedule. Jellyfin's
/// <c>DefaultAuthorizationHandler</c> validates the parental/access schedule on every
/// request that carries the schedule requirement, so blocking a user server-side stops
/// their playback (and all other access) even on clients that ignore the Stop playstate
/// command — the load-bearing failure on Android TV (REQUIREMENTS.md §2.1).
///
/// A blocked user gets a single recognizable "marker" schedule (Everyday 00:00–00:00 =
/// never allowed). Any pre-existing (parent-set) schedules are saved to disk before we
/// overwrite them and restored verbatim when we release the block. This whole feature is
/// opt-in (<see cref="PluginConfiguration.EnforceViaAccessSchedule"/>, default off).
/// </summary>
public sealed class AccessScheduleEnforcer
{
    private const double MarkerHour = 0d;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IUserManager _userManager;
    private readonly StateStore _store;
    private readonly ILogger<AccessScheduleEnforcer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessScheduleEnforcer"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="store">State store.</param>
    /// <param name="logger">Logger.</param>
    public AccessScheduleEnforcer(
        IUserManager userManager,
        StateStore store,
        ILogger<AccessScheduleEnforcer> logger)
    {
        _userManager = userManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles every user's access schedule with what the limits require. Users who are
    /// over their persistent (daily/window) limit and whose feature is enabled get blocked;
    /// everyone else who carries our marker gets released. Safe to call frequently — it only
    /// writes when a user's state actually needs to change.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="today">Today's local date key (yyyy-MM-dd).</param>
    /// <param name="local">Server-local now.</param>
    /// <returns>A task.</returns>
    public async Task ReconcileAsync(PluginConfiguration config, string today, DateTime local)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var window = LimitCalculator.WindowFor(local, config);

            // Which users should be blocked right now. A parent "Stop now" always hard-blocks
            // (it's a deliberate manual action and the only thing that reliably stops clients
            // like Android TV); automatic over-limit blocking is layered on only when the
            // opt-in feature is enabled.
            var desired = new Dictionary<Guid, bool>();
            foreach (var cfg in config.Users)
            {
                if (!cfg.Enabled || !Guid.TryParse(cfg.UserId, out var guid))
                {
                    continue;
                }

                var state = _store.GetOrCreate(guid.ToString("N", CultureInfo.InvariantCulture), today);

                if (state.ManuallyStopped)
                {
                    desired[guid] = true;
                }
                else if (config.EnforceViaAccessSchedule)
                {
                    var preset = LimitCalculator.ResolvePreset(cfg, config, local);
                    desired[guid] = IsOverPersistentLimit(preset, state, window);
                }
            }

            foreach (var user in _userManager.GetUsers())
            {
                if (user is null)
                {
                    continue;
                }

                var hasMarker = HasMarker(user);
                var shouldBlock = desired.TryGetValue(user.Id, out var b) && b;

                if (shouldBlock && !hasMarker)
                {
                    await BlockAsync(user).ConfigureAwait(false);
                }
                else if (!shouldBlock && hasMarker)
                {
                    await UnblockAsync(user).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: access-schedule reconciliation failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases every user that currently carries our marker. Used on shutdown / when the
    /// feature is turned off so no kid is left locked out of Jellyfin.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task ReleaseAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var user in _userManager.GetUsers())
            {
                if (user is not null && HasMarker(user))
                {
                    await UnblockAsync(user).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: releasing access-schedule blocks failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsMarker(AccessSchedule s) =>
        s.DayOfWeek == DynamicDayOfWeek.Everyday && s.StartHour == MarkerHour && s.EndHour == MarkerHour;

    private static bool IsOverPersistentLimit(Preset? preset, DailyState state, Window window)
    {
        if (preset is null)
        {
            return false;
        }

        // Daily bonus applies to both the daily and the active-window budgets (§5.1). The
        // session cap is deliberately excluded: a session-cap breach is a "take a break"
        // signal, not a "you're done for the day", so it shouldn't lock the whole account.
        if (preset.DailyCapMinutes is int daily &&
            state.SecondsWatchedTotal >= (long)daily * 60 + state.DailyBonusSeconds)
        {
            return true;
        }

        var windowCap = LimitCalculator.WindowCap(preset, window);
        if (windowCap is int wc &&
            state.GetWindowSeconds(window) >= (long)wc * 60 + state.DailyBonusSeconds)
        {
            return true;
        }

        return false;
    }

    private bool HasMarker(Jellyfin.Database.Implementations.Entities.User user)
    {
        var schedules = user.AccessSchedules;
        return schedules is not null && schedules.Any(IsMarker);
    }

    private async Task BlockAsync(Jellyfin.Database.Implementations.Entities.User user)
    {
        var policy = _userManager.GetUserDto(user).Policy;
        if (policy is null)
        {
            return;
        }

        var existing = policy.AccessSchedules ?? Array.Empty<AccessSchedule>();

        // Preserve any parent-set schedules so we can put them back on release.
        SaveOriginals(user.Id, existing.Where(s => !IsMarker(s)));

        policy.AccessSchedules = new[]
        {
            new AccessSchedule(DynamicDayOfWeek.Everyday, MarkerHour, MarkerHour, user.Id),
        };

        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        _logger.LogInformation(
            "KidsLimit: hard-blocked user {User} via access schedule (over daily/window limit).",
            user.Username);
    }

    private async Task UnblockAsync(Jellyfin.Database.Implementations.Entities.User user)
    {
        var policy = _userManager.GetUserDto(user).Policy;
        if (policy is null)
        {
            return;
        }

        policy.AccessSchedules = LoadOriginals(user.Id);
        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        DeleteOriginals(user.Id);
        _logger.LogInformation("KidsLimit: released access-schedule block for user {User}.", user.Username);
    }

    private void SaveOriginals(Guid userId, IEnumerable<AccessSchedule> schedules)
    {
        try
        {
            var dtos = schedules
                .Select(s => new SavedSchedule { Day = (int)s.DayOfWeek, Start = s.StartHour, End = s.EndHour })
                .ToList();
            var path = OriginalsPath(userId);
            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(dtos, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed saving original access schedules for {UserId}.", userId);
        }
    }

    private AccessSchedule[] LoadOriginals(Guid userId)
    {
        try
        {
            var path = OriginalsPath(userId);
            if (path is null || !File.Exists(path))
            {
                return Array.Empty<AccessSchedule>();
            }

            var dtos = JsonSerializer.Deserialize<List<SavedSchedule>>(File.ReadAllText(path))
                ?? new List<SavedSchedule>();
            return dtos
                .Select(d => new AccessSchedule((DynamicDayOfWeek)d.Day, d.Start, d.End, userId))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed loading original access schedules for {UserId}.", userId);
            return Array.Empty<AccessSchedule>();
        }
    }

    private void DeleteOriginals(Guid userId)
    {
        try
        {
            var path = OriginalsPath(userId);
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed clearing original access schedules for {UserId}.", userId);
        }
    }

    private string? OriginalsPath(Guid userId)
    {
        var dataDir = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataDir))
        {
            return null;
        }

        var name = userId.ToString("N", CultureInfo.InvariantCulture);
        return Path.Combine(dataDir, "enforcement", name + ".json");
    }

    private sealed class SavedSchedule
    {
        public int Day { get; set; }

        public double Start { get; set; }

        public double End { get; set; }
    }
}
