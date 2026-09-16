# LeaseBook Icon Pack

The assets live in `web/public/`. This file deliberately does not, because Vite copies everything
under `public/` to the dist root, which would publish it at `/README.md`.

Primary mark:

- Deep teal: #208181
- Dark teal: #006C6C
- Dark ground (the `png-dark/` tile): #1A1D21

Note that the manifest's `background_color` is **#0C0F12**, not #1A1D21 — it matches the app's dark
`--bg` token so the PWA splash hands off to the app without a visible step.

Included:

- `leasebook.svg` — SVG wrapper around the 1024px master raster on transparency. It is a
  container, not vector artwork, so it does not scale past 1024px and costs ~300KB; the web build
  does not reference it.
- `leasebook-dark.svg` — same wrapper, mark on #1A1D21
- `leasebook.ico` — multi-resolution ICO, built from `png-favicon/`
- `favicon.ico` — byte-identical copy under the conventional browser filename
- `site.webmanifest` — basic PWA/browser manifest
- `png-favicon/` — tight-cropped transparent PNGs for browser chrome (see below)
- `png-transparent/` — transparent PNGs, app-icon crop
- `png-dark/` — PNGs on #1A1D21

## Two crops

`png-transparent/` and `png-dark/` share the master's ~20-25% margin. That is right for a touch or
PWA icon, where the platform draws the artwork inside its own rounded tile, and it is what keeps the
mark inside the maskable safe zone. It is too loose for a browser tab, where the mark ends up a small
glyph in a mostly empty 16px box.

`png-favicon/` is regenerated from the 1024px master with the mark filling 88% of the frame on its
dominant axis (610x530 bbox, 6% side margins, ~12% top and bottom). Downscaling is area-averaged in
premultiplied alpha, so the anti-aliased edge stays teal instead of picking up a dark halo.

One caveat if you regenerate it: the master carries an invisible alpha=1 spill across a 756x797 area.
A bbox taken at `alpha > 0` inflates the crop by about 25% and silently undoes the tightening. The
bbox is stable from `alpha > 1` through `alpha > 64`.

PNG sizes (`png-transparent/`, `png-dark/`):
16x16, 20x20, 24x24, 29x29, 32x32, 36x36, 40x40, 48x48, 57x57, 58x58, 60x60, 64x64, 72x72, 76x76, 80x80, 87x87, 96x96, 114x114, 120x120, 128x128, 144x144, 152x152, 167x167, 180x180, 192x192, 256x256, 384x384, 512x512, 1024x1024

ICO sizes:
16x16, 24x24, 32x32, 48x48, 64x64 — the sizes browsers actually select for a favicon.
Larger entries were dropped: with a manifest present, installs and desktop shortcuts take the PWA
icons instead, so a 128/256 entry only risked a visible crop change between sizes.

Web set actually wired up in `web/index.html` and `site.webmanifest`:

- `favicon.ico`
- `png-favicon/leasebook-16x16.png`, `-32x32.png`
- `png-dark/leasebook-180x180.png` (Apple touch icon — the opaque set, because iOS composites a
  transparent touch icon over pure black)
- `png-transparent/leasebook-192x192.png`, `-512x512.png` (PWA, `purpose: any`)
- `png-dark/leasebook-192x192.png`, `-512x512.png` (PWA, `purpose: maskable` — opaque, and the
  mark sits within 62% of the half-width, inside the 80% maskable safe zone)

The live copy of these links is `web/index.html`.
