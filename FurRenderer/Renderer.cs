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
    public Vector3 UndercoatColor; public float Padding;
}

/// <summary>
/// Draws a loaded <see cref="FurAsset"/> (undercoat, then strands) into a multisampled fur layer and blends the resolved layer
/// over the swap-chain back buffer.
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
    private ID3D11PixelShader* psDepth;
    private ID3D11PixelShader* psSceneLuma;
    private ID3D11ComputeShader* csStrandMatrices;
    private ID3D11Buffer* constants;
    private ID3D11Buffer* bones;
    private ID3D11ShaderResourceView* bonesSrv;
    private ID3D11Buffer* points;
    private ID3D11ShaderResourceView* pointsSrv;
    private ID3D11Buffer* skins;
    private ID3D11ShaderResourceView* skinsSrv;
    private ID3D11Buffer* meshVertices;
    private ID3D11ShaderResourceView* meshVerticesSrv;
    private ID3D11Buffer* meshSkins;
    private ID3D11ShaderResourceView* meshSkinsSrv;
    private ID3D11Buffer* meshIndices;
    private ID3D11Buffer* strandMatrices;
    private ID3D11ShaderResourceView* strandMatricesSrv;
    private ID3D11UnorderedAccessView* strandMatricesUav;
    private ID3D11BlendState* blend;
    private ID3D11BlendState* compositeBlend;
    private ID3D11RasterizerState* raster;
    private ID3D11DepthStencilState* depthState;
    private ID3D11DepthStencilState* depthWriteState;
    private bool depthStateReverse;
    // Fur layer: multisampled colour (premultiplied once resolved) and depth, and its single-sample resolve.
    private readonly uint sampleCount;
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
    private uint ownDepthW, ownDepthH;
    private ID3D11Texture2D* rtvTarget;
    // Second slot: the game alternates between drawing frames into the back buffer and into ToneAdjustSource.
    private ID3D11RenderTargetView* rtvOther;
    private ID3D11Texture2D* rtvOtherTarget;
    private ID3D11RenderTargetView* rtv;
    private uint frameIndex;
    private ID3D11Texture2D* sceneCopy;
    private ID3D11ShaderResourceView* sceneCopySrv;
    private ID3D11Texture2D* sceneLuma;
    private ID3D11RenderTargetView* sceneLumaRtv;
    private ID3D11ShaderResourceView* sceneLumaSrv;

    public const int MaxBones = 256;
    public FurAsset? Asset { get; private set; }
    private const float AssetDensityFraction = 0.10f; // tools/grow_coat_fur.py AMOUNT used for the shipped assets

    public Renderer(ID3D11Device* device, string shaderPath, byte[] source)
    {
        this.device = device;
        device->AddRef();
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
        using (var blob = Compile(source, shaderPath, "PSDepth", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &psDepth) Check(device->CreatePixelShader(blob.Pointer, blob.Size, null, out_), "CreatePixelShader(depth)");
        using (var blob = Compile(source, shaderPath, "PSSceneLuma", "ps_5_0"))
            fixed (ID3D11PixelShader** out_ = &psSceneLuma) Check(device->CreatePixelShader(blob.Pointer, blob.Size, null, out_), "CreatePixelShader(scene luma)");
        using (var blob = Compile(source, shaderPath, "CSStrandMatrices", "cs_5_0"))
            fixed (ID3D11ComputeShader** out_ = &csStrandMatrices) Check(device->CreateComputeShader(blob.Pointer, blob.Size, null, out_), "CreateComputeShader");
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
        CreateStructured(null, MaxBones * 4, sizeof(Vector4), dynamic: true, ref bones, ref bonesSrv);

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
    }

    public void SetAsset(FurAsset? asset)
    {
        Release(ref pointsSrv); Release(ref points);
        Release(ref skinsSrv); Release(ref skins);
        Release(ref meshVerticesSrv); Release(ref meshVertices);
        Release(ref meshSkinsSrv); Release(ref meshSkins);
        Release(ref meshIndices);
        Release(ref strandMatricesUav); Release(ref strandMatricesSrv); Release(ref strandMatrices);
        Asset = null;
        if (asset == null) return;
        if (asset.BoneNames.Length > MaxBones) throw new InvalidDataException($"Asset uses {asset.BoneNames.Length} bones; max {MaxBones}.");
        fixed (StrandPoint* p = asset.Points)
            CreateStructured(p, asset.Points.Length, sizeof(StrandPoint), dynamic: false, ref points, ref pointsSrv);
        fixed (StrandSkin* p = asset.Skins)
            CreateStructured(p, asset.Skins.Length, sizeof(StrandSkin), dynamic: false, ref skins, ref skinsSrv);
        var matricesDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = checked((uint)(asset.StrandCount * 4 * sizeof(Vector3))), // StrandMatrix: 4 x float3
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS),
            MiscFlags = (uint)D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)(4 * sizeof(Vector3)),
        };
        fixed (ID3D11Buffer** out_ = &strandMatrices) Check(device->CreateBuffer(&matricesDesc, null, out_), "CreateBuffer(strand matrices)");
        fixed (ID3D11ShaderResourceView** out_ = &strandMatricesSrv)
            Check(device->CreateShaderResourceView((ID3D11Resource*)strandMatrices, null, out_), "CreateShaderResourceView(strand matrices)");
        fixed (ID3D11UnorderedAccessView** out_ = &strandMatricesUav)
            Check(device->CreateUnorderedAccessView((ID3D11Resource*)strandMatrices, null, out_), "CreateUnorderedAccessView(strand matrices)");
        if (asset.MeshIndices.Length > 0)
        {
            fixed (MeshVertex* p = asset.MeshVertices)
                CreateStructured(p, asset.MeshVertices.Length, sizeof(MeshVertex), dynamic: false, ref meshVertices, ref meshVerticesSrv);
            fixed (StrandSkin* p = asset.MeshSkins)
                CreateStructured(p, asset.MeshSkins.Length, sizeof(StrandSkin), dynamic: false, ref meshSkins, ref meshSkinsSrv);
            fixed (uint* p = asset.MeshIndices)
            {
                var desc = new D3D11_BUFFER_DESC
                {
                    ByteWidth = checked((uint)(asset.MeshIndices.Length * sizeof(uint))),
                    Usage = D3D11_USAGE_IMMUTABLE,
                    BindFlags = (uint)D3D11_BIND_INDEX_BUFFER,
                };
                var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                fixed (ID3D11Buffer** out_ = &meshIndices) Check(device->CreateBuffer(&desc, &init, out_), "CreateBuffer(indices)");
            }
        }
        Asset = asset;
    }

    /// <summary>Back buffer, depth mode and camera of the last draw (for the debug log).</summary>
    public string LastDrawInfo { get; private set; } = "";

    /// <summary>Draws <paramref name="strandCount"/> strands. Returns false (and draws nothing) if a resource is missing.</summary>
    public bool Draw(ID3D11DeviceContext* ctx, ID3D11Texture2D* backBuffer, ID3D11ShaderResourceView* sceneDepth, Vector2 depthSize,
        ReadOnlySpan<Matrix4x4> skinMatrices, Matrix4x4 view, Matrix4x4 proj, int strandCount, int childCount, Configuration cfg)
    {
        if (Asset == null || strandCount <= 0) return false;
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
            Proj = proj,
            CameraPos = invView.Translation,
            MinPixelWidth = cfg.MinPixelWidth,
            ViewportSize = new Vector2(bbDesc.Width, bbDesc.Height),
            DepthUvScale = cfg.DepthUvScaleOverride > 0 ? new Vector2(cfg.DepthUvScaleOverride) : autoUv,
            LightDir = light,
            LightIntensity = cfg.LightIntensity,
            BaseColor = cfg.BaseColor,
            RootDarken = cfg.RootDarken,
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
            UndercoatColor = cfg.BaseColor * cfg.UndercoatDarken,
            // Assets are baked at 10% of the reference strand density (Interpolate amount 0.10); thicken coverage by the
            // shortfall so silhouettes stay as solid as the reference instead of showing the background between strands.
            CoverageScale = Math.Max(1f, Asset.StrandCount / (AssetDensityFraction * Math.Max(1, Math.Min(strandCount, Asset.StrandCount)) * Math.Clamp(childCount, 1, 16))),
        };
        LastDrawInfo = $"backbuffer {bbDesc.Width}x{bbDesc.Height} {bbDesc.Format}; reverseZ(proj) {reverse}; depthUvScale {fc.DepthUvScale}; sceneDepth {(fc.UseSceneDepth != 0 ? "on" : "off")}";
        Upload(ctx, (ID3D11Resource*)constants, &fc, sizeof(FrameConstants));
        fixed (Matrix4x4* m = skinMatrices)
            Upload(ctx, (ID3D11Resource*)bones, m, Math.Min(skinMatrices.Length, MaxBones) * sizeof(Matrix4x4));

        using var saved = new SavedState(ctx);
        var drawn = (uint)Math.Min(strandCount, Asset.StrandCount);
        ctx->CSSetShader(csStrandMatrices, null, 0);
        var csSrvs = stackalloc ID3D11ShaderResourceView*[2] { skinsSrv, bonesSrv };
        ctx->CSSetShaderResources(1, 2, csSrvs);
        var uav = strandMatricesUav;
        ctx->CSSetUnorderedAccessViews(0, 1, &uav, null);
        ctx->Dispatch((drawn + 63) / 64, 1, 1);
        uav = null;
        ctx->CSSetUnorderedAccessViews(0, 1, &uav, null); // or the VS can't read the matrices
        var transparent = stackalloc float[4];
        ctx->ClearRenderTargetView(furColorRtv, transparent);
        ctx->ClearDepthStencilView(ownDepthDsv, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, reverse ? 0f : 1f, 0);
        var furRtv = furColorRtv;
        ctx->OMSetRenderTargets(1, &furRtv, ownDepthDsv);
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
        if (fc.HasSceneColor != 0)
        {
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
            ctx->OMSetRenderTargets(1, &furRtv, ownDepthDsv);
        }
        var vsSrvs = stackalloc ID3D11ShaderResourceView*[7] { pointsSrv, skinsSrv, bonesSrv, null, null, meshVerticesSrv, meshSkinsSrv };
        ctx->VSSetShaderResources(0, 7, vsSrvs);
        var matrices = strandMatricesSrv;
        ctx->VSSetShaderResources(9, 1, &matrices);
        var psSrvs = stackalloc ID3D11ShaderResourceView*[2] { sceneDepth, sceneLumaSrv };
        ctx->PSSetShaderResources(3, 2, psSrvs);
        // Undercoat first so strands depth-test against it; left out when checking strand placement.
        if (meshIndices != null && !cfg.DebugSolid)
        {
            ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ctx->IASetIndexBuffer(meshIndices, DXGI_FORMAT_R32_UINT, 0);
            ctx->VSSetShader(vsUnder, null, 0);
            ctx->PSSetShader(psUnder, null, 0);
            ctx->DrawIndexed((uint)Asset.MeshIndices.Length, 0, 0);
        }
        ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        ctx->VSSetShader(vs, null, 0);
        ctx->PSSetShader(ps, null, 0);
        ctx->DrawInstanced(8, drawn * fc.ChildCount, 0, 0);

        // Resolve the fur layer and blend it over the frame.
        var target = rtv;
        ctx->OMSetRenderTargets(1, &target, null);
        ctx->ResolveSubresource((ID3D11Resource*)furResolved, 0, (ID3D11Resource*)furColor, 0, DXGI_FORMAT_R8G8B8A8_UNORM);
        ctx->OMSetBlendState(compositeBlend, blendFactor, 0xFFFFFFFF);
        ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vsComposite, null, 0);
        ctx->PSSetShader(psComposite, null, 0);
        var layer = furResolvedSrv;
        ctx->PSSetShaderResources(7, 1, &layer);
        ctx->Draw(3, 0);

        // Fur depth into the game's depth buffer, for depth-based post effects (ReShade). Only when it maps 1:1 to the frame.
        if (sceneDepth != null && (uint)depthSize.X == bbDesc.Width && (uint)depthSize.Y == bbDesc.Height && EnsureSceneDepthDsv(sceneDepth))
        {
            ID3D11ShaderResourceView* none = null;
            ctx->PSSetShaderResources(3, 1, &none); // the scene depth can't be read while it is the target
            ctx->OMSetRenderTargets(0, null, sceneDepthDsv);
            ctx->OMSetDepthStencilState(depthWriteState, 0);
            ctx->PSSetShader(psDepth, null, 0);
            var furDepth = ownDepthSrv;
            ctx->PSSetShaderResources(8, 1, &furDepth);
            ctx->Draw(3, 0);
        }
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
        return true;
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
        if (!created) ReleaseSceneCopy();
        return created;
    }

    private void ReleaseSceneCopy()
    {
        Release(ref sceneCopySrv); Release(ref sceneCopy);
        Release(ref sceneLumaSrv); Release(ref sceneLumaRtv); Release(ref sceneLuma);
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
        if (rtvTarget != backBuffer)
        {
            var view = rtv; rtv = rtvOther; rtvOther = view;
            var target = rtvTarget; rtvTarget = rtvOtherTarget; rtvOtherTarget = target;
        }
        if (rtvTarget != backBuffer)
        {
            Release(ref rtv);
            Release(ref rtvTarget);
            fixed (ID3D11RenderTargetView** out_ = &rtv) Check(device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, out_), "CreateRenderTargetView");
            backBuffer->AddRef();
            rtvTarget = backBuffer;
        }
        if (ownDepth != null && ownDepthW == bbDesc.Width && ownDepthH == bbDesc.Height) return;
        Release(ref ownDepthSrv); Release(ref ownDepthDsv); Release(ref ownDepth);
        Release(ref furColorRtv); Release(ref furColor);
        Release(ref furResolvedSrv); Release(ref furResolved);
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
        desc.SampleDesc.Count = 1;
        desc.BindFlags = (uint)D3D11_BIND_SHADER_RESOURCE;
        fixed (ID3D11Texture2D** out_ = &furResolved) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(fur resolve)");
        fixed (ID3D11ShaderResourceView** out_ = &furResolvedSrv) Check(device->CreateShaderResourceView((ID3D11Resource*)furResolved, null, out_), "CreateShaderResourceView(fur resolve)");
        ownDepthW = bbDesc.Width;
        ownDepthH = bbDesc.Height;
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
        SetAsset(null);
        Release(ref vs); Release(ref ps); Release(ref constants);
        Release(ref vsUnder); Release(ref psUnder); Release(ref vsComposite); Release(ref psComposite); Release(ref psDepth);
        Release(ref psSceneLuma); Release(ref csStrandMatrices);
        Release(ref bonesSrv); Release(ref bones);
        Release(ref blend); Release(ref compositeBlend); Release(ref raster); Release(ref depthState); Release(ref depthWriteState);
        Release(ref ownDepthSrv); Release(ref ownDepthDsv); Release(ref ownDepth); Release(ref sceneDepthDsv);
        Release(ref furColorRtv); Release(ref furColor);
        Release(ref furResolvedSrv); Release(ref furResolved);
        Release(ref rtv); Release(ref rtvTarget); Release(ref rtvOther); Release(ref rtvOtherTarget);
        Release(ref readback);
        ReleaseSceneCopy();
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
        private readonly ID3D11Buffer* vsCb, psCb;
        private readonly ID3D11ShaderResourceView*[] vsSrvs = new ID3D11ShaderResourceView*[10]; // t0..t9
        private readonly ID3D11ShaderResourceView*[] psSrvs = new ID3D11ShaderResourceView*[8]; // t3..t10
        private readonly ID3D11ComputeShader* cs;
        private readonly ID3D11ShaderResourceView*[] csSrvs = new ID3D11ShaderResourceView*[2]; // t1..t2
        private readonly ID3D11UnorderedAccessView* csUav;
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
            fixed (ID3D11ShaderResourceView** vsOut = vsSrvs) ctx->VSGetShaderResources(0, (uint)vsSrvs.Length, vsOut);
            fixed (ID3D11ShaderResourceView** psOut = psSrvs) ctx->PSGetShaderResources(3, (uint)psSrvs.Length, psOut);
            ID3D11ComputeShader* c; ctx->CSGetShader(&c, null, null); cs = c;
            fixed (ID3D11ShaderResourceView** csOut = csSrvs) ctx->CSGetShaderResources(1, (uint)csSrvs.Length, csOut);
            ID3D11UnorderedAccessView* u; ctx->CSGetUnorderedAccessViews(0, 1, &u); csUav = u;
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
            fixed (ID3D11ShaderResourceView** v = vsSrvs) ctx->VSSetShaderResources(0, (uint)vsSrvs.Length, v);
            fixed (ID3D11ShaderResourceView** p = psSrvs) ctx->PSSetShaderResources(3, (uint)psSrvs.Length, p);
            ctx->CSSetShader(cs, null, 0);
            fixed (ID3D11ShaderResourceView** c = csSrvs) ctx->CSSetShaderResources(1, (uint)csSrvs.Length, c);
            var u = csUav;
            var keepCount = uint.MaxValue; // leave an append/counter UAV's hidden count as it was
            ctx->CSSetUnorderedAccessViews(0, 1, &u, &keepCount);
            ctx->IASetIndexBuffer(indexBuffer, indexFormat, indexOffset);
            ReleaseAll();
        }

        private void ReleaseAll()
        {
            Rel(rtv); Rel(dsv); Rel(raster); Rel(blend); Rel(depth); Rel(layout); Rel(vs); Rel(ps);
            Rel(vsCb); Rel(psCb); Rel(indexBuffer);
            for (var i = 0; i < vsSrvs.Length; i++) Rel(vsSrvs[i]);
            for (var i = 0; i < psSrvs.Length; i++) Rel(psSrvs[i]);
            Rel(cs); Rel(csUav);
            for (var i = 0; i < csSrvs.Length; i++) Rel(csSrvs[i]);
        }

        private static void Rel(void* p)
        {
            if (p != null) ((IUnknown*)p)->Release();
        }
    }
}
