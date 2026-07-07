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
/// Hard-enforces limits server-side for clients that ignore the Stop playstate command
/// (the load-bearing failure on Android TV, REQUIREMENTS.md §2.1). Two modes:
///
/// <list type="bullet">
/// <item><description><b>DisablePlayback</b> — flips the user's <c>EnableMediaPlayback</c>
/// policy off. Jellyfin's PlaybackInfo endpoint then refuses to hand out media sources,
/// so nothing new can start (including auto-play of the next episode), but the kid can
/// still browse the library and sees a normal "playback not allowed" error instead of
/// being locked out entirely. An in-flight direct-play stream on a client that ignores
/// Stop may run to the end of the current item.</description></item>
/// <item><description><b>AccessSchedule</b> — drives the user's native access schedule to
/// "never allowed" (Everyday 00:00–00:00). Jellyfin's <c>DefaultAuthorizationHandler</c>
/// validates the schedule on every request, so even an in-flight stream dies at its next
/// range/segment request. Most forceful, but blocks all Jellyfin access for the user
/// while over limit.</description></item>
/// </list>
///
/// Whatever we change (schedules and/or the playback permission) is saved to disk before
/// we touch it and restored verbatim when the block is released. Automatic over-limit
/// blocking is opt-in (<see cref="PluginConfiguration.EnforceViaAccessSchedule"/>); a
/// parent "Stop now" always blocks, using the configured mode.
/// </summary>
public sealed class HardBlockEnforcer
{
    private const double MarkerHour = 0d;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IUserManager _userManager;
    private readonly StateStore _store;
    private readonly ILogger<HardBlockEnforcer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="HardBlockEnforcer"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="store">State store.</param>
    /// <param name="logger">Logger.</param>
    public HardBlockEnforcer(
        IUserManager userManager,
        StateStore store,
        ILogger<HardBlockEnforcer> logger)
    {
        _userManager = userManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles every user's server-side block with what the limits require. Users who
    /// are over their persistent (daily/window) limit and whose feature is enabled get
    /// blocked; everyone else who carries one of our blocks gets released. Safe to call
    /// frequently — it only writes when a user's state actually needs to change.
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
            var mode = config.ResolvedHardEnforcementMode;

            // Which users should be blocked right now. A parent "Stop now" always
            // hard-blocks (it's a deliberate manual action); automatic over-limit blocking
            // is layered on only when the opt-in feature is enabled.
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

                var hasBlock = IsBlockedByUs(user);
                var shouldBlock = desired.TryGetValue(user.Id, out var b) && b;

                if (shouldBlock && !hasBlock)
                {
                    await BlockAsync(user, mode).ConfigureAwait(false);
                }
                else if (!shouldBlock && hasBlock)
                {
                    await UnblockAsync(user).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: hard-block reconciliation failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases every user that currently carries one of our blocks. Used on shutdown /
    /// when the feature is turned off so no kid is left locked out of Jellyfin.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task ReleaseAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var user in _userManager.GetUsers())
            {
                if (user is not null && IsBlockedByUs(user))
                {
                    await UnblockAsync(user).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: releasing hard blocks failed.");
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

    private bool IsBlockedByUs(Jellyfin.Database.Implementations.Entities.User user)
    {
        // The saved-originals file is written before we apply a block and deleted after we
        // release it, so its existence is the authoritative "we own a block" marker for
        // both modes. The schedule-marker check keeps blocks recognizable even if the
        // plugin's data folder was wiped mid-block (pre-existing behavior).
        var path = OriginalsPath(user.Id);
        if (path is not null && File.Exists(path))
        {
            return true;
        }

        var schedules = user.AccessSchedules;
        return schedules is not null && schedules.Any(IsMarker);
    }

    private async Task BlockAsync(Jellyfin.Database.Implementations.Entities.User user, string mode)
    {
        var policy = _userManager.GetUserDto(user).Policy;
        if (policy is null)
        {
            return;
        }

        var existing = policy.AccessSchedules ?? Array.Empty<AccessSchedule>();

        // Preserve whatever we're about to change so we can put it back on release.
        SaveOriginals(user.Id, new SavedBlock
        {
            Mode = mode,
            EnableMediaPlayback = policy.EnableMediaPlayback,
            Schedules = existing
                .Where(s => !IsMarker(s))
                .Select(s => new SavedSchedule { Day = (int)s.DayOfWeek, Start = s.StartHour, End = s.EndHour })
                .ToList(),
        });

        if (string.Equals(mode, PluginConfiguration.ModeDisablePlayback, StringComparison.OrdinalIgnoreCase))
        {
            policy.EnableMediaPlayback = false;
        }
        else
        {
            policy.AccessSchedules = new[]
            {
                new AccessSchedule(DynamicDayOfWeek.Everyday, MarkerHour, MarkerHour, user.Id),
            };
        }

        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        _logger.LogInformation(
            "KidsLimit: hard-blocked user {User} ({Mode} mode).",
            user.Username,
            mode);
    }

    private async Task UnblockAsync(Jellyfin.Database.Implementations.Entities.User user)
    {
        var policy = _userManager.GetUserDto(user).Policy;
        if (policy is null)
        {
            return;
        }

        // Restore everything a block could have touched, regardless of which mode applied
        // it (the mode may have been reconfigured while a block was active).
        var saved = LoadOriginals(user.Id);
        policy.AccessSchedules = saved.Schedules
            .Select(d => new AccessSchedule((DynamicDayOfWeek)d.Day, d.Start, d.End, user.Id))
            .ToArray();

        // Null = legacy state file that never recorded the playback permission (written
        // by a version that only knew access-schedule blocks) — leave it untouched.
        if (saved.EnableMediaPlayback is bool playback)
        {
            policy.EnableMediaPlayback = playback;
        }

        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        DeleteOriginals(user.Id);
        _logger.LogInformation("KidsLimit: released hard block for user {User}.", user.Username);
    }

    private void SaveOriginals(Guid userId, SavedBlock block)
    {
        try
        {
            var path = OriginalsPath(userId);
            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(block, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed saving original policy for {UserId}.", userId);
        }
    }

    private SavedBlock LoadOriginals(Guid userId)
    {
        try
        {
            var path = OriginalsPath(userId);
            if (path is null || !File.Exists(path))
            {
                return new SavedBlock();
            }

            var json = File.ReadAllText(path);

            // Files written before the DisablePlayback mode existed are a bare JSON array
            // of schedules; treat them as an AccessSchedule-mode block.
            if (json.TrimStart().StartsWith('['))
            {
                return new SavedBlock
                {
                    Mode = PluginConfiguration.ModeAccessSchedule,
                    Schedules = JsonSerializer.Deserialize<List<SavedSchedule>>(json) ?? new List<SavedSchedule>(),
                };
            }

            return JsonSerializer.Deserialize<SavedBlock>(json) ?? new SavedBlock();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed loading original policy for {UserId}.", userId);
            return new SavedBlock();
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
            _logger.LogWarning(ex, "KidsLimit: failed clearing original policy for {UserId}.", userId);
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

    private sealed class SavedBlock
    {
        public string Mode { get; set; } = PluginConfiguration.ModeAccessSchedule;

        public bool? EnableMediaPlayback { get; set; }

        public List<SavedSchedule> Schedules { get; set; } = new();
    }

    private sealed class SavedSchedule
    {
        public int Day { get; set; }

        public double Start { get; set; }

        public double End { get; set; }
    }
}
