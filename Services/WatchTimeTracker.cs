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
/// telling the client to stop — and telling the child why, first.
///
/// <para>
/// Enforcement is deliberately a single mechanism now. The plugin used to also mutate
/// Jellyfin user policies (access schedule / playback permission) because the Android TV
/// client was documented as ignoring the Stop command; bench-testing the Jellyfin 12
/// client showed it honors Stop, Pause and Seek, so the policy path was removed. What
/// remains is "ask the client to stop, and keep asking" — which never locks a child out of
/// the server, never leaves an account in a half-modified state, and lets the TV show a
/// message explaining itself. The cost is that there is no fallback if a future client
/// regresses, which is what the over-limit watchdog exists to shout about.
/// </para>
/// </summary>
public sealed class WatchTimeTracker : IHostedService, IDisposable
{
    // Max seconds credited from a single progress tick (guards against long gaps/seeks).
    private const int MaxDeltaSeconds = 30;

    // A gap larger than this starts a fresh "sitting" (resets the session-cap counter).
    private const int SessionResetGapMinutes = 30;

    // How often the enforcement sweep runs. Short, because it is the safety net for a
    // child who presses play again the moment the screen clears: a PlaybackStart event
    // normally catches that instantly, but a client that starts quietly must not get a
    // free quarter-minute. The sweep itself is a cheap in-memory walk of live sessions.
    private const int SweepSeconds = 5;

    // The housekeeping in OnMaintenance (rollover, pruning, forced flush) is not urgent
    // and involves disk, so it runs on every Nth sweep rather than every sweep.
    private const int MaintenanceEverySweeps = 3;

    // Don't re-show the "TV is sleeping" message more than this often per session, or a
    // child holding the play button turns it into a strobe.
    private const int BlockedMessageCooldownSeconds = 30;

    private readonly ISessionManager _sessionManager;
    private readonly StateStore _store;
    private readonly WalletStore _wallets;
    private readonly RewardsService _rewards;
    private readonly PlaybackTerminator _terminator;
    private readonly NotificationService _notifications;
    private readonly LegacyBlockRestorer _legacyRestorer;
    private readonly ILogger<WatchTimeTracker> _logger;

    private Timer? _sweepTimer;
    private int _sweepCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchTimeTracker"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="store">State store.</param>
    /// <param name="wallets">Wallet store.</param>
    /// <param name="rewards">Rewards service.</param>
    /// <param name="terminator">Playback terminator.</param>
    /// <param name="notifications">Push-notification fan-out (over-limit alerts).</param>
    /// <param name="legacyRestorer">One-shot cleanup of the retired hard-block state.</param>
    /// <param name="logger">Logger.</param>
    public WatchTimeTracker(
        ISessionManager sessionManager,
        StateStore store,
        WalletStore wallets,
        RewardsService rewards,
        PlaybackTerminator terminator,
        NotificationService notifications,
        LegacyBlockRestorer legacyRestorer,
        ILogger<WatchTimeTracker> logger)
    {
        _sessionManager = sessionManager;
        _store = store;
        _wallets = wallets;
        _rewards = rewards;
        _terminator = terminator;
        _notifications = notifications;
        _legacyRestorer = legacyRestorer;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dataDir = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("Plugin instance not initialized.");
        _store.Initialize(dataDir);
        _wallets.Initialize(dataDir);

        // Refund redeemed-but-unwatched coins when a day rolls over (REWARDS.md).
        _store.DayFinished = _rewards.RefundFinishedDay;

        // Undo anything the retired hard-block enforcer may still be holding. Fire and
        // forget: an install upgraded mid-block must not wait on user-policy writes to
        // start tracking, and the restorer swallows its own failures.
        _ = _legacyRestorer.RestoreAllAsync(dataDir);

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        _sweepTimer = new Timer(
            OnSweep,
            null,
            TimeSpan.FromSeconds(SweepSeconds),
            TimeSpan.FromSeconds(SweepSeconds));

        _logger.LogInformation("KidsLimit tracker started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;

        _sweepTimer?.Dispose();
        _sweepTimer = null;

        _store.FlushDebounced(force: true);
        _logger.LogInformation("KidsLimit tracker stopped.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _sweepTimer?.Dispose();

    /// <summary>
    /// Runs the enforcement pass for one user's live sessions immediately, instead of
    /// waiting up to <see cref="SweepSeconds"/> for the next sweep. Called by the parent
    /// "Stop now" endpoint so the whole thing — message, grace, stop — starts the instant
    /// the parent taps, while keeping a single enforcement code path.
    /// </summary>
    /// <param name="userId">The user whose sessions to act on.</param>
    /// <returns>How many playing sessions were examined.</returns>
    public int EnforceUserNow(Guid userId)
    {
        var count = 0;
        foreach (var session in _sessionManager.Sessions)
        {
            if (session?.NowPlayingItem is null || session.UserId != userId)
            {
                continue;
            }

            count++;
            ProcessSession(session, session.PlayState?.IsPaused ?? false, isStop: false);
        }

        return count;
    }

    /// <summary>
    /// Substitutes the placeholders a parent may use in a configured message, and falls
    /// back to the shipped default when the field has been blanked out — an empty message
    /// would otherwise mean the child's TV stops with no explanation at all.
    /// </summary>
    private static string Format(string? configured, string fallback, string kidName, int minutes)
    {
        var text = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        return text
            .Replace("{minutes}", minutes.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{name}", kidName, StringComparison.Ordinal);
    }

    private void OnSweep(object? state)
    {
        try
        {
            // Actively sweep live playback so enforcement does not depend on clients
            // faithfully emitting PlaybackProgress. A client that goes quiet after being
            // told to stop must still be credited and told again.
            SweepActiveSessions();

            if (++_sweepCount % MaintenanceEverySweeps != 0)
            {
                return;
            }

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
                    ss.OverLimitSinceUtc = null;
                    ss.OverLimitAlerted = false;
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
                    // Seconds beyond the BASE caps are running on bonus: drain the
                    // day-wide pool by that much, exactly once, before crediting — so a
                    // redeem lifts the caps by its worth in total, not once per window
                    // and per sitting. Capped at the pool so time watched while blocked
                    // with no bonus (clients ignoring Stop) can't eat future grants.
                    var baseRemaining = LimitCalculator.BaseRemaining(preset, dailyState, window, ss.SecondsWatched);
                    var overBase = delta - Math.Clamp(baseRemaining, 0L, delta);
                    if (overBase > 0)
                    {
                        var pool = Math.Max(0, dailyState.DailyBonusSeconds - dailyState.BonusConsumedSeconds);
                        dailyState.BonusConsumedSeconds += Math.Min(overBase, pool);
                    }

                    ss.SecondsWatched += delta;
                    dailyState.SecondsWatchedTotal += delta;
                    dailyState.AddWindowSeconds(window, delta);
                }

                if (isStop)
                {
                    dailyState.ActiveSessions.Remove(sessionId);
                    return;
                }

                // A parent's "Stop now" is enforced right here rather than by touching the
                // account. It outranks any remaining budget, and because it lives in the
                // day's state it survives a restart and keeps biting every time the child
                // presses play — until the parent calls /allow or grants bonus, or midnight.
                //
                // Checked ahead of the paused guard on purpose: a paused session is still a
                // TV sitting there with a frozen frame on it, and a parent who taps Stop
                // means that one too.
                if (dailyState.ManuallyStopped)
                {
                    decision.NewlyBlocked = !ss.Blocked;
                    ss.Blocked = true;
                    decision.Stop = true;
                    decision.Reason = StopReason.ParentStop;
                    MarkMessage(ss, nowUtc, ref decision);
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

                    // "Time's up" the first time we stop this sitting; "TV is sleeping"
                    // every time afterwards, because by then the child is pressing play on
                    // a limit they have already been told about and needs a different
                    // sentence — not the same one again.
                    decision.Reason = decision.NewlyBlocked ? StopReason.LimitReached : StopReason.AlreadyOver;
                    MarkMessage(ss, nowUtc, ref decision);

                    // Watchdog: time still accruing (delta > 0) means the client is playing
                    // on despite the Stop. After the configured grace, alert the parent that
                    // auto-stop appears to have failed — once per sitting. With the policy
                    // fallback gone this alert is the only backstop there is.
                    if (delta > 0)
                    {
                        ss.OverLimitSinceUtc ??= nowUtc;
                        var overFor = nowUtc - ss.OverLimitSinceUtc.Value;
                        if (!ss.OverLimitAlerted &&
                            config.OverLimitAlertEnabled &&
                            overFor.TotalMinutes >= Math.Max(1, config.OverLimitAlertMinutes))
                        {
                            ss.OverLimitAlerted = true;
                            decision.AlertOverLimit = true;
                            decision.OverLimitMinutes = (int)Math.Round(overFor.TotalMinutes);
                        }
                    }
                }
                else
                {
                    // Back under limit (e.g. bonus granted) — re-arm the watchdog and the
                    // block flag, so the next stop is a fresh "time's up" rather than the
                    // terser repeat message.
                    ss.OverLimitSinceUtc = null;
                    ss.OverLimitAlerted = false;
                    ss.Blocked = false;

                    if (!ss.Warned &&
                        remaining.RemainingSeconds <= (long)userCfg.WarnMinutesBeforeLimit * 60)
                    {
                        ss.Warned = true;
                        decision.Warn = true;
                        decision.RemainingSeconds = remaining.RemainingSeconds;
                    }
                }
            });

            _store.FlushDebounced();

            var kidName = string.IsNullOrWhiteSpace(session.UserName)
                ? (string.IsNullOrWhiteSpace(userCfg.UserName) ? userId : userCfg.UserName)
                : session.UserName;

            if (decision.Stop)
            {
                if (decision.NewlyBlocked)
                {
                    _logger.LogInformation(
                        "KidsLimit: stopping {User} on session {Session} ({Reason}) — " +
                        "client='{Client}', device='{Device}', supportsRemoteControl={RemoteControl}.",
                        kidName,
                        sessionId,
                        decision.Reason,
                        session.Client,
                        session.DeviceName,
                        session.SupportsRemoteControl);
                }

                _ = AnnounceThenStopAsync(session, config, decision, kidName);
            }
            else if (decision.Warn)
            {
                // Round the real remaining time up rather than quoting the configured lead:
                // the warning fires on the first tick at or below the threshold, so "10
                // minutes" would be a small lie and a child counts.
                var minutesLeft = Math.Max(1, (int)Math.Ceiling(decision.RemainingSeconds / 60d));
                _ = SendMessageAsync(
                    session.Id,
                    config,
                    Format(config.WarnMessageHeader, PluginConfiguration.DefaultWarnHeader, kidName, minutesLeft),
                    Format(config.WarnMessageText, PluginConfiguration.DefaultWarnText, kidName, minutesLeft));
            }

            if (decision.AlertOverLimit)
            {
                _logger.LogWarning(
                    "KidsLimit: {User} still playing {Minutes} min after their limit on " +
                    "'{Device}' ({Client}) — the Stop command is being ignored; notifying parent.",
                    kidName,
                    decision.OverLimitMinutes,
                    session.DeviceName,
                    session.Client);
                _notifications.Send(
                    config,
                    "⚠️ Auto-stop failed?",
                    $"{kidName} is still watching {decision.OverLimitMinutes} min after the limit " +
                    $"on {session.DeviceName} ({session.Client}). The Stop command seems to be " +
                    "ignored by this client — you may need to stop the TV by hand.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit failed handling a playback event.");
        }
    }

    /// <summary>
    /// Decides whether this tick should also put a message on screen. Every tick sends the
    /// Stop again (cheap, and the point of the design), but the message is rate-limited:
    /// the first stop of a sitting always speaks, and repeats are throttled so a child
    /// leaning on the play button doesn't produce a flashing wall of banners.
    /// </summary>
    private static void MarkMessage(SessionState ss, DateTime nowUtc, ref Decision decision)
    {
        if (decision.NewlyBlocked ||
            ss.LastBlockMessageUtc is null ||
            (nowUtc - ss.LastBlockMessageUtc.Value).TotalSeconds >= BlockedMessageCooldownSeconds)
        {
            ss.LastBlockMessageUtc = nowUtc;
            decision.Announce = true;
        }
    }

    /// <summary>
    /// Tells the child why, then stops the stream.
    /// <para>
    /// The order is the whole point and it is the opposite of what this did before. The
    /// client only renders a DisplayMessage <b>while something is playing</b>, so a message
    /// sent after the Stop lands on a dead player and is never seen — which is exactly what
    /// used to happen, and why the TV appeared to just switch itself off for no reason.
    /// Message first, a few seconds to read it, then stop.
    /// </para>
    /// </summary>
    private async Task AnnounceThenStopAsync(
        SessionInfo session,
        PluginConfiguration config,
        Decision decision,
        string kidName)
    {
        try
        {
            if (decision.Announce)
            {
                var (header, text) = decision.Reason switch
                {
                    StopReason.ParentStop => (
                        Format(config.ParentStopMessageHeader, PluginConfiguration.DefaultParentStopHeader, kidName, 0),
                        Format(config.ParentStopMessageText, PluginConfiguration.DefaultParentStopText, kidName, 0)),
                    StopReason.AlreadyOver => (
                        Format(config.BlockedMessageHeader, PluginConfiguration.DefaultBlockedHeader, kidName, 0),
                        Format(config.BlockedMessageText, PluginConfiguration.DefaultBlockedText, kidName, 0)),
                    _ => (
                        Format(config.LimitMessageHeader, PluginConfiguration.DefaultLimitHeader, kidName, 0),
                        Format(config.LimitMessageText, PluginConfiguration.DefaultLimitText, kidName, 0)),
                };

                await SendMessageAsync(session.Id, config, header, text).ConfigureAwait(false);

                // Only the first stop of a sitting earns the full reading pause. A repeat
                // stop (the child pressed play again) ends promptly: they have already been
                // told, and a long grace would hand out free watch time every attempt.
                var grace = decision.Reason == StopReason.AlreadyOver
                    ? Math.Min(2, Math.Clamp(config.StopGraceSeconds, 0, 60))
                    : Math.Clamp(config.StopGraceSeconds, 0, 60);
                if (grace > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(grace)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KidsLimit: announcing the stop to session {SessionId} failed.", session.Id);
        }

        // Stop regardless of whether the message got through: the message is a courtesy,
        // the stop is the requirement.
        await _terminator.StopSessionAsync(session).ConfigureAwait(false);
    }

    private async Task SendMessageAsync(string sessionId, PluginConfiguration config, string header, string text)
    {
        try
        {
            await _sessionManager.SendMessageCommand(
                null,
                sessionId,
                new MessageCommand
                {
                    Header = header,
                    Text = text,
                    TimeoutMs = Math.Clamp(config.MessageSeconds, 1, 60) * 1000L,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "KidsLimit: DisplayMessage to session {SessionId} failed.", sessionId);
        }
    }

    /// <summary>Why a session is being stopped — selects which message the child sees.</summary>
    private enum StopReason
    {
        /// <summary>The daily/window/session budget just ran out.</summary>
        LimitReached,

        /// <summary>They were already out of time and pressed play again.</summary>
        AlreadyOver,

        /// <summary>A parent pressed "Stop now".</summary>
        ParentStop,
    }

    private struct Decision
    {
        public bool Stop;
        public bool NewlyBlocked;
        public bool Announce;
        public StopReason Reason;
        public bool Warn;
        public long RemainingSeconds;
        public bool AlertOverLimit;
        public int OverLimitMinutes;
    }
}
