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
- **Keep watching**: one tile, faced with the **poster** of whatever is playing
  now (or was last watched), priced at what it costs to finish. It replaced the
  old abstract "1 coin / 3 coins → +N minutes" tiles: minutes are a unit a
  non-reader cannot feel, a picture of her show is one she can act on, and it
  leaves a single rule — coins buy shows. While something is genuinely playing
  it spends through `kid/time` (bonus time only, playback untouched); with
  nothing playing it redeems the title so the spend also *starts* it.
- **Redeemed time behaves like bonus time** (§5.1): it lifts daily, session and
  the currently-active window budget — but as **one day-wide pool**. A playing
  second that exceeds any base cap drains the pool by one second (exactly once,
  even when it exceeds several caps at the same moment); what one window or one
  sitting consumed is no longer available to the others. So 3 coins are 15
  extra minutes for the day, not 15 per window. The evening window boundary
  itself is not moved — the plugin never had a hard bedtime cutoff, only window
  caps.
- **Midnight refund**: redeemed-but-unwatched seconds return to the wallet at
  rollover. Consumption is attributed to parent-granted bonus first (generous to
  the kid). Formula: `refund = min(redeemedToday, max(0, dailyBonus −
  bonusConsumed))`, where `bonusConsumed` is the pool drain tracked live against
  ALL cap families (daily, window, session) — time that was only watchable
  because of the bonus never refunds, including on presets with window caps but
  no daily cap. Refunded seconds are converted back to whole coins rounding
  **up**: a coin whose time was only partly watched comes back in full
  (deliberately generous; the rounding can never mint more coins than were
  redeemed).
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
   - Header: the kid's Jellyfin profile photo (👋 wave when there is none) and
     her three balances, each drawn as a different thing so they can't be
     confused. **TV-time meter** (📺 + draining battery bar + minutes): her
     plain daily minutes from the preset — deliberately NOT coins, since this
     time drains on its own and can't be banked; amber when low, grey + 🌙 when
     the day is used up, hidden when no limit applies. **Piggy capsule** (🐷):
     saved chore coins, drawn as countable star-coins up to four, then a
     coin-pile icon plus the count. Inside the same capsule, the **☀️ count**
     is how many piggy coins may still come out today (the daily redeem cap) —
     a gate on the bank, not a third currency; ☀️ flips to 🌙 when spent. The
     header is sticky: a D-pad has no "jump to top", so the balances stay
     visible however deep the child scrolls into the grid.
   - The watch grid is sorted by most-recently-watched first, movies and series
     mixed by real recency (a series' recency comes from its episodes' play
     dates), then alphabetically.
   - **Prices are drawn as coins matched against her own piggy**: solid coins
     are the ones she can pay right now, hollow ones are what is still missing.
     Counting "two more" is something she can do; "it costs 8 and I have 5" is
     subtraction. Above six coins the row stops being countable and becomes the
     pile plus the number. Exactly one tile — the cheapest she can afford
     outright — breathes, so a wall of locked posters always offers one clear
     "this one, right now" instead of reading as a flat no.
   - **The exchange is animated, not narrated**: on a spend the coins visibly
     leave the piggy, cross the header and land in the TV meter, which grows as
     they arrive. Standing on a spend tile first previews it — a hatched gold
     ghost segment on the meter showing how much longer it would get. No
     celebration card covers this: the two chips and their relationship *are*
     the lesson.
   - **Asked at the moment it matters**: when the meter hits empty and she still
     has redeemable coins, the page offers 🐷 ➜ 📺 with one button, once per
     flat battery. Left idle, it shows a wordless three-line explainer —
     🧹→🪙→🐷, 🐷→🪙→📺, 📺→🍿 — once per idle stretch.
   - The page polls a lightweight state endpoint (`kid/state?light=1`, no
     library page) every 5 s, dropping to 1.5 s for three minutes while a claim
     is waiting on a parent, so an approval lands while the child is still
     watching for it. All the polled endpoints are `no-store`; the loop re-arms
     itself after every response and pulls immediately on visibility/focus/
     pageshow/online, because Android freezes a backgrounded app's timers.
   - **An answer from the parent announces itself**: approved coins fly into the
     piggy with a sound and a green `+N`; a claim that came back with nothing
     gets a shrug. A number quietly ticking up is invisible to a six-year-old.
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

## Chore art

The tile picture is the label — the child can't read the name — so chores are drawn from a
built-in catalog (`ChoreClipart`), served by `GET /KidsLimit/clipart/{key}` and listed by
`GET /KidsLimit/clipart`. The catalog only offers keys whose file is actually embedded, and
raster art wins over SVG for the same key, so a new picture is a drop-in file plus one catalog
line. The current set is soft-clay renders; seven legacy Mulberry Symbols line-art SVGs remain
catalogued but are grouped separately in the pickers, since mixing the two styles on one page
reads as broken. See [`docs/CHORE-IMAGE-PROMPTS.md`](docs/CHORE-IMAGE-PROMPTS.md) for the
generator prompts and [`docs/ADDING-CHORES.md`](docs/ADDING-CHORES.md) for adding chores.

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
