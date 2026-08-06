# Adding a chore (and its picture)

Written to be read cold, with no memory of the conversation that produced the current set.
It covers both halves of the job: **choosing a chore that works for a 6-year-old who can't
read**, and **wiring it into the code**.

Companion file: [`CHORE-IMAGE-PROMPTS.md`](CHORE-IMAGE-PROMPTS.md) — the art style and the
nine existing prompts. Background on the whole rewards system: [`../REWARDS.md`](../REWARDS.md).

---

## 1. How the pieces fit

| Thing | Where it lives |
| --- | --- |
| The picture | `Web/clipart/<key>.png` (embedded in the DLL by a csproj glob) |
| The key → label catalog | `Configuration/ChoreClipart.cs` → `Catalog` |
| The suggested chore (name, emoji, coins, max/day) | `Configuration/PluginConfiguration.cs` → `DefaultChores()` |
| The same list, client-side | `Configuration/configPage.html` → `SUGGESTED_CHORES` |
| The actual chores a parent uses | Plugin configuration (XML), edited on the config page or the phone page |

Two properties make this cheap to extend:

- **The pickers are server-driven.** `GET /KidsLimit/clipart` returns only the catalogued keys
  that actually have a file embedded, so the config page and the phone page never need editing
  to show new art, and never offer a picture that would 404.
- **Raster beats SVG for the same key.** `ChoreClipart.Formats` resolves `.png` → `.webp` →
  `.jpg` → `.svg`, so dropping `make-bed.png` next to the legacy `make-bed.svg` replaces the
  art with no code change and without breaking chores already configured against that key.

---

## 2. Choosing a chore

The constraints that actually bind, in the order they bite:

1. **The picture is the label.** The child can't read the name. If a chore can't be drawn as
   one unmistakable object group, it isn't a chore for this app.
2. **It must not look like an existing tile.** This is the failure that ruins a set. "Tidy
   your room" and "tidy the craft table" only work because one is *teddy into a box* and the
   other is *crayons into a cup on a table*. Before adding, list the current tiles and name
   the object that makes the new one different.
3. **Binary and parent-checkable.** Claims go through approval, so a parent has to be able to
   glance and say yes. "Be kind today" is not a chore; "put your plate in the sink" is.
   Anything time-based needs a visible cue in the art (the sand timer in `play-brother`).
4. **Safe at six.** No blades, no stove, no hot water, no cleaning chemicals, no glass. The
   art teaches the scope, so it must not show them either — that's why the dishwasher tile is
   plates and cups on the bottom rack and nothing else.
5. **Priced against the economy, not effort.** Defaults: a coin is 5 minutes
   (`CoinMinutes`), the bank caps at 24 coins, and **only 6 coins a day can be spent**
   (`MaxRedeemCoinsPerDay`). So:

   | Coins | For | Example |
   | --- | --- | --- |
   | 1 | seconds-long habits, often repeatable | plate in the sink, bed |
   | 2 | a real few-minute job | dishwasher, craft table, folding away clothes |
   | 3 | the premium tier, keep it rare | playing with a younger sibling |

   Keep the **daily total earnable above 6** so the child chooses what to do rather than
   clearing a checklist — but adding a chore raises that total, so check the whole set adds up
   to something like 10–16, not 30. And set `MaxPerDay` deliberately: it is the only thing
   stopping a 1-coin chore from being farmed.

Sanity check before writing any code: read `DefaultChores()` and ask whether the new chore
makes the set better or just longer. Eight to ten tiles fill one D-pad grid without scrolling.

---

## 3. Making the picture

Use the `STYLE` block in [`CHORE-IMAGE-PROMPTS.md §2`](CHORE-IMAGE-PROMPTS.md) **verbatim** —
it is what holds the set together — plus a one-sentence `SUBJECT` for the new chore and a flat
pastel background color not already used by a neighbouring tile (§3 lists the nine in use).

Then:

```bash
magick in.png -resize 512x512 -strip Web/clipart/<key>.png
oxipng -o4 --strip safe Web/clipart/<key>.png
```

Full-bleed square, under ~120 KB. Corners are rounded by CSS on the kid page — don't attempt
transparent rounded corners in the image.

Append the new prompt to `CHORE-IMAGE-PROMPTS.md §3` so the set stays reproducible.

---

## 4. Wiring it in

Pick a **key**: lowercase, hyphenated, describing the chore, e.g. `feed-the-cat`. It is a
permanent identifier — a parent's saved chore points at it, so renaming a key orphans that
chore (`SettingsController` blanks unknown keys on the next save). Choose it once.

### 4.1 Always

**`Configuration/ChoreClipart.cs`** — add to `Catalog`, in the current-set block above the
legacy line-art entries:

```csharp
new("feed-the-cat", "Feed the cat"),
```

`Keys` and the pickers derive from this. **No csproj edit** — `Web\clipart\*.png` is a glob.

### 4.2 Only if it should be a built-in suggestion

Two lists must stay identical — the server's seed and the config page's "Add suggested
chores" button:

**`Configuration/PluginConfiguration.cs`** → `DefaultChores()`:

```csharp
new Chore { Id = "feed-the-cat", Name = "Feed the cat", Icon = "🐱", Clipart = "feed-the-cat", Coins = 1, MaxPerDay = 1 },
```

**`Configuration/configPage.html`** → `SUGGESTED_CHORES`:

```javascript
{ Id: 'feed-the-cat', Name: 'Feed the cat', Icon: '🐱', Clipart: 'feed-the-cat', Coins: 1, MaxPerDay: 1 },
```

Use the clipart key as the chore `Id` for built-ins; that is what makes the button's
duplicate check work. Always give an `Icon` too — it is the fallback whenever the art can't be
resolved.

Note that `DefaultChores()` seeds **only on a genuinely fresh install** (guarded by
`config.Initialized` in `Plugin.cs`). An existing server picks new suggestions up through the
"Add suggested chores" button, never automatically.

### 4.3 Never needed

`configPage.html` / `parent.html` picture pickers, `RewardsController`, the kid page. They all
read the catalog at runtime.

---

## 5. Verifying

```bash
dotnet build Jellyfin.Plugin.KidsLimit.csproj -c Release
```

The art is an **embedded resource**, so the plugin must be rebuilt and reinstalled and
Jellyfin restarted — a file dropped into `Web/clipart` on a running server does nothing.

Then:

1. `GET /KidsLimit/clipart` lists the new key with `"Style": "clay"`. If it's missing, the
   file isn't embedded (check the extension and that the name matches the key exactly).
2. `GET /KidsLimit/clipart/<key>` returns the image with `Content-Type: image/png`.
3. Config page → Rewards → the chore's thumbnail button → the picture appears under **Clay
   pictures**, not under *Line art*.
4. Kid page: the tile shows the picture. If it shows the emoji instead, the key resolved to
   nothing and the server blanked it — back to step 1.
5. Look at the full grid from where the child sits. Any tile you have to think about, or any
   pair that reads alike, is a regenerate.

---

## 6. Removing a chore

Deleting a chore is done by the parent in the UI; nothing in code needs to change. If you
remove a **key** from `Catalog`, any saved chore pointing at it falls back to its emoji
immediately and loses the key on the next settings save. Leaving an unused key catalogued is
harmless — the catalog only offers keys whose art exists.
