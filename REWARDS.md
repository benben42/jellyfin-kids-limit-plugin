# Chore Rewards ("Coins") — Design

A persistent reward system layered on top of the daily limits: the kid earns
**coins** by doing chores, banks them indefinitely (no midnight reset), and spends
them later as extra watch time — asked for on a clock face, on a TV-friendly,
picture-only page.

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
- **Default spend** (`DefaultSpendCoins`, default 3 = 15 min): where the kid
  page's spend clock opens, so the usual amount is a single 👍 and ▲/▼ are only
  for asking for something else.
- **Two lock reasons** on the kid page, told apart in pictures: 🔒 on a coin =
  the jar is empty (go earn coins); 🌙 = coins are banked but today's redeem
  allowance is spent (come back tomorrow). The spend tile is never permanently
  locked.
- **One spend, on a clock**: the page had a wall of posters, each with its own
  coin price. It failed its actual user — every tile asked a different arithmetic
  question ("can I afford *this* one?"), and none of them answered the one she
  had ("more telly, now"). So all of it collapsed into **one tile** (a coin going
  into a telly), and the amount is chosen afterwards on an **analog clock**: the
  hands show the real time, and a gold wedge sweeps forward from the minute hand
  by the time she is buying. Time as a *shape* is readable before arithmetic is.
  ▲/▼ move it one coin at a time, ▲ greys out at what she can afford today, and
  the wedge never exceeds one full turn (past that it wraps onto itself and stops
  meaning anything). Confirming spends through `kid/time`, which also resumes the
  last thing she watched when nothing is playing — the coins have to visibly
  produce television.
- **Resume-aware pricing** survives in the API (`kid/redeem`, `BuildTitle`): a
  partly-watched movie costs only its *remaining* runtime and playback resumes at
  the saved position, and a redeem that exceeds today's allowance is partial. No
  kid-page surface prices titles any more — the clock buys plain minutes — but the
  endpoint and its pricing stay for direct callers and for the resume path.
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
   - *Spend*: exactly one tile — a coin dropping into a telly whose screen is a
     clock — which opens the spend clock above. Confirming debits the coins,
     grants the time and (best effort) resumes the last thing she watched when
     nothing is playing; when it cannot, the page shows "open Jellyfin" guidance
     (pictogram).
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
   - **The price is drawn as coins matched against her own piggy**, under the
     clock: solid coins are the ones she can pay right now, hollow ones are what
     is still missing. Counting "two more" is something she can do; "it costs 8
     and I have 5" is subtraction. Above six coins the row becomes the pile plus
     the number. The spend tile itself breathes whenever anything is affordable.
   - **The exchange is animated, not narrated**: on a spend the coins visibly
     leave the piggy, cross the header and land in the TV meter, which grows as
     they arrive. Standing on a spend tile first previews it — a hatched gold
     ghost segment on the meter showing how much longer it would get. No
     celebration card covers this: the two chips and their relationship *are*
     the lesson.
   - **Asked at the moment it matters**: when the meter hits empty and she still
     has redeemable coins, the page opens the spend clock itself — already set to
     the default amount, so the whole answer is one 👍 — once per flat battery. Left idle, it shows a wordless three-line explainer —
     🧹→🪙→🐷, 🐷→🪙→📺, 📺→🍿 — once per idle stretch.
   - The page polls `kid/state` — coins, chores and time only, no library query
     behind it at all — every 5 s, dropping to 1.5 s for three minutes while a claim
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
   The standalone parent page deliberately drops the per-chore earn buttons (a
   dozen rows per kid for something the kid claims herself, which then lands right
   there as ⏳ to approve); "± adjust" covers manual coins in one control.
   Also available as a **standalone page**: `GET /KidsLimit/parent?token=<BonusApiToken>`
   serves the same controls from any phone browser with no Jellyfin admin login
   (config comes from `GET /KidsLimit/parent/meta`). Copy the URL from settings
   or the admin dashboard's "📱 Phone page" button and bookmark it.
3. **Plugin settings** — "Rewards" tab: coin value, bank cap, daily redeem cap,
   the spend clock's default amount, ntfy topic URL, chores editor,
   reference-title picker (library search), and a per-kid token + ready-to-copy
   kid page URL.
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
  read own wallet/chores state, claim a chore, spend coins on time (or on a
  title, via the API), fetch poster images. It cannot grant, adjust, approve, or touch other users.

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
