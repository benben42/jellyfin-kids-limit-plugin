# The Jellyfin 12 port

Jellyfin 12.0 shipped on 2026-09-08. It is a platform break, not an API break — for this
plugin. Every server API the plugin touches is byte-identical between `10.11.11` and
`12.0.0`; the port is a retarget of the project file and the CI workflows, with **no C#
changes at all**.

This document records what was audited (so nobody re-derives it), why the 10.11 line had
to be dropped rather than kept alongside, and which of 12's new plugin APIs are actually
worth something here.

> **Partly superseded, 2026-09-17.** The audit below is a snapshot of the 3.0.0.0 port and
> is left intact as a record. Since then, bench-testing the 12-beta2 Android TV client
> showed it honors the Stop command, and `HardBlockEnforcer` — every row below that mentions
> it — was **deleted** in 3.1. The user-policy APIs it used (`UpdatePolicyAsync`,
> `AccessSchedule`, `EnableMediaPlayback`) are no longer called by anything. See
> `REQUIREMENTS.md` §2.1 for the measurements and the upgrade note in §3 below.

## 1. What changed in this repo

| File | Change |
|------|--------|
| `Jellyfin.Plugin.KidsLimit.csproj` | `net9.0` → `net10.0`; `Jellyfin.Controller` / `Jellyfin.Model` `10.11.11` → `12.0.0` |
| `build.yaml` | `targetAbi` `10.11.11.0` → `12.0.0.0`; `framework` `net9.0` → `net10.0` |
| `.github/workflows/build.yml` | SDK `9.0.x` → `10.0.x`; artifact path `bin/Release/net10.0/` |
| `.github/workflows/release.yml` | same SDK and zip-source path change |
| `README.md`, `CLAUDE.md` | target server, local build instructions, the ABI split below |

`scripts/update_manifest.py` needed nothing: it reads `targetAbi` out of `build.yaml`, so
the next tagged release writes a `12.0.0.0` manifest entry on its own.

## 2. The ABI split — one build cannot serve both

Jellyfin 12 retargeted the server to .NET 10. A `net9.0` plugin assembly does not load on
it, and a `net10.0` one does not load on 10.11, so there is no single artifact that covers
both server lines, and `build.yaml` carries exactly one `targetAbi`.

So the plugin repository splits by version:

- **`2.3.0.2`** — last build for **Jellyfin 10.11.x**. It stays in `manifest.json`
  untouched. A 10.11 server filters the manifest by `targetAbi` and keeps being offered
  this version; it does not see, and cannot install, anything newer.
- **`3.0.0.0` and up** — **Jellyfin 12.0+**. The first release off this branch must be
  tagged `v3.0.0.0`: a `2.3.x` successor would read as a patch to the 10.11 line, and the
  major bump is the only signal a user gets that the server requirement moved.

Nobody on 10.11 breaks — they stop receiving updates, which is the intended cost of the
server jump.

## 3. The compatibility audit

No .NET SDK exists in the usual dev container, so this was verified against the shipped
packages rather than by compiling: `Jellyfin.Controller`, `Jellyfin.Model`,
`Jellyfin.Common`, `Jellyfin.Data`, `Jellyfin.Database.Implementations` and
`Jellyfin.Extensions` were pulled from NuGet at both `10.11.11` and `12.0.0`, and their
public surfaces diffed — the XML documentation files for signatures, the assembly metadata
name heaps for the members Jellyfin does not document (`UserPolicy.EnableMediaPlayback` is
one). CI is the confirming compile.

Every API the plugin uses, and its status in 12.0.0:

| API | Used by | 12.0.0 |
|-----|---------|--------|
| `ISessionManager.Sessions`, `PlaybackStart` / `PlaybackProgress` / `PlaybackStopped` | `WatchTimeTracker` | unchanged |
| `ISessionManager.SendPlaystateCommand` / `SendMessageCommand` / `SendPlayCommand` | `PlaybackTerminator`, `WatchTimeTracker`, `RewardsService` | unchanged |
| `ISessionManager.CloseLiveStreamIfNeededAsync` | `PlaybackTerminator` | unchanged |
| `ITranscodeManager.KillTranscodingJobs` | `PlaybackTerminator` | unchanged |
| `IUserManager.GetUsers()` / `GetUserById` / `GetUserByName` | everywhere | unchanged |
| `IUserManager.GetUserDto(user).Policy` + `UpdatePolicyAsync` | `HardBlockEnforcer` | unchanged |
| `AccessSchedule` ctor, `DynamicDayOfWeek`, `UserPolicy.AccessSchedules` / `EnableMediaPlayback` | `HardBlockEnforcer` | unchanged |
| `ILibraryManager.GetItemList(InternalItemsQuery)` / `GetItemById` | `RewardsController`, `RewardsService` | unchanged (both overloads) |
| `IUserDataManager.GetUserData` | resume pricing, recently-watched | unchanged |
| `BaseItem.GetRecursiveChildren` / `GetBaseItemKind` / `GetImageInfo` / `GetParent` / `IsVisibleStandalone` | `RewardsService`, `RewardsController` | unchanged |
| `BasePlugin<T>`, `IHasWebPages`, `PluginPageInfo` (incl. `EnableInMainMenu`, `MenuIcon`, `DisplayName`) | `Plugin` | unchanged |
| `IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)` | `PluginServiceRegistrator` | unchanged |
| `Jellyfin.Database.Implementations.Entities.User` | `HardBlockEnforcer`, `RewardsService`, controllers | unchanged, still reached transitively via `Jellyfin.Model` → `Jellyfin.Data` |

### 12's breaking changes, and why none of them land here

- **`IUserManager.Users` / `UsersIds` became `GetUsers()` / `GetUsersIds()`.** The plugin
  was already calling the methods (`HardBlockEnforcer`, `SettingsController`). This was the
  single likeliest break and it was pre-paid.
- **Legacy authorization disabled by default; `/emby/*` and `/mediabrowser/*` prefixes
  removed.** Both plugin auth schemes ride on `[AllowAnonymous]` controllers with their own
  tokens (`BonusApiToken`, per-user `KidToken`), and no page sends a Jellyfin credential —
  `kid.html` pulls poster art and the avatar from `/KidsLimit/kid/image/{id}` and
  `/KidsLimit/kid/avatar`, which the plugin serves itself. Nothing in the repo uses
  `api_key=`, `X-Emby-Authorization`, or a legacy route prefix.
- **Removed API routes** (`EasyPassword`, `CriticReviews`, `NetworkShares`,
  `QuickConnect/Initiate`, …): none referenced.
- **`ISearchEngine` → `ISearchManager`, `IItemRepository` split into
  `IItemPersistenceService` / `IItemCountService` / `INextUpService`, `ISubtitleWriter`
  family removed, `AlphanumericComparator` removed, `IAuthenticationProvider.HasPassword`
  removed, `IPasswordResetProvider` signature change**: none of these are injected or
  implemented here.
- **Relational playlists/collections, `LinkedChildren`, dropped `ExtraIds`.** The plugin
  never touched serialized child lists; `GetRecursiveChildren` on a `Series` is unaffected.
- **Swashbuckle 10 / regenerated OpenAPI.** The plugin's controllers are described by the
  server's generator, not by anything checked in here.
- **Modern web layout is now the default.** `PluginPageInfo` still carries
  `EnableInMainMenu` / `MenuIcon` / `MenuSection`, so the dashboard sidebar entry is
  expected to survive — worth one visual check after upgrading, since it is the only part
  of the plugin whose behavior is decided by jellyfin-web rather than by the server API.

### Upgrade order for the household server

Jellyfin's own release notes ask for third-party plugins to be **removed before**
migrating to 12, and for a **full library scan afterwards**. For this plugin specifically:

1. Clear any parent "Stop now" hold with **Allow** in the dashboard before uninstalling.
   *(Up to 3.0 you also had to turn hard enforcement off and Save first, because an
   uninstalled plugin could not un-block anyone. 3.1 removed the hard block entirely and
   ships `LegacyBlockRestorer`, which restores any policy a 3.0-or-earlier block left
   behind on first start — so an install that was upgraded mid-block repairs itself.)*
2. Uninstall the plugin, upgrade the server, then install `3.0.0.0`+ from the repository.
3. State and wallets are plain JSON under the plugin data directory and are untouched by
   the server's database migrations — watch time, coins and history carry over.

## 4. What 12 offers this plugin

Ranked by what they would actually buy. None is required; all are additive.

### Worth doing

- **Next-up instead of a random episode.** `RewardsService.TryPlayAsync` currently picks a
  *random* episode when a kid spends coins on a series (`episodes[Random.Shared.Next(…)]`)
  — she pays for a series and can land mid-arc on something she has already seen. 12 adds
  `ILibraryManager.GetNextUpEpisodesBatch` (and `INextUpService`), so the next unwatched
  episode is reachable through the manager the service already injects, with no new DI.
  Worth being honest that `ITVSeriesManager.GetNextUp` existed in 10.11 too — this is a
  long-standing UX gap that 12 makes slightly cheaper to close, not a new capability.

- **Batched user data.** `IUserDataManager.GetUserDataBatch` and `GetResumeUserDataBatch`
  replace the per-item `GetUserData` loop in `RewardsController.RecentlyWatchedFront`, and
  `GetResumeUserData` is the purpose-built path for exactly what `RewardsService.BuildTitle`
  does when it prices "what it costs to *finish* this". Small win — the loop runs over
  three items — but it is the semantically right call and it is free.

### Possible, with a caveat

- **Search providers.** `ISearchProvider` / `IInternalSearchProvider`, registered through
  `ISearchManager.AddParts`, let a plugin extend or replace how search results are produced,
  in priority order ahead of the built-in `SqlSearchProvider`. A kid-aware provider could
  demote or hide what a kid cannot afford or is currently over-limit on. It is the first
  time this plugin *could* shape what a kid sees rather than only what she can play — which
  is precisely why it deserves a deliberate decision: the plugin's stated contract is that
  it observes playback and never mutates account or library state, and a search provider
  that hides titles is a visible break from that.

- **Server-locale localization.** `ILocalizationManager.GetServerLocalizedString` and
  `GetLanguageDisplayName` make it possible to localize the parent-facing pages against the
  server locale. `kid.html` is deliberately picture-only for a non-reading child, so this
  only concerns `parent.html`, `dashboard.html` and `configPage.html` — real work, real
  benefit only for a non-English household.

### Not applicable

Similarity and recommendation providers (`ILocalSimilarItemsProvider` and friends), comic
metadata (`IComicProvider`), chapters for non-video items, Schedules Direct
(`ISchedulesDirectService`), media-segment cleanup, alternate-version and linked-child
APIs, `ICollectionManager.GetCollectionsContainingItem`, `IPlaylistManager` positional
inserts, `IHasEmbeddedImage` (compiled-in plugins only).

### Behavior changes that help without any work

- The web client's new **"still watching" prompt** pauses idle playback. The tracker only
  credits time when a session is not paused, so a kid who falls asleep in front of the TV
  now burns less of her allowance to the web client — the same problem the enforcement
  sweep (15 s then, 5 s since 3.1) and `MaxDeltaSeconds` clamp already defend against from
  the server side.
- **Nothing in 12 changed the hard-block surface.** `AccessSchedule`,
  `UserPolicy.EnableMediaPlayback` and `UpdatePolicyAsync` were untouched, so
  `HardBlockEnforcer` carried over exactly. *(Moot as of 3.1: the enforcer was removed once
  the client was shown to honor Stop, and the plugin no longer writes user policies at all.)*
