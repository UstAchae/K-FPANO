# K-FPANO

L3Harris F-PANO (fused panoramic night vision goggle) and the Ops-Core RAILINK powered rails for SPT 4.1.6: the items, their server mod and the thermal client.

## Items (sold by Peacekeeper, black and FDE)

- **F-PANO**: four-tube panoramic night vision with a fused thermal picture (white hot / black hot), a heading HUD and the I2 GAIN bar. Fits nine helmet shapes (13 helmets) in their own NVG slot, or the RAILINK's.
- **RAILINK**: powered rails with the battery built in; the F-PANO picture, cable and green light only when it is powered. Takes the helmet's headset (RAC, ComTac V/VI baked onto the rails), two ARC adapters (PRA/SCRA, one each side), two front lights and two strobes.
- **Lights**: SureFire HL2-A (IR low/high, white low/high), Princeton Tec MPLS (red, white), CORE Survival HEL-STAR rail strobe (IR/visible, dim/bright).

Keys: Ctrl+H the strobes, Ctrl+Left / Ctrl+Right the left / right light; the arrow keys the F-PANO's thermal modes and I2 gain.

Conflicts: the RAILINK conflicts only with the GPNVG-18 and the K-ENVG-B goggles; the rails' F-PANO and the helmet's own NVG slot are not both filled; PRA/SCRA conflict with the ComTac V/VI; mandibles no longer conflict with the rail headsets. The old separate F-PANO mount and battery still load (owned copies) but fit nowhere and are not sold.

## Install

Unzip the release into the SPT folder: `SPT_Runtime/user/mods/UstAchae-FPANO` (server) and `BepInEx/plugins/JustachaeFPANO` (client). Works on its own; with K-3PS-C its eyepieces image the device.

## Repository

- `item/` - Blender export (`export_parts.py`), frames, seat on the helmet, flip hinge and figure-8 eyepiece glass (`prepare.py`), items split (`split_items.py`), RAILINK fits per helmet (`fit_*.py`), ComTac bakes (`bake_headsets.py`), Unity bundle build (`Unity/Assets/Editor/FpanoBuilder.cs`, icon checks `IconCheck*.cs`), game shader swap (`finalize.py`). `nodes.json` lists the prefab's nodes, the thermal lens marker and the eyepiece discs for the other mods.
- `server/` - SPT server mod, `install.py`.
- `client/` - BepInEx plugin (thermal fusion, RAILINK power, lights, headsets), `build/build.sh`.
- `package.py` - the release zip from the built outputs (checks server and client carry one version).

The purchased model sources are never committed. Model: 3DMA, NVG L3Harris FPGPNVG (credit to 3D Military Assets).
