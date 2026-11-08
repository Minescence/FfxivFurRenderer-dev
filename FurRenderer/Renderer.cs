using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.D3D11_BLEND;
using static TerraFX.Interop.DirectX.D3D11_COMPARISON_FUNC;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_CULL_MODE;
using static TerraFX.Interop.DirectX.D3D11_DEPTH_WRITE_MASK;
using static TerraFX.Interop.DirectX.D3D11_FILL_MODE;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_RESOURCE_MISC_FLAG;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.D3D_PRIMITIVE_TOPOLOGY;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace FurRenderer;

[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 Proj;
    public Vector3 CameraPos; public float MinPixelWidth;
    public Vector2 ViewportSize; public Vector2 DepthUvScale;
    public Vector3 LightDir; public float LightIntensity;
    public Vector3 BaseColor; public float RootDarken;
    public Vector3 AmbientColor; public float Opacity;
    public float LengthScale, WidthScale, DepthBiasMeters, Specular;
    public float FadeStart, OutputGamma, HueJitter; public uint FrameIndex;
    public uint UseSceneDepth, TemporalDither, DebugSolid; public float Gravity;
    public float SurfaceLighting, SceneLighting, SceneGain; public uint HasSceneColor;
    public float Exposure, AgxToneMap; public uint ChildCount; public float ChildSpread;
    public float Absorption, FacingDepth, CoverageScale; public uint SampleCount;
    public float UndercoatDarken; public uint TableMask, DrawnStrands, CullFlags;
    public uint HiZWidth, HiZHeight, HiZLevels, ArgsOffset;
    public uint RecolorUndyed; public float TemporalBlend; public Vector2 Padding;
    public Matrix4x4 LastViewProj;
}

/// <summary>
/// One item's fur for a frame: its skin palette, its fur materials' textures (SRVs, 0 = none: per material k the live colour
/// table at k, the index map at MaxMaterials + k, the diffuse map at 2 MaxMaterials + k) and the share of its strands to draw.
/// </summary>
internal readonly record struct FurDraw(Renderer.FurBuffers Buffers, Matrix4x4[] Skin, nint[] Textures, float Density);

/// <summary>
/// Draws fur assets (undercoat, then strands) into one multisampled fur layer and blends the resolved layer over the swap-chain
/// back buffer.
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    private readonly ID3D11Device* device;
    private ID3D11VertexShader* vs;
    private ID3D11PixelShader* ps;
    private ID3D11VertexShader* vsUnder;
    private ID3D11PixelShader* psUnder;
    private ID3D11VertexShader* vsComposite;
    private ID3D11PixelShader* psComposite;
    private ID3D11PixelShader* psTemporal;
    private ID3D11PixelShader* psDepth;
    private ID3D11PixelShader* psSceneLuma;
    private ID3D11PixelShader* psSurfaceLight;
    private ID3D11SamplerState* linearClamp, linearWrap;
    private ID3D11ComputeShader* csStrandMatrices;
    private ID3D11ComputeShader* csHiZFirst;
    private ID3D11ComputeShader* csBucketOffsets, csScatter;
    private ID3D11Buffer* buckets;
    private ID3D11UnorderedAccessView* bucketsUav;
    private ID3D11ComputeShader* csHiZDown;
    // Hi-Z of the scene depth (see Fur.hlsl), rebuilt every frame for culling: all mips, and each mip on its own.
    private ID3D11Texture2D* hiZ;
    private ID3D11ShaderResourceView* hiZSrv;
    private ID3D11ShaderResourceView*[] hiZLevelSrvs = [];
    private ID3D11UnorderedAccessView*[] hiZLevelUavs = [];
    private uint hiZDepthW, hiZDepthH, hiZW, hiZH;
    // DrawInstancedIndirect arguments, 16 bytes per item, and copies the CPU reads a few frames late for the visible count.
    private ID3D11Buffer* drawArgs;
    private ID3D11UnorderedAccessView* drawArgsUav;
    private int drawArgsCapacity;
    private readonly ID3D11Buffer*[] argsStaging = new ID3D11Buffer*[QueryFrames];
    private readonly int[] argsItems = new int[QueryFrames];
    private readonly long[] argsTotal = new long[QueryFrames];
    private ID3D11Buffer* constants;
    private ID3D11Buffer* bones;
    private ID3D11ShaderResourceView* bonesSrv;
    private ID3D11BlendState* blend;
    private ID3D11BlendState* compositeBlend;
    private ID3D11RasterizerState* raster;
    private ID3D11DepthStencilState* depthState;
    private ID3D11DepthStencilState* depthWriteState;
    private bool depthStateReverse;
    // Fur layer: multisampled colour (premultiplied once resolved) and depth, and its single-sample resolve.
    private uint sampleCount;
    private ID3D11Texture2D* furColor;
    private ID3D11RenderTargetView* furColorRtv;
    private ID3D11Texture2D* ownDepth;
    private ID3D11DepthStencilView* ownDepthDsv;
    private ID3D11ShaderResourceView* ownDepthSrv;
    // The game's scene depth as a target, for writing the fur's depth into it.
    private ID3D11DepthStencilView* sceneDepthDsv;
    private nint sceneDepthDsvResource;
    private ID3D11Texture2D* furResolved;
    private ID3D11ShaderResourceView* furResolvedSrv;
    // PSTemporal's results, alternating: each frame reads the last one and writes the other. historyTime 0 = none yet.
    private ID3D11Texture2D* history0, history1;
    private ID3D11RenderTargetView* historyRtv0, historyRtv1;
    private ID3D11ShaderResourceView* historySrv0, historySrv1;
    private bool historyFlip;
    private long historyTime;
    private Matrix4x4 lastViewProj;
    // Per fur layer sample, where it was on screen last frame (PSTemporal follows it there).
    private ID3D11Texture2D* motion;
    private ID3D11RenderTargetView* motionRtv;
    private ID3D11ShaderResourceView* motionSrv;
    private uint ownDepthW, ownDepthH;
    private ID3D11RenderTargetView* rtv; // the frame's target (back buffer or ToneAdjustSource), during Draw only
    private uint frameIndex;
    private ID3D11Texture2D* sceneCopy;
    private ID3D11ShaderResourceView* sceneCopySrv;
    private ID3D11Texture2D* sceneLuma;
    private ID3D11RenderTargetView* sceneLumaRtv;
    private ID3D11ShaderResourceView* sceneLumaSrv;
    private const uint SurfaceLightDivisor = 8; // also in PSSurfaceLight
    private ID3D11Texture2D* surfaceLight;
    private ID3D11RenderTargetView* surfaceLightRtv;
    private ID3D11ShaderResourceView* surfaceLightSrv;

    // GPU time and shader invocations of the fur pass, read back a few frames late so nothing waits for the GPU.
    private const int QueryFrames = 4;
    private readonly ID3D11Query*[] disjointQueries = new ID3D11Query*[QueryFrames];
    private readonly ID3D11Query*[] startQueries = new ID3D11Query*[QueryFrames];
    private readonly ID3D11Query*[] endQueries = new ID3D11Query*[QueryFrames];
    private readonly ID3D11Query*[] statsQueries = new ID3D11Query*[QueryFrames];
    private readonly bool[] queryIssued = new bool[QueryFrames];
    private int queryFrame;

    /// <summary>GPU time and shader invocations of a recent fur draw (for the settings window and debug results).</summary>
    public string PerfInfo { get; private set; } = "";

    public const int MaxBones = 256;
    public const int MaxMaterials = 4; // fur materials per item: ColorTable0..3, IndexMap0..3, DiffuseMap0..3 in Fur.hlsl
    public const int MaterialTextures = 3 * MaxMaterials;
    private const float AssetDensityFraction = 0.10f; // tools/grow_coat_fur.py AMOUNT used for the baked assets, and FurGrower's density

    public Renderer(ID3D11Device* device, string shaderPath, byte[] source)
    {
        this.device = device;
        device->AddRef();
        // A shader that fails to compile would otherwise leak the device and everything created before it.
        try { Create(shaderPath, source); }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Create(string shaderPath, byte[] source)
    {
        using (var vsBlob = Compile(source, shaderPath, "VSMain", "vs_5_0"))
            fixed (ID3D11VertexShader** out_ = &vs) Check(device->CreateVertexShader(vsBlob.Pointer, vsBlob.Size, null, out_), "CreateVertexShader");
        using (var psBlob = Compile(source, shaderPath, "PSMain", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &ps) Check(device->CreatePixelShader(psBlob.Pointer, psBlob.Size, null, out_), "CreatePixelShader");
        using (var blob = Compile(source, shaderPath, "VSUnder", "vs_5_0"))
            fixed (ID3D11VertexShader** out_ = &vsUnder) Check(device->CreateVertexShader(blob.Pointer, blob.Size, null, out_), "CreateVertexShader(under)");
        using (var blob = Compile(source, shaderPath, "PSUnder", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &psUnder) Check(device->CreatePixelShader(blob.Pointer, blob.Size, null, out_), "CreatePixelShader(under)");
        using (var blob = Compile(source, shaderPath, "VSComposite", "vs_5_0"))
            fixed (ID3D11VertexShader** out_ = &vsComposite) Check(device->CreateVertexShader(blob.Pointer, blob.Size, null, out_), "CreateVertexShader(composite)");
        using (var blob = Compile(source, shaderPath, "PSComposite", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &psComposite) Check(device->CreatePixelShader(blob.Pointer, blob.Size, null, out_), "CreatePixelShader(composite)");
        using (var blob = Compile(source, shaderPath, "PSTemporal", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &psTemporal) Check(device->CreatePixelShader(blob.Pointer, blob.Size, null, out_), "CreatePixelShader(temporal)");
        using (var blob = Compile(source, shaderPath, "PSDepth", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &psDepth) Check(device->CreatePixelShader(blob.Pointer, blob.Size, null, out_), "CreatePixelShader(depth)");
        using (var blob = Compile(source, shaderPath, "PSSceneLuma", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &psSceneLuma) Check(device->CreatePixelShader(blob.Pointer, blob.Size, null, out_), "CreatePixelShader(scene luma)");
        using (var blob = Compile(source, shaderPath, "PSSurfaceLight", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &psSurfaceLight) Check(device->CreatePixelShader(blob.Pointer, blob.Size, null, out_), "CreatePixelShader(surface light)");
        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP, AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP, AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            MaxLOD = float.MaxValue,
        };
        fixed (ID3D11SamplerState** out_ = &linearClamp) Check(device->CreateSamplerState(&samplerDesc, out_), "CreateSamplerState");
        samplerDesc.AddressU = samplerDesc.AddressV = samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP; // material UVs can lie outside 0..1
        fixed (ID3D11SamplerState** out_ = &linearWrap) Check(device->CreateSamplerState(&samplerDesc, out_), "CreateSamplerState(wrap)");
        using (var blob = Compile(source, shaderPath, "CSStrandMatrices", "cs_5_0"))
            fixed (ID3D11ComputeShader** out_ = &csStrandMatrices) Check(device->CreateComputeShader(blob.Pointer, blob.Size, null, out_), "CreateComputeShader");
        using (var blob = Compile(source, shaderPath, "CSBucketOffsets", "cs_5_0"))
            fixed (ID3D11ComputeShader** out_ = &csBucketOffsets) Check(device->CreateComputeShader(blob.Pointer, blob.Size, null, out_), "CreateComputeShader(bucket offsets)");
        using (var blob = Compile(source, shaderPath, "CSScatter", "cs_5_0"))
            fixed (ID3D11ComputeShader** out_ = &csScatter) Check(device->CreateComputeShader(blob.Pointer, blob.Size, null, out_), "CreateComputeShader(scatter)");
        CreateUavBuffer(2 * 256, ref buckets, ref bucketsUav); // Fur.hlsl DepthBuckets
        using (var blob = Compile(source, shaderPath, "CSHiZFirst", "cs_5_0"))
            fixed (ID3D11ComputeShader** out_ = &csHiZFirst) Check(device->CreateComputeShader(blob.Pointer, blob.Size, null, out_), "CreateComputeShader(Hi-Z)");
        using (var blob = Compile(source, shaderPath, "CSHiZDown", "cs_5_0"))
            fixed (ID3D11ComputeShader** out_ = &csHiZDown) Check(device->CreateComputeShader(blob.Pointer, blob.Size, null, out_), "CreateComputeShader(Hi-Z down)");
        // 4x is guaranteed for these formats at feature level 11; 8x where the GPU has it.
        uint colorLevels, depthLevels;
        sampleCount = device->CheckMultisampleQualityLevels(DXGI_FORMAT_R8G8B8A8_UNORM, 8, &colorLevels).SUCCEEDED && colorLevels > 0
            && device->CheckMultisampleQualityLevels(DXGI_FORMAT_D32_FLOAT, 8, &depthLevels).SUCCEEDED && depthLevels > 0 ? 8u : 4u;

        var cbDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)((Unsafe.SizeOf<FrameConstants>() + 15) & ~15),
            Usage = D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_WRITE,
        };
        fixed (ID3D11Buffer** out_ = &constants) Check(device->CreateBuffer(&cbDesc, null, out_), "CreateBuffer(constants)");
        CreateStructured(null, 2 * MaxBones * 4, sizeof(Vector4), dynamic: true, ref bones, ref bonesSrv); // this frame's, last frame's

        // Stochastic transparency writes opaque samples into the fur layer (alpha = 1 marks covered samples), so blending is off.
        var blendDesc = new D3D11_BLEND_DESC();
        blendDesc.RenderTarget[0].BlendEnable = false;
        blendDesc.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
        blendDesc.RenderTarget[0].DestBlend = D3D11_BLEND_ZERO;
        blendDesc.RenderTarget[0].BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        blendDesc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
        blendDesc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ZERO;
        blendDesc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        blendDesc.RenderTarget[0].RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL;
        fixed (ID3D11BlendState** out_ = &blend) Check(device->CreateBlendState(&blendDesc, out_), "CreateBlendState");
        // The resolved layer is premultiplied by coverage: frame = layer + frame * (1 - coverage). The frame's alpha is faded
        // out under the fur: the game leaves it high on bright surfaces (about the red channel), and ReShade's KeepUI skips
        // effects where alpha >= 0.8, so fur over the pale coat went ungraded while fur over the background was graded.
        blendDesc.RenderTarget[0].BlendEnable = true;
        blendDesc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
        blendDesc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ZERO;
        blendDesc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        fixed (ID3D11BlendState** out_ = &compositeBlend) Check(device->CreateBlendState(&blendDesc, out_), "CreateBlendState(composite)");

        var rasterDesc = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_NONE,
            DepthClipEnable = true,
        };
        fixed (ID3D11RasterizerState** out_ = &raster) Check(device->CreateRasterizerState(&rasterDesc, out_), "CreateRasterizerState");

        for (var i = 0; i < QueryFrames; i++)
        {
            var queryDesc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT };
            fixed (ID3D11Query** out_ = &disjointQueries[i]) Check(device->CreateQuery(&queryDesc, out_), "CreateQuery(disjoint)");
            queryDesc.Query = D3D11_QUERY.D3D11_QUERY_TIMESTAMP;
            fixed (ID3D11Query** out_ = &startQueries[i]) Check(device->CreateQuery(&queryDesc, out_), "CreateQuery(start)");
            fixed (ID3D11Query** out_ = &endQueries[i]) Check(device->CreateQuery(&queryDesc, out_), "CreateQuery(end)");
            queryDesc.Query = D3D11_QUERY.D3D11_QUERY_PIPELINE_STATISTICS;
            fixed (ID3D11Query** out_ = &statsQueries[i]) Check(device->CreateQuery(&queryDesc, out_), "CreateQuery(statistics)");
        }
    }

    // Results of the queries issued QueryFrames draws ago, if the GPU has them; never waits.
    private void ReadPerf(ID3D11DeviceContext* ctx, int slot)
    {
        if (!queryIssued[slot]) return;
        const uint noFlush = (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint;
        D3D11_QUERY_DATA_PIPELINE_STATISTICS stats;
        ulong start, end;
        if (ctx->GetData((ID3D11Asynchronous*)disjointQueries[slot], &disjoint, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT), noFlush).Value != 0
            || ctx->GetData((ID3D11Asynchronous*)startQueries[slot], &start, sizeof(ulong), noFlush).Value != 0
            || ctx->GetData((ID3D11Asynchronous*)endQueries[slot], &end, sizeof(ulong), noFlush).Value != 0
            || ctx->GetData((ID3D11Asynchronous*)statsQueries[slot], &stats, (uint)sizeof(D3D11_QUERY_DATA_PIPELINE_STATISTICS), noFlush).Value != 0
            || disjoint.Disjoint)
            return;
        var drawnShare = "";
        D3D11_MAPPED_SUBRESOURCE mapped;
        // The copy precedes the queries' end, so it is done; DO_NOT_WAIT anyway.
        if (argsStaging[slot] != null && ctx->Map((ID3D11Resource*)argsStaging[slot], 0, D3D11_MAP_READ, (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped).SUCCEEDED)
        {
            long instances = 0;
            for (var k = 0; k < argsItems[slot]; k++) instances += ((uint*)mapped.pData)[k * 4 + 1];
            ctx->Unmap((ID3D11Resource*)argsStaging[slot], 0);
            drawnShare = $"; {instances * 100.0 / Math.Max(argsTotal[slot], 1):F0}% of strands drawn";
        }
        PerfInfo = $"GPU {(end - start) * 1000.0 / disjoint.Frequency:F2} ms; {stats.VSInvocations / 1e6:F1}M vertices, {stats.PSInvocations / 1e6:F1}M fragments{drawnShare}";
    }

    private void EnsureDrawArgs(int items)
    {
        if (drawArgs != null && drawArgsCapacity >= items) return;
        ReleaseDrawArgs();
        var capacity = Math.Max(items, 16);
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(capacity * 16),
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_UNORDERED_ACCESS,
            MiscFlags = (uint)(D3D11_RESOURCE_MISC_DRAWINDIRECT_ARGS | D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS),
        };
        fixed (ID3D11Buffer** out_ = &drawArgs) Check(device->CreateBuffer(&desc, null, out_), "CreateBuffer(draw arguments)");
        var uavDesc = new D3D11_UNORDERED_ACCESS_VIEW_DESC { Format = DXGI_FORMAT_R32_TYPELESS, ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER };
        uavDesc.Buffer.NumElements = (uint)(capacity * 4);
        uavDesc.Buffer.Flags = (uint)D3D11_BUFFER_UAV_FLAG.D3D11_BUFFER_UAV_FLAG_RAW;
        fixed (ID3D11UnorderedAccessView** out_ = &drawArgsUav) Check(device->CreateUnorderedAccessView((ID3D11Resource*)drawArgs, &uavDesc, out_), "CreateUnorderedAccessView(draw arguments)");
        desc = new D3D11_BUFFER_DESC { ByteWidth = desc.ByteWidth, Usage = D3D11_USAGE_STAGING, CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ };
        for (var i = 0; i < QueryFrames; i++)
        {
            fixed (ID3D11Buffer** out_ = &argsStaging[i]) Check(device->CreateBuffer(&desc, null, out_), "CreateBuffer(draw arguments copy)");
            queryIssued[i] = false; // the copies are new and empty
        }
        drawArgsCapacity = capacity;
    }

    private void ReleaseDrawArgs()
    {
        Release(ref drawArgsUav); Release(ref drawArgs);
        for (var i = 0; i < QueryFrames; i++) Release(ref argsStaging[i]);
        drawArgsCapacity = 0;
    }

    // Builds the Hi-Z of this frame's scene depth and binds it for CSStrandMatrices (t18). False if it can't be made.
    private bool BuildHiZ(ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* sceneDepth, Vector2 depthSize)
    {
        uint dw = (uint)depthSize.X, dh = (uint)depthSize.Y;
        if (dw == 0 || dh == 0) return false;
        if (hiZ == null || hiZDepthW != dw || hiZDepthH != dh)
        {
            ReleaseHiZ();
            // A power of two, so every mip halves exactly and each depth texel p lands in texel p >> (L + 1) of mip L.
            hiZW = BitOperations.RoundUpToPowerOf2((dw + 1) / 2);
            hiZH = BitOperations.RoundUpToPowerOf2((dh + 1) / 2);
            var levels = BitOperations.Log2(Math.Max(hiZW, hiZH)) + 1;
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = hiZW, Height = hiZH, MipLevels = (uint)levels, ArraySize = 1, Format = DXGI_FORMAT_R32_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE_DEFAULT,
                BindFlags = (uint)(D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS),
            };
            var created = false;
            fixed (ID3D11Texture2D** out_ = &hiZ) created = device->CreateTexture2D(&desc, null, out_).SUCCEEDED;
            if (created) fixed (ID3D11ShaderResourceView** out_ = &hiZSrv) created = device->CreateShaderResourceView((ID3D11Resource*)hiZ, null, out_).SUCCEEDED;
            hiZLevelSrvs = new ID3D11ShaderResourceView*[levels];
            hiZLevelUavs = new ID3D11UnorderedAccessView*[levels];
            for (var l = 0; created && l < levels; l++)
            {
                var srvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = DXGI_FORMAT_R32_FLOAT, ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D };
                srvDesc.Texture2D.MostDetailedMip = (uint)l;
                srvDesc.Texture2D.MipLevels = 1;
                var uavDesc = new D3D11_UNORDERED_ACCESS_VIEW_DESC { Format = DXGI_FORMAT_R32_FLOAT, ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_TEXTURE2D };
                uavDesc.Texture2D.MipSlice = (uint)l;
                fixed (ID3D11ShaderResourceView** out_ = &hiZLevelSrvs[l]) created = device->CreateShaderResourceView((ID3D11Resource*)hiZ, &srvDesc, out_).SUCCEEDED;
                if (created) fixed (ID3D11UnorderedAccessView** out_ = &hiZLevelUavs[l]) created = device->CreateUnorderedAccessView((ID3D11Resource*)hiZ, &uavDesc, out_).SUCCEEDED;
            }
            if (!created) { ReleaseHiZ(); return false; }
            (hiZDepthW, hiZDepthH) = (dw, dh);
        }
        ctx->CSSetShader(csHiZFirst, null, 0);
        ctx->CSSetShaderResources(3, 1, &sceneDepth);
        for (var l = 0; l < hiZLevelUavs.Length; l++)
        {
            // The mip being written first: binding the one below for reading while it is still the target nulls the view.
            var uav = hiZLevelUavs[l];
            ctx->CSSetUnorderedAccessViews(3, 1, &uav, null);
            if (l == 1) ctx->CSSetShader(csHiZDown, null, 0);
            if (l > 0) { var below = hiZLevelSrvs[l - 1]; ctx->CSSetShaderResources(19, 1, &below); }
            ctx->Dispatch((Math.Max(hiZW >> l, 1u) + 7) / 8, (Math.Max(hiZH >> l, 1u) + 7) / 8, 1);
        }
        ID3D11UnorderedAccessView* noUav = null;
        ID3D11ShaderResourceView* noSrv = null;
        ctx->CSSetUnorderedAccessViews(3, 1, &noUav, null);
        ctx->CSSetShaderResources(19, 1, &noSrv);
        ctx->CSSetShaderResources(3, 1, &noSrv); // the scene depth becomes a depth target later in the frame
        var all = hiZSrv;
        ctx->CSSetShaderResources(18, 1, &all);
        return true;
    }

    private void ReleaseHiZ()
    {
        for (var l = 0; l < hiZLevelSrvs.Length; l++) Release(ref hiZLevelSrvs[l]);
        for (var l = 0; l < hiZLevelUavs.Length; l++) Release(ref hiZLevelUavs[l]);
        hiZLevelSrvs = [];
        hiZLevelUavs = [];
        Release(ref hiZSrv); Release(ref hiZ);
        hiZDepthW = hiZDepthH = 0;
    }

    /// <summary>The GPU side of one fur asset. Render thread only.</summary>
    internal sealed class FurBuffers : IDisposable
    {
        public ID3D11Buffer* Points, Skins, StrandSurfaces, MeshVertices, MeshSurfaces, MeshSkins, MeshIndices, StrandMatrices, VisibleStrands;
        public ID3D11ShaderResourceView* PointsSrv, SkinsSrv, StrandSurfacesSrv, MeshVerticesSrv, MeshSurfacesSrv, MeshSkinsSrv, StrandMatricesSrv, VisibleStrandsSrv;
        public ID3D11UnorderedAccessView* StrandMatricesUav, VisibleStrandsUav, StrandKeysUav;
        public ID3D11Buffer* StrandKeys;
        public Matrix4x4[]? LastSkin; // the skin palette drawn last frame
        public int StrandCount;
        public uint MeshIndexCount;

        public void Dispose()
        {
            Release(ref PointsSrv); Release(ref Points);
            Release(ref SkinsSrv); Release(ref Skins);
            Release(ref StrandSurfacesSrv); Release(ref StrandSurfaces);
            Release(ref MeshVerticesSrv); Release(ref MeshVertices);
            Release(ref MeshSurfacesSrv); Release(ref MeshSurfaces);
            Release(ref MeshSkinsSrv); Release(ref MeshSkins);
            Release(ref MeshIndices);
            Release(ref StrandMatricesUav); Release(ref StrandMatricesSrv); Release(ref StrandMatrices);
            Release(ref VisibleStrandsUav); Release(ref VisibleStrandsSrv); Release(ref VisibleStrands);
            Release(ref StrandKeysUav); Release(ref StrandKeys);
        }
    }

    public FurBuffers CreateBuffers(FurAsset asset)
    {
        var b = new FurBuffers { StrandCount = asset.StrandCount, MeshIndexCount = (uint)asset.MeshIndices.Length };
        try
        {
            fixed (StrandPoint* p = asset.Points)
                CreateStructured(p, asset.Points.Length, sizeof(StrandPoint), dynamic: false, ref b.Points, ref b.PointsSrv);
            fixed (StrandSkin* p = asset.Skins)
                CreateStructured(p, asset.Skins.Length, sizeof(StrandSkin), dynamic: false, ref b.Skins, ref b.SkinsSrv);
            fixed (StrandSurface* p = asset.StrandSurfaces)
                CreateStructured(p, asset.StrandSurfaces.Length, sizeof(StrandSurface), dynamic: false, ref b.StrandSurfaces, ref b.StrandSurfacesSrv);
            const int matrixStride = 8 * 12 + 12; // StrandMatrix: 4 x float3, uint3 colour, 4 x float3 (last frame's)
            var matricesDesc = new D3D11_BUFFER_DESC
            {
                ByteWidth = checked((uint)(asset.StrandCount * matrixStride)),
                Usage = D3D11_USAGE_DEFAULT,
                BindFlags = (uint)(D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS),
                MiscFlags = (uint)D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
                StructureByteStride = matrixStride,
            };
            fixed (ID3D11Buffer** out_ = &b.StrandMatrices) Check(device->CreateBuffer(&matricesDesc, null, out_), "CreateBuffer(strand matrices)");
            fixed (ID3D11ShaderResourceView** out_ = &b.StrandMatricesSrv)
                Check(device->CreateShaderResourceView((ID3D11Resource*)b.StrandMatrices, null, out_), "CreateShaderResourceView(strand matrices)");
            fixed (ID3D11UnorderedAccessView** out_ = &b.StrandMatricesUav)
                Check(device->CreateUnorderedAccessView((ID3D11Resource*)b.StrandMatrices, null, out_), "CreateUnorderedAccessView(strand matrices)");
            matricesDesc.ByteWidth = checked((uint)(asset.StrandCount * sizeof(uint)));
            matricesDesc.StructureByteStride = sizeof(uint);
            fixed (ID3D11Buffer** out_ = &b.VisibleStrands) Check(device->CreateBuffer(&matricesDesc, null, out_), "CreateBuffer(visible strands)");
            fixed (ID3D11ShaderResourceView** out_ = &b.VisibleStrandsSrv)
                Check(device->CreateShaderResourceView((ID3D11Resource*)b.VisibleStrands, null, out_), "CreateShaderResourceView(visible strands)");
            fixed (ID3D11UnorderedAccessView** out_ = &b.VisibleStrandsUav)
                Check(device->CreateUnorderedAccessView((ID3D11Resource*)b.VisibleStrands, null, out_), "CreateUnorderedAccessView(visible strands)");
            CreateUavBuffer(asset.StrandCount, ref b.StrandKeys, ref b.StrandKeysUav);
            if (asset.MeshIndices.Length > 0)
            {
                fixed (MeshVertex* p = asset.MeshVertices)
                    CreateStructured(p, asset.MeshVertices.Length, sizeof(MeshVertex), dynamic: false, ref b.MeshVertices, ref b.MeshVerticesSrv);
                fixed (StrandSurface* p = asset.MeshSurfaces)
                    CreateStructured(p, asset.MeshSurfaces.Length, sizeof(StrandSurface), dynamic: false, ref b.MeshSurfaces, ref b.MeshSurfacesSrv);
                fixed (StrandSkin* p = asset.MeshSkins)
                    CreateStructured(p, asset.MeshSkins.Length, sizeof(StrandSkin), dynamic: false, ref b.MeshSkins, ref b.MeshSkinsSrv);
                fixed (uint* p = asset.MeshIndices)
                {
                    var desc = new D3D11_BUFFER_DESC
                    {
                        ByteWidth = checked((uint)(asset.MeshIndices.Length * sizeof(uint))),
                        Usage = D3D11_USAGE_IMMUTABLE,
                        BindFlags = (uint)D3D11_BIND_INDEX_BUFFER,
                    };
                    var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                    fixed (ID3D11Buffer** out_ = &b.MeshIndices) Check(device->CreateBuffer(&desc, &init, out_), "CreateBuffer(indices)");
                }
            }
            return b;
        }
        catch
        {
            b.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Row 0 diffuse of a material's live colour table (dye applied), for the log: the shaders read it themselves. Null if it
    /// can't be read. Waits for the GPU, so call it only when the table changes.
    /// </summary>
    public Vector3? ReadTableDiffuse(ID3D11DeviceContext* ctx, ID3D11ShaderResourceView* view)
    {
        ID3D11Resource* resource;
        view->GetResource(&resource);
        try { return ReadTableDiffuse(ctx, (ID3D11Texture2D*)resource); }
        finally { resource->Release(); }
    }

    private Vector3? ReadTableDiffuse(ID3D11DeviceContext* ctx, ID3D11Texture2D* table)
    {
        D3D11_TEXTURE2D_DESC desc;
        table->GetDesc(&desc);
        if (desc.Format != DXGI_FORMAT_R16G16B16A16_FLOAT) return null;
        desc.Width = desc.Height = desc.MipLevels = desc.ArraySize = 1;
        desc.SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 };
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;
        ID3D11Texture2D* staging;
        if (device->CreateTexture2D(&desc, null, &staging).FAILED) return null;
        var box = new D3D11_BOX { right = 1, bottom = 1, back = 1 };
        ctx->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)table, 0, &box);
        Vector3? diffuse = null;
        D3D11_MAPPED_SUBRESOURCE mapped;
        if (ctx->Map((ID3D11Resource*)staging, 0, D3D11_MAP_READ, 0, &mapped).SUCCEEDED)
        {
            var texel = (Half*)mapped.pData;
            diffuse = new Vector3((float)texel[0], (float)texel[1], (float)texel[2]);
            ctx->Unmap((ID3D11Resource*)staging, 0);
        }
        staging->Release();
        return diffuse;
    }

    /// <summary>A texture view's size, format and mips (for the log).</summary>
    public static string Describe(ID3D11ShaderResourceView* view)
    {
        D3D11_SHADER_RESOURCE_VIEW_DESC desc;
        view->GetDesc(&desc);
        ID3D11Resource* resource;
        view->GetResource(&resource);
        D3D11_TEXTURE2D_DESC tex;
        ((ID3D11Texture2D*)resource)->GetDesc(&tex);
        resource->Release();
        return $"{tex.Width}x{tex.Height} {desc.Format}, {desc.Texture2D.MipLevels} mips";
    }

    /// <summary>Back buffer, depth mode and camera of the last draw (for the debug log).</summary>
    public string LastDrawInfo { get; private set; } = "";

    /// <summary>Draws each item's <see cref="FurDraw.Density"/> of its strands.</summary>
    /// <param name="sceneAlbedo">GBuffer 2, see <see cref="GameInterop.GetSceneAlbedo"/>; without it SceneLit can't tell other surfaces from the item.</param>
    public void Draw(ID3D11DeviceContext* ctx, ID3D11Texture2D* backBuffer, ID3D11ShaderResourceView* sceneDepth, ID3D11ShaderResourceView* sceneAlbedo,
        Vector2 depthSize, List<FurDraw> draws, Matrix4x4 view, Matrix4x4 proj, int childCount, Configuration cfg)
    {
        if (draws.Count == 0) return;
        D3D11_TEXTURE2D_DESC bbDesc;
        backBuffer->GetDesc(&bbDesc);
        EnsureTargets(backBuffer, bbDesc);
        var reverse = IsReverseZ(proj);
        EnsureDepthState(reverse);
        if (cfg.SceneLighting > 0 && EnsureSceneCopy(bbDesc))
            ctx->CopyResource((ID3D11Resource*)sceneCopy, (ID3D11Resource*)backBuffer);

        // Constants
        Matrix4x4.Invert(view, out var invView);
        var light = cfg.LightDirection.LengthSquared() > 0 ? Vector3.Normalize(cfg.LightDirection) : Vector3.UnitY;
        var autoUv = new Vector2(depthSize.X / bbDesc.Width, depthSize.Y / bbDesc.Height);
        var fc = new FrameConstants
        {
            ViewProj = view * proj,
            LastViewProj = historyTime != 0 ? lastViewProj : view * proj,
            Proj = proj,
            CameraPos = invView.Translation,
            MinPixelWidth = cfg.MinPixelWidth,
            ViewportSize = new Vector2(bbDesc.Width, bbDesc.Height),
            DepthUvScale = cfg.DepthUvScaleOverride > 0 ? new Vector2(cfg.DepthUvScaleOverride) : autoUv,
            LightDir = light,
            LightIntensity = cfg.LightIntensity,
            BaseColor = cfg.BaseColor,
            RecolorUndyed = cfg.RecolorUndyed ? 1u : 0u,
            // Without a recent result (none yet, resized, or the fur not drawn for a while), start from this frame.
            TemporalBlend = historyTime != 0 && Stopwatch.GetElapsedTime(historyTime).TotalSeconds < 0.5 ? 1f - Math.Clamp(cfg.TemporalSmoothing, 0f, 0.9f) : 1f,
            RootDarken = cfg.RootDarken,
            UndercoatDarken = cfg.UndercoatDarken,
            AmbientColor = cfg.AmbientColor,
            Opacity = cfg.Opacity,
            LengthScale = cfg.LengthScale,
            WidthScale = cfg.WidthScale,
            DepthBiasMeters = cfg.DepthBiasMeters,
            Specular = cfg.Specular,
            FadeStart = Math.Clamp(cfg.FadeStart, 0f, 0.99f),
            OutputGamma = Math.Max(cfg.OutputGamma, 0.1f),
            HueJitter = cfg.HueJitter,
            FrameIndex = frameIndex++,
            UseSceneDepth = cfg.UseSceneDepth && sceneDepth != null ? 1u : 0u,
            TemporalDither = cfg.TemporalDither ? 1u : 0u,
            DebugSolid = cfg.DebugSolid ? 1u : 0u,
            Gravity = cfg.Gravity,
            SurfaceLighting = cfg.SurfaceLighting,
            SceneLighting = cfg.SceneLighting,
            SceneGain = cfg.SceneGain,
            HasSceneColor = sceneCopySrv != null && cfg.SceneLighting > 0 ? 1u : 0u,
            Exposure = cfg.Exposure,
            AgxToneMap = Math.Clamp(cfg.AgxToneMap, 0f, 1f),
            ChildCount = (uint)Math.Clamp(childCount, 1, 16),
            ChildSpread = cfg.ChildSpread,
            Absorption = cfg.Absorption,
            FacingDepth = cfg.FacingDepth,
            SampleCount = sampleCount,
        };
        LastDrawInfo = $"backbuffer {bbDesc.Width}x{bbDesc.Height} {bbDesc.Format}; reverseZ(proj) {reverse}; depthUvScale {fc.DepthUvScale}; sceneDepth {(fc.UseSceneDepth != 0 ? "on" : "off")}";
        Upload(ctx, (ID3D11Resource*)constants, &fc, sizeof(FrameConstants));

        using var saved = new SavedState(ctx);
        var querySlot = queryFrame++ % QueryFrames;
        ReadPerf(ctx, querySlot);
        ctx->Begin((ID3D11Asynchronous*)disjointQueries[querySlot]);
        ctx->End((ID3D11Asynchronous*)startQueries[querySlot]);
        ctx->Begin((ID3D11Asynchronous*)statsQueries[querySlot]);
        var transparent = stackalloc float[4];
        ctx->ClearRenderTargetView(furColorRtv, transparent);
        ctx->ClearRenderTargetView(motionRtv, transparent);
        ctx->ClearDepthStencilView(ownDepthDsv, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, reverse ? 0f : 1f, 0);
        var furRtvs = stackalloc ID3D11RenderTargetView*[2] { furColorRtv, motionRtv };
        ctx->OMSetRenderTargets(2, furRtvs, ownDepthDsv);
        var viewport = new D3D11_VIEWPORT { Width = bbDesc.Width, Height = bbDesc.Height, MaxDepth = 1 };
        ctx->RSSetViewports(1, &viewport);
        ctx->RSSetState(raster);
        var blendFactor = stackalloc float[4];
        ctx->OMSetBlendState(blend, blendFactor, 0xFFFFFFFF);
        ctx->OMSetDepthStencilState(depthState, 0);
        ctx->IASetInputLayout(null);
        var cb = constants;
        ctx->VSSetConstantBuffers(0, 1, &cb);
        ctx->PSSetConstantBuffers(0, 1, &cb);
        ctx->CSSetConstantBuffers(0, 1, &cb);
        var wrap = linearWrap; // SurfaceColor
        ctx->CSSetSamplers(1, 1, &wrap);
        ctx->PSSetSamplers(1, 1, &wrap);
        if (fc.HasSceneColor != 0)
        {
            ctx->PSSetShaderResources(29, 1, &sceneAlbedo); // SceneLit and PSSurfaceLight
            ctx->VSSetShaderResources(29, 1, &sceneAlbedo); // VSMain's SceneLit at strand roots
            // Before the fur pass binds the result for reading.
            var lumaRtv = sceneLumaRtv;
            ctx->OMSetRenderTargets(1, &lumaRtv, null);
            ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ctx->VSSetShader(vsComposite, null, 0);
            ctx->PSSetShader(psSceneLuma, null, 0);
            ctx->PSSetShaderResources(3, 1, &sceneDepth);
            var copy = sceneCopySrv;
            ctx->PSSetShaderResources(10, 1, &copy);
            ctx->Draw(3, 0);
            if (fc.UseSceneDepth != 0)
            {
                var lightRtv = surfaceLightRtv;
                ctx->OMSetRenderTargets(1, &lightRtv, null);
                ctx->ClearRenderTargetView(lightRtv, transparent);
                var lightViewport = viewport;
                lightViewport.Width = Math.Max(bbDesc.Width / SurfaceLightDivisor, 1);
                lightViewport.Height = Math.Max(bbDesc.Height / SurfaceLightDivisor, 1);
                ctx->RSSetViewports(1, &lightViewport);
                ctx->VSSetShader(vsUnder, null, 0);
                ctx->PSSetShader(psSurfaceLight, null, 0);
                var luma = sceneLumaSrv;
                ctx->PSSetShaderResources(4, 1, &luma);
                var boneSrv = bonesSrv;
                ctx->VSSetShaderResources(2, 1, &boneSrv);
                var meshSrvs = stackalloc ID3D11ShaderResourceView*[2];
                foreach (var draw in draws)
                {
                    var item = draw.Buffers;
                    if (item.MeshIndices == null) continue;
                    BindMaterials(ctx, draw, ref fc); // PSSurfaceLight leaves out other surfaces by the item's colour
                    Upload(ctx, (ID3D11Resource*)constants, &fc, sizeof(FrameConstants));
                    UploadBones(ctx, draw);
                    meshSrvs[0] = item.MeshVerticesSrv;
                    meshSrvs[1] = item.MeshSkinsSrv;
                    ctx->VSSetShaderResources(5, 2, meshSrvs);
                    var meshSurfaces = item.MeshSurfacesSrv;
                    ctx->VSSetShaderResources(20, 1, &meshSurfaces);
                    ctx->IASetIndexBuffer(item.MeshIndices, DXGI_FORMAT_R32_UINT, 0);
                    ctx->DrawIndexed(item.MeshIndexCount, 0, 0);
                }
                ctx->RSSetViewports(1, &viewport);
                ctx->OMSetRenderTargets(2, furRtvs, ownDepthDsv); // or the map can't be read: D3D nulls a view of a bound target
                ctx->GenerateMips(surfaceLightSrv);
                var lightSrv = surfaceLightSrv;
                ctx->PSSetShaderResources(11, 1, &lightSrv);
                ctx->VSSetShaderResources(11, 1, &lightSrv); // VSMain reads the light at strand roots
                var sampler = linearClamp;
                ctx->PSSetSamplers(0, 1, &sampler);
                ctx->VSSetSamplers(0, 1, &sampler);
            }
            else ctx->OMSetRenderTargets(2, furRtvs, ownDepthDsv);
        }
        var psSrvs = stackalloc ID3D11ShaderResourceView*[2] { sceneDepth, sceneLumaSrv };
        ctx->PSSetShaderResources(3, 2, psSrvs);

        // Culling: each item's arguments start at 8 vertices and no instances; CSBucketOffsets stores how many it keeps.
        EnsureDrawArgs(draws.Count);
        var args = stackalloc uint[draws.Count * 4];
        for (var k = 0; k < draws.Count; k++) (args[k * 4], args[k * 4 + 1], args[k * 4 + 2], args[k * 4 + 3]) = (8, 0, 0, 0);
        var argsBox = new D3D11_BOX { right = (uint)(draws.Count * 16), bottom = 1, back = 1 };
        ctx->UpdateSubresource((ID3D11Resource*)drawArgs, 0, &argsBox, args, 0, 0);
        fc.CullFlags = cfg.CullHiddenStrands ? 1u : 0u;
        if (fc.CullFlags != 0 && fc.UseSceneDepth != 0 && BuildHiZ(ctx, sceneDepth, depthSize))
            (fc.CullFlags, fc.HiZWidth, fc.HiZHeight, fc.HiZLevels) = (3u, hiZW, hiZH, (uint)hiZLevelUavs.Length);
        long strandInstances = 0;

        ctx->CSSetShader(csStrandMatrices, null, 0);
        var csSrvs = stackalloc ID3D11ShaderResourceView*[3];
        var csUavs = stackalloc ID3D11UnorderedAccessView*[6];
        var zeros = stackalloc uint[4];
        var vsSrvs = stackalloc ID3D11ShaderResourceView*[7];
        for (var d = 0; d < draws.Count; d++)
        {
            var draw = draws[d];
            var item = draw.Buffers;
            var drawn = (uint)(item.StrandCount * Math.Clamp(draw.Density, 0f, 1f));
            if (drawn == 0) continue;
            (fc.DrawnStrands, fc.ArgsOffset) = (drawn, (uint)(d * 16));
            strandInstances += drawn * fc.ChildCount;
            BindMaterials(ctx, draw, ref fc);
            // Assets hold 10% of the reference strand density (Interpolate amount 0.10); thicken coverage by the shortfall so
            // silhouettes stay as solid as the reference instead of showing the background between strands.
            fc.CoverageScale = Math.Max(1f, item.StrandCount / (AssetDensityFraction * drawn * fc.ChildCount));
            Upload(ctx, (ID3D11Resource*)constants, &fc, sizeof(FrameConstants));
            UploadBones(ctx, draw);

            csSrvs[0] = item.PointsSrv;
            csSrvs[1] = item.SkinsSrv;
            csSrvs[2] = bonesSrv;
            ctx->CSSetShaderResources(0, 3, csSrvs);
            csUavs[0] = item.StrandMatricesUav;
            csUavs[1] = drawArgsUav;
            csUavs[2] = item.VisibleStrandsUav;
            csUavs[3] = null;
            csUavs[4] = item.StrandKeysUav;
            csUavs[5] = bucketsUav;
            ctx->ClearUnorderedAccessViewUint(bucketsUav, zeros);
            ctx->CSSetUnorderedAccessViews(0, 6, csUavs, null);
            ctx->Dispatch((drawn + 63) / 64, 1, 1);
            ctx->CSSetShader(csBucketOffsets, null, 0);
            ctx->Dispatch(1, 1, 1);
            ctx->CSSetShader(csScatter, null, 0);
            ctx->Dispatch((drawn + 63) / 64, 1, 1);
            ctx->CSSetShader(csStrandMatrices, null, 0);
            csUavs[0] = csUavs[1] = csUavs[2] = csUavs[4] = csUavs[5] = null;
            ctx->CSSetUnorderedAccessViews(0, 6, csUavs, null); // or the VS can't read them and the draw can't take its arguments

            vsSrvs[0] = item.PointsSrv;
            vsSrvs[1] = null;
            vsSrvs[2] = bonesSrv;
            vsSrvs[3] = sceneDepth;   // VSMain: the light at strand roots
            vsSrvs[4] = sceneLumaSrv;
            vsSrvs[5] = item.MeshVerticesSrv;
            vsSrvs[6] = item.MeshSkinsSrv;
            ctx->VSSetShaderResources(0, 7, vsSrvs);
            var matrices = item.StrandMatricesSrv;
            ctx->VSSetShaderResources(9, 1, &matrices);
            var visible = item.VisibleStrandsSrv;
            ctx->VSSetShaderResources(17, 1, &visible);
            var meshSurfaces = item.MeshSurfacesSrv;
            ctx->VSSetShaderResources(20, 1, &meshSurfaces);
            // Undercoat first so strands depth-test against it; left out when checking strand placement.
            if (item.MeshIndices != null && !cfg.DebugSolid)
            {
                ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                ctx->IASetIndexBuffer(item.MeshIndices, DXGI_FORMAT_R32_UINT, 0);
                ctx->VSSetShader(vsUnder, null, 0);
                ctx->PSSetShader(psUnder, null, 0);
                ctx->DrawIndexed(item.MeshIndexCount, 0, 0);
            }
            ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            ctx->VSSetShader(vs, null, 0);
            ctx->PSSetShader(ps, null, 0);
            ctx->DrawInstancedIndirect(drawArgs, (uint)(d * 16));
        }
        foreach (var draw in draws) draw.Buffers.LastSkin = draw.Skin;
        ctx->CopyResource((ID3D11Resource*)argsStaging[querySlot], (ID3D11Resource*)drawArgs);
        (argsItems[querySlot], argsTotal[querySlot]) = (draws.Count, strandInstances);

        // Resolve the fur layer, blend it with the last frames' (PSTemporal) and that over the frame.
        ctx->ResolveSubresource((ID3D11Resource*)furResolved, 0, (ID3D11Resource*)furColor, 0, DXGI_FORMAT_R8G8B8A8_UNORM);
        var historyRtv = historyFlip ? historyRtv1 : historyRtv0;
        var historySrv = historyFlip ? historySrv1 : historySrv0;
        var lastSrv = historyFlip ? historySrv0 : historySrv1;
        historyFlip = !historyFlip;
        historyTime = Stopwatch.GetTimestamp();
        lastViewProj = fc.ViewProj;
        ctx->OMSetRenderTargets(1, &historyRtv, null);
        ctx->OMSetBlendState(blend, blendFactor, 0xFFFFFFFF);
        ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vsComposite, null, 0);
        ctx->PSSetShader(psTemporal, null, 0);
        var layer = furResolvedSrv;
        ctx->PSSetShaderResources(7, 1, &layer);
        ctx->PSSetShaderResources(30, 1, &lastSrv);
        var ownDepthView = ownDepthSrv;
        ctx->PSSetShaderResources(8, 1, &ownDepthView);
        var motionView = motionSrv;
        ctx->PSSetShaderResources(31, 1, &motionView);
        var clampSampler = linearClamp;
        ctx->PSSetSamplers(0, 1, &clampSampler);
        ctx->Draw(3, 0);
        var target = rtv;
        ctx->OMSetRenderTargets(1, &target, null);
        ctx->OMSetBlendState(compositeBlend, blendFactor, 0xFFFFFFFF);
        ctx->PSSetShader(psComposite, null, 0);
        ctx->PSSetShaderResources(7, 1, &historySrv); // PSDepth below reads it too
        ctx->Draw(3, 0);

        // Fur depth into the game's depth buffer, for depth-based post effects (ReShade). Only when it maps 1:1 to the frame.
        if (sceneDepth != null && (uint)depthSize.X == bbDesc.Width && (uint)depthSize.Y == bbDesc.Height && EnsureSceneDepthDsv(sceneDepth))
        {
            ID3D11ShaderResourceView* none = null;
            ctx->PSSetShaderResources(3, 1, &none); // the scene depth can't be read while it is the target
            ctx->VSSetShaderResources(3, 1, &none);
            ctx->OMSetRenderTargets(0, null, sceneDepthDsv);
            ctx->OMSetDepthStencilState(depthWriteState, 0);
            ctx->PSSetShader(psDepth, null, 0);
            var furDepth = ownDepthSrv;
            ctx->PSSetShaderResources(8, 1, &furDepth);
            ctx->Draw(3, 0);
        }
        ctx->End((ID3D11Asynchronous*)statsQueries[querySlot]);
        ctx->End((ID3D11Asynchronous*)endQueries[querySlot]);
        ctx->End((ID3D11Asynchronous*)disjointQueries[querySlot]);
        queryIssued[querySlot] = true;
        if (cfg.DebugSolid)
        {
            // Shader-independent marker: a magenta box at the top-left proves the back buffer we draw into is what gets presented.
            ID3D11DeviceContext1* ctx1;
            if (ctx->QueryInterface(TerraFX.Interop.Windows.Windows.__uuidof<ID3D11DeviceContext1>(), (void**)&ctx1).SUCCEEDED)
            {
                var color = stackalloc float[4] { 1, 0, 1, 1 };
                var rect = new RECT { left = 50, top = 50, right = 250, bottom = 250 };
                ctx1->ClearView((ID3D11View*)rtv, color, &rect, 1);
                ctx1->Release();
            }
        }
        // A view of the swap chain's buffer held past the frame would make the game's ResizeBuffers fail.
        Release(ref rtv);
    }

    private static bool IsReverseZ(Matrix4x4 proj)
    {
        static float Ndc(Matrix4x4 p, float z)
        {
            var c = Vector4.Transform(new Vector4(0, 0, z, 1), p);
            return c.Z / c.W;
        }
        // Probe in front of the camera along whichever view axis the projection treats as forward.
        var forward = proj.M34 < 0 ? -1f : 1f;
        return Ndc(proj, forward * 1f) > Ndc(proj, forward * 100f);
    }

    private void EnsureDepthState(bool reverse)
    {
        if (depthState != null && depthStateReverse == reverse) return;
        Release(ref depthState);
        var desc = new D3D11_DEPTH_STENCIL_DESC
        {
            DepthEnable = true,
            DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL,
            DepthFunc = reverse ? D3D11_COMPARISON_GREATER : D3D11_COMPARISON_LESS,
        };
        fixed (ID3D11DepthStencilState** out_ = &depthState) Check(device->CreateDepthStencilState(&desc, out_), "CreateDepthStencilState");
        Release(ref depthWriteState);
        desc.DepthFunc = reverse ? D3D11_COMPARISON_GREATER_EQUAL : D3D11_COMPARISON_LESS_EQUAL;
        fixed (ID3D11DepthStencilState** out_ = &depthWriteState) Check(device->CreateDepthStencilState(&desc, out_), "CreateDepthStencilState(write)");
        depthStateReverse = reverse;
    }

    private bool EnsureSceneDepthDsv(ID3D11ShaderResourceView* sceneDepth)
    {
        ID3D11Resource* resource;
        sceneDepth->GetResource(&resource);
        try
        {
            if (sceneDepthDsv != null && sceneDepthDsvResource == (nint)resource) return true;
            Release(ref sceneDepthDsv);
            sceneDepthDsvResource = 0;
            D3D11_TEXTURE2D_DESC desc;
            ((ID3D11Texture2D*)resource)->GetDesc(&desc);
            var dsvDesc = new D3D11_DEPTH_STENCIL_VIEW_DESC
            {
                Format = desc.Format switch
                {
                    DXGI_FORMAT_R24G8_TYPELESS => DXGI_FORMAT_D24_UNORM_S8_UINT,
                    DXGI_FORMAT_R32_TYPELESS => DXGI_FORMAT_D32_FLOAT,
                    DXGI_FORMAT_R32G8X24_TYPELESS => DXGI_FORMAT_D32_FLOAT_S8X24_UINT,
                    _ => DXGI_FORMAT_UNKNOWN,
                },
                ViewDimension = D3D11_DSV_DIMENSION.D3D11_DSV_DIMENSION_TEXTURE2D,
            };
            if (dsvDesc.Format == DXGI_FORMAT_UNKNOWN || desc.SampleDesc.Count != 1) return false;
            fixed (ID3D11DepthStencilView** out_ = &sceneDepthDsv)
                if (device->CreateDepthStencilView(resource, &dsvDesc, out_).FAILED) return false;
            sceneDepthDsvResource = (nint)resource;
            return true;
        }
        finally
        {
            resource->Release();
        }
    }

    private ID3D11Texture2D* readback;

    private bool EnsureSceneCopy(D3D11_TEXTURE2D_DESC bb)
    {
        if (sceneCopy != null)
        {
            D3D11_TEXTURE2D_DESC current;
            sceneCopy->GetDesc(&current);
            if (current.Width == bb.Width && current.Height == bb.Height && current.Format == bb.Format) return true;
            ReleaseSceneCopy();
        }
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = bb.Width, Height = bb.Height, MipLevels = 1, ArraySize = 1, Format = bb.Format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_SHADER_RESOURCE,
        };
        fixed (ID3D11Texture2D** out_ = &sceneCopy)
            if (device->CreateTexture2D(&desc, null, out_).FAILED) return false; // e.g. typeless back buffer format
        fixed (ID3D11ShaderResourceView** out_ = &sceneCopySrv)
            if (device->CreateShaderResourceView((ID3D11Resource*)sceneCopy, null, out_).FAILED) { Release(ref sceneCopy); return false; }
        desc.Format = DXGI_FORMAT_R32G32_FLOAT;
        desc.BindFlags = (uint)(D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE);
        bool created;
        fixed (ID3D11Texture2D** out_ = &sceneLuma) created = device->CreateTexture2D(&desc, null, out_).SUCCEEDED;
        if (created) fixed (ID3D11RenderTargetView** out_ = &sceneLumaRtv) created = device->CreateRenderTargetView((ID3D11Resource*)sceneLuma, null, out_).SUCCEEDED;
        if (created) fixed (ID3D11ShaderResourceView** out_ = &sceneLumaSrv) created = device->CreateShaderResourceView((ID3D11Resource*)sceneLuma, null, out_).SUCCEEDED;
        desc.Width = Math.Max(bb.Width / SurfaceLightDivisor, 1);
        desc.Height = Math.Max(bb.Height / SurfaceLightDivisor, 1);
        desc.MipLevels = 0;
        desc.Format = DXGI_FORMAT_R16G16_FLOAT;
        desc.MiscFlags = (uint)D3D11_RESOURCE_MISC_GENERATE_MIPS;
        if (created) fixed (ID3D11Texture2D** out_ = &surfaceLight) created = device->CreateTexture2D(&desc, null, out_).SUCCEEDED;
        if (created) fixed (ID3D11RenderTargetView** out_ = &surfaceLightRtv) created = device->CreateRenderTargetView((ID3D11Resource*)surfaceLight, null, out_).SUCCEEDED;
        if (created) fixed (ID3D11ShaderResourceView** out_ = &surfaceLightSrv) created = device->CreateShaderResourceView((ID3D11Resource*)surfaceLight, null, out_).SUCCEEDED;
        if (!created) ReleaseSceneCopy();
        return created;
    }

    private void ReleaseSceneCopy()
    {
        Release(ref sceneCopySrv); Release(ref sceneCopy);
        Release(ref sceneLumaSrv); Release(ref sceneLumaRtv); Release(ref sceneLuma);
        Release(ref surfaceLightSrv); Release(ref surfaceLightRtv); Release(ref surfaceLight);
    }

    /// <summary>Self-test: counts pure magenta (debug-solid) pixels in the back buffer, sampling every 4th pixel.</summary>
    public int CountMagenta(ID3D11DeviceContext* ctx, ID3D11Texture2D* backBuffer)
    {
        D3D11_TEXTURE2D_DESC bb;
        backBuffer->GetDesc(&bb);
        if (readback == null)
        {
            var desc = bb;
            desc.Usage = D3D11_USAGE_STAGING;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ;
            desc.MiscFlags = 0;
            fixed (ID3D11Texture2D** out_ = &readback) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(readback)");
        }
        ctx->CopyResource((ID3D11Resource*)readback, (ID3D11Resource*)backBuffer);
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(ctx->Map((ID3D11Resource*)readback, 0, D3D11_MAP_READ, 0, &mapped), "Map(readback)");
        var count = 0;
        for (uint y = 0; y < bb.Height; y += 4)
        {
            var row = (byte*)mapped.pData + y * mapped.RowPitch;
            for (uint x = 0; x < bb.Width; x += 4)
            {
                var p = row + x * 4; // RGBA8 / BGRA8: magenta is 255,0,255 either way
                if (p[0] == 255 && p[1] == 0 && p[2] == 255) count++;
            }
        }
        ctx->Unmap((ID3D11Resource*)readback, 0);
        return count;
    }

    private void EnsureTargets(ID3D11Texture2D* backBuffer, D3D11_TEXTURE2D_DESC bbDesc)
    {
        Release(ref rtv); // left over if the last draw threw
        fixed (ID3D11RenderTargetView** out_ = &rtv) Check(device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, out_), "CreateRenderTargetView");
        if (ownDepth != null && ownDepthW == bbDesc.Width && ownDepthH == bbDesc.Height) return;
        Release(ref ownDepthSrv); Release(ref ownDepthDsv); Release(ref ownDepth);
        Release(ref furColorRtv); Release(ref furColor);
        Release(ref furResolvedSrv); Release(ref furResolved);
        Release(ref motionSrv); Release(ref motionRtv); Release(ref motion);
        ReleaseHistory();
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = bbDesc.Width,
            Height = bbDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_R32_TYPELESS,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = sampleCount },
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_DEPTH_STENCIL | D3D11_BIND_SHADER_RESOURCE),
        };
        fixed (ID3D11Texture2D** out_ = &ownDepth) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(depth)");
        var ownDsvDesc = new D3D11_DEPTH_STENCIL_VIEW_DESC { Format = DXGI_FORMAT_D32_FLOAT, ViewDimension = D3D11_DSV_DIMENSION.D3D11_DSV_DIMENSION_TEXTURE2DMS };
        fixed (ID3D11DepthStencilView** out_ = &ownDepthDsv) Check(device->CreateDepthStencilView((ID3D11Resource*)ownDepth, &ownDsvDesc, out_), "CreateDepthStencilView");
        var ownSrvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = DXGI_FORMAT_R32_FLOAT, ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2DMS };
        fixed (ID3D11ShaderResourceView** out_ = &ownDepthSrv) Check(device->CreateShaderResourceView((ID3D11Resource*)ownDepth, &ownSrvDesc, out_), "CreateShaderResourceView(depth)");
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.BindFlags = (uint)D3D11_BIND_RENDER_TARGET;
        fixed (ID3D11Texture2D** out_ = &furColor) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(fur layer)");
        fixed (ID3D11RenderTargetView** out_ = &furColorRtv) Check(device->CreateRenderTargetView((ID3D11Resource*)furColor, null, out_), "CreateRenderTargetView(fur layer)");
        desc.Format = DXGI_FORMAT_R16G16_FLOAT;
        desc.BindFlags = (uint)(D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE);
        fixed (ID3D11Texture2D** out_ = &motion) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(motion)");
        fixed (ID3D11RenderTargetView** out_ = &motionRtv) Check(device->CreateRenderTargetView((ID3D11Resource*)motion, null, out_), "CreateRenderTargetView(motion)");
        fixed (ID3D11ShaderResourceView** out_ = &motionSrv) Check(device->CreateShaderResourceView((ID3D11Resource*)motion, null, out_), "CreateShaderResourceView(motion)");
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BindFlags = (uint)D3D11_BIND_SHADER_RESOURCE;
        fixed (ID3D11Texture2D** out_ = &furResolved) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(fur resolve)");
        fixed (ID3D11ShaderResourceView** out_ = &furResolvedSrv) Check(device->CreateShaderResourceView((ID3D11Resource*)furResolved, null, out_), "CreateShaderResourceView(fur resolve)");
        desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT; // in 8 bits a 0.2 blend stops short of the frame: its steps round away within 2.5/255
        desc.BindFlags = (uint)(D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE);
        fixed (ID3D11Texture2D** out_ = &history0) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(history)");
        fixed (ID3D11Texture2D** out_ = &history1) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(history)");
        fixed (ID3D11RenderTargetView** out_ = &historyRtv0) Check(device->CreateRenderTargetView((ID3D11Resource*)history0, null, out_), "CreateRenderTargetView(history)");
        fixed (ID3D11RenderTargetView** out_ = &historyRtv1) Check(device->CreateRenderTargetView((ID3D11Resource*)history1, null, out_), "CreateRenderTargetView(history)");
        fixed (ID3D11ShaderResourceView** out_ = &historySrv0) Check(device->CreateShaderResourceView((ID3D11Resource*)history0, null, out_), "CreateShaderResourceView(history)");
        fixed (ID3D11ShaderResourceView** out_ = &historySrv1) Check(device->CreateShaderResourceView((ID3D11Resource*)history1, null, out_), "CreateShaderResourceView(history)");
        ownDepthW = bbDesc.Width;
        ownDepthH = bbDesc.Height;
    }

    private void ReleaseHistory()
    {
        Release(ref historySrv0); Release(ref historySrv1); Release(ref historyRtv0); Release(ref historyRtv1);
        Release(ref history0); Release(ref history1);
        historyTime = 0;
    }

    // An item's fur material textures for SurfaceColor (CS and PS), and which of them are bound (TableMask; constants not uploaded).
    private static void BindMaterials(ID3D11DeviceContext* ctx, FurDraw draw, ref FrameConstants fc)
    {
        var colorSrvs = stackalloc ID3D11ShaderResourceView*[1 + MaxMaterials]; // t12: strand surfaces, t13..: colour tables
        var mapSrvs = stackalloc ID3D11ShaderResourceView*[2 * MaxMaterials];   // t21..: index maps, then diffuse maps
        fc.TableMask = 0;
        colorSrvs[0] = draw.Buffers.StrandSurfacesSrv;
        for (var k = 0; k < MaterialTextures; k++)
        {
            var srv = (ID3D11ShaderResourceView*)draw.Textures[k];
            if (k < MaxMaterials) colorSrvs[1 + k] = srv;
            else mapSrvs[k - MaxMaterials] = srv;
            if (srv != null) fc.TableMask |= 1u << k;
        }
        ctx->CSSetShaderResources(12, 1 + MaxMaterials, colorSrvs);
        ctx->CSSetShaderResources(21, 2 * MaxMaterials, mapSrvs);
        ctx->PSSetShaderResources(13, MaxMaterials, colorSrvs + 1);
        ctx->PSSetShaderResources(21, 2 * MaxMaterials, mapSrvs);
    }

    // A GPU-only structured buffer of 'count' uints.
    private void CreateUavBuffer(int count, ref ID3D11Buffer* buffer, ref ID3D11UnorderedAccessView* uav)
    {
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = checked((uint)(count * sizeof(uint))),
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_UNORDERED_ACCESS,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = sizeof(uint),
        };
        fixed (ID3D11Buffer** out_ = &buffer) Check(device->CreateBuffer(&desc, null, out_), "CreateBuffer(uav)");
        fixed (ID3D11UnorderedAccessView** out_ = &uav) Check(device->CreateUnorderedAccessView((ID3D11Resource*)buffer, null, out_), "CreateUnorderedAccessView");
    }

    private void CreateStructured(void* data, int count, int stride, bool dynamic, ref ID3D11Buffer* buffer, ref ID3D11ShaderResourceView* srv)
    {
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = checked((uint)(count * stride)),
            Usage = dynamic ? D3D11_USAGE_DYNAMIC : D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = dynamic ? (uint)D3D11_CPU_ACCESS_WRITE : 0,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)stride,
        };
        var init = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
        fixed (ID3D11Buffer** out_ = &buffer) Check(device->CreateBuffer(&desc, data == null ? null : &init, out_), "CreateBuffer(structured)");
        var srvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
        };
        srvDesc.Buffer.FirstElement = 0;
        srvDesc.Buffer.NumElements = (uint)count;
        fixed (ID3D11ShaderResourceView** out_ = &srv) Check(device->CreateShaderResourceView((ID3D11Resource*)buffer, &srvDesc, out_), "CreateShaderResourceView");
    }

    // The item's skin palette, then from MaxBones on last frame's (its own again on its first frame), for the motion.
    private void UploadBones(ID3D11DeviceContext* ctx, FurDraw draw)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        if (ctx->Map((ID3D11Resource*)bones, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped).FAILED) return;
        var last = draw.Buffers.LastSkin is { } l && l.Length == draw.Skin.Length ? l : draw.Skin;
        var bytes = Math.Min(draw.Skin.Length, MaxBones) * sizeof(Matrix4x4);
        fixed (Matrix4x4* m = draw.Skin) Buffer.MemoryCopy(m, mapped.pData, bytes, bytes);
        fixed (Matrix4x4* m = last) Buffer.MemoryCopy(m, (byte*)mapped.pData + MaxBones * sizeof(Matrix4x4), bytes, bytes);
        ctx->Unmap((ID3D11Resource*)bones, 0);
    }

    private static void Upload(ID3D11DeviceContext* ctx, ID3D11Resource* resource, void* data, int bytes)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        if (ctx->Map(resource, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped).FAILED) return;
        Buffer.MemoryCopy(data, mapped.pData, bytes, bytes);
        ctx->Unmap(resource, 0);
    }

    private static Blob Compile(byte[] source, string path, string entry, string target)
    {
        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        var nameBytes = Encoding.ASCII.GetBytes(Path.GetFileName(path) + "\0");
        var entryBytes = Encoding.ASCII.GetBytes(entry + "\0");
        var targetBytes = Encoding.ASCII.GetBytes(target + "\0");
        HRESULT hr;
        fixed (byte* src = source)
        fixed (byte* name = nameBytes)
        fixed (byte* e = entryBytes)
        fixed (byte* t = targetBytes)
            hr = DirectX.D3DCompile(src, (nuint)source.Length, (sbyte*)name, null, null, (sbyte*)e, (sbyte*)t,
                D3DCOMPILE.D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
        string message = "";
        if (errors != null)
        {
            message = Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer(), (int)errors->GetBufferSize());
            errors->Release();
        }
        if (hr.FAILED) throw new InvalidOperationException($"Shader compile {entry} failed: {message}");
        return new Blob(code);
    }

    private static void Check(HRESULT hr, string what)
    {
        if (hr.FAILED) throw new InvalidOperationException($"{what} failed: 0x{hr.Value:X8}");
    }

    private static void Release<T>(ref T* p) where T : unmanaged
    {
        if (p != null) ((IUnknown*)p)->Release();
        p = null;
    }

    public void Dispose()
    {
        Release(ref vs); Release(ref ps); Release(ref constants);
        Release(ref vsUnder); Release(ref psUnder); Release(ref vsComposite); Release(ref psComposite); Release(ref psTemporal); Release(ref psDepth);
        Release(ref psSceneLuma); Release(ref psSurfaceLight); Release(ref linearClamp); Release(ref linearWrap); Release(ref csStrandMatrices);
        Release(ref csHiZFirst); Release(ref csHiZDown);
        Release(ref csBucketOffsets); Release(ref csScatter); Release(ref bucketsUav); Release(ref buckets);
        ReleaseHiZ();
        ReleaseDrawArgs();
        Release(ref bonesSrv); Release(ref bones);
        Release(ref blend); Release(ref compositeBlend); Release(ref raster); Release(ref depthState); Release(ref depthWriteState);
        Release(ref ownDepthSrv); Release(ref ownDepthDsv); Release(ref ownDepth); Release(ref sceneDepthDsv);
        Release(ref furColorRtv); Release(ref furColor);
        Release(ref furResolvedSrv); Release(ref furResolved);
        Release(ref motionSrv); Release(ref motionRtv); Release(ref motion);
        ReleaseHistory();
        Release(ref rtv);
        Release(ref readback);
        ReleaseSceneCopy();
        for (var i = 0; i < QueryFrames; i++)
        {
            Release(ref disjointQueries[i]); Release(ref startQueries[i]); Release(ref endQueries[i]); Release(ref statsQueries[i]);
        }
        device->Release();
    }

    private readonly struct Blob(ID3DBlob* blob) : IDisposable
    {
        public void* Pointer => blob->GetBufferPointer();
        public nuint Size => blob->GetBufferSize();
        public void Dispose() => blob->Release();
    }

    /// <summary>Captures and restores the pipeline state this renderer touches so Dalamud/game rendering is unaffected.</summary>
    private readonly struct SavedState : IDisposable
    {
        private readonly ID3D11DeviceContext* ctx;
        private readonly ID3D11RenderTargetView* rtv;
        private readonly ID3D11DepthStencilView* dsv;
        private readonly ID3D11RasterizerState* raster;
        private readonly ID3D11BlendState* blend;
        private readonly ID3D11DepthStencilState* depth;
        private readonly uint stencilRef, sampleMask, viewportCount;
        private readonly D3D11_VIEWPORT viewport;
        private readonly Vector4 blendFactor;
        private readonly ID3D11InputLayout* layout;
        private readonly D3D_PRIMITIVE_TOPOLOGY topology;
        private readonly ID3D11VertexShader* vs;
        private readonly ID3D11PixelShader* ps;
        private readonly ID3D11Buffer* vsCb, psCb, csCb;
        private readonly ID3D11ShaderResourceView*[] vsSrvs = new ID3D11ShaderResourceView*[30]; // t0..t29
        private readonly ID3D11ShaderResourceView*[] psSrvs = new ID3D11ShaderResourceView*[29]; // t3..t31
        private readonly ID3D11SamplerState*[] psSamplers = new ID3D11SamplerState*[2]; // s0..s1
        private readonly ID3D11SamplerState* csSampler; // s1
        private readonly ID3D11SamplerState* vsSampler; // s0
        private readonly ID3D11ComputeShader* cs;
        private readonly ID3D11ShaderResourceView*[] csSrvs = new ID3D11ShaderResourceView*[29]; // t0..t28
        private readonly ID3D11UnorderedAccessView*[] csUavs = new ID3D11UnorderedAccessView*[6]; // u0..u5
        private readonly ID3D11Buffer* indexBuffer;
        private readonly DXGI_FORMAT indexFormat;
        private readonly uint indexOffset;

        public SavedState(ID3D11DeviceContext* ctx)
        {
            this.ctx = ctx;
            ID3D11RenderTargetView* r; ID3D11DepthStencilView* d;
            ctx->OMGetRenderTargets(1, &r, &d); rtv = r; dsv = d;
            ID3D11RasterizerState* rs; ctx->RSGetState(&rs); raster = rs;
            ID3D11BlendState* bs; Vector4 bf; uint sm;
            ctx->OMGetBlendState(&bs, (float*)&bf, &sm); blend = bs; blendFactor = bf; sampleMask = sm;
            ID3D11DepthStencilState* ds; uint sr; ctx->OMGetDepthStencilState(&ds, &sr); depth = ds; stencilRef = sr;
            uint vc = 1; D3D11_VIEWPORT vp; ctx->RSGetViewports(&vc, &vp); viewportCount = vc; viewport = vp;
            ID3D11InputLayout* il; ctx->IAGetInputLayout(&il); layout = il;
            D3D_PRIMITIVE_TOPOLOGY tp; ctx->IAGetPrimitiveTopology(&tp); topology = tp;
            ID3D11VertexShader* v; ctx->VSGetShader(&v, null, null); vs = v;
            ID3D11PixelShader* p; ctx->PSGetShader(&p, null, null); ps = p;
            ID3D11Buffer* c0; ctx->VSGetConstantBuffers(0, 1, &c0); vsCb = c0;
            ID3D11Buffer* c1; ctx->PSGetConstantBuffers(0, 1, &c1); psCb = c1;
            ID3D11Buffer* c2; ctx->CSGetConstantBuffers(0, 1, &c2); csCb = c2;
            fixed (ID3D11ShaderResourceView** vsOut = vsSrvs) ctx->VSGetShaderResources(0, (uint)vsSrvs.Length, vsOut);
            fixed (ID3D11ShaderResourceView** psOut = psSrvs) ctx->PSGetShaderResources(3, (uint)psSrvs.Length, psOut);
            fixed (ID3D11SamplerState** ss = psSamplers) ctx->PSGetSamplers(0, (uint)psSamplers.Length, ss);
            ID3D11SamplerState* css; ctx->CSGetSamplers(1, 1, &css); csSampler = css;
            ID3D11SamplerState* vss; ctx->VSGetSamplers(0, 1, &vss); vsSampler = vss;
            ID3D11ComputeShader* c; ctx->CSGetShader(&c, null, null); cs = c;
            fixed (ID3D11ShaderResourceView** csOut = csSrvs) ctx->CSGetShaderResources(0, (uint)csSrvs.Length, csOut);
            fixed (ID3D11UnorderedAccessView** uOut = csUavs) ctx->CSGetUnorderedAccessViews(0, (uint)csUavs.Length, uOut);
            ID3D11Buffer* ib; DXGI_FORMAT f; uint o; ctx->IAGetIndexBuffer(&ib, &f, &o); indexBuffer = ib; indexFormat = f; indexOffset = o;
        }

        public void Dispose()
        {
            var r = rtv;
            ctx->OMSetRenderTargets(1, &r, dsv);
            ctx->RSSetState(raster);
            var bf = blendFactor;
            ctx->OMSetBlendState(blend, (float*)&bf, sampleMask);
            ctx->OMSetDepthStencilState(depth, stencilRef);
            if (viewportCount > 0) { var vp = viewport; ctx->RSSetViewports(1, &vp); }
            ctx->IASetInputLayout(layout);
            ctx->IASetPrimitiveTopology(topology);
            ctx->VSSetShader(vs, null, 0);
            ctx->PSSetShader(ps, null, 0);
            var c0 = vsCb; ctx->VSSetConstantBuffers(0, 1, &c0);
            var c1 = psCb; ctx->PSSetConstantBuffers(0, 1, &c1);
            var c2 = csCb; ctx->CSSetConstantBuffers(0, 1, &c2);
            fixed (ID3D11ShaderResourceView** v = vsSrvs) ctx->VSSetShaderResources(0, (uint)vsSrvs.Length, v);
            fixed (ID3D11ShaderResourceView** p = psSrvs) ctx->PSSetShaderResources(3, (uint)psSrvs.Length, p);
            fixed (ID3D11SamplerState** ss = psSamplers) ctx->PSSetSamplers(0, (uint)psSamplers.Length, ss);
            var css = csSampler; ctx->CSSetSamplers(1, 1, &css);
            var vss = vsSampler; ctx->VSSetSamplers(0, 1, &vss);
            ctx->CSSetShader(cs, null, 0);
            fixed (ID3D11ShaderResourceView** c = csSrvs) ctx->CSSetShaderResources(0, (uint)csSrvs.Length, c);
            var keepCounts = stackalloc uint[6] { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue }; // leave append/counter UAVs' hidden counts as they were
            fixed (ID3D11UnorderedAccessView** u = csUavs) ctx->CSSetUnorderedAccessViews(0, (uint)csUavs.Length, u, keepCounts);
            ctx->IASetIndexBuffer(indexBuffer, indexFormat, indexOffset);
            ReleaseAll();
        }

        private void ReleaseAll()
        {
            Rel(rtv); Rel(dsv); Rel(raster); Rel(blend); Rel(depth); Rel(layout); Rel(vs); Rel(ps);
            Rel(vsCb); Rel(psCb); Rel(csCb); Rel(indexBuffer);
            for (var i = 0; i < vsSrvs.Length; i++) Rel(vsSrvs[i]);
            for (var i = 0; i < psSrvs.Length; i++) Rel(psSrvs[i]);
            Rel(cs); Rel(csSampler); Rel(vsSampler);
            for (var i = 0; i < psSamplers.Length; i++) Rel(psSamplers[i]);
            for (var i = 0; i < csSrvs.Length; i++) Rel(csSrvs[i]);
            for (var i = 0; i < csUavs.Length; i++) Rel(csUavs[i]);
        }

        private static void Rel(void* p)
        {
            if (p != null) ((IUnknown*)p)->Release();
        }
    }
}
