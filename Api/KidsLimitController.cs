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
    private readonly PlaybackTerminator _terminator;
    private readonly WatchTimeTracker _tracker;

    /// <summary>
    /// Initializes a new instance of the <see cref="KidsLimitController"/> class.
    /// </summary>
    /// <param name="store">State store.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="terminator">Playback terminator.</param>
    /// <param name="tracker">The watch-time tracker, which owns the enforcement path.</param>
    public KidsLimitController(
        StateStore store,
        ISessionManager sessionManager,
        IUserManager userManager,
        PlaybackTerminator terminator,
        WatchTimeTracker tracker)
    {
        _store = store;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _terminator = terminator;
        _tracker = tracker;
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

        // Nothing else to do: the tracker recomputes the remaining budget on its next pass
        // (within seconds), so extra time simply stops the stopping.
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

        NoStore();
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

        NoStore();
        return BuildStatus(resolved.Value);
    }

    /// <summary>
    /// Stops a user now and keeps them stopped for the rest of the day. The hold lives in
    /// the day's state, so it survives a restart and re-fires every time the child presses
    /// play, until the parent grants bonus, calls <see cref="Allow"/>, or midnight arrives.
    /// Nothing about the Jellyfin account is touched — they can still browse, and they get
    /// an on-screen message explaining who stopped the TV.
    /// </summary>
    /// <param name="user">User id or name.</param>
    /// <param name="token">Shared secret.</param>
    /// <returns>Number of playing sessions that were acted on.</returns>
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
        _store.SetManualStop(resolved.Value.UserIdN, today, true);

        // Hand the actual stopping to the tracker rather than repeating it here. It owns
        // the announce-then-stop sequence — the child sees "a grown-up turned the TV off"
        // and gets a few seconds to read it — and routing through it keeps one enforcement
        // path, so the parent stop cannot drift out of step with the limit stop.
        var cfg = Plugin.Instance?.FindUser(resolved.Value.UserIdN);
        if (cfg?.Enabled == true)
        {
            return new { stopped = _tracker.EnforceUserNow(resolved.Value.UserGuid) };
        }

        // Not a managed kid, so the tracker ignores them entirely (unconfigured users are
        // unlimited adults). Still honor an explicit parent stop, bluntly.
        var stopped = await _terminator.StopAllSessionsAsync(resolved.Value.UserGuid).ConfigureAwait(false);
        return new { stopped };
    }

    /// <summary>
    /// Lifts a parent "Stop now" hold so the user can play again, subject to their normal
    /// budget. Does not grant any extra time — a kid who is out of time will simply be
    /// stopped again by the limit instead of by the hold.
    /// </summary>
    /// <param name="user">User id or name.</param>
    /// <param name="token">Shared secret.</param>
    /// <returns>The user's updated status.</returns>
    [HttpPost("allow")]
    public ActionResult<UserStatusDto> Allow(
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

        var today = LimitCalculator.DateKey(DateTime.Now);
        _store.SetManualStop(resolved.Value.UserIdN, today, false);

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

        NoStore();
        var history = _store.GetHistory(resolved.Value.UserIdN);
        if (days > 0 && history.Count > days)
        {
            history = history.Skip(history.Count - days).ToList();
        }

        return history;
    }

    /// <summary>
    /// Marks a response as live data that must never be reused from a cache — the
    /// dashboards poll these URLs on a timer, and a cached body silently freezes the
    /// numbers on screen. See the same helper on <see cref="RewardsController"/>.
    /// </summary>
    private void NoStore() =>
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

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
