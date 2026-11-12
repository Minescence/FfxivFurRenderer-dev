using System.Collections.Concurrent;
using System.Globalization;
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
    private readonly IDataManager dataManager;
    private readonly bool[] found = new bool[Renderer.MaxBones];
    private readonly Dictionary<string, bool> bakedAssets = []; // asset file name -> exists; releases ship none, so not checked per frame
    private volatile FrameSnapshot? snapshot;
    private readonly Dictionary<string, FurItem> items = []; // by model, enabled submeshes and wearer race; main thread
    private readonly List<FurItem> retiring = [];
    private readonly ConcurrentQueue<FurItem> retiredItems = new();
    private readonly List<nint> replacedTextures = [];
    private readonly ConcurrentQueue<nint> releasedTextures = new(); // material textures the render thread releases, like retiredItems
    private bool reloadItems;
    private int frame;
    private float lodFocal; // render thread: the projection's vertical focal length outside GPose

    private const int RetireAfterFrames = 300;
    private const uint SamplerIndex = 0x565F8FD8, SamplerDiffuse = 0x115306BE; // CRC32 of the shader names g_SamplerIndex, g_SamplerDiffuse

    /// <summary>A fur-bearing model: growing, or ready to draw.</summary>
    private sealed class FurItem
    {
        public required string Name;
        public Task<FurAsset?>? Growth;
        public Matrix4x4[]? Deform; // race deformer per bone; set by the growth task
        public string[] BoneNames = [];
        public readonly BoneLookup Lookup = new();
        public int LastUsed;
        public bool Recolor = true; // 'Recolour undyed items' applies: the player's own items, not others' (they look as the game colours them)
        // The fur materials' live colour tables, index and diffuse maps (SRVs, laid out as FurDraw.Textures), AddRef'd so a
        // replaced one's address can't be reused while it is held: a pointer change is always a new texture. Main thread; the
        // render thread releases them.
        public readonly nint[] Textures = new nint[Renderer.MaterialTextures];
        public readonly nint[] LoggedTextures = new nint[Renderer.MaterialTextures]; // render thread
        public volatile FurAsset? Upload; // held for the render thread, then dropped: the strands stay on the GPU only
        // Buffers the growth task made (Renderer.CreateBuffers is free-threaded); Retired once the render thread released the item.
        public Renderer.FurBuffers? Prepared;
        public Renderer.FurBuffers? Buffers; // render thread
    }

    private static readonly Renderer.FurBuffers Retired = new();

    private readonly record struct ItemDraw(FurItem Item, Matrix4x4[] Skin, nint[] Textures, Vector3? Position);

    /// <summary>Main-thread state for one frame, consumed by the render thread.</summary>
    private sealed class FrameSnapshot
    {
        public string? Skip;
        public bool Gpose;
        public readonly List<ItemDraw> Items = [];
        public int Growing, Bones, MissingBones;
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
    // Hook.Original throws once the hook is disposed, which took the game down on a reload with the render thread in the detour.
    private readonly DoSetTargetDelegate? originalSetTarget;
    private volatile bool disposing;
    private int detoursRunning;
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

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IGameInteropProvider hooks, IPluginLog log, IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;
        this.dataManager = dataManager;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Each step resets what changed meaning or tuning in that version, leaving out what a later step resets again.
        if (config.Version < 2) { config.BackBufferBind = -1; config.Version = 2; } // v1 defaults drew over the UI
        if (config.Version < 4) { config.Density = Defaults.Density; config.Version = 4; } // look matched to the reference render (full density)
        if (config.Version < 5) // look-dev pass against the reference render (absorption, sheen, children)
        {
            (config.HueJitter, config.SurfaceLighting, config.Exposure) = (Defaults.HueJitter, Defaults.SurfaceLighting, Defaults.Exposure);
            config.Version = 5;
        }
        if (config.Version < 7) // colour fit to the reference renders, with the undercoat under the fur
        {
            (config.BaseColor, config.RootDarken, config.Absorption, config.UndercoatDarken) = (Defaults.BaseColor, Defaults.RootDarken, Defaults.Absorption, Defaults.UndercoatDarken);
            config.Version = 7;
        }
        // Without the game's lighting the fur had no shadows; gain 4 clipped sunlit fur to white.
        if (config.Version < 8) { config.SceneLighting = 1f; config.SceneGain = 1.5f; config.Version = 8; }
        if (config.Version < 9) { config.Specular = 0.05f; config.Version = 9; } // white sheen frosted the tips and greyed dark colours
        if (config.Version < 10) // strand defaults are now the user's in-game tuning against the reference
        {
            (config.ChildCount, config.ChildSpread, config.LengthScale, config.WidthScale, config.MinPixelWidth, config.Gravity) =
                (Defaults.ChildCount, Defaults.ChildSpread, Defaults.LengthScale, Defaults.WidthScale, Defaults.MinPixelWidth, Defaults.Gravity);
            config.Version = 10;
        }
        // 6 (the reference fit) turned every camera-facing stretch of fur into deep crimson bands and spots.
        if (config.Version < 11) { config.FacingDepth = 2f; config.Version = 11; }
        interop = new GameInterop(log);
        debugCapture = new DebugCapture(pluginInterface.GetPluginConfigDirectory(), config, interop.SetCamera, () => traceRequested = true);
        try
        {
            setTargetHook = hooks.HookFromAddress<DoSetTargetDelegate>(ImmediateContext.Addresses.DoSetTargetCommand.Value, SetTargetDetour);
            originalSetTarget = setTargetHook.Original;
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
        lastFrameInfo = $"frame: binds {backBufferBinds}, fur draws {furDraws}, last fur target 0x{lastFurTarget:X}; {renderer?.PerfInfo}";
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
        // Poses and camera for the frame the render thread draws next, taken after the game's Tick. Reading them live on the
        // render thread raced the animation update (one-frame T-poses). Taking them at Framework.Update raced the render thread
        // itself, which starts on the previous Tick's frame as Present returns: about one frame in nine drew with the camera
        // of the frame before, and with the camera moving towards the coat (ground collision) the whole fur failed the depth test.
        var snap = new FrameSnapshot { Gpose = FFXIVClientStructs.FFXIV.Client.Game.GameMain.IsInGPose() };
        frame++;
        foreach (var (key, item) in items)
        {
            if (!reloadItems && frame - item.LastUsed < RetireAfterFrames) continue;
            items.Remove(key);
            retiring.Add(item);
        }
        if (reloadItems) bakedAssets.Clear();
        reloadItems = false;
        // Dalamud stops calling a draw handler that throws, which would freeze the fur on its last pose.
        try { Fill(snap); }
        catch (Exception ex)
        {
            snap.Items.Clear();
            snap.Skip = "fur update failed: " + ex.Message;
            Diag("fill", ex.ToString());
        }
        snapshot = snap;
        // Only now: the render thread frees these before it reads the snapshot, so it can't still be drawing them.
        foreach (var item in retiring) retiredItems.Enqueue(item);
        retiring.Clear();
        foreach (var texture in replacedTextures) releasedTextures.Enqueue(texture);
        replacedTextures.Clear();
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
        Interlocked.Increment(ref detoursRunning);
        try
        {
            if (disposing) originalSetTarget?.Invoke(self, command);
            else DrawAroundSetTarget(self, command);
        }
        finally
        {
            Interlocked.Decrement(ref detoursRunning);
        }
    }

    private void DrawAroundSetTarget(ImmediateContext* self, RenderCommandSetTarget* command)
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

        originalSetTarget?.Invoke(self, command);
        try
        {
            var rtm = RenderTargetManager.Instance();
            var rt = command->RenderTargets[0].Value;
            var isBackBuffer = rtm != null && rt != null && rt == rtm->SwapChainBackBuffer;
            if (isBackBuffer) backBufferBinds++;
            if (trace != null) lock (diagLock) trace?.Add($"{trace.Count}: rt0 {Describe(rt, rtm)} depth {Describe(command->DepthBuffer, rtm)}{(isBackBuffer ? $"  <= back buffer bind {backBufferBinds}" : "")}");
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

    private static byte[] ReadShader(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("embedded shader missing", name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // Logs to dalamud.log only when a value changes, so per-frame state stays readable.
    private void Diag(string key, string message)
    {
        lock (diagLock)
        {
            if (lastDiag.TryGetValue(key, out var previous) && previous == message) return;
            lastDiag[key] = message;
        }
        log.Info("[{Key}] {Message}", key, message);
    }

    private void Fill(FrameSnapshot snap)
    {
        if (!config.Enabled) { snap.Skip = "disabled"; return; }
        if (config.OnlyInGpose && !snap.Gpose) { snap.Skip = "waiting for GPose"; return; }
        var character = interop.GetTargetCharacterBase();
        if (character == 0) { snap.Skip = "no target character"; return; }
        if (!interop.TryGetCamera(out snap.View, out snap.Proj)) { snap.Skip = "camera unavailable"; return; }
        Diag("camera", $"StandardZ-derived reverseZ {interop.LastCameraReverseZ}; proj row3 {snap.Proj.M31:G4} {snap.Proj.M32:G4} {snap.Proj.M33:G4} {snap.Proj.M34:G4}; row4 {snap.Proj.M41:G4} {snap.Proj.M42:G4} {snap.Proj.M43:G4} {snap.Proj.M44:G4}");
        AddCharacter(snap, character, interop.GetTargetObjectIndex(), "");
        // Others' items by object index, not draw object: a sync plugin applying someone's mods redraws them.
        if (config.OtherPlayers)
            foreach (var (other, index) in interop.GetOtherHumans())
                AddCharacter(snap, other, index, $"#{index}");
        foreach (var prop in interop.GetProps())
            AddFurModels(snap, prop, -1, 0); // props have their own skeletons: no race deformer
        if (snap.Items.Count == 0 && snap.Growing == 0) snap.Skip = "no fur items";
    }

    // owner: "" for the target, whose items are kept by race so its GPose copy reuses them; else the suffix of the character's own items.
    private void AddCharacter(FrameSnapshot snap, nint character, int objectIndex, string owner)
    {
        var race = interop.GetRaceSexId(character);
        var slot = interop.GetCoatSlot(character);
        // Without Hikari's textures the coat's model ids are the unmodded item, as on players no sync plugin applies the mod to.
        if (slot != CoatSlot.None && interop.FindFurModels(character, coatSlots: true) is [var worn, ..])
        {
            var male = interop.IsMale(character);
            var name = $"coat_{(slot == CoatSlot.Ring ? "ril" : "glv")}_{(male ? "m" : "f")}.furasset";
            var key = $"{name}@c{race:D4}";
            // Baked assets hold the mod's mesh, so releases can't ship them: there the coat grows from the worn model like any other item.
            // So do others' coats: theirs is the mod as their sync plugin applies it, maybe two-tone, and baked strands take one colour.
            if (!bakedAssets.TryGetValue(name, out var baked)) bakedAssets[name] = baked = File.Exists(Path.Combine(AssetDirectory, name));
            if (owner == "" && (items.TryGetValue(key, out var coat) || baked))
            {
                coat ??= items[key] = StartItem(character, objectIndex, (ushort)(male ? 101 : 201), race, name, () => FurAsset.Load(Path.Combine(AssetDirectory, name)));
                // Baked strands are all coloured by the worn coat's first fur material.
                AddDraw(snap, character, coat, worn.Slot, worn.FurMaterials);
            }
            else AddFurModels(snap, character, objectIndex, race, owner, coatSlots: true);
        }
        AddFurModels(snap, character, objectIndex, race, owner);
    }

    private void AddFurModels(FrameSnapshot snap, nint character, int objectIndex, ushort race, string owner = "", bool coatSlots = false)
    {
        foreach (var fur in interop.FindFurModels(character, coatSlots))
        {
            // Props by actor: two with the same minion have their own bones and colour tables.
            var key = $"{fur.Path}|{fur.FurMaterials:X}|{fur.EnabledAttributes:X}@{(race != 0 ? race : character):X}{owner}";
            if (!items.TryGetValue(key, out var item))
            {
                var modelRace = race != 0 ? ModelRace(fur.Path, objectIndex, race) : race;
                items[key] = item = StartItem(character, objectIndex, modelRace, race, Path.GetFileName(fur.Path) + owner,
                    () => FurGrower.Grow(ReadModel(fur.Path), fur.FurMaterials, fur.EnabledAttributes));
                item.Recolor = owner == "";
            }
            AddDraw(snap, character, item, fur.Slot, fur.FurMaterials);
        }
    }

    // The race a model was made for, from the name of the game path it was loaded for. Under Penumbra a model is a mod's file, and
    // sync plugins (Lightless, Mare) name theirs by hash, so Penumbra's resource tree (EQDP-resolved) gives that path. Without an
    // answer the file's own name is the guess.
    private ushort ModelRace(string path, int objectIndex, ushort race)
    {
        var name = Path.GetFileName(path);
        if (Path.IsPathRooted(path) && objectIndex is >= 0 and <= ushort.MaxValue)
        {
            try
            {
                var trees = pluginInterface.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5")
                    .InvokeFunc([(ushort)objectIndex]);
                var normal = path.Replace('\\', '/');
                if (trees is [{ } files]
                    && files.FirstOrDefault(f => string.Equals(f.Key.Replace('\\', '/'), normal, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault() is { } gamePath)
                    name = Path.GetFileName(gamePath);
            }
            catch (Dalamud.Plugin.Ipc.Exceptions.IpcError) { } // Penumbra not loaded
        }
        var named = System.Text.RegularExpressions.Regex.Match(name, @"^c([0-9]{4})");
        return named.Success ? ushort.Parse(named.Groups[1].Value) : race;
    }

    private byte[] ReadModel(string path)
        => Path.IsPathRooted(path) ? File.ReadAllBytes(path)
            : dataManager.GetFile(path)?.Data ?? throw new FileNotFoundException("model not in the game data", path);

    // Growth runs off the game's threads (under a second); its deformer inputs are game state, so they are read here.
    private FurItem StartItem(nint character, int objectIndex, ushort modelRace, ushort race, string name, Func<FurAsset?> build)
    {
        var parents = modelRace != race ? interop.GetBoneParents(character) : null;
        byte[]? pbd = null;
        var pbdSource = "";
        if (parents != null) (pbd, pbdSource) = ReadPbd(objectIndex);
        var item = new FurItem { Name = name, LastUsed = frame };
        item.Growth = Task.Run(() =>
        {
            try
            {
                var asset = build();
                // Off the render thread: making a coat's buffers held it up to 18 ms.
                if (asset != null && renderer is { FreeThreaded: true } r)
                {
                    var made = r.CreateBuffers(asset);
                    if (Interlocked.CompareExchange(ref item.Prepared, made, null) != null) made.Dispose();
                }
                if (asset == null)
                {
                    Diag("item " + name, "skipped: no fur surface");
                    return null;
                }
                if (asset.BoneNames.Length > Renderer.MaxBones) throw new InvalidDataException($"{asset.BoneNames.Length} bones; max {Renderer.MaxBones}");
                var chain = pbd == null ? null : RaceDeformer.Chain(pbd, modelRace, race);
                if (chain is { Count: > 0 } && parents != null) item.Deform = RaceDeformer.PerBone(asset.BoneNames, chain, parents);
                Diag("item " + name, $"{asset.StrandCount:N0} strands, {asset.BoneNames.Length} bones; race c{modelRace:D4} -> c{race:D4}: " +
                    (parents == null ? "none needed" : pbd == null ? "human.pbd unavailable" : chain == null ? "no deformer chain" : $"{chain.Count} deformer step(s)") + $" {pbdSource}");
                return asset;
            }
            catch (Exception ex)
            {
                Diag("item " + name, "failed: " + ex.Message);
                log.Warning(ex, "Failed to prepare fur for {Name}", name);
                return null;
            }
        });
        return item;
    }

    // Nothing while the item is growing or has nothing to draw. furMaterials: the item's fur materials in the slot's model.
    private void AddDraw(FrameSnapshot snap, nint character, FurItem item, int slot, uint furMaterials)
    {
        item.LastUsed = frame;
        if (item.Growth is { } growth)
        {
            if (!growth.IsCompleted) { snap.Growing++; return; }
            item.Growth = null;
            if (growth.Result is { } asset) (item.BoneNames, item.Upload) = (asset.BoneNames, Volatile.Read(ref item.Prepared) == null ? asset : null);
        }
        var bones = item.BoneNames.Length;
        if (bones == 0) return;
        var skin = new Matrix4x4[bones];
        if (!interop.TryGetSkinMatrices(character, item.Lookup, item.BoneNames, skin, found.AsSpan(0, bones))) return;
        if (item.Deform is { } d)
            for (var i = 0; i < bones; i++) skin[i] = d[i] * skin[i];
        var missing = 0;
        for (var i = 0; i < bones; i++) if (!found[i]) missing++;
        if (missing > 0) Diag("bones " + item.Name, $"missing {missing}/{bones}");
        snap.Bones += bones;
        snap.MissingBones += missing;
        // The k-th textures belong to the k-th set bit of furMaterials: the rank FurGrower gives each strand.
        var material = -1;
        for (var k = 0; k < Renderer.MaxMaterials; k++)
        {
            do material++; while (material < 32 && (furMaterials >> material & 1) == 0);
            for (var kind = 0; kind < 3; kind++)
            {
                var texture = material >= 32 ? 0 : kind switch
                {
                    0 => (nint)interop.GetColorTable(character, slot, material),
                    1 => (nint)interop.GetMaterialTexture(character, slot, material, SamplerIndex),
                    _ => (nint)interop.GetMaterialTexture(character, slot, material, SamplerDiffuse),
                };
                var at = kind * Renderer.MaxMaterials + k;
                if (texture == item.Textures[at]) continue;
                if (texture != 0) ((ID3D11ShaderResourceView*)texture)->AddRef();
                if (item.Textures[at] != 0) replacedTextures.Add(item.Textures[at]);
                item.Textures[at] = texture;
            }
        }
        snap.Items.Add(new ItemDraw(item, skin, (nint[])item.Textures.Clone(), interop.GetRootPosition(character)));
    }

    private static void ReleaseTextures(FurItem item)
    {
        for (var k = 0; k < item.Textures.Length; k++)
        {
            if (item.Textures[k] != 0) ((ID3D11ShaderResourceView*)item.Textures[k])->Release();
            item.Textures[k] = 0;
        }
    }

    // Render thread, or Dispose once it has stopped: what the main thread handed over for release.
    private void ReleaseRetired()
    {
        while (retiredItems.TryDequeue(out var retired))
        {
            retired.Buffers?.Dispose();
            if (Interlocked.Exchange(ref retired.Prepared, Retired) is { } prepared && prepared != Retired) prepared.Dispose();
            ReleaseTextures(retired);
        }
        while (releasedTextures.TryDequeue(out var texture)) ((ID3D11ShaderResourceView*)texture)->Release();
    }

    private void RenderFur(ID3D11Texture2D* boundTarget)
    {
        ReleaseRetired(); // before reading the snapshot: see OnDraw
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

        // Bones and camera come from the main thread (see OnDraw): this runs on the render thread.
        var snap = snapshot;
        if (snap == null) { status = "waiting for first frame"; return; }
        if (snap.Skip != null) { status = snap.Skip; return; }
        var draws = new List<FurDraw>(snap.Items.Count);
        int strands = 0, drawnStrands = 0;
        var density = Math.Clamp(snap.Gpose ? config.GposeDensity : config.Density, 0f, 1f);
        Matrix4x4.Invert(snap.View, out var invView);
        // Distances as at the normal field of view: GPose zoom (a narrower one) makes items bigger on screen.
        if (!snap.Gpose) lodFocal = snap.Proj.M22;
        var zoom = snap.Gpose && lodFocal > 0 ? snap.Proj.M22 / lodFocal : 1f;
        foreach (var (item, skin, textures, position) in snap.Items)
        {
            for (var k = 0; k < textures.Length; k++)
            {
                if (textures[k] == item.LoggedTextures[k]) continue;
                item.LoggedTextures[k] = textures[k];
                var (kind, rank) = Math.DivRem(k, Renderer.MaxMaterials);
                var key = $"colour {item.Name} {rank} {kind}";
                if (kind == 0 && textures[k] != 0)
                    renderer.ReadTableDiffuse(context, (ID3D11ShaderResourceView*)textures[k], diffuse => Diag(key, $"table row 0 {diffuse?.ToString() ?? "unreadable"}"));
                else
                    Diag(key, textures[k] == 0 ? kind == 0 ? "no table: fur colour" : $"no {(kind == 1 ? "index" : "diffuse")} map"
                        : $"{(kind == 1 ? "index" : "diffuse")} map {Renderer.Describe((ID3D11ShaderResourceView*)textures[k])}");
            }
            if (item.Buffers == null && Volatile.Read(ref item.Prepared) is { } prepared && prepared != Retired) (item.Buffers, item.Prepared) = (prepared, Retired);
            if (item.Upload is { } asset)
            {
                item.Upload = null;
                try { item.Buffers = renderer.CreateBuffers(asset); }
                catch (InvalidOperationException ex) { Diag("item " + item.Name, "failed: " + ex.Message); }
            }
            if (item.Buffers is not { } buffers) continue;
            var itemDensity = density;
            if (config.DistanceLod && position is { } p)
            {
                var distance = Vector3.Distance(invView.Translation, p) / zoom;
                if (distance > config.LodDistance) itemDensity *= Math.Max(config.LodMinDensity, MathF.Pow(config.LodDistance / distance, config.LodBias));
            }
            draws.Add(new FurDraw(buffers, skin, textures, itemDensity, config.RecolorUndyed && item.Recolor));
            strands += buffers.StrandCount;
            drawnStrands += (int)(buffers.StrandCount * itemDensity);
        }
        if (draws.Count == 0) { status = snap.Growing > 0 ? "growing fur" : "waiting for fur"; return; }
        var (view, proj) = (snap.View, snap.Proj);
        gpose = snap.Gpose;
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
            var albedo = interop.GetSceneAlbedo();
            Diag("albedo", albedo != null ? "GBuffer 2" : "no GBuffer 2: scene light also taken from surfaces over the items");
            renderer.Draw(context, backBuffer, depth, albedo, new Vector2(dw, dh), draws, view, proj, gpose ? config.GposeChildCount : config.ChildCount, config);
            furDraws++;
            lastFurTarget = (nint)backBuffer;
            ID3D11Resource* depthTexture = null;
            if (depth != null) depth->GetResource(&depthTexture);
            debugCapture.OnFurDrawn(context, backBuffer, (ID3D11Texture2D*)depthTexture);
            if (depthTexture != null) depthTexture->Release();
            Diag("draw", renderer.LastDrawInfo);
            status = $"drawing {drawnStrands:N0}/{strands:N0} strands in {draws.Count} item(s){(snap.Growing > 0 ? $", {snap.Growing} growing" : "")}; bones missing {snap.MissingBones}/{snap.Bones}; depth {(depth != null ? $"{dw}x{dh}" : "off")}";
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

    private void DrawWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(420, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Fur Renderer", ref windowOpen)) { ImGui.End(); return; }
        var changed = false;
        ImGui.TextWrapped(status);
        ImGui.BeginDisabled(); // greyed; TextUnformatted because these hold '%'
        if (renderer is { PerfInfo: { Length: > 0 } perf }) ImGui.TextUnformatted(perf);
        ImGui.TextUnformatted("Ctrl+click a slider to type a value, right-click a setting to reset it, hover for what it means.");
        ImGui.EndDisabled();
        ImGui.Separator();

        var d = Defaults;
        changed |= Check("Enabled", () => config.Enabled, v => config.Enabled = v, d.Enabled, "Turn the fur on or off.");
        changed |= Check("Other players", () => config.OtherPlayers, v => config.OtherPlayers = v, d.OtherPlayers,
            "Also grow fur on other characters around you and on GPose actors. They only wear a fur mod on your screen when a sync plugin (Lightless, Mare, Snowcloak) applies their mods. Each coat costs about as much as your own (GPU time and ~230 MB of video memory).");

        if (ImGui.CollapsingHeader("Performance", ImGuiTreeNodeFlags.DefaultOpen))
        {
            const string density = " Higher: thicker, fuller fur, costs more. Lower: sparser fur, faster.";
            changed |= Slider("Density", () => config.Density, v => config.Density = v, d.Density, 0f, 1f,
                "How many of the coat's strands are drawn in normal play." + density, 0, "%", 100f);
            changed |= SliderInt("Strands per root", () => config.ChildCount, v => config.ChildCount = v, d.ChildCount, 1, 16,
                "Each grown strand is drawn this many times, the extra ones placed around it. Higher: denser fur, costs about that many times more. 1 = no extras.");
            ImGui.BeginDisabled();
            ImGui.TextUnformatted($"Up close: {BaseStrandsPerCm2 * config.Density * config.ChildCount:F0} strands per cm².");
            ImGui.EndDisabled();
            changed |= Check("Fewer strands far away", () => config.DistanceLod, v => config.DistanceLod = v, d.DistanceLod,
                "Draw fewer but wider strands on far-away characters. Looks about the same from a distance and saves a lot of GPU time with many coats around.");
            changed |= Slider("Full detail within", () => config.LodDistance, v => config.LodDistance = v, d.LodDistance, 0.5f, 20f,
                "Characters closer than this always get every strand. Higher: full detail further out, costs more. Lower: saves sooner.", 1, " m");
            changed |= Slider("Thin-out speed", () => config.LodBias, v => config.LodBias = v, d.LodBias, 0.25f, 3f,
                "How quickly strands thin out beyond that distance. Higher: faster, but far fur gets clumpy. Lower: keeps more strands, costs more. 1 keeps the look about the same.");
            changed |= Slider("Least density far away", () => config.LodMinDensity, v => config.LodMinDensity = v, d.LodMinDensity, 0.05f, 1f,
                "Never thin out below this share of the density, however far away. Higher: far fur stays fuller. Lower: cheaper crowds.", 0, "%", 100f);
            changed |= Check("Skip hidden strands", () => config.CullHiddenStrands, v => config.CullHiddenStrands = v, d.CullHiddenStrands,
                "Don't draw strands you can't see (off screen, behind walls or behind other fur). Looks the same and roughly halves the cost; only turn off to troubleshoot.");
            changed |= Check("8x anti-aliasing", () => config.Msaa8, v => config.Msaa8 = v, d.Msaa8,
                "Smoother strand edges and see-through tips. Costs about 1.2 ms per frame more at 1440p; the difference is small.");
            changed |= Slider("Temporal smoothing", () => config.TemporalSmoothing, v => config.TemporalSmoothing = v, d.TemporalSmoothing, 0f, 0.9f,
                "Blends each frame with the previous ones so thin strands and tips don't flicker. Higher: calmer but softer, can smear in fast motion. Lower: sharper but more shimmer. 0 = off.", 0, "%", 100f);
        }

        if (ImGui.CollapsingHeader("GPose", ImGuiTreeNodeFlags.DefaultOpen))
        {
            const string density = " Higher: thicker, fuller fur, costs more. Lower: sparser fur, faster.";
            changed |= Check("Only in GPose", () => config.OnlyInGpose, v => config.OnlyInGpose = v, d.OnlyInGpose,
                "Show fur only in GPose. Saves all of its cost during normal play.");
            changed |= Slider("Density (GPose)", () => config.GposeDensity, v => config.GposeDensity = v, d.GposeDensity, 0f, 1f,
                "How many of the coat's strands are drawn in GPose, where you can afford more for screenshots." + density, 0, "%", 100f);
            changed |= SliderInt("Strands per root (GPose)", () => config.GposeChildCount, v => config.GposeChildCount = v, d.GposeChildCount, 1, 16,
                "The same as 'Strands per root', used in GPose. Higher: denser fur, costs about that many times more.");
            ImGui.BeginDisabled();
            ImGui.TextUnformatted($"Up close: {BaseStrandsPerCm2 * config.GposeDensity * config.GposeChildCount:F0} strands per cm² in GPose.");
            ImGui.EndDisabled();
        }

        if (ImGui.CollapsingHeader("Strand shape", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= Slider("Length", () => config.LengthScale, v => config.LengthScale = v, d.LengthScale, 0.2f, 3f,
                "Higher: longer, shaggier fur. Lower: short, velvety fur. 1x = 2.5 to 5 cm (half that on the head and neck).", 2, "x");
            changed |= Slider("Thickness", () => config.WidthScale, v => config.WidthScale = v, d.WidthScale, 0.2f, 5f,
                "Higher: thicker strands, fuller and more solid fur. Lower: finer, wispier strands.", 2, "x");
            changed |= Slider("Least thickness on screen", () => config.MinPixelWidth, v => config.MinPixelWidth = v, d.MinPixelWidth, 0.25f, 3f,
                "Strands thinner than this on screen are drawn this thick but fainter, so far fur doesn't shimmer. Higher: calmer but softer from afar. Lower: crisper but more shimmer.", 2, " px");
            changed |= Slider("Opacity", () => config.Opacity, v => config.Opacity = v, d.Opacity, 0f, 1f,
                "Higher: solid fur you can't see through. Lower: see-through, airy fur.", 0, "%", 100f);
            changed |= Slider("Droop", () => config.Gravity, v => config.Gravity = v, d.Gravity, 0f, 1.5f,
                "How much strands hang down. Higher: heavy, flowing fur. 0: strands stand straight out from the surface.");
            changed |= Slider("Tip taper", () => config.FadeStart, v => config.FadeStart = v, d.FadeStart, 0f, 0.95f,
                "Where along the strand it starts to narrow towards its tip. Higher: blunt, full-width strands. Lower: soft, pointed tips.", 0, "%", 100f);
            changed |= Slider("Scatter", () => config.ChildSpread, v => config.ChildSpread = v, d.ChildSpread, 0f, 0.01f,
                "How far the extra strands ('Strands per root') spread around their root. Higher: even, fluffy coverage. Lower: strands clump into tufts.", 1, " mm", 1000f);
            changed |= Check("Hair lies on the fur", () => config.HideBuriedStrands, v => config.HideBuriedStrands = v, d.HideBuriedStrands,
                "Hair or a tail resting on the coat lies on top of the fur. Off: it sinks into the fur and shows through as dark streaks.");
        }

        if (ImGui.CollapsingHeader("Colour and light", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= Check("Recolour undyed items", () => config.RecolorUndyed, v => config.RecolorUndyed = v, d.RecolorUndyed,
                "Your undyed fur items take the fur colour below, so a whole outfit matches. Off: they keep the game's colour. Dyed items and other players' items always keep their own colours.");
            changed |= Color("Fur colour", () => config.BaseColor, v => config.BaseColor = v, d.BaseColor,
                "The colour of your undyed fur items while 'Recolour undyed items' is on.");
            changed |= Slider("Game lighting", () => config.SceneLighting, v => config.SceneLighting = v, d.SceneLighting, 0f, 1f,
                "How much the fur follows the game's lighting (sun, lamps, shadows). Higher: fits into the scene. Lower: lit evenly by the plugin's own light, the same everywhere.", 0, "%", 100f);
            changed |= Slider("Game lighting strength", () => config.SceneGain, v => config.SceneGain = v, d.SceneGain, 0.5f, 10f,
                "Brightness of the fur under the game's lighting. Higher: brighter. Lower: darker.", 2, "x");
            changed |= Slider("Exposure", () => config.Exposure, v => config.Exposure = v, d.Exposure, 0.1f, 8f,
                "Overall brightness of the fur. Higher: brighter. Lower: darker.", 2, "x");
            changed |= Slider("Root brightness", () => config.RootDarken, v => config.RootDarken = v, d.RootDarken, 0f, 1f,
                "How much light reaches deep into the fur. Higher: evenly bright fur. Lower: dark roots, more depth and contrast.", 0, "%", 100f);
            changed |= Slider("Depth colour", () => config.Absorption, v => config.Absorption = v, d.Absorption, 0f, 6f,
                "Deep fur gets darker and richer in colour, like real fur. Higher: stronger, more saturated shadows near the roots. 0 = off.");
            changed |= Slider("Partings", () => config.FacingDepth, v => config.FacingDepth = v, d.FacingDepth, 0f, 10f,
                "Extra depth colour where you look straight down into the fur (partings, swirls). Higher: darker, more visible partings.", 1, "x");
            changed |= Slider("Undercoat", () => config.UndercoatDarken, v => config.UndercoatDarken = v, d.UndercoatDarken, 0f, 1f,
                "Brightness of the surface under the fur, which shows through gaps. Higher: brighter gaps. Lower: darker gaps, more depth.", 2, "x fur colour");
            changed |= Slider("Strand variation", () => config.HueJitter, v => config.HueJitter = v, d.HueJitter, 0f, 0.5f,
                "Random brightness per strand. Higher: more natural, varied fur. Lower: one even colour.", 0, "%", 100f);
            changed |= Slider("Sheen", () => config.Specular, v => config.Specular = v, d.Specular, 0f, 1f,
                "The soft shine on the fur. Higher: glossier. Lower: matte.", 0, "%", 100f);
            changed |= Slider("Soft lighting", () => config.SurfaceLighting, v => config.SurfaceLighting = v, d.SurfaceLighting, 0f, 1f,
                "Higher: soft, plush lighting. Lower: every strand lit on its own, more defined and stringy, like hair.", 0, "%", 100f);
            changed |= Slider("Film look", () => config.AgxToneMap, v => config.AgxToneMap = v, d.AgxToneMap, 0f, 1f,
                "Higher: softer, filmic brights that don't blow out to white (as in the reference renders). Lower: plainer, punchier colours.", 0, "%", 100f);
            changed |= Slider("Gamma", () => config.OutputGamma, v => config.OutputGamma = v, d.OutputGamma, 1f, 3f,
                "Fine brightness curve for the midtones while 'Film look' is below 100%. Leave at default unless the fur looks washed out or too dark.");
            changed |= Color("Ambient light", () => config.AmbientColor, v => config.AmbientColor = v, d.AmbientColor,
                "The plugin's own fill light, which keeps shadowed fur from going black.");
            changed |= Slider("Key light", () => config.LightIntensity, v => config.LightIntensity = v, d.LightIntensity, 0f, 3f,
                "Strength of the plugin's own main light. Higher: brighter highlights and sheen.", 2, "x");
            changed |= Direction("Key light direction", () => config.LightDirection, v => config.LightDirection = v, d.LightDirection,
                "Where the plugin's main light comes from (X, Y = up, Z).");
        }

        if (ImGui.CollapsingHeader("Advanced / troubleshooting"))
        {
            ImGui.TextWrapped("Leave these at their defaults unless something looks wrong.");
            changed |= Check("Hide fur behind objects", () => config.UseSceneDepth, v => config.UseSceneDepth = v, d.UseSceneDepth,
                "Fur is hidden behind walls, other characters and your own body. Off: fur draws over everything.");
            changed |= Slider("Root depth tolerance", () => config.DepthBiasMeters, v => config.DepthBiasMeters = v, d.DepthBiasMeters, 0f, 0.1f,
                "Raise if fur roots flicker or vanish against the item's own surface. Too high: fur shows through things in front of it.", 1, " cm", 100f);
            changed |= Slider("Depth scale override", () => config.DepthUvScaleOverride, v => config.DepthUvScaleOverride = v, d.DepthUvScaleOverride, 0f, 2f,
                "Only for render-resolution mods where fur is hidden in the wrong places. 0 = automatic.");
            changed |= SliderInt("Draw point", () => config.BackBufferBind, v => config.BackBufferBind = v, d.BackBufferBind, -1, 8,
                "When in the game's frame the fur is drawn. -1 = automatic (under the UI). 0 = last, over the UI. Change only if fur flickers or is missing with other overlay or ReShade plugins.");
            changed |= Check("Changing dither", () => config.TemporalDither, v => config.TemporalDither = v, d.TemporalDither,
                "Change the strands' see-through pattern every frame. With 'Temporal smoothing' this averages out to softer fur; without it, it sparkles.");
            changed |= Check("Show strands in magenta", () => config.DebugSolid, v => config.DebugSolid = v, d.DebugSolid,
                "Draws every strand solid magenta to see where fur is placed.");
            if (ImGui.Button("Regrow fur")) reloadItems = true;
            ImGui.SameLine();
            if (ImGui.Button("Log one frame")) traceRequested = true;
            ImGui.SameLine();
            if (ImGui.Button("Self-test") && testCases == null) StartSelfTest();
            ImGui.SameLine();
            if (ImGui.Button("Reset all settings")) { CopyDefaults(); changed = true; }
        }
        if (changed) pluginInterface.SavePluginConfig(config);
        ImGui.End();
    }

    private void CopyDefaults()
    {
        foreach (var p in typeof(Configuration).GetProperties()) if (p.CanWrite) p.SetValue(config, p.GetValue(Defaults));
    }

    private static readonly Configuration Defaults = new();
    private const float BaseStrandsPerCm2 = FurGrower.StrandsPerM2 / 10_000f;

    private static bool Check(string label, Func<bool> get, Action<bool> set, bool initial, string help)
    {
        var v = get();
        var changed = ImGui.Checkbox(label, ref v);
        if (changed) set(v);
        return HelpAndReset(label, help, initial ? "on" : "off", () => set(initial)) | changed;
    }

    /// <summary>Shown as value x <paramref name="scale"/> with <paramref name="decimals"/> and <paramref name="unit"/>; stored unscaled.</summary>
    private static bool Slider(string label, Func<float> get, Action<float> set, float initial, float min, float max, string help,
        int decimals = 2, string unit = "", float scale = 1f)
    {
        var v = get() * scale;
        // AlwaysClamp: Ctrl+click typing would otherwise save values outside the range.
        var changed = ImGui.SliderFloat(label, ref v, min * scale, max * scale, $"%.{decimals}f{unit.Replace("%", "%%")}", ImGuiSliderFlags.AlwaysClamp);
        if (changed) set(v / scale);
        return HelpAndReset(label, help, (initial * scale).ToString($"F{decimals}", CultureInfo.InvariantCulture) + unit, () => set(initial)) | changed;
    }

    private static bool SliderInt(string label, Func<int> get, Action<int> set, int initial, int min, int max, string help)
    {
        var v = get();
        var changed = ImGui.SliderInt(label, ref v, min, max, "%d", ImGuiSliderFlags.AlwaysClamp);
        if (changed) set(v);
        return HelpAndReset(label, help, initial.ToString(), () => set(initial)) | changed;
    }

    private static bool Direction(string label, Func<Vector3> get, Action<Vector3> set, Vector3 initial, string help)
    {
        var v = get();
        var changed = ImGui.SliderFloat3(label, ref v, -1f, 1f, "%.2f", ImGuiSliderFlags.AlwaysClamp);
        if (changed) set(v);
        return HelpAndReset(label, help, string.Create(CultureInfo.InvariantCulture, $"{initial.X:F2}, {initial.Y:F2}, {initial.Z:F2}"), () => set(initial)) | changed;
    }

    private static bool Color(string label, Func<Vector3> get, Action<Vector3> set, Vector3 initial, string help)
    {
        var v = get();
        // NoOptions: the colour editor's own right-click menu would take the place of the reset one.
        var changed = ImGui.ColorEdit3(label, ref v, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoOptions);
        if (changed) set(v);
        return HelpAndReset(label, help, string.Create(CultureInfo.InvariantCulture, $"{initial.X:F3}, {initial.Y:F3}, {initial.Z:F3}"), () => set(initial)) | changed;
    }

    // For the control just drawn: its explanation on hover and a right-click menu that resets it. True when reset.
    private static bool HelpAndReset(string label, string help, string initial, Action reset)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24);
            ImGui.TextUnformatted(help);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        // An explicit id: a colour editor leaves no item id of its own for the popup.
        if (!ImGui.BeginPopupContextItem(label + "##reset", ImGuiPopupFlags.MouseButtonRight)) return false;
        var clicked = ImGui.MenuItem($"Reset to default ({initial})");
        if (clicked) reset();
        ImGui.EndPopup();
        return clicked;
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= OnDraw;
        // A draw in progress on the render thread has to finish before its resources go.
        disposing = true;
        Thread.MemoryBarrier(); // pairs with the detour's increment: either it sees disposing, or this sees its count
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref detoursRunning) > 0 && waited.ElapsedMilliseconds < 10000) Thread.Sleep(1); // the first draw compiles the shaders
        setTargetHook?.Dispose();
        commandManager.RemoveHandler(CommandName);
        foreach (var item in items.Values.Concat(retiring)) retiredItems.Enqueue(item);
        foreach (var texture in replacedTextures) releasedTextures.Enqueue(texture);
        ReleaseRetired();
        renderer?.Dispose();
        interop.Dispose();
    }
}
