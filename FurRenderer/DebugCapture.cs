using System.IO.Compression;
using System.Text.Json;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace FurRenderer;

/// <summary>
/// Debug remote control for look-dev without screenshots from the user. Drop <c>debug-request.json</c> into the plugin
/// config directory:
/// <code>{ "set": { "Gravity": 0.5, "BaseColor": { "X": 1, "Y": 0, "Z": 0 } }, "frames": 10, "capture": "name", "scale": 2,
///         "crop": [x, y, w, h], "camera": { "yaw": 180, "pitch": -10, "distance": 3 } }</code>
/// The plugin applies "set" (not saved to the config file), waits "frames", then writes captures/name.png (the frame right
/// after the fur draw, downscaled by "scale", optionally cropped first in full-resolution pixels) and debug-result.txt.
/// "atPresent": true grabs the presented frame instead, including whatever the game draws after the fur.
/// "alpha": true saves the frame's alpha channel as grey (ReShade's KeepUI reads it as a UI mask).
/// "depth": true saves the game's scene depth after the fur draw instead (grey = sqrt of the stored depth, 24-bit depth only).
/// </summary>
internal sealed unsafe class DebugCapture
{
    private readonly string directory;
    private readonly Configuration config;
    private readonly Func<float?, float?, float?, string> setCamera;
    private readonly Action requestTrace;
    private int pollFrame;
    private int waitFrames = -1;
    private string? captureName;
    private int scale = 2;
    private int[]? crop;
    private bool atPresent;
    private bool alpha;
    private bool depth;
    private readonly List<string> applied = new();
    // Render-thread capture: armed at Present, filled by the render thread right after the fur draw, saved at the next Present.
    private volatile bool armed;
    private int armedFrames;
    private volatile byte[]? grabbed;
    private int grabbedWidth, grabbedHeight;
    private string? grabError;

    public DebugCapture(string directory, Configuration config, Func<float?, float?, float?, string> setCamera, Action requestTrace)
    {
        this.directory = directory;
        this.config = config;
        this.setCamera = setCamera;
        this.requestTrace = requestTrace;
    }

    private string RequestPath => Path.Combine(directory, "debug-request.json");

    /// <summary>Call once per frame at Present. Returns a status line when a request completed.</summary>
    public string? Tick(Func<(nint Ctx, nint BackBuffer)?> acquire, Action<nint> release, Func<string> frameInfo)
    {
        if (waitFrames < 0 && !armed && grabbed == null && grabError == null)
        {
            if (++pollFrame % 30 != 0 || !File.Exists(RequestPath)) return null;
            Begin();
            return null;
        }
        if (waitFrames > 0 && --waitFrames > 0) return null;
        if (captureName != null && waitFrames == 0 && !atPresent)
        {
            // Hand the grab to the render thread; the swap chain read at Present races the render thread's next frame.
            waitFrames = -1;
            armedFrames = 0;
            armed = true;
            return null;
        }
        waitFrames = -1;
        var result = $"applied: {string.Join(", ", applied)}; {frameInfo()}";
        if (captureName != null)
        {
            var path = Path.Combine(directory, "captures", captureName + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (armed && ++armedFrames < 60) return null;
            if (armed || atPresent)
            {
                // Fur never drew (disabled, wrong draw point...): fall back to the presented frame.
                result += armed ? "; fur not drawn, captured at Present" : "; captured at Present";
                armed = false;
                var target = acquire();
                if (target != null)
                {
                    try { Grab((ID3D11DeviceContext*)target.Value.Ctx, (ID3D11Texture2D*)target.Value.BackBuffer); }
                    finally { release(target.Value.BackBuffer); }
                }
            }
            if (grabbed is { } rgb)
            {
                try
                {
                    File.WriteAllBytes(path, Png(rgb, grabbedWidth, grabbedHeight));
                    result += "; captured " + path;
                }
                catch (IOException ex)
                {
                    result += "; capture failed: " + ex.Message;
                }
            }
            else
            {
                result += "; capture failed: " + (grabError ?? "no back buffer");
            }
            grabbed = null;
            grabError = null;
            captureName = null;
        }
        File.WriteAllText(Path.Combine(directory, "debug-result.txt"), result);
        return result;
    }

    /// <summary>Render thread, right after the fur draw (before the UI): grabs the frame when a capture is armed.</summary>
    public void OnFurDrawn(ID3D11DeviceContext* ctx, ID3D11Texture2D* target, ID3D11Texture2D* sceneDepth)
    {
        if (!armed) return;
        Grab(ctx, depth && sceneDepth != null ? sceneDepth : target);
        armed = false;
    }

    private void Grab(ID3D11DeviceContext* ctx, ID3D11Texture2D* backBuffer)
    {
        try
        {
            Save(ctx, backBuffer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            grabError = ex.Message;
        }
    }

    private void Begin()
    {
        applied.Clear();
        captureName = null;
        crop = null;
        atPresent = false;
        alpha = false;
        depth = false;
        scale = 2;
        var frames = 10;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(RequestPath));
            File.Delete(RequestPath);
            var root = doc.RootElement;
            if (root.TryGetProperty("set", out var set))
            {
                // Same serializer Dalamud uses for the config file, so property names and shapes match.
                Newtonsoft.Json.JsonConvert.PopulateObject(set.GetRawText(), config);
                applied.Add(set.GetRawText());
            }
            if (root.TryGetProperty("camera", out var cam))
            {
                // Orbit around the character: yaw (degrees from its facing), pitch (degrees), distance (yalms).
                float? Get(string name) => cam.TryGetProperty(name, out var v) ? v.GetSingle() : null;
                applied.Add(setCamera(Get("yaw"), Get("pitch"), Get("distance")));
            }
            if (root.TryGetProperty("trace", out var t) && t.GetBoolean())
            {
                requestTrace(); // logs one frame's render-target commands as [trace]
                applied.Add("trace");
            }
            if (root.TryGetProperty("frames", out var f)) frames = Math.Max(1, f.GetInt32());
            if (root.TryGetProperty("capture", out var c)) captureName = string.Concat(c.GetString()!.Split(Path.GetInvalidFileNameChars()));
            if (root.TryGetProperty("scale", out var s)) scale = Math.Max(1, s.GetInt32());
            if (root.TryGetProperty("crop", out var r)) crop = r.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (root.TryGetProperty("atPresent", out var ap)) atPresent = ap.GetBoolean();
            if (root.TryGetProperty("alpha", out var al)) alpha = al.GetBoolean();
            if (root.TryGetProperty("depth", out var dp)) depth = dp.GetBoolean();
        }
        catch (Exception ex)
        {
            try { File.Delete(RequestPath); } catch (IOException) { }
            applied.Add("bad request: " + ex.Message);
        }
        waitFrames = frames;
    }

    private void Save(ID3D11DeviceContext* ctx, ID3D11Texture2D* backBuffer)
    {
        D3D11_TEXTURE2D_DESC desc;
        backBuffer->GetDesc(&desc);
        var bgra = desc.Format is DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT_B8G8R8A8_UNORM_SRGB or DXGI_FORMAT_B8G8R8A8_TYPELESS;
        var rgba = desc.Format is DXGI_FORMAT_R8G8B8A8_UNORM or DXGI_FORMAT_R8G8B8A8_UNORM_SRGB or DXGI_FORMAT_R8G8B8A8_TYPELESS;
        var d24 = desc.Format == DXGI_FORMAT_R24G8_TYPELESS;
        if (!bgra && !rgba && !d24) throw new NotSupportedException($"back buffer format {desc.Format}");

        var stagingDesc = desc;
        stagingDesc.Usage = D3D11_USAGE_STAGING;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ;
        stagingDesc.MiscFlags = 0;
        ID3D11Device* device;
        ctx->GetDevice(&device);
        ID3D11Texture2D* staging = null;
        try
        {
            var hr = device->CreateTexture2D(&stagingDesc, null, &staging);
            if (hr.FAILED) throw new InvalidOperationException($"CreateTexture2D(staging) 0x{hr.Value:X8}");
            ctx->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)backBuffer);
            D3D11_MAPPED_SUBRESOURCE mapped;
            hr = ctx->Map((ID3D11Resource*)staging, 0, D3D11_MAP_READ, 0, &mapped);
            if (hr.FAILED) throw new InvalidOperationException($"Map 0x{hr.Value:X8}");
            int x0 = 0, y0 = 0, w = (int)desc.Width, h = (int)desc.Height;
            if (crop is { Length: 4 })
            {
                x0 = Math.Clamp(crop[0], 0, w - 1); y0 = Math.Clamp(crop[1], 0, h - 1);
                w = Math.Clamp(crop[2], 1, w - x0); h = Math.Clamp(crop[3], 1, h - y0);
            }
            int ow = w / scale, oh = h / scale;
            var rgb = new byte[ow * oh * 3];
            for (var y = 0; y < oh; y++)
            {
                var row = (byte*)mapped.pData + (y0 + y * scale) * mapped.RowPitch;
                for (var x = 0; x < ow; x++)
                {
                    var p = row + (x0 + x * scale) * 4;
                    var o = (y * ow + x) * 3;
                    if (d24)
                    {
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = (byte)(255 * Math.Sqrt((p[0] | p[1] << 8 | p[2] << 16) / 16777215.0));
                        continue;
                    }
                    rgb[o] = alpha ? p[3] : bgra ? p[2] : p[0];
                    rgb[o + 1] = alpha ? p[3] : p[1];
                    rgb[o + 2] = alpha ? p[3] : bgra ? p[0] : p[2];
                }
            }
            ctx->Unmap((ID3D11Resource*)staging, 0);
            (grabbedWidth, grabbedHeight) = (ow, oh);
            grabbed = rgb;
        }
        finally
        {
            if (staging != null) staging->Release();
            device->Release();
        }
    }

    private static byte[] Png(byte[] rgb, int w, int h)
    {
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (var y = 0; y < h; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(rgb, y * w * 3, w * 3);
            }
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)w); BigEndian(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var buf = new byte[12 + data.Length];
        BigEndian(buf, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) buf[4 + i] = (byte)type[i];
        data.CopyTo(buf, 8);
        BigEndian(buf, 8 + data.Length, Crc(buf.AsSpan(4, 4 + data.Length)));
        s.Write(buf);
    }

    private static void BigEndian(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
