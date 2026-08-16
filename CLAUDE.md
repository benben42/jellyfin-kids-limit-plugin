# CLAUDE.md

Guidance for AI assistants working in this repository.

## What this is

A **Jellyfin server plugin** (`Jellyfin.Plugin.KidsLimit`) that enforces cumulative daily
watch-time limits for kids — per user, per weekday, per time-of-day window — plus a chore
"coins" rewards system kids can spend as extra watch time.

- Target: **Jellyfin 10.11.x**, **net9.0**, `Jellyfin.Controller` / `Jellyfin.Model` 10.11.11.
- Plugin GUID: `a1e5c7f2-3b4d-4e6a-9c8b-2d1f0e3a5b7c` (also hard-coded in `build.yaml`,
  `Configuration/configPage.html`, `Web/dashboard.html`, `manifest.json` — keep in sync).
- Uses the modern `IPluginServiceRegistrator` + `IHostedService` model, not the deprecated
  `IServerEntryPoint`.
- The plugin **observes** playback and stops sessions. It never disables accounts or
  unselects libraries — except the explicitly opt-in hard-block path (below), which saves
  and restores whatever it touches.

Source-of-truth design docs, all still current and worth reading before non-trivial changes:

| Doc | Covers |
|-----|--------|
| `REQUIREMENTS.md` | Full spec: data model, §5 enforcement algorithm, §5.1 bonus semantics, §2.1 the Android TV risk |
| `REWARDS.md` | Coins/chores design: bank cap, daily redeem cap, midnight refund, auth, storage |
| `README.md` | User-facing install/config/API reference |
| `docs/ADDING-CHORES.md` | Exact steps to add a chore + its art (which files, which are optional) |
| `docs/CHORE-IMAGE-PROMPTS.md` | Image-generator prompts and the clay art style rules |
| `android-tv/README.md` | The sideloadable WebView wrapper APK |

## Layout

```
Plugin.cs                     BasePlugin<PluginConfiguration>; GetPages(); MigrateConfiguration()
PluginServiceRegistrator.cs   DI registration (all singletons + one hosted service)
Configuration/
  PluginConfiguration.cs      XML-serialized config + DefaultPresets() / DefaultChores()
  Preset.cs UserLimitConfig.cs Chore.cs NotificationTarget.cs
  ChoreClipart.cs             Catalog + allow-list mapping clipart keys -> embedded files
  configPage.html             Jellyfin admin config page (embedded resource)
Services/
  WatchTimeTracker.cs         IHostedService: session events + 15 s maintenance timer; the engine
  LimitCalculator.cs          Pure limit math (static). Shared by tracker and API — keep it pure
  HardBlockEnforcer.cs        Opt-in server-side blocking (access schedule / playback permission)
  PlaybackTerminator.cs       Stop -> Pause -> kill transcode job -> close live stream
  RewardsService.cs           Coins: earn/claim/approve/redeem/refund, reference-title pricing
  NotificationService.cs      Push fan-out: ntfy, Pushover, Gotify, Discord, Slack, Telegram, Apprise, webhook
State/
  DailyState.cs               Per-user per-day counters + per-session tracking + Window enum
  StateStore.cs               Thread-safe, debounced JSON persistence; midnight rollover; history
  WalletStore.cs              Thread-safe, write-through JSON persistence for coin wallets
  UserWallet.cs DailyHistoryEntry.cs
Api/
  KidsLimitController.cs      Parent API: bonus/status/stop/allow/history
  RewardsController.cs        Rewards API + serves the kid and standalone-parent HTML pages
  SettingsController.cs       GET/POST full plugin config for the standalone parent page
  Models/                     UserStatusDto, SettingsDto
Web/
  dashboard.html              Parent dashboard inside the Jellyfin admin UI (embedded)
  parent.html                 Standalone parent app, token-only, no admin login (embedded)
  kid.html                    Picture-only kid TV page (embedded)
  clipart/*.webp|svg          Chore tile art (embedded via glob)
android-tv/                   Gradle project: thin WebView shell around the kid page
scripts/update_manifest.py    Upserts a version into manifest.json from build.yaml (CI only)
spike/stop-test.sh            Phase 0 manual probe: does the real TV honor Stop?
```

There are **no tests, no linter config, no .editorconfig, and no analyzers**. CI only builds.
The container this repo is usually cloned into has **no .NET SDK installed** — `dotnet build`
will not run locally; rely on the GitHub Actions build for compile verification, and be
correspondingly careful about compile errors.

## Build & release

```bash
dotnet build Jellyfin.Plugin.KidsLimit.csproj -c Release   # -> bin/Release/net9.0/*.dll
```

- `.github/workflows/build.yml` builds on every push to `main` and `claude/**`, and on PRs.
- `.github/workflows/release.yml` releases on a pushed `v*` tag, on a `main` push whose
  `build.yaml` version has no tag yet, or via `workflow_dispatch`. It builds with
  `-p:Version=<tag minus v>`, zips the DLL, creates the GitHub Release, then runs
  `scripts/update_manifest.py` and commits `manifest.json` back to `main`.
- `.github/workflows/android-tv.yml` builds the APK when `android-tv/**` changes.

**Version reality check.** `build.yaml` and the `.csproj` `<Version>` both still say
`1.1.0.0`, while shipped releases are at `2.1.1.0` — recent releases were cut by pushing
`v*` tags by hand, and the tag (not the file) determined the built version. So the in-repo
version numbers are stale by design of how releases have actually been made. Don't "fix"
them as a drive-by; if you bump `build.yaml` on `main`, that alone triggers a release.

## How enforcement works

1. `WatchTimeTracker` subscribes to `PlaybackStart` / `PlaybackProgress` / `PlaybackStopped`
   and also sweeps `ISessionManager.Sessions` every 15 s (clients that go quiet after
   ignoring a Stop must not escape crediting).
2. Every tick credits the elapsed gap **only when not paused**, clamped to
   `MaxDeltaSeconds` (30) to survive long gaps/seeks. A gap over
   `SessionResetGapMinutes` (30) starts a fresh "sitting" and resets the session counter.
3. `LimitCalculator.Compute` returns the **most restrictive** remaining of the applicable
   session / daily / active-window caps. `null` cap = unlimited; `null` preset = unlimited.
4. `remaining <= 0` → `PlaybackTerminator.StopSessionAsync` (re-sent every tick) plus, if
   newly blocked, an immediate `HardBlockEnforcer.ReconcileAsync`. Near the threshold →
   one best-effort `DisplayMessage` warning per sitting.
5. If a session keeps accruing time past the limit for `OverLimitAlertMinutes`, a push
   notification tells the parent auto-stop appears to have failed (once per sitting).

Rollover to a new local day happens both on playback events and on the maintenance timer.
The rollover archives the finished day to history and fires `StateStore.DayFinished`, which
`RewardsService.RefundFinishedDay` uses to refund unwatched redeemed coins.

## Invariants and traps

These are the things that have already caused bugs. Respect them.

**Config is XML-serialized by Jellyfin.** `PluginConfiguration` and everything it holds
must be XML-serializer friendly: primitives, nullable primitives, strings, and `List<T>`
of the same. No `Dictionary`. That's why weekday→preset is seven explicit string
properties on `UserLimitConfig`.

**Never seed a config collection in the `PluginConfiguration` constructor.** The XML
serializer *appends* to an already-populated collection, so constructor-seeded defaults
duplicated on every restart. Seeding happens exactly once in `Plugin.MigrateConfiguration()`,
guarded by the `Initialized` flag; that method also de-duplicates presets by id for installs
that already suffered the old bug.

**User ids are Guid "N" format** (32 hex chars, no dashes, lowercase) everywhere in state,
wallets and file names. `Plugin.FindUser` and `SettingsController.Normalize` strip dashes
and lowercase before comparing. Config `UserLimitConfig.UserId` may be either form —
normalize before matching. `UserName` in config is display-only cache; `UserId` is
authoritative.

**Bonus is ONE day-wide pool.** `DailyState.BonusConsumedSeconds` tracks how much of it has
been spent. A playing second that exceeds *any* base cap drains the pool once, even when it
exceeds several caps at the same time. `LimitCalculator.AvailableBonus` excludes a cap's own
overage (already inside `used`) to avoid double-counting. Without this, one redeem lifted
every window and every sitting by its full value. `BaseRemaining` exists solely so the
tracker can tell which seconds were running on bonus.

**Zero caps must not read as "already over".** `HardBlockEnforcer.IsOverPersistentLimit`
guards every cap check with `usage > 0`. A cap of 0 (School Day's morning window) means "no
watching in this scope" — enforced at play time by the tracker — and must never hard-block
an account that has watched nothing, or the kid can't even log in. The **session** cap is
deliberately excluded from hard blocking: it's a "take a break" signal, not "done for the day".

**Hard blocks save originals before touching anything.** `BlockAsync` writes
`<data>/enforcement/<userId>.json` (mode + `EnableMediaPlayback` + non-marker schedules)
*before* mutating the policy; the file's existence is the authoritative "we own this block"
marker; `UnblockAsync` restores verbatim and deletes it. `LoadOriginals` still parses the
legacy bare-array format. A parent "Stop now" always hard-blocks regardless of the opt-in
setting.

**`StateStore.DayFinished` runs under the store lock.** Handlers must not call back into
`StateStore`. `RefundFinishedDay` only touches `WalletStore`.

**Store locking style.** `StateStore.Mutate` / `WalletStore.Mutate` run the caller's action
under the lock and mark dirty; snapshots are cloned before being handed out or written. Daily
state is **debounced** (~20 s, `force: true` on the maintenance tick and after grants); wallets
are **written through immediately** — earned coins must survive a crash. Both write via
`*.tmp` + `File.Move(overwrite: true)`.

**Two auth schemes, both on `[AllowAnonymous]` controllers.** Parent endpoints check the
shared `BonusApiToken` via `?token=` or the `X-KidsLimit-Token` header; **an empty configured
token disables the API entirely** (returns false, not "allow"). Kid endpoints
(`/KidsLimit/kid…`) use the per-user `KidToken` and may only touch that kid's own wallet,
claims, and redeems. `SettingsController.SaveSettings` deliberately ignores an empty posted
`BonusApiToken` so a parent can't lock themselves out of the page they're editing from.
One-tap approve/decline links use a per-claim random `ActionKey` that dies once handled.

**Embedded resources.** All four HTML pages and `Web/clipart/*.{svg,png,webp,jpg}` are
embedded. The clipart globs mean **adding art needs no csproj edit**. Resource names are
namespace-dotted (`Jellyfin.Plugin.KidsLimit.Web.kid.html`); MSBuild can mangle `-` to `_`
in file names, which `ChoreClipart.BuildResolved` tolerates. Raster formats win over `.svg`
for the same key, so new art supersedes legacy line art with no code change.

**Jellyfin 10.11 API specifics** already worked out — don't regress them:
`Jellyfin.Database.Implementations.Entities.User` (the entity namespace moved in 10.11),
`IUserManager.GetUsers()` for enumeration, `IUserManager.GetUserDto(user).Policy` +
`UpdatePolicyAsync`, `ITranscodeManager.KillTranscodingJobs`,
`ISessionManager.CloseLiveStreamIfNeededAsync`.

## Conventions

- **XML doc comments on every public type and member**, including parameters and returns —
  the codebase is uniformly documented even though `GenerateDocumentationFile` is off. Match it.
- Comments explain *why*, especially where a subtlety bit someone before. The existing
  comments are load-bearing documentation; don't strip them when refactoring.
- `Nullable` and `ImplicitUsings` are enabled, but files still write explicit `using`
  directives — follow the surrounding file.
- Explicit `StringComparison` / `CultureInfo` on string ops (the code was written as if
  CA rules were on).
- Services swallow-and-log rather than throw: a plugin fault must never take down playback
  or the maintenance loop. Keep `try`/`catch` + `_logger.LogWarning` at those boundaries.
- Log messages are prefixed `KidsLimit:` and use structured placeholders.
- Keep `LimitCalculator` pure and side-effect free — both the tracker and the API depend on
  it agreeing with itself.

## Front-end notes

The three parent/kid pages are hand-written vanilla HTML/CSS/JS in single files — no build
step, no framework, no dependencies. Changing UI means editing the embedded HTML.

- `Configuration/configPage.html` and `Web/dashboard.html` run inside the Jellyfin admin UI
  and use `ApiClient.getPluginConfiguration(pluginId)` / `updatePluginConfiguration` and
  `ApiClient.getUrl(...)`. `Web/dashboard.html` is also surfaced in the admin sidebar
  (`EnableInMainMenu` in `Plugin.GetPages`).
- `Web/parent.html` has **no Jellyfin session**: it reads and writes everything through
  `/KidsLimit/settings` and the rest of the token-authed API. Any new setting must be added
  to `SettingsDto`, `SettingsResponseDto`, and `SettingsController.SaveSettings` (with
  sanitising) to be editable there — not just to the admin config page.
- `Web/kid.html` targets a non-reading child on a TV remote: picture-only tiles, D-pad
  arrow-key focus handling, focus preserved across refreshes. `/KidsLimit/kid/state` is
  deliberately cheap (no library query at all), which is what lets it poll every few
  seconds. Spending is one tile — a coin dropping into a telly — which opens the **spend
  clock**: a real analog face whose gold wedge sweeps forward from the minute hand by the
  time being bought, ▲/▼ = ±1 coin, opening at `DefaultSpendCoins`. Time as a shape is
  readable before arithmetic is; the poster wall it replaced asked a different sum per
  tile and a six-year-old could not convert. Prices still render as coins matched against
  her balance (solid = affordable, `.missing` = still needed), a spend animates coins from
  the piggy chip into the TV meter, and focusing the spend tile previews the gain as a
  ghost segment on that meter. All polled endpoints send `no-store` and the poll loop
  re-arms itself per response (Android freezes backgrounded timers); anything that
  changes behind the child's back — a parent approving a chore — announces itself.
- Known duplication to keep in sync: `SUGGESTED_CHORES` in `configPage.html` must mirror
  `PluginConfiguration.DefaultChores()`. See `docs/ADDING-CHORES.md` §4.2.

## Adding things

**A chore's art:** drop `Web/clipart/<key>.webp` in and add one line to `ChoreClipart.Catalog`.
Nothing else is required — pickers, the kid page and the endpoint all read the catalog at
runtime. Full checklist in `docs/ADDING-CHORES.md`.

**A config setting:** add the property to `PluginConfiguration` (XML-friendly, defaulted in
the constructor — but never a pre-populated collection), then wire it into `configPage.html`,
`SettingsDto`/`SettingsResponseDto`, and `SettingsController` if the standalone parent page
should manage it too.

**An API endpoint:** put it on the controller matching its audience, pick the right auth
helper (`ParentAuthorized` vs `ResolveKid`), and add a row to the README's route table.

## Git workflow

- Develop on the assigned `claude/**` branch; `build.yml` runs there automatically.
- Never push to `main` directly — releases are cut from it.
- Don't touch `manifest.json` by hand; CI owns it.
