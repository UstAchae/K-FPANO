# K-FPANO

L3Harris F-PANO (fused panoramic night vision goggle) for SPT 4.1.6: the item (built from a purchased 3DMA model, which is never committed) and its server mod; the thermal client joins later.

- `item/` - Blender export (`export_parts.py`), frames, seat on the helmet, flip hinge and figure-8 eyepiece glass (`prepare.py`), Unity bundle build (`Unity/Assets/Editor/FpanoBuilder.cs`), game shader swap (`finalize.py`). `nodes.json` lists the prefab's nodes, the thermal lens marker and the eyepiece discs for the other mods.
- `server/` - SPT server mod (clones the GPNVG-18 template per colour, sold by Peacekeeper), `install.py`.

Model: 3DMA, NVG L3Harris FPGPNVG (credit to 3D Military Assets).
