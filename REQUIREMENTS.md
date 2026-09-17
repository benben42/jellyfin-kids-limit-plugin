# Jellyfin Kids Watch-Time Limit Plugin — Requirements

> **Purpose of this document:** a complete, self-contained specification so the
> plugin can be built in a fresh session without any prior chat context. It
> captures every decision made during brainstorming, the technical constraints
> of the Jellyfin plugin platform, and the one load-bearing risk that must be
> validated **before** building anything else.

---

## 1. Goal

A Jellyfin server plugin that enforces **cumulative daily watch-time limits** for
kids, with per-user, per-weekday, per-time-of-day granularity, plus parent
override ("bonus time") and an at-a-glance dashboard. Admin (server owner) = parent.

The plugin **observes** playback in real time and, when a limit is reached,
**stops the offending session** and (best-effort) shows a warning. It never
mutates the user's account or library permissions.

---

## 2. Platform constraints (what Jellyfin actually allows)

These facts shaped every design decision. Confirm against the target Jellyfin
version at build time.

- **We can observe playback** via `ISessionManager` events: `PlaybackStart`,
  `PlaybackProgress` (fires roughly every ~10 s while playing), `PlaybackStopped`.
  This is how watch time is accumulated. Enforcement precision is therefore
  ~10 s, which is fine.
- **We cannot prevent a play from starting.** There is no "ask permission before
  play" hook. Enforcement is necessarily **reactive**: detect the session, then
  send it a **Stop/Pause** command (`ISessionManager.SendPlaystateCommand`) and,
  best-effort, a **display message** (`SendMessageCommand` / `GeneralCommand`
  `DisplayMessage`).
- **Persistence + background work + REST:** a plugin can run a hosted background
  service and expose **custom REST controllers** (`BaseController`). State is
  stored in the plugin's own data directory and must survive server restarts.
- **Target modern Jellyfin (10.9+/10.10.x):** use `IHostedService` +
  `IPluginServiceRegistrator` (NOT the deprecated `IServerEntryPoint`). The main
  plugin class (`BasePlugin<TConfig>`) cannot itself be the hosted service.
- **Config page** lives by default under Dashboard → Plugins → (this plugin).
  See §11 for making it directly accessible.

### 2.1 THE LOAD-BEARING RISK — resolved: the TV honors Stop

The household's primary client is **Jellyfin Android TV**. Historically that
client advertised `SupportsRemoteControl=False` / `SupportsMediaControl=False`
and swallowed server-sent commands, and **everything in this plugin depends on
the server being able to stop playback on the actual TV** — so this was the
first thing to validate and the reason the hard-block fallback ever existed.

**Answered, 2026-09-17, against the Jellyfin 12 server and the 12-beta2 Android
TV client**, by firing each mechanism separately from the stop-method test bench
(`/KidsLimit/test`) with the TV in front of the tester:

| Mechanism | Result |
|---|---|
| `DisplayMessage` | **Renders** — including emoji. Only while something is playing. |
| Playstate `Stop` | **Stops playback.** |
| Playstate `Stop` ×5 | Stops. |
| Playstate `Pause` | Stops (freezes). |
| Playstate `Seek` to end | Ends the item. |
| `GoHome` / `Back` general commands | Ignored — the client does not implement them. |
| Kill transcode job | Nothing (the session was DirectPlay — no job to kill). |
| Close live stream | Nothing (not Live TV). |
| `ReportSessionEnded` / `CloseIfNeeded` | Nothing; the client re-registers. |
| Log the device out | Nothing — the app stayed logged in and kept playing. |
| Disable media playback (policy) | Nothing to the in-flight stream, as expected. |

So the "monitor → stop" model works on the primary client, and the
account-disabling hammer §12 rejects on principle is not needed in practice.
**The hard block has been removed** (see §5) and the plugin no longer modifies
any Jellyfin account setting.

**The standing risk this leaves.** A direct-played file is the one stream the
server cannot interrupt without the client's cooperation, and there is now no
fallback if a client update regresses. The over-limit watchdog (§8) is the
entire safety net: if a kid keeps accruing time past the limit, the parent is
notified that auto-stop appears to have failed. **Re-run the test bench after
every Android TV client update** — the answer above is specific to a client
version, not permanent.

---

## 3. Core concepts / glossary

- **User** = a Jellyfin user account. Limits are per-user. One kid = one account.
- **"Watching"** = active playback only (state = playing). **Paused, buffering,
  and menu/browse time do NOT count.** There is no Live TV in this household
  (out of scope regardless).
- **Preset** = a named, reusable set of limits (see §4). E.g. "School Day".
- **Weekly schedule** = maps each weekday (Mon–Sun) to a preset.
- **Date override** = maps a specific calendar date to a preset; wins over the
  weekly schedule for that date. This is what makes "sick day" / holidays work.
- **Window** = a time-of-day band within a single day (morning / afternoon /
  evening). Window boundaries are **global** (server-wide) but configurable.
- **Bonus** = parent-granted extra time added on top of limits for the current
  day; expires at midnight (see §7).
- **Timezone / day boundary:** single server-local timezone. Daily counters and
  bonuses **reset at local midnight**. Keep it simple — no per-user timezones.

---

## 4. Data model

### 4.1 Preset
A named limit set. Any cap may be **null = unlimited**.

```
Preset {
  id: string
  name: string                 // e.g. "School Day"
  dailyCapMinutes:   int?       // max total playing minutes in the day
  sessionCapMinutes: int?       // max playing minutes in a single session
  windowCaps: {
    morning:   int?             // max playing minutes in the morning window
    afternoon: int?
    evening:   int?
  }
}
```

**Shipped default presets** (editable/removable by the parent):
`School Day`, `Weekend`, `Holiday`, `Recovery Day` (the "sick day" preset —
final name is the parent's choice; "Duvet Day" was also floated).

### 4.2 Global window definitions
The day (00:00–24:00) is fully partitioned into three windows by **two
configurable cut-points** (so there are never gaps or overlaps):

```
WindowConfig {
  middayStart:  time   // default 12:00  → morning ends / afternoon begins
  eveningStart: time   // default 18:00  → afternoon ends / evening begins
}
// morning   = [00:00, middayStart)
// afternoon = [middayStart, eveningStart)
// evening   = [eveningStart, 24:00)
```

Note: early-morning hours (e.g. 05:00) fall in the "morning" window by design.

### 4.3 Per-user configuration

```
UserLimitConfig {
  userId: string
  enabled: bool                 // false / absent = UNLIMITED = treated as adult
  weeklySchedule: { mon..sun -> presetId }
  dateOverrides:  { "YYYY-MM-DD" -> presetId }
  warnMinutesBeforeLimit: int   // default 10
}
```

**Default posture:** if a user has no config or `enabled=false`, they have **no
limits** (adults are simply never enabled). Limits are strictly opt-in per kid.

### 4.4 Runtime state (persisted; survives restart)

Per user, for the current day:

```
DailyState {
  userId: string
  date: "YYYY-MM-DD"                 // used to detect rollover at midnight
  secondsWatchedTotal: int
  secondsWatchedByWindow: { morning, afternoon, evening }
  dailyBonusSeconds: int             // granted bonus, expires at midnight
  sessionBonusSeconds: int           // granted bonus for the active session
  // transient per active session:
  activeSessions: { sessionId -> { secondsWatched, warned: bool, blocked: bool } }
}
```

Persist accumulated seconds on each progress event (debounced) so a server
restart loses at most a few seconds. On any event, if `state.date != today`,
**roll over** (reset totals + bonuses, keep config). A daily scheduled task is a
belt-and-suspenders backup for the rollover.

### 4.5 Plugin global config

```
PluginConfig {
  windowConfig: WindowConfig
  presets: Preset[]
  users:   UserLimitConfig[]
  bonusApiToken: string          // shared secret for the REST bonus endpoint (§6)
}
```

---

## 5. Enforcement logic

On every `PlaybackProgress` event where the play state is **playing**:

1. Resolve the user and session. If the user is not `enabled` → **no limits**, return.
2. Resolve **today's active preset**:
   `dateOverrides[today] ?? weeklySchedule[weekdayOf(today)]`.
3. Determine the **current window** from the clock + `WindowConfig`.
4. Accumulate elapsed playing time into: this session's seconds, `secondsWatchedTotal`,
   and `secondsWatchedByWindow[currentWindow]`. Persist (debounced).
5. Compute remaining for each **non-null** applicable cap:
   - `sessionRemaining = sessionCap + sessionBonus − sessionSeconds`
   - `dailyRemaining   = dailyCap   + dailyBonus   − secondsWatchedTotal`
   - `windowRemaining  = windowCap[window] + (bonus applies, see below) − windowSeconds[window]`
6. `effectiveRemaining = min(all applicable non-null remainings)`.
7. **If `effectiveRemaining <= 0`** → send **Stop** to the session (+ best-effort
   `DisplayMessage`); mark session `blocked`.
8. **Else if `effectiveRemaining <= warnMinutesBeforeLimit` and not yet warned**
   → send best-effort `DisplayMessage` warning AND flag a **parent-facing**
   notification (the on-TV toast is unreliable — see §8); mark `warned`.

**Most-restrictive-wins** is implicit in the `min()` at step 6.

### 5.1 How bonus interacts with caps (decision)

A parent granting "+30" means *"the kid gets 30 more minutes of playable time
right now."* To honor that intent, **bonus is added to the daily, session, AND
the currently-active window's remaining** — so a granted bonus always actually
yields playable time and never gets silently swallowed by a window cap. Bonus
expires at local midnight. (If a future toggle to scope bonus more narrowly is
wanted, note it then; this is the sensible default.)

### 5.2 After a hard stop

No auto-resume. If the parent grants bonus, the kid simply **presses play
again**; the monitor now sees budget > 0 and allows it.

### 5.3 Multiple simultaneous sessions

Not expected in this household, but handle safely by **summing** concurrent
sessions into the daily/window totals (each session also has its own session cap).

---

## 6. Parent API (REST)

Custom controller under a route like `/KidsLimit`. **Auth = a shared secret
token** set in plugin config (`bonusApiToken`) — deliberately simple so a phone
home-screen shortcut, an NFC tag by the TV, or Home Assistant can grant time
with one tap. (Optionally also accept a valid Jellyfin admin API key.)

Minimum endpoints:

```
POST /KidsLimit/bonus?user={id|name}&minutes={10|30|60|custom}&token={secret}
     → adds `minutes` to today's daily + session (+ active window) budget.

GET  /KidsLimit/status?token={secret}
     → JSON for all enabled users: today's used, remaining, current session,
       active preset, per-window usage, bonus granted. (Feeds the dashboard and
       external integrations.)

GET  /KidsLimit/status/{user}?token={secret}   → single user.
```

Optional/nice-to-have: `POST /KidsLimit/stop?user=…` (manual immediate stop).

Dashboard buttons (§9), when used by a logged-in admin, ride the existing admin
session and need no separate token.

---

## 7. Bonus semantics (summary)

- Granularity: **+10 / +30 / +60 min** quick buttons, plus a custom amount.
- Scope: adds to **daily + session (+ active window)** budgets — see §5.1.
- **Expires at local midnight** (reset with the daily rollover).
- Granted via dashboard buttons or the REST endpoint (§6).

---

## 8. Warnings & what the TV actually shows

The 12-beta2 Android TV client **does** render `DisplayMessage`, emoji included
(§2.1) — the old assumption that it was a no-op there is dead. Two constraints
shape how it is used:

- **A message only draws while something is playing.** A message sent *after* a
  Stop lands on a dead player and is never seen. So the plugin announces first,
  waits `StopGraceSeconds` (default 6) so it can be read, and only then stops.
  This was the actual bug behind "the TV just switches itself off".
- **The child cannot read.** The messages lead with emoji (⏰ 🛑 📺💤 🪙) and
  keep words to a minimum. All eight strings are editable from either settings
  page, so they can be reworded or translated without a rebuild.

Three distinct moments get three distinct messages: the near-limit warning, the
limit being reached, and a child who is already out of time pressing play again
("TV is sleeping", not "time's up" repeated). A parent's **Stop now** gets a
fourth. Repeats are throttled to one per 30 s per session — the *stopping*
repeats every sweep, the *talking* does not — and a repeat attempt gets a much
shorter reading pause, so pressing play over and over cannot buy extra minutes.

The warning remains a courtesy and a parent-facing signal; **the stop is the
enforcement**, reinforced by routine.

---

## 9. Parent dashboard

A single page showing, per enabled user:

- Today's **used / remaining** (against the binding cap), current **session**
  time, the **active preset** name, and **per-window** usage.
- **Bonus buttons**: +10 / +30 / +60 / custom, and a **manual Stop** button.
- **History / averages** (requirement: "day averages, how many hours already
  watched"). Prefer computing rolling history from our own persisted daily
  rollups; **optionally** enrich with the **Playback Reporting** plugin's SQLite
  data if installed (the household already runs Playback Reporting + Reports).
  Playback Reporting is a *nice-to-have data source, never a hard dependency* —
  our real-time tracking is authoritative.

---

## 10. Configuration UX

- **Presets:** create/edit/delete named limit sets.
- **Weekly schedule:** per user, assign a preset to each weekday (Mon–Sun).
- **Date overrides:** per user, assign a preset to a specific date (sick day,
  holiday, snow day, grounded, etc.). Overrides beat the weekly schedule.
- **Global windows:** edit the two cut-points (§4.2).
- **Per-user enable** toggle (default off = unlimited/adult).
- **Bonus API token** field.

The preset layer is what keeps the 7-day × 3-window × N-user matrix manageable:
define "School Day" / "Weekend" / etc. once, then assign.

---

## 11. Direct access (avoid Dashboard → Plugins drill-down)

Jellyfin has **no first-class API for a plugin to add a top-level sidebar
entry.** Approach:

1. Build the dashboard as a **standalone page reachable by URL**.
2. **Recommended:** add a built-in **Custom Menu Link** (Dashboard → General /
   Branding) that deep-links straight to that page — one-time manual setup, zero
   extra dependencies, gives the direct entry the parent wants.
3. **Optional later:** support the community **Plugin Pages** plugin
   (`IAmParadox27/jellyfin-plugin-pages`) for a natively-themed in-UI page — but
   it drags in two extra plugin installs (Plugin Pages + File Transformation),
   so it's opt-in, not default.

---

## 12. Non-goals / explicit limitations

- **Offline / downloaded playback is invisible to the server** — cannot be
  counted or stopped. Out of scope.
- **Live TV** — none in this household; out of scope.
- **Non-Jellyfin playback** — out of scope.
- **Do NOT enforce by mutating account state.** Explicitly rejected approach
  (this is what the JellyWatchWise standalone app does — it unselects the user's
  libraries or disables the account). It's fragile (state stuck if the process
  crashes), side-effecting, wrong-altitude ("your account is broken" vs "time's
  up"), and coarse. We enforce at the **session** level only and never touch the
  user's account/library configuration.
- **No prevention of starting playback** — only reactive stop (platform
  limitation, §2).

---

## 13. Suggested build order

- **Phase 0 — Spike (blocking):** confirm the server can **Stop** playback on the
  household Android TV (§2.1). Go/no-go for everything else.
- **Phase 1 — Scaffolding:** plugin from the official template, `PluginConfig`,
  config page skeleton, `IHostedService` + service registrator.
- **Phase 2 — Tracking:** subscribe to `ISessionManager` events; accumulate
  playing time into `DailyState`; persist (debounced); midnight rollover.
- **Phase 3 — Enforcement:** the §5 algorithm — compute effective remaining,
  Stop at 0, best-effort warn at threshold.
- **Phase 4 — Config model:** presets, weekly schedule, date overrides, global
  windows, per-user enable; config-page UI.
- **Phase 5 — Bonus API:** REST controller + shared-secret auth; wire dashboard
  buttons.
- **Phase 6 — Dashboard:** per-user cards, bonus/stop buttons, history/averages
  (optional Playback Reporting enrichment).
- **Phase 7 — Polish:** custom menu link instructions; optional Plugin Pages
  support; docs.

---

## 14. Decisions log (quick reference)

| # | Decision |
|---|----------|
| Users | Per Jellyfin user; unlimited/unconfigured = adult |
| Watching | Active playback only; no paused/buffer/menu; no Live TV |
| Weekday limits | Via presets assigned per weekday |
| Sub-limits | Max session, max daily, per-window (morning/afternoon/evening) |
| Windows | Within-day bands; global configurable cut-points (12:00 / 18:00 default); no special "weekend" concept |
| Conflicts | Most-restrictive-wins (`min` of caps) |
| Bonus | +10/+30/+60/custom; adds to daily + session (+ active window); expires at midnight |
| Enforcement | Warn (best-effort, parent-facing on ATV) at 10 min left, then hard **Stop** |
| Reset | Local midnight; single server timezone |
| Mechanism | `ISessionManager` events + `SendPlaystateCommand` + best-effort `DisplayMessage`; never mutate account/library |
| Persistence | Plugin's own store; survives restart |
| Parent API | Shared-secret token; `POST /bonus`, `GET /status` |
| Dashboard access | Standalone page + custom menu link (Plugin Pages optional) |
| Playback Reporting | Optional history source, never a hard dependency |
| **#1 risk** | **Can the server stop playback on Android TV? Spike before building.** |
