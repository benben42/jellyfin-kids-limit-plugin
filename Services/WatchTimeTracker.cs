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
    private readonly ILogger<WatchTimeTracker> _logger;

    private Timer? _maintenanceTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTimeTracker"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="store">State store.</param>
    /// <param name="logger">Logger.</param>
    public WatchTimeTracker(ISessionManager sessionManager, StateStore store, ILogger<WatchTimeTracker> logger)
    {
        _sessionManager = sessionManager;
        _store = store;
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
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

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
            _store.FlushDebounced(force: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit maintenance tick failed.");
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => Handle(e, isStop: false);

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e) => Handle(e, isStop: false);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e) => Handle(e, isStop: true);

    private void Handle(PlaybackProgressEventArgs e, bool isStop)
    {
        try
        {
            var session = e.Session;
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
            var isPaused = e.IsPaused;

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
                _ = SendStopAsync(sessionId);
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

    private async Task SendStopAsync(string sessionId)
    {
        try
        {
            await _sessionManager.SendPlaystateCommand(
                null,
                sessionId,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);

            // Best-effort on-screen notice for clients that render it (web/mobile).
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
            _logger.LogWarning(ex, "KidsLimit failed sending Stop to session {SessionId}.", sessionId);
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
        public bool Warn;
        public long RemainingSeconds;
    }
}
