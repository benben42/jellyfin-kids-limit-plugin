using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Jellyfin.Plugin.KidsLimit.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KidsLimit.Api;

/// <summary>
/// The stop-method test bench: a parent-token page and API that fires one stop mechanism
/// at a time so the reliable ones can be told apart from the ones a given client version
/// silently ignores (REQUIREMENTS.md §2.1, re-opened by the Jellyfin 12 client).
///
/// <para>
/// Diagnostic only — nothing here runs on the enforcement path. It shares
/// <see cref="KidsLimitController"/>'s parent-token scheme, so an install with no
/// <see cref="PluginConfiguration.BonusApiToken"/> configured cannot reach it at all.
/// </para>
/// </summary>
[ApiController]
[Route("KidsLimit/test")]
[AllowAnonymous]
public class StopTestController : ControllerBase
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly StopMethodTester _tester;

    /// <summary>
    /// Initializes a new instance of the <see cref="StopTestController"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager (live diagnostics).</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="tester">The stop-method bench.</param>
    public StopTestController(
        ISessionManager sessionManager,
        IUserManager userManager,
        StopMethodTester tester)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _tester = tester;
    }

    private PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Serves the test bench page: <c>/KidsLimit/test?token=&lt;BonusApiToken&gt;</c>.
    /// </summary>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The HTML page.</returns>
    [HttpGet("")]
    public ActionResult Page([FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Content(
                "<!DOCTYPE html><html><body style=\"background:#111;color:#eee;font-family:sans-serif;" +
                "display:flex;align-items:center;justify-content:center;height:100vh;font-size:3em;\">" +
                "🔒</body></html>",
                "text/html");
        }

        NoStore();
        using var stream = GetType().Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.KidsLimit.Web.stoptest.html");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "text/html");
    }

    /// <summary>
    /// Lists the testable stop methods and the users that can be targeted.
    /// </summary>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>Method catalog and user list.</returns>
    [HttpGet("methods")]
    [Produces("application/json")]
    public ActionResult<object> GetMethods([FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        NoStore();

        // Offer every Jellyfin user, not just configured kids: the point of the bench is to
        // test whatever account happens to be signed in on the TV in front of you.
        var users = _userManager.GetUsers()
            .Where(u => u is not null)
            .Select(u => new
            {
                UserId = u.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = u.Username,
                Managed = Config.Users.Any(c =>
                    c.Enabled && Guid.TryParse(c.UserId, out var g) && g == u.Id),
            })
            .OrderByDescending(u => u.Managed)
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            Methods = StopMethodTester.Methods.Select(m => new
            {
                m.Id,
                m.Group,
                m.Title,
                m.Description,
                m.WatchFor,
                m.Severity,
                m.NeedsSession,
            }).ToList(),
            Users = users,
        };
    }

    /// <summary>
    /// Dumps what the server currently believes about every live session — the half of the
    /// answer the TV screen cannot give you. Play method (DirectPlay vs Transcode) decides
    /// whether the server-side levers can bite at all, and
    /// <c>SupportsRemoteControl</c>/<c>SupportedCommands</c> say whether the client ever
    /// agreed to be remote-controlled in the first place.
    /// </summary>
    /// <param name="user">Optional user id or name to filter by.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>Session diagnostics.</returns>
    [HttpGet("sessions")]
    [Produces("application/json")]
    public ActionResult<object> GetSessions(
        [FromQuery] string? user = null,
        [FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        NoStore();

        var filter = string.IsNullOrWhiteSpace(user) ? null : ResolveUser(user);
        var sessions = _sessionManager.Sessions
            .Where(s => filter is null || s.UserId == filter.Value)
            .OrderByDescending(s => s.NowPlayingItem is not null)
            .ThenByDescending(s => s.LastActivityDate)
            .Select(Describe)
            .ToList();

        return new
        {
            ServerLocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Sessions = sessions,
        };
    }

    /// <summary>
    /// Runs one stop method against a user and reports, step by step, what the server
    /// attempted and whether each step was accepted.
    /// </summary>
    /// <param name="user">User id or name.</param>
    /// <param name="method">Method id from <see cref="GetMethods"/>.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The attempt report.</returns>
    [HttpPost("run")]
    [Produces("application/json")]
    public async Task<ActionResult<StopAttemptResult>> Run(
        [FromQuery] string user,
        [FromQuery] string method,
        [FromQuery] string? token = null)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        if (StopMethodTester.Find(method) is null)
        {
            return BadRequest($"Unknown method '{method}'.");
        }

        var guid = ResolveUser(user);
        if (guid is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        NoStore();
        return await _tester.RunAsync(method, guid.Value).ConfigureAwait(false);
    }

    private static object Describe(SessionInfo s) => new
    {
        s.Id,
        s.UserName,
        UserId = s.UserId.ToString("N", CultureInfo.InvariantCulture),
        s.Client,
        Version = s.ApplicationVersion,
        s.DeviceName,
        s.DeviceId,
        s.DeviceType,
        s.IsActive,

        // The two flags that decide whether any playstate/general command can land.
        s.SupportsMediaControl,
        s.SupportsRemoteControl,
        SupportedCommands = s.SupportedCommands?
            .Select(c => c.ToString())
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList() ?? new List<string>(),

        // Which controller(s) the server would deliver a command through. No
        // WebSocketController here means commands go nowhere regardless of the flags above.
        Controllers = s.SessionControllers?
            .Select(c => c.GetType().Name)
            .ToList() ?? new List<string>(),

        NowPlaying = s.NowPlayingItem is null ? null : new
        {
            s.NowPlayingItem.Name,
            Type = s.NowPlayingItem.Type.ToString(),
            RunTimeMinutes = s.NowPlayingItem.RunTimeTicks is long t ? (int?)(t / 600_000_000L) : null,
        },

        PlayState = s.PlayState is null ? null : new
        {
            PositionMinutes = s.PlayState.PositionTicks is long p ? (int?)(p / 600_000_000L) : null,
            s.PlayState.IsPaused,
            s.PlayState.CanSeek,

            // DirectPlay is the case no server-side stream teardown can touch.
            PlayMethod = s.PlayState.PlayMethod?.ToString() ?? "(unknown)",
            s.PlayState.LiveStreamId,
            s.PlayState.MediaSourceId,
        },

        Transcoding = s.TranscodingInfo is null ? null : new
        {
            s.TranscodingInfo.Container,
            s.TranscodingInfo.VideoCodec,
            s.TranscodingInfo.AudioCodec,
            s.TranscodingInfo.IsVideoDirect,
            s.TranscodingInfo.IsAudioDirect,
            Reasons = s.TranscodingInfo.TranscodeReasons.ToString(),
        },

        LastActivity = s.LastActivityDate.ToLocalTime()
            .ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        LastPlaybackCheckIn = s.LastPlaybackCheckIn.ToLocalTime()
            .ToString("HH:mm:ss", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Marks a response as live data that must never be reused from a cache — the page
    /// polls the diagnostics while playback is running.
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

    private Guid? ResolveUser(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        // Use var so we don't hard-code the entity namespace, which moved in 10.11.
        var user = Guid.TryParse(idOrName, out var g) ? _userManager.GetUserById(g) : null;
        user ??= _userManager.GetUserByName(idOrName);
        return user?.Id;
    }
}
