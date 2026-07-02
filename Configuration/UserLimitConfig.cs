using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.KidsLimit.Configuration;

/// <summary>
/// Per-user limit configuration. Weekday-&gt;preset mapping is stored as seven explicit
/// string fields rather than a dictionary so the XML serializer can round-trip it.
/// </summary>
public class UserLimitConfig
{
    /// <summary>Gets or sets the Jellyfin user id (Guid string).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the cached user name (display only; UserId is authoritative).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether limits apply. False/absent = unlimited adult.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the minutes-before-limit warning threshold. Default 10.</summary>
    public int WarnMinutesBeforeLimit { get; set; } = 10;

    /// <summary>Gets or sets the preset id used on Mondays.</summary>
    public string MondayPresetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the preset id used on Tuesdays.</summary>
    public string TuesdayPresetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the preset id used on Wednesdays.</summary>
    public string WednesdayPresetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the preset id used on Thursdays.</summary>
    public string ThursdayPresetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the preset id used on Fridays.</summary>
    public string FridayPresetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the preset id used on Saturdays.</summary>
    public string SaturdayPresetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the preset id used on Sundays.</summary>
    public string SundayPresetId { get; set; } = string.Empty;

    /// <summary>Gets or sets calendar-date overrides that beat the weekly schedule.</summary>
    public List<DateOverride> DateOverrides { get; set; } = new();

    /// <summary>
    /// Resolves the preset id assigned to a weekday.
    /// </summary>
    /// <param name="day">The day of week.</param>
    /// <returns>The preset id, or empty string if none assigned.</returns>
    public string GetWeekdayPresetId(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => MondayPresetId,
        DayOfWeek.Tuesday => TuesdayPresetId,
        DayOfWeek.Wednesday => WednesdayPresetId,
        DayOfWeek.Thursday => ThursdayPresetId,
        DayOfWeek.Friday => FridayPresetId,
        DayOfWeek.Saturday => SaturdayPresetId,
        DayOfWeek.Sunday => SundayPresetId,
        _ => string.Empty,
    };
}

/// <summary>
/// Maps a specific calendar date (yyyy-MM-dd, server-local) to a preset id.
/// </summary>
public class DateOverride
{
    /// <summary>Gets or sets the local calendar date in <c>yyyy-MM-dd</c> form.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets the preset id to apply on that date.</summary>
    public string PresetId { get; set; } = string.Empty;
}
