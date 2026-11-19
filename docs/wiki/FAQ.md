# FAQ

**I can't see where the world wraps. Is it working?**
That is the point: both sides of the jump are the same open sea. Type `/cyl info` to see how far the nearest jump line
is, or `/cyl goseam` (with the `tp` privilege) to be put just before it.

**Can I walk across the seam?**
The seam is open ocean about 3 km wide, so you swim or sail across. In Earth worlds the seam runs through the Bering
Strait and across Antarctica; where Earth's land continues across, you can walk.

**Why can't I build near the seam?**
The seam band is protected so both sides stay identical. Build outside it. An admin can turn this off
(`ProtectSeamBand`). Changes near the jump line are then copied to both sides; changes further out in the band are
not.

**I want the sea mist at the seam.**
`/worldconfig cylinderSeamMist true`, then restart the world or server.

**On a boat, the jump pulls me back a little.**
A known limit: the server moves the boat a moment after your game does. You keep the boat and what is in it.

**I froze to death on the ice sheet.**
That is the winter rule: below −40 °C a cold meter fills, and at the Wall in winter it fills in about 4 minutes. Go in
summer, with warm clothes and a fast mount, and watch `/cyl info`. An admin can switch the rule off
(`IceSheetWinterRule`).

**Can I get past the Wall?**
No. It is unbreakable, building near it is blocked, and survival players on top of or behind it are put back in front.

**My world has no walls or seam.**
A superflat world or a server with `Enabled: false` doesn't get them. A world too narrow for the seam gets no
Cylinder World at all (the log says so; about 51,000 blocks wide or more is recommended), and a world too short for the
walls gets none. An existing world you added the mod to keeps its already generated chunks near the edges as they were;
only new chunks get the seam sea and walls.

**I changed the config and nothing happened.**
Most settings are saved into the world when it is made and only change new worlds. The settings marked "live" on the
[Settings](Settings) page apply at the next start.

**The HUD shows a plain X coordinate.**
Yes. `/cyl info` gives your longitude and latitude.
