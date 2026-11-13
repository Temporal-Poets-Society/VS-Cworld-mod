# Mod DB page — Cylinder World (draft)

Draft for mods.vintagestory.at. Current prepared patch: **1.0.2**, preventing new lava sources in polar walls. The first public release was prepared as **1.0.0**, based on the 0.11.14 core.

Publishing direction (31 October 2026): present this as the **first public release**, recommend **256k or 512k** worlds, and state that further mod compatibility (including Terra Pretty) will be tested and developed. Compatibility is not yet verified. Follow-up: **1.0.0 is agreed** for the first published version: the author confirmed satisfaction with the core; testing and broader mod compatibility remain ongoing work. The world-edge setup step was removed at the user's request.

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
| Source Code Url | your repository URL, if public (license is GPL-3.0) |
| Issue tracker Url | the repository's issues page, or leave empty and use comments |
| Homepage / Trailer / Wiki / Donate | optional |

## Fix before uploading

- Patch 1.0.2 (1 Nov): author is `Andris`; upload `release/cylinderworld-1.0.2.zip`. Previous release packages are preserved.
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
3. Click **Add release**, then upload `release/cylinderworld-1.0.2.zip` in **Files**.
4. Select the compatible game versions, add the release notes below, and save the release.
5. When ready to publish, change the mod page status from Draft to Released.

Reference: [Mod DB upload guide](https://www.vintagestory.at/forums/topic/18633-upload-your-mod-to-the-moddb/).

## Release notes for the 1.0.2 file upload

```
1.0.2 - polar wall lava fix
- Prevents vanilla lava and water rivulets from generating in polar wall columns.
- Existing lava in previously generated chunks is not automatically removed.
- Preserves the Earth+ creative tab.

Primarily tailored for Earth Map.
Ongoing work: testing, improvements and broader mod compatibility.

Seam-crossing features:
- Near the jump line your client keeps both sides loaded, so crossing back and forth reloads nothing.
- The whole view is sent before the first jump, nearest the exit first.
- Leaving the seam zone, the other side is unloaded gradually instead of in one frame.
- Each jump writes a diagnostic line to the client and server logs.
Requires Vintage Story 1.22.0. Create a new world.
```
