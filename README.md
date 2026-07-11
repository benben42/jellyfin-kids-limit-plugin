# Jellyfin Kids Watch-Time Limit Plugin

A Jellyfin **server** plugin that enforces **cumulative daily watch-time limits**
for kids — per user, per weekday, and per time-of-day window — with parent
"bonus time", a REST API, and a live parent dashboard.

The plugin **observes** playback in real time via `ISessionManager` and, when a
limit is reached, **stops the offending session** (best-effort on-screen warning
too). It never mutates the user's account or library permissions. See
[`REQUIREMENTS.md`](REQUIREMENTS.md) for the full specification.

Target: **Jellyfin 10.11.x** (net9.0). Built with the modern
`IHostedService` + `IPluginServiceRegistrator` model.

---

## ⚠️ Phase 0 — validate the load-bearing risk FIRST

Everything here depends on the server being able to **Stop playback on the
household's Android TV**. That client has historically reported
`SupportsRemoteControl=False` and swallowed `DisplayMessage` toasts. Before
relying on this plugin, run the spike:

```bash
export JELLYFIN_URL="http://your-server:8096"
export JELLYFIN_TOKEN="<admin API key from Dashboard → API Keys>"

# Start a video on the Android TV, then:
./spike/stop-test.sh list                 # find its session Id
./spike/stop-test.sh stop <sessionId>     # does the TV actually stop?
./spike/stop-test.sh pause <sessionId>    # does Pause work?
./spike/stop-test.sh message <sessionId>  # does a toast appear?
```

- **Stop works** → you're good; the plugin's monitor→kill model works.
- **Stop does NOT work** → the polite command can't reach the primary client
  (see `REQUIREMENTS.md` §2.1 / §12). Use **Hard enforcement** (below) instead.

The plugin also re-sends Stop on every progress tick (~10 s) while over limit, so
a client that ignores a single Stop still gets stopped repeatedly. In addition,
whenever a session is over limit (or a parent presses **Stop now**) the plugin
**tears the stream down server-side**: the session's transcoding job is killed
and any live stream is closed, so transcoded/remuxed (HLS) playback stalls and
stops within seconds even on clients that ignore every command — no player
restart needed. The one case the server cannot interrupt mid-stream is a
**direct-played** static file on a client that ignores Stop; that's what hard
enforcement is for.

### Hard enforcement (for clients that ignore Stop, e.g. Android TV)

If your TV swallows the Stop command, enable **Hard-block kids at the server**
in plugin settings (off by default). When a kid is over their **daily or
window** limit, the plugin blocks that user at the server, in one of two
selectable modes:

- **Block all access (access schedule)** — flips the user's native Jellyfin
  access schedule to "never allowed". Jellyfin validates the schedule on every
  request, so even an already-running stream dies at its next range/segment
  request. Most forceful, but while blocked the kid is locked out of Jellyfin
  entirely (browsing too, and some clients drop to an error/login screen).
- **Block playback only** — turns off the user's *media playback* permission
  instead. Nothing new can start (including auto-play of the next episode) and
  the kid can still browse the library, getting a normal "playback not allowed"
  error when they press play. Gentler, but a client that ignores Stop may finish
  the item it is currently direct-playing before the block bites.

The block is applied immediately when a limit is crossed (not just on the next
maintenance tick), and is released automatically when the kid is back under
limit, is granted bonus, or at local midnight. Whatever the plugin changes
(schedules and/or the playback permission) is saved first and restored verbatim
on release. **Turn the option off and Save before uninstalling the plugin** so
no kid is left locked out. The dashboard's **Stop now** button always applies a
hard block using the selected mode, regardless of the on/off setting.

---

## Building

The CI workflow (`.github/workflows/build.yml`) builds the DLL on every push and
uploads it as an artifact. To build locally you need the **.NET 9 SDK**
(Jellyfin 10.11 targets net9.0):

```bash
dotnet build Jellyfin.Plugin.KidsLimit.csproj -c Release
# → bin/Release/net9.0/Jellyfin.Plugin.KidsLimit.dll
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

## Parent dashboard

A standalone page at **Dashboard → Plugins → …** → *parent dashboard* link, or
directly:

```
/web/index.html#!/configurationpage?name=KidsLimitDashboard
```

Per kid it shows today's used/remaining, current session, active preset,
per-window usage, a 7-day average, and **+10 / +30 / +60 / custom bonus** and
**Stop now** buttons. **Stop now** stops the kid's sessions (commands + server-
side stream teardown) and applies a hard block using the configured hard-block
mode, so playback stops even on clients that ignore the Stop command, and holds
until you press **Allow again**, grant bonus, or local midnight — the card
flips to an **Allow again** button while a kid is stopped.

### Direct access (no Plugins drill-down)

Jellyfin has no API to add a top-level sidebar entry. Add a **Custom Menu Link**
(Dashboard → General / Branding) pointing at the dashboard URL above for a
one-tap parent entry. (The community *Plugin Pages* plugin is an optional
alternative — not required.)

## REST API

Auth is the shared token via `?token=` or the `X-KidsLimit-Token` header.
`user` may be a Jellyfin user id or name.

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/KidsLimit/bonus?user=&minutes=&token=` | Add bonus to daily + session (+ active window) |
| `GET`  | `/KidsLimit/status?token=` | Status for all enabled users |
| `GET`  | `/KidsLimit/status/{user}?token=` | Status for one user |
| `POST` | `/KidsLimit/stop?user=&token=` | Immediately stop a user and hard-block them for the rest of the day |
| `POST` | `/KidsLimit/allow?user=&token=` | Lift a `stop` hold so the user can play again (no extra time granted) |
| `GET`  | `/KidsLimit/history/{user}?days=&token=` | Finished-day rollups (averages/history) |

One-tap phone shortcut / NFC / Home Assistant example:

```
POST http://server:8096/KidsLimit/bonus?user=Ada&minutes=30&token=YOURSECRET
```

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
