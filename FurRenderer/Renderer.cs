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
    public float Absorption, FacingDepth, CoverageScale, Padding;
}

/// <summary>Draws a loaded <see cref="FurAsset"/> straight into the swap-chain back buffer.</summary>
internal sealed unsafe class Renderer : IDisposable
{
    private readonly ID3D11Device* device;
    private ID3D11VertexShader* vs;
    private ID3D11PixelShader* ps;
    private ID3D11Buffer* constants;
    private ID3D11Buffer* bones;
    private ID3D11ShaderResourceView* bonesSrv;
    private ID3D11Buffer* points;
    private ID3D11ShaderResourceView* pointsSrv;
    private ID3D11Buffer* skins;
    private ID3D11ShaderResourceView* skinsSrv;
    private ID3D11BlendState* blend;
    private ID3D11RasterizerState* raster;
    private ID3D11DepthStencilState* depthState;
    private bool depthStateReverse;
    private ID3D11Texture2D* ownDepth;
    private ID3D11DepthStencilView* ownDepthDsv;
    private uint ownDepthW, ownDepthH;
    private ID3D11Texture2D* rtvTarget;
    // Second slot: the game alternates between drawing frames into the back buffer and into ToneAdjustSource.
    private ID3D11RenderTargetView* rtvOther;
    private ID3D11Texture2D* rtvOtherTarget;
    private ID3D11RenderTargetView* rtv;
    private uint frameIndex;
    private ID3D11Texture2D* sceneCopy;
    private ID3D11ShaderResourceView* sceneCopySrv;

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

        var cbDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)((Unsafe.SizeOf<FrameConstants>() + 15) & ~15),
            Usage = D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_WRITE,
        };
        fixed (ID3D11Buffer** out_ = &constants) Check(device->CreateBuffer(&cbDesc, null, out_), "CreateBuffer(constants)");
        CreateStructured(null, MaxBones * 4, sizeof(Vector4), dynamic: true, ref bones, ref bonesSrv);

        // Stochastic transparency writes opaque fragments, so blending is off.
        var blendDesc = new D3D11_BLEND_DESC();
        blendDesc.RenderTarget[0].BlendEnable = false;
        blendDesc.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
        blendDesc.RenderTarget[0].DestBlend = D3D11_BLEND_ZERO;
        blendDesc.RenderTarget[0].BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        blendDesc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
        blendDesc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ZERO;
        blendDesc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        blendDesc.RenderTarget[0].RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_RED
            | (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_GREEN | (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_BLUE;
        fixed (ID3D11BlendState** out_ = &blend) Check(device->CreateBlendState(&blendDesc, out_), "CreateBlendState");

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
        Asset = null;
        if (asset == null) return;
        if (asset.BoneNames.Length > MaxBones) throw new InvalidDataException($"Asset uses {asset.BoneNames.Length} bones; max {MaxBones}.");
        fixed (StrandPoint* p = asset.Points)
            CreateStructured(p, asset.Points.Length, sizeof(StrandPoint), dynamic: false, ref points, ref pointsSrv);
        var packed = new GpuSkin[asset.StrandCount];
        for (var i = 0; i < packed.Length; i++)
        {
            var s = asset.Skins[i];
            packed[i] = new GpuSkin { Bones = s.B0 | (uint)s.B1 << 8 | (uint)s.B2 << 16 | (uint)s.B3 << 24, W = new Vector4(s.W0, s.W1, s.W2, s.W3) };
        }
        fixed (GpuSkin* p = packed)
            CreateStructured(p, packed.Length, sizeof(GpuSkin), dynamic: false, ref skins, ref skinsSrv);
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
            // Assets are baked at 10% of the reference strand density (Interpolate amount 0.10); thicken coverage by the
            // shortfall so silhouettes stay as solid as the reference instead of showing the background between strands.
            CoverageScale = Math.Max(1f, Asset.StrandCount / (AssetDensityFraction * Math.Max(1, Math.Min(strandCount, Asset.StrandCount)) * Math.Clamp(childCount, 1, 16))),
        };
        LastDrawInfo = $"backbuffer {bbDesc.Width}x{bbDesc.Height} {bbDesc.Format}; reverseZ(proj) {reverse}; depthUvScale {fc.DepthUvScale}; sceneDepth {(fc.UseSceneDepth != 0 ? "on" : "off")}";
        Upload(ctx, (ID3D11Resource*)constants, &fc, sizeof(FrameConstants));
        fixed (Matrix4x4* m = skinMatrices)
            Upload(ctx, (ID3D11Resource*)bones, m, Math.Min(skinMatrices.Length, MaxBones) * sizeof(Matrix4x4));

        using var saved = new SavedState(ctx);
        ctx->ClearDepthStencilView(ownDepthDsv, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, reverse ? 0f : 1f, 0);
        var rtvLocal = rtv;
        ctx->OMSetRenderTargets(1, &rtvLocal, ownDepthDsv);
        var viewport = new D3D11_VIEWPORT { Width = bbDesc.Width, Height = bbDesc.Height, MaxDepth = 1 };
        ctx->RSSetViewports(1, &viewport);
        ctx->RSSetState(raster);
        var blendFactor = stackalloc float[4];
        ctx->OMSetBlendState(blend, blendFactor, 0xFFFFFFFF);
        ctx->OMSetDepthStencilState(depthState, 0);
        ctx->IASetInputLayout(null);
        ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        ctx->VSSetShader(vs, null, 0);
        ctx->PSSetShader(ps, null, 0);
        var cb = constants;
        ctx->VSSetConstantBuffers(0, 1, &cb);
        ctx->PSSetConstantBuffers(0, 1, &cb);
        var vsSrvs = stackalloc ID3D11ShaderResourceView*[3] { pointsSrv, skinsSrv, bonesSrv };
        ctx->VSSetShaderResources(0, 3, vsSrvs);
        var psSrvs = stackalloc ID3D11ShaderResourceView*[2] { sceneDepth, sceneCopySrv };
        ctx->PSSetShaderResources(3, 2, psSrvs);
        ctx->DrawInstanced(8, (uint)Math.Min(strandCount, Asset.StrandCount) * fc.ChildCount, 0, 0);
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
        depthStateReverse = reverse;
    }

    private ID3D11Texture2D* readback;

    private bool EnsureSceneCopy(D3D11_TEXTURE2D_DESC bb)
    {
        if (sceneCopy != null)
        {
            D3D11_TEXTURE2D_DESC current;
            sceneCopy->GetDesc(&current);
            if (current.Width == bb.Width && current.Height == bb.Height && current.Format == bb.Format) return true;
            Release(ref sceneCopySrv);
            Release(ref sceneCopy);
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
        return true;
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
        Release(ref ownDepthDsv);
        Release(ref ownDepth);
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = bbDesc.Width,
            Height = bbDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_D32_FLOAT,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_DEPTH_STENCIL,
        };
        fixed (ID3D11Texture2D** out_ = &ownDepth) Check(device->CreateTexture2D(&desc, null, out_), "CreateTexture2D(depth)");
        fixed (ID3D11DepthStencilView** out_ = &ownDepthDsv) Check(device->CreateDepthStencilView((ID3D11Resource*)ownDepth, null, out_), "CreateDepthStencilView");
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
        Release(ref bonesSrv); Release(ref bones);
        Release(ref blend); Release(ref raster); Release(ref depthState);
        Release(ref ownDepthDsv); Release(ref ownDepth);
        Release(ref rtv); Release(ref rtvTarget); Release(ref rtvOther); Release(ref rtvOtherTarget);
        Release(ref readback);
        Release(ref sceneCopySrv); Release(ref sceneCopy);
        device->Release();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuSkin
    {
        public uint Bones;
        public Vector4 W;
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
        private readonly ID3D11ShaderResourceView* vsSrv0, vsSrv1, vsSrv2, psSrv3, psSrv4;

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
            var srvs = stackalloc ID3D11ShaderResourceView*[3];
            ctx->VSGetShaderResources(0, 3, srvs); vsSrv0 = srvs[0]; vsSrv1 = srvs[1]; vsSrv2 = srvs[2];
            var ps34 = stackalloc ID3D11ShaderResourceView*[2];
            ctx->PSGetShaderResources(3, 2, ps34); psSrv3 = ps34[0]; psSrv4 = ps34[1];
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
            var srvs = stackalloc ID3D11ShaderResourceView*[3] { vsSrv0, vsSrv1, vsSrv2 };
            ctx->VSSetShaderResources(0, 3, srvs);
            var ps34 = stackalloc ID3D11ShaderResourceView*[2] { psSrv3, psSrv4 };
            ctx->PSSetShaderResources(3, 2, ps34);
            ReleaseAll();
        }

        private void ReleaseAll()
        {
            Rel(rtv); Rel(dsv); Rel(raster); Rel(blend); Rel(depth); Rel(layout); Rel(vs); Rel(ps);
            Rel(vsCb); Rel(psCb); Rel(vsSrv0); Rel(vsSrv1); Rel(vsSrv2); Rel(psSrv3); Rel(psSrv4);
        }

        private static void Rel(void* p)
        {
            if (p != null) ((IUnknown*)p)->Release();
        }
    }
}
