using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.KidsLimit.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.KidsLimit;

/// <summary>
/// The Kids Watch-Time Limit plugin entry point. Registers configuration and the two
/// web pages (admin config + parent dashboard).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        MigrateConfiguration();
    }

    /// <summary>Gets the singleton instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Kids Watch-Time Limit";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a1e5c7f2-3b4d-4e6a-9c8b-2d1f0e3a5b7c");

    /// <inheritdoc />
    public override string Description =>
        "Enforces cumulative daily watch-time limits for kids: per-user, per-weekday, " +
        "per-window, with parent bonus time and a dashboard.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "KidsLimitConfig",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        },
        new PluginPageInfo
        {
            Name = "KidsLimitDashboard",
            EmbeddedResourcePath = GetType().Namespace + ".Web.dashboard.html",

            // Surface the parent dashboard directly in the admin sidebar so parents don't
            // have to dig through Plugins -> this plugin. The dashboard links to settings.
            EnableInMainMenu = true,
            DisplayName = "Kids Watch-Time",
            MenuIcon = "schedule",
        },
    };

    /// <summary>
    /// Finds the per-user limit config for a user id, or null.
    /// </summary>
    /// <param name="userId">The user id (any casing / format).</param>
    /// <returns>The user config or null.</returns>
    public UserLimitConfig? FindUser(string userId)
    {
        var norm = Normalize(userId);
        foreach (var u in Configuration.Users)
        {
            if (Normalize(u.UserId) == norm)
            {
                return u;
            }
        }

        return null;
    }

    /// <summary>
    /// One-time configuration migration run at startup. Cleans up the built-in-preset
    /// duplication caused by older builds (which seeded defaults in the config constructor,
    /// so the XML serializer appended a fresh copy on every restart) and seeds the built-in
    /// presets exactly once on a genuinely fresh install.
    /// </summary>
    private void MigrateConfiguration()
    {
        var config = Configuration;
        var changed = false;

        config.Presets ??= new List<Preset>();

        // De-duplicate presets by id (keeping the first occurrence). Existing installs may
        // already have accumulated several copies of each built-in from the old bug.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<Preset>(config.Presets.Count);
        foreach (var preset in config.Presets)
        {
            var key = Normalize(preset.Id);
            if (!string.IsNullOrEmpty(key) && !seen.Add(key))
            {
                changed = true; // a duplicate id — drop it
                continue;
            }

            deduped.Add(preset);
        }

        if (changed)
        {
            config.Presets = deduped;
        }

        // Seed the built-in presets only on a genuinely fresh install (never seeded before
        // and no presets present). This also stops future restarts from re-adding them.
        if (!config.Initialized)
        {
            if (config.Presets.Count == 0)
            {
                config.Presets = PluginConfiguration.DefaultPresets();
            }

            config.Initialized = true;
            changed = true;
        }

        if (changed)
        {
            SaveConfiguration();
        }
    }

    private static string Normalize(string id) =>
        (id ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLower(CultureInfo.InvariantCulture);
}
