using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Jellyfin.Plugin.KidsLimit.Services;
using Jellyfin.Plugin.KidsLimit.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Api;

/// <summary>
/// Rewards REST API (REWARDS.md). Two auth schemes:
/// parent endpoints reuse the shared-secret <c>BonusApiToken</c> (query <c>token</c> or
/// <c>X-KidsLimit-Token</c> header) exactly like <see cref="KidsLimitController"/>;
/// kid endpoints use the per-user <c>KidToken</c>, which only allows self-service
/// actions (view own wallet, claim a chore, redeem coins).
/// </summary>
[ApiController]
[Route("KidsLimit")]
[AllowAnonymous]
public class RewardsController : ControllerBase
{
    private const int LedgerTailLength = 40;

    private readonly RewardsService _rewards;
    private readonly WalletStore _wallets;
    private readonly StateStore _store;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly NotificationService _notifications;
    private readonly ILogger<RewardsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RewardsController"/> class.
    /// </summary>
    /// <param name="rewards">Rewards service.</param>
    /// <param name="wallets">Wallet store.</param>
    /// <param name="store">Daily state store.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userDataManager">User data manager (play dates for the watch-grid ordering).</param>
    /// <param name="notifications">Push-notification fan-out.</param>
    /// <param name="logger">Logger.</param>
    public RewardsController(
        RewardsService rewards,
        WalletStore wallets,
        StateStore store,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        NotificationService notifications,
        ILogger<RewardsController> logger)
    {
        _rewards = rewards;
        _wallets = wallets;
        _store = store;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _notifications = notifications;
        _logger = logger;
    }

    private PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // ---------------------------------------------------------------- parent API

    /// <summary>Gets a user's wallet: balance, pending claims and recent ledger.</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The wallet.</returns>
    [HttpGet("wallet/{user}")]
    [Produces("application/json")]
    public ActionResult<object> GetWallet([FromRoute] string user, [FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        var guid = ResolveUserGuid(user);
        if (guid is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        NoStore();
        return WalletDto(guid.Value);
    }

    /// <summary>Credits a chore's coins directly (parent one-tap earn, no approval round-trip).</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="choreId">The chore.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The updated wallet.</returns>
    [HttpPost("wallet/earn")]
    [Produces("application/json")]
    public ActionResult<object> Earn(
        [FromQuery] string user,
        [FromQuery] string choreId,
        [FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        var guid = ResolveUserGuid(user);
        if (guid is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        var chore = Config.Chores.FirstOrDefault(c => string.Equals(c.Id, choreId, StringComparison.Ordinal));
        if (chore is null)
        {
            return NotFound($"Unknown chore '{choreId}'.");
        }

        _rewards.Credit(Config, IdN(guid.Value), chore.Coins, RewardsService.TypeEarn, chore.Id, chore.Name);
        return WalletDto(guid.Value);
    }

    /// <summary>Manually adjusts a wallet by a coin delta (positive or negative).</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="coins">Coin delta.</param>
    /// <param name="note">Optional note for the ledger.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The updated wallet.</returns>
    [HttpPost("wallet/adjust")]
    [Produces("application/json")]
    public ActionResult<object> Adjust(
        [FromQuery] string user,
        [FromQuery] int coins,
        [FromQuery] string? note = null,
        [FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        if (coins == 0)
        {
            return BadRequest("coins must be non-zero.");
        }

        var guid = ResolveUserGuid(user);
        if (guid is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        _rewards.Credit(Config, IdN(guid.Value), coins, RewardsService.TypeAdjust, string.Empty, note ?? "Parent adjustment");
        return WalletDto(guid.Value);
    }

    /// <summary>Redeems coins into today's bonus time on the kid's behalf (parent-operated).</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="coins">Coins to spend.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The redeem outcome plus updated wallet.</returns>
    [HttpPost("wallet/redeem")]
    [Produces("application/json")]
    public ActionResult<object> RedeemForUser(
        [FromQuery] string user,
        [FromQuery] int coins,
        [FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        var guid = ResolveUserGuid(user);
        if (guid is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        var outcome = _rewards.Redeem(Config, IdN(guid.Value), coins, "Extra time (parent)");
        return new { outcome.Error, outcome.CoinsSpent, outcome.SecondsGranted, outcome.CoinBalance };
    }

    /// <summary>Approves a pending chore claim, crediting its coins.</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="claimId">The claim id.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The updated wallet.</returns>
    [HttpPost("claims/approve")]
    [Produces("application/json")]
    public ActionResult<object> ApproveClaim(
        [FromQuery] string user,
        [FromQuery] string claimId,
        [FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        var guid = ResolveUserGuid(user);
        if (guid is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        if (!_rewards.ApproveClaim(Config, IdN(guid.Value), claimId))
        {
            return NotFound($"Unknown claim '{claimId}'.");
        }

        return WalletDto(guid.Value);
    }

    /// <summary>Rejects a pending chore claim.</summary>
    /// <param name="user">User id or name.</param>
    /// <param name="claimId">The claim id.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The updated wallet.</returns>
    [HttpPost("claims/reject")]
    [Produces("application/json")]
    public ActionResult<object> RejectClaim(
        [FromQuery] string user,
        [FromQuery] string claimId,
        [FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        var guid = ResolveUserGuid(user);
        if (guid is null)
        {
            return NotFound($"Unknown user '{user}'.");
        }

        if (!_rewards.RejectClaim(IdN(guid.Value), claimId))
        {
            return NotFound($"Unknown claim '{claimId}'.");
        }

        return WalletDto(guid.Value);
    }

    /// <summary>
    /// Searches the library for movies/series by name — used by the settings page to pick
    /// reference titles for the kid page.
    /// </summary>
    /// <param name="q">Search term.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>Up to 20 matches with id, name, type and runtime.</returns>
    [HttpGet("items/search")]
    [Produces("application/json")]
    public ActionResult<object> SearchItems([FromQuery] string q, [FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("q is required.");
        }

        var results = _libraryManager.GetItemList(new InternalItemsQuery
        {
            SearchTerm = q,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            Limit = 20,
        });

        return results.Select(item =>
        {
            var title = _rewards.ResolveReferenceTitle(Config, item.Id.ToString("N", CultureInfo.InvariantCulture));
            return new
            {
                ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
                item.Name,
                Type = item.GetBaseItemKind().ToString(),
                RuntimeMinutes = title?.RuntimeMinutes,
                CoinCost = title?.CoinCost,
            };
        }).ToList<object>();
    }

    /// <summary>Resolves already-configured reference item ids to names/costs (settings page).</summary>
    /// <param name="ids">Comma-separated item ids.</param>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>Resolved titles; unknown ids are skipped.</returns>
    [HttpGet("items/resolve")]
    [Produces("application/json")]
    public ActionResult<object> ResolveItems([FromQuery] string ids, [FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        return (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => new { Id = id, Title = _rewards.ResolveReferenceTitle(Config, id) })
            .Where(x => x.Title is not null)
            .Select(x => new
            {
                ItemId = x.Id,
                x.Title!.Name,
                Type = x.Title.IsSeries ? "Series" : "Movie",
                x.Title.RuntimeMinutes,
                x.Title.CoinCost,
            })
            .ToList<object>();
    }

    /// <summary>Sends a test notification to every enabled target (settings page "Test" button).</summary>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>How many targets succeeded out of how many were attempted.</returns>
    [HttpPost("notify/test")]
    [Produces("application/json")]
    public async Task<ActionResult<object>> TestNotifications([FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        var (succeeded, attempted) = await _notifications.SendAsync(
            Config,
            "Kids Watch-Time 🪙",
            "Test notification — the rewards system can reach you here.").ConfigureAwait(false);
        return new { Succeeded = succeeded, Attempted = attempted };
    }

    /// <summary>
    /// One-tap approve/decline landing endpoint for the links embedded in chore-claim
    /// notifications (Pushover/ntfy). Anonymous by design — the per-claim secret in
    /// <c>key</c> authorizes exactly one pending claim and dies with it — so it works
    /// straight from the phone's notification shade without a Jellyfin login.
    /// </summary>
    /// <param name="user">User id (Guid "N") the claim belongs to.</param>
    /// <param name="claim">The claim id.</param>
    /// <param name="key">The per-claim action secret.</param>
    /// <param name="op">"approve" or "decline".</param>
    /// <returns>A small human-readable confirmation page.</returns>
    [HttpGet("claim/act")]
    public ActionResult ClaimAction(
        [FromQuery] string user,
        [FromQuery] string claim,
        [FromQuery] string key,
        [FromQuery] string op)
    {
        var guid = ResolveUserGuid(user);
        if (guid is null)
        {
            return ActionResultPage("🤷", "Unknown link.");
        }

        var approve = string.Equals(op, "approve", StringComparison.OrdinalIgnoreCase);
        var handled = _rewards.ActOnClaim(Config, IdN(guid.Value), claim, key, approve);
        if (handled is null)
        {
            return ActionResultPage("🤷", "This claim was already handled (or the link is invalid).");
        }

        var kidName = _userManager.GetUserById(guid.Value)?.Username ?? "kid";
        return approve
            ? ActionResultPage("✅", $"Approved — {kidName} gets 🪙{handled.Coins} for “{handled.ChoreName}”.")
            : ActionResultPage("❌", $"Declined — “{handled.ChoreName}” ({kidName}).");
    }

    /// <summary>
    /// Serves the standalone parent dashboard — same controls as the Jellyfin admin
    /// dashboard page, but reachable from any phone/browser with just the parent token
    /// (no Jellyfin admin login): <c>/KidsLimit/parent?token=&lt;BonusApiToken&gt;</c>.
    /// </summary>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>The HTML page.</returns>
    [HttpGet("parent")]
    public ActionResult ParentPage([FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Content(
                "<!DOCTYPE html><html><body style=\"background:#111;color:#eee;font-family:sans-serif;" +
                "display:flex;align-items:center;justify-content:center;height:100vh;font-size:3em;\">" +
                "🔒</body></html>",
                "text/html");
        }

        NoStore();
        using var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.KidsLimit.Web.parent.html");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "text/html");
    }

    /// <summary>
    /// Config snapshot the standalone parent page needs (it has no Jellyfin session, so it
    /// cannot call getPluginConfiguration like the admin dashboard does): chores for the
    /// one-tap earn buttons, per-kid page links, coin settings.
    /// </summary>
    /// <param name="token">Parent shared secret.</param>
    /// <returns>Chores, users and coin settings.</returns>
    [HttpGet("parent/meta")]
    [Produces("application/json")]
    public ActionResult<object> ParentMeta([FromQuery] string? token = null)
    {
        if (!ParentAuthorized(token))
        {
            return Unauthorized();
        }

        NoStore();
        var config = Config;
        return new
        {
            CoinMinutes = Math.Max(1, config.CoinMinutes),
            config.MaxRedeemCoinsPerDay,
            config.BankCapCoins,
            Chores = config.Chores
                .Where(c => c.Enabled)
                .Select(c => new { c.Id, c.Name, c.Icon, c.Coins })
                .ToList(),
            Users = config.Users
                .Where(u => u.Enabled && Guid.TryParse(u.UserId, out _))
                .Select(u => new
                {
                    UserId = IdN(Guid.Parse(u.UserId)),
                    Name = Guid.TryParse(u.UserId, out var g) ? _userManager.GetUserById(g)?.Username : null,
                    KidUrl = string.IsNullOrEmpty(u.KidToken) ? null : "kid?token=" + Uri.EscapeDataString(u.KidToken),
                })
                .ToList(),
        };
    }

    private ContentResult ActionResultPage(string emoji, string text) =>
        Content(
            "<!DOCTYPE html><html><head><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<title>Kids Watch-Time</title></head>" +
            "<body style=\"background:#14172e;color:#eee;font-family:sans-serif;display:flex;flex-direction:column;" +
            "align-items:center;justify-content:center;min-height:100vh;margin:0;gap:1em;text-align:center;padding:1em;\">" +
            "<div style=\"font-size:5em;\">" + emoji + "</div>" +
            "<div style=\"font-size:1.3em;max-width:30em;\">" + System.Net.WebUtility.HtmlEncode(text) + "</div>" +
            "</body></html>",
            "text/html");

    // ------------------------------------------------------------------ kid API

    /// <summary>Serves the kid TV page. Data calls carry the token from the query string.</summary>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The HTML page.</returns>
    [HttpGet("kid")]
    public ActionResult KidPage([FromQuery] string? token = null)
    {
        if (ResolveKid(token) is null)
        {
            return Content(
                "<!DOCTYPE html><html><body style=\"background:#111;color:#eee;font-family:sans-serif;" +
                "display:flex;align-items:center;justify-content:center;height:100vh;font-size:3em;\">" +
                "🔒</body></html>",
                "text/html");
        }

        NoStore();
        using var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.KidsLimit.Web.kid.html");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "text/html");
    }

    /// <summary>
    /// Gets everything the kid page renders: coins, chores and her remaining TV time.
    /// <para>
    /// Deliberately cheap — no library queries at all. The page polls this every few
    /// seconds so a parent's approve/reject shows up on the TV within seconds, and since
    /// the child now spends coins on a clock rather than on posters, nothing here needs
    /// the library. (The older poster grid made this the expensive call on the page.)
    /// </para>
    /// </summary>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The kid page state.</returns>
    [HttpGet("kid/state")]
    [Produces("application/json")]
    public ActionResult<object> KidState([FromQuery] string? token = null)
    {
        var kid = ResolveKid(token);
        if (kid is null)
        {
            return Unauthorized();
        }

        NoStore();
        var config = Config;
        var userId = IdN(kid.Value.Guid);
        var local = DateTime.Now;
        var today = LimitCalculator.DateKey(local);
        var wallet = _wallets.GetSnapshot(userId);
        var redeemedToday = string.Equals(wallet.RedeemDate, today, StringComparison.Ordinal)
            ? wallet.CoinsRedeemedToday
            : 0;
        var redeemableNow = config.MaxRedeemCoinsPerDay > 0
            ? Math.Min(wallet.CoinBalance, Math.Max(0, config.MaxRedeemCoinsPerDay - redeemedToday))
            : wallet.CoinBalance;

        var chores = config.Chores.Where(c => c.Enabled).Select(c => new
        {
            c.Id,
            c.Name,
            c.Icon,
            // Blank out a key whose art is not embedded (yet), so the tile falls back to the
            // emoji rather than showing a broken image on the TV.
            Clipart = ChoreClipart.Resolve(c.Clipart) is null ? string.Empty : c.Clipart,
            c.Coins,
            c.MaxPerDay,
            ClaimedToday = RewardsService.ClaimedToday(wallet, c.Id, today),
            Pending = wallet.PendingClaims.Count(p =>
                string.Equals(p.ChoreId, c.Id, StringComparison.Ordinal) &&
                string.Equals(p.Date, today, StringComparison.Ordinal)),
        }).ToList();

        // The header's TV-time meter: her plain daily minutes, which are a different thing
        // from coins (they drain on their own and can't be banked). Remaining = the same
        // most-restrictive-cap math the tracker enforces; the budget is the meter's full
        // scale, taken from whichever cap family is configured.
        long? timeRemaining = null;
        long? timeBudget = null;
        var userCfg = Plugin.Instance?.FindUser(userId);
        var preset = userCfg is null || !userCfg.Enabled
            ? null
            : LimitCalculator.ResolvePreset(userCfg, config, local);
        if (preset is not null)
        {
            var window = LimitCalculator.WindowFor(local, config);
            var daily = _store.GetOrCreate(userId, today);
            var sessionSeconds = daily.ActiveSessions.Values
                .Select(s => s.SecondsWatched)
                .DefaultIfEmpty(0)
                .Max();
            var remaining = LimitCalculator.Compute(preset, daily, window, sessionSeconds);
            if (remaining.HasLimit)
            {
                timeRemaining = daily.ManuallyStopped ? 0 : Math.Max(0, remaining.RemainingSeconds);
                timeBudget = preset.DailyCapMinutes is int dc
                    ? (dc * 60L) + daily.DailyBonusSeconds
                    : LimitCalculator.WindowCap(preset, window) is int wc
                        ? (wc * 60L) + daily.DailyBonusSeconds
                        : preset.SessionCapMinutes is int sc
                            ? (sc * 60L) + daily.SessionBonusSeconds
                            : null;
                if (timeBudget is long budget)
                {
                    timeBudget = Math.Max(budget, timeRemaining.Value);
                }
            }
        }

        return new
        {
            kid.Value.Name,
            CoinBalance = wallet.CoinBalance,
            CoinMinutes = Math.Max(1, config.CoinMinutes),
            RedeemableNow = redeemableNow,
            CoinsRedeemedToday = redeemedToday,
            MaxRedeemCoinsPerDay = config.MaxRedeemCoinsPerDay,

            // Where the spend clock's hand starts, so the usual amount is one "yes" away.
            DefaultSpendCoins = Math.Max(1, config.DefaultSpendCoins),
            TimeRemainingSeconds = timeRemaining,
            TimeBudgetSeconds = timeBudget,
            Chores = chores,
        };
    }

    /// <summary>Kid claims a chore (goes to the parent's pending queue).</summary>
    /// <param name="choreId">The chore.</param>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>Whether the claim was filed.</returns>
    [HttpPost("kid/claim")]
    [Produces("application/json")]
    public ActionResult<object> KidClaim([FromQuery] string choreId, [FromQuery] string? token = null)
    {
        var kid = ResolveKid(token);
        if (kid is null)
        {
            return Unauthorized();
        }

        var config = Config;
        var claim = _rewards.Claim(config, IdN(kid.Value.Guid), kid.Value.Name, choreId, PublicBase(config));
        _logger.LogInformation(
            "KidsLimit: kid {User} claimed chore {Chore}; filed={Filed}.",
            IdN(kid.Value.Guid),
            choreId,
            claim is not null);
        return new { Ok = claim is not null };
    }

    /// <summary>
    /// Kid redeems coins as plain extra watch time — no title attached. This is what the
    /// kid page's spend clock buys: she picks an amount of time on a clock face, and these
    /// are the minutes it grants.
    /// </summary>
    /// <param name="coins">Coins to spend (validated against balance and the daily cap).</param>
    /// <param name="resume">
    /// True asks the server to also put the last thing she watched back on, but only when
    /// nothing is playing — see <see cref="TryResumeLastAsync"/>. The kid page always asks,
    /// so a spend produces television rather than just a bigger number.
    /// </param>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The outcome: error, coins left, seconds granted, whether playback started.</returns>
    [HttpPost("kid/time")]
    [Produces("application/json")]
    public async Task<ActionResult<object>> KidExtraTime(
        [FromQuery] int coins,
        [FromQuery] bool resume = false,
        [FromQuery] string? token = null)
    {
        // A spend used to leave no trace at all unless it threw, and the two quiet exits
        // below — an unmatched token and a bad amount — answer 401/400, which the server
        // does not log either. The child saw a ⚠️ and the log had nothing in it, so there
        // was no way to tell a request that was refused from one that never arrived. Every
        // exit says something now; this is a handful of lines a day, not a firehose.
        var kid = ResolveKid(token);
        if (kid is null)
        {
            _logger.LogWarning(
                "KidsLimit: spend refused — the kid token matched no enabled user (token {Length} chars). "
                + "A kid page left open across a settings save carries the old token.",
                token?.Length ?? 0);
            return Unauthorized();
        }

        if (coins <= 0)
        {
            _logger.LogWarning(
                "KidsLimit: spend refused for {User} — asked for {Coins} coin(s).",
                IdN(kid.Value.Guid),
                coins);
            return BadRequest("coins must be positive.");
        }

        _logger.LogInformation(
            "KidsLimit: kid {User} is spending {Coins} coin(s) (resume={Resume}).",
            IdN(kid.Value.Guid),
            coins,
            resume);

        var config = Config;
        var outcome = _rewards.Redeem(config, IdN(kid.Value.Guid), coins, "Extra time");

        // Only after the coins actually left the wallet: never start playback she has not
        // paid for (and the time she just bought is what keeps it running).
        var played = outcome.Error is null && resume
            && await PlayedOrFalseAsync(
                TryResumeLastAsync(config, kid.Value.Guid), kid.Value.Guid).ConfigureAwait(false);

        _logger.LogInformation(
            "KidsLimit: spend for {User} answered error={Error}, spent={Spent}, granted={Granted}s, played={Played}.",
            IdN(kid.Value.Guid),
            outcome.Error ?? "none",
            outcome.CoinsSpent,
            outcome.SecondsGranted,
            played);

        return new { outcome.Error, outcome.CoinBalance, outcome.SecondsGranted, outcome.CoinsSpent, Played = played };
    }

    /// <summary>
    /// Kid redeems coins to watch a title: debits its coin cost, grants the bonus time
    /// and (best effort) starts playback on the kid's active session. Pricing is
    /// resume-aware (a half-watched movie costs only what's left, and playback resumes
    /// there), and the redeem may be PARTIAL: when the price exceeds what's redeemable
    /// today, whatever IS redeemable is spent and that much time granted — the daily cap
    /// then means "N coins of watching per day", not "only short titles ever".
    /// </summary>
    /// <param name="itemId">The library item id.</param>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The outcome: error, coins left, whether playback started, partial flag.</returns>
    [HttpPost("kid/redeem")]
    [Produces("application/json")]
    public async Task<ActionResult<object>> KidRedeem([FromQuery] string itemId, [FromQuery] string? token = null)
    {
        var kid = ResolveKid(token);
        if (kid is null)
        {
            return Unauthorized();
        }

        var config = Config;
        if (!Guid.TryParse(itemId, out var itemGuid))
        {
            return NotFound("Unknown item.");
        }

        // Only allow items the kid's Jellyfin user can actually see (library access +
        // parental rating), so the token can't be used to unlock content out of scope.
        var item = _libraryManager.GetItemById(itemGuid);
        var user = _userManager.GetUserById(kid.Value.Guid);
        if (item is null || user is null || !item.IsVisibleStandalone(user))
        {
            return NotFound("Not available.");
        }

        var title = _rewards.BuildTitle(config, item, user);
        if (title is null)
        {
            return NotFound("No runtime.");
        }

        var userId = IdN(kid.Value.Guid);
        var charge = Math.Min(title.CoinCost, RedeemableNow(config, userId));
        if (charge <= 0)
        {
            // Distinguish the two lock reasons for the kid page: out of coins vs
            // out of today's redeem allowance.
            var balance = _wallets.GetSnapshot(userId).CoinBalance;
            return new { Error = balance <= 0 ? "balance" : "dailycap", CoinBalance = balance, Played = false };
        }

        var outcome = _rewards.Redeem(config, userId, charge, title.Name);
        if (outcome.Error is not null)
        {
            return new { outcome.Error, outcome.CoinBalance, Played = false };
        }

        var played = await PlayedOrFalseAsync(
            _rewards.TryPlayAsync(kid.Value.Guid, title.ItemId, title.ResumeTicks),
            kid.Value.Guid).ConfigureAwait(false);
        return new
        {
            Error = (string?)null,
            outcome.CoinBalance,
            outcome.SecondsGranted,
            outcome.CoinsSpent,
            Partial = charge < title.CoinCost,
            Played = played,
        };
    }

    /// <summary>
    /// Poster proxy for the kid page, so images work with only the kid token (no Jellyfin
    /// session). Episodes/series fall back to their series/parent primary image.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The primary image file.</returns>
    [HttpGet("kid/image/{itemId}")]
    public ActionResult KidImage([FromRoute] string itemId, [FromQuery] string? token = null)
    {
        var kid = ResolveKid(token);
        if (kid is null)
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(itemId, out var guid))
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById(guid);
        var user = _userManager.GetUserById(kid.Value.Guid);
        if (item is null || user is null || !item.IsVisibleStandalone(user))
        {
            return NotFound();
        }

        while (item is not null)
        {
            var image = item.GetImageInfo(ImageType.Primary, 0);
            if (image is not null && System.IO.File.Exists(image.Path))
            {
                Response.Headers.CacheControl = "public, max-age=86400";
                return PhysicalFile(image.Path, ContentTypeFor(image.Path));
            }

            item = item.GetParent();
        }

        return NotFound();
    }

    /// <summary>
    /// Serves the kid's Jellyfin profile photo for the page header, so it works with only
    /// the kid token (no Jellyfin session). 404 when the user has no profile image — the
    /// page then falls back to its wave emoji.
    /// </summary>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The profile image file.</returns>
    [HttpGet("kid/avatar")]
    public ActionResult KidAvatar([FromQuery] string? token = null)
    {
        var kid = ResolveKid(token);
        if (kid is null)
        {
            return Unauthorized();
        }

        var path = _userManager.GetUserById(kid.Value.Guid)?.ProfileImage?.Path;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        return PhysicalFile(path, ContentTypeFor(path));
    }

    /// <summary>
    /// Lists the built-in chore art that actually has a file embedded, for the picture pickers
    /// on the config page and the phone page — so adding art is a drop-in file plus a catalog
    /// line, with no picker list to update in two places. Public and cacheable: static metadata
    /// about static art, no user data.
    /// </summary>
    /// <returns>Key, label and visual style for each offerable clipart.</returns>
    [HttpGet("clipart")]
    public ActionResult ClipartCatalog()
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        return Ok(ChoreClipart.Available()
            .Select(e => new { e.Key, e.Label, Style = ChoreClipart.StyleOf(e.Key) }));
    }

    /// <summary>
    /// Serves a built-in chore picture. Public and cacheable — the art is static and carries no
    /// user data. Used by the kid tile (when a chore has no photo) and by the parent-facing
    /// pickers. The key is validated against a fixed allow-list so it can never be used to fetch
    /// arbitrary embedded resources; the content type comes from the resolved file, so raster art
    /// and the legacy SVGs are both served correctly.
    /// </summary>
    /// <param name="key">The clipart key, e.g. "make-bed".</param>
    /// <returns>The image, or 404 for an unknown key or one with no art embedded yet.</returns>
    [HttpGet("clipart/{key}")]
    public ActionResult Clipart([FromRoute] string key)
    {
        var resolved = ChoreClipart.Resolve(key);
        if (resolved is null)
        {
            return NotFound();
        }

        using var stream = GetType().Assembly.GetManifestResourceStream(resolved.Resource);
        if (stream is null)
        {
            return NotFound();
        }

        // Buffer: the resource stream is disposed with this scope, so it cannot be handed
        // to the framework to write out lazily. The art is a few KB either way.
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Response.Headers.CacheControl = "public, max-age=604800";
        return File(buffer.ToArray(), resolved.ContentType);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Marks a response as live data that must never be reused from a cache.
    /// The kid page and the parent page both poll the same URL over and over, so a
    /// cached body means the child keeps staring at a coin count her parent already
    /// changed. The two HTML pages get it too: they ship inside the plugin DLL, so a
    /// cached copy survives a plugin upgrade and the fix the parent just installed
    /// silently does not arrive — the TV's WebView is the one client nobody can force
    /// a reload on. Images and the clipart catalog deliberately do NOT get this — they
    /// are static and should stay cached.
    /// </summary>
    private void NoStore() =>
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };

    private static string IdN(Guid guid) => guid.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// The kid's most recently watched titles, movies and series mixed.
    /// Sorting Movie+Series by <see cref="ItemSortBy.DatePlayed"/> can't do this: a series
    /// item never gets a play date of its own (only its episodes do), so every series would
    /// sink into the alphabetical tail while movies float. Instead we ask for recently
    /// played movies AND episodes, collapse each episode onto its series, and keep the
    /// first <paramref name="max"/> distinct titles that have a poster.
    /// </summary>
    /// <param name="user">The kid's Jellyfin user.</param>
    /// <param name="max">How many titles are wanted (the query asks for headroom above it).</param>
    /// <returns>The leading titles, most recently watched first.</returns>
    private List<BaseItem> RecentlyWatchedFront(
        Jellyfin.Database.Implementations.Entities.User user,
        int max)
    {
        var recent = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = new[]
            {
                (ItemSortBy.DatePlayed, Jellyfin.Database.Implementations.Enums.SortOrder.Descending),
            },
            // Headroom: several episodes of one series collapse into a single tile.
            Limit = max * 3,
        });

        var front = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        foreach (var item in recent)
        {
            if (_userDataManager.GetUserData(user, item)?.LastPlayedDate is null)
            {
                continue; // padding the DatePlayed sort returns, never actually played
            }

            var target = item is MediaBrowser.Controller.Entities.TV.Episode episode
                ? (BaseItem?)episode.Series
                : item;
            if (target is null || !seen.Add(target.Id))
            {
                continue;
            }

            if (target.GetImageInfo(ImageType.Primary, 0) is null)
            {
                continue; // the grid only shows items with a poster to tap
            }

            front.Add(target);
            if (front.Count >= max)
            {
                break;
            }
        }

        return front;
    }

    /// <summary>
    /// Awaits a best-effort auto-play and never lets it fail the request.
    /// <para>
    /// Both callers reach it AFTER the coins have left the wallet, and everything it
    /// touches — a library query, user data, posters, a session's socket — can throw for
    /// reasons that have nothing to do with the spend. An exception escaping here would
    /// answer 500 for a purchase that actually happened: the child is shown a ⚠️ for
    /// time she has already paid for, and pays a second time when she tries again. Any
    /// fault is simply "nothing started" — the time is hers either way and she presses
    /// play herself.
    /// </para>
    /// </summary>
    /// <param name="attempt">The auto-play already under way.</param>
    /// <param name="userGuid">The kid, for the log line.</param>
    /// <returns>True when a session accepted the Play command; false on any failure.</returns>
    private async Task<bool> PlayedOrFalseAsync(Task<bool> attempt, Guid userGuid)
    {
        try
        {
            return await attempt.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "KidsLimit: auto-play after a spend failed for {User}; the coins were still spent.",
                IdN(userGuid));
            return false;
        }
    }

    /// <summary>
    /// Best-effort "and put it back on": after the kid buys time on the spend clock with
    /// nothing playing, resumes the last thing she watched where she left off.
    /// <para>
    /// The coins have to visibly produce television or the exchange has no point — she
    /// pays, the TV stays black, and nothing taught her what the coins did. Silent when
    /// something is already playing (re-issuing Play would yank her back to the resume
    /// point mid-episode) and silent when there is nothing to resume.
    /// </para>
    /// </summary>
    /// <param name="config">Plugin config.</param>
    /// <param name="userGuid">The kid's Jellyfin user.</param>
    /// <returns>True when playback was actually started.</returns>
    private async Task<bool> TryResumeLastAsync(PluginConfiguration config, Guid userGuid)
    {
        var user = _userManager.GetUserById(userGuid);
        if (user is null || _rewards.NowPlayingFor(userGuid) is not null)
        {
            return false;
        }

        // Exactly one title, so the query stays small — this runs only on a spend.
        var item = RecentlyWatchedFront(user, 1).FirstOrDefault();
        if (item is null || !item.IsVisibleStandalone(user))
        {
            return false;
        }

        var title = _rewards.BuildTitle(config, item, user);
        if (title is null)
        {
            return false;
        }

        return await _rewards.TryPlayAsync(userGuid, title.ItemId, title.ResumeTicks).ConfigureAwait(false);
    }

    /// <summary>Coins the kid may still redeem today: bounded by balance and the daily cap.</summary>
    private int RedeemableNow(PluginConfiguration config, string userId)
    {
        var wallet = _wallets.GetSnapshot(userId);
        var today = LimitCalculator.DateKey(DateTime.Now);
        var redeemedToday = string.Equals(wallet.RedeemDate, today, StringComparison.Ordinal)
            ? wallet.CoinsRedeemedToday
            : 0;
        return config.MaxRedeemCoinsPerDay > 0
            ? Math.Min(wallet.CoinBalance, Math.Max(0, config.MaxRedeemCoinsPerDay - redeemedToday))
            : wallet.CoinBalance;
    }

    /// <summary>
    /// Base URL for links that must work from the parent's phone (notification actions).
    /// The configured public URL wins; otherwise fall back to how this request came in.
    /// </summary>
    private string PublicBase(PluginConfiguration config) =>
        string.IsNullOrWhiteSpace(config.PublicBaseUrl)
            ? Request.Scheme + "://" + Request.Host + Request.PathBase
            : config.PublicBaseUrl.Trim();

    private bool ParentAuthorized(string? token)
    {
        var configured = Config.BonusApiToken;
        if (string.IsNullOrEmpty(configured))
        {
            return false; // No token configured => API disabled.
        }

        var provided = token;
        if (string.IsNullOrEmpty(provided) &&
            Request.Headers.TryGetValue("X-KidsLimit-Token", out var header))
        {
            provided = header.ToString();
        }

        return !string.IsNullOrEmpty(provided) &&
               string.Equals(provided, configured, StringComparison.Ordinal);
    }

    private (Guid Guid, string Name)? ResolveKid(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var cfg = Config.Users.FirstOrDefault(u =>
            u.Enabled &&
            !string.IsNullOrEmpty(u.KidToken) &&
            string.Equals(u.KidToken, token, StringComparison.Ordinal));
        if (cfg is null || !Guid.TryParse(cfg.UserId, out var guid))
        {
            return null;
        }

        var user = _userManager.GetUserById(guid);
        return user is null ? null : (user.Id, user.Username);
    }

    private Guid? ResolveUserGuid(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var user = Guid.TryParse(idOrName, out var g) ? _userManager.GetUserById(g) : null;
        user ??= _userManager.GetUserByName(idOrName);
        return user?.Id;
    }

    private object WalletDto(Guid userGuid)
    {
        var wallet = _wallets.GetSnapshot(IdN(userGuid));
        var today = LimitCalculator.DateKey(DateTime.Now);
        return new
        {
            UserId = IdN(userGuid),
            wallet.CoinBalance,
            CoinMinutes = Math.Max(1, Config.CoinMinutes),
            CoinsRedeemedToday = string.Equals(wallet.RedeemDate, today, StringComparison.Ordinal)
                ? wallet.CoinsRedeemedToday
                : 0,
            PendingClaims = wallet.PendingClaims
                .OrderBy(c => c.ClaimedAtUtc)
                .Select(c => new { c.Id, c.ChoreId, c.ChoreName, c.Coins, c.ClaimedAtUtc, c.Date })
                .ToList(),
            Ledger = wallet.Ledger
                .Skip(Math.Max(0, wallet.Ledger.Count - LedgerTailLength))
                .Select(e => new { e.AtUtc, e.Date, e.DeltaCoins, e.Type, e.ChoreId, e.Note })
                .ToList(),
        };
    }
}
