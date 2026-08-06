# Chore tile art — image-generator prompts

Ready-to-paste prompts for the nine chore pictures. Style: **soft 3D clay** (Play-Doh /
plasticine render). Chosen because it survives the two things that break chore art on a TV —
it stays readable from across the room, and it is the style current generators reproduce most
consistently across a whole set.

Where the art goes and how to wire a new one in: [`ADDING-CHORES.md`](ADDING-CHORES.md).

---

## 1. How to use this file

1. Generate each picture with **`STYLE` + the chore's `SUBJECT` + the chore's `BACKGROUND`**,
   pasted as one prompt. The `STYLE` block is identical every time — that is what makes the
   set look like a set.
2. Generate all nine **in one session, with the same model and settings**. Mixed lighting or
   mixed clay texture across tiles reads as broken even when each image is fine alone.
3. Square output, 1024×1024. Downscale to **512×512 PNG** before committing — the tile is
   ~140 px on 1080p and ~280 px on 4K, and every byte ships inside the plugin DLL.
4. Save as `Web/clipart/<key>.png`, using the exact key from each section below.

The pictures are the label — a 6-year-old who can't read has nothing else to go on. So the
test for every image is: **covered up the other eight, would she still know which chore this
is from three metres away?** If not, regenerate; don't settle.

---

## 2. STYLE block (identical in every prompt)

```
3D clay render, handmade plasticine and Play-Doh look, soft matte surface with a subtle
fingerprint texture, chunky rounded forms, no sharp edges anywhere. One single centered
object group, no scene, no room, no floor line, no horizon. Soft even studio lighting from
the upper left, one gentle soft contact shadow directly beneath the objects. Bright friendly
saturated colors. Three-quarter view tilted about 15 degrees from above. The subject fills
about 70 percent of the frame with generous even padding on all four sides. Square 1:1
composition, children's app icon, extremely readable at small size and from a distance.
No text, no letters, no numbers, no logos, no watermark, no border, no frame.
```

**Negative prompt** (for generators that take one — Stable Diffusion, Flux, ComfyUI; skip for
ChatGPT/Midjourney):

```
text, letters, numbers, watermark, signature, photorealistic, realistic skin, cluttered
background, room interior, scene, furniture in background, gradient background, vignette,
scattered extra objects, cropped subject, border, frame, drop shadow box, knives, blades,
scissors, glass, dark colors, muddy colors, low contrast, busy detail
```

**Midjourney users**: append `--ar 1:1 --style raw --stylize 150`, and generate the whole set
from one `--sref` (style reference) after the first image you like, so the clay texture and
light stay locked across tiles.

---

## 3. The nine prompts

Backgrounds are flat single colors, one per tile. They are a *secondary* cue — the object
carries the meaning — but keeping them distinct stops two tiles from reading as one blob in
peripheral vision. Grid order in the app should keep same-family colors apart.

### 3.1 `make-bed` — Make your bed · 🪙1 · max 1/day

> **BACKGROUND:** flat pastel sky blue background, hex #CFE7FF

```
SUBJECT: a small child's bed made up neatly, plump clay duvet with a simple star pattern
folded back at one corner, one fat pillow, short wooden bed frame with rounded legs.
```

*No teddy on this bed* — the teddy belongs to `tidy-toys` and the two tiles must not share a
mascot.

### 3.2 `clothes-basket` — Dirty clothes in the basket · 🪙1 · max 1/day

> **BACKGROUND:** flat pastel lavender background, hex #E9E0FF

```
SUBJECT: a round woven laundry basket with a crumpled t-shirt and one striped sock flopping
over the rim, one more sock in mid-air just above the basket as if dropped in.
```

The mid-air sock is what says *putting clothes in*, not *a basket exists*. Keep it.

### 3.3 `plate-in-sink` — Put your plate in the sink · 🪙1 · max 3/day

> **BACKGROUND:** flat pastel mint green background, hex #CFF2E1

```
SUBJECT: a single kitchen sink basin with a short curved tap, one dinner plate and one cup
standing inside the basin, cut out as a standalone object with nothing around it.
```

### 3.4 `tidy-toys` — Tidy your room · 🪙2 · max 1/day

> **BACKGROUND:** flat pastel apricot background, hex #FFE7BE

```
SUBJECT: an open wooden toy box with a smiling teddy bear and two colorful building blocks
inside, one red block just outside the box being tipped in.
```

### 3.5 `unload-dishwasher` — Unload the dishwasher · 🪙2 · max 1/day

> **BACKGROUND:** flat pastel blush pink background, hex #FFDCE4

```
SUBJECT: an open dishwasher with its lower rack slid out, filled with a neat row of colorful
clean plates and two upside-down cups, and a small stack of two plates standing beside it.
```

**No knives, no blades, no sharp cutlery in this image** — the picture teaches the scope of
the chore, and the scope is the bottom rack, plates and cups.

### 3.6 `tidy-craft-table` — Tidy the craft table · 🪙2 · max 1/day

> **BACKGROUND:** flat pastel butter yellow background, hex #FFF3B0

```
SUBJECT: a small craft table seen from three-quarter above, with a cup holding colorful
crayons standing on it, two loose crayons and one small piece of colored paper being tidied
toward the cup, a glue stick lying beside it.
```

Deliberately built around **crayons and a cup on a table** so it cannot be confused with
`tidy-toys` (teddy and a box). Different objects, different container, different color family.
No scissors.

### 3.7 `put-away-clothes` — Put away your clean clothes · 🪙2 · max 1/day

> **BACKGROUND:** flat pastel pistachio green background, hex #DDEBC0

```
SUBJECT: an open dresser drawer with a neat stack of three folded t-shirts inside, one more
folded shirt hovering just above the drawer as if being placed in, the dresser front cut out
as a standalone object.
```

Folded and a drawer — the opposite of `clothes-basket`'s crumpled and a basket. That contrast
is the whole point; keep both halves of it.

### 3.8 `play-brother` — Play with little brother · 🪙3 · max 1/day

> **BACKGROUND:** flat pastel peach background, hex #FFD9C7

```
SUBJECT: two clay children sitting on the ground facing each other, rolling a big colorful
striped ball between them: an older girl about six years old with brown hair in a ponytail
and a yellow shirt, and a toddler boy about two with short brown hair and a blue shirt. Simple
friendly clay faces, dot eyes and small smiles. A small hourglass sand timer with orange sand
stands upright beside them.
```

The **sand timer is load-bearing**, not decoration: it turns an unverifiable chore into
"play until the sand runs out", which is a rule a 6-year-old can see in the picture.

### 3.9 `read-to-brother` — Read a picture book to your brother · 🪙2 · max 1/day

> **BACKGROUND:** flat pastel periwinkle background, hex #D3DDF7

```
SUBJECT: the same two clay children sitting side by side, the older girl about six years old
with brown hair in a ponytail and a yellow shirt holding an open picture book on her lap, the
toddler boy about two with short brown hair and a blue shirt leaning in against her shoulder
looking at the pages. Simple friendly clay faces, dot eyes and small smiles. No text or
pictures printed on the book pages.
```

**Generate 3.9 immediately after 3.8**, in the same session, reusing the character wording
verbatim — matching the two children across two images is the hardest part of this set, and
character drift is the usual failure. If your tool has one, use image-to-image or a character
reference from the 3.8 result. If the pair still won't hold, fall back to a book-only subject
(an open picture book with a teddy leaning against it) rather than shipping two children who
look like different people.

---

## 4. After generating

```bash
# square, 512px, no metadata; then crush it
magick in.png -resize 512x512 -strip Web/clipart/<key>.png
oxipng -o4 --strip safe Web/clipart/<key>.png    # or: pngquant --quality 65-90
```

Target **under ~120 KB per file**. Corners are rounded by CSS on the kid page, so leave the
image a full-bleed square — do not try to make the generator produce transparent corners.

Then check them together, not one at a time:

- Open the kid page on the real TV, all nine tiles visible at once.
- Stand where the child actually sits. Any tile you have to think about is a regenerate.
- Look for two tiles that read as the same picture in peripheral vision — that's the failure
  mode this whole file is built to avoid.

## 5. The older line-art set

Seven line-art SVGs from the previous set are still catalogued and offered separately in the
picker (`set-table`, `water-plants`, `books-shelf`, `wipe-table`, `feed-pet`, `brush-teeth`,
`help-baby`). They are Mulberry Symbols, CC BY-SA 4.0.

**Don't mix them with the clay tiles on one kid page** — the styles fight and the page reads
as half-finished. If you want one of those chores, regenerate it in clay using the recipe
above and drop the PNG in next to the SVG; raster wins over SVG for the same key
automatically, so the tile switches style with no code change and chores already configured
against that key keep working.
