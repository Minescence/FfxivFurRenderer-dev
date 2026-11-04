# Hand-off — 2026-11-04 (FurRenderer, beads DalamudDev-k4s.5)

## Open problem: two holes in the back collar
The fur shell does not cover the game's coat mesh around the back collar, and the gap grows with arm pose. The strand roots there are skinned mostly to n_hkata_l/r (about 48%), j_ude_a (about 25%) and j_sebo_b (about 10%). Our fur follows the arms, while the game draws the collar as a smooth ring.

Comparison captures are in `%APPDATA%\XIVLauncher\pluginConfigs\FurRenderer\captures\`:
- `kine_mesh.png`: the game mesh, plugin disabled
- `kine_shell.png`: the DebugSolid shell
- `kine_back.png`: the normal fur

All three use camera `{"yaw":180,"pitch":-5,"distance":2.5}`.

## Ruled out (with evidence)
- Timing and threading: the Framework.Update snapshot and a live render-thread read give the same result.
- Weight truncation.
- The choice of pbd.
- Duplicate bones across partials.
- Root coverage in the bind pose.
- Back faces.
- The ril model vs the glv model: identical positions and skinning.
- The blend decode.
- The per-mesh bone-table mapping: verified offline, no out-of-range indices.
- The race-deformer parent fallback: the Nofflebones pbd has explicit entries for n_hkata, j_ude_a, j_sako and j_sebo.
- Meddle uses the same bind source (havok ReferencePose) and pose 0, as we do.
- **The KineDriver** (Ktisis sig `48 8B C4 55 57 48 83 EC 58`):
  - Its first argument is `&Skeleton->PartialSkeletons[0]`. It runs once per frame on a worker thread.
  - The pose it receives has been reset to reference (0° from bind).
  - Skinning from inside that hook caused the T-pose flicker, so it was reverted and the hook removed.
  - The pose at Framework.Update is the final animated one: j_ude_a_l is 71° and n_hkata_l 52° from bind.

## Next steps (cheapest decisive first)
1. **Split palette vs asset.** CPU-skin about 50 raw collar vertices from the mdl, using their own 8 weights, our palette and the race deform. Draw them as debug points.
   - If they leave the rendered collar, our palette is wrong.
   - If they stay on it, the strand asset is the problem.
2. **Check the bind.** Compare `PartialSkeletons[0].SkeletonResourceHandle->InverseBoneMatrix` (+0xF8) for n_hkata_l, j_ude_a_l and j_sebo_b against our `BuildBindInverse` (GameInterop.cs), including the transposed form. Also log `ReferencePose.Scale` along j_sako → j_ude_a → n_hkata, since matrix composition ≠ QS composition when scale ≠ 1.
3. **Check the other poses.** Dump n_hkata_l from havok poses 1–3 (`PartialSkeleton._havokPoses`) with the arm raised.

## State of the code
- The Release build succeeds (`dotnet build FurRenderer/FurRenderer.csproj -c Release`).
- All TEMP diagnostics are removed: DumpPartials, the kine probes and the grow-script `culled-roots.npy` line.
- `ModelInfo` and `RaceInfo` remain in GameInterop as diagnostics.
- In-memory debug overrides were restored: DebugSolid false, LengthScale 1.0, DepthBiasMeters 0.01, Enabled true.

## Other pending work
- Regrow glv_m with the current cull (0.04 m / 1 of 16 rays) plus the 85° overhang, repack it, and copy it to ril_m.
- Update PLAN.md.
- Later: tune against the reference (dither speckle, brightness).

## Tooling notes
- Debug remote control: `$TEMP/fur/req.sh '<json>'`. Always pass `camera`. `set` is not saved, so restore the values afterwards.
- `touch FurRenderer/bin/Release/FurRenderer.dll` forces a plugin reload.
- Grow command (from source/TheSleeveExperiment):
  ```
  "$B" -b --disable-autoexec TheSleeveExperiment.blend -P ../../tools/grow_coat_fur.py -- "$(cat ../coat-fur/glv_X/mdlpath.txt)" ../coat-fur/glv_X/mdl.json OUTDIR 0.1 ../coat-fur/glv_X/layout.json
  ```
  It takes about 3.5 minutes. Pack with `python -I tools/pack_furasset.py DIR OUT.furasset`.
