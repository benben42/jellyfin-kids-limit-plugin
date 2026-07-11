using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.KidsLimit.State;

/// <summary>
/// Per-user persistent reward wallet (REWARDS.md). Unlike <see cref="DailyState"/> this
/// never resets at midnight; coins live until spent. Persisted as JSON in the plugin
/// data folder.
/// </summary>
public class UserWallet
{
    /// <summary>Gets or sets the Jellyfin user id (Guid "N" form).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the current coin balance.</summary>
    public int CoinBalance { get; set; }

    /// <summary>Gets or sets the local date (yyyy-MM-dd) the redeem counter belongs to.</summary>
    public string RedeemDate { get; set; } = string.Empty;

    /// <summary>Gets or sets coins redeemed on <see cref="RedeemDate"/> (daily redeem cap).</summary>
    public int CoinsRedeemedToday { get; set; }

    /// <summary>Gets or sets chore claims waiting for parent approval.</summary>
    public List<PendingClaim> PendingClaims { get; set; } = new();

    /// <summary>Gets or sets the transaction history, oldest first, capped in length.</summary>
    public List<LedgerEntry> Ledger { get; set; } = new();
}

/// <summary>A chore claim made by the kid, awaiting parent approval.</summary>
public class PendingClaim
{
    /// <summary>Gets or sets the claim id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the chore id.</summary>
    public string ChoreId { get; set; } = string.Empty;

    /// <summary>Gets or sets the chore name at claim time (display; chores may be edited later).</summary>
    public string ChoreName { get; set; } = string.Empty;

    /// <summary>Gets or sets the coin value at claim time.</summary>
    public int Coins { get; set; }

    /// <summary>Gets or sets when the claim was made (UTC).</summary>
    public DateTime ClaimedAtUtc { get; set; }

    /// <summary>Gets or sets the local date (yyyy-MM-dd) of the claim, for max-per-day counting.</summary>
    public string Date { get; set; } = string.Empty;
}

/// <summary>One wallet transaction.</summary>
public class LedgerEntry
{
    /// <summary>Gets or sets when the transaction happened (UTC).</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>Gets or sets the local date (yyyy-MM-dd), for max-per-day counting.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets the coin delta (positive = earned, negative = spent).</summary>
    public int DeltaCoins { get; set; }

    /// <summary>Gets or sets the entry kind: Earn, Claim, Redeem, Adjust, Refund, Reject.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the chore id, when the entry stems from a chore.</summary>
    public string ChoreId { get; set; } = string.Empty;

    /// <summary>Gets or sets a human-readable note ("Dishwasher", "Watched Toy Story", …).</summary>
    public string Note { get; set; } = string.Empty;
}
