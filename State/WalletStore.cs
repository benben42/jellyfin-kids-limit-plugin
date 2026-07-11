using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.State;

/// <summary>
/// Thread-safe persistence for <see cref="UserWallet"/>. Wallet transactions are rare
/// (a handful per day) so every mutation is written through to disk immediately —
/// earned coins must survive a crash.
/// </summary>
public class WalletStore
{
    private const int MaxLedgerEntries = 500;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly ILogger<WalletStore> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, UserWallet> _wallets = new(StringComparer.Ordinal);

    private string _dataDir = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="WalletStore"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public WalletStore(ILogger<WalletStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Initializes the on-disk location. Must be called once at startup.</summary>
    /// <param name="dataDir">The plugin data folder.</param>
    public void Initialize(string dataDir)
    {
        lock (_sync)
        {
            _dataDir = dataDir;
            Directory.CreateDirectory(WalletDir());
        }
    }

    /// <summary>Returns a snapshot copy of a user's wallet (safe to read without the lock).</summary>
    /// <param name="userId">The user id (Guid "N" form).</param>
    /// <returns>A detached copy of the wallet.</returns>
    public UserWallet GetSnapshot(string userId)
    {
        lock (_sync)
        {
            return Clone(GetOrCreateLocked(userId));
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the wallet under the store lock, trims the
    /// ledger, and writes the wallet through to disk.
    /// </summary>
    /// <param name="userId">The user id (Guid "N" form).</param>
    /// <param name="action">Mutation to apply.</param>
    /// <returns>A snapshot of the wallet after the mutation.</returns>
    public UserWallet Mutate(string userId, Action<UserWallet> action)
    {
        UserWallet snapshot;
        lock (_sync)
        {
            var wallet = GetOrCreateLocked(userId);
            action(wallet);

            if (wallet.CoinBalance < 0)
            {
                wallet.CoinBalance = 0;
            }

            if (wallet.Ledger.Count > MaxLedgerEntries)
            {
                wallet.Ledger = wallet.Ledger
                    .Skip(wallet.Ledger.Count - MaxLedgerEntries)
                    .ToList();
            }

            snapshot = Clone(wallet);
        }

        Write(snapshot);
        return snapshot;
    }

    private UserWallet GetOrCreateLocked(string userId)
    {
        if (!_wallets.TryGetValue(userId, out var wallet))
        {
            wallet = Load(userId) ?? new UserWallet { UserId = userId };
            _wallets[userId] = wallet;
        }

        return wallet;
    }

    private UserWallet? Load(string userId)
    {
        try
        {
            var path = WalletPath(userId);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<UserWallet>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed loading wallet for {UserId}", userId);
            return null;
        }
    }

    private void Write(UserWallet wallet)
    {
        try
        {
            var path = WalletPath(wallet.UserId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(wallet, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KidsLimit: failed writing wallet for {UserId}", wallet.UserId);
        }
    }

    private static UserWallet Clone(UserWallet w) => new()
    {
        UserId = w.UserId,
        CoinBalance = w.CoinBalance,
        RedeemDate = w.RedeemDate,
        CoinsRedeemedToday = w.CoinsRedeemedToday,
        PendingClaims = w.PendingClaims.Select(c => new PendingClaim
        {
            Id = c.Id,
            ChoreId = c.ChoreId,
            ChoreName = c.ChoreName,
            Coins = c.Coins,
            ClaimedAtUtc = c.ClaimedAtUtc,
            Date = c.Date,
            ActionKey = c.ActionKey,
        }).ToList(),
        Ledger = w.Ledger.Select(e => new LedgerEntry
        {
            AtUtc = e.AtUtc,
            Date = e.Date,
            DeltaCoins = e.DeltaCoins,
            Type = e.Type,
            ChoreId = e.ChoreId,
            Note = e.Note,
        }).ToList(),
    };

    private string WalletDir() => Path.Combine(_dataDir, "wallet");

    private string WalletPath(string userId)
    {
        // User ids are Guids, but be defensive against path traversal.
        var chars = userId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray();
        var cleaned = new string(chars);
        var name = string.IsNullOrEmpty(cleaned)
            ? "unknown"
            : cleaned.ToLower(CultureInfo.InvariantCulture);
        return Path.Combine(WalletDir(), name + ".json");
    }
}
