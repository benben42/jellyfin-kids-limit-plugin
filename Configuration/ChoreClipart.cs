using System;
using System.Collections.Generic;
using System.Reflection;

namespace Jellyfin.Plugin.KidsLimit.Configuration;

/// <summary>
/// The fixed catalog of built-in chore placeholder clipart. The keys map 1:1 to the
/// <c>Web/clipart/{key}.svg</c> embedded resources served by
/// <c>GET /KidsLimit/clipart/{key}</c>, and double as the allow-list that stops the
/// endpoint from being used to fetch arbitrary embedded resources.
/// </summary>
public static class ChoreClipart
{
    /// <summary>
    /// Gets the valid clipart keys. Keep this in sync with the SVGs under <c>Web/clipart</c>
    /// and the gallery list in <c>configPage.html</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>
    {
        "tidy-toys",
        "make-bed",
        "clothes-basket",
        "set-table",
        "water-plants",
        "books-shelf",
        "wipe-table",
        "feed-pet",
        "brush-teeth",
        "help-baby",
    };

    /// <summary>
    /// Resolves the embedded-resource name for a clipart key, or null if the key is unknown
    /// or the resource is missing. Tolerant of the MSBuild resource-name mangling that can turn
    /// the <c>-</c> in a file name into <c>_</c>, so lookups don't silently 404.
    /// </summary>
    /// <param name="key">The clipart key, e.g. "make-bed".</param>
    /// <returns>The manifest resource name, or null.</returns>
    public static string? ResourceName(string key)
    {
        if (string.IsNullOrEmpty(key) || !Keys.Contains(key))
        {
            return null;
        }

        var suffixHyphen = ".clipart." + key + ".svg";
        var suffixUnderscore = ".clipart." + key.Replace('-', '_') + ".svg";
        foreach (var name in typeof(ChoreClipart).GetTypeInfo().Assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(suffixHyphen, StringComparison.Ordinal) ||
                name.EndsWith(suffixUnderscore, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }
}
