using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.KidsLimit.Api.Models;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Jellyfin.Plugin.KidsLimit.Services;
using Jellyfin.Plugin.KidsLimit.State;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KidsLimit.Api;

/// <summary>
/// Parent REST API. Auth is a shared secret (plugin config <c>BonusApiToken</c>) supplied
/// via the <c>token</c> query parameter or the <c>X-KidsLimit-Token</c> header — deliberately
/// simple so a phone shortcut / NFC tag / Home Assistant can grant time with one tap. The
/// admin dashboard fetches this same token through its authenticated admin session.
/// </summary>
[ApiController]
[Route("KidsLimit")]
[AllowAnonymous]
[Produces("application/json")]
public class KidsLimitController : ControllerBase
{
    private readonly StateStore _store;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly HardBlockEnforcer _enforcer;
    private readonly PlaybackTerminator _terminator;

    /// <summary>
    /// Initializes a new instance of the <see cref="KidsLimitController"/> class.
    /// </summary>
    /// <param name="store">State store.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="enforcer">Hard-block enforcer.</param>
    /// <param name="terminator">Playback terminator.</param>
    public KidsLimitController(
        StateStore store,
        ISessionManager sessionManager,
        IUserManager userManager,
        HardBlockEnforcer enforcer,
        PlaybackTerminator terminator)
    {
        _store = store;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _enforcer = enforcer;
        _terminator = terminator;
    }

    private PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Grants bonus watch-time to a user (adds to daily + session + active window).</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="minutes">Minutes of bonus to grant.</param>
    /// <param name="token">Shared secret.</param>
    /// <returns>The user's updated status.</returns>
    [HttpPost("bonus")]
    public ActionResult<UserStatusDto> GrantBonus(
        [FromQuery] string user,
        [FromQuery] int minutes,
        [FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        if (minutes == 0)
        {
            return BadRequest("minutes must be non-zero.");
        }

        var resolved = ResolveUser(user);
        if (resolved is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        var localNow = DateTime.Now;
        var today = LimitCalculator.DateKey(localNow);
        _store.GrantBonus(resolved.Value.UserIdN, today, (long)minutes * 60);

        // Immediately reconcile the hard block so bonus time lets a blocked kid back in
        // right away instead of waiting for the next background tick.
        _ = _enforcer.ReconcileAsync(Config, today, localNow);

        return BuildStatus(resolved.Value);
    }

    /// <summary>Gets status for all enabled users.</summary>
    /// <param name="token">Shared secret.</param>
    /// <returns>Per-user status list.</returns>
    [HttpGet("status")]
    public ActionResult<IEnumerable<UserStatusDto>> GetStatus([FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        var result = new List<UserStatusDto>();
        foreach (var cfg in Config.Users.Where(u => u.Enabled))
        {
            var resolved = ResolveUser(cfg.UserId);
            if (resolved is not null)
            {
                result.Add(BuildStatus(resolved.Value));
            }
        }

        return result.OrderBy(r => r.UserName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Gets status for a single user.</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="token">Shared secret.</param>
    /// <returns>The user's status.</returns>
    [HttpGet("status/{user}")]
    public ActionResult<UserStatusDto> GetUserStatus(
        [FromRoute] string user,
        [FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        var resolved = ResolveUser(user);
        if (resolved is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        return BuildStatus(resolved.Value);
    }

    /// <summary>
    /// Immediately stops a user and hard-blocks them for the rest of the day. The block is
    /// applied server-side (using the configured hard-enforcement mode) so playback stops
    /// even on clients that ignore the server's Stop command (e.g. Android TV), and it
    /// persists across a "press play again" until the parent grants bonus, calls
    /// <see cref="Allow"/>, or midnight. Stop/Pause commands are also sent and any
    /// transcode/live stream is torn down for immediate effect.
    /// </summary>
    /// <param name="user">User id or name.</param>
    /// <param name="token">Shared secret.</param>
    /// <returns>Number of sessions the Stop command was sent to.</returns>
    [HttpPost("stop")]
    public async Task<ActionResult<object>> Stop(
        [FromQuery] string user,
        [FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        var resolved = ResolveUser(user);
        if (resolved is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        var localNow = DateTime.Now;
        var today = LimitCalculator.DateKey(localNow);

        // Persist the manual stop, then reconcile the access schedule so the hard block is
        // applied right away (works even where the Stop playstate command is ignored). This
        // runs regardless of the auto-enforcement setting — it's a deliberate parent action.
        _store.SetManualStop(resolved.Value.UserIdN, today, true);
        await _enforcer.ReconcileAsync(Config, today, localNow).ConfigureAwait(false);

        var stopped = await _terminator.StopAllSessionsAsync(resolved.Value.UserGuid).ConfigureAwait(false);
        return new { stopped };
    }

    /// <summary>
    /// Lifts a parent "Stop now" hold, restoring the user's access schedule so they can play
    /// again (subject to their normal budget). Does not grant any extra time.
    /// </summary>
    /// <param name="user">User id or name.</param>
    /// <param name="token">Shared secret.</param>
    /// <returns>The user's updated status.</returns>
    [HttpPost("allow")]
    public async Task<ActionResult<UserStatusDto>> Allow(
        [FromQuery] string user,
        [FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        var resolved = ResolveUser(user);
        if (resolved is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        var localNow = DateTime.Now;
        var today = LimitCalculator.DateKey(localNow);

        _store.SetManualStop(resolved.Value.UserIdN, today, false);
        await _enforcer.ReconcileAsync(Config, today, localNow).ConfigureAwait(false);

        return BuildStatus(resolved.Value);
    }

    /// <summary>Gets finished-day history for a user (for averages/history charts).</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="days">Max number of most-recent days to return.</param>
    /// <param name="token">Shared secret.</param>
    /// <returns>History entries.</returns>
    [HttpGet("history/{user}")]
    public ActionResult<IEnumerable<DailyHistoryEntry>> History(
        [FromRoute] string user,
        [FromQuery] int days = 30,
        [FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        var resolved = ResolveUser(user);
        if (resolved is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        var history = _store.GetHistory(resolved.Value.UserIdN);
        if (days > 0 && history.Count > days)
        {
            history = history.Skip(history.Count - days).ToList();
        }

        return history;
    }

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

    private ResolvedUser? ResolveUser(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        // Use var so we don't hard-code the entity namespace, which moved between
        // Jellyfin releases (10.11 relocated it to Jellyfin.Database.Implementations).
        var user = Guid.TryParse(idOrName, out var g) ? _userManager.GetUserById(g) : null;
        user ??= _userManager.GetUserByName(idOrName);
        if (user is null)
        {
            return null;
        }

        return new ResolvedUser
        {
            UserGuid = user.Id,
            UserName = user.Username,
        };
    }

    private UserStatusDto BuildStatus(ResolvedUser resolved)
    {
        var config = Config;
        var local = DateTime.Now;
        var today = LimitCalculator.DateKey(local);
        var window = LimitCalculator.WindowFor(local, config);

        var cfg = Plugin.Instance?.FindUser(resolved.UserIdN);
        var state = _store.GetOrCreate(resolved.UserIdN, today);

        var dto = new UserStatusDto
        {
            UserId = resolved.UserIdN,
            UserName = resolved.UserName,
            Enabled = cfg?.Enabled ?? false,
            Date = today,
            CurrentWindow = window.ToString(),
            SecondsWatchedTotal = state.SecondsWatchedTotal,
            SecondsMorning = state.SecondsMorning,
            SecondsAfternoon = state.SecondsAfternoon,
            SecondsEvening = state.SecondsEvening,
            BonusSeconds = state.DailyBonusSeconds,
            ManuallyStopped = state.ManuallyStopped,
        };

        // Current session seconds = the busiest active session (usually just one).
        var activeSessionSeconds = state.ActiveSessions.Values
            .Select(s => s.SecondsWatched)
            .DefaultIfEmpty(0)
            .Max();
        dto.CurrentSessionSeconds = activeSessionSeconds;

        var preset = cfg is null ? null : LimitCalculator.ResolvePreset(cfg, config, local);
        if (preset is not null)
        {
            dto.PresetName = preset.Name;
            dto.DailyCapMinutes = preset.DailyCapMinutes;
            dto.SessionCapMinutes = preset.SessionCapMinutes;
            dto.MorningCapMinutes = preset.MorningCapMinutes;
            dto.AfternoonCapMinutes = preset.AfternoonCapMinutes;
            dto.EveningCapMinutes = preset.EveningCapMinutes;

            var remaining = LimitCalculator.Compute(preset, state, window, activeSessionSeconds);
            dto.HasLimit = remaining.HasLimit;
            dto.EffectiveRemainingSeconds = remaining.HasLimit ? remaining.RemainingSeconds : null;
            dto.BindingCap = remaining.BindingCap;

            // Derive "blocked" from the live remaining computation rather than a sticky
            // per-session flag. This way granting bonus time (which adds to the budget)
            // clears the blocked indicator on the very next status poll, instead of the
            // dashboard staying red until the next playback event happens to fire.
            dto.Blocked = remaining.HasLimit && remaining.RemainingSeconds <= 0;
        }
        else
        {
            dto.PresetName = cfg?.Enabled == true ? "(no preset assigned)" : "(unlimited)";
            dto.HasLimit = false;
            dto.Blocked = false;
        }

        // A parent "Stop now" hold blocks regardless of any remaining budget.
        if (state.ManuallyStopped)
        {
            dto.Blocked = true;
        }

        return dto;
    }

    private readonly struct ResolvedUser
    {
        public Guid UserGuid { get; init; }

        public string UserName { get; init; }

        public string UserIdN => UserGuid.ToString("N", CultureInfo.InvariantCulture);
    }
}
