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

    private static string Normalize(string id) =>
        (id ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLower(CultureInfo.InvariantCulture);
}
