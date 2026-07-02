using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Jellyfin.Plugin.KidsLimit.State;

namespace Jellyfin.Plugin.KidsLimit.Services;

/// <summary>
/// Pure, side-effect-free limit math. Shared by the real-time tracker and the REST API
/// so both agree on "how much time is left". Implements the §5 algorithm.
/// </summary>
public static class LimitCalculator
{
    /// <summary>Determines the window for a local time-of-day.</summary>
    /// <param name="local">The server-local timestamp.</param>
    /// <param name="config">Plugin config (window cut-points).</param>
    /// <returns>The active window.</returns>
    public static Window WindowFor(DateTime local, PluginConfiguration config)
    {
        var minutes = (local.Hour * 60) + local.Minute;
        if (minutes < config.MiddayStartMinutes)
        {
            return Window.Morning;
        }

        return minutes < config.EveningStartMinutes ? Window.Afternoon : Window.Evening;
    }

    /// <summary>Formats a local date as the yyyy-MM-dd key used throughout.</summary>
    /// <param name="local">The local date.</param>
    /// <returns>The date key.</returns>
    public static string DateKey(DateTime local) =>
        local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves today's active preset for a user: date override beats weekly schedule.
    /// Returns null when the user has no preset assigned for the day (treated as unlimited).
    /// </summary>
    /// <param name="user">The user config.</param>
    /// <param name="config">Plugin config.</param>
    /// <param name="local">The local date.</param>
    /// <returns>The resolved preset, or null.</returns>
    public static Preset? ResolvePreset(UserLimitConfig user, PluginConfiguration config, DateTime local)
    {
        var key = DateKey(local);
        var over = user.DateOverrides?.FirstOrDefault(d =>
            string.Equals(d.Date, key, StringComparison.Ordinal));

        var presetId = !string.IsNullOrEmpty(over?.PresetId)
            ? over!.PresetId
            : user.GetWeekdayPresetId(local.DayOfWeek);

        if (string.IsNullOrEmpty(presetId))
        {
            return null;
        }

        return config.Presets.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Computes remaining playable seconds for a session, honoring the most-restrictive
    /// of the applicable (non-null) session / daily / window caps, plus bonus.
    /// </summary>
    /// <param name="preset">The active preset (null = unlimited).</param>
    /// <param name="state">The user's daily state.</param>
    /// <param name="window">The current window.</param>
    /// <param name="sessionSeconds">Seconds watched by the session being evaluated.</param>
    /// <returns>The computed remaining result.</returns>
    public static RemainingResult Compute(Preset? preset, DailyState state, Window window, long sessionSeconds)
    {
        if (preset is null)
        {
            return RemainingResult.Unlimited;
        }

        long? best = null;
        string binding = string.Empty;

        void Consider(int? capMinutes, long used, long bonus, string name)
        {
            if (capMinutes is null)
            {
                return;
            }

            var remaining = ((long)capMinutes.Value * 60) + bonus - used;
            if (best is null || remaining < best.Value)
            {
                best = remaining;
                binding = name;
            }
        }

        // Bonus applies to daily, session, AND the currently-active window (§5.1).
        Consider(preset.SessionCapMinutes, sessionSeconds, state.SessionBonusSeconds, "session");
        Consider(preset.DailyCapMinutes, state.SecondsWatchedTotal, state.DailyBonusSeconds, "daily");
        Consider(
            WindowCap(preset, window),
            state.GetWindowSeconds(window),
            state.DailyBonusSeconds,
            "window");

        if (best is null)
        {
            return RemainingResult.Unlimited;
        }

        return new RemainingResult(true, best.Value, binding);
    }

    /// <summary>Gets the cap (minutes) for a window from a preset.</summary>
    /// <param name="preset">The preset.</param>
    /// <param name="window">The window.</param>
    /// <returns>The window cap in minutes, or null.</returns>
    public static int? WindowCap(Preset preset, Window window) => window switch
    {
        Window.Morning => preset.MorningCapMinutes,
        Window.Afternoon => preset.AfternoonCapMinutes,
        Window.Evening => preset.EveningCapMinutes,
        _ => null,
    };
}

/// <summary>Result of a remaining-time computation.</summary>
public readonly struct RemainingResult
{
    /// <summary>A result representing "no applicable limits".</summary>
    public static readonly RemainingResult Unlimited = new(false, long.MaxValue, string.Empty);

    /// <summary>Initializes a new instance of the <see cref="RemainingResult"/> struct.</summary>
    /// <param name="hasLimit">Whether any cap applies.</param>
    /// <param name="remainingSeconds">Effective remaining seconds.</param>
    /// <param name="bindingCap">Which cap is binding.</param>
    public RemainingResult(bool hasLimit, long remainingSeconds, string bindingCap)
    {
        HasLimit = hasLimit;
        RemainingSeconds = remainingSeconds;
        BindingCap = bindingCap;
    }

    /// <summary>Gets a value indicating whether any cap applies.</summary>
    public bool HasLimit { get; }

    /// <summary>Gets the effective (most-restrictive) remaining seconds.</summary>
    public long RemainingSeconds { get; }

    /// <summary>Gets which cap is binding: session / daily / window (or empty).</summary>
    public string BindingCap { get; }
}
