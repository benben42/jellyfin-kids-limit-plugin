using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Jellyfin.Plugin.KidsLimit.Configuration;

/// <summary>
/// The fixed catalog of built-in chore tile art. The keys map 1:1 to the
/// <c>Web/clipart/{key}.{png|webp|jpg|svg}</c> embedded resources served by
/// <c>GET /KidsLimit/clipart/{key}</c>, and double as the allow-list that stops the
/// endpoint from being used to fetch arbitrary embedded resources.
/// <para>
/// A key may be catalogued before its art exists — <see cref="Available"/> reports only the
/// entries that actually resolve to an embedded file, so the config-page pickers never offer
/// a picture that would 404. Dropping <c>Web/clipart/{key}.png</c> into the repo is therefore
/// the whole job of adding art for a catalogued key; see <c>docs/ADDING-CHORES.md</c>.
/// </para>
/// <para>
/// Raster formats win over <c>.svg</c> for the same key (see <c>Formats</c>), so a new
/// clay-render PNG dropped next to a legacy line-art SVG replaces it without a code change
/// and without breaking chores already configured against that key.
/// </para>
/// </summary>
public static class ChoreClipart
{
    /// <summary>
    /// Candidate file formats in resolution order: raster first, so newer art supersedes the
    /// legacy line-art SVG that may still sit next to it under the same key.
    /// </summary>
    private static readonly (string Extension, string ContentType)[] Formats =
    {
        (".png", "image/png"),
        (".webp", "image/webp"),
        (".jpg", "image/jpeg"),
        (".svg", "image/svg+xml"),
    };

    private static readonly Lazy<IReadOnlyDictionary<string, ResolvedClipart>> ResolvedLazy =
        new(BuildResolved);

    /// <summary>
    /// Gets the catalogued clipart, in the order the pickers should offer it: the current
    /// chore set first, then the legacy line-art symbols kept for existing installs.
    /// Keep this in sync with the files under <c>Web/clipart</c>.
    /// </summary>
    public static IReadOnlyList<ClipartEntry> Catalog { get; } = new List<ClipartEntry>
    {
        // Current set — clay renders (docs/CHORE-IMAGE-PROMPTS.md).
        new("make-bed", "Make the bed"),
        new("clothes-basket", "Dirty clothes in the basket"),
        new("plate-in-sink", "Plate in the sink"),
        new("tidy-toys", "Tidy your room"),
        new("unload-dishwasher", "Unload the dishwasher"),
        new("tidy-craft-table", "Tidy the craft table"),
        new("put-away-clothes", "Put away clean clothes"),
        new("play-brother", "Play with little brother"),
        new("read-to-brother", "Read a book to your brother"),

        // Legacy line-art symbols (Mulberry Symbols, CC BY-SA 4.0).
        new("set-table", "Set the table"),
        new("water-plants", "Water the plants"),
        new("books-shelf", "Books on the shelf"),
        new("wipe-table", "Wipe the table"),
        new("feed-pet", "Feed the pet"),
        new("brush-teeth", "Brush your teeth"),
        new("help-baby", "Help with the baby"),
    };

    /// <summary>
    /// Gets the valid clipart keys — the allow-list guarding the clipart endpoint. Includes
    /// keys whose art is not embedded yet; use <see cref="Available"/> for what can be shown.
    /// </summary>
    public static IReadOnlySet<string> Keys { get; } =
        Catalog.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Gets the catalogued entries that have art embedded, in catalog order. This is what the
    /// config page and the phone page offer in their picture pickers.
    /// </summary>
    /// <returns>The offerable entries.</returns>
    public static IReadOnlyList<ClipartEntry> Available() =>
        Catalog.Where(e => ResolvedLazy.Value.ContainsKey(e.Key)).ToList();

    /// <summary>
    /// Resolves the embedded resource backing a clipart key.
    /// </summary>
    /// <param name="key">The clipart key, e.g. "make-bed".</param>
    /// <returns>The resource name and content type, or null if the key is unknown or has no art.</returns>
    public static ResolvedClipart? Resolve(string key)
    {
        if (string.IsNullOrEmpty(key) || !Keys.Contains(key))
        {
            return null;
        }

        return ResolvedLazy.Value.TryGetValue(key, out var resolved) ? resolved : null;
    }

    /// <summary>
    /// Names the visual style a key's art is drawn in, so the pickers can group the two sets
    /// and a parent does not accidentally mix them on one kid page. Derived from the file
    /// format rather than declared, so it cannot drift from what is actually embedded.
    /// </summary>
    /// <param name="key">The clipart key.</param>
    /// <returns>"clay" for raster art, "symbol" for the legacy SVG line art, "" if unresolved.</returns>
    public static string StyleOf(string key)
    {
        var resolved = Resolve(key);
        if (resolved is null)
        {
            return string.Empty;
        }

        return resolved.ContentType == "image/svg+xml" ? "symbol" : "clay";
    }

    /// <summary>
    /// Scans the assembly once and maps each catalogued key to its best-ranked embedded file.
    /// Tolerant of the MSBuild resource-name mangling that can turn the <c>-</c> in a file name
    /// into <c>_</c>, so lookups don't silently 404.
    /// </summary>
    private static IReadOnlyDictionary<string, ResolvedClipart> BuildResolved()
    {
        var map = new Dictionary<string, ResolvedClipart>(StringComparer.Ordinal);
        var names = typeof(ChoreClipart).GetTypeInfo().Assembly.GetManifestResourceNames();

        foreach (var key in Keys)
        {
            var mangled = key.Replace('-', '_');

            // Formats are in preference order, so the first hit wins outright.
            foreach (var (extension, contentType) in Formats)
            {
                var suffixHyphen = ".clipart." + key + extension;
                var suffixUnderscore = ".clipart." + mangled + extension;

                var match = names.FirstOrDefault(n =>
                    n.EndsWith(suffixHyphen, StringComparison.Ordinal) ||
                    n.EndsWith(suffixUnderscore, StringComparison.Ordinal));
                if (match is not null)
                {
                    map[key] = new ResolvedClipart(match, contentType);
                    break;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// A catalogued clipart: the key referenced by <see cref="Chore.Clipart"/> and the
    /// human-readable label shown next to it in the parent-facing pickers.
    /// </summary>
    /// <param name="Key">The clipart key, e.g. "make-bed".</param>
    /// <param name="Label">The label shown in the picker.</param>
    public sealed record ClipartEntry(string Key, string Label);

    /// <summary>
    /// A clipart key resolved to a concrete embedded file.
    /// </summary>
    /// <param name="Resource">The manifest resource name.</param>
    /// <param name="ContentType">The MIME type to serve it as.</param>
    public sealed record ResolvedClipart(string Resource, string ContentType);
}
