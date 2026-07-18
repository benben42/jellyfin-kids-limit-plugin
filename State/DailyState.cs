using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.KidsLimit.State;

/// <summary>Time-of-day window within a single day.</summary>
public enum Window
{
    /// <summary>[00:00, middayStart).</summary>
    Morning,

    /// <summary>[middayStart, eveningStart).</summary>
    Afternoon,

    /// <summary>[eveningStart, 24:00).</summary>
    Evening,
}

/// <summary>
/// Per-user accumulated state for a single local day. Persisted as JSON in the plugin
/// data folder so it survives server restarts.
/// </summary>
public class DailyState
{
    /// <summary>Gets or sets the Jellyfin user id (Guid string).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the local calendar date this state belongs to (yyyy-MM-dd).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets total playing seconds accumulated today.</summary>
    public long SecondsWatchedTotal { get; set; }

    /// <summary>Gets or sets playing seconds accumulated in the morning window.</summary>
    public long SecondsMorning { get; set; }

    /// <summary>Gets or sets playing seconds accumulated in the afternoon window.</summary>
    public long SecondsAfternoon { get; set; }

    /// <summary>Gets or sets playing seconds accumulated in the evening window.</summary>
    public long SecondsEvening { get; set; }

    /// <summary>Gets or sets bonus seconds granted for today; expires at local midnight.</summary>
    public long DailyBonusSeconds { get; set; }

    /// <summary>Gets or sets bonus seconds granted for the current session(s); expires at local midnight.</summary>
    public long SessionBonusSeconds { get; set; }

    /// <summary>
    /// Gets or sets the portion of <see cref="DailyBonusSeconds"/> that was paid for with
    /// wallet coins today. Used by the midnight rollover to refund redeemed-but-unwatched
    /// time back to the wallet (REWARDS.md).
    /// </summary>
    public long RedeemedSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a parent forced an immediate stop via the
    /// dashboard ("Stop now"). While set, the user is hard-blocked for the rest of the day
    /// regardless of remaining budget. Cleared by granting bonus, the Allow endpoint, or the
    /// midnight rollover.
    /// </summary>
    public bool ManuallyStopped { get; set; }

    /// <summary>Gets or sets the transient per-session tracking, keyed by session id.</summary>
    public Dictionary<string, SessionState> ActiveSessions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets the accumulated seconds for a given window.</summary>
    /// <param name="window">The window.</param>
    /// <returns>Seconds watched in that window.</returns>
    public long GetWindowSeconds(Window window) => window switch
    {
        Window.Morning => SecondsMorning,
        Window.Afternoon => SecondsAfternoon,
        Window.Evening => SecondsEvening,
        _ => 0,
    };

    /// <summary>Adds seconds to a given window's accumulator.</summary>
    /// <param name="window">The window.</param>
    /// <param name="seconds">Seconds to add.</param>
    public void AddWindowSeconds(Window window, long seconds)
    {
        switch (window)
        {
            case Window.Morning: SecondsMorning += seconds; break;
            case Window.Afternoon: SecondsAfternoon += seconds; break;
            case Window.Evening: SecondsEvening += seconds; break;
        }
    }
}

/// <summary>Transient tracking for one active playback session.</summary>
public class SessionState
{
    /// <summary>Gets or sets seconds of active playback accumulated in this session.</summary>
    public long SecondsWatched { get; set; }

    /// <summary>Gets or sets a value indicating whether the near-limit warning was already sent.</summary>
    public bool Warned { get; set; }

    /// <summary>Gets or sets a value indicating whether a hard stop was already issued.</summary>
    public bool Blocked { get; set; }

    /// <summary>
    /// Gets or sets when this session was first seen still actively playing while over
    /// limit. Drives the "auto-stop failed" parent alert; null while under limit.
    /// </summary>
    public DateTime? OverLimitSinceUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the over-limit alert was already sent for this sitting.</summary>
    public bool OverLimitAlerted { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the last progress tick used to compute deltas.
    /// Not persisted meaningfully across restarts (treated as "now" on first tick).
    /// </summary>
    public DateTime LastTickUtc { get; set; }
}
