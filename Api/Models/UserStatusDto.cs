namespace Jellyfin.Plugin.KidsLimit.Api.Models;

/// <summary>Per-user status returned by the REST API and consumed by the dashboard.</summary>
public class UserStatusDto
{
    /// <summary>Gets or sets the user id (Guid "N" form).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether limits are enabled for the user.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets today's local date (yyyy-MM-dd).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets the active preset name.</summary>
    public string PresetName { get; set; } = string.Empty;

    /// <summary>Gets or sets the current window (Morning/Afternoon/Evening).</summary>
    public string CurrentWindow { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether any cap applies today.</summary>
    public bool HasLimit { get; set; }

    /// <summary>Gets or sets effective (most-restrictive) remaining seconds; null = unlimited.</summary>
    public long? EffectiveRemainingSeconds { get; set; }

    /// <summary>Gets or sets which cap is binding (session/daily/window).</summary>
    public string BindingCap { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether an active session is currently blocked.</summary>
    public bool Blocked { get; set; }

    /// <summary>Gets or sets total playing seconds today.</summary>
    public long SecondsWatchedTotal { get; set; }

    /// <summary>Gets or sets the current session's playing seconds.</summary>
    public long CurrentSessionSeconds { get; set; }

    /// <summary>Gets or sets morning-window seconds today.</summary>
    public long SecondsMorning { get; set; }

    /// <summary>Gets or sets afternoon-window seconds today.</summary>
    public long SecondsAfternoon { get; set; }

    /// <summary>Gets or sets evening-window seconds today.</summary>
    public long SecondsEvening { get; set; }

    /// <summary>Gets or sets bonus seconds granted today.</summary>
    public long BonusSeconds { get; set; }

    /// <summary>Gets or sets the daily cap (minutes); null = unlimited.</summary>
    public int? DailyCapMinutes { get; set; }

    /// <summary>Gets or sets the session cap (minutes); null = unlimited.</summary>
    public int? SessionCapMinutes { get; set; }

    /// <summary>Gets or sets the morning cap (minutes); null = unlimited.</summary>
    public int? MorningCapMinutes { get; set; }

    /// <summary>Gets or sets the afternoon cap (minutes); null = unlimited.</summary>
    public int? AfternoonCapMinutes { get; set; }

    /// <summary>Gets or sets the evening cap (minutes); null = unlimited.</summary>
    public int? EveningCapMinutes { get; set; }
}
