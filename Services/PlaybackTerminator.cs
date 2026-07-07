using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Services;

/// <summary>
/// Stops playback on a session using every server-side lever available, in escalating
/// order of forcefulness:
///
/// 1. Stop playstate command — the polite ask; honored by web/mobile, ignored by the
///    Android TV client (REQUIREMENTS.md §2.1).
/// 2. Pause playstate command — some clients honor Pause even when they ignore Stop.
/// 3. Kill the session's transcoding job — when the stream is transcoded or remuxed
///    (HLS), the server simply stops producing segments, so playback stalls and stops
///    within a few seconds of the client's buffer draining, with no client cooperation
///    at all. This is what makes over-limit playback end without a player restart.
/// 4. Close any live stream the session holds open.
///
/// Direct-played static files are the one case the server cannot interrupt mid-stream:
/// the client may already have buffered (or keep range-requesting) the file, and only a
/// policy-level block (see <see cref="HardBlockEnforcer"/>) invalidates its next
/// request.
/// </summary>
public sealed class PlaybackTerminator
{
    private readonly ISessionManager _sessionManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly ILogger<PlaybackTerminator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackTerminator"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="transcodeManager">Transcode manager.</param>
    /// <param name="logger">Logger.</param>
    public PlaybackTerminator(
        ISessionManager sessionManager,
        ITranscodeManager transcodeManager,
        ILogger<PlaybackTerminator> logger)
    {
        _sessionManager = sessionManager;
        _transcodeManager = transcodeManager;
        _logger = logger;
    }

    /// <summary>
    /// Stops playback on every active session belonging to a user.
    /// </summary>
    /// <param name="userId">The user's Guid.</param>
    /// <returns>The number of sessions that were playing.</returns>
    public async Task<int> StopAllSessionsAsync(Guid userId)
    {
        var sessions = _sessionManager.Sessions
            .Where(s => s.UserId == userId && s.NowPlayingItem is not null)
            .ToList();

        foreach (var session in sessions)
        {
            await StopSessionAsync(session).ConfigureAwait(false);
        }

        return sessions.Count;
    }

    /// <summary>
    /// Stops playback on a single session (commands + transcode/live-stream teardown).
    /// Safe to call repeatedly; every step is idempotent.
    /// </summary>
    /// <param name="session">The session to stop.</param>
    /// <returns>A task.</returns>
    public async Task StopSessionAsync(SessionInfo session)
    {
        var stopped = await TrySendPlaystateAsync(session, PlaystateCommand.Stop).ConfigureAwait(false);
        if (!stopped)
        {
            await TrySendPlaystateAsync(session, PlaystateCommand.Pause).ConfigureAwait(false);
        }

        KillStreams(session);
    }

    /// <summary>
    /// Tears down the server side of a session's stream (transcode job + live stream)
    /// without sending any client command. When the stream is transcoded/remuxed this
    /// stops playback even on clients that ignore remote-control commands.
    /// </summary>
    /// <param name="session">The session whose streams to kill.</param>
    public void KillStreams(SessionInfo session)
    {
        try
        {
            if (!string.IsNullOrEmpty(session.DeviceId))
            {
                // Delete-files predicate: always clean up segment files for killed jobs.
                _transcodeManager.KillTranscodingJobs(session.DeviceId, null, _ => true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "KidsLimit: killing transcoding jobs for device {Device} failed.",
                session.DeviceId);
        }

        try
        {
            var liveStreamId = session.PlayState?.LiveStreamId;
            if (!string.IsNullOrEmpty(liveStreamId))
            {
                _ = _sessionManager.CloseLiveStreamIfNeededAsync(liveStreamId, session.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KidsLimit: closing live stream for session {Session} failed.", session.Id);
        }
    }

    private async Task<bool> TrySendPlaystateAsync(SessionInfo session, PlaystateCommand command)
    {
        try
        {
            await _sessionManager.SendPlaystateCommand(
                null,
                session.Id,
                new PlaystateRequest { Command = command },
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "KidsLimit: {Command} command was rejected by session {Session} " +
                "(client='{Client}', device='{Device}'). This client likely ignores " +
                "server-sent remote-control commands.",
                command,
                session.Id,
                session.Client,
                session.DeviceName);
            return false;
        }
    }
}
