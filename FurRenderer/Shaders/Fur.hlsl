// Strand fur: each instance is one strand of 4 points (3 segments) expanded into an 8-vertex camera-facing ribbon.
// Passes: PSSceneLuma reduces the frame to what SceneLit reads, PSSurfaceLight keeps the fur surface's share of it, and
// CSStrandMatrices skins and colours each strand; then, all into a multisampled fur layer, VSUnder/PSUnder draw the fur surface
// (undercoat), VSMain/PSMain the strands with per-sample stochastic coverage; the resolved layer (premultiplied) is blended
// over the frame by VSComposite/PSComposite.
// Matrices are row-major and used as mul(v, M) to match System.Numerics.
#pragma pack_matrix(row_major)

struct SkinData
{
    uint2 bones;     // 8 x u8 bone indices (unused: index 0, weight 0)
    float4 w0, w1;
};

StructuredBuffer<float4> Points : register(t0);       // xyz model/bind space, w radius
StructuredBuffer<SkinData> Skins : register(t1);
StructuredBuffer<float4> Bones : register(t2);        // 4 rows per bone (explicit rows: no matrix-packing ambiguity); from MaxBones on, last frame's
Texture2D<float> SceneDepth : register(t3);
Texture2D<float2> SceneLuma : register(t4);           // per pixel: the frame's linear luminance and the scene's view distance
struct MeshVertex
{
    float3 position;
    float3 normal;
};
StructuredBuffer<MeshVertex> MeshVertices : register(t5);
StructuredBuffer<SkinData> MeshSkins : register(t6);
Texture2D<float4> FurLayer : register(t7);            // resolved fur layer, premultiplied by coverage
Texture2DMS<float> FurDepth : register(t8);           // the fur layer's own depth
struct StrandMatrix
{
    float3 r0, r1, r2, r3;  // SkinMatrix rows without w (only mul(float4(p, 1), m).xyz is taken)
    uint3 color;            // SurfaceColor as halves: r | g << 16, b | light gain << 16; then its 'brightest' as a float
    float3 l0, l1, l2, l3;  // the same rows with last frame's bones
};
StructuredBuffer<StrandMatrix> StrandMatrices : register(t9);
RWStructuredBuffer<StrandMatrix> StrandMatricesOut : register(u0);
Texture2D<float2> SurfaceLight : register(t11);       // 1/8 size with mips: the fur surface's luminance x coverage, coverage
SamplerState LinearClamp : register(s0);
Texture2D<float4> SceneColor : register(t10);         // copy of the lit frame (display-encoded) taken just before drawing
Texture2D<float4> FurHistory : register(t30);         // PSTemporal's output last frame
Texture2DMS<float2> Motion : register(t31);           // per fur layer sample: where it was on screen last frame, in pixels from now
Texture2D<float4> SceneAlbedo : register(t29);        // the game's GBuffer 2: rgb = square root of the diffuse colour it lit each pixel with
// Per strand root / undercoat vertex: x = rank of its fur material among the item's | 0x10000 when y holds its UV (u | v << 16,
// halves). Baked assets have no UVs.
StructuredBuffer<uint2> StrandSurfaces : register(t12);
StructuredBuffer<uint2> MeshSurfaces : register(t20);
// The live colour tables of the item's fur materials (dye applied): R16G16B16A16_FLOAT, row r's diffuse at texel (0, r).
Texture2D<float4> ColorTable0 : register(t13);
Texture2D<float4> ColorTable1 : register(t14);
Texture2D<float4> ColorTable2 : register(t15);
Texture2D<float4> ColorTable3 : register(t16);
// Their index maps (which table rows colour each texel) and diffuse maps.
Texture2D<float4> IndexMap0 : register(t21);
Texture2D<float4> IndexMap1 : register(t22);
Texture2D<float4> IndexMap2 : register(t23);
Texture2D<float4> IndexMap3 : register(t24);
Texture2D<float4> DiffuseMap0 : register(t25);
Texture2D<float4> DiffuseMap1 : register(t26);
Texture2D<float4> DiffuseMap2 : register(t27);
Texture2D<float4> DiffuseMap3 : register(t28);
SamplerState LinearWrap : register(s1);
// Culling: CSStrandMatrices lists the baked strands that can show, VSMain draws only those (DrawInstancedIndirect).
StructuredBuffer<uint> VisibleStrands : register(t17);      // front to back, see DepthBuckets
RWStructuredBuffer<uint> VisibleStrandsOut : register(u2);
RWByteAddressBuffer DrawArgs : register(u1);                // DrawInstancedIndirect arguments; this item's InstanceCount at ArgsOffset + 4
// Hi-Z: mip L holds the farthest scene view distance over 2^(L+1) x 2^(L+1) scene depth texels; mip 0 is a power of two at
// least half the depth size, and texels past the depth's edge hold open sky.
Texture2D<float> HiZ : register(t18);
Texture2D<float> HiZSource : register(t19);                 // the mip below the one being built
RWTexture2D<float> HiZDest : register(u3);
// Front-to-back order: CSStrandMatrices keys each visible strand by its depth bucket and its place in it, CSBucketOffsets turns
// the bucket counts into offsets and the instance count, CSScatter writes the visible list in bucket order: nearest first, so
// early-Z rejects more of the strands behind (blending is off and the depth test strict, so only equal-depth ties change).
// Within a bucket the order stays the shuffled asset order: Morton-ordered strands drew 2.8 ms slower (2026-11-08).
static const uint DepthBuckets = 256;
RWStructuredBuffer<uint> StrandKeys : register(u4);         // bucket << 24 | place, or ~0 when not drawn
RWStructuredBuffer<uint> Buckets : register(u5);            // DepthBuckets counts, then DepthBuckets offsets
// Two passes (CullFlags & 4): strands that passed CSOcclusion last frame draw first; the rest of those in view wait (Deferred)
// for CSOcclusion, which tests them against a Hi-Z of the scene and the fur drawn so far, so only fur hidden by fur this frame
// is left out.
RWStructuredBuffer<uint> LastVisible : register(u6);        // per strand: whether it passed CSOcclusion last frame
static const uint Deferred = 0xFFFFFFFE;

cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProj;
    row_major float4x4 Proj;
    float3 CameraPos;      float MinPixelWidth;     // ribbons never get thinner than this many pixels
    float2 ViewportSize;   float2 DepthUvScale;    // SV_Position.xy * DepthUvScale -> scene depth texel
    float3 LightDir;       float LightIntensity;   // LightDir points from surface towards light
    float3 BaseColor;      float RootDarken;       // fur colour of undyed items; colour at root = colour * RootDarken
    float3 AmbientColor;   float Opacity;
    float LengthScale;     float WidthScale;  float DepthBiasMeters; float Specular;
    float FadeStart;       float OutputGamma; float HueJitter;       uint FrameIndex;
    uint UseSceneDepth;    uint TemporalDither; uint DebugSolid; float Gravity;   // Gravity: droop towards world -Y
    float SurfaceLighting; float SceneLighting; float SceneGain; uint HasSceneColor;   // SurfaceLighting: 0 = pure Kajiya-Kay, 1 = soft lighting from the growth direction; SceneLighting: 0 = synthetic only, 1 = brightness from the game's lit item
    float Exposure;        float AgxToneMap;   uint ChildCount;   float ChildSpread;   // AgxToneMap: 0 = gamma only, 1 = AgX like the reference render; ChildCount strands drawn per baked strand
    float Absorption;      float FacingDepth;  float CoverageScale; uint SampleCount;
    float UndercoatDarken; uint TableMask;     uint DrawnStrands; uint CullFlags;   // TableMask: bit k / 4 + k / 8 + k = ColorTable<k> / IndexMap<k> / DiffuseMap<k> is bound
    uint2 HiZSize;         uint HiZLevels;     uint ArgsOffset;   // CullFlags: 1 = outside the view, 2 = behind the scene (Hi-Z)
    uint RecolorUndyed;    float TemporalBlend; float2 Padding;   // RecolorUndyed: items in Hikari's undyed colour take BaseColor; TemporalBlend: PSTemporal's weight of this frame (1 = no history)
    row_major float4x4 LastViewProj;
};

struct VsOut
{
    float4 pos : SV_Position;
    float3 world : WORLDPOS;
    float3 tangent : TANGENT;
    nointerpolation float3 growth : GROWTH;
    float t : INTERCEPT;
    float coverage : COVERAGE;
    nointerpolation uint strand : STRAND;
    nointerpolation float4 surface : SURFACE;   // SurfaceColor of the strand's material
    // Per strand copy: brightness jitter, the scene light at the root (SceneLit's scale) and whether the root is usable for it.
    nointerpolation float3 shade : SHADE;
    nointerpolation float brightest : BRIGHTEST; // SurfaceColor's
    float4 lastClip : LASTCLIP; // clip position last frame
};

float LinearDepth(float d);
float3 SceneLit(float3 color, float2 at, float depth, float gain, float brightest);

float4x4 Bone(uint b)
{
    return float4x4(Bones[b * 4], Bones[b * 4 + 1], Bones[b * 4 + 2], Bones[b * 4 + 3]);
}

static const uint MaxBones = 256; // Renderer.MaxBones

// palette: 0 = this frame's bones, MaxBones = last frame's.
float4x4 SkinMatrix(SkinData s, uint palette)
{
    float w[8] = { s.w0.x, s.w0.y, s.w0.z, s.w0.w, s.w1.x, s.w1.y, s.w1.z, s.w1.w };
    float4x4 m = 0;
    [unroll] for (uint k = 0; k < 8; k++)
        m += Bone(palette + ((s.bones[k / 4] >> (k % 4 * 8)) & 0xFF)) * w[k];
    return m;
}

// Where a point was on screen last frame (last: its clip position then), in pixels from where it is now ('at').
float2 MotionOf(float4 last, float2 at)
{
    return last.w > 0 ? (last.xy / last.w * float2(0.5, -0.5) + 0.5) * ViewportSize - at : 1e5; // behind the camera: no history
}

// Row 0 of Hikari's materials, undyed. Such items take the configured fur colour so an outfit matches the coat (unless
// RecolorUndyed is off); dyes and other colours (the white boots, the red boa) show as they are.
static const float3 UndyedFur = float3(0.984, 0.615, 0.603);
static const float3 Luminance = float3(0.2126, 0.7152, 0.0722);
// How far the game's depth may be from a point of the item's surface where the game shows that surface: under 5 mm in game
// (skinning and depth precision). A tail or horns over the coat lie 1.5 cm and more in front of it.
static const float OwnSurface = 0.01;

float3 TableRow(Texture2D<float4> table, uint row)
{
    uint w, h;
    table.GetDimensions(w, h);
    return table.Load(int3(0, min(row, h - 1), 0)).rgb; // Dawntrail tables have 32 rows, older ones 16
}

// See SurfaceColor. 'mask' bits: 1 = the table is bound, 16 = the index map, 256 = the diffuse map.
float4 MaterialColor(Texture2D<float4> table, Texture2D<float4> index, Texture2D<float4> diffuse, uint mask, bool mapped, float2 uv, float2 dx, float2 dy,
                     out float brightest)
{
    // As the game's character shaders: the index map's red picks a pair of table rows and its green blends them (row 0 without
    // one; every installed fur material's fur_id selects row 0 on all texels), times the diffuse map squared.
    float3 row = UndyedFur;
    if (mask & 1)
    {
        uint pair = 0;
        float blend = 0;
        if (mapped && (mask & 16))
        {
            uint w, h;
            index.GetDimensions(w, h);
            float2 id = index.Load(int3(frac(uv) * float2(w, h), 0)).rg;
            pair = (uint)round(id.r * 255.0 / 17.0);
            blend = 1.0 - id.g;
        }
        row = lerp(TableRow(table, pair * 2), TableRow(table, pair * 2 + 1), blend);
    }
    float3 tint = 1;
    if (mapped && (mask & 256))
    {
        uint w, h, levels;
        diffuse.GetDimensions(0, w, h, levels);
        float3 d = diffuse.SampleGrad(LinearWrap, uv, dx, dy).rgb;
        // The map's mean from its 4x4 mip, all 16 texels in 4 bilinear taps: the 1x1 mip of some maps is black (a 4096x4096
        // fur map's, with its 2x2 to 16x16 mips intact), which made the tint over 100x too bright.
        int level = min(max((int)firstbithigh(min(w, h)) - 2, 0), (int)levels - 1);
        float3 mean = 0;
        [unroll] for (uint k = 0; k < 4; k++)
            mean += diffuse.SampleLevel(LinearWrap, float2(k & 1 ? 0.75 : 0.25, k & 2 ? 0.75 : 0.25), level).rgb / 4;
        // Over the map's mean, so it varies the colour without changing its average.
        tint = d * d / max(dot(mean * mean, Luminance), 1e-3);
    }
    float3 game = row * tint; // what the game draws there
    brightest = mask & 1 ? dot(row, Luminance) : 1e9; // without the table the game's colour is unknown
    bool undyed = !(mask & 1) || distance(row, UndyedFur) <= 0.02;
    float3 color = undyed && RecolorUndyed != 0 ? BaseColor : row;
    return float4(color * tint, min(dot(UndyedFur, Luminance) / max(dot(game, Luminance), 1e-3), 16.0));
}

// Fur colour (rgb) at a point of the item's surface: its fur material ('material', as StrandSurfaces.x) at 'uv', read live so
// dyes and colour-table edits show the next frame. And its scene light gain (a): SceneLit reads the light off the game's own
// lit item, dye and texture included, so without the gain a dark dye darkens the fur twice, as its colour and again as less
// light. It also takes the texture's brightness back out: SceneLit's taps already hold it, averaged, and twice would square
// its contrast. Capped because a near-black item's pixels are mostly sheen, which no dye darkens. dx, dy: uv's screen
// derivatives (0 = the finest mip). 'brightest': the luminance of its colour table row, which its diffuse colour in GBuffer 2
// never exceeds (the diffuse map is at most 1), so SceneLit can leave out other surfaces.
float4 SurfaceColor(uint material, float2 uv, float2 dx, float2 dy, out float brightest)
{
    bool mapped = (material & 0x10000) != 0;
    material = min(material & 0xFFFF, 3u); // an item's fifth fur material and up share the fourth's textures
    uint mask = TableMask >> material;
    float4 result = 0;
    switch (material)
    {
        case 0: result = MaterialColor(ColorTable0, IndexMap0, DiffuseMap0, mask, mapped, uv, dx, dy, brightest); break;
        case 1: result = MaterialColor(ColorTable1, IndexMap1, DiffuseMap1, mask, mapped, uv, dx, dy, brightest); break;
        case 2: result = MaterialColor(ColorTable2, IndexMap2, DiffuseMap2, mask, mapped, uv, dx, dy, brightest); break;
        default: result = MaterialColor(ColorTable3, IndexMap3, DiffuseMap3, mask, mapped, uv, dx, dy, brightest); break;
    }
    return result;
}

float2 UnpackUv(uint packed)
{
    return float2(f16tof32(packed), f16tof32(packed >> 16));
}

uint HashU(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v.x;
}

float Hash(uint3 v)
{
    return (HashU(v) & 0x00FFFFFF) / 16777216.0;
}

float3 PointWorld(uint strand, uint i, float4x4 m, float3 rootModel, float3 rootWorld, float lengthFactor)
{
    float3 p = rootModel + (Points[strand * 4 + i].xyz - Points[strand * 4].xyz) * LengthScale * lengthFactor;
    // Bend towards the ground more along the strand while keeping each point's distance from the root.
    float3 v = mul(float4(p, 1), m).xyz - rootWorld;
    float d = length(v);
    if (d < 1e-6) return rootWorld;
    v.y -= Gravity * d * (i / 3.0) * 2.0;
    return rootWorld + normalize(v) * d;
}

VsOut VSMain(uint vid : SV_VertexID, uint instance : SV_InstanceID)
{
    uint i = vid >> 1;
    float side = (vid & 1) ? 1.0 : -1.0;
    // The asset is baked at a tenth of the reference's interpolated-child density; extra children copy their parent's shape
    // from a root moved randomly across the surface (perpendicular to the parent), with some length variation.
    uint strand = VisibleStrands[instance / ChildCount], child = instance % ChildCount;
    float3 rootModel = Points[strand * 4].xyz;
    float lengthFactor = 1.0;
    if (child > 0)
    {
        float3 g = normalize(Points[strand * 4 + 3].xyz - rootModel);
        float3 a = normalize(cross(g, abs(g.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
        float angle = 6.2831853 * Hash(uint3(strand, child, 11u));
        rootModel += (a * cos(angle) + cross(g, a) * sin(angle)) * ChildSpread * sqrt(Hash(uint3(strand, child, 5u)));
        lengthFactor = 0.8 + 0.3 * Hash(uint3(strand, child, 23u));
    }
    StrandMatrix sm = StrandMatrices[strand];
    float4x4 m = float4x4(float4(sm.r0, 0), float4(sm.r1, 0), float4(sm.r2, 0), float4(sm.r3, 1));
    float3 rootWorld = mul(float4(rootModel, 1), m).xyz;
    float3 p = PointWorld(strand, i, m, rootModel, rootWorld, lengthFactor);
    float3 tangent = normalize(PointWorld(strand, min(i + 1, 3), m, rootModel, rootWorld, lengthFactor) - PointWorld(strand, i > 0 ? i - 1 : 0, m, rootModel, rootWorld, lengthFactor));
    float3 tip = PointWorld(strand, 3, m, rootModel, rootWorld, lengthFactor) - rootWorld;
    float3 toCam = CameraPos - p;
    float dist = length(toCam);
    float3 sideDir = cross(tangent, toCam / max(dist, 1e-5));
    float sideLen = length(sideDir);
    sideDir = sideLen > 1e-5 ? sideDir / sideLen : float3(1, 0, 0);

    float radius = Points[strand * 4 + i].w * WidthScale;
    float worldPerPixel = 2.0 * dist / (abs(Proj._22) * ViewportSize.y);
    // Seen end-on a ribbon shrinks to a dot, so whorls show the deep roots and undercoat between strands (the reference's
    // crimson cores). Spread those dots wider at proportionally lower coverage so they average out instead of speckling
    // (sideLen = sin of the angle between strand and view).
    float halfWidth = max(radius, 0.5 * MinPixelWidth * worldPerPixel) * min(1.0 / max(sideLen, 1e-3), 3.0);
    // Coverage stands in for the reference strands the asset doesn't hold (CoverageScale). Close up it saturates, so spend
    // the rest on width; otherwise silhouettes thin out to see-through.
    float cover = radius / halfWidth * CoverageScale;
    halfWidth *= max(cover, 1.0);
    // Reference material: opaque to FadeStart of the length, then a linear fade to the tip. Done as a taper, which MSAA
    // smooths; fading alpha instead went through the per-sample dither and speckled the tips.
    float t = i / 3.0;
    halfWidth *= t <= FadeStart ? 1.0 : saturate(1.0 - (t - FadeStart) / (1.0 - FadeStart));

    VsOut o;
    o.world = p + sideDir * side * halfWidth;
    o.pos = mul(float4(o.world, 1), ViewProj);
    float4x4 last = float4x4(float4(sm.l0, 0), float4(sm.l1, 0), float4(sm.l2, 0), float4(sm.l3, 1));
    float3 lastPoint = PointWorld(strand, i, last, rootModel, mul(float4(rootModel, 1), last).xyz, lengthFactor);
    o.lastClip = mul(float4(lastPoint + o.world - p, 1), LastViewProj);
    o.tangent = tangent;
    o.growth = length(tip) > 1e-6 ? normalize(tip) : tangent;
    float4 rootClip = mul(float4(rootWorld, 1), ViewProj);
    float3 root = float3((rootClip.xy / rootClip.w * float2(0.5, -0.5) + 0.5) * ViewportSize, rootClip.w); // pixels, view distance
    o.t = t;
    o.coverage = saturate(cover);
    // Not the instance: the visible list's order changes every frame, and the strand's jitter and dither hash this.
    o.strand = strand * ChildCount + child;
    o.surface = float4(f16tof32(sm.color.x), f16tof32(sm.color.x >> 16), f16tof32(sm.color.y), f16tof32(sm.color.y >> 16));
    o.brightest = asfloat(sm.color.z);
    // Once per vertex instead of per fragment, as they are the same for the whole copy. The light comes from the game's item
    // around the root; PSMain takes the surface's light at its own pixel when the root is off-screen or anything hides it,
    // the item's own silhouette included.
    bool rootSeen = all(root.xy >= 4) && all(root.xy < ViewportSize - 4) && root.z > 0
        && !(UseSceneDepth != 0 && LinearDepth(SceneDepth.Load(int3(root.xy * DepthUvScale, 0))) < root.z - OwnSurface);
    o.shade = float3(1.0 + (Hash(uint3(o.strand, 17u, 3u)) - 0.5) * 2.0 * HueJitter,
                     rootSeen ? SceneLit(1.0, root.xy, root.z, o.surface.a, o.brightest).x : 0, rootSeen ? 1 : 0);
    return o;
}

// View-space distance from a hardware depth value, valid for forward, reverse and infinite projections.
float LinearDepth(float d)
{
    return abs(Proj._43 / (d * Proj._34 - Proj._33));
}

// Whether any of a baked strand's drawn copies can show: inside the view, and not behind the farthest scene depth over the
// screen rectangle that bounds them. Conservative; PSMain's BehindScene still decides each pixel.
bool StrandVisible(uint strand, float4x4 m)
{
    if ((CullFlags & 1) == 0)
        return true;
    float3 rootModel = Points[strand * 4].xyz;
    float3 grown = mul(float4(Points[strand * 4 + 3].xyz - rootModel, 0), m).xyz * LengthScale; // root to tip, world
    float scale = max(length(m[0].xyz), max(length(m[1].xyz), length(m[2].xyz)));
    // Copies are up to 1.1x as long and root up to ChildSpread away; ribbons widen up to 3x end-on or by CoverageScale
    // (MinPixelWidth is added on screen below). Straight strands fit a sphere around their middle; droop bends a strand but
    // keeps its length, so drooping ones need one around the root.
    float reach = Gravity > 0 ? 1.1 : 0.55;
    float radius = length(grown) * reach + (ChildSpread + Points[strand * 4].w * WidthScale * max(3.0, CoverageScale)) * scale;
    float4 c = mul(float4(mul(float4(rootModel, 1), m).xyz + grown * (1.1 - reach), 1), ViewProj);
    float nearest = c.w - radius;
    if (nearest <= 0.2) // reaches the game's near plane (0.1 m) or behind the camera: projected bounds don't hold
        return true;
    // The sphere's exact screen bounds, from the tangents in view space (the game's projection is symmetric, no offset).
    float2 v = c.xy / float2(Proj._11, Proj._22);
    float denominator = c.w * c.w - radius * radius;
    float2 t = sqrt(v * v + denominator);
    float2 lo = (v * c.w - radius * t) / denominator * float2(Proj._11, Proj._22);
    float2 hi = (v * c.w + radius * t) / denominator * float2(Proj._11, Proj._22);
    float margin = 1.5 * MinPixelWidth + 1.0;
    float2 pmin = float2(lo.x * 0.5 + 0.5, 0.5 - hi.y * 0.5) * ViewportSize - margin;
    float2 pmax = float2(hi.x * 0.5 + 0.5, 0.5 - lo.y * 0.5) * ViewportSize + margin;
    if (any(pmax < 0) || any(pmin >= ViewportSize))
        return false;
    if ((CullFlags & 2) == 0)
        return true;
    // In depth texels, then the level where the rectangle spans at most 4 x 4 texels (or the 1 x 1 top, which covers all).
    // 2 x 2 at a level twice as coarse kept every strand within ~128 px of a silhouette at 3 m.
    float2 dmin = max(pmin, 0) * DepthUvScale, dmax = min(pmax, ViewportSize - 1) * DepthUvScale;
    float extent = max(max(dmax.x - dmin.x, dmax.y - dmin.y), 1.0);
    uint level = min((uint)max(ceil(log2(extent / 3.0)) - 1.0, 0.0), HiZLevels - 1);
    uint2 a = (uint2)dmin >> (level + 1), b = (uint2)dmax >> (level + 1), last = max(HiZSize >> level, 1u) - 1;
    float farthest = 0;
    [unroll] for (uint y = 0; y < 4; y++)
        [unroll] for (uint x = 0; x < 4; x++)
            farthest = max(farthest, HiZ.Load(int3(min(min(a + uint2(x, y), b), last), level)));
    return nearest <= farthest + DepthBiasMeters;
}

groupshared uint groupBuckets[DepthBuckets], groupBase[DepthBuckets];

// Logarithmic in view distance from 0.1 to 100 m: about 8 cm wide at 3 m.
uint DepthBucket(float distance)
{
    return (uint)clamp(log2(max(distance, 0.1) / 0.1) * (DepthBuckets / log2(1000.0)), 0.0, DepthBuckets - 2.0); // keys stay below Deferred
}

// SkinMatrix and colour per visible baked strand, once per frame instead of in each of its children's 8 vertices, and its key
// in the front-to-back list of strands to draw (see DepthBuckets). Threads of the last group past the buffer's end read zeros and their writes are dropped (out-of-bounds UAV
// writes are discarded).
[numthreads(64, 1, 1)]
void CSStrandMatrices(uint3 id : SV_DispatchThreadID, uint index : SV_GroupIndex)
{
    for (uint b = index; b < DepthBuckets; b += 64)
        groupBuckets[b] = 0;
    GroupMemoryBarrierWithGroupSync();
    float4x4 m = SkinMatrix(Skins[id.x], 0);
    bool visible = id.x < DrawnStrands && StrandVisible(id.x, m);
    // Only VSMain reads the record, through the visible list.
    [branch] if (visible)
    {
        float4x4 last = SkinMatrix(Skins[id.x], MaxBones);
        StrandMatrix o;
        o.r0 = m[0].xyz; o.r1 = m[1].xyz; o.r2 = m[2].xyz; o.r3 = m[3].xyz;
        o.l0 = last[0].xyz; o.l1 = last[1].xyz; o.l2 = last[2].xyz; o.l3 = last[3].xyz;
        uint2 surface = StrandSurfaces[id.x];
        float brightest;
        float4 c = SurfaceColor(surface.x, UnpackUv(surface.y), 0, 0, brightest);
        o.color = uint3(f32tof16(c.r) | f32tof16(c.g) << 16, f32tof16(c.b) | f32tof16(c.a) << 16, asuint(brightest));
        StrandMatricesOut[id.x] = o;
    }
    // By the root's view distance, the place counted per group first: one global atomic per group and bucket, not per strand.
    bool now = visible && ((CullFlags & 4) == 0 || LastVisible[id.x] != 0);
    uint bucket = 0, place = 0;
    if (now)
    {
        bucket = DepthBucket(mul(float4(mul(float4(Points[id.x * 4].xyz, 1), m).xyz, 1), ViewProj).w);
        InterlockedAdd(groupBuckets[bucket], 1, place);
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint k = index; k < DepthBuckets; k += 64)
        if (groupBuckets[k] > 0)
            InterlockedAdd(Buckets[k], groupBuckets[k], groupBase[k]);
    GroupMemoryBarrierWithGroupSync();
    if (id.x < DrawnStrands)
        StrandKeys[id.x] = now ? bucket << 24 | (groupBase[bucket] + place) : visible ? Deferred : ~0u;
}

// Second pass: every strand in view is tested against the Hi-Z with the fur drawn so far (CSHiZFur), which sets LastVisible;
// the deferred ones that pass are keyed for drawing now.
[numthreads(64, 1, 1)]
void CSOcclusion(uint3 id : SV_DispatchThreadID, uint index : SV_GroupIndex)
{
    for (uint b = index; b < DepthBuckets; b += 64)
        groupBuckets[b] = 0;
    GroupMemoryBarrierWithGroupSync();
    uint key = id.x < DrawnStrands ? StrandKeys[id.x] : ~0u;
    bool draw = false;
    uint bucket = 0, place = 0;
    if (key != ~0u)
    {
        StrandMatrix sm = StrandMatricesOut[id.x];
        float4x4 m = float4x4(float4(sm.r0, 0), float4(sm.r1, 0), float4(sm.r2, 0), float4(sm.r3, 1));
        bool seen = StrandVisible(id.x, m);
        LastVisible[id.x] = seen ? 1 : 0;
        draw = key == Deferred && seen;
        if (draw)
        {
            bucket = DepthBucket(mul(float4(mul(float4(Points[id.x * 4].xyz, 1), m).xyz, 1), ViewProj).w);
            InterlockedAdd(groupBuckets[bucket], 1, place);
        }
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint k = index; k < DepthBuckets; k += 64)
        if (groupBuckets[k] > 0)
            InterlockedAdd(Buckets[k], groupBuckets[k], groupBase[k]);
    GroupMemoryBarrierWithGroupSync();
    if (id.x < DrawnStrands)
        StrandKeys[id.x] = draw ? bucket << 24 | (groupBase[bucket] + place) : ~0u;
}

// Hi-Z mip 0 for CSOcclusion: per texel the nearer of the scene and the fur drawn so far, farthest over 2 x 2 texels and over
// the fur's samples (a sample no strand covered holds the far clear, so partly covered pixels occlude nothing).
[numthreads(8, 8, 1)]
void CSHiZFur(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    SceneDepth.GetDimensions(w, h);
    float farthest = 0;
    [unroll] for (uint k = 0; k < 4; k++)
    {
        uint2 p = id.xy * 2 + uint2(k & 1, k >> 1);
        float scene = all(p < uint2(w, h)) ? LinearDepth(SceneDepth.Load(int3(p, 0))) : 1e30;
        // The farthest sample is the smallest or the largest stored depth (forward or reverse Z): convert only those two.
        float lo = 1, hi = 0;
        for (uint s = 0; s < SampleCount; s++)
        {
            float d = FurDepth.Load(int2(float2(p) / DepthUvScale), s);
            lo = min(lo, d);
            hi = max(hi, d);
        }
        float fur = max(LinearDepth(lo), LinearDepth(hi));
        farthest = max(farthest, min(scene, fur));
    }
    HiZDest[id.xy] = farthest;
}

groupshared uint scanned[DepthBuckets];

[numthreads(DepthBuckets, 1, 1)]
void CSBucketOffsets(uint index : SV_GroupIndex)
{
    uint count = Buckets[index];
    scanned[index] = count;
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 1; step < DepthBuckets; step <<= 1)
    {
        uint add = index >= step ? scanned[index - step] : 0;
        GroupMemoryBarrierWithGroupSync();
        scanned[index] += add;
        GroupMemoryBarrierWithGroupSync();
    }
    Buckets[DepthBuckets + index] = scanned[index] - count;
    if (index == DepthBuckets - 1)
        DrawArgs.Store(ArgsOffset + 4, scanned[index] * ChildCount);
}

[numthreads(64, 1, 1)]
void CSScatter(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= DrawnStrands)
        return;
    uint key = StrandKeys[id.x];
    if (key < Deferred)
        VisibleStrandsOut[Buckets[DepthBuckets + (key >> 24)] + (key & 0xFFFFFF)] = id.x;
}

// Hi-Z mip 0 from the scene depth (sky and texels past its edge are infinitely far), then each mip from the one below.
[numthreads(8, 8, 1)]
void CSHiZFirst(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    SceneDepth.GetDimensions(w, h);
    float farthest = 0;
    [unroll] for (uint k = 0; k < 4; k++)
    {
        uint2 p = id.xy * 2 + uint2(k & 1, k >> 1);
        farthest = max(farthest, all(p < uint2(w, h)) ? LinearDepth(SceneDepth.Load(int3(p, 0))) : 1e30);
    }
    HiZDest[id.xy] = farthest;
}

[numthreads(8, 8, 1)]
void CSHiZDown(uint3 id : SV_DispatchThreadID)
{
    float farthest = 0;
    [unroll] for (uint k = 0; k < 4; k++)
        farthest = max(farthest, HiZSource.Load(int3(id.xy * 2 + uint2(k & 1, k >> 1), 0)));
    HiZDest[id.xy] = farthest;
}

// Blender's AgX view transform (the reference render's), minimal fit by bwrensch / iolite-engine.com. Linear Rec.709 in,
// display-encoded out. Matrices are written as GLSL column vectors, so they multiply from the left with mul(v, M).
float3 Agx(float3 c)
{
    const float3x3 inset = float3x3(0.842479062253094, 0.0423282422610123, 0.0423756549057051,
                                    0.0784335999999992, 0.878468636469772, 0.0784336,
                                    0.0792237451477643, 0.0791661274605434, 0.879142973793104);
    const float3x3 outset = float3x3(1.19687900512017, -0.0528968517574562, -0.0529716355144438,
                                     -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
                                     -0.0990297440797205, -0.0989611768448433, 1.15107367264116);
    const float minEv = -12.47393, maxEv = 4.026069;
    float3 x = (clamp(log2(max(mul(c, inset), 1e-10)), minEv, maxEv) - minEv) / (maxEv - minEv);
    float3 x2 = x * x, x4 = x2 * x2;
    x = 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
    return saturate(mul(x, outset));
}

bool BehindScene(float4 pos)
{
    return UseSceneDepth != 0 && LinearDepth(pos.z) > LinearDepth(SceneDepth.Load(int3(pos.xy * DepthUvScale, 0))) + DepthBiasMeters;
}

// Whether back-buffer pixel p shows a surface brighter in colour than the item (SurfaceColor's 'brightest') can be: GBuffer 2
// holds, squared, colour table row x diffuse map squared on the items' pixels (measured), so the item never exceeds its row.
// Such a surface can lie within OwnSurface of the coat, like a tail or horns resting on it or the chin over a collar, and with a
// dark dye the gain made its brightness up to 16x too bright: white fur. Always false without GBuffer 2 (reads 0).
bool BrighterThanItem(int2 p, float brightest)
{
    float3 g = SceneAlbedo.Load(int3(float2(p) * DepthUvScale, 0)).rgb;
    return dot(g * g, Luminance) > brightest * 1.5;
}

// Scales by how brightly the game lit its coat at pixel 'at', so the fur follows the scene's sun, lamps and shadows. Only
// brightness is taken (not the coat texture's colour), averaged over a few taps so texture detail doesn't streak along strands.
// Taps more than OwnSurface from 'depth' (the surface's view distance there) or BrighterThanItem show something else:
// background past a silhouette, or another part of the character. 'depth' 0: no point of the surface is known there (a
// strand's own pixel lies behind the strand), so only SurfaceLight. 'gain' and 'brightest' are SurfaceColor's.
float3 SceneLit(float3 color, float2 at, float depth, float gain, float brightest)
{
    if (HasSceneColor == 0 || SceneLighting <= 0)
        return color;
    static const int2 taps[5] = { int2(0, 0), int2(-4, -4), int2(4, -4), int2(-4, 4), int2(4, 4) };
    float luma = 0, count = 0;
    // Depth 0 can't lie within OwnSurface of any tap (the near plane is 0.1 m), so skip the taps.
    [branch] if (depth > 0 || UseSceneDepth == 0)
    [unroll] for (int k = 0; k < 5; k++)
    {
        int2 p = int2(at) + taps[k];
        float2 s = SceneLuma.Load(int3(p, 0));
        // s.y is 0 only off-screen (out-of-bounds read), where there is no scene to take light from.
        if (UseSceneDepth != 0 && (s.y == 0 || abs(s.y - depth) > OwnSurface) || BrighterThanItem(p, brightest))
            continue;
        luma += s.x;
        count++;
    }
    // No surface here: a strand past the silhouette whose root is off-screen or hidden, up close hundreds of pixels from the
    // surface it grows from. Unscaled it would glow in any scene darker than daylight. Each coarser level of the surface's light
    // fills in what the finer ones lack.
    if (count == 0)
    {
        static const float levels[4] = { 1, 3, 5, 8 };
        [unroll] for (int k = 0; k < 4; k++)
        {
            float2 s = SurfaceLight.SampleLevel(LinearClamp, at / ViewportSize, levels[k]);
            luma += (1 - count) * s.x;
            count += (1 - count) * s.y;
        }
    }
    return count > 0 ? color * lerp(1.0, luma / count * SceneGain * gain, SceneLighting) : color;
}

// What SceneLit reads, once per pixel instead of per tap and fragment.
float2 PSSceneLuma(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(pos.xy);
    return float2(dot(pow(saturate(SceneColor.Load(int3(p, 0)).rgb), OutputGamma), Luminance),
                  LinearDepth(SceneDepth.Load(int3(float2(p) * DepthUvScale, 0))));
}

float4 Output(float3 color)
{
    color *= Exposure;
    float3 display;
    [branch] if (AgxToneMap >= 1) // the default: skip the gamma curve it would blend away
        display = Agx(color);
    else
        display = lerp(pow(saturate(color), 1.0 / OutputGamma), Agx(color), AgxToneMap);
    return float4(display, 1.0);
}

// How directly the view looks down into the fur (cosine between view and fur direction). Fitted to Cycles renders of the
// reference fur on this coat: the colour exponent barely moves until facing 0.7, then climbs steeply (about facing^8).
// A broad (linear) response tinted all camera-facing fur, which showed through the pale side-on strands near silhouettes.
float LookingIn(float facing)
{
    return pow(saturate(facing), 8.0);
}

// Seen end-on, the asset's sparse strands (a fraction of the reference density) let the dark roots and undercoat show between
// the tips where a full pelt would hide them: a dark groove that follows the camera. How much to shade those as tips instead.
float EndOn(float facing)
{
    return 0.8 * smoothstep(0.5, 1.0, facing);
}

// The coat surface under the fur, in the emitter's colour (reference Material.004: fur colour x 0.3, rough metal). Without it
// the game's own coat shows through wherever the fur is seen end-on or thins out.
struct UnderOut
{
    float4 pos : SV_Position;
    float3 world : WORLDPOS;
    float3 normal : NORMAL;
    nointerpolation uint material : MATERIAL; // as MeshSurfaces.x
    float2 uv : TEXCOORD;
    float4 lastClip : LASTCLIP; // clip position last frame
};

UnderOut VSUnder(uint vid : SV_VertexID)
{
    MeshVertex v = MeshVertices[vid];
    float4x4 m = SkinMatrix(MeshSkins[vid], 0);
    UnderOut o;
    o.world = mul(float4(v.position, 1), m).xyz;
    o.lastClip = mul(float4(mul(float4(v.position, 1), SkinMatrix(MeshSkins[vid], MaxBones)).xyz, 1), LastViewProj);
    o.normal = mul(float4(v.normal, 0), m).xyz;
    o.pos = mul(float4(o.world, 1), ViewProj);
    uint2 surface = MeshSurfaces[vid];
    o.material = surface.x;
    o.uv = UnpackUv(surface.y);
    return o;
}

float4 PSUnder(UnderOut i, out float2 motion : SV_Target1) : SV_Target
{
    motion = MotionOf(i.lastClip, i.pos.xy);
    if (BehindScene(i.pos))
        discard;
    float3 n = normalize(i.normal);
    n *= dot(n, CameraPos - i.world) < 0 ? -1.0 : 1.0;
    float diffuse = saturate(dot(n, normalize(LightDir)) * 0.5 + 0.5);
    // Lit like a strand root: light reaches the surface through the fur (absorption as in PSMain at the root).
    float facing = dot(n, normalize(CameraPos - i.world));
    float brightest;
    float4 surface = SurfaceColor(i.material, i.uv, ddx(i.uv), ddy(i.uv), brightest);
    float3 albedo = surface.rgb * UndercoatDarken * pow(max(surface.rgb, 1e-3), Absorption * (1.0 + FacingDepth * LookingIn(facing)));
    albedo = lerp(albedo, surface.rgb, EndOn(facing)); // the gaps between end-on strands, shaded like their tips
    return Output(SceneLit(albedo * (AmbientColor + diffuse * LightIntensity), i.pos.xy, LinearDepth(i.pos.z), surface.a, brightest));
}

// Into SurfaceLight, with VSUnder: what the game lit where its surface is the one this mesh draws (not what is in front of it).
float2 PSSurfaceLight(UnderOut i) : SV_Target
{
    int2 p = int2(i.pos.xy * 8);
    float2 s = SceneLuma.Load(int3(p, 0));
    float brightest;
    SurfaceColor(i.material, i.uv, ddx(i.uv), ddy(i.uv), brightest);
    if (abs(s.y - LinearDepth(i.pos.z)) > OwnSurface || BrighterThanItem(p, brightest))
        discard;
    return float2(s.x, 1);
}

float4 PSMain(VsOut i, uint geometry : SV_Coverage, out uint coverage : SV_Coverage, out float2 motion : SV_Target1) : SV_Target
{
    coverage = 0xFF;
    motion = MotionOf(i.lastClip, i.pos.xy);
    if (BehindScene(i.pos))
        discard;
    if (DebugSolid != 0)
        return float4(1, 0, 1, 1);

    float alpha = i.coverage * Opacity;
    // Stochastic transparency per MSAA sample, stratified: of the samples this strand covers, keep alpha of them (the
    // fraction dithered), starting at a random sample per strand and pixel so overlapping strands still add up.
    // Independent per-sample coin flips gave a pixel at alpha 0.2 anywhere from 0 to 4 of 8 samples (speckled tips).
    uint bits = HashU(uint3(i.pos.xy, (TemporalDither != 0 ? FrameIndex : 0u) + i.strand * 7919u));
    uint kept = (uint)(alpha * countbits(geometry) + (bits & 0xFFFF) / 65536.0);
    uint turn = (bits >> 16) & (SampleCount - 1); // SampleCount is 4 or 8
    // Rotate the covered samples so 'turn' is bit 0, keep the lowest 'kept' of them (the shortest prefix holding that many,
    // by binary search on countbits), rotate back.
    uint all = (1u << SampleCount) - 1;
    uint rot = ((geometry >> turn) | (geometry << (SampleCount - turn))) & all;
    uint n = 0;
    [unroll] for (uint b = 4; b > 0; b >>= 1)
        n = countbits(rot & ((1u << (n + b)) - 1)) < kept ? n + b : n;
    uint keep = kept > 0 ? rot & ((2u << n) - 1) : 0;
    coverage = ((keep << turn) | (keep >> (SampleCount - turn))) & all;
    if (coverage == 0)
        discard;

    // Kajiya-Kay with very rough highlights (reference: roughness 1).
    float3 t = normalize(i.tangent);
    float3 v = normalize(CameraPos - i.world);
    float3 l = normalize(LightDir);
    float tl = dot(t, l);
    // Kajiya-Kay alone goes dark wherever strands point at the light; blend with soft wrap lighting on the growth
    // direction (≈ coat surface normal) to get the multiple-scattering look of the reference hair material.
    float kk = sqrt(saturate(1.0 - tl * tl));
    float surface = saturate(dot(normalize(i.growth), l) * 0.5 + 0.5);
    float diffuse = lerp(kk, surface, SurfaceLighting);
    float3 h = normalize(l + v);
    float th = dot(t, h);
    // Reference hair roughness is 1: the white reflection lobe is very broad (a sheen), not a sharp highlight.
    float spec = pow(sqrt(saturate(1.0 - th * th)), 3.0) * Specular;

    float3 albedo = i.surface.rgb * i.shade.x;
    // Light reaching deep into the fur has passed through many strands (Beer-Lambert: colour^path), which makes roots and
    // fur seen end-on (looking down into it) darker and more saturated, like the reference's magenta partings.
    float cosView = dot(normalize(i.growth), v);
    float buried = (1.0 - i.t) * (1.0 - EndOn(cosView));
    albedo *= pow(max(i.surface.rgb, 1e-3), Absorption * buried * (1.0 + FacingDepth * LookingIn(cosView)));
    // Light reaching this depth of the fur (reference: colour x intercept, black at the root). It shades the sheen too, or
    // roots go grey wherever they are seen side-on (silhouettes, partings).
    float shadow = lerp(RootDarken, 1.0, 1.0 - buried);
    float3 color = (albedo * (AmbientColor + diffuse * LightIntensity) + spec * LightIntensity) * shadow;

    // A branch: as ?: fxc flattened it, running SceneLit's taps and fallback on every fragment.
    [branch] if (i.shade.z > 0)
        color *= i.shade.y;
    else
        color = SceneLit(color, i.pos.xy, 0, i.surface.a, i.brightest);
    return Output(color);
}

float4 VSComposite(uint vid : SV_VertexID) : SV_Position
{
    return float4(float2((vid << 1) & 2, vid & 2) * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PSComposite(float4 pos : SV_Position) : SV_Target
{
    return FurLayer.Load(int3(pos.xy, 0));
}

// FurHistory at 'at' (pixels) through a Catmull-Rom filter in 9 bilinear taps: bilinear alone, repeated every frame the fur
// moves, blurs it within a few frames.
float4 HistoryAt(float2 at)
{
    float2 texel1 = floor(at - 0.5) + 0.5, f = at - texel1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f)), w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f)), w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 x[3] = { texel1 - 1, texel1 + w2 / w12, texel1 + 2 }, w[3] = { w0, w12, w3 };
    float4 sum = 0;
    [unroll] for (int k = 0; k < 9; k++)
        sum += FurHistory.SampleLevel(LinearClamp, float2(x[k % 3].x, x[k / 3].y) / ViewportSize, 0) * w[k % 3].x * w[k / 3].y;
    return sum;
}

// Strands are thinner than a pixel, so their samples change with any sub-pixel movement and the tips twinkle. The fur is drawn
// after the game's own temporal AA (DLSS, TSCMAA), so the layer gets its own: blended with the last frame's result where the
// nearest fur around the pixel was then (Motion), clipped to this frame's 3x3 neighbourhood (mean +- one deviation) so what
// was hidden or uncovered leaves no trail.
float4 PSTemporal(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(pos.xy);
    float4 current = FurLayer.Load(int3(p, 0));
    if (TemporalBlend >= 1)
        return current;
    float4 sum = 0, squares = 0, lo = 1e9, hi = -1e9;
    float2 motion = 0;
    float nearestDistance = 1e30;
    [unroll] for (int k = 0; k < 9; k++)
    {
        int2 q = clamp(p + int2(k % 3 - 1, k / 3 - 1), 0, int2(ViewportSize) - 1);
        float4 c = FurLayer.Load(int3(q, 0));
        sum += c; squares += c * c; lo = min(lo, c); hi = max(hi, c);
        float d = FurDepth.Load(q, 0);
        if (c.a > 0 && LinearDepth(d) < nearestDistance)
        {
            nearestDistance = LinearDepth(d);
            motion = Motion.Load(q, 0);
        }
    }
    float2 at = pos.xy + motion;
    if (any(at < 0) || any(at >= ViewportSize))
        return current; // off-screen last frame
    float4 mean = sum / 9, deviation = sqrt(max(squares / 9 - mean * mean, 0));
    float4 history = clamp(HistoryAt(at), max(mean - deviation, lo), min(mean + deviation, hi));
    return lerp(history, current, TemporalBlend);
}

// Writes the nearest fur depth into the game's depth buffer where the fur is at least half opaque. Without it ReShade's
// depth effects (fake GI, AO) shade fur past the coat's silhouette as the background behind it.
float PSDepth(float4 pos : SV_Position) : SV_Depth
{
    if (FurLayer.Load(int3(pos.xy, 0)).a < 0.5)
        discard;
    float nearest = FurDepth.Load(int2(pos.xy), 0);
    [unroll] for (uint k = 1; k < 8; k++)
    {
        float d = FurDepth.Load(int2(pos.xy), k);
        if (k < SampleCount && LinearDepth(d) < LinearDepth(nearest))
            nearest = d;
    }
    return nearest;
}
