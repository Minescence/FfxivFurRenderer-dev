// Strand fur: each instance is one strand of 4 points (3 segments) expanded into an 8-vertex camera-facing ribbon.
// Matrices are row-major and used as mul(v, M) to match System.Numerics.
#pragma pack_matrix(row_major)

struct SkinData
{
    uint bones;      // 4 x u8 bone indices
    float4 weights;
};

StructuredBuffer<float4> Points : register(t0);       // xyz model/bind space, w radius
StructuredBuffer<SkinData> Skins : register(t1);
StructuredBuffer<float4> Bones : register(t2);        // 4 rows per bone (explicit rows: no matrix-packing ambiguity)
Texture2D<float> SceneDepth : register(t3);
Texture2D<float4> SceneColor : register(t4);          // copy of the lit frame (display-encoded) taken just before drawing

cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProj;
    row_major float4x4 Proj;
    float3 CameraPos;      float MinPixelWidth;     // ribbons never get thinner than this many pixels
    float2 ViewportSize;   float2 DepthUvScale;    // SV_Position.xy * DepthUvScale -> scene depth texel
    float3 LightDir;       float LightIntensity;   // LightDir points from surface towards light
    float3 BaseColor;      float RootDarken;       // colour at root = BaseColor * RootDarken
    float3 AmbientColor;   float Opacity;
    float LengthScale;     float WidthScale;  float DepthBiasMeters; float Specular;
    float FadeStart;       float OutputGamma; float HueJitter;       uint FrameIndex;
    uint UseSceneDepth;    uint TemporalDither; uint DebugSolid; float Gravity;   // Gravity: droop towards world -Y
    float SurfaceLighting; float SceneLighting; float SceneGain; uint HasSceneColor;
    float Exposure;        float AgxToneMap;   uint ChildCount;   float ChildSpread;   // ChildCount strands drawn per baked strand
    float Absorption;      float FacingDepth;  float CoverageScale; float Padding;          // AgxToneMap: 0 = gamma only, 1 = AgX like the reference render   // SceneLighting: 0 = synthetic only, 1 = colour/light from the game's lit coat             // 0 = pure Kajiya-Kay, 1 = soft lighting from the growth direction
};

struct VsOut
{
    float4 pos : SV_Position;
    float3 world : WORLDPOS;
    float3 tangent : TANGENT;
    nointerpolation float3 growth : GROWTH;
    nointerpolation float3 root : ROOTPX;   // root position in pixels (xy) and view distance (z)
    float t : INTERCEPT;
    float coverage : COVERAGE;
    nointerpolation uint strand : STRAND;
};

float4x4 Bone(uint b)
{
    return float4x4(Bones[b * 4], Bones[b * 4 + 1], Bones[b * 4 + 2], Bones[b * 4 + 3]);
}

float4x4 SkinMatrix(SkinData s)
{
    float4x4 m = Bone(s.bones & 0xFF) * s.weights.x;
    m += Bone((s.bones >> 8) & 0xFF) * s.weights.y;
    m += Bone((s.bones >> 16) & 0xFF) * s.weights.z;
    m += Bone((s.bones >> 24) & 0xFF) * s.weights.w;
    return m;
}

float Hash(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return (v.x & 0x00FFFFFF) / 16777216.0;
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
    uint strand = instance / ChildCount, child = instance % ChildCount;
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
    float4x4 m = SkinMatrix(Skins[strand]);
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
    float halfWidth = max(radius, 0.5 * MinPixelWidth * worldPerPixel);
    // A ribbon seen end-on shrinks to a dot, leaving bald whorls wherever fur points at the camera; widen it there
    // (sideLen = sin of the angle between strand and view) without lowering its coverage.
    float endOnWiden = min(1.0 / max(sideLen, 1e-3), 3.0);

    VsOut o;
    o.world = p + sideDir * side * halfWidth * endOnWiden;
    o.pos = mul(float4(o.world, 1), ViewProj);
    o.tangent = tangent;
    o.growth = length(tip) > 1e-6 ? normalize(tip) : tangent;
    float4 rootClip = mul(float4(rootWorld, 1), ViewProj);
    o.root = float3((rootClip.xy / rootClip.w * float2(0.5, -0.5) + 0.5) * ViewportSize, rootClip.w);
    o.t = i / 3.0;
    o.coverage = saturate(radius / halfWidth * CoverageScale);
    o.strand = instance;
    return o;
}

// View-space distance from a hardware depth value, valid for forward, reverse and infinite projections.
float LinearDepth(float d)
{
    return abs(Proj._43 / (d * Proj._34 - Proj._33));
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

float4 PSMain(VsOut i) : SV_Target
{
    if (UseSceneDepth != 0)
    {
        float scene = SceneDepth.Load(int3(i.pos.xy * DepthUvScale, 0));
        if (LinearDepth(i.pos.z) > LinearDepth(scene) + DepthBiasMeters)
            discard;
    }

    if (DebugSolid != 0)
        return float4(1, 0, 1, 1);

    // Reference material: opaque to 25% of the length, then linear fade to the tip.
    float alpha = (i.t <= FadeStart ? 1.0 : saturate(1.0 - (i.t - FadeStart) / (1.0 - FadeStart))) * i.coverage * Opacity;
    float noise = Hash(uint3(i.pos.xy, TemporalDither != 0 ? FrameIndex : 0u) + uint3(0, 0, i.strand * 7919u));
    if (noise >= alpha)
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

    float jitter = 1.0 + (Hash(uint3(i.strand, 17u, 3u)) - 0.5) * 2.0 * HueJitter;
    float3 albedo = BaseColor * lerp(RootDarken, 1.0, i.t) * jitter;
    // Light reaching deep into the fur has passed through many strands (Beer-Lambert: colour^path), which makes roots and
    // fur seen end-on (looking down into it) darker and more saturated, like the reference's magenta partings.
    float facing = saturate(dot(normalize(i.growth), v));
    albedo *= pow(max(BaseColor, 1e-3), Absorption * (1.0 - i.t) * (1.0 + FacingDepth * facing * facing));
    float3 color = albedo * (AmbientColor + diffuse * LightIntensity) + spec * LightIntensity;

    // Scale by how brightly the game lit the coat around the strand root, so the fur follows the scene's sun, lamps and
    // shadows. Only brightness is taken (not the coat texture's colour), averaged over a few taps so texture detail
    // doesn't streak along strands. Falls back to the strand's own pixel when the root is off-screen or hidden.
    if (HasSceneColor != 0 && SceneLighting > 0)
    {
        float2 at = i.root.xy;
        bool onScreen = all(at >= 4) && all(at < ViewportSize - 4) && i.root.z > 0;
        if (!onScreen || (UseSceneDepth != 0 && LinearDepth(SceneDepth.Load(int3(at * DepthUvScale, 0))) < i.root.z - 0.05))
            at = i.pos.xy;
        static const int2 taps[5] = { int2(0, 0), int2(-4, -4), int2(4, -4), int2(-4, 4), int2(4, 4) };
        float luma = 0;
        [unroll] for (int k = 0; k < 5; k++)
            luma += dot(pow(saturate(SceneColor.Load(int3(int2(at) + taps[k], 0)).rgb), OutputGamma), float3(0.2126, 0.7152, 0.0722));
        color *= lerp(1.0, luma / 5 * SceneGain, SceneLighting);
    }
    color *= Exposure;
    color = lerp(pow(saturate(color), 1.0 / OutputGamma), Agx(color), AgxToneMap);
    return float4(color, 1.0);
}
