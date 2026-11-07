using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using Texture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using Skeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace FurRenderer;

/// <summary>
/// Everything the renderer needs from the game. Matrices use System.Numerics row-vector convention (v * M).
/// All members must be called on the framework/render thread that owns the game state.
/// </summary>
internal sealed unsafe class GameInterop : IDisposable
{
    private const int GPoseObjectIndex = 201;
    private const ushort GlovesModelSetId = 239;
    private const ushort RingModelSetId = 122;
    private const int CutsceneObjectEnd = 440; // actors spawned in GPose (Brio, Anamnesis) sit in the cutscene range

    private readonly IPluginLog log;

    // Typed depth SRV created over a typeless resource, keyed by resource pointer.
    private nint depthSrvResource;
    private ID3D11ShaderResourceView* depthSrv;

    /// <summary>
    /// True when the last <see cref="TryGetCamera"/> read a reverse-Z projection (Render.Camera.StandardZ == false).
    /// </summary>
    public bool LastCameraReverseZ { get; private set; } = true;

    /// <summary>Why the last scene-depth lookup succeeded or failed (for the debug log).</summary>
    public string DepthInfo { get; private set; } = "not queried";

    public GameInterop(IPluginLog log)
    {
        this.log = log;
    }

    public void Dispose()
    {
        ReleaseDepthSrv();
    }

    /// <summary>How the device/context were resolved (for the debug log).</summary>
    public string DeviceInfo { get; private set; } = "not queried";

    /// <summary>The swap chain's D3D11 device and its immediate context (not AddRef'd; both outlive the plugin).</summary>
    public bool TryGetDevice(out ID3D11Device* device, out ID3D11DeviceContext* context)
    {
        device = null;
        context = null;
        var dev = Device.Instance();
        if (dev == null || dev->SwapChain == null || dev->SwapChain->DXGISwapChain == null)
        {
            DeviceInfo = "no Device/SwapChain";
            return false;
        }

        // Ask the swap chain for its device and the device for its immediate context instead of trusting struct fields:
        // anything drawn on another (e.g. deferred) context never reaches the presented image.
        var swapChain = (IDXGISwapChain*)dev->SwapChain->DXGISwapChain;
        ID3D11Device* real;
        var hr = swapChain->GetDevice(__uuidof<ID3D11Device>(), (void**)&real);
        if (hr.FAILED || real == null)
        {
            DeviceInfo = $"IDXGISwapChain::GetDevice failed 0x{hr.Value:X8}";
            return false;
        }
        ID3D11DeviceContext* immediate;
        real->GetImmediateContext(&immediate);
        real->Release();
        immediate->Release();

        var fieldContext = (ID3D11DeviceContext*)dev->D3D11DeviceContext;
        DeviceInfo = $"swapchain device 0x{(nint)real:X} (Forwarder field 0x{(nint)dev->D3D11Forwarder:X}); immediate context 0x{(nint)immediate:X} " +
                     $"(D3D11DeviceContext field 0x{(nint)fieldContext:X}, type {(fieldContext != null ? fieldContext->GetType().ToString() : "null")})";
        device = real;
        context = immediate;
        return true;
    }

    /// <summary>Current swap-chain back buffer. AddRef'd: caller must Release.</summary>
    public bool TryGetBackBuffer(out ID3D11Texture2D* backBuffer)
    {
        backBuffer = null;
        var dev = Device.Instance();
        if (dev == null || dev->SwapChain == null || dev->SwapChain->DXGISwapChain == null)
            return false;

        var swapChain = (IDXGISwapChain*)dev->SwapChain->DXGISwapChain;
        ID3D11Texture2D* buffer;
        var hr = swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&buffer);
        if (hr.FAILED || buffer == null)
            return false;

        backBuffer = buffer;
        return true;
    }

    /// <summary>
    /// Scene depth as a shader resource view (not AddRef'd; valid for this frame) and its texture size.
    /// Readable as a float in .r: over a typeless resource, a typed SRV is created here and cached by resource pointer.
    /// </summary>
    public bool TryGetSceneDepth(out ID3D11ShaderResourceView* depth, out uint width, out uint height)
    {
        depth = null;
        width = 0;
        height = 0;

        var rtm = RenderTargetManager.Instance();
        if (rtm == null || rtm->DepthStencil == null)
        {
            DepthInfo = "RenderTargetManager.DepthStencil is null";
            return false;
        }

        Texture* tex = rtm->DepthStencil;
        if (tex->D3D11Texture2D == null)
        {
            DepthInfo = "DepthStencil has no D3D11Texture2D";
            return false;
        }

        var resource = (ID3D11Texture2D*)tex->D3D11Texture2D;
        D3D11_TEXTURE2D_DESC desc;
        resource->GetDesc(&desc);
        var info = $"depth {desc.Width}x{desc.Height} format {desc.Format} samples {desc.SampleDesc.Count} bind 0x{desc.BindFlags:X}";
        if (desc.SampleDesc.Count != 1 || (desc.BindFlags & (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE) == 0)
        {
            DepthInfo = info + ": multisampled or not shader-readable";
            return false;
        }

        var readable = ToReadableFormat(desc.Format);
        if (readable == DXGI_FORMAT.DXGI_FORMAT_UNKNOWN)
        {
            DepthInfo = info + ": unsupported format";
            return false;
        }

        width = desc.Width;
        height = desc.Height;

        // The game's own SRV, if it already views the depth plane as a float.
        if (tex->D3D11ShaderResourceView != null)
        {
            var own = (ID3D11ShaderResourceView*)tex->D3D11ShaderResourceView;
            D3D11_SHADER_RESOURCE_VIEW_DESC ownDesc;
            own->GetDesc(&ownDesc);
            if (ownDesc.Format == readable)
            {
                DepthInfo = info + ": using game SRV";
                depth = own;
                return true;
            }
        }

        DepthInfo = info + $": using own SRV as {readable}";
        if (depthSrv != null && depthSrvResource == (nint)resource)
        {
            depth = depthSrv;
            return true;
        }

        if (!TryGetDevice(out var device, out _))
            return false;

        ReleaseDepthSrv();
        var srvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = readable,
            ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D,
        };
        srvDesc.Texture2D.MostDetailedMip = 0;
        srvDesc.Texture2D.MipLevels = 1;

        ID3D11ShaderResourceView* created;
        var hr = device->CreateShaderResourceView((ID3D11Resource*)resource, &srvDesc, &created);
        if (hr.FAILED || created == null)
        {
            DepthInfo = info + $": CreateShaderResourceView failed 0x{hr.Value:X8}";
            log.Warning("Depth SRV creation failed: 0x{Hr:X8}", hr.Value);
            return false;
        }

        depthSrv = created;
        depthSrvResource = (nint)resource;
        depth = created;
        return true;
    }

    /// <summary>World->view and view->clip of the camera that rendered the current scene (normal and GPose).</summary>
    /// <remarks>
    /// Source: Graphics.Scene.CameraManager.CurrentCamera->RenderCamera (Render.Camera). View is ViewMatrix with M44 forced to 1
    /// (the game leaves it uninitialised; same fix as ffxiv_bossmod), projection is ProjectionMatrix. The game uses
    /// reverse-Z by default (bossmod comment; Render.Camera.StandardZ == false), i.e. near plane = depth 1, far/infinite = depth 0,
    /// depth buffer range [0,1]. <see cref="LastCameraReverseZ"/> reports the live StandardZ flag.
    /// </remarks>
    public bool TryGetCamera(out Matrix4x4 view, out Matrix4x4 projection)
    {
        view = Matrix4x4.Identity;
        projection = Matrix4x4.Identity;

        var manager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        if (manager == null)
            return false;

        var camera = manager->CurrentCamera;
        if (camera == null || camera->RenderCamera == null)
            return false;

        var render = camera->RenderCamera;
        Matrix4x4 v = render->ViewMatrix;
        v.M44 = 1;
        view = v;
        projection = render->ProjectionMatrix;
        LastCameraReverseZ = !render->StandardZ;
        return true;
    }

    /// <summary>
    /// Target character: GPose actor (object index 201) while in GPose, else the local player. Null when unavailable.
    /// Returned as the CharacterBase draw object address.
    /// </summary>
    public nint GetTargetCharacterBase()
    {
        var gameObject = GetTargetObject();
        return gameObject == null ? 0 : (nint)AsHuman((nint)gameObject->DrawObject);
    }

    private static FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* GetTargetObject()
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
        if (manager == null)
            return null;
        var index = FFXIVClientStructs.FFXIV.Client.Game.GameMain.IsInGPose() ? GPoseObjectIndex : 0;
        return manager->Objects.IndexSorted[index].Value;
    }

    /// <summary>
    /// Orbits the active game camera (Game.Camera DirH/DirV/Distance, radians and yalms) around the target character.
    /// Yaw is relative to the character's facing, in degrees. Null leaves a value unchanged. Main thread only.
    /// </summary>
    public string SetCamera(float? yawDegrees, float? pitchDegrees, float? distance)
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        var camera = manager == null ? null : manager->GetActiveCamera();
        var target = GetTargetObject();
        if (camera == null || target == null)
            return "camera: unavailable";
        if (yawDegrees is { } yaw)
            camera->DirH = MathF.IEEERemainder(target->Rotation + yaw * MathF.PI / 180f, 2f * MathF.PI);
        if (pitchDegrees is { } pitch)
            camera->DirV = Math.Clamp(pitch * MathF.PI / 180f, camera->DirVMin, camera->DirVMax);
        if (distance is { } d)
            camera->Distance = camera->InterpDistance = Math.Clamp(d, camera->MinDistance, camera->MaxDistance);
        return $"camera: DirH {camera->DirH:F3} DirV {camera->DirV:F3} (range {camera->DirVMin:F2}..{camera->DirVMax:F2}) "
             + $"dist {camera->Distance:F2} (range {camera->MinDistance:F1}..{camera->MaxDistance:F1}) charRot {target->Rotation:F3}";
    }

    /// <summary>
    /// For each requested bone name, writes the skinning matrix (model bind space -> world space):
    ///   inverse(bindModelSpace[bone]) * currentModelSpace[bone] * skeletonRootToWorld
    /// where bindModelSpace comes from the havok skeleton reference pose (composed through parent indices) and
    /// currentModelSpace from the animated pose. Searches all partial skeletons by name. Missing bones use the
    /// skeleton root-to-world transform; found[i] reports exact matches.
    /// </summary>
    /// <remarks>
    /// Unverified assumption: bind and pose model spaces of partial skeletons other than the first are both relative to
    /// that partial's own root; bones in the main (body) partial are exact.
    /// </remarks>
    public bool TryGetSkinMatrices(nint characterBase, BoneLookup lookup, IReadOnlyList<string> boneNames, Span<Matrix4x4> skin, Span<bool> found)
    {
        var count = Math.Min(boneNames.Count, Math.Min(skin.Length, found.Length));
        var character = AsCharacter(characterBase);
        if (character == null || character->Skeleton == null)
            return false;

        var skeleton = character->Skeleton;
        var rootToWorld = RootToWorld(skeleton);
        for (var i = 0; i < count; i++)
        {
            skin[i] = rootToWorld;
            found[i] = false;
        }

        var partialCount = skeleton->PartialSkeletonCount;
        if (partialCount == 0 || skeleton->PartialSkeletons == null)
            return false;

        // Signature of the skeleton set: rebuild lookup/bind caches when any partial's havok skeleton changes.
        nint signature = 17;
        for (var p = 0; p < partialCount; p++)
        {
            var pose = GetPose(skeleton, p);
            signature = signature * 31 + (pose != null ? (nint)pose->Skeleton : 0);
        }

        if (lookup.Character != characterBase || lookup.Signature != signature || lookup.Bones.Length != boneNames.Count)
            RebuildLookup(lookup, characterBase, signature, skeleton, partialCount, boneNames);

        var any = false;
        for (var i = 0; i < count; i++)
        {
            var (partial, bone) = lookup.Bones[i];
            if (partial < 0)
                continue;

            var pose = GetPose(skeleton, partial);
            if (pose == null || pose->Skeleton == null || bone >= pose->ModelPose.Length
                || !lookup.BindInverse.TryGetValue((nint)pose->Skeleton, out var bindInverse) || bone >= bindInverse.Length)
                continue;

            skin[i] = bindInverse[bone] * ToMatrix(pose->ModelPose.Data[bone]) * rootToWorld;
            found[i] = true;
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Which coat model the target renders, or None. Gloves slot model set id 239 => Gloves; either ring slot with
    /// accessory model set id 122 => Ring (the mod's Mirage Ring variant). Gloves wins if both.
    /// </summary>
    public CoatSlot GetCoatSlot(nint characterBase)
    {
        var human = AsHuman(characterBase);
        if (human == null)
            return CoatSlot.None;

        if (human->Arms.Id == GlovesModelSetId)
            return CoatSlot.Gloves;

        if (human->RFinger.Id == RingModelSetId || human->LFinger.Id == RingModelSetId)
            return CoatSlot.Ring;

        return CoatSlot.None;
    }

    /// <summary>
    /// Models using Hikari's fur textures, in the coat's slots or in all the others. The texture paths stay game
    /// paths under Penumbra; the model's file name is the mod's file on disk.
    /// </summary>
    public List<FurModel> FindFurModels(nint characterBase, bool coatSlots = false)
    {
        var models = new List<FurModel>();
        var character = AsCharacter(characterBase);
        if (character == null || character->Models == null)
            return models;

        var human = AsHuman(characterBase);
        for (var slot = 0; slot < character->SlotCount; slot++)
        {
            var model = character->Models[slot];
            if (model == null || model->ModelResourceHandle == null || model->Materials == null || (human != null && IsCoatSlot(human, slot)) != coatSlots)
                continue;

            var fur = 0u;
            for (var m = 0; m < Math.Min(model->MaterialCount, 32); m++)
            {
                var material = model->Materials[m];
                if (material == null || material->MaterialResourceHandle == null || !UsesFurTexture(material->MaterialResourceHandle))
                    continue;
                fur |= 1u << m;
            }

            if (fur == 0)
                continue;
            var path = model->ModelResourceHandle->FileName.ToString();
            models.Add(new FurModel(path[(path.LastIndexOf('|') + 1)..], fur, model->EnabledAttributeIndexMask, slot));
        }

        return models;
    }

    private static bool UsesFurTexture(FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.MaterialResourceHandle* material)
    {
        for (var t = 0; t < material->TextureCount; t++)
            if (material->TexturePath(t).AsSpan().StartsWith("chara/common/texture/minamotohikari/fur_"u8))
                return true;
        return false;
    }

    private static bool IsCoatSlot(Human* human, int slot) => slot switch
    {
        2 => human->Arms.Id == GlovesModelSetId,
        8 => human->RFinger.Id == RingModelSetId,
        9 => human->LFinger.Id == RingModelSetId,
        _ => false,
    };

    /// <summary>Visible monster-type actors that can be fur props: the player's minion, or in GPose the actors after the GPose player up to the end of the cutscene range.</summary>
    public List<nint> GetProps()
    {
        var props = new List<nint>();
        if (FFXIVClientStructs.FFXIV.Client.Game.GameMain.IsInGPose())
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
            if (manager != null)
                for (var i = GPoseObjectIndex + 1; i < CutsceneObjectEnd; i++)
                    AddProp(props, manager->Objects.IndexSorted[i].Value);
        }
        else
        {
            var target = GetTargetObject();
            if (target != null)
                AddProp(props, (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)((Character*)target)->CompanionData.CompanionObject);
        }

        return props;
    }

    private static void AddProp(List<nint> props, FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* gameObject)
    {
        var character = gameObject == null ? null : AsCharacter((nint)gameObject->DrawObject);
        if (character != null && character->GetModelType() == CharacterBase.ModelType.Monster && character->DrawObject.IsVisible)
            props.Add((nint)character);
    }

    /// <summary>
    /// The live colour table (dye applied) of a slot's material as the game's shader view, not AddRef'd; null while there is
    /// none or it isn't the float table the fur shaders read. Dyes and Glamourer/Penumbra edits replace the texture.
    /// </summary>
    public ID3D11ShaderResourceView* GetColorTable(nint characterBase, int slot, int material)
    {
        var character = AsCharacter(characterBase);
        if (character == null || character->ColorTableTextures == null || material >= CharacterBase.MaterialsPerSlot)
            return null;
        var table = character->ColorTableTextures[slot * CharacterBase.MaterialsPerSlot + material];
        if (table == null || table->D3D11ShaderResourceView == null)
            return null;
        var view = (ID3D11ShaderResourceView*)table->D3D11ShaderResourceView;
        D3D11_SHADER_RESOURCE_VIEW_DESC desc;
        view->GetDesc(&desc);
        return desc.Format == DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT && desc.ViewDimension == D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D ? view : null;
    }

    /// <summary>
    /// The shader view of the texture a slot's material binds to the sampler named by <paramref name="samplerCrc"/> (the CRC32
    /// of its name, e.g. g_SamplerIndex), not AddRef'd; null if it binds none.
    /// </summary>
    public ID3D11ShaderResourceView* GetMaterialTexture(nint characterBase, int slot, int material, uint samplerCrc)
    {
        var character = AsCharacter(characterBase);
        if (character == null || character->Models == null || slot >= character->SlotCount) return null;
        var model = character->Models[slot];
        if (model == null || model->Materials == null || material >= model->MaterialCount) return null;
        var mat = model->Materials[material];
        if (mat == null || mat->Textures == null || mat->MaterialResourceHandle == null || mat->MaterialResourceHandle->ShaderPackageResourceHandle == null)
            return null;
        // Material textures are keyed by the shader package's id for the name, listed with its samplers (the fur materials'
        // index map) or its textures (their diffuse map).
        var shpk = mat->MaterialResourceHandle->ShaderPackageResourceHandle->ShaderPackage;
        if (shpk == null) return null;
        uint? samplerId = null;
        for (var s = 0; s < shpk->SamplerCount && samplerId == null && shpk->Samplers != null; s++)
            if (shpk->Samplers[s].CRC == samplerCrc) samplerId = shpk->Samplers[s].Id;
        for (var s = 0; s < shpk->TextureCount && samplerId == null && shpk->Textures != null; s++)
            if (shpk->Textures[s].CRC == samplerCrc) samplerId = shpk->Textures[s].Id;
        for (var t = 0; t < mat->TextureCount && samplerId != null; t++)
        {
            if (mat->Textures[t].Id != samplerId) continue;
            var handle = mat->Textures[t].Texture;
            if (handle == null || handle->Texture == null || handle->Texture->D3D11ShaderResourceView == null) return null;
            var view = (ID3D11ShaderResourceView*)handle->Texture->D3D11ShaderResourceView;
            D3D11_SHADER_RESOURCE_VIEW_DESC desc;
            view->GetDesc(&desc);
            return desc.ViewDimension == D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D ? view : null;
        }
        return null;
    }

    /// <summary>World position of a character's skeleton root (its feet), or null.</summary>
    public Vector3? GetRootPosition(nint characterBase)
    {
        var character = AsCharacter(characterBase);
        return character == null || character->Skeleton == null ? null : (Vector3)character->Skeleton->Transform.Position;
    }

    /// <summary>True if the target's body model is male (c0101-family asset), false for female (c0201-family).</summary>
    public bool IsMale(nint characterBase)
    {
        var human = AsHuman(characterBase);
        return human != null && human->Customize.Sex == 0;
    }

    /// <summary>Race/sex body code of the target (e.g. 1401 for Au Ra female), as used by c####-prefixed model paths.</summary>
    public ushort GetRaceSexId(nint characterBase)
    {
        var human = AsHuman(characterBase);
        return human == null ? (ushort)0 : human->RaceSexId;
    }

    /// <summary>Bone name -> parent bone name over all partial skeletons of the target.</summary>
    public Dictionary<string, string> GetBoneParents(nint characterBase)
    {
        var parents = new Dictionary<string, string>();
        var human = AsHuman(characterBase);
        if (human == null || human->Skeleton == null || human->Skeleton->PartialSkeletons == null) return parents;
        for (var p = 0; p < human->Skeleton->PartialSkeletonCount; p++)
        {
            var pose = GetPose(human->Skeleton, p);
            if (pose == null || pose->Skeleton == null) continue;
            var havok = pose->Skeleton;
            for (var b = 0; b < Math.Min(havok->Bones.Length, havok->ParentIndices.Length); b++)
            {
                var parent = havok->ParentIndices.Data[b];
                var name = havok->Bones[b].Name.String;
                var parentName = parent >= 0 && parent < havok->Bones.Length ? havok->Bones[parent].Name.String : null;
                if (name != null && parentName != null) parents.TryAdd(name, parentName);
            }
        }
        return parents;
    }

    /// <summary>Object table index of the target (for Penumbra's per-object path resolution), or -1.</summary>
    public int GetTargetObjectIndex()
    {
        var target = GetTargetObject();
        return target == null ? -1 : target->ObjectIndex;
    }

    private static Human* AsHuman(nint address)
    {
        var character = AsCharacter(address);
        return character != null && character->GetModelType() == CharacterBase.ModelType.Human ? (Human*)address : null;
    }

    private static CharacterBase* AsCharacter(nint address)
    {
        if (address == 0)
            return null;

        var drawObject = (DrawObject*)address;
        return drawObject->Object.GetObjectType() == ObjectType.CharacterBase ? (CharacterBase*)address : null;
    }

    private static hkaPose* GetPose(Skeleton* skeleton, int partial)
    {
        var partialSkeleton = &skeleton->PartialSkeletons[partial];
        return partialSkeleton->GetHavokPose(0);
    }

    private static Matrix4x4 RootToWorld(Skeleton* skeleton)
    {
        var t = skeleton->Transform;
        return Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Position);
    }

    private static Matrix4x4 ToMatrix(in FFXIVClientStructs.Havok.Common.Base.Math.QsTransform.hkQsTransformf t)
    {
        var scale = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
        var rotation = new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W);
        var translation = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
    }

    private static void RebuildLookup(BoneLookup lookup, nint characterBase, nint signature, Skeleton* skeleton, int partialCount, IReadOnlyList<string> boneNames)
    {
        lookup.BindInverse.Clear();
        var byName = new Dictionary<string, (int Partial, int Bone)>();
        for (var p = 0; p < partialCount; p++)
        {
            var pose = GetPose(skeleton, p);
            if (pose == null || pose->Skeleton == null)
                continue;

            var havok = pose->Skeleton;
            lookup.BindInverse[(nint)havok] = BuildBindInverse(havok);
            for (var b = 0; b < havok->Bones.Length; b++)
            {
                var name = havok->Bones[b].Name.String;
                if (name != null)
                    byName.TryAdd(name, (p, b)); // first (lowest) partial wins
            }
        }

        lookup.Bones = new (int, int)[boneNames.Count];
        for (var i = 0; i < lookup.Bones.Length; i++)
            lookup.Bones[i] = byName.GetValueOrDefault(boneNames[i], (-1, -1));

        lookup.Character = characterBase;
        lookup.Signature = signature;
    }

    private static Matrix4x4[] BuildBindInverse(hkaSkeleton* havok)
    {
        var count = Math.Min(havok->ReferencePose.Length, havok->ParentIndices.Length);
        var model = new Matrix4x4[count];
        var inverse = new Matrix4x4[count];
        for (var i = 0; i < count; i++)
        {
            var local = ToMatrix(havok->ReferencePose.Data[i]);
            var parent = havok->ParentIndices.Data[i];
            // Parents precede children in havok skeletons; guard against malformed data anyway.
            model[i] = parent >= 0 && parent < i ? local * model[parent] : local;
            if (!Matrix4x4.Invert(model[i], out inverse[i]))
                inverse[i] = Matrix4x4.Identity;
        }

        return inverse;
    }

    private static DXGI_FORMAT ToReadableFormat(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT.DXGI_FORMAT_R24G8_TYPELESS => DXGI_FORMAT.DXGI_FORMAT_R24_UNORM_X8_TYPELESS,
        DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
        DXGI_FORMAT.DXGI_FORMAT_R32G8X24_TYPELESS => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS,
        _ => DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
    };

    private void ReleaseDepthSrv()
    {
        if (depthSrv != null)
            depthSrv->Release();

        depthSrv = null;
        depthSrvResource = 0;
    }
}

/// <summary>A model drawn with Hikari's fur textures: its file, which of its materials use them, and its slot (for the colour tables).</summary>
internal readonly record struct FurModel(string Path, uint FurMaterials, uint EnabledAttributes, int Slot);

/// <summary>A bone list resolved against one character's skeletons; rebuilt when either changes.</summary>
internal sealed class BoneLookup
{
    public nint Character, Signature;
    public (int Partial, int Bone)[] Bones = [];
    public readonly Dictionary<nint, Matrix4x4[]> BindInverse = []; // hkaSkeleton* -> inverse of the reference-pose model-space transform of every bone
}

internal enum CoatSlot
{
    None,
    Gloves,
    Ring,
}
