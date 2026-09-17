# Jellyfin Kids Watch-Time Limit Plugin

A Jellyfin **server** plugin that enforces **cumulative daily watch-time limits**
for kids — per user, per weekday, and per time-of-day window — with parent
"bonus time", a REST API, and a live parent dashboard.

The plugin **observes** playback in real time via `ISessionManager` and, when a
limit is reached, **stops the offending session** (best-effort on-screen warning
too). It never mutates the user's account or library permissions. See
[`REQUIREMENTS.md`](REQUIREMENTS.md) for the full specification.

Target: **Jellyfin 12.0.x** (net10.0). Built with the modern
`IHostedService` + `IPluginServiceRegistrator` model.

Jellyfin 12 retargeted the server to .NET 10, so a plugin built for 10.11 will
not load on it (and vice versa). Versions **3.0.0.0 and newer** of this plugin
are for **Jellyfin 12.0+**; the last build for **Jellyfin 10.11.x** is
**2.3.0.2**, which stays in the plugin repository manifest and is what a 10.11
server keeps being offered.

---

## Can the server actually stop your TV?

Everything here depends on it, so it is worth knowing rather than assuming — and
the answer is a property of your *client version*, which changes under you when
the app updates.

**Measured 2026-09-17 on Jellyfin 12 + the 12-beta2 Android TV client: yes.**
Stop, Pause and Seek are all honored, and on-screen messages render (emoji
included). Full per-mechanism results are in `REQUIREMENTS.md` §2.1.

To check it for yourself — after a client update, or on different hardware —
open the **stop-method test bench** on a phone while standing at the TV:

```
/KidsLimit/test?token=<parent token>
```

It fires one mechanism at a time (message, Stop, Stop ×5, Pause, Seek-to-end,
GoHome, Back, kill transcode, close live stream, end session, log the device
out) and reports per step whether the server accepted it, so a command that was
*rejected* is distinguishable from one that was accepted and *ignored*. The
panel at the top matters as much as the buttons: if **Play method** reads
`DirectPlay`, no server-side stream teardown can touch what is already playing.

There is also `spike/stop-test.sh` for a quick shell-only check.

The plugin re-sends Stop every five seconds while a kid is over limit, and also
tears the stream down server-side where it can — the session's transcoding job
is killed and any live stream closed, so transcoded/remuxed (HLS) playback
stalls within seconds even on an uncooperative client. The one case the server
cannot interrupt mid-stream is a **direct-played** static file on a client that
ignores Stop — see the trade-off below.

### How a limit is enforced

When a kid runs out of time the plugin puts a message on the TV, waits a few
seconds so it can be read, then tells the client to stop — and keeps telling it,
every five seconds, for as long as they are over. Nothing about the Jellyfin
account is changed: the kid can still log in, still browse, and simply cannot
play until they have time again. Pressing play again gets them stopped again,
with a different message.

Earlier versions also blocked the account at the server (access schedule /
playback permission), because the Android TV client used to ignore Stop. Bench
testing against the Jellyfin 12 client showed it honors Stop, Pause and Seek, so
that path was removed in 3.1 — see REQUIREMENTS.md §2.1 for the full results. If
you are upgrading from 3.0 or earlier while a block is active, the plugin
restores the original policy automatically on first start.

**The trade-off, stated plainly:** a **direct-played** file is the one stream the
server cannot interrupt on its own, so if a future client update starts ignoring
Stop again there is no fallback. Keep the **over-limit alert** switched on — it
is the only thing that will tell you.

### What the TV says

The client renders messages (emoji included) **only while something is playing**,
which is why the plugin announces *before* stopping. Four moments get four
messages — the near-limit warning, the limit being reached, a kid who is already
out of time pressing play, and a parent's **Stop now** — and all of them are
editable under *What the TV says* on either settings page. `{minutes}` and
`{name}` are substituted.

Repeat attempts get a shorter reading pause than the first stop, so pressing play
over and over cannot buy extra minutes.

---

## Building

The CI workflow (`.github/workflows/build.yml`) builds the DLL on every push and
uploads it as an artifact. To build locally you need the **.NET 10 SDK**
(Jellyfin 12 targets net10.0):

```bash
dotnet build Jellyfin.Plugin.KidsLimit.csproj -c Release
# → bin/Release/net10.0/Jellyfin.Plugin.KidsLimit.dll
```

## Installing

### Option A — Plugin repository (recommended, no manual DLL download)

1. In Jellyfin: **Dashboard → Plugins → Repositories → Add Repository**.
2. **Repository Name:** `Kids Watch-Time Limit` (anything you like)
3. **Repository URL:**
   ```
   https://raw.githubusercontent.com/benben42/jellyfin-kids-limit-plugin/main/manifest.json
   ```
4. Save, then go to **Dashboard → Plugins → Catalog**, find **Kids
   Watch-Time Limit** under *General*, and install it.
5. Restart Jellyfin, then configure it at **Dashboard → Plugins → Kids
   Watch-Time Limit**.

Jellyfin handles fetching and updating the plugin from there — new releases
just show up as updates in the catalog.

### Option B — Manual install

1. Download the DLL from the [latest release](https://github.com/benben42/jellyfin-kids-limit-plugin/releases/latest)
   (or build it yourself, see below).
2. Copy `Jellyfin.Plugin.KidsLimit.dll` into a folder under your Jellyfin
   `plugins` directory, e.g. `config/plugins/KidsLimit/`.
3. Restart Jellyfin.
4. Open **Dashboard → Plugins → Kids Watch-Time Limit** to configure.

### Cutting a release (maintainer)

Releases are cut **automatically from `main`**: bump `version:` in `build.yaml`
(and the `<Version>` properties in the `.csproj` to keep them in sync), merge to
`main`, and the `.github/workflows/release.yml` workflow does the rest — it sees
that no `v<version>` tag exists yet, builds the DLL, zips it, creates the tag
and the GitHub Release with the zip attached, and commits an updated
`manifest.json` (repository index) back to `main`. Pushes to `main` that don't
change the version are no-ops for the release workflow.

Pushing a `v*` tag by hand still works and releases exactly that tag, as does
running the workflow manually via *Actions → Release Plugin → Run workflow*
(leave the tag input empty to release the current `build.yaml` version).
`build.yaml` is the single source of truth for the plugin's name, GUID,
description, and target ABI used in the manifest.

## Configuration

**Dashboard → Plugins → Kids Watch-Time Limit** lets you set:

- **Global windows** — the two cut-points that split each day into
  morning / afternoon / evening (defaults 12:00 and 18:00).
- **Parent API token** — shared secret for the REST API (blank = API disabled).
- **Presets** — reusable named limit sets (daily / session / per-window caps;
  blank = unlimited). Ships with *School Day*, *Weekend*, *Holiday*,
  *Recovery Day*; each preset is a collapsible card (click to edit), and
  *Restore built-in presets* re-adds any of the four that are missing without
  creating duplicates.
- **Per-user limits** — each kid is a collapsible card: enable them, assign a
  preset to each weekday, add date overrides (sick day / holiday), and set the
  warn-minutes threshold. Users left disabled are unlimited adults.
- **Over-limit alert** — when a kid keeps actively playing more than N minutes
  (default 3) past their limit — i.e. the Stop command is being ignored — a push
  notification is sent to the configured notification targets. One alert per
  sitting. With no server-side fallback left, this is the only backstop there is;
  leave it on.

Everything above can also be edited from the standalone parent page (below)
under **⚙️ Settings** — no Jellyfin admin login needed, just the parent token.

## Parent dashboard

A standalone page at **Dashboard → Plugins → …** → *parent dashboard* link, or
directly:

```
/web/index.html#!/configurationpage?name=KidsLimitDashboard
```

Per kid it shows today's used/remaining, current session, active preset,
per-window usage, a 7-day average, and **+10 / +30 / +60 / custom bonus** and
**Stop now** buttons. **Stop now** shows the kid a message, stops their
sessions, and holds — every fresh press of play is stopped again — until you
press **Allow again**, grant bonus, or local midnight. The card flips to an
**Allow again** button while a kid is stopped. No account setting is touched.

### Direct access (no Plugins drill-down)

Jellyfin has no API to add a top-level sidebar entry. Add a **Custom Menu Link**
(Dashboard → General / Branding) pointing at the dashboard URL above for a
one-tap parent entry. (The community *Plugin Pages* plugin is an optional
alternative — not required.)

## Chore rewards (coins)

Kids can **earn coins for chores and bank them** — the balance never resets at
midnight — then spend them later as extra watch time (redeemed time flows
through the normal bonus mechanism). A TV-friendly, **picture-only kid page**
(`/KidsLimit/kid?token=…`, per-kid token from settings) lets a non-reading
child claim chores (emoji tiles → parent approves on the dashboard, with push
notifications to your phone via ntfy / Pushover / Gotify / Discord / Slack /
Telegram / Apprise API / generic webhook) and spend coins on watch time by
answering "how much telly?" on a **big analog clock**: the hands show the real
time, a gold wedge sweeps forward by the time she is buying, ▲/▼ move it one
coin at a time, and it opens already set to the amount you configure (default
3 coins = 15 minutes). Confirming grants the time and (best effort) resumes
what she was watching. `android-tv/` contains a minimal
sideloadable WebView wrapper so the kid page is an app on the TV launcher.
Design, rules (bank cap, daily redeem cap, midnight refund) and details:
[`REWARDS.md`](REWARDS.md).

## REST API

Auth is the shared token via `?token=` or the `X-KidsLimit-Token` header.
`user` may be a Jellyfin user id or name.

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/KidsLimit/bonus?user=&minutes=&token=` | Add bonus to daily + session (+ active window) |
| `GET`  | `/KidsLimit/status?token=` | Status for all enabled users |
| `GET`  | `/KidsLimit/status/{user}?token=` | Status for one user |
| `POST` | `/KidsLimit/stop?user=&token=` | Stop a user now and keep them stopped for the rest of the day |
| `POST` | `/KidsLimit/allow?user=&token=` | Lift a `stop` hold so the user can play again (no extra time granted) |
| `GET`  | `/KidsLimit/history/{user}?days=&token=` | Finished-day rollups (averages/history) |
| `GET`  | `/KidsLimit/wallet/{user}?token=` | Coin balance, pending claims, recent ledger |
| `POST` | `/KidsLimit/wallet/earn?user=&choreId=&token=` | Credit a chore's coins directly |
| `POST` | `/KidsLimit/wallet/adjust?user=&coins=&note=&token=` | Manual coin correction (± ) |
| `POST` | `/KidsLimit/wallet/redeem?user=&coins=&token=` | Spend coins as bonus time now |
| `POST` | `/KidsLimit/claims/approve?user=&claimId=&token=` | Approve a kid's chore claim |
| `POST` | `/KidsLimit/claims/reject?user=&claimId=&token=` | Reject a kid's chore claim |
| `GET`  | `/KidsLimit/items/search?q=&token=` | Library search for reference titles |
| `POST` | `/KidsLimit/notify/test?token=` | Send a test push to all notification targets |
| `GET`  | `/KidsLimit/settings?token=` | Full plugin configuration + all Jellyfin users |
| `POST` | `/KidsLimit/settings?token=` | Replace the plugin configuration (parent settings page) |
| `GET`  | `/KidsLimit/test?token=` | Stop-method test bench page (see below) |
| `GET`  | `/KidsLimit/test/methods?token=` | Catalog of testable stop methods + targetable users |
| `GET`  | `/KidsLimit/test/sessions?user=&token=` | Live session diagnostics (play method, remote-control support) |
| `POST` | `/KidsLimit/test/run?user=&method=&token=` | Fire one stop method and report each step's outcome |

Kid self-service endpoints (`/KidsLimit/kid…`) use the per-user kid token
instead and only allow viewing the own wallet, claiming a chore and redeeming
for a configured reference title.

One-tap phone shortcut / NFC / Home Assistant example:

```
POST http://server:8096/KidsLimit/bonus?user=Ada&minutes=30&token=YOURSECRET
```

### Stop-method test bench

Whether a TV client honors the server's "stop playing" command is a property of that
client's version, not of this plugin — and it changes under you when the client updates.
Open `/KidsLimit/test?token=…` on a phone (there is also a button on the parent page,
under **Addresses & access**) while standing in front of the TV, start something playing,
and fire the methods one at a time:

- **Is the client listening?** — pops a message on the TV. If the message appears but Stop
  does nothing, the command channel is fine and the client is ignoring Stop specifically.
- **Playstate commands** — Stop, Stop × 5, Pause, Seek-to-end.
- **Navigation commands** — Go home, Back, Stop-then-Go-home.
- **Pull the stream out from under it** — kill the transcode job, close the live stream,
  or run exactly what enforcement sends today.
- **Kill the session** — end the session, close it, or log the device out entirely.
- **Policy blocks** — disable media playback, or drive the access schedule to
  "never allowed" (with or without an accompanying stream kill).

The panel at the top is as important as the buttons: if **Play method** reads `DirectPlay`,
no amount of server-side stream teardown can interrupt what is already playing, and if
**Accepts remote control** reads `NO` the client never agreed to be remote-controlled at all.

Every lasting effect is undone by **Release everything** at the bottom of the page (which
then re-applies whatever the kid's real limits still call for). The one exception is
*Log this device out*, which requires signing the TV back in by hand. Record what happened
after each method with the stopped / partly / nothing buttons and use **Copy report** to
get a pasteable summary.

## How it works (brief)

- Subscribes to `PlaybackStart` / `PlaybackProgress` / `PlaybackStopped`.
- Credits **only active (playing) time** — paused/buffering/menu time is ignored.
- Accumulates into per-user daily state (total, per-window, per-session),
  persisted (debounced) to the plugin data folder so a restart loses ≤ a few
  seconds. Rolls over at local midnight (per-event + a 30 s maintenance timer).
- On each tick, computes the most-restrictive remaining of the applicable
  session / daily / window caps (bonus added to daily, session, **and** the
  active window). At `≤ 0` → **Stop**; near the threshold → best-effort warning.

## Non-goals

Offline/downloaded playback and Live TV are invisible to the server and out of
scope. The plugin never disables accounts or unselects libraries — enforcement
is strictly at the session level.

## Credits

The built-in chore pictures (`Web/clipart/*.svg`) are adapted from the
[Mulberry Symbol Set](https://mulberrysymbols.org/) © Garry Paxton / Steve Lee,
licensed under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).
Each SVG (symbol composed onto a colored plate) remains CC BY-SA 4.0.
