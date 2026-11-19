# Mod DB page — Cylinder World (draft)

Draft for mods.vintagestory.at. Current prepared release: **1.1.2** (19 November 2026; GitHub release v1.1.2 once published; 1.1.1 is on GitHub), for Earth 2.0, which requires 1.1.0 or later; upload it before Earth 2.0. On the Mod DB before: 1.0.1 and 1.0.4. The first public release was prepared as **1.0.0**, based on the 0.11.14 core.

Publishing direction (kept from the first release, 31 October 2026): recommend **256k or 512k** worlds, and state that further mod compatibility (including Terra Pretty) will be tested and developed; it is not yet verified.

Author's note: the mod was made to enable travelling around the world and is currently tailored mainly for Earth Map. Disclose that all code was created using AI tools, with the author's own ideas and design and extensive testing, refinement and improvements.

## Fields

| Field | Value |
|---|---|
| Status | Draft until the description is in and the file is uploaded, then **Released** when ready to publish |
| Category | Game Mod |
| Tags | Worldgen, Immersion (add others such as Survival or Exploration only if the site has them) |
| Name | **Cylinder World** (matches `modinfo.json`; "Cworld" is only the folder name) |
| URL alias | `cylinderworld` (same as the modid) |
| Summary (66 chars) | The world wraps east-west, with impassable ice walls at the poles. |
| Side | Client and Server side mod (`requiredOnClient` and `requiredOnServer` are both true) |
| Source Code Url | https://github.com/Temporal-Poets-Society/VS-Cworld-mod (the public repo; license is GPL-3.0) |
| Issue tracker Url | the repository's issues page, or leave empty and use comments |
| Wiki | https://github.com/Temporal-Poets-Society/VS-Cworld-mod/wiki |
| Homepage / Trailer / Donate | optional |

## Fix before uploading

- Release 1.1.2 (19 Nov): author is `Andris`; upload `release/cylinderworld-1.1.2.zip` (204,273 bytes, SHA-256
  `1cf8a56378cc8fa02b35f95051649afaa932879b8e5ee718271648c85dc09b4e`; the 1.1.2 release record in `docs/DECISIONS.md`).
  The release package with every field is `work/moddb-release-2026-11-15/cylinderworld/` (`release-info.txt`).
- Logo: `docs/moddb-logo.png` (480×320, Earth map on a cylinder, made by `python tools/make-logo.py ../Earth/preview/global.png docs/moddb-logo.png 144`).
  1. Drag it onto the page's Screenshots area (drag&drop to upload).
  2. Pick it in **ModDB Logo image**. Leave **External Logo image** on Default (it crops the ModDB logo).
  3. Save. The logo is not shown in the slideshow, so also upload real screenshots.
- Screenshots: `preview-example.png` (a `/cyl map` render) works. In-game shots are better: the mist at the seam,
  the Wall seen from the ice sheet, sea ice in winter, `/cyl info` output.

## Description

The shortened publishing text is in `docs/moddb-description.html`. Open it in a text editor, copy everything, then in the Mod DB editor click `<>` (Source code), select all content in that source window, paste the raw HTML and click OK. Pasting raw HTML into the visual editor displays the tags as text. If that has already happened, replace the entire source-window content with the original HTML file.

## Upload the mod ZIP

1. Save the new mod page; it can remain Draft while you prepare it.
2. Click **Cylinder World** in the breadcrumb to open the mod page.
3. Click **Add release**, then upload `release/cylinderworld-1.1.2.zip` in **Files**.
4. Select the compatible game versions, add the release notes below, and save the release.
5. When ready to publish, change the mod page status from Draft to Released.

Reference: [Mod DB upload guide](https://www.vintagestory.at/forums/topic/18633-upload-your-mod-to-the-moddb/).

## Release notes for the 1.1.2 file upload

The changelog since 1.0.4 (the last version on the Mod DB) is `changelog.txt` in the release package (`work/moddb-release-2026-11-15/cylinderworld/`), copied below.

```
1.1.2
- Earth-world servers with many players no longer stall once a second. The polar cold check asked Earth's terrain about every player each second; it now answers at once between 55° S and 66° N, where there is no ice, and asks only nearer the poles. Worlds generate and play as in 1.1.1.

1.1.1
- The "Seam mist" option is no longer on the world-creation screen. New worlds still have the mist off, and an admin can switch it on with /worldconfig cylinderSeamMist true (applies at the next start). Nothing else changed.

1.1.0 (the companion of Earth 2.0)
The whole mod was reviewed and fixed. New worlds generate as in 1.0.4 apart from the fixes below and, in plain (non-Earth) worlds, the landform layout; existing worlds keep their layout.
- Seam mist per world: a world option, off by default.
- Crossing: if the far side isn't loaded yet, the jump waits until it is (you travel on over the same sea) and then lands you safely; other players and shared boats stay smooth through a jump; a plain /tp of about one ring is handled like a seam jump.
- Block changes across the seam: chests and other containers changed near the seam reach the other side and are kept over a reload; changes made just before a server stops are kept; each side keeps its own snow; the record of seam changes stays small.
- Earth worlds: Earth's lakes, salt lakes and rivers in the seam band keep their water; hot springs are left to Earth; rain works with Earth's weather; the cold on the polar sea counts only where it is frozen under you.
- World generation: no lava or water springs in the polar ice; lava already saved in an old world's ice walls is taken out the first time each area loads; a world whose first start stopped (for example while Earth downloaded its data) is set up properly on the next try.
- New worlds write the planet's pole distance as the world's "Polar-Equator distance", so other mods that read it agree with the latitude.
- Temperatures: the polar cold correction is applied once per reading (it could drift by tens of degrees during catch-up).
- A game update that breaks one feature now switches off only that feature, with one line in the log, instead of crashing.
- A config file with an error is left as it is (it used to be overwritten with defaults); /cyl map works from the server console; /cyl goseam lands on the surface.
- A world made by a newer Cylinder World is refused instead of being generated wrongly.
```
