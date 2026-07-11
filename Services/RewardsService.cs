using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Jellyfin.Plugin.KidsLimit.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Services;

/// <summary>
/// Orchestrates the chore rewards system (REWARDS.md): earning coins, kid claims with
/// parent approval, redeeming coins into today's bonus time, the midnight refund of
/// redeemed-but-unwatched time, ntfy notifications, and best-effort auto-play.
/// </summary>
public sealed class RewardsService
{
    /// <summary>Ledger type: parent credited coins directly (or approved a claim).</summary>
    public const string TypeEarn = "Earn";

    /// <summary>Ledger type: coins spent on watch time.</summary>
    public const string TypeRedeem = "Redeem";

    /// <summary>Ledger type: manual parent correction.</summary>
    public const string TypeAdjust = "Adjust";

    /// <summary>Ledger type: midnight refund of redeemed-but-unwatched time.</summary>
    public const string TypeRefund = "Refund";

    /// <summary>Ledger type: a claim the parent rejected (zero delta, kept for the record).</summary>
    public const string TypeReject = "Reject";

    private readonly WalletStore _wallets;
    private readonly StateStore _store;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly HardBlockEnforcer _enforcer;
    private readonly NotificationService _notifications;
    private readonly ILogger<RewardsService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RewardsService"/> class.
    /// </summary>
    /// <param name="wallets">Wallet store.</param>
    /// <param name="store">Daily state store.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="enforcer">Hard-block enforcer.</param>
    /// <param name="notifications">Push-notification fan-out.</param>
    /// <param name="logger">Logger.</param>
    public RewardsService(
        WalletStore wallets,
        StateStore store,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        HardBlockEnforcer enforcer,
        NotificationService notifications,
        ILogger<RewardsService> logger)
    {
        _wallets = wallets;
        _store = store;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _enforcer = enforcer;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>Seconds one coin is worth under the given configuration.</summary>
    /// <param name="config">Plugin config.</param>
    /// <returns>Seconds per coin.</returns>
    public static long CoinSeconds(PluginConfiguration config) =>
        Math.Max(1, config.CoinMinutes) * 60L;

    /// <summary>
    /// Credits coins to a wallet, clamping at the bank cap. Used for direct parent
    /// grants, claim approvals and refunds.
    /// </summary>
    /// <param name="config">Plugin config.</param>
    /// <param name="userId">User id (Guid "N").</param>
    /// <param name="coins">Coins to add (may be negative for an Adjust).</param>
    /// <param name="type">Ledger type.</param>
    /// <param name="choreId">Originating chore id, or empty.</param>
    /// <param name="note">Human-readable note.</param>
    /// <returns>The wallet after the credit.</returns>
    public UserWallet Credit(
        PluginConfiguration config,
        string userId,
        int coins,
        string type,
        string choreId,
        string note)
    {
        var today = LimitCalculator.DateKey(DateTime.Now);
        return _wallets.Mutate(userId, w =>
        {
            var applied = coins;
            if (coins > 0 && config.BankCapCoins > 0)
            {
                applied = Math.Min(coins, Math.Max(0, config.BankCapCoins - w.CoinBalance));
            }

            w.CoinBalance += applied;
            w.Ledger.Add(new LedgerEntry
            {
                AtUtc = DateTime.UtcNow,
                Date = today,
                DeltaCoins = applied,
                Type = type,
                ChoreId = choreId,
                Note = applied == coins ? note : note + " (bank full)",
            });
        });
    }

    /// <summary>How many times a chore was already claimed (approved or pending) today.</summary>
    /// <param name="wallet">The wallet to inspect.</param>
    /// <param name="choreId">The chore.</param>
    /// <param name="today">Today's local date key.</param>
    /// <returns>Count of today's claims for the chore.</returns>
    public static int ClaimedToday(UserWallet wallet, string choreId, string today)
    {
        var approved = wallet.Ledger.Count(e =>
            string.Equals(e.Date, today, StringComparison.Ordinal) &&
            string.Equals(e.ChoreId, choreId, StringComparison.Ordinal) &&
            string.Equals(e.Type, TypeEarn, StringComparison.Ordinal));
        var pending = wallet.PendingClaims.Count(c =>
            string.Equals(c.Date, today, StringComparison.Ordinal) &&
            string.Equals(c.ChoreId, choreId, StringComparison.Ordinal));
        return approved + pending;
    }

    /// <summary>
    /// Files a kid claim for a chore (pending parent approval) and pings the parent's
    /// phone via ntfy when configured.
    /// </summary>
    /// <param name="config">Plugin config.</param>
    /// <param name="userId">User id (Guid "N").</param>
    /// <param name="userName">User display name (for the notification).</param>
    /// <param name="choreId">The chore being claimed.</param>
    /// <returns>The new pending claim, or null when the claim is not allowed.</returns>
    public PendingClaim? Claim(PluginConfiguration config, string userId, string userName, string choreId)
    {
        var chore = config.Chores.FirstOrDefault(c =>
            c.Enabled && string.Equals(c.Id, choreId, StringComparison.Ordinal));
        if (chore is null)
        {
            return null;
        }

        var today = LimitCalculator.DateKey(DateTime.Now);
        PendingClaim? claim = null;

        _wallets.Mutate(userId, w =>
        {
            if (chore.MaxPerDay > 0 && ClaimedToday(w, choreId, today) >= chore.MaxPerDay)
            {
                return;
            }

            claim = new PendingClaim
            {
                Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                ChoreId = chore.Id,
                ChoreName = chore.Name,
                Coins = chore.Coins,
                ClaimedAtUtc = DateTime.UtcNow,
                Date = today,
            };
            w.PendingClaims.Add(claim);
        });

        if (claim is not null)
        {
            _notifications.Send(
                config,
                $"{userName}: {chore.Name}",
                $"Claims {chore.Coins} coin(s) — approve on the dashboard.");
        }

        return claim;
    }

    /// <summary>Approves a pending claim, crediting its coins.</summary>
    /// <param name="config">Plugin config.</param>
    /// <param name="userId">User id (Guid "N").</param>
    /// <param name="claimId">The claim to approve.</param>
    /// <returns>True when the claim existed.</returns>
    public bool ApproveClaim(PluginConfiguration config, string userId, string claimId)
    {
        PendingClaim? claim = null;
        _wallets.Mutate(userId, w =>
        {
            claim = w.PendingClaims.FirstOrDefault(c => string.Equals(c.Id, claimId, StringComparison.Ordinal));
            if (claim is not null)
            {
                w.PendingClaims.Remove(claim);
            }
        });

        if (claim is null)
        {
            return false;
        }

        Credit(config, userId, claim.Coins, TypeEarn, claim.ChoreId, claim.ChoreName);
        return true;
    }

    /// <summary>Rejects a pending claim (recorded in the ledger with zero delta).</summary>
    /// <param name="userId">User id (Guid "N").</param>
    /// <param name="claimId">The claim to reject.</param>
    /// <returns>True when the claim existed.</returns>
    public bool RejectClaim(string userId, string claimId)
    {
        var found = false;
        _wallets.Mutate(userId, w =>
        {
            var claim = w.PendingClaims.FirstOrDefault(c => string.Equals(c.Id, claimId, StringComparison.Ordinal));
            if (claim is null)
            {
                return;
            }

            found = true;
            w.PendingClaims.Remove(claim);
            w.Ledger.Add(new LedgerEntry
            {
                AtUtc = DateTime.UtcNow,
                Date = claim.Date,
                DeltaCoins = 0,
                Type = TypeReject,
                ChoreId = claim.ChoreId,
                Note = claim.ChoreName,
            });
        });
        return found;
    }

    /// <summary>
    /// Redeems coins into today's bonus time. Enforces the balance and the daily redeem
    /// cap, grants the bonus (marked wallet-sourced for the midnight refund) and lifts
    /// any hard block right away.
    /// </summary>
    /// <param name="config">Plugin config.</param>
    /// <param name="userId">User id (Guid "N").</param>
    /// <param name="coins">Coins to spend (must be positive).</param>
    /// <param name="note">Ledger note ("Toy Story", "Extra time", …).</param>
    /// <returns>The redeem outcome.</returns>
    public RedeemOutcome Redeem(PluginConfiguration config, string userId, int coins, string note)
    {
        if (coins <= 0)
        {
            return new RedeemOutcome { Error = "invalid" };
        }

        var localNow = DateTime.Now;
        var today = LimitCalculator.DateKey(localNow);
        string? error = null;

        var wallet = _wallets.Mutate(userId, w =>
        {
            if (!string.Equals(w.RedeemDate, today, StringComparison.Ordinal))
            {
                w.RedeemDate = today;
                w.CoinsRedeemedToday = 0;
            }

            if (w.CoinBalance < coins)
            {
                error = "balance";
                return;
            }

            if (config.MaxRedeemCoinsPerDay > 0 &&
                w.CoinsRedeemedToday + coins > config.MaxRedeemCoinsPerDay)
            {
                error = "dailycap";
                return;
            }

            w.CoinBalance -= coins;
            w.CoinsRedeemedToday += coins;
            w.Ledger.Add(new LedgerEntry
            {
                AtUtc = DateTime.UtcNow,
                Date = today,
                DeltaCoins = -coins,
                Type = TypeRedeem,
                Note = note,
            });
        });

        if (error is not null)
        {
            return new RedeemOutcome { Error = error, CoinBalance = wallet.CoinBalance };
        }

        var seconds = coins * CoinSeconds(config);
        _store.GrantBonus(userId, today, seconds, fromWallet: true);
        _ = _enforcer.ReconcileAsync(config, today, localNow);

        return new RedeemOutcome
        {
            CoinsSpent = coins,
            SecondsGranted = seconds,
            CoinBalance = wallet.CoinBalance,
        };
    }

    /// <summary>
    /// Midnight-rollover handler: refunds redeemed-but-unwatched seconds back to the
    /// wallet as whole coins. Consumption is attributed to parent-granted bonus first
    /// (generous to the kid); see REWARDS.md for the formula. Invoked from the state
    /// store's rollover under its lock — must not call back into <see cref="StateStore"/>.
    /// </summary>
    /// <param name="finished">Snapshot of the finishing day.</param>
    public void RefundFinishedDay(DailyState finished)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || finished.RedeemedSeconds <= 0)
            {
                return;
            }

            long unusedBonus;
            var cfg = Plugin.Instance?.FindUser(finished.UserId);
            var preset = cfg is null || !DateTime.TryParseExact(
                    finished.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? null
                : LimitCalculator.ResolvePreset(cfg, config, day);

            if (preset?.DailyCapMinutes is int dailyCap)
            {
                var bonusConsumed = Math.Max(0, finished.SecondsWatchedTotal - (dailyCap * 60L));
                unusedBonus = Math.Max(0, finished.DailyBonusSeconds - bonusConsumed);
            }
            else
            {
                // No daily cap => the bonus was never needed; refund everything redeemed.
                unusedBonus = finished.RedeemedSeconds;
            }

            var refundSeconds = Math.Min(finished.RedeemedSeconds, unusedBonus);
            var refundCoins = (int)(refundSeconds / CoinSeconds(config));
            if (refundCoins <= 0)
            {
                return;
            }

            Credit(config, finished.UserId, refundCoins, TypeRefund, string.Empty, "Unused time from " + finished.Date);
            _logger.LogInformation(
                "KidsLimit: refunded {Coins} unused coin(s) to {UserId} for {Date}.",
                refundCoins,
                finished.UserId,
                finished.Date);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: redeem refund failed for {UserId}", finished.UserId);
        }
    }

    /// <summary>
    /// Resolves a reference title for the kid page: display name, coin cost, and the
    /// concrete item to play. For a series the cost is the median episode runtime and
    /// the playable item is a random episode.
    /// </summary>
    /// <param name="config">Plugin config.</param>
    /// <param name="itemId">The configured library item id.</param>
    /// <returns>The resolved info, or null when the item no longer exists.</returns>
    public ReferenceTitle? ResolveReferenceTitle(PluginConfiguration config, string itemId)
    {
        if (!Guid.TryParse(itemId, out var guid))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(guid);
        if (item is null)
        {
            return null;
        }

        long? runtimeTicks = item.RunTimeTicks;
        if (item is Series series)
        {
            var runtimes = series.GetRecursiveChildren()
                .OfType<Episode>()
                .Where(e => e.RunTimeTicks.HasValue)
                .Select(e => e.RunTimeTicks!.Value)
                .OrderBy(t => t)
                .ToList();
            if (runtimes.Count > 0)
            {
                runtimeTicks = runtimes[runtimes.Count / 2];
            }
        }

        if (runtimeTicks is null or <= 0)
        {
            return null;
        }

        var minutes = (int)Math.Round(TimeSpan.FromTicks(runtimeTicks.Value).TotalMinutes);
        var coins = Math.Max(1, (int)Math.Ceiling(minutes / (double)Math.Max(1, config.CoinMinutes)));

        return new ReferenceTitle
        {
            ItemId = item.Id,
            Name = item.Name,
            IsSeries = item is Series,
            RuntimeMinutes = minutes,
            CoinCost = coins,
        };
    }

    /// <summary>
    /// Best-effort: starts playback of an item on one of the user's active sessions.
    /// For a series a random episode is chosen. Clients that ignore remote-control
    /// commands (some TV apps) make this fail silently — the caller reports that back
    /// to the kid page so it can show "open Jellyfin" guidance instead.
    /// </summary>
    /// <param name="userGuid">The user.</param>
    /// <param name="itemId">The item (movie/episode/series).</param>
    /// <returns>True when a session accepted the Play command.</returns>
    public async Task<bool> TryPlayAsync(Guid userGuid, Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return false;
        }

        if (item is Series series)
        {
            var episodes = series.GetRecursiveChildren().OfType<Episode>().ToList();
            if (episodes.Count == 0)
            {
                return false;
            }

            item = episodes[Random.Shared.Next(episodes.Count)];
        }

        var sessions = _sessionManager.Sessions
            .Where(s => s.UserId == userGuid)
            .OrderByDescending(s => s.LastActivityDate)
            .ToList();

        foreach (var session in sessions)
        {
            try
            {
                await _sessionManager.SendPlayCommand(
                    null,
                    session.Id,
                    new PlayRequest
                    {
                        ItemIds = new[] { item.Id },
                        PlayCommand = PlayCommand.PlayNow,
                        ControllingUserId = userGuid,
                    },
                    CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "KidsLimit: Play command rejected by session {Session} (client='{Client}').",
                    session.Id,
                    session.Client);
            }
        }

        return false;
    }

}

/// <summary>Result of a redeem attempt.</summary>
public class RedeemOutcome
{
    /// <summary>Gets or sets the failure reason: null = success, "balance", "dailycap", "invalid".</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the coins actually spent.</summary>
    public int CoinsSpent { get; set; }

    /// <summary>Gets or sets the bonus seconds granted.</summary>
    public long SecondsGranted { get; set; }

    /// <summary>Gets or sets the balance after the attempt.</summary>
    public int CoinBalance { get; set; }
}

/// <summary>A resolved reference title for the kid page.</summary>
public class ReferenceTitle
{
    /// <summary>Gets or sets the library item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the item is a series (cost = one episode).</summary>
    public bool IsSeries { get; set; }

    /// <summary>Gets or sets the runtime in minutes (median episode runtime for series).</summary>
    public int RuntimeMinutes { get; set; }

    /// <summary>Gets or sets the coin cost to watch one of it.</summary>
    public int CoinCost { get; set; }
}
