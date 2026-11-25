using System.IO.Compression;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using Newtonsoft.Json.Linq;
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
/// The plugin applies "set" (the override is kept out of the saved config: see ForSaving), waits "frames", then writes captures/name.png (the frame right
/// after the fur draw, downscaled by "scale", optionally cropped first in full-resolution pixels) and debug-result.txt.
/// "atPresent": true grabs the presented frame instead, including whatever the game draws after the fur.
/// "alpha": true saves the frame's alpha channel as grey (ReShade's KeepUI reads it as a UI mask).
/// "depth": true saves the game's scene depth after the fur draw instead (grey = sqrt of the stored depth, 24-bit depth only).
/// "burst": n saves n consecutive frames as captures/name_00.png ... (for flicker; each frame is a staging copy, so crop:
/// without "crop" a burst is capped at 30 frames). "frames" is clamped to 1..3600.
/// "path": [[yaw, pitch, distance], ...] moves the camera through these keyframes (as "camera"), spread evenly over the burst's
/// frames: a moving-camera flicker test.
/// "camera": null reads the camera back: the result has "camera yaw=.. pitch=.. distance=.." (invariant '.'), which "camera" takes
/// as is to restore the view (every "camera" request reports it, after applying its values). The camera starts at the first one while "frames" pass.
/// "dye": { "slot": "Body", "stains": [36, 0] } dyes the item worn there through Glamourer (until the next redraw); without
/// "stains" it reports the item and its dyes (slots Head, Body, Hands, Legs, Feet, RFinger, LFinger: the ring coat is a finger); "dye": "revert" reverts Glamourer's equipment state to the game's (applied
/// designs too; to undo a test dye, set the reported dyes back instead).
/// "mod": { "name": "Fluffy Coat", "group": "Ring Version - Mirage Ring", "options": ["Enabled (Fur Plugin)"] } sets that option
/// group of the mod (matched by part of its name) in the local player's Penumbra collection and redraws; without "group" it
/// reports the mod's settings. "gameConfig": { "GraphicsRezoUpscaleType": 0 } sets game system options (FFXIV.cfg names, unsigned;
/// null reports) until set back, like the game's settings window. "command": "/gpose" runs a text command as typed (only "/...").
/// "time": Eorzea seconds of the day (43200 = noon) is held through the game's own override until -1 releases it (or the
/// plugin unloads). Weather isn't here: WeatherManager.WeatherOverride moved the game's weather id and EnvManager's, but the
/// frame stayed the same (rain and fog in Il Mheg, which has both); Weatherman patches the renderer for that. Its IPC can't
/// set weather or time for another plugin either: only while its own override is on, and its next update writes its own back.
/// "freeze": true (GPose only) freezes the GPose character's animation and the physics through Brio's IPC, for the same pose in
/// every capture; false unfreezes both. "exitGpose": true leaves GPose ("/gpose" only enters it); unfreeze first.
/// </summary>
internal sealed unsafe class DebugCapture
{
    private readonly string directory;
    private readonly Configuration config;
    private readonly Func<float?, float?, float?, string> setCamera;
    private readonly Action requestTrace;
    private readonly IDalamudPluginInterface pi;
    private readonly IGameConfig gameConfig;
    private readonly IObjectTable objectTable;
    private long holdTime = -1; // Eorzea seconds of the day, -1 = the game's
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
    private readonly List<byte[]> shots = new(); // the render thread adds while armed; Tick reads once armed is cleared
    private int burst = 1;
    private readonly List<nint> pending = new(); // a burst's staging copies, read after its last frame
    private float[][]? path; // camera keyframes over the burst: yaw, pitch, distance
    private int readX, readY, readW, readH; // the region of the staging copies to read
    private bool readBgra, readD24;
    private int grabbedWidth, grabbedHeight;
    private string? grabError;
    // Each property a debug "set" changed: its value before the first set, and the value the last set left.
    private readonly Dictionary<string, (JToken Before, JToken Set)> overrides = new();
    // Lists replaced, not appended to (Newtonsoft's default fills the existing list).
    private static readonly Newtonsoft.Json.JsonSerializerSettings Replace = new() { ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace };
    private DateTime stuckRequest; // a request file that couldn't be deleted: not read again until it changes

    public DebugCapture(IDalamudPluginInterface pi, IGameConfig gameConfig, IObjectTable objectTable, Configuration config,
        Func<float?, float?, float?, string> setCamera, Action requestTrace)
    {
        this.pi = pi;
        this.gameConfig = gameConfig;
        this.objectTable = objectTable;
        directory = pi.GetPluginConfigDirectory();
        this.config = config;
        this.setCamera = setCamera;
        this.requestTrace = requestTrace;
    }

    private string RequestPath => Path.Combine(directory, "debug-request.json");

    /// <summary>Call once per frame at Present. Returns a status line when a request completed.</summary>
    public string? Tick(Func<(nint Ctx, nint BackBuffer)?> acquire, Action<nint> release, Func<string> frameInfo)
    {
        HoldTime();
        if (waitFrames < 0 && !armed && shots.Count == 0 && grabError == null)
        {
            if (++pollFrame % 30 != 0 || !File.Exists(RequestPath) || File.GetLastWriteTimeUtc(RequestPath) == stuckRequest) return null;
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
        if (armed)
        {
            if (path != null) MoveCamera(armedFrames);
            if (++armedFrames < 60 + burst) return null; // the render thread hasn't drawn fur yet
        }
        waitFrames = -1;
        var result = $"applied: {string.Join(", ", applied)}; {frameInfo()}";
        if (captureName != null)
        {
            var path = Path.Combine(directory, "captures", captureName + ".png");
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
            if (shots.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    for (var i = 0; i < shots.Count; i++)
                        File.WriteAllBytes(burst > 1 ? $"{path[..^4]}_{i:D2}.png" : path, Png(shots[i], grabbedWidth, grabbedHeight));
                    result += burst > 1 ? $"; captured {shots.Count} frames as {path[..^4]}_NN.png" : "; captured " + path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result += "; capture failed: " + ex.Message;
                }
            }
            else
            {
                result += "; capture failed: " + (grabError ?? "no back buffer");
            }
            shots.Clear();
            grabError = null;
            captureName = null;
        }
        try { File.WriteAllText(Path.Combine(directory, "debug-result.txt"), result); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result += "; debug-result.txt not written: " + ex.Message; }
        return result;
    }

    /// <summary>Render thread, right after the fur draw (before the UI): grabs the frame when a capture is armed.</summary>
    public void OnFurDrawn(ID3D11DeviceContext* ctx, ID3D11Texture2D* target, ID3D11Texture2D* sceneDepth)
    {
        if (!armed) return;
        // A burst copies each frame on the GPU and reads them all after the last one: reading every frame stalled the GPU each
        // time, which changes the frame timing a flicker may depend on.
        Grab(ctx, depth && sceneDepth != null ? sceneDepth : target, pending.Count + 1 >= burst);
        if (shots.Count >= burst || grabError != null) armed = false;
    }

    private void Grab(ID3D11DeviceContext* ctx, ID3D11Texture2D* source, bool read = true)
    {
        try
        {
            Copy(ctx, source);
            if (read) ReadPending(ctx);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            grabError = ex.Message;
        }
        finally
        {
            if (read || grabError != null) ReleasePending();
        }
    }

    // Every frame, in case the game clears its override.
    private void HoldTime()
    {
        var fw = Framework.Instance();
        if (holdTime < 0 || fw == null) return;
        var et = fw->ClientTime.EorzeaTime;
        fw->ClientTime.EorzeaTimeOverride = et - et % 86400 + holdTime;
        fw->ClientTime.IsEorzeaTimeOverridden = true;
    }

    /// <summary>Hands the time back to the game.</summary>
    public void ReleaseHolds() => Hold(-1);

    /// <summary>Unload: hands the time back and frees a burst's staging textures. Call after the render thread has left OnFurDrawn.</summary>
    public void Dispose()
    {
        armed = false;
        ReleaseHolds();
        ReleasePending();
    }

    /// <summary>What to save: <paramref name="config"/> itself, or a copy with each property a debug "set" changed back at its earlier value.</summary>
    public Configuration ForSaving(Configuration config)
    {
        if (overrides.Count == 0) return config;
        var saved = JObject.FromObject(config);
        foreach (var (name, (before, set)) in overrides.ToArray())
            if (JToken.DeepEquals(saved[name], set)) saved[name] = before;
            else overrides.Remove(name); // changed since in the settings window: that value is the user's
        return saved.ToObject<Configuration>(Newtonsoft.Json.JsonSerializer.Create(Replace))!;
    }

    // -1 hands it back to the game.
    private string Hold(long time)
    {
        var fw = Framework.Instance();
        if (time < 0 && holdTime >= 0 && fw != null) fw->ClientTime.IsEorzeaTimeOverridden = false;
        holdTime = time < 0 ? -1 : time % 86400;
        HoldTime();
        return $"time {(holdTime < 0 ? "game's" : $"{holdTime / 3600:D2}:{holdTime / 60 % 60:D2}")}";
    }

    private static readonly Dictionary<string, byte> GlamourerSlots = new(StringComparer.OrdinalIgnoreCase)
        { ["Head"] = 3, ["Body"] = 4, ["Hands"] = 5, ["Legs"] = 7, ["Feet"] = 8, ["RFinger"] = 12, ["LFinger"] = 14 }; // Glamourer.Api's ApiEquipSlot

    // The local player (object index 0) through Glamourer's IPC (Glamourer.Api 1.7: SetItem.V3, GetState, RevertState).
    private string Dye(JsonElement dye)
    {
        if (dye.ValueKind == JsonValueKind.String && dye.GetString() == "revert")
            return "dye revert: " + pi.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState").InvokeFunc(0, 0, 2); // Equipment
        var slotName = dye.GetProperty("slot").GetString()!;
        if (!GlamourerSlots.TryGetValue(slotName, out var slot)) throw new FormatException("dye slot: " + string.Join(", ", GlamourerSlots.Keys));
        var (ec, state) = pi.GetIpcSubscriber<int, uint, (int, JObject?)>("Glamourer.GetState").InvokeFunc(0, 0);
        var worn = state?["Equipment"]?[slotName];
        if (ec != 0 || worn == null) return $"dye: Glamourer.GetState returned {ec}";
        var item = worn.Value<ulong>("ItemId");
        var line = $"dye {slotName}: item {item}, was {worn["Stain"]}/{worn["Stain2"]}";
        if (!dye.TryGetProperty("stains", out var s)) return line;
        // A List: Dalamud passes other types through JSON, where a byte[] becomes a base64 string Glamourer can't read.
        var stains = s.EnumerateArray().Select(v => (byte)v.GetInt32()).ToList();
        var result = pi.GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3")
            .InvokeFunc(0, slot, item, stains, 0, 1); // ApplyFlag.Once
        return $"{line}, now {string.Join("/", stains)}: {(result == 0 ? "ok" : "Glamourer error " + result)}";
    }

    // The local player's collection through Penumbra's IPC (API 5). "name" matches one mod by part of its name or folder.
    private string Mod(JsonElement mod)
    {
        var name = mod.GetProperty("name").GetString()!;
        var mods = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc()
            .Where(m => m.Key.Contains(name, StringComparison.OrdinalIgnoreCase) || m.Value.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mods.Count != 1) return $"mod '{name}': {mods.Count} matches {string.Join(" | ", mods.Select(m => m.Value))}";
        var (dir, modName) = mods[0];
        var (_, _, (collection, collectionName)) = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5").InvokeFunc(0);
        var line = $"mod {modName} in {collectionName}";
        if (mod.TryGetProperty("group", out var group))
        {
            var options = mod.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToList();
            var ec = pi.GetIpcSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>("Penumbra.TrySetModSettings.V5")
                .InvokeFunc(collection, dir, "", group.GetString()!, options);
            pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5").InvokeAction(0, 0); // RedrawType.Redraw
            line += $", set {group.GetString()} = [{string.Join(", ", options)}]: PenumbraApiEc {ec}";
        }
        var (rc, s) = pi.GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>(
            "Penumbra.GetCurrentModSettings.V5").InvokeFunc(collection, dir, "", false);
        if (s is not { } settings) return $"{line}; settings: PenumbraApiEc {rc}";
        var (enabled, priority, groups, _) = settings;
        return $"{line}; {(enabled ? "on" : "off")}, priority {priority}; " + string.Join("; ", groups.Select(g => $"{g.Key} = [{string.Join(", ", g.Value)}]"));
    }

    // A game system option by its FFXIV.cfg name (unsigned), as the game's settings window sets it: it stays until set back.
    // A null value only reports it.
    private string GameConfig(string name, JsonElement value)
    {
        if (!gameConfig.System.TryGet(name, out uint was)) return $"{name}: no such uint option";
        if (value.ValueKind == JsonValueKind.Null) return $"{name} {was}";
        gameConfig.System.Set(name, value.GetUInt32());
        return $"{name} was {was}, now {value.GetUInt32()}";
    }

    // A text command through the chat box, as if typed. Only commands, and only in dev builds: other text, or /say in a
    // release someone else's tool writes requests for, would be said in chat.
    private string Command(string text)
    {
        if (!pi.IsDev) throw new InvalidOperationException("command: dev builds only");
        if (!text.StartsWith('/')) throw new FormatException("command must start with /");
        var message = Utf8String.FromString(text);
        UIModule.Instance()->ProcessChatBoxEntry(message);
        message->Dtor(true);
        return "command " + text;
    }

    // The GPose character (object 201, as GameInterop draws in GPose) and the physics, through Brio's IPC (API V3).
    private string Freeze(bool on)
    {
        if (!FFXIVClientStructs.FFXIV.Client.Game.GameMain.IsInGPose()) throw new InvalidOperationException("freeze: GPose only");
        var actor = objectTable[201] ?? throw new InvalidOperationException("freeze: no GPose actor");
        var physics = pi.GetIpcSubscriber<bool>(on ? "Brio.FreezePhysics.V3" : "Brio.UnFreezePhysics.V3").InvokeFunc();
        var animation = pi.GetIpcSubscriber<Dalamud.Game.ClientState.Objects.Types.IGameObject, bool>(on ? "Brio.FreezeActor.V3" : "Brio.UnFreezeActor.V3").InvokeFunc(actor);
        return $"freeze {on}: physics {physics}, animation {animation} (object {actor.ObjectIndex})";
    }

    // Frame i of the burst: between the keyframes it falls between (the last one after the burst's last frame).
    private void MoveCamera(int i)
    {
        var u = Math.Min(i, burst - 1) / (float)Math.Max(burst - 1, 1) * (path!.Length - 1);
        var k = Math.Min((int)u, path.Length - 2);
        var f = u - k;
        float At(int c) => path[k][c] + (path[k + 1][c] - path[k][c]) * f;
        setCamera(At(0), At(1), At(2));
    }

    private void Begin()
    {
        applied.Clear();
        path = null;
        captureName = null;
        crop = null;
        atPresent = false;
        alpha = false;
        depth = false;
        scale = 2;
        burst = 1;
        var frames = 10;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(RequestPath));
            File.Delete(RequestPath);
            var root = doc.RootElement;
            if (root.TryGetProperty("set", out var set))
            {
                // Same serializer Dalamud uses for the config file, so property names and shapes match.
                var before = JObject.FromObject(config);
                Newtonsoft.Json.JsonConvert.PopulateObject(set.GetRawText(), config, Replace);
                var after = JObject.FromObject(config);
                foreach (var prop in set.EnumerateObject())
                    if (before.Property(prop.Name, StringComparison.OrdinalIgnoreCase) is { } old)
                        overrides[old.Name] = (overrides.TryGetValue(old.Name, out var o) ? o.Before : old.Value, after[old.Name]!);
                applied.Add(set.GetRawText());
            }
            if (root.TryGetProperty("camera", out var cam))
            {
                // Orbit around the character: yaw (degrees from its facing), pitch (degrees), distance (yalms).
                // "camera": null changes nothing and reports the current "camera yaw=.. pitch=.. distance=..".
                float? Get(string name) => cam.ValueKind != JsonValueKind.Null && cam.TryGetProperty(name, out var v) ? v.GetSingle() : null;
                applied.Add(setCamera(Get("yaw"), Get("pitch"), Get("distance")));
            }
            if (root.TryGetProperty("dye", out var dy)) applied.Add(Dye(dy));
            if (root.TryGetProperty("mod", out var md)) applied.Add(Mod(md));
            if (root.TryGetProperty("gameConfig", out var gc))
                foreach (var option in gc.EnumerateObject()) applied.Add(GameConfig(option.Name, option.Value));
            if (root.TryGetProperty("command", out var cm)) applied.Add(Command(cm.GetString()!));
            if (root.TryGetProperty("time", out var tm)) applied.Add(Hold(tm.GetInt64()));
            if (root.TryGetProperty("freeze", out var fz)) applied.Add(Freeze(fz.GetBoolean()));
            if (root.TryGetProperty("exitGpose", out var eg) && eg.GetBoolean())
            {
                var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
                if (ui != null) ui->ExitGPose();
                applied.Add("exit gpose");
            }
            if (root.TryGetProperty("trace", out var t) && t.GetBoolean())
            {
                requestTrace(); // logs one frame's render-target commands as [trace]
                applied.Add("trace");
            }
            if (root.TryGetProperty("frames", out var f)) frames = Math.Clamp(f.GetInt32(), 1, 3600);
            if (root.TryGetProperty("capture", out var c)) captureName = string.Concat(c.GetString()!.Split(Path.GetInvalidFileNameChars()));
            if (root.TryGetProperty("scale", out var s)) scale = Math.Max(1, s.GetInt32());
            if (root.TryGetProperty("crop", out var r)) crop = r.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (root.TryGetProperty("atPresent", out var ap)) atPresent = ap.GetBoolean();
            if (root.TryGetProperty("alpha", out var al)) alpha = al.GetBoolean();
            if (root.TryGetProperty("depth", out var dp)) depth = dp.GetBoolean();
            if (root.TryGetProperty("burst", out var b)) burst = Math.Clamp(b.GetInt32(), 1, 240);
            if (root.TryGetProperty("path", out var pa))
            {
                var keys = pa.EnumerateArray().Select(k => k.EnumerateArray().Select(v => v.GetSingle()).ToArray()).ToArray();
                if (keys.Length < 2 || keys.Any(k => k.Length != 3)) throw new FormatException("path needs 2+ keyframes of [yaw, pitch, distance]");
                path = keys;
                applied.Add(setCamera(keys[0][0], keys[0][1], keys[0][2]));
            }
            // Each frame is a staging copy: of the crop, or whole (no crop, or depth, which only copies whole; 4K assumed).
            var pixels = crop is { Length: 4 } && !depth ? Math.Max((long)crop[2] * crop[3], 1) : 3840L * 2160;
            var most = (int)Math.Clamp(120_000_000 / pixels, 1, 240); // about 480 MB
            if (burst > most)
            {
                burst = most;
                applied.Add($"burst capped at {most} frames of this size");
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(RequestPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stuckRequest = File.GetLastWriteTimeUtc(RequestPath); }
            applied.Add("bad request: " + ex.Message);
        }
        waitFrames = frames;
    }

    // Copies the crop into a new staging texture (all of it for depth: depth-stencil resources only copy whole).
    private void Copy(ID3D11DeviceContext* ctx, ID3D11Texture2D* source)
    {
        D3D11_TEXTURE2D_DESC desc;
        source->GetDesc(&desc);
        var bgra = desc.Format is DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT_B8G8R8A8_UNORM_SRGB or DXGI_FORMAT_B8G8R8A8_TYPELESS;
        var rgba = desc.Format is DXGI_FORMAT_R8G8B8A8_UNORM or DXGI_FORMAT_R8G8B8A8_UNORM_SRGB or DXGI_FORMAT_R8G8B8A8_TYPELESS;
        var d24 = desc.Format == DXGI_FORMAT_R24G8_TYPELESS;
        if (!bgra && !rgba && !d24) throw new NotSupportedException($"back buffer format {desc.Format}");
        int x0 = 0, y0 = 0, w = (int)desc.Width, h = (int)desc.Height;
        if (crop is { Length: 4 })
        {
            x0 = Math.Clamp(crop[0], 0, w - 1); y0 = Math.Clamp(crop[1], 0, h - 1);
            w = Math.Clamp(crop[2], 1, w - x0); h = Math.Clamp(crop[3], 1, h - y0);
        }

        var stagingDesc = desc;
        if (!d24) (stagingDesc.Width, stagingDesc.Height) = ((uint)w, (uint)h);
        stagingDesc.MipLevels = 1;
        stagingDesc.Usage = D3D11_USAGE_STAGING;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ;
        stagingDesc.MiscFlags = 0;
        ID3D11Device* device;
        ctx->GetDevice(&device);
        ID3D11Texture2D* staging = null;
        var hr = device->CreateTexture2D(&stagingDesc, null, &staging);
        device->Release();
        if (hr.FAILED) throw new InvalidOperationException($"CreateTexture2D(staging) 0x{hr.Value:X8}");
        if (d24)
        {
            ctx->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)source);
        }
        else
        {
            var box = new D3D11_BOX { left = (uint)x0, top = (uint)y0, front = 0, right = (uint)(x0 + w), bottom = (uint)(y0 + h), back = 1 };
            ctx->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
        }
        pending.Add((nint)staging);
        (readX, readY, readW, readH, readBgra, readD24) = d24 ? (x0, y0, w, h, false, true) : (0, 0, w, h, bgra, false);
    }

    private void ReadPending(ID3D11DeviceContext* ctx)
    {
        int ow = readW / scale, oh = readH / scale;
        foreach (var t in pending)
        {
            var staging = (ID3D11Resource*)t;
            D3D11_MAPPED_SUBRESOURCE mapped;
            var hr = ctx->Map(staging, 0, D3D11_MAP_READ, 0, &mapped);
            if (hr.FAILED) throw new InvalidOperationException($"Map 0x{hr.Value:X8}");
            var rgb = new byte[ow * oh * 3];
            for (var y = 0; y < oh; y++)
            {
                var row = (byte*)mapped.pData + (readY + y * scale) * mapped.RowPitch;
                for (var x = 0; x < ow; x++)
                {
                    var p = row + (readX + x * scale) * 4;
                    var o = (y * ow + x) * 3;
                    if (readD24)
                    {
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = (byte)(255 * Math.Sqrt((p[0] | p[1] << 8 | p[2] << 16) / 16777215.0));
                        continue;
                    }
                    rgb[o] = alpha ? p[3] : readBgra ? p[2] : p[0];
                    rgb[o + 1] = alpha ? p[3] : p[1];
                    rgb[o + 2] = alpha ? p[3] : readBgra ? p[0] : p[2];
                }
            }
            ctx->Unmap(staging, 0);
            shots.Add(rgb);
        }
        (grabbedWidth, grabbedHeight) = (ow, oh);
    }

    private void ReleasePending()
    {
        foreach (var t in pending) ((ID3D11Texture2D*)t)->Release();
        pending.Clear();
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
