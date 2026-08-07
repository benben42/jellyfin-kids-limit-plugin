# Chore tile art — image-generator prompts

Nine complete, copy-paste prompts for the chore pictures, written for **ChatGPT (GPT Image)
and Gemini**. Style: **soft 3D clay** (Play-Doh / plasticine render) — chosen because it stays
readable from across the room and is the style current generators reproduce most consistently
across a whole set.

Each prompt below is self-contained: copy one block, paste it, generate. Nothing to assemble.

Where the art goes and how to wire a new one in: [`ADDING-CHORES.md`](ADDING-CHORES.md).

---

## 1. How to use this file

1. **Run all nine in one chat session**, in order. Both ChatGPT and Gemini keep visual style
   from earlier images in the same conversation, and that consistency is most of the job — the
   set has to look like one set, not nine good pictures.
2. If a picture drifts off-style, say **"same style as the previous image"** and paste the
   prompt again rather than editing it.
3. Ask for the result as a **square image**. If the model returns something else, say
   "regenerate as a square 1:1 image".
4. Keep the full-resolution download as a master in `docs/pictures/<key>.png`, then convert to
   a **512×512 WebP** for the app (§4). The tile is ~140 px on 1080p and ~280 px on 4K, and
   every byte ships inside the plugin DLL.
5. Save each as `Web/clipart/<key>.webp` using the exact key in the heading.

The picture is the label — a 6-year-old who can't read has nothing else to go on. So the test
for every image is: **cover up the other eight — would she still know which chore this is,
from three metres away?** If not, regenerate; don't settle.

---

## 2. The nine prompts

### 2.1 `make-bed` — Make your bed · 🪙1 · max 1/day

```
Create a square 1:1 children's app icon illustration of a small child's bed, made up neatly:
a plump duvet with a simple star pattern folded back at one corner, one fat pillow, and a
short wooden bed frame with rounded legs.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the object.

Composition: the bed alone, centred, on a completely flat plain pastel sky blue background
(hex #CFE7FF) — no room, no scene, no furniture behind it, no floor line, no horizon, no
gradient. Three-quarter view tilted about 15 degrees from above. The bed fills about 70% of
the frame with generous even padding on all four sides.

No text, letters, numbers, logos, watermarks, borders or frames. It must stay instantly
readable when shrunk to a small icon and seen from several metres away.
```

*Deliberately no teddy on this bed — the teddy belongs to the `tidy-toys` tile and the two
must not share a mascot.*

### 2.2 `clothes-basket` — Dirty clothes in the basket · 🪙1 · max 1/day

```
Create a square 1:1 children's app icon illustration of a round woven laundry basket with a
crumpled t-shirt and one striped sock flopping over the rim, and one more sock caught in
mid-air just above the basket, as if it has been dropped in.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the basket.

Composition: the basket alone, centred, on a completely flat plain pastel lavender background
(hex #E9E0FF) — no room, no scene, no furniture behind it, no floor line, no horizon, no
gradient. Three-quarter view tilted about 15 degrees from above. The basket fills about 70% of
the frame with generous even padding on all four sides.

No text, letters, numbers, logos, watermarks, borders or frames. It must stay instantly
readable when shrunk to a small icon and seen from several metres away.
```

*The mid-air sock is what says "putting clothes in" rather than "a basket exists". Keep it.*

### 2.3 `plate-in-sink` — Put your plate in the sink · 🪙1 · max 3/day

```
Create a square 1:1 children's app icon illustration of a single kitchen sink basin with a
short curved tap, holding one dinner plate and one cup standing inside the basin. The sink is
cut out as a standalone object with nothing around it.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the sink.

Composition: the sink alone, centred, on a completely flat plain pastel mint green background
(hex #CFF2E1) — no kitchen, no counter, no cupboards, no scene behind it, no floor line, no
horizon, no gradient. Three-quarter view tilted about 15 degrees from above. The sink fills
about 70% of the frame with generous even padding on all four sides.

No text, letters, numbers, logos, watermarks, borders or frames. It must stay instantly
readable when shrunk to a small icon and seen from several metres away.
```

### 2.4 `tidy-toys` — Tidy your room · 🪙2 · max 1/day

```
Create a square 1:1 children's app icon illustration of an open wooden toy box with a smiling
teddy bear and two colourful building blocks inside it, and one red block just outside the box
being tipped in.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the toy box.

Composition: the toy box alone, centred, on a completely flat plain pastel apricot background
(hex #FFE7BE) — no room, no scene, no furniture behind it, no floor line, no horizon, no
gradient. Three-quarter view tilted about 15 degrees from above. The toy box fills about 70%
of the frame with generous even padding on all four sides.

No text, letters, numbers, logos, watermarks, borders or frames. It must stay instantly
readable when shrunk to a small icon and seen from several metres away.
```

### 2.5 `unload-dishwasher` — Unload the dishwasher · 🪙2 · max 1/day

```
Create a square 1:1 children's app icon illustration of an open dishwasher with its lower rack
slid out. The rack holds a neat row of colourful clean plates and two upside-down cups, and a
small stack of two plates stands beside it. Show only plates and cups — nothing sharp, no
cutlery of any kind.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the dishwasher.

Composition: the dishwasher alone, centred, on a completely flat plain pastel blush pink
background (hex #FFDCE4) — no kitchen, no counter, no cupboards, no scene behind it, no floor
line, no horizon, no gradient. Three-quarter view tilted about 15 degrees from above. The
dishwasher fills about 70% of the frame with generous even padding on all four sides.

No text, letters, numbers, logos, watermarks, borders or frames. It must stay instantly
readable when shrunk to a small icon and seen from several metres away.
```

*The "plates and cups only" line is a safety instruction, not a style one: the picture teaches
the child the scope of the chore, and the scope is the bottom rack.*

### 2.6 `tidy-craft-table` — Tidy the craft table · 🪙2 · max 1/day

```
Create a square 1:1 children's app icon illustration of a small craft table with a cup full of
colourful crayons standing on it, two loose crayons and one small piece of coloured paper
being tidied toward the cup, and a glue stick lying beside it.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the table.

Composition: the table alone, centred, on a completely flat plain pastel butter yellow
background (hex #FFF3B0) — no room, no scene, no furniture behind it, no floor line, no
horizon, no gradient. Three-quarter view tilted about 15 degrees from above. The table fills
about 70% of the frame with generous even padding on all four sides.

No scissors. No text, letters, numbers, logos, watermarks, borders or frames. It must stay
instantly readable when shrunk to a small icon and seen from several metres away.
```

*Built around crayons and a cup on a table specifically so it cannot be confused with
`tidy-toys` (teddy and a box) — different objects, different container, different colour
family. That contrast is the whole point of this tile.*

### 2.7 `put-away-clothes` — Put away your clean clothes · 🪙2 · max 1/day

```
Create a square 1:1 children's app icon illustration of an open dresser drawer with a neat
stack of three folded t-shirts inside it, and one more folded shirt hovering just above the
drawer as if being placed in. The dresser front is cut out as a standalone object.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the dresser.

Composition: the dresser alone, centred, on a completely flat plain pastel pistachio green
background (hex #DDEBC0) — no room, no scene, no furniture behind it, no floor line, no
horizon, no gradient. Three-quarter view tilted about 15 degrees from above. The dresser fills
about 70% of the frame with generous even padding on all four sides.

No text, letters, numbers, logos, watermarks, borders or frames. It must stay instantly
readable when shrunk to a small icon and seen from several metres away.
```

*Folded clothes and a drawer — the deliberate opposite of `clothes-basket`'s crumpled clothes
and a basket. Keep both halves of that contrast.*

### 2.8 `play-brother` — Play with little brother · 🪙3 · max 1/day

```
Create a square 1:1 children's app icon illustration of two clay children sitting on the
ground facing each other, rolling a big colourful striped ball between them: an older girl of
about six with brown hair in a ponytail and a yellow shirt, and a toddler boy of about two
with short brown hair and a blue shirt. Simple friendly clay faces with dot eyes and small
smiles. A small hourglass sand timer with orange sand stands upright beside them.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the children.

Composition: the two children alone, centred, on a completely flat plain pastel peach
background (hex #FFD9C7) — no room, no scene, no furniture behind them, no floor line, no
horizon, no gradient. Three-quarter view tilted about 15 degrees from above. The children fill
about 70% of the frame with generous even padding on all four sides.

No text, letters, numbers, logos, watermarks, borders or frames. It must stay instantly
readable when shrunk to a small icon and seen from several metres away.
```

*The sand timer is load-bearing, not decoration: it turns an unverifiable chore into "play
until the sand runs out" — a rule a 6-year-old can read straight off the picture.*

### 2.9 `read-to-brother` — Read a picture book to your brother · 🪙2 · max 1/day

**Generate this one immediately after 2.8, in the same chat**, so the two children carry over.

```
Using exactly the same two clay children as the previous image, create a square 1:1 children's
app icon illustration of them sitting side by side: the older girl of about six with brown
hair in a ponytail and a yellow shirt holds an open picture book on her lap, and the toddler
boy of about two with short brown hair and a blue shirt leans in against her shoulder, looking
at the pages. Simple friendly clay faces with dot eyes and small smiles. The book pages are
blank — no printed words or pictures on them.

Style: a 3D clay render — everything looks handmade from plasticine and Play-Doh, with a soft
matte surface, a subtle fingerprint texture, chunky rounded forms and no sharp edges anywhere.
Bright, friendly, saturated colours. Soft even studio lighting from the upper left, with one
gentle soft shadow directly beneath the children.

Composition: the two children alone, centred, on a completely flat plain pastel periwinkle
background (hex #D3DDF7) — no room, no scene, no furniture behind them, no floor line, no
horizon, no gradient. Three-quarter view tilted about 15 degrees from above. The children fill
about 70% of the frame with generous even padding on all four sides.

No text, letters, numbers, logos, watermarks, borders or frames. It must stay instantly
readable when shrunk to a small icon and seen from several metres away.
```

*Matching the same two children across two images is the hardest thing in this set, and
character drift is the usual failure. If the pair still won't hold after a couple of tries,
fall back to a book-only subject — an open picture book with a teddy leaning against it —
rather than shipping two children who look like different people.*

---

## 3. A note on the background colours

Each tile gets its own flat pastel. The colour is a *secondary* cue — the object carries the
meaning — but keeping them distinct stops two tiles from reading as one blob in peripheral
vision. In use: sky blue, lavender, mint, apricot, blush pink, butter yellow, pistachio,
peach, periwinkle. If you add a tenth chore later, pick something not adjacent to these.

---

## 4. After generating

Keep the generator's full-size output in `docs/pictures/<key>.png` as the master, then produce
the file the app ships:

```python
from PIL import Image
im = Image.open('docs/pictures/<key>.png').convert('RGB').resize((512, 512), Image.LANCZOS)
im.save('Web/clipart/<key>.webp', 'WEBP', quality=92, method=6)
```

**WebP, not PNG.** These are smooth 3D renders on flat pastel grounds: palette-quantised PNG
dithers visible noise into exactly those flat areas, and full-colour PNG runs ~250 KB a tile.
WebP at q92 is visually lossless here and lands around **25–35 KB** — the whole set is ~250 KB
inside the DLL. Every client that runs Jellyfin's web UI, including the Android TV WebView,
supports it.

Corners are rounded by CSS on the kid page, so leave the image a full-bleed square — don't ask
the generator for transparent rounded corners.

Then check them together, not one at a time:

- Open the kid page on the real TV with all nine tiles visible at once.
- Stand where the child actually sits. Any tile you have to think about is a regenerate.
- Look for two tiles that read as the same picture in peripheral vision — that is the failure
  mode this whole file is built to avoid.

## 5. Status of the set

Eight of the nine are generated and in `Web/clipart/`. **`plate-in-sink` (§2.3) is still
missing** — until it lands, that chore falls back to its 🍽️ emoji on the kid page, which works
but is the odd tile out.

Seven line-art SVGs from the previous set are still catalogued and offered separately in the
picker (`set-table`, `water-plants`, `books-shelf`, `wipe-table`, `feed-pet`, `brush-teeth`,
`help-baby`). They are Mulberry Symbols, CC BY-SA 4.0.

**Don't mix them with the clay tiles on one kid page** — the styles fight and the page reads
as half-finished. If you want one of those chores, generate it in clay by copying any prompt
above and swapping the subject sentence and background colour, then drop the WebP in next to
the SVG: raster wins over SVG for the same key automatically, so the tile switches style with
no code change and chores already configured against that key keep working. (That is how
`make-bed`, `clothes-basket` and `tidy-toys` were replaced; their now-unreachable SVGs were
deleted afterwards.)
