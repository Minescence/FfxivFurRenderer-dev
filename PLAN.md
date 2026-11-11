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
  - Root skin is interpolated barycentrically from the coat triangle each root sits on (max root distance 0), keeping up to 8 influences like the game. Top-4 dropped 15–25% of the weight at the back collar and shifted roots ~2 cm, leaving two holes (k4s.5).
  - Output is in `source/coat-fur/`: `coat.furstrands`, `coat.furskin` (bone index = MDL bone list in `coat-fur.json`), and `coat-fur-preview.blend`.
- **Density:**
  - The default amount 0.05 gives 430,786 strands, which is the reference's viewport density.
  - The reference render density (amount 1.0) would be about 8.6M strands, too many for real time.
  - Plan: about 0.4M in real time, plus a GPose "quality" setting with 1–2M strands rendered wider at the tips.
- **Follow-ups:**
  - The 6.43 m² of surface includes interior and overlapping puff faces, so some strands grow where they can't be seen. Cull roots that can't be seen from outside before raising density.
  - The coat model keeps rendering in game, but its salmon texture showed through end-on fur. The plugin now draws the coat surface itself (undercoat, .furasset v3) under the strands.
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

- 2.1 Skin the guides in a compute shader (linear blend skinning, 8 weights; .furasset v3 adds the undercoat mesh) from the bone matrices in 0.4.
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

### 2026-11-05 look-dev pass 3
- Undercoat: `grow_coat_fur.py` exports the coat surface (`coat.furmesh`), packed into .furasset v3 and skinned like the strands. Colour = BaseColor x UndercoatDarken (0.3, the reference emitter Material.004), with the same depth absorption as a strand root. Black was tried (the reference render looks black under the fur) but at real-time density it shows as speckle.
- Fur layer: 8x MSAA (4x fallback) with per-sample stochastic coverage (SV_Coverage), resolved and composited premultiplied. This replaces the per-pixel dither that made pixelated tips and floating pixels.
- End-on widening is now energy-preserving (wider at proportionally lower coverage), so whorls show depth instead of a solid cap.
- Colour fit: reference patches fitted through AgX as k x base^p. Lit side-on fur is p ~0.6 (base plus white sheen), band sides p ~1.9, whorl cores p ~2.8. Defaults are now Absorption 0.4, linear FacingDepth 6, Specular 0.3, RootDarken 0 (config v7). FacingDepth is view-dependent by design; the user flagged angle-dependent shade before (v6 zeroed it).
- Full reference density (10 children) dropped the game to about 20 fps and still left gaps in creases, so density is not the lever.
- The sheen is shadowed by the depth factor too (RootDarken..1 along the strand); unshadowed, roots seen side-on went grey (the coat creator's "black turns grey under the lighting").
- Close up the alpha that stands in for missing density (CoverageScale) saturates; the remainder now widens the ribbon, so silhouettes no longer thin out to see-through.
- SceneLit ignores taps more than 30 cm from the shaded depth (background seen past a silhouette).
- Measured fur-layer opacity (black fur vs fur off): fully opaque inside the silhouette, partial only in a 10-20 px fringe. The "transparent" veil near silhouettes is pale side-on strands lying over crimson end-on strands, not the background or the undercoat (a 10x brighter undercoat changes nothing there).
- Debug captures with "scale" > 1 point-sample and exaggerate noise; use "scale":1 with "crop" for edge work.
- Look target on the actual coat: `tools/render_coat_fur.py` renders the reference fur stack on the game coat in Cycles (CPU; a GPU render froze the game). Renders and facing maps are in `source/reference/coat/`. They show pale, opaque silhouettes and crimson only in partings and where the view runs along the fur.
- Colour vs surface facing in those renders: exponent p ~0.6-0.8 up to facing 0.7, 1.1 at 0.85, 1.5 at 0.92, ~2.1 above 0.98 (both views agree). The FacingDepth response is now facing^8 (was linear), for strands and undercoat. The linear response tinted all camera-facing fur crimson, which showed through the pale side-on strands near silhouettes as a translucent veil.
- Ellipse at the coat edge (seen only in the user's frames): the user runs ReShade (Ipsusu preset: KeepUI, MultiLUT, Glamarye). The game leaves the back buffer alpha at about the red channel, and KeepUI skips all effects where alpha >= 0.8 (it reads that as UI). The fur left the game's alpha alone, so fur over the pale coat went ungraded and fur over the background was graded, with a hard edge at the coat silhouette. The composite now fades the frame alpha by coverage (alpha = 0 under fur).
- Tip speckle: the tip fade went through the per-sample dither (isolated pixels at 1-3/8 coverage). The fade is now a width taper in the vertex shader (coverage unchanged), which MSAA smooths; with FadeStart 1 the tips were already clean. The dither is also stratified over the samples the strand covers (input SV_Coverage), which cuts per-pixel noise where coverage < 1.
- Debug captures: "atPresent": true grabs at Present instead of after the fur draw; "alpha": true saves the alpha channel. At-Present captures did not show the ReShade ellipse, so they may be taken before ReShade's effects (unverified).
- Alpha alone did not fix it: Glamarye fake GI reads depth (GI desaturated and weighted by depth, local AO by depth difference), so fur past the coat silhouette still shaded as background. The renderer now writes the nearest fur depth into the game depth buffer (R24G8, own DSV) where the fur layer is at least half opaque, after the composite. Own MSAA depth is now R32_TYPELESS with an SRV. Skipped when scene depth is off or not frame-sized.
- Debug captures: "depth": true saves the game scene depth after the fur draw (grey = sqrt of the 24-bit value).
- No shadows (user, 2026-11-05): the synthetic light is a fixed world direction from above plus flat ambient, so it ignores the game sun, cast shadows and occlusion. The game-lit path (SceneLighting) was already in the code but the user config had it at 0, and gain 4 clipped sunlit fur to white (game coat linear luma: about 0.66 in full sun, 0.05-0.08 in shadow). Config v8 sets SceneLighting 1 and SceneGain 1.5 (default 1.5); captures f_front_v8/f_back_v8 show the tail shadow and darker undersides. The front Cycles render (front0) has the same crimson facing bands, so FacingDepth is not the bug. Not done: strand-level Kajiya-Kay highlights still use the fixed LightDir, not the game sun.
- White tips / grey black fur (user, 2026-11-05): the Kajiya-Kay sheen is added white and scaled by lerp(RootDarken, 1, t), and at roughness-1 width it covers every strand, so Specular 0.3 added about 0.27 white that grows root to tip. Black BaseColor rendered mid-grey (captures w_black vs w_black_s0). Config v9: Specular 0.05 (default 0.05, about hair Fresnel); black now renders charcoal with form, pink loses the frost (f_front_v9, f_front_v9_black).
- Defaults = user tuning (user, 2026-11-05): the user pressed Reset settings, which dropped their in-game strand tuning against the reference renders, and asked for Reset to give that look. Config v10 makes these the defaults and restores them: ChildCount 3, ChildSpread 0.01, LengthScale 1.5, WidthScale 1.065, MinPixelWidth 0.595, Gravity 0 (captures v10_front, v10_side). FacingDepth stays 6: the reference renders (session images 5-7) show the same crimson centres where the sleeve rolls face the camera; the user had reported them as darkening toward the camera just before.
- Camera-facing darkening (user, 2026-11-05): FacingDepth 6 made deep crimson bands down the lapels and spots at whorls. Config v11: FacingDepth 2 (default 2); captures lp_*, lq_*, v11_*. Left: a thin dark parting line along each lapel ridge and whorls where it kinks. It is geometric (normal-grown strands diverge over the ridge and show their black roots): FacingDepth 0, a brighter undercoat (0.6) or RootDarken 0.15-0.4 do not remove it (RootDarken only turns it red). The coat mesh has 1 cm edges, normals change about 4 degrees per edge, with short sharper seams (>30 degrees) on the lower lapels where the user saw whorls. Fix would be smoothing growth normals in grow_coat_fur.py and re-growing (not done).
- Lapel grooves (user "weird concaves", 2026-11-05): view-dependent, not a mesh crease. The lapels are smooth tubes (about 7 cm radius, 8 deg/cm), and the dark line moved across them when the camera turned 35 deg (mv_0/mv_35). Seen end-on, the sparse asset (a fraction of reference density) shows black roots and the crimson-tinted undercoat between tips. Shader: EndOn(cos) = 0.8*smoothstep(0.5, 1, cos); strands shade as nearer the tip by that much (absorption depth and root shadow), and the undercoat lerps to BaseColor by it (it is what shows in the gaps). Lines, specks and the back/shoulder crimson patches are gone (eo2_whorl, eo2_0, eo2_side). No config change.
- Camera flash (2026-11-05): fur vanished for one frame when the camera moved towards or away from the coat (up/down orbit with ground collision). Occlusion-query probe: those frames drew 0.2-5M fur samples instead of 25-50M. Cause: the main-thread snapshot (camera, bones) was taken in Framework.Update, but the render thread starts drawing the previous Tick's frame as Present returns, about 1.6 ms after Update begins (timeline U -> render binds/trigger -> Present). About 1 frame in 9 the draw beat the snapshot (snapshot advance 0 then 2) and used the camera of the frame before; moving along the view axis put the whole fur behind the 1 cm scene-depth test. Fix: take the snapshot in UiBuilder.Draw (main thread, after Tick, before Present). Snapshot advance is then 1 on 300/300 draws, heavy and light GPU load, and the up/down spin shows no sample dips. Ruled out: dynamic resolution (GraphicsRezoScale 1.0, sizes constant), D3D state, draw target (always ToneAdjustSource).
- Performance (user, 2026-11-05: optimise without changing the look, benchmark later). The strands pass dominates the cost: about 23.5M VS and 61M PS invocations per frame at ChildCount 3. DebugSolid takes 6.5-8 ms against 12-24 ms shaded, but the GPU is shared with another game, so these timings are noisy. Three changes, none measured yet: (1) CSStrandMatrices skins each drawn baked strand once per frame into a 47 MB buffer (StrandMatrix: 4 x float3, UAV u0 / VS t9). Before, SkinMatrix (8 bones, 32 loads) ran in every vertex of every child. (2) PSSceneLuma reduces the frame copy to an R32G32_FLOAT texture once per pixel, holding linear luminance and scene view distance. SceneLit (t4) now does one load per tap; before it did two loads, a pow3 and a divide, five taps per fragment. The frame copy moved to t10. (3) Output skips the gamma curve when AgxToneMap is 1, the default. SavedState now also saves the CS shader, CS t1-t2 and u0, VS t0-t9 and PS t3-t10. Output is identical up to float rounding, with one edge case: an off-screen SceneLit tap used to be skipped (an out-of-bounds depth read of 0 linearises to the far plane) and now counts as luma 0, but only when the fur is within 30 cm of the camera. Capture "opt" checked only that fur draws; no pixel A/B was run.
- Audit of the performance commit (2026-11-05): the note above understates the edge case. If every on-screen tap is rejected, an off-screen tap made count > 0 with luma 0, so close-up fur at the screen border went black (the auditor's harness, comparing old and new builds: up to 206/255 in the 5 px border). SceneLit now skips taps with stored distance 0, which only out-of-bounds reads return. Remaining difference, dormant: with DepthUvScale < 1, tap -1 truncates to depth texel 0, and the old code counted it as luma 0; the new code skips it. The log shows scale 1. A failed luma texture now turns scene lighting off, like a failed frame copy, instead of throwing and later drawing with a null texture.
- Any fur mod (user, 2026-11-05, four read-only subagent reports; implemented, see below, and the disk cache proved unnecessary). 17 materials in 14 installed mods reference chara/common/texture/minamotohikari/fur_*.tex, all on character.shpk; no byte-identical copies of those textures under other paths among 4,764 .tex. Detection: walk CharacterBase.Models -> Model.Materials -> MaterialResourceHandle.TexturePath(i), which stays the game path under Penumbra (read live with Cheat Engine, not yet from plugin code). Today's GetCoatSlot also fires on any item with model set 239, modded or not. No usable CPU vertex data at runtime: read the .mdl from ModelResourceHandle's FileName (disk path for modded, game path for vanilla). Mesh MaterialIndex selects the material; Model.EnabledAttributeIndexMask gives the enabled attributes. Baked strands are simple (checked on coat_ril_f: length 2.5-5 cm, chord/polyline 0.9999, median 3.6 deg off the nearest vertex normal), but 5% lean more than 54 deg, so a port needs the border lean from grow_coat_fur.py as well as the clump step. Density 152.5k strands/m2. Sizes: boots 23-42k, mittens 22k, hat 28-34k, sweater 58-139k, pants 281k, sleepsack 170-191k, boas 178-688k, chair 344k, blanket 1.07-2.68M, asylum wall/floor 7-8.4M (needs a cap). Coat + boots + mittens + hat adds 10%. Formats: 47 mdl, all V6, 30 of them 4-influence, which grow_coat_fur.py:110 can't read. Route picked: grow at runtime in C# with a disk cache, keeping the Blender coat assets until a look A/B passes. A numpy prototype without lean or clump grew the coat in 4.7 s (Blender: 3.5 min). Needed: multi-item renderer and snapshot (Renderer.Draw, SetAsset, FrameSnapshot, Fill, RenderFur), per-item colour (15 materials share row 0 diffuse, 16 have a dye word, so read the live ColorTableTextures, untested), attribute filtering (pants atr_hiz, wall atr_lod), a strand cap. Props (m8037, m8045, m8058, m8100, m8329, m8355) come second: they are their own objects with Monster CharacterBase and own skeletons, and AsHuman rejects them. Baked assets hold the mod's mesh verbatim (pack_furasset.py:49): never commit or ship them, and FurRenderer.csproj copies Assets to the output folder, so check that before packaging.
- Any fur mod, implemented (user, 2026-11-05: everything under 980k strands). FurGrower.cs ports grow_coat_fur.py: V6 .mdl only, LOD0 fur meshes, attribute filter from Model.EnabledAttributeIndexMask, 152,525 strands/m2. No disk cache after all: at most about 0.5 s per surveyed model, on a Task, so only the coat stays baked. Detection walks Models -> Materials -> texture paths; the coat's slots are excluded because its baked assets draw instead. Props: the player's companion, or object indices 202..439 in GPose, Monster type and visible, keyed per actor. Renderer: one fur layer, per-item constants, bones, dispatch and draws. Retired items go to the render thread after the snapshot that drops them is published, and it frees their buffers before reading a snapshot. Face rule, from captures: strands on j_kao/j_ex_met_va (above) and j_kubi/j_ago (below) are half length, don't point at the face vertically, and lean back from borders. Double-sided pants: welding only groups normals that agree, or they cancel to dark streaks. Audits: grown coat vs baked (measured) 980,119 vs 980,124 strands, length p50 3.754 vs 3.751 cm, angle off the normal mean 2.57 vs 2.45 deg; all 46 models parse and grow.
- Dyes (user: the coat stays pink unless black, 2026-11-05). The coat ignored the dye; black only showed through the game's dark pixels lowering the light. Colour now comes from CharacterBase.ColorTableTextures[slot*10+material], first texel, R16G16B16A16_FLOAT: row 0 diffuse with the dye applied. Within 0.02 of undyed (0.984, 0.615, 0.603, read from the coat's .mtrl too) takes config BaseColor. Light is read off the game's own dyed pixel, so dark dyes darkened twice (a flat black shape): SceneGain is scaled per item by undyed luminance / dye luminance, capped at 16. Glamourer.SetItem.V3 needs a List<byte>, not byte[] (base64 in JSON, throws), and a one-element list clears the second channel.
- Bright patches near the screen edge (user screenshots, 2026-11-05). Fragments whose five light taps were all rejected kept the unscaled colour: identical to a SceneLighting 0 render (abs diff 0.0, measured). They are strands past the silhouette with the root off-screen or hidden. Ruled out: wider rings of taps (hard wedges where a tap crosses a silhouette, and misses). Now: the undercoat mesh drawn at 1/8 size where the game's depth agrees within 10 cm, storing luminance x coverage with mips (SurfaceLight), read only by those fragments, each coarser level filling what the finer lack. Smooth, and sky or floor can't leak in. Bind trap: unbind it as a target before GenerateMips and before binding it for reading, or D3D silently nulls the view and it reads empty.
- Hot reload crash (2026-11-05): Hook.Original throws ObjectDisposedException after Dispose, and the detour called it on the render thread during a rebuild. The delegate is cached and Dispose waits for running detours, with a full fence because a volatile write and a read can reorder.
- Releases (user, 2026-11-05): this repo is private (Minescence/FfxivFurRenderer-dev); Minescence/FfxivFurRenderer is public and holds only README.md, repo.json and the release zips, cloned at C:\DalamudDev\FfxivFurRenderer-Release. FurRenderer/DalamudPackager.targets keeps the baked coat assets out of every zip, because they hold the mod's mesh; check the zip anyway. To release: bump Version in the csproj and AssemblyVersion in FurRenderer.json, commit, git clone this repo somewhere clean (a clone has no Assets, since they are gitignored, so the coat grows at runtime), build Release there, check the zip has no Assets and no local paths, gh release create vX.Y.Z on the public repo, regenerate repo.json from the zip's manifest with the new links and LastUpdate, push. Pass the full repo name and stop on failure: the old name once redirected here, and a release landed in the dev repo.
- Other players (user, 2026-11-11; toggle "Other players", config OtherPlayers, default off). Others show a mod's items on this client only when a sync plugin (Lightless, Mare, Snowcloak) applies their mods, so detection stays the fur-texture check; the coat's model ids alone are also the unmodded item, so the coat (baked or grown) now needs its fur textures too. GameInterop.GetOtherHumans: visible Human draw objects at object indices 1..439 other than the target. Their items are keyed by object index (sync plugins redraw on every mod change) and their PBD resolves through Penumbra per object. Sync plugins name cache files by hash (D:\XIVSync\Lightless\<sha>.mdl), so the model's race now comes from Penumbra.GetGameObjectResourcePaths.V5 (EQDP-resolved game path), falling back to the file name. Each other coat wearer adds its own buffers: 236 B per strand, about 230 MB for the 980k-strand coat. First in-game look (user, 2026-11-11, a synced Miqo'te in the two-tone coat: pink body, white trim): all one hot pink, flatly lit. Two causes: the baked coat (the user's own mod files, every strand coloured by the first fur material) drew on her, and RecolorUndyed turned her undyed pink into the user's BaseColor. Others' coats now always grow from their worn model (hers: c0201a0122_ril.mdl, 859,960 strands), and RecolorUndyed is per item (FurDraw.RecolorUndyed, set in BindMaterials), on for the target's items and props only. Debug captures after the fix match her reference.
- Hair in the fur (user, 2026-11-11: dark "shadow" streaks beside a synced player's hair on the coat). Not lighting: SceneLighting 0, RootDarken 1, a 2 mm DepthBias, UndercoatDarken 1 and no culling all kept them; DebugSolid showed the hair itself through gaps between strands. The game draws hair resting on the coat a few mm above its surface, inside the fur (strands reach up to 5 cm x LengthScale), so strands rooted under it covered it. VSMain now drops strands whose root has scene depth OwnSurface..5 cm x LengthScale in front (setting "Hair lies on the fur", HideBuriedStrands, default on); further in front is left to the depth test so fur still shows past a silhouette. A/B captures showed the hair on top as in the user's reference and no visible silhouette thinning (camera moved between them; not a same-view diff). Unhandled: a root covered by the item's own surface within that range (folds, grazing silhouettes) is dropped too; telling it apart needs the item's own depth (a coat-depth pre-pass).
