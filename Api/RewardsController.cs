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
    private const int LibraryPageSize = 60;

    private readonly RewardsService _rewards;
    private readonly WalletStore _wallets;
    private readonly StateStore _store;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly NotificationService _notifications;

    /// <summary>
    /// Initializes a new instance of the <see cref="RewardsController"/> class.
    /// </summary>
    /// <param name="rewards">Rewards service.</param>
    /// <param name="wallets">Wallet store.</param>
    /// <param name="store">Daily state store.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="notifications">Push-notification fan-out.</param>
    public RewardsController(
        RewardsService rewards,
        WalletStore wallets,
        StateStore store,
        IUserManager userManager,
        ILibraryManager libraryManager,
        NotificationService notifications)
    {
        _rewards = rewards;
        _wallets = wallets;
        _store = store;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _notifications = notifications;
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

        using var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.KidsLimit.Web.kid.html");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "text/html");
    }

    /// <summary>Gets everything the kid page renders: coins, chores, reference titles, time.</summary>
    /// <param name="light">
    /// True skips the (comparatively expensive) library page so the kid page can poll
    /// coins/chores frequently — a parent's approve/reject then shows up within seconds.
    /// </param>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The kid page state.</returns>
    [HttpGet("kid/state")]
    [Produces("application/json")]
    public ActionResult<object> KidState([FromQuery] bool light = false, [FromQuery] string? token = null)
    {
        var kid = ResolveKid(token);
        if (kid is null)
        {
            return Unauthorized();
        }

        var config = Config;
        var userId = IdN(kid.Value.Guid);
        var today = LimitCalculator.DateKey(DateTime.Now);
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
            c.Coins,
            c.MaxPerDay,
            ClaimedToday = RewardsService.ClaimedToday(wallet, c.Id, today),
            Pending = wallet.PendingClaims.Count(p =>
                string.Equals(p.ChoreId, c.Id, StringComparison.Ordinal) &&
                string.Equals(p.Date, today, StringComparison.Ordinal)),
        }).ToList();

        // Watch options are the kid's OWN accessible library as posters (respecting their
        // Jellyfin library access + parental rating), so it never looks like "only these
        // few shows". The first page ships with the state; more pages come from kid/library.
        var (titles, titlesTotal) = light
            ? (new List<object>(), 0)
            : LibraryPage(kid.Value.Guid, config, 0, LibraryPageSize);

        return new
        {
            kid.Value.Name,
            CoinBalance = wallet.CoinBalance,
            CoinMinutes = Math.Max(1, config.CoinMinutes),
            RedeemableNow = redeemableNow,
            CoinsRedeemedToday = redeemedToday,
            MaxRedeemCoinsPerDay = config.MaxRedeemCoinsPerDay,
            Chores = chores,
            Titles = titles,
            TitlesTotal = titlesTotal,
            PageSize = LibraryPageSize,
        };
    }

    /// <summary>
    /// Returns a page of the kid's accessible library as poster tiles, so the watch grid
    /// can lazily grow beyond the first page shipped with <see cref="KidState"/>.
    /// </summary>
    /// <param name="skip">How many items to skip.</param>
    /// <param name="take">How many items to return (clamped).</param>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The page of poster tiles plus the total item count.</returns>
    [HttpGet("kid/library")]
    [Produces("application/json")]
    public ActionResult<object> KidLibrary(
        [FromQuery] int skip = 0,
        [FromQuery] int take = LibraryPageSize,
        [FromQuery] string? token = null)
    {
        var kid = ResolveKid(token);
        if (kid is null)
        {
            return Unauthorized();
        }

        var (items, total) = LibraryPage(kid.Value.Guid, Config, skip, take);
        return new { Items = items, Total = total };
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
        return new { Ok = claim is not null };
    }

    /// <summary>
    /// Kid redeems coins as plain extra watch time — no title attached. Powers the ⏳
    /// "more time" tiles on the kid page, e.g. to keep watching whatever is already
    /// playing on the TV.
    /// </summary>
    /// <param name="coins">Coins to spend (validated against balance and the daily cap).</param>
    /// <param name="token">The per-user kid token.</param>
    /// <returns>The outcome: error, coins left, seconds granted.</returns>
    [HttpPost("kid/time")]
    [Produces("application/json")]
    public ActionResult<object> KidExtraTime([FromQuery] int coins, [FromQuery] string? token = null)
    {
        var kid = ResolveKid(token);
        if (kid is null)
        {
            return Unauthorized();
        }

        if (coins <= 0)
        {
            return BadRequest("coins must be positive.");
        }

        var outcome = _rewards.Redeem(Config, IdN(kid.Value.Guid), coins, "Extra time");
        return new { outcome.Error, outcome.CoinBalance, outcome.SecondsGranted };
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

        var played = await _rewards.TryPlayAsync(kid.Value.Guid, title.ItemId, title.ResumeTicks).ConfigureAwait(false);
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

    // ------------------------------------------------------------------ helpers

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
    /// Builds a page of the kid's accessible movies/series as priced poster tiles. Passing
    /// the user to the query makes Jellyfin apply that user's library access and parental
    /// rating, so only content the child is allowed to see comes back.
    /// </summary>
    private (List<object> Items, int Total) LibraryPage(Guid userGuid, PluginConfiguration config, int skip, int take)
    {
        var user = _userManager.GetUserById(userGuid);
        if (user is null)
        {
            return (new List<object>(), 0);
        }

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            ImageTypes = new[] { ImageType.Primary }, // only items with a poster to tap
            // What she watched last comes first (so the movie she wants to FINISH is right
            // there), then the rest alphabetically. SortOrder moved to
            // Jellyfin.Database.Implementations in 10.11 (same relocation noted for the
            // User entity); fully-qualify so we don't add a brittle using.
            OrderBy = new[]
            {
                (ItemSortBy.DatePlayed, Jellyfin.Database.Implementations.Enums.SortOrder.Descending),
                (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending),
            },
            StartIndex = Math.Max(0, skip),
            Limit = Math.Clamp(take, 1, 200),
        };

        var result = _libraryManager.GetItemsResult(query);
        var items = result.Items
            .Select(item => _rewards.BuildTitle(config, item, user))
            .Where(t => t is not null)
            .Select(t => (object)new
            {
                ItemId = t!.ItemId.ToString("N", CultureInfo.InvariantCulture),
                t.Name,
                t.IsSeries,
                t.RuntimeMinutes,
                t.CoinCost,
                t.FullCoinCost,
                t.RemainingMinutes,
                t.InProgress,
                t.ProgressPct,
            })
            .ToList();

        return (items, result.TotalRecordCount);
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
