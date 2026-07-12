namespace Jellyfin.Plugin.KidsLimit.Configuration;

/// <summary>
/// A parent-defined chore the kid can earn coins for. XML-serializer friendly.
/// </summary>
public class Chore
{
    /// <summary>Gets or sets the stable identifier referenced by claims and ledger entries.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-friendly name, e.g. "Unload dishwasher".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the emoji shown on the kid's tile, e.g. "🛏️".</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the placeholder clipart key shown on the kid's tile when the chore has
    /// no photo yet, e.g. "make-bed". Empty falls back to <see cref="Icon"/>. The key must be
    /// one of the built-in clipart served by <c>GET /KidsLimit/clipart/{key}</c>.
    /// </summary>
    public string Clipart { get; set; } = string.Empty;

    /// <summary>Gets or sets how many coins the chore pays.</summary>
    public int Coins { get; set; } = 1;

    /// <summary>Gets or sets how many times per day the chore can be claimed. 0 = unlimited.</summary>
    public int MaxPerDay { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether the chore is currently offered.</summary>
    public bool Enabled { get; set; } = true;
}
