# FfxivFurRenderer — plan

Dalamud plugin that draws GPU strand fur on a character, approximating a Blender hair render. Local-only (other players don't see it). GPose-first, real-time later.

Predecessor: `C:\DalamudDev\FfxivFurLab` (failed in-model approach; see `archive/failed-shell-card-approach/POSTMORTEM.md`). Its MDL/MTRL toolkit is reused for step 5.

## Inputs requested from the Blender author

1. The `.blend` with the hair system and materials intact, plus the Blender version. Or, failing that:
2. The emitter coat mesh (.glb/.fbx) with armature and weights, in rest pose.
3. Guide hairs as Alembic curves, with children off.
4. Screenshots of the children settings (count, clump, roughness, kink), root/tip radius, and the hair material node tree.
5. The renderer (Cycles or EEVEE) and the light setup.
6. Which FFXIV model or mod the coat came from.

## Reference scene findings (TheSleeveExperiment.blend, received 2026-11-04)

Extracted with `tools/inspect_blend.py` and `tools/export_strands.py`. Outputs are in `source/TheSleeveExperiment/export/`.

- Blender 5.2.1, Cycles. The character is a Meddle 0.1.3 export (Miqo'te female, MekRig, Bibo body with IVCS/YAS bones).
- **The fur is not on the character or the gear.** It grows on 7 separate, unrigged prop meshes (`Full`, `Full.001`–`.006`) using Blender's stock Geometry Nodes hair: Generate → Set Profile → Interpolate → Clump → Trim.
  - `Full.001`–`.004` are the coiled "sleeve" wrapped around the body: 323k strands, 4.5–23 cm from the skin.
  - `Full`, `.005`, `.006` are the background walls: 51k strands.
- **Strands:** 374,460 in total, **4 points each** (3 segments). Length is 2.6–4.9 cm (median 3.7 cm). Radius tapers from 0.3 mm at the root to 0 at the tip.
  - The geometry is cheap. The look comes from density plus shading.
- **Material "Fur (Principled Hair BSDF)":** Chiang model, colour parametrisation.
  - Base colour is linear RGB (0.497, 0.153, 0.227), multiplied 50% by `Intercept`, so roots are half brightness and tips full.
  - Roughness 1, radial roughness 1, coat 1, IOR 1.05, offset 2°.
  - **Opacity:** opaque up to 25% of the length, then a linear fade to transparent at the tip. This soft tip is what makes it read as fluffy instead of spiky.
- **Skin binding:** each root takes the top-4 weights of the nearest posed body vertex (99 bones, all FFXIV names).
  - This is valid for the sleeve fur only; the walls are 0.7–2.3 m away and must stay static.
  - The bind pose is the Meddle pose in the .blend; `boneTable[].poseMatrixWorld` is in Blender world space (Z-up).
  - Converting it into the game's bone frames is unverified. Calibrate it in Phase 2 against a live skeleton.
- **Bone dependency:** `iv_*` and `ya_*` bones only exist with IVCS/Bibo skeletons. Weights on missing bones must fall back to the parent bone.

### Export formats

- `<Curves>.furstrands`: `'FURS'`, u32 version=1, u32 curveCount, u32 pointsPerCurve, then per point float3 position (Blender world, metres) and float radius.
- `<Curves>.furskin`: u32 curveCount, then per curve u16[4] bone index (into `strands-stats.json` `boneTable`) and f32[4] weights.

## Target: Hikari's DT Big Fluffy Coat (decided 2026-11-04)

The goal is to make the installed Penumbra coat (`C:\Penumbra\[Hikari][DT] - Big Fluffy Coat`) as fluffy as the reference. The reference's own sleeve/walls are not shipped.

- **Coat model:** `Model/Default/chara/equipment/e0239/model/c0201e0239_glv.mdl` (Gloves slot, c0201). There is also a Mirage Ring variant.
  - 4 meshes; mesh 0 is body skin and meshes 1–3 are the coat: 84,775 vertices, 127,488 triangles, 6.43 m² of surface.
  - Material: `character.shpk`, using the game's fur/parallax colorset rows (shader IDs 4/5/6) and Hikari's shared fur textures. The volume is a sculpted puff, not shells.
  - Vertex layout: 28+44 bytes. Skinning uses the DT 8-influence packing (8× u8 weights and 8× u8 indices), confirmed by every vertex's weights summing to 255.
- **`tools/grow_coat_fur.py`:** runs the reference's exact hair stack (`Curves.002`) on the coat surface inside the reference .blend.
  - Recipe: guides at 30k/m², 5 cm, 4 points; children at 1M/m²; clump 0.3; radius 0.3 mm; random trim 0.025.
  - It exports strands in **game model space** (the coat's bind space), so the runtime skins them with the same bone palette the game uses for the coat. This removes the Blender→game calibration problem for the coat.
  - Root skin is interpolated barycentrically from the coat triangle each root sits on (max root distance 0), so it is exact.
  - Output is in `source/coat-fur/`: `coat.furstrands`, `coat.furskin` (bone index = MDL bone list in `coat-fur.json`), and `coat-fur-preview.blend`.
- **Density:**
  - The default amount 0.05 gives 430,786 strands, which is the reference's viewport density.
  - The reference render density (amount 1.0) would be about 8.6M strands, too many for real time.
  - Plan: about 0.4M in real time, plus a GPose "quality" setting with 1–2M strands rendered wider at the tips.
- **Follow-ups:**
  - The 6.43 m² of surface includes interior and overlapping puff faces, so some strands grow where they can't be seen. Cull roots that can't be seen from outside before raising density.
  - The coat model keeps rendering in game as the base. Its parallax fur reads as the undercoat (Phase 5 is likely unnecessary).
  - The Gloves model is c0201 only. Male (c0101) and ring variants need the same run against their MDLs.

## Phase 0 — runtime spike (unblocked; no .blend needed)

Prove that we can draw depth-correct, skinned geometry into the scene. Each item records evidence (RE tool, address/signature, screenshot).

- 0.1 **Injection point.** Find the D3D11 pass after lighting composite and before DoF/bloom/tonemap. Use x64dbg/RenderDoc on the game; FFXIV-TV (hooks `DrawIndexed`/`OMSetRenderTargets`) and Dalamud's ReShade addon interface are precedents. Fallback: post-tonemap injection (loses DoF on fur).
- 0.2 **Depth.** Depth buffer resource and format, reverse-Z or not, and a readable SRV/DSV for depth testing.
- 0.3 **Camera.** View/projection matrices from FFXIVClientStructs (RenderCamera / GPose camera), plus TAA jitter, if any, to match.
- 0.4 **Skeleton.** Per-bone model-space matrices for the local player / GPose actor via ClientStructs `Human -> Skeleton -> PartialSkeleton -> hkaPose` (same path as Brio/Ktisis). Match the bone names to the coat's MDL bone table.
- 0.5 **Exit test.** A test quad skinned to `j_kosi` follows the character, is occluded by the body, and survives camera moves in GPose.

## Phase 1 — import pipeline (needs the .blend)

- 1.1 **Done (first pass):** `tools/export_strands.py` exports the evaluated final strands, not guides, together with nearest-vertex skin weights.
  - Guides plus GPU children are deferred. At 374k × 4 points (about 24 MB) the final strands fit on the GPU as they are.
  - Remaining: emit Blender→game axis conversion, and per-curve `surface_normal` (the attribute exists) for shading.
- 1.2 Convert positions to FFXIV model space. Reuse the axis convention from `FfxivFurLab/docs/gltf-coordinate-conventions.md` and verify against `fixtures/legacy-big-collar.source.mdl`.
- 1.3 Offline check: re-render the exported guides in Blender. Compare silhouette and length against the original.

## Phase 2 — GPU strands

- 2.1 Skin the guides in a compute shader (linear blend skinning, 4 weights) from the bone matrices in 0.4.
- 2.2 Children: deferred (see 1.1). Load the exported strands directly. Add GPU children only if the density needs to go above 374k.
- 2.3 Vertex shader expands strands into camera-facing ribbons from `SV_VertexID` (no vertex buffer). Width is clamped to ≥ 1 px, with alpha scaled by true width / 1 px to stop shimmer.
- 2.4 Budget target: 200k strands × 6 segments in real time and 1M in GPose on an RTX 3070. Measure with GPU timestamps.

## Phase 3 — shading toward the reference

- 3.1 Kajiya-Kay (or Marschner-lite) with two shifted specular lobes.
- 3.2 Match the .blend material: colour = base × lerp(1, intercept, 0.5); alpha = 1 until intercept 0.25, then a linear fade to 0. Use a very rough, wide lobe (roughness 1). Add root darkening as a self-shadow stand-in.
- 3.3 Light source: the game's sun/ambient from the environment manager, if found in 0.x; otherwise a user-set key light.
- 3.4 Transparency: weighted blended OIT, composited before post-processing so the game's DoF applies.
- 3.5 Look-dev loop: same camera, game screenshot next to the Blender render.

## Phase 4 — plugin UX

ImGui window: enable/disable, density, length, width, colours, light override, GPose-only toggle, and a perf readout. Config is saved per character.

## Phase 5 — base coat mod (FfxivFurLab toolkit)

Strip the shells/cards (meshes 0–20 of the source coat) and keep a root-coloured fabric under the fur. Package as a Penumbra mod used alongside the plugin.

## Known risks

- Wrong or undiscoverable injection point (0.1). This is the main risk; fallback is post-tonemap.
- No game shadows or point lights on the fur.
- Physics bones (coat/cloth sim) may not match the guides.
- Game patches move signatures; prefer ClientStructs over raw sigs.

## Status (2026-11-04)

Implemented; Release build passes; **not yet run in game**.

- Plugin: `FurRenderer/` (`/fur` opens settings). Output: `FurRenderer/bin/Release/`.
- Assets: `tools/grow_coat_fur.py` (Blender, clones the reference `Curves.002` stack onto the Hikari coat, culls roots inside the body) → `tools/pack_furasset.py` → `FurRenderer/Assets/coat_{glv,ril}_{f,m}.furasset` (758k female / 932k male strands at amount 0.10; ring = copy of gloves).
- In-game checks: depth occlusion and UV scale, reverse-Z (`LastCameraReverseZ`), immediate context, GPose actor 201, bones outside the first partial skeleton, non-c0201 racial deformers.

### 2026-11-04 look-dev pass (autonomous captures)
- Fur verified drawing under the UI (auto bind) in captures.
- Scene lighting reworked: brightness-only from the game's lit coat around the root (5 taps), multiplies the synthetic shading. Defaults SceneLighting 1, SceneGain 4 (config v3 migration).
- End-on strand widening (≤3x) removes bald whorls where fur points at the camera.
- Fur.hlsl is now an embedded resource, so shader edits rebuild the DLL and hot-reload.

### 2026-11-04 look-dev pass 2
- Flashing fixed: some frames render into RenderTargetManager.ToneAdjustSource and are copied to the back buffer last; both count as the main target now.
- Auto draw point: right before the first UI bind after the scene-depth binds (close cameras overwrote fur drawn after bind 2).
- Reference material (from the .blend): Chiang hair BSDF, colour (0.641, 0.205, 0.328) × intercept, roughness 1, coat 1, tip fade from 25%, AgX view transform, render density 1e6/m² (asset baked at 10%).
- Added: AgX tone map + exposure, depth absorption (colour^path, longer path when looking into the fur), broad sheen, GPU child strands.
- Debug requests gained "camera" {yaw (deg from facing), pitch, distance} and "trace"; captures are taken on the render thread.
