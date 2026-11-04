using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TerraFX.Interop.DirectX;

namespace FurRenderer;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fur";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly GameInterop interop;
    private readonly DebugCapture debugCapture;
    private Renderer? renderer;
    private string? rendererError;
    private string? loadedAssetName;
    private string? assetError;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private volatile Matrix4x4[]? deform; // race deformer per bone of the loaded asset
    private bool[] found = new bool[Renderer.MaxBones];
    private volatile FrameSnapshot? snapshot;

    /// <summary>Main-thread state for one frame, consumed by the render thread.</summary>
    private sealed class FrameSnapshot
    {
        public string? Skip;
        public bool Gpose;
        public nint Character;
        public string AssetName = "";
        public string LoadKey = ""; // asset + wearer race: the race deformer is rebuilt on load
        public ushort Race, ModelRace;
        public Dictionary<string, string>? Parents;
        public byte[]? Pbd;
        public string PbdSource = "";
        public Matrix4x4[]? Skin;
        public int Bones;
        public string BonesInfo = "";
        public Matrix4x4 View, Proj;
    }
    private bool windowOpen;
    private string status = "idle";
    private readonly Dictionary<string, string> lastDiag = new();
    private readonly Lock diagLock = new(); // the SetTarget hook runs on the game's render thread

    // Pre-UI injection: the game's render thread executes SetTarget commands; the UI starts with a bind of the swap-chain
    // back buffer, so drawing right after that bind puts the fur under the UI (approach from Gaolbreak's capturer).
    private delegate void DoSetTargetDelegate(ImmediateContext* self, RenderCommandSetTarget* command);
    private readonly Hook<DoSetTargetDelegate>? setTargetHook;
    private int backBufferBinds, lastFrameBinds;
    private int furDraws;
    private readonly System.Diagnostics.Stopwatch cadenceWatch = new();
    private int presentsWindow, furDrawsWindow, emptyPresentsWindow, triggersWindow, depthBindsWindow, uiBindsWindow;
    private string lastFrameInfo = "";
    private nint lastFurTarget;
    private bool traceRequested;
    private List<string>? trace;

    // Self-test: draws debug magenta at each injection point (with/without scene depth) and counts what survives to Present.
    private List<(int Bind, bool Depth)>? testCases;
    private readonly List<string> testResults = new();
    private int testCase, testFrame;
    private (int Bind, bool Depth, bool Solid, bool Enabled) testSaved;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IClientState clientState, IObjectTable objectTable, IGameInteropProvider hooks, IPluginLog log, IFramework framework, IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;
        this.framework = framework;
        this.dataManager = dataManager;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (config.Version < 2) { config.BackBufferBind = -1; config.Version = 2; } // v1 defaults drew over the UI
        if (config.Version < 3) { config.SceneLighting = 1f; config.SceneGain = 4f; config.Version = 3; } // scene lighting now scales brightness only
        if (config.Version < 4) // look matched to the reference render (colour, AgX, full density)
        {
            var fresh = new Configuration();
            (config.BaseColor, config.RootDarken, config.Specular, config.Density) = (fresh.BaseColor, fresh.RootDarken, fresh.Specular, fresh.Density);
            config.Version = 4;
        }
        if (config.Version < 5) // look-dev pass against the reference render (absorption, sheen, children)
        {
            var fresh = new Configuration();
            (config.BaseColor, config.RootDarken, config.HueJitter, config.Specular, config.SurfaceLighting, config.Exposure) =
                (fresh.BaseColor, fresh.RootDarken, fresh.HueJitter, fresh.Specular, fresh.SurfaceLighting, fresh.Exposure);
            config.Version = 5;
        }
        if (config.Version < 6) { config.FacingDepth = 0f; config.Version = 6; } // view-dependent darkening changed the shade with camera angle
        interop = new GameInterop(clientState, objectTable, log);
        debugCapture = new DebugCapture(pluginInterface.GetPluginConfigDirectory(), config, interop.SetCamera, () => traceRequested = true);
        try
        {
            setTargetHook = hooks.HookFromAddress<DoSetTargetDelegate>(ImmediateContext.Addresses.DoSetTargetCommand.Value, SetTargetDetour);
            setTargetHook.Enable();
            Diag("hook", "DoSetTargetCommand hooked");
        }
        catch (Exception ex)
        {
            log.Error(ex, "DoSetTargetCommand hook failed; falling back to drawing at Present");
            Diag("hook", "failed: " + ex.Message);
        }
        commandManager.AddHandler(CommandName, new CommandInfo((_, _) => windowOpen = !windowOpen) { HelpMessage = "Toggle the Fur Renderer settings." });
        // Fur is part of the scene, not UI: keep drawing when the user hides the UI, in cutscenes and in GPose.
        pluginInterface.UiBuilder.DisableUserUiHide = true;
        pluginInterface.UiBuilder.DisableCutsceneUiHide = true;
        pluginInterface.UiBuilder.DisableGposeUiHide = true;
        pluginInterface.UiBuilder.Draw += OnDraw;
        framework.Update += OnFrameworkUpdate;
        pluginInterface.UiBuilder.OpenConfigUi += () => windowOpen = true;
        pluginInterface.UiBuilder.OpenMainUi += () => windowOpen = true;
    }

    private string AssetDirectory => Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Assets");

    private bool DrawAtPresent => config.BackBufferBind == 0 || setTargetHook == null;
    private bool drewThisFrame;
    private bool sawDepthBind;

    // Present runs after all of the frame's render commands: close out the per-frame bind counter and trace here.
    private void OnDraw()
    {
        Diag("binds", $"back buffer bound {backBufferBinds}x last frame; drawing at {(DrawAtPresent ? "Present" : config.BackBufferBind < 0 ? "auto" : $"bind {config.BackBufferBind}")}");
        drewThisFrame = false;
        lastFrameBinds = backBufferBinds;
        lastFrameInfo = $"frame: binds {backBufferBinds}, fur draws {furDraws}, last fur target 0x{lastFurTarget:X}";
        presentsWindow++; furDrawsWindow += furDraws; if (furDraws == 0) emptyPresentsWindow++;
        if (presentsWindow == 1) cadenceWatch.Restart();
        if (presentsWindow == 300)
        {
            // Only when frames went out without fur (that showed up as flashing).
            if (emptyPresentsWindow > 0 && config.Enabled)
                log.Info("[cadence] {Fps:F1} presents/s; last 300 presents: {Draws} fur draws ({Triggers} triggers), {Empty} presents with none; back-buffer binds: {Depth} with depth, {Ui} without; status {Status}",
                    299 / cadenceWatch.Elapsed.TotalSeconds, furDrawsWindow, triggersWindow, emptyPresentsWindow, depthBindsWindow, uiBindsWindow, status);
            presentsWindow = furDrawsWindow = emptyPresentsWindow = triggersWindow = depthBindsWindow = uiBindsWindow = 0;
        }
        furDraws = 0;
        backBufferBinds = 0;
        lock (diagLock)
        {
            if (trace != null)
            {
                log.Info("[trace] SetTarget commands in one frame: {Trace}", string.Join(" | ", trace));
                trace = null;
            }
            if (traceRequested) { traceRequested = false; trace = new List<string>(); }
        }

        if (DrawAtPresent) SafeRenderFur(null);
        if (testCases != null) StepSelfTest();
        var debugResult = debugCapture.Tick(
            () => interop.TryGetDevice(out _, out var c) && interop.TryGetBackBuffer(out var b) ? ((nint)c, (nint)b) : null,
            b => ((ID3D11Texture2D*)b)->Release(),
            () => lastFrameInfo);
        if (debugResult != null) log.Info("[debug-request] {Result}", debugResult);
        Diag("status", System.Text.RegularExpressions.Regex.Replace(status, @"^drawing [\d.,]+", "drawing"));
        if (windowOpen) DrawWindow();
    }

    private void SetTargetDetour(ImmediateContext* self, RenderCommandSetTarget* command)
    {
        try
        {
            var rtm = RenderTargetManager.Instance();
            var rt = command->RenderTargets[0].Value;
            // Some frames are drawn into ToneAdjustSource and only copied to the back buffer at the very end (trace 2026-11-04);
            // missing those left roughly every fourth frame without fur (flashing).
            var isBackBuffer = rtm != null && rt != null && (rt == rtm->SwapChainBackBuffer || rt == rtm->ToneAdjustSource);
            // Auto: draw just before the first UI bind (back buffer without depth) that follows the scene-depth binds. Traces
            // (2026-11-04) show binds 1 composite, 2-3 with DepthStencil, 4-5 UI; with the camera close the game draws over
            // bind 2's contents, so drawing right after bind 2 was hidden.
            if (isBackBuffer && !DrawAtPresent && config.BackBufferBind < 0)
            {
                if (command->DepthBuffer != null) { sawDepthBind = true; depthBindsWindow++; }
                else uiBindsWindow++;
                if (command->DepthBuffer == null && sawDepthBind)
                {
                    sawDepthBind = false;
                    triggersWindow++;
                    SafeRenderFur((ID3D11Texture2D*)rt->D3D11Texture2D);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "SetTarget detour failed");
        }

        setTargetHook!.Original(self, command);
        try
        {
            var rtm = RenderTargetManager.Instance();
            var rt = command->RenderTargets[0].Value;
            var isBackBuffer = rtm != null && rt != null && rt == rtm->SwapChainBackBuffer;
            if (isBackBuffer) backBufferBinds++;
            lock (diagLock) trace?.Add($"{trace.Count}: rt0 {Describe(rt, rtm)} depth {Describe(command->DepthBuffer, rtm)}{(isBackBuffer ? $"  <= back buffer bind {backBufferBinds}" : "")}");
            if (isBackBuffer && !DrawAtPresent && config.BackBufferBind > 0 && backBufferBinds == config.BackBufferBind && !drewThisFrame)
            {
                drewThisFrame = true;
                SafeRenderFur((ID3D11Texture2D*)rt->D3D11Texture2D);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "SetTarget detour failed");
        }
    }

    private static string Describe(Texture* t, RenderTargetManager* rtm)
    {
        if (t == null) return "null";
        if (rtm != null && t == rtm->SwapChainBackBuffer) return "BackBuffer";
        if (rtm != null && t == rtm->ToneAdjustSource) return "ToneAdjustSource";
        if (rtm != null && t == rtm->DepthStencil) return "DepthStencil";
        return $"0x{(nint)t:X}({t->ActualWidth}x{t->ActualHeight})";
    }

    private void StartSelfTest()
    {
        testSaved = (config.BackBufferBind, config.UseSceneDepth, config.DebugSolid, config.Enabled);
        testCases = new();
        for (var bind = 0; bind <= Math.Max(lastFrameBinds, 1); bind++) { testCases.Add((bind, true)); testCases.Add((bind, false)); }
        testResults.Clear();
        testCase = -1;
        traceRequested = true;
        NextTestCase();
    }

    private void NextTestCase()
    {
        testCase++;
        testFrame = 0;
        if (testCase >= testCases!.Count)
        {
            (config.BackBufferBind, config.UseSceneDepth, config.DebugSolid, config.Enabled) = testSaved;
            log.Info("[selftest] magenta samples surviving to Present: {Results}", string.Join(" | ", testResults));
            status = "self-test done: " + string.Join(" | ", testResults);
            testCases = null;
            return;
        }
        var c = testCases[testCase];
        config.Enabled = true;
        config.DebugSolid = true;
        config.BackBufferBind = c.Bind;
        config.UseSceneDepth = c.Depth;
    }

    private void StepSelfTest()
    {
        if (++testFrame < 10) return; // let the setting take effect for a few frames
        var result = "?";
        if (renderer != null && interop.TryGetDevice(out _, out var ctx) && interop.TryGetBackBuffer(out var bb))
        {
            try { result = renderer.CountMagenta(ctx, bb).ToString(); }
            catch (Exception ex) { result = "error " + ex.Message; }
            finally { bb->Release(); }
        }
        var c = testCases![testCase];
        testResults.Add($"{(c.Bind == 0 ? "Present" : $"bind {c.Bind}")} depth {(c.Depth ? "on" : "off")}: {result}");
        NextTestCase();
    }

    // boundTarget: the D3D texture behind the game's bound kernel back buffer (hook mode), or null to use the swap chain's.
    private void SafeRenderFur(ID3D11Texture2D* boundTarget)
    {
        try
        {
            RenderFur(boundTarget);
        }
        catch (Exception ex)
        {
            // Never let a render failure repeat every frame: disable and report.
            config.Enabled = false;
            status = "disabled after error: " + ex.Message;
            log.Error(ex, "Fur rendering failed; disabled.");
        }
    }

    // Logs to dalamud.log only when a value changes, so per-frame state stays readable.
    private static byte[] ReadShader(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("embedded shader missing", name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void Diag(string key, string message)
    {
        lock (diagLock)
        {
            if (lastDiag.TryGetValue(key, out var previous) && previous == message) return;
            lastDiag[key] = message;
        }
        log.Info("[{Key}] {Message}", key, message);
    }

    // Runs before the game's Tick, so poses and camera are final for the frame the render thread draws next. Reading them live
    // from the render thread raced the main thread's animation update (one-frame T-poses, jitter, fur lagging the camera).
    private void OnFrameworkUpdate(IFramework _)
    {
        var snap = new FrameSnapshot { Gpose = FFXIVClientStructs.FFXIV.Client.Game.GameMain.IsInGPose() };
        Fill(snap);
        snapshot = snap;
    }

    private bool Fill(FrameSnapshot snap)
    {
        if (!config.Enabled) { snap.Skip = "disabled"; return false; }
        if (config.OnlyInGpose && !snap.Gpose) { snap.Skip = "waiting for GPose"; return false; }
        snap.Character = interop.GetTargetCharacterBase();
        if (snap.Character == 0) { snap.Skip = "no target character"; return false; }
        var slot = interop.GetCoatSlot(snap.Character);
        var male = interop.IsMale(snap.Character);
        if (slot == CoatSlot.None && config.RequireCoatEquipped) { snap.Skip = "coat not equipped"; return false; }
        snap.AssetName = $"coat_{(slot == CoatSlot.Ring ? "ril" : "glv")}_{(male ? "m" : "f")}.furasset";
        (snap.Race, snap.ModelRace) = (interop.GetRaceSexId(snap.Character), (ushort)(male ? 101 : 201));
        snap.LoadKey = $"{snap.AssetName}@c{snap.Race:D4}";
        if (snap.LoadKey != loadedAssetName)
        {
            snap.Parents = interop.GetBoneParents(snap.Character);
            (snap.Pbd, snap.PbdSource) = ReadPbd(interop.GetTargetObjectIndex());
        }
        if (!interop.TryGetCamera(out snap.View, out snap.Proj)) { snap.Skip = "camera unavailable"; return false; }
        Diag("camera", $"StandardZ-derived reverseZ {interop.LastCameraReverseZ}; proj row3 {snap.Proj.M31:G4} {snap.Proj.M32:G4} {snap.Proj.M33:G4} {snap.Proj.M34:G4}; row4 {snap.Proj.M41:G4} {snap.Proj.M42:G4} {snap.Proj.M43:G4} {snap.Proj.M44:G4}");
        // The asset (and its bone list) is loaded on the render thread; until it matches, the render thread waits a frame.
        var asset = renderer?.Asset;
        if (asset == null || snap.LoadKey != loadedAssetName) return true;
        var bones = asset.BoneNames.Length;
        var skin = new Matrix4x4[bones];
        if (!interop.TryGetSkinMatrices(snap.Character, asset.BoneNames, skin, found.AsSpan(0, bones))) { snap.Skip = "skeleton unavailable"; return false; }
        if (deform is { } d && d.Length == bones)
            for (var i = 0; i < bones; i++) skin[i] = d[i] * skin[i];
        var missing = 0;
        for (var i = 0; i < bones; i++) if (!found[i]) missing++;
        (snap.Skin, snap.Bones, snap.BonesInfo) = (skin, bones, $"bones missing {missing}/{bones}");
        return true;
    }

    private void RenderFur(ID3D11Texture2D* boundTarget)
    {
        if (!config.Enabled) { status = "disabled"; return; }
        var gpose = FFXIVClientStructs.FFXIV.Client.Game.GameMain.IsInGPose();
        if (config.OnlyInGpose && !gpose) { status = "waiting for GPose"; return; }
        if (!interop.TryGetDevice(out var device, out var context)) { status = "no D3D11 device"; return; }
        Diag("device", interop.DeviceInfo);
        if (renderer == null && rendererError == null)
        {
            try { renderer = new Renderer(device, "Fur.hlsl", ReadShader("Shaders.Fur.hlsl")); Diag("renderer", "initialised"); }
            catch (Exception ex) { rendererError = ex.Message; log.Error(ex, "Renderer init failed"); }
        }
        if (renderer == null) { status = "renderer init failed: " + rendererError; return; }

        // Bones and camera come from the main thread (see OnFrameworkUpdate): this runs on the render thread.
        var snap = snapshot;
        if (snap == null) { status = "waiting for first frame"; return; }
        if (snap.Skip != null) { status = snap.Skip; return; }
        if (snap.LoadKey != loadedAssetName && snap.Parents != null) LoadAsset(snap);
        var asset = renderer.Asset;
        if (asset == null) { status = "asset missing: " + assetError; return; }
        if (snap.Skin == null || snap.Bones != asset.BoneNames.Length) { status = "waiting for skeleton"; return; }
        var bones = snap.Bones;
        Diag("bones", snap.BonesInfo);
        var (view, proj, gpose2) = (snap.View, snap.Proj, snap.Gpose);
        gpose = gpose2;
        if (!interop.TryGetBackBuffer(out var backBuffer)) { status = "no back buffer"; return; }
        if (boundTarget != null)
        {
            Diag("target", boundTarget == backBuffer ? "bound kernel back buffer == swap chain buffer 0" : $"bound kernel back buffer 0x{(nint)boundTarget:X} != swap chain buffer 0 0x{(nint)backBuffer:X}; drawing into the bound one");
            backBuffer->Release();
            backBuffer = boundTarget;
            backBuffer->AddRef();
        }
        try
        {
            ID3D11ShaderResourceView* depth = null;
            uint dw = 0, dh = 0;
            if (config.UseSceneDepth) interop.TryGetSceneDepth(out depth, out dw, out dh);
            Diag("depth", config.UseSceneDepth ? interop.DepthInfo : "disabled in settings");
            var density = gpose ? config.GposeDensity : config.Density;
            var count = (int)(asset.StrandCount * Math.Clamp(density, 0f, 1f));
            renderer.Draw(context, backBuffer, depth, new Vector2(dw, dh), snap.Skin.AsSpan(0, bones), view, proj, count, gpose ? config.GposeChildCount : config.ChildCount, config);
            furDraws++;
            lastFurTarget = (nint)backBuffer;
            debugCapture.OnFurDrawn(context, backBuffer);
            Diag("draw", renderer.LastDrawInfo);
            status = $"drawing {count:N0}/{asset.StrandCount:N0} strands ({snap.AssetName}); {snap.BonesInfo}; depth {(depth != null ? $"{dw}x{dh}" : "off")}";
        }
        finally
        {
            backBuffer->Release();
        }
    }

    // Body mods (Rue, etc.) replace human.pbd through Penumbra per collection, and the game only swaps it in while building
    // the deformer, so ask Penumbra which file this character resolves it to.
    private (byte[]? Data, string Source) ReadPbd(int objectIndex)
    {
        const string gamePath = "chara/xls/boneDeformer/human.pbd";
        var resolved = gamePath;
        if (objectIndex >= 0)
        {
            try { resolved = pluginInterface.GetIpcSubscriber<string, int, string>("Penumbra.ResolveGameObjectPath").InvokeFunc(gamePath, objectIndex); }
            catch (Dalamud.Plugin.Ipc.Exceptions.IpcError) { } // Penumbra not loaded: the game uses the vanilla file
        }
        try
        {
            return Path.IsPathRooted(resolved) ? (File.ReadAllBytes(resolved), resolved) : (dataManager.GetFile(resolved)?.Data, resolved);
        }
        catch (IOException ex)
        {
            log.Warning(ex, "Failed to read {Path}", resolved);
            return (dataManager.GetFile(gamePath)?.Data, gamePath + " (fallback)");
        }
    }

    private void LoadAsset(FrameSnapshot snap)
    {
        var name = snap.AssetName;
        loadedAssetName = snap.LoadKey;
        assetError = null;
        try
        {
            var asset = FurAsset.Load(Path.Combine(AssetDirectory, name));
            var pbd = snap.Pbd;
            var chain = pbd == null ? null : RaceDeformer.Chain(pbd, snap.ModelRace, snap.Race);
            deform = chain is { Count: > 0 } ? RaceDeformer.PerBone(asset.BoneNames, chain, snap.Parents!) : null;
            renderer!.SetAsset(asset);
            Diag("asset", $"loaded {name}: {asset.StrandCount:N0} strands, {asset.BoneNames.Length} bones; race c{snap.ModelRace:D4} -> c{snap.Race:D4}: " +
                (pbd == null ? "human.pbd unavailable" : chain == null ? "no deformer chain" : $"{chain.Count} deformer step(s)") + $" from {snap.PbdSource}");
        }
        catch (Exception ex)
        {
            renderer!.SetAsset(null);
            assetError = $"{name}: {ex.Message}";
            Diag("asset", "load failed " + assetError);
            log.Warning(ex, "Failed to load fur asset {Name}", name);
        }
    }

    private void DrawWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(420, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Fur Renderer", ref windowOpen)) { ImGui.End(); return; }
        var changed = false;
        ImGui.TextWrapped(status);
        ImGui.Separator();

        changed |= Check("Enabled", () => config.Enabled, v => config.Enabled = v);
        changed |= Check("Only in GPose", () => config.OnlyInGpose, v => config.OnlyInGpose = v);
        changed |= Check("Require coat equipped", () => config.RequireCoatEquipped, v => config.RequireCoatEquipped = v);
        if (ImGui.CollapsingHeader("Strands", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= Slider("Density", () => config.Density, v => config.Density = v, 0f, 1f);
            var children = config.ChildCount;
            if (ImGui.SliderInt("Children per strand", ref children, 1, 16)) { config.ChildCount = children; changed = true; }
            var gposeChildren = config.GposeChildCount;
            if (ImGui.SliderInt("Children per strand (GPose)", ref gposeChildren, 1, 16)) { config.GposeChildCount = gposeChildren; changed = true; }
            changed |= Slider("Child spread (m)", () => config.ChildSpread, v => config.ChildSpread = v, 0f, 0.01f);
            changed |= Slider("GPose density", () => config.GposeDensity, v => config.GposeDensity = v, 0f, 1f);
            changed |= Slider("Length", () => config.LengthScale, v => config.LengthScale = v, 0.2f, 3f);
            changed |= Slider("Width", () => config.WidthScale, v => config.WidthScale = v, 0.2f, 5f);
            changed |= Slider("Min pixel width", () => config.MinPixelWidth, v => config.MinPixelWidth = v, 0.25f, 3f);
            changed |= Slider("Opacity", () => config.Opacity, v => config.Opacity = v, 0f, 1f);
            changed |= Slider("Droop", () => config.Gravity, v => config.Gravity = v, 0f, 1.5f);
            changed |= Slider("Tip fade start", () => config.FadeStart, v => config.FadeStart = v, 0f, 0.95f);
        }
        if (ImGui.CollapsingHeader("Shading", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= Color("Base colour (linear)", () => config.BaseColor, v => config.BaseColor = v);
            changed |= Slider("Root brightness", () => config.RootDarken, v => config.RootDarken = v, 0f, 1f);
            changed |= Slider("Per-strand variation", () => config.HueJitter, v => config.HueJitter = v, 0f, 0.5f);
            changed |= Color("Ambient", () => config.AmbientColor, v => config.AmbientColor = v);
            var dir = config.LightDirection;
            if (ImGui.SliderFloat3("Light direction", ref dir, -1f, 1f)) { config.LightDirection = dir; changed = true; }
            changed |= Slider("Light intensity", () => config.LightIntensity, v => config.LightIntensity = v, 0f, 3f);
            changed |= Slider("Scene lighting", () => config.SceneLighting, v => config.SceneLighting = v, 0f, 1f);
            changed |= Slider("Scene brightness", () => config.SceneGain, v => config.SceneGain = v, 0.5f, 10f);
            changed |= Slider("Exposure", () => config.Exposure, v => config.Exposure = v, 0.1f, 8f);
            changed |= Slider("Depth absorption", () => config.Absorption, v => config.Absorption = v, 0f, 6f);
            changed |= Slider("Looking-in depth", () => config.FacingDepth, v => config.FacingDepth = v, 0f, 10f);
            changed |= Slider("AgX tone map", () => config.AgxToneMap, v => config.AgxToneMap = v, 0f, 1f);
            changed |= Slider("Soft lighting", () => config.SurfaceLighting, v => config.SurfaceLighting = v, 0f, 1f);
            changed |= Slider("Specular", () => config.Specular, v => config.Specular = v, 0f, 1f);
            changed |= Slider("Output gamma", () => config.OutputGamma, v => config.OutputGamma = v, 1f, 3f);
        }
        if (ImGui.CollapsingHeader("Depth / debug"))
        {
            var bind = config.BackBufferBind;
            if (ImGui.SliderInt("Draw point (-1 auto, 0 over UI, N = bind N)", ref bind, -1, 8)) { config.BackBufferBind = bind; changed = true; }
            if (ImGui.Button("Log one frame's render targets")) traceRequested = true;
            ImGui.SameLine();
            if (ImGui.Button("Run injection self-test") && testCases == null) StartSelfTest();
            changed |= Check("Occlude with scene depth", () => config.UseSceneDepth, v => config.UseSceneDepth = v);
            changed |= Slider("Depth bias (m)", () => config.DepthBiasMeters, v => config.DepthBiasMeters = v, 0f, 0.1f);
            changed |= Slider("Depth UV scale (0 = auto)", () => config.DepthUvScaleOverride, v => config.DepthUvScaleOverride = v, 0f, 2f);
            changed |= Check("Debug: solid magenta", () => config.DebugSolid, v => config.DebugSolid = v);
            changed |= Check("Temporal dither", () => config.TemporalDither, v => config.TemporalDither = v);
            if (ImGui.Button("Reload asset")) loadedAssetName = null;
            ImGui.SameLine();
            if (ImGui.Button("Reset settings")) { CopyDefaults(); changed = true; }
        }
        if (changed) pluginInterface.SavePluginConfig(config);
        ImGui.End();
    }

    private void CopyDefaults()
    {
        var d = new Configuration();
        foreach (var p in typeof(Configuration).GetProperties()) if (p.CanWrite) p.SetValue(config, p.GetValue(d));
    }

    private static bool Check(string label, Func<bool> get, Action<bool> set)
    {
        var v = get();
        if (!ImGui.Checkbox(label, ref v)) return false;
        set(v);
        return true;
    }

    private static bool Slider(string label, Func<float> get, Action<float> set, float min, float max)
    {
        var v = get();
        if (!ImGui.SliderFloat(label, ref v, min, max)) return false;
        set(v);
        return true;
    }

    private static bool Color(string label, Func<Vector3> get, Action<Vector3> set)
    {
        var v = get();
        if (!ImGui.ColorEdit3(label, ref v, ImGuiColorEditFlags.Float)) return false;
        set(v);
        return true;
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= OnDraw;
        framework.Update -= OnFrameworkUpdate;
        setTargetHook?.Dispose();
        commandManager.RemoveHandler(CommandName);
        renderer?.Dispose();
        (interop as IDisposable)?.Dispose();
    }
}
