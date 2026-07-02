namespace Jellyfin.Plugin.KidsLimit.State;

/// <summary>
/// A finished-day rollup, appended when a day rolls over. Used to compute
/// history/averages on the dashboard from our own authoritative data.
/// </summary>
public class DailyHistoryEntry
{
    /// <summary>Gets or sets the local calendar date (yyyy-MM-dd).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets total playing seconds watched that day.</summary>
    public long SecondsWatchedTotal { get; set; }

    /// <summary>Gets or sets morning-window seconds that day.</summary>
    public long SecondsMorning { get; set; }

    /// <summary>Gets or sets afternoon-window seconds that day.</summary>
    public long SecondsAfternoon { get; set; }

    /// <summary>Gets or sets evening-window seconds that day.</summary>
    public long SecondsEvening { get; set; }

    /// <summary>Gets or sets total bonus seconds granted that day.</summary>
    public long BonusSecondsGranted { get; set; }
}
