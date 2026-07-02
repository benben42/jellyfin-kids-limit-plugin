# Jellyfin Kids Watch-Time Limit Plugin

A Jellyfin **server** plugin that enforces **cumulative daily watch-time limits**
for kids — per user, per weekday, and per time-of-day window — with parent
"bonus time", a REST API, and a live parent dashboard.

The plugin **observes** playback in real time via `ISessionManager` and, when a
limit is reached, **stops the offending session** (best-effort on-screen warning
too). It never mutates the user's account or library permissions. See
[`REQUIREMENTS.md`](REQUIREMENTS.md) for the full specification.

Target: **Jellyfin 10.10.x** (net8.0). Built with the modern
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
- **Stop does NOT work** → the core enforcement can't reach the primary client
  (see `REQUIREMENTS.md` §2.1 / §12). Resolve that before depending on limits.

The plugin also re-sends Stop on every progress tick (~10 s) while over limit, so
a client that ignores a single Stop still gets stopped repeatedly.

---

## Building

The CI workflow (`.github/workflows/build.yml`) builds the DLL on every push and
uploads it as an artifact. To build locally you need the **.NET 8 SDK**:

```bash
dotnet build Jellyfin.Plugin.KidsLimit.csproj -c Release
# → bin/Release/net8.0/Jellyfin.Plugin.KidsLimit.dll
```

## Installing

1. Copy `Jellyfin.Plugin.KidsLimit.dll` into a folder under your Jellyfin
   `plugins` directory, e.g. `config/plugins/KidsLimit/`.
2. Restart Jellyfin.
3. Open **Dashboard → Plugins → Kids Watch-Time Limit** to configure.

(Packaging into a proper repo zip via
[`jprm`](https://github.com/oddstr13/jellyfin-plugin-repository-manager) using
`build.yaml` is also supported.)

## Configuration

**Dashboard → Plugins → Kids Watch-Time Limit** lets you set:

- **Global windows** — the two cut-points that split each day into
  morning / afternoon / evening (defaults 12:00 and 18:00).
- **Parent API token** — shared secret for the REST API (blank = API disabled).
- **Presets** — reusable named limit sets (daily / session / per-window caps;
  blank = unlimited). Ships with *School Day*, *Weekend*, *Holiday*,
  *Recovery Day*.
- **Per-user limits** — enable a kid, assign a preset to each weekday, add
  date overrides (sick day / holiday), set the warn-minutes threshold. Users
  left disabled are unlimited adults.

## Parent dashboard

A standalone page at **Dashboard → Plugins → …** → *parent dashboard* link, or
directly:

```
/web/index.html#!/configurationpage?name=KidsLimitDashboard
```

Per kid it shows today's used/remaining, current session, active preset,
per-window usage, a 7-day average, and **+10 / +30 / +60 / custom bonus** and
**Stop now** buttons.

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
| `POST` | `/KidsLimit/stop?user=&token=` | Immediately stop a user's playback |
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
