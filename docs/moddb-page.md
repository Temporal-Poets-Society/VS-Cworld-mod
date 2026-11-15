# Mod DB page — Cylinder World (draft)

Draft for mods.vintagestory.at. Current prepared release: **1.1.0** (14 November 2026), the companion of Earth 2.0.0, which requires it; upload it before Earth 2.0.0. Published before: 1.0.1 and 1.0.4. The first public release was prepared as **1.0.0**, based on the 0.11.14 core.

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
| Homepage / Trailer / Wiki / Donate | optional |

## Fix before uploading

- Release 1.1.0 (14 Nov): author is `Andris`; upload `release/cylinderworld-1.1.0.zip` (its size and SHA-256 are in the 1.1.0 release record, `docs/DECISIONS.md`). Previous release packages are preserved.
- The release notes say "Earth 2.0.0 requires this version": true only once Earth's `modinfo.json` asks for
  `"cylinderworld": "1.1.0"` (EA-0096; it still says 1.0.4). Drop that line if 1.1.0 is uploaded first and Earth 2.0.0
  hasn't changed it yet.
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
3. Click **Add release**, then upload `release/cylinderworld-1.1.0.zip` in **Files**.
4. Select the compatible game versions, add the release notes below, and save the release.
5. When ready to publish, change the mod page status from Draft to Released.

Reference: [Mod DB upload guide](https://www.vintagestory.at/forums/topic/18633-upload-your-mod-to-the-moddb/).

## Release notes for the 1.1.0 file upload

```
1.1.0 - companion of Earth 2.0.0
The whole mod was reviewed and fixed. New worlds generate as in 1.0.4 except the fixes and, in new plain (non-Earth)
worlds, the landform layout; existing worlds keep their layout.
- Seam mist per world: a world option, off by default, switched by an admin with `/worldconfig cylinderSeamMist true`
  (1.1.1: no longer on the world-creation screen).
- Block edits across the seam: chests and other containers reach the other side, the edit journal stays small, and
  edits from the last moment before a server stops are kept. Each side keeps its own snow.
- Crossing: players and what they ride wait until the other side is loaded; other players and shared boats stay
  smooth through a jump; a crossing on foot over snow works.
- New worlds: a world whose first start stopped (Earth's data download) is set up as new on the retry; Earth's lakes and
  rivers in the seam band keep their water; the planet's pole distance is written as polarEquatorDistance.
- Worldgen: no lava or water sources in the polar ice; lava already saved in old worlds' walls is taken out once.
- Rain works with Earth's weather; hot springs are left to Earth; a game update switches off only the feature it breaks.
Requires Vintage Story 1.22.0. Earth 2.0.0 requires this version.
```
