using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Jellyfin.Plugin.KidsLimit.State;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Services;

/// <summary>
/// Hosted background service that observes playback via <see cref="ISessionManager"/>,
/// accumulates active-playing time into <see cref="DailyState"/>, and enforces limits by
/// sending Stop (and best-effort DisplayMessage) commands. Implements Phases 2 &amp; 3.
/// </summary>
public sealed class WatchTimeTracker : IHostedService, IDisposable
{
    // Max seconds credited from a single progress tick (guards against long gaps/seeks).
    private const int MaxDeltaSeconds = 30;

    // A gap larger than this starts a fresh "sitting" (resets the session-cap counter).
    private const int SessionResetGapMinutes = 30;

    private readonly ISessionManager _sessionManager;
    private readonly StateStore _store;
    private readonly AccessScheduleEnforcer _enforcer;
    private readonly ILogger<WatchTimeTracker> _logger;

    private Timer? _maintenanceTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTimeTracker"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="store">State store.</param>
    /// <param name="enforcer">Access-schedule hard enforcer.</param>
    /// <param name="logger">Logger.</param>
    public WatchTimeTracker(
        ISessionManager sessionManager,
        StateStore store,
        AccessScheduleEnforcer enforcer,
        ILogger<WatchTimeTracker> logger)
    {
        _sessionManager = sessionManager;
        _store = store;
        _enforcer = enforcer;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dataDir = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("Plugin instance not initialized.");
        _store.Initialize(dataDir);

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        _maintenanceTimer = new Timer(
            OnMaintenance,
            null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));

        _logger.LogInformation("KidsLimit tracker started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;

        _maintenanceTimer?.Dispose();
        _maintenanceTimer = null;

        _store.FlushDebounced(force: true);
        _logger.LogInformation("KidsLimit tracker stopped.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _maintenanceTimer?.Dispose();

    private void OnMaintenance(object? state)
    {
        try
        {
            var local = DateTime.Now;
            _store.RolloverAll(LimitCalculator.DateKey(local));
            _store.PruneIdleSessions(DateTime.UtcNow.AddHours(-6));

            // Actively sweep live playback so enforcement (crediting time + re-sending
            // Stop to over-limit sessions) does not depend on clients faithfully emitting
            // PlaybackProgress events. Some clients go quiet after ignoring a Stop, which
            // otherwise leaves them "blocked" on the dashboard yet still playing.
            SweepActiveSessions();

            _store.FlushDebounced(force: true);

            // Hard enforcement (opt-in): reconcile native access schedules so over-limit
            // kids are blocked server-side even on clients that ignore Stop (Android TV).
            var config = Plugin.Instance?.Configuration;
            if (config is not null)
            {
                _ = _enforcer.ReconcileAsync(config, LimitCalculator.DateKey(local), local);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit maintenance tick failed.");
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => ProcessSession(e.Session, e.IsPaused, isStop: false);

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) => ProcessSession(e.Session, e.IsPaused, isStop: false);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) => ProcessSession(e.Session, e.IsPaused, isStop: true);

    /// <summary>
    /// Polls all live playback sessions and runs the same crediting/enforcement path used
    /// by playback events. Guards against clients that stop emitting PlaybackProgress
    /// (or ignore a Stop) but keep playing.
    /// </summary>
    private void SweepActiveSessions()
    {
        foreach (var session in _sessionManager.Sessions)
        {
            if (session?.NowPlayingItem is null)
            {
                continue;
            }

            var isPaused = session.PlayState?.IsPaused ?? false;
            ProcessSession(session, isPaused, isStop: false);
        }
    }

    private void ProcessSession(SessionInfo? session, bool isPaused, bool isStop)
    {
        try
        {
            if (session is null || string.IsNullOrEmpty(session.Id))
            {
                return;
            }

            var userId = session.UserId == Guid.Empty ? string.Empty : session.UserId.ToString("N");
            if (string.IsNullOrEmpty(userId))
            {
                return;
            }

            var config = Plugin.Instance?.Configuration;
            var userCfg = Plugin.Instance?.FindUser(userId);
            if (config is null || userCfg is null || !userCfg.Enabled)
            {
                return; // Unconfigured / disabled users are unlimited adults.
            }

            var localNow = DateTime.Now;
            var nowUtc = DateTime.UtcNow;
            var today = LimitCalculator.DateKey(localNow);
            var window = LimitCalculator.WindowFor(localNow, config);
            var preset = LimitCalculator.ResolvePreset(userCfg, config, localNow);

            var decision = default(Decision);
            var sessionId = session.Id;

            _store.Mutate(userId, today, dailyState =>
            {
                if (!dailyState.ActiveSessions.TryGetValue(sessionId, out var ss))
                {
                    ss = new SessionState { LastTickUtc = nowUtc };
                    dailyState.ActiveSessions[sessionId] = ss;
                }

                var gap = nowUtc - ss.LastTickUtc;

                // A long gap since we last saw this session id => new sitting.
                if (gap.TotalMinutes > SessionResetGapMinutes)
                {
                    ss.SecondsWatched = 0;
                    ss.Warned = false;
                    ss.Blocked = false;
                }

                long delta = 0;
                if (!isPaused)
                {
                    // Credit elapsed playing time (also credits the tail end of a Stop
                    // that wasn't paused first). Clamp to guard against long gaps/seeks.
                    delta = Math.Clamp((long)Math.Round(gap.TotalSeconds), 0L, (long)MaxDeltaSeconds);
                }

                ss.LastTickUtc = nowUtc;

                if (delta > 0)
                {
                    ss.SecondsWatched += delta;
                    dailyState.SecondsWatchedTotal += delta;
                    dailyState.AddWindowSeconds(window, delta);
                }

                if (isStop)
                {
                    dailyState.ActiveSessions.Remove(sessionId);
                    return;
                }

                if (isPaused)
                {
                    return; // Paused time doesn't count and doesn't trigger enforcement.
                }

                var remaining = LimitCalculator.Compute(preset, dailyState, window, ss.SecondsWatched);
                if (!remaining.HasLimit)
                {
                    return;
                }

                if (remaining.RemainingSeconds <= 0)
                {
                    decision.NewlyBlocked = !ss.Blocked;
                    ss.Blocked = true;
                    decision.Stop = true; // Re-send every tick for robustness against clients that ignore it.
                }
                else if (!ss.Warned &&
                         remaining.RemainingSeconds <= (long)userCfg.WarnMinutesBeforeLimit * 60)
                {
                    ss.Warned = true;
                    decision.Warn = true;
                    decision.RemainingSeconds = remaining.RemainingSeconds;
                }
            });

            _store.FlushDebounced();

            if (decision.Stop)
            {
                if (decision.NewlyBlocked)
                {
                    // Log the client's advertised control capabilities so it's obvious from
                    // the server log whether the TV even claims to accept remote Stop. The
                    // Jellyfin Android TV client is known to report these as false (see
                    // REQUIREMENTS.md §2.1) and to ignore server-sent commands.
                    _logger.LogInformation(
                        "KidsLimit: user {User} is over limit on session {Session} " +
                        "(client='{Client}', device='{Device}', supportsRemoteControl={RemoteControl}, " +
                        "supportsMediaControl={MediaControl}); sending Stop.",
                        userId,
                        sessionId,
                        session.Client,
                        session.DeviceName,
                        session.SupportsRemoteControl,
                        session.Capabilities?.SupportsMediaControl);
                }

                _ = SendStopAsync(sessionId, session.Client, session.DeviceName);
            }
            else if (decision.Warn)
            {
                _ = SendWarnAsync(sessionId, userCfg.WarnMinutesBeforeLimit);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit failed handling a playback event.");
        }
    }

    private async Task SendStopAsync(string sessionId, string? client, string? device)
    {
        // Each command is sent independently so a client that rejects one (e.g. no
        // media-control support) doesn't prevent the others from being attempted.
        var stopped = await TrySendPlaystateAsync(sessionId, PlaystateCommand.Stop, client, device)
            .ConfigureAwait(false);

        // Some clients honor Pause even when they ignore Stop; try it as a fallback so
        // playback at least halts.
        if (!stopped)
        {
            await TrySendPlaystateAsync(sessionId, PlaystateCommand.Pause, client, device)
                .ConfigureAwait(false);
        }

        // Best-effort on-screen notice for clients that render it (web/mobile).
        try
        {
            await _sessionManager.SendMessageCommand(
                null,
                sessionId,
                new MessageCommand
                {
                    Header = "Time's up",
                    Text = "Watch-time limit reached. Ask a parent for bonus time to keep watching.",
                    TimeoutMs = 8000,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KidsLimit: DisplayMessage to session {SessionId} failed.", sessionId);
        }
    }

    private async Task<bool> TrySendPlaystateAsync(
        string sessionId, PlaystateCommand command, string? client, string? device)
    {
        try
        {
            await _sessionManager.SendPlaystateCommand(
                null,
                sessionId,
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
                sessionId,
                client,
                device);
            return false;
        }
    }

    private async Task SendWarnAsync(string sessionId, int minutesLeft)
    {
        try
        {
            await _sessionManager.SendMessageCommand(
                null,
                sessionId,
                new MessageCommand
                {
                    Header = "Almost done",
                    Text = $"About {minutesLeft} minutes of watch time left today.",
                    TimeoutMs = 8000,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit failed sending warning to session {SessionId}.", sessionId);
        }
    }

    private struct Decision
    {
        public bool Stop;
        public bool NewlyBlocked;
        public bool Warn;
        public long RemainingSeconds;
    }
}
