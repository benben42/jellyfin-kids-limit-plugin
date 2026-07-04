using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.State;

/// <summary>
/// Thread-safe persistence for <see cref="DailyState"/> and finished-day history.
/// State is written to the plugin data folder as JSON. Writes are debounced so a
/// server crash loses at most a few seconds of accumulated time.
/// </summary>
public class StateStore
{
    private const int MaxHistoryEntries = 400; // a bit over a year
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly ILogger<StateStore> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, DailyState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastFlushUtc = new(StringComparer.Ordinal);

    private string _dataDir = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateStore"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public StateStore(ILogger<StateStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets or sets the minimum seconds between debounced flushes per user.</summary>
    public double DebounceSeconds { get; set; } = 20;

    /// <summary>Initializes the on-disk location. Must be called once at startup.</summary>
    /// <param name="dataDir">The plugin data folder.</param>
    public void Initialize(string dataDir)
    {
        lock (_sync)
        {
            _dataDir = dataDir;
            Directory.CreateDirectory(StateDir());
            Directory.CreateDirectory(HistoryDir());
        }
    }

    /// <summary>
    /// Returns the (already-rolled-over) state for a user, loading from disk on first use.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="localToday">Today's local date string (yyyy-MM-dd).</param>
    /// <returns>The current-day state.</returns>
    public DailyState GetOrCreate(string userId, string localToday)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(userId, out var state))
            {
                state = Load(userId) ?? new DailyState { UserId = userId, Date = localToday };
                _states[userId] = state;
            }

            RollOverIfNeeded(state, localToday);
            return state;
        }
    }

    /// <summary>Runs <paramref name="action"/> under the store lock and marks the user dirty.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="localToday">Today's local date string.</param>
    /// <param name="action">Mutation to apply to the state.</param>
    public void Mutate(string userId, string localToday, Action<DailyState> action)
    {
        lock (_sync)
        {
            var state = GetOrCreate(userId, localToday);
            action(state);
            _dirty.Add(userId);
        }
    }

    /// <summary>Persists any user whose debounce window has elapsed. Call frequently.</summary>
    /// <param name="force">When true, flush all dirty users regardless of debounce.</param>
    public void FlushDebounced(bool force = false)
    {
        List<DailyState> toWrite = new();
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            foreach (var userId in _dirty.ToList())
            {
                _lastFlushUtc.TryGetValue(userId, out var last);
                if (force || (now - last).TotalSeconds >= DebounceSeconds)
                {
                    if (_states.TryGetValue(userId, out var s))
                    {
                        toWrite.Add(Clone(s));
                        _lastFlushUtc[userId] = now;
                        _dirty.Remove(userId);
                    }
                }
            }
        }

        foreach (var s in toWrite)
        {
            WriteState(s);
        }
    }

    /// <summary>Grants bonus seconds to a user's daily and session budgets.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="localToday">Today's local date string.</param>
    /// <param name="seconds">Bonus seconds to add.</param>
    public void GrantBonus(string userId, string localToday, long seconds)
    {
        Mutate(userId, localToday, s =>
        {
            s.DailyBonusSeconds += seconds;
            s.SessionBonusSeconds += seconds;

            // Granting time also lifts a parent "Stop now" so the kid can play again.
            s.ManuallyStopped = false;

            // Un-block any active sessions so "press play again" works immediately.
            foreach (var sess in s.ActiveSessions.Values)
            {
                sess.Blocked = false;
                sess.Warned = false;
            }
        });
        FlushDebounced(force: true);
    }

    /// <summary>Sets (or clears) the parent "Stop now" flag for a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="localToday">Today's local date string.</param>
    /// <param name="stopped">True to force-stop for the rest of the day; false to allow again.</param>
    public void SetManualStop(string userId, string localToday, bool stopped)
    {
        Mutate(userId, localToday, s =>
        {
            s.ManuallyStopped = stopped;

            // When lifting a manual stop, also clear any sticky per-session block flags so
            // the kid can press play again without waiting for the next sweep.
            if (!stopped)
            {
                foreach (var sess in s.ActiveSessions.Values)
                {
                    sess.Blocked = false;
                    sess.Warned = false;
                }
            }
        });
        FlushDebounced(force: true);
    }

    /// <summary>
    /// Belt-and-suspenders midnight rollover for every currently-loaded user, independent
    /// of playback events.
    /// </summary>
    /// <param name="localToday">Today's local date string.</param>
    public void RolloverAll(string localToday)
    {
        lock (_sync)
        {
            foreach (var state in _states.Values)
            {
                RollOverIfNeeded(state, localToday);
            }
        }
    }

    /// <summary>Removes session tracking entries whose last tick is older than the cutoff.</summary>
    /// <param name="cutoffUtc">Sessions last active before this are pruned.</param>
    public void PruneIdleSessions(DateTime cutoffUtc)
    {
        lock (_sync)
        {
            foreach (var state in _states.Values)
            {
                var stale = state.ActiveSessions
                    .Where(kv => kv.Value.LastTickUtc < cutoffUtc)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in stale)
                {
                    state.ActiveSessions.Remove(key);
                    _dirty.Add(state.UserId);
                }
            }
        }
    }

    /// <summary>Reads the finished-day history for a user (most recent last).</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The history entries.</returns>
    public List<DailyHistoryEntry> GetHistory(string userId)
    {
        lock (_sync)
        {
            var path = HistoryPath(userId);
            if (!File.Exists(path))
            {
                return new List<DailyHistoryEntry>();
            }

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<DailyHistoryEntry>>(json) ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed reading history for {UserId}", userId);
                return new List<DailyHistoryEntry>();
            }
        }
    }

    private void RollOverIfNeeded(DailyState state, string localToday)
    {
        if (string.Equals(state.Date, localToday, StringComparison.Ordinal))
        {
            return;
        }

        // Archive the finishing day (only if it accumulated anything worth keeping).
        if (!string.IsNullOrEmpty(state.Date) && state.SecondsWatchedTotal > 0)
        {
            AppendHistory(state);
        }

        _logger.LogInformation(
            "Rolling over daily state for {UserId}: {OldDate} -> {NewDate}",
            state.UserId,
            state.Date,
            localToday);

        state.Date = localToday;
        state.SecondsWatchedTotal = 0;
        state.SecondsMorning = 0;
        state.SecondsAfternoon = 0;
        state.SecondsEvening = 0;
        state.DailyBonusSeconds = 0;
        state.SessionBonusSeconds = 0;
        state.ManuallyStopped = false;

        // Keep active sessions but reset their per-day counters/flags.
        foreach (var sess in state.ActiveSessions.Values)
        {
            sess.SecondsWatched = 0;
            sess.Warned = false;
            sess.Blocked = false;
        }

        _dirty.Add(state.UserId);
    }

    private void AppendHistory(DailyState state)
    {
        try
        {
            var path = HistoryPath(state.UserId);
            List<DailyHistoryEntry> history = File.Exists(path)
                ? JsonSerializer.Deserialize<List<DailyHistoryEntry>>(File.ReadAllText(path)) ?? new()
                : new();

            // Avoid duplicating an entry for the same date.
            history.RemoveAll(h => string.Equals(h.Date, state.Date, StringComparison.Ordinal));
            history.Add(new DailyHistoryEntry
            {
                Date = state.Date,
                SecondsWatchedTotal = state.SecondsWatchedTotal,
                SecondsMorning = state.SecondsMorning,
                SecondsAfternoon = state.SecondsAfternoon,
                SecondsEvening = state.SecondsEvening,
                BonusSecondsGranted = state.DailyBonusSeconds,
            });

            if (history.Count > MaxHistoryEntries)
            {
                history = history.Skip(history.Count - MaxHistoryEntries).ToList();
            }

            File.WriteAllText(path, JsonSerializer.Serialize(history, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed appending history for {UserId}", state.UserId);
        }
    }

    private DailyState? Load(string userId)
    {
        try
        {
            var path = StatePath(userId);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<DailyState>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading state for {UserId}", userId);
            return null;
        }
    }

    private void WriteState(DailyState state)
    {
        try
        {
            var path = StatePath(state.UserId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed writing state for {UserId}", state.UserId);
        }
    }

    private static DailyState Clone(DailyState s) => new()
    {
        UserId = s.UserId,
        Date = s.Date,
        SecondsWatchedTotal = s.SecondsWatchedTotal,
        SecondsMorning = s.SecondsMorning,
        SecondsAfternoon = s.SecondsAfternoon,
        SecondsEvening = s.SecondsEvening,
        DailyBonusSeconds = s.DailyBonusSeconds,
        SessionBonusSeconds = s.SessionBonusSeconds,
        ManuallyStopped = s.ManuallyStopped,
        ActiveSessions = s.ActiveSessions.ToDictionary(
            kv => kv.Key,
            kv => new SessionState
            {
                SecondsWatched = kv.Value.SecondsWatched,
                Warned = kv.Value.Warned,
                Blocked = kv.Value.Blocked,
                LastTickUtc = kv.Value.LastTickUtc,
            },
            StringComparer.Ordinal),
    };

    private string StateDir() => Path.Combine(_dataDir, "state");

    private string HistoryDir() => Path.Combine(_dataDir, "history");

    private string StatePath(string userId) =>
        Path.Combine(StateDir(), Sanitize(userId) + ".json");

    private string HistoryPath(string userId) =>
        Path.Combine(HistoryDir(), Sanitize(userId) + ".json");

    private static string Sanitize(string userId)
    {
        // User ids are Guids, but be defensive against path traversal.
        var chars = userId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray();
        var cleaned = new string(chars);
        return string.IsNullOrEmpty(cleaned)
            ? "unknown"
            : cleaned.ToLower(CultureInfo.InvariantCulture);
    }
}
