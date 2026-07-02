namespace Jellyfin.Plugin.KidsLimit.Configuration;

/// <summary>
/// A named, reusable set of caps. Any cap may be <c>null</c> meaning "unlimited".
/// </summary>
public class Preset
{
    /// <summary>Gets or sets the stable identifier referenced by schedules/overrides.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-friendly name, e.g. "School Day".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the max total playing minutes in the day. Null = unlimited.</summary>
    public int? DailyCapMinutes { get; set; }

    /// <summary>Gets or sets the max playing minutes in a single session. Null = unlimited.</summary>
    public int? SessionCapMinutes { get; set; }

    /// <summary>Gets or sets the max playing minutes in the morning window. Null = unlimited.</summary>
    public int? MorningCapMinutes { get; set; }

    /// <summary>Gets or sets the max playing minutes in the afternoon window. Null = unlimited.</summary>
    public int? AfternoonCapMinutes { get; set; }

    /// <summary>Gets or sets the max playing minutes in the evening window. Null = unlimited.</summary>
    public int? EveningCapMinutes { get; set; }
}
