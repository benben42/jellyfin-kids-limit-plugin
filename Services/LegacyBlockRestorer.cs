using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Services;

/// <summary>
/// One-shot upgrade cleanup for installs coming from a version that enforced limits by
/// mutating Jellyfin user policies.
///
/// <para>
/// Up to 3.0 the plugin had a <c>HardBlockEnforcer</c> that, for a kid who was over their
/// limit, either turned off <c>EnableMediaPlayback</c> or drove the user's access schedule
/// to "Everyday 00:00–00:00" (never allowed). It saved the original policy to
/// <c>&lt;data&gt;/enforcement/&lt;userId&gt;.json</c> first and restored it when the block
/// was released. That whole mechanism is gone: testing against the Jellyfin 12 Android TV
/// client showed it honors the Stop command, so enforcement is now purely "stop the stream
/// and keep stopping it", and no account is ever touched.
/// </para>
///
/// <para>
/// The danger in simply deleting the enforcer is an install that is upgraded <b>while a
/// block is active</b>: the code that would have restored the policy no longer exists, so
/// the kid is left permanently unable to play — or, in access-schedule mode, unable to log
/// in at all — with no UI anywhere that explains why. So this runs once at startup, puts
/// back everything the old enforcer could have changed, and removes the state folder. It
/// is a no-op on a clean install and on every subsequent start.
/// </para>
/// </summary>
public sealed class LegacyBlockRestorer
{
    private const double MarkerHour = 0d;

    private readonly IUserManager _userManager;
    private readonly ILogger<LegacyBlockRestorer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LegacyBlockRestorer"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="logger">Logger.</param>
    public LegacyBlockRestorer(IUserManager userManager, ILogger<LegacyBlockRestorer> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Restores every policy the retired hard-block enforcer may still be holding, then
    /// deletes its state folder. Swallows and logs everything — a failure here must not
    /// stop the plugin from starting.
    /// </summary>
    /// <param name="dataFolderPath">The plugin's data folder.</param>
    /// <returns>A task.</returns>
    public async Task RestoreAllAsync(string dataFolderPath)
    {
        try
        {
            var dir = Path.Combine(dataFolderPath, "enforcement");
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.json")
                : Array.Empty<string>();

            foreach (var file in files)
            {
                await RestoreFromFileAsync(file).ConfigureAwait(false);
            }

            // Belt and braces: a data folder that was wiped mid-block leaves no file to
            // restore from, but the marker schedule is still recognizable on the account.
            // Clearing it is right — the plugin no longer writes schedules at all, so any
            // "never allowed" marker we find is ours and is stale by definition.
            foreach (var user in _userManager.GetUsers())
            {
                if (user is not null && HasMarkerSchedule(user))
                {
                    await ClearMarkerAsync(user).ConfigureAwait(false);
                }
            }

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                _logger.LogInformation(
                    "KidsLimit: removed the retired hard-block state folder ({Count} file(s) restored).",
                    files.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: restoring legacy hard blocks failed.");
        }
    }

    private static bool IsMarker(AccessSchedule s) =>
        s.DayOfWeek == DynamicDayOfWeek.Everyday && s.StartHour == MarkerHour && s.EndHour == MarkerHour;

    private static bool HasMarkerSchedule(User user)
    {
        var schedules = user.AccessSchedules;
        return schedules is not null && schedules.Any(IsMarker);
    }

    private async Task RestoreFromFileAsync(string path)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParse(name, out var userId))
            {
                return;
            }

            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                return;
            }

            var policy = _userManager.GetUserDto(user).Policy;
            if (policy is null)
            {
                return;
            }

            var saved = Load(path);

            policy.AccessSchedules = saved.Schedules
                .Select(d => new AccessSchedule((DynamicDayOfWeek)d.Day, d.Start, d.End, user.Id))
                .ToArray();

            // Null = a state file written before the enforcer learned the DisablePlayback
            // mode, which never recorded the playback permission. Leave it alone.
            if (saved.EnableMediaPlayback is bool playback)
            {
                policy.EnableMediaPlayback = playback;
            }

            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            _logger.LogInformation(
                "KidsLimit: restored {User}'s original policy left behind by the retired hard block.",
                user.Username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed restoring legacy block state from {Path}.", path);
        }
    }

    private async Task ClearMarkerAsync(User user)
    {
        try
        {
            var policy = _userManager.GetUserDto(user).Policy;
            if (policy is null)
            {
                return;
            }

            policy.AccessSchedules = (policy.AccessSchedules ?? Array.Empty<AccessSchedule>())
                .Where(s => !IsMarker(s))
                .ToArray();

            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            _logger.LogInformation(
                "KidsLimit: cleared a stale \"never allowed\" access schedule from {User}.",
                user.Username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed clearing a stale marker schedule for {User}.", user.Username);
        }
    }

    private SavedBlock Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);

            // The oldest files are a bare JSON array of schedules.
            if (json.TrimStart().StartsWith('['))
            {
                return new SavedBlock
                {
                    Schedules = JsonSerializer.Deserialize<List<SavedSchedule>>(json) ?? new List<SavedSchedule>(),
                };
            }

            return JsonSerializer.Deserialize<SavedBlock>(json) ?? new SavedBlock();
        }
        catch (Exception ex)
        {
            // An unreadable file must still clear the block: falling through with an empty
            // schedule list restores "no restrictions", which is the safe direction to err
            // in — the alternative leaves a child locked out of the server.
            _logger.LogWarning(ex, "KidsLimit: unreadable legacy block file {Path}; clearing restrictions.", path);
            return new SavedBlock();
        }
    }

    private sealed class SavedBlock
    {
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
