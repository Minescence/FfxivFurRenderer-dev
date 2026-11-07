using System.Collections.Concurrent;
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
    private bool[] found = new bool[Renderer.MaxBones];
    private volatile FrameSnapshot? snapshot;
    private readonly Dictionary<string, FurItem> items = []; // by model, enabled submeshes and wearer race; main thread
    private readonly List<FurItem> retiring = [];
    private readonly ConcurrentQueue<FurItem> retiredItems = new();
    private readonly List<nint> replacedTables = [];
    private readonly ConcurrentQueue<nint> releasedTables = new(); // colour tables the render thread releases, like retiredItems
    private bool reloadItems;
    private int frame;
    private float lodFocal; // render thread: the projection's vertical focal length outside GPose

    private const int RetireAfterFrames = 300;

    /// <summary>A fur-bearing model: growing, or ready to draw.</summary>
    private sealed class FurItem
    {
        public required string Name;
        public Task<FurAsset?>? Growth;
        public Matrix4x4[]? Deform; // race deformer per bone; set by the growth task
        public string[] BoneNames = [];
        public int StrandCount;
        public readonly BoneLookup Lookup = new();
        public int LastUsed;
        // The fur materials' live colour tables (SRVs), AddRef'd so a replaced table's address can't be reused while it is
        // held: a pointer change is always a new table. Main thread; the render thread releases them.
        public readonly nint[] Tables = new nint[Renderer.MaxMaterials];
        public readonly nint[] LoggedTables = new nint[Renderer.MaxMaterials]; // render thread
        public volatile FurAsset? Upload; // held for the render thread, then dropped: the strands stay on the GPU only
        public Renderer.FurBuffers? Buffers; // render thread
    }

    private readonly record struct ItemDraw(FurItem Item, Matrix4x4[] Skin, nint[] Tables, Vector3? Position);

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

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IClientState clientState, IObjectTable objectTable, IGameInteropProvider hooks, IPluginLog log, IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.log = log;
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
        if (config.Version < 7) // colour fit to the reference renders, with the undercoat under the fur
        {
            var fresh = new Configuration();
            (config.BaseColor, config.RootDarken, config.Specular, config.Absorption, config.FacingDepth, config.UndercoatDarken) =
                (fresh.BaseColor, fresh.RootDarken, fresh.Specular, fresh.Absorption, fresh.FacingDepth, fresh.UndercoatDarken);
            config.Version = 7;
        }
        // Without the game's lighting the fur had no shadows; gain 4 clipped sunlit fur to white.
        if (config.Version < 8) { config.SceneLighting = 1f; config.SceneGain = 1.5f; config.Version = 8; }
        if (config.Version < 9) { config.Specular = 0.05f; config.Version = 9; } // white sheen frosted the tips and greyed dark colours
        if (config.Version < 10) // strand defaults are now the user's in-game tuning against the reference
        {
            var fresh = new Configuration();
            (config.ChildCount, config.ChildSpread, config.LengthScale, config.WidthScale, config.MinPixelWidth, config.Gravity) =
                (fresh.ChildCount, fresh.ChildSpread, fresh.LengthScale, fresh.WidthScale, fresh.MinPixelWidth, fresh.Gravity);
            config.Version = 10;
        }
        // 6 (the reference fit) turned every camera-facing stretch of fur into deep crimson bands and spots.
        if (config.Version < 11) { config.FacingDepth = 2f; config.Version = 11; }
        interop = new GameInterop(clientState, objectTable, log);
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
        foreach (var table in replacedTables) releasedTables.Enqueue(table);
        replacedTables.Clear();
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

    private void Fill(FrameSnapshot snap)
    {
        if (!config.Enabled) { snap.Skip = "disabled"; return; }
        if (config.OnlyInGpose && !snap.Gpose) { snap.Skip = "waiting for GPose"; return; }
        var character = interop.GetTargetCharacterBase();
        if (character == 0) { snap.Skip = "no target character"; return; }
        if (!interop.TryGetCamera(out snap.View, out snap.Proj)) { snap.Skip = "camera unavailable"; return; }
        Diag("camera", $"StandardZ-derived reverseZ {interop.LastCameraReverseZ}; proj row3 {snap.Proj.M31:G4} {snap.Proj.M32:G4} {snap.Proj.M33:G4} {snap.Proj.M34:G4}; row4 {snap.Proj.M41:G4} {snap.Proj.M42:G4} {snap.Proj.M43:G4} {snap.Proj.M44:G4}");
        var race = interop.GetRaceSexId(character);
        var slot = interop.GetCoatSlot(character);
        if (slot != CoatSlot.None || !config.RequireCoatEquipped)
        {
            var male = interop.IsMale(character);
            var name = $"coat_{(slot == CoatSlot.Ring ? "ril" : "glv")}_{(male ? "m" : "f")}.furasset";
            var key = $"{name}@c{race:D4}";
            var path = Path.Combine(AssetDirectory, name);
            // Baked assets hold the mod's mesh, so releases can't ship them: there the coat grows from the worn model like any other item.
            if (items.TryGetValue(key, out var coat) || File.Exists(path))
            {
                coat ??= items[key] = StartItem(character, (ushort)(male ? 101 : 201), race, name, () => FurAsset.Load(path));
                // Baked strands are all coloured by the worn coat's first fur material.
                var worn = interop.FindFurModels(character, coatSlots: true) is [var w, ..] ? w : default;
                AddDraw(snap, character, coat, worn.Slot, worn.FurMaterials);
            }
            else AddFurModels(snap, character, race, coatSlots: true);
        }
        AddFurModels(snap, character, race);
        foreach (var prop in interop.GetProps())
            AddFurModels(snap, prop, 0); // props have their own skeletons: no race deformer
        if (snap.Items.Count == 0 && snap.Growing == 0) snap.Skip = "no fur items";
    }

    private void AddFurModels(FrameSnapshot snap, nint character, ushort race, bool coatSlots = false)
    {
        foreach (var fur in interop.FindFurModels(character, coatSlots))
        {
            // Props by actor: two with the same minion have their own bones and colour tables.
            var key = $"{fur.Path}|{fur.FurMaterials:X}|{fur.EnabledAttributes:X}@{(race != 0 ? race : character):X}";
            if (!items.TryGetValue(key, out var item))
            {
                var name = Path.GetFileName(fur.Path);
                // shortcut: takes the model's race from its file name, as all 46 surveyed models keep it; a renamed file gets no
                // race deformer. Upgrade: ask Penumbra.GetGameObjectResourcePaths.V5 for the game path.
                var named = System.Text.RegularExpressions.Regex.Match(name, @"^c([0-9]{4})");
                var modelRace = race != 0 && named.Success ? ushort.Parse(named.Groups[1].Value) : race;
                items[key] = item = StartItem(character, modelRace, race, name, () => FurGrower.Grow(ReadModel(fur.Path), fur.FurMaterials, fur.EnabledAttributes, coatSlots ? FurGrower.MaxCoatStrands : FurGrower.MaxStrands));
            }
            AddDraw(snap, character, item, fur.Slot, fur.FurMaterials);
        }
    }

    private byte[] ReadModel(string path)
        => Path.IsPathRooted(path) ? File.ReadAllBytes(path)
            : dataManager.GetFile(path)?.Data ?? throw new FileNotFoundException("model not in the game data", path);

    // Growth runs off the game's threads (under a second); its deformer inputs are game state, so they are read here.
    private FurItem StartItem(nint character, ushort modelRace, ushort race, string name, Func<FurAsset?> build)
    {
        var parents = modelRace != race ? interop.GetBoneParents(character) : null;
        byte[]? pbd = null;
        var pbdSource = "";
        if (parents != null) (pbd, pbdSource) = ReadPbd(interop.GetTargetObjectIndex());
        var item = new FurItem { Name = name, LastUsed = frame };
        item.Growth = Task.Run(() =>
        {
            try
            {
                var asset = build();
                if (asset == null)
                {
                    Diag("item " + name, $"skipped: no fur surface, or {FurGrower.MaxStrands:N0} strands or more");
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
            if (growth.Result is { } asset) (item.BoneNames, item.StrandCount, item.Upload) = (asset.BoneNames, asset.StrandCount, asset);
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
        // The k-th table belongs to the k-th set bit of furMaterials: the rank FurGrower gives each strand.
        var material = -1;
        for (var k = 0; k < Renderer.MaxMaterials; k++)
        {
            do material++; while (material < 32 && (furMaterials >> material & 1) == 0);
            var table = material < 32 ? (nint)interop.GetColorTable(character, slot, material) : 0;
            if (table == item.Tables[k]) continue;
            if (table != 0) ((ID3D11ShaderResourceView*)table)->AddRef();
            if (item.Tables[k] != 0) replacedTables.Add(item.Tables[k]);
            item.Tables[k] = table;
        }
        snap.Items.Add(new ItemDraw(item, skin, (nint[])item.Tables.Clone(), interop.GetRootPosition(character)));
    }

    private static void ReleaseTables(FurItem item)
    {
        for (var k = 0; k < item.Tables.Length; k++)
        {
            if (item.Tables[k] != 0) ((ID3D11ShaderResourceView*)item.Tables[k])->Release();
            item.Tables[k] = 0;
        }
    }

    private void RenderFur(ID3D11Texture2D* boundTarget)
    {
        // Before reading the snapshot: see OnDraw.
        while (retiredItems.TryDequeue(out var retired))
        {
            retired.Buffers?.Dispose();
            ReleaseTables(retired);
        }
        while (releasedTables.TryDequeue(out var table)) ((ID3D11ShaderResourceView*)table)->Release();
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
        foreach (var (item, skin, tables, position) in snap.Items)
        {
            for (var k = 0; k < tables.Length; k++)
            {
                if (tables[k] == item.LoggedTables[k]) continue;
                item.LoggedTables[k] = tables[k];
                Diag($"colour {item.Name} {k}", tables[k] == 0 ? "no table: fur colour"
                    : $"table row 0 {renderer.ReadTableDiffuse(context, (ID3D11ShaderResourceView*)tables[k])?.ToString() ?? "unreadable"}");
            }
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
                if (distance > config.LodDistance) itemDensity *= Math.Max(config.LodMinDensity, config.LodDistance / distance);
            }
            draws.Add(new FurDraw(buffers, skin, tables, itemDensity));
            strands += item.StrandCount;
            drawnStrands += (int)(item.StrandCount * itemDensity);
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
            renderer.Draw(context, backBuffer, depth, new Vector2(dw, dh), draws, view, proj, gpose ? config.GposeChildCount : config.ChildCount, config);
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
        if (renderer is { PerfInfo: { Length: > 0 } perf }) ImGui.TextDisabled(perf);
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
            changed |= Check("Fewer strands far away", () => config.DistanceLod, v => config.DistanceLod = v);
            changed |= Slider("Full density within (m)", () => config.LodDistance, v => config.LodDistance = v, 0.5f, 20f);
            changed |= Slider("Least density far away", () => config.LodMinDensity, v => config.LodMinDensity = v, 0.05f, 1f);
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
            changed |= Slider("Undercoat brightness", () => config.UndercoatDarken, v => config.UndercoatDarken = v, 0f, 1f);
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
            changed |= Check("Skip hidden strands", () => config.CullHiddenStrands, v => config.CullHiddenStrands = v);
            changed |= Slider("Depth bias (m)", () => config.DepthBiasMeters, v => config.DepthBiasMeters = v, 0f, 0.1f);
            changed |= Slider("Depth UV scale (0 = auto)", () => config.DepthUvScaleOverride, v => config.DepthUvScaleOverride = v, 0f, 2f);
            changed |= Check("Debug: solid magenta", () => config.DebugSolid, v => config.DebugSolid = v);
            changed |= Check("Temporal dither", () => config.TemporalDither, v => config.TemporalDither = v);
            if (ImGui.Button("Reload fur")) reloadItems = true;
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
        // A draw in progress on the render thread has to finish before its resources go.
        disposing = true;
        Thread.MemoryBarrier(); // pairs with the detour's increment: either it sees disposing, or this sees its count
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref detoursRunning) > 0 && waited.ElapsedMilliseconds < 10000) Thread.Sleep(1); // the first draw compiles the shaders
        setTargetHook?.Dispose();
        commandManager.RemoveHandler(CommandName);
        foreach (var item in items.Values.Concat(retiring)) { item.Buffers?.Dispose(); ReleaseTables(item); }
        while (retiredItems.TryDequeue(out var retired)) { retired.Buffers?.Dispose(); ReleaseTables(retired); }
        foreach (var table in replacedTables) releasedTables.Enqueue(table);
        while (releasedTables.TryDequeue(out var table)) ((ID3D11ShaderResourceView*)table)->Release();
        renderer?.Dispose();
        (interop as IDisposable)?.Dispose();
    }
}
