# Chore Rewards ("Coins") — Design

A persistent reward system layered on top of the daily limits: the kid earns
**coins** by doing chores, banks them indefinitely (no midnight reset), and spends
them later as extra watch time — ideally by picking a poster of what she wants to
watch on a TV-friendly, picture-only page.

Target user for the kid UI: a 6-year-old who cannot read, using an Android TV
remote (D-pad). Everything is pictures, emoji, counts and sounds.

## Concepts

| Concept | Meaning |
| --- | --- |
| **Coin** | The kid-facing unit of time. Worth `CoinMinutes` (default 5) minutes. All earning/spending is whole coins so a 6-year-old can count them. |
| **Wallet** | Per-user persistent balance + ledger. Lives in `wallet/<userId>.json` in the plugin data folder. Never resets at midnight. |
| **Chore** | Configured by the parent: name, emoji icon, coin value, max claims per day. |
| **Claim** | Kid taps a chore on the TV page → a *pending claim*. Parent approves or rejects from the dashboard. Approval credits coins. |
| **Earn (direct)** | Parent can credit coins directly from the dashboard (one tap per chore, or a custom amount) — no claim/approval round-trip. |
| **Redeem** | Spending coins converts them into **today's** `DailyBonusSeconds` — the exact same mechanism as parent-granted bonus time, so `LimitCalculator`, the tracker and the hard-block enforcer are untouched. |
| **Reference titles** | Parent-picked favourite movies/series. The kid page shows the balance and redemption *as those posters*: "Tom & Jerry ×3". Runtime and poster come from the Jellyfin library. |

## Rules

- **Denomination**: coins only. `CoinMinutes` config (default 5).
- **Bank cap** (`BankCapCoins`, default 24 = 2 h): earning beyond the cap is
  clamped (ledger records the clamp). Since coins never expire, the cap is the
  only anti-hoarding tool.
- **Daily redeem cap** (`MaxRedeemCoinsPerDay`, default 6 = 30 min): a big bank
  cannot be blown in one sitting. Tracked per local day in the wallet.
- **Resume-aware pricing**: a partly-watched movie costs only its *remaining*
  runtime ("20 minutes to finish" = 4 coins, not the full 18) and redeeming it
  resumes playback from the saved position. The kid page shows a progress bar on
  the poster. Series posters keep the median-episode price (episodes are cheap
  and there is no single "resume the series" position).
- **Partial redeem**: picking a poster that costs more than what is redeemable
  today spends what *is* redeemable and grants that much time — so the daily cap
  means "N coins of watching per day", not "only short titles ever". A long
  movie is watched in daily installments; each day the poster shows the
  remaining-time price. The midnight refund still returns unwatched coins.
- **Two lock reasons** on the kid page, told apart in pictures: 🔒 on a coin =
  the jar is empty (go earn coins); 🌙 = coins are banked but today's redeem
  allowance is spent (come back tomorrow). Posters are never permanently locked.
- **Plain extra time**: ⏳ tiles (1 or 3 coins) redeem coins into bonus time with
  no title attached — for finishing whatever is already playing on the TV.
- **Redeemed time behaves like bonus time** (§5.1): it lifts daily, session and
  the currently-active window budget. The evening window boundary itself is not
  moved — the plugin never had a hard bedtime cutoff, only window caps.
- **Midnight refund**: redeemed-but-unwatched seconds return to the wallet at
  rollover. Consumption is attributed to parent-granted bonus first (generous to
  the kid). Formula: `refund = min(redeemedToday, max(0, dailyBonus − max(0,
  watched − dailyCap)))`; with no daily cap the whole redeemed amount refunds.
  Refunded seconds are converted back to whole coins (floor); the remainder is
  forgiven (never silently drops a full coin).
- **Claims**: require approval by default. A chore at its `MaxPerDay` (approved +
  pending for today) shows as done and cannot be claimed again.
- **Coins do not unblock by themselves** — only *redeeming* grants time (and
  clears blocks, exactly like a parent bonus grant).

## Surfaces

1. **Kid TV page** — `GET /KidsLimit/kid?token=<KidToken>` (anonymous HTML; all
   data calls carry the token). Two rows of big tiles:
   - *Chores*: emoji tile + coin badge; states: claimable / pending (⏳) / done (✅).
   - *Watch*: reference-title posters with coin cost; picking one debits the
     coins, grants the bonus time and (best effort) starts playback of that item
     on the kid's active Jellyfin session (`SendPlayCommand`). If no session is
     active the page shows "open Jellyfin" guidance (pictogram).
   - Coin jar header: coins drawn as countable star-coin icons, plus a "today"
     chip showing the day's spend allowance (bright coins = still spendable now,
     dim = already spent; ☀️ flips to 🌙 when the allowance is gone).
   - The watch grid is sorted by most-recently-watched first (the movie to
     finish sits up front), then alphabetically.
   - The page polls a lightweight state endpoint (`kid/state?light=1`, no
     library page) every few seconds, so parent approvals/rejections/coin
     grants show up without a manual refresh.
   - D-pad spatial navigation (arrow keys in WebView), huge focus ring, WebAudio
     sound feedback. No reading required.
2. **Parent dashboard** (existing admin page) — per-kid wallet: balance, pending
   claims with ✓/✗, one-tap earn buttons per chore, custom adjust, manual redeem.
   Also available as a **standalone page**: `GET /KidsLimit/parent?token=<BonusApiToken>`
   serves the same controls from any phone browser with no Jellyfin admin login
   (config comes from `GET /KidsLimit/parent/meta`). Copy the URL from settings
   or the admin dashboard's "📱 Phone page" button and bookmark it.
3. **Plugin settings** — "Rewards" tab: coin value, bank cap, daily redeem cap,
   ntfy topic URL, chores editor, reference-title picker (library search), and a
   per-kid token + ready-to-copy kid page URL.
4. **Android TV app** — `android-tv/` contains a minimal sideloadable WebView
   wrapper (leanback launcher entry, fullscreen, keeps screen on) pointed at the
   kid page URL. All UI iteration happens server-side; the APK never needs
   rebuilding.

## Auth

- Parent endpoints: existing `BonusApiToken` shared-secret scheme.
- Kid endpoints: per-user `KidToken` (generated in settings). It only permits:
  read own wallet/chores state, claim a chore, redeem for a reference title,
  fetch poster images. It cannot grant, adjust, approve, or touch other users.

## Notifications

Chore claims fan out to a configurable list of `NotificationTargets`
("Emma claims Dishwasher 🪙3"), so the parent's phone pings while the kid
waits. Claim notifications carry **one-tap ✅ Approve / ❌ Decline links**:
Pushover gets them as tappable HTML links in the message body, ntfy as real
notification action buttons. The links hit the anonymous
`GET /KidsLimit/claim/act` endpoint, authorized by a per-claim random secret
that dies once the claim is handled. Link host comes from the `PublicBaseUrl`
setting, falling back to the address the claim request came in on. Approval
also still works on the dashboard. Supported providers: **ntfy,
Pushover, Gotify, Discord and Slack incoming webhooks, Telegram bots, an
Apprise API server** (which relays to 100+ further services), and a **generic
JSON webhook** (`{"title","message"}`). A test button in settings
(`POST /KidsLimit/notify/test`) verifies delivery.

## Storage

- `wallet/<userId>.json`: `{ CoinBalance, RedeemDate, CoinsRedeemedToday,
  PendingClaims[], Ledger[] }` (ledger capped at 500 entries).
- `DailyState` gains `RedeemedSeconds` (today's wallet-sourced bonus) used by the
  rollover refund; `StateStore` raises a day-finished callback the rewards
  service uses to credit the refund.
