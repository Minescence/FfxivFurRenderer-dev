using System.Numerics;
using Dalamud.Configuration;

namespace FurRenderer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 11;

    public bool Enabled { get; set; } = true;
    public bool OnlyInGpose { get; set; }
    /// <summary>Also draw fur on the other visible characters (players around you, actors posed in GPose).</summary>
    public bool OtherPlayers { get; set; }

    /// <summary>Fraction of the packed strands drawn in normal play / in GPose.</summary>
    public float Density { get; set; } = 1.0f;
    public float GposeDensity { get; set; } = 1.0f;
    /// <summary>Strands drawn per baked strand (the assets hold a tenth of the reference density) and how far children spread (m).</summary>
    public int ChildCount { get; set; } = 3;
    public int GposeChildCount { get; set; } = 6;
    public float ChildSpread { get; set; } = 0.01f;
    /// <summary>
    /// Fewer strands on items far from the camera: full density within LodDistance (m from the item's feet, at the normal field
    /// of view), then (LodDistance / distance)^LodBias of it, down to LodMinDensity. Strands are drawn at least MinPixelWidth
    /// wide, so each covers a share of the item's screen area that grows with distance; bias 1 keeps the share, and the look,
    /// as at LodDistance. Bias 2 (1 / distance^2, the item's area) left 4x fewer strands, which CoverageScale widened into
    /// coarse tufts.
    /// </summary>
    public bool DistanceLod { get; set; } = true;
    public float LodDistance { get; set; } = 4f;
    public float LodBias { get; set; } = 1f;
    public float LodMinDensity { get; set; } = 0.25f;

    /// <summary>Strand look tuned in game against the reference renders (long, straight, no droop).</summary>
    public float LengthScale { get; set; } = 1.5f;
    public float WidthScale { get; set; } = 1.065f;
    public float MinPixelWidth { get; set; } = 0.595f;
    public float Opacity { get; set; } = 1.0f;
    public float FadeStart { get; set; } = 0.25f;
    /// <summary>Share of the last frames' fur kept in each frame, following its motion (Renderer's temporal pass); 0 = off.</summary>
    public float TemporalSmoothing { get; set; } = 0.8f;
    /// <summary>8 samples per pixel in the fur layer instead of 4 (about 1.2 ms more at 1440p), where the GPU has it.</summary>
    public bool Msaa8 { get; set; }
    public float Gravity { get; set; } = 0f;

    // Reference material (Fur (Principled Hair BSDF)): tip colour linear (0.641, 0.205, 0.328) multiplied by the intercept, so
    // roots go black.
    public Vector3 BaseColor { get; set; } = new(0.641f, 0.205f, 0.328f);
    /// <summary>Items in Hikari's undyed colour take BaseColor, so an outfit matches the coat; off, they keep the game's colour.</summary>
    public bool RecolorUndyed { get; set; } = true;
    public float RootDarken { get; set; } = 0f;
    /// <summary>Coat surface under the fur = BaseColor x this (reference emitter Material.004: colour multiplied by 0.3).</summary>
    public float UndercoatDarken { get; set; } = 0.3f;
    public float HueJitter { get; set; } = 0.2f;
    public Vector3 AmbientColor { get; set; } = new(0.45f, 0.42f, 0.45f);
    public Vector3 LightDirection { get; set; } = new(-0.4f, 0.8f, 0.45f);
    public float LightIntensity { get; set; } = 0.9f;
    /// <summary>White sheen, about hair's Fresnel reflectance. The lobe is so broad it covers every strand: 0.3 turned black fur grey with white tips.</summary>
    public float Specular { get; set; } = 0.05f;
    public float SurfaceLighting { get; set; } = 0.3f;
    /// <summary>
    /// Colour absorption through the fur depth (darker, more saturated roots) and how much more when looking down into it.
    /// Fitted through AgX to the reference renders: lit fur seen side-on is near the base colour plus sheen, fur pointing at
    /// the camera is deep crimson. That fit gave FacingDepth 6, which in game made bands and spots wherever the coat faces the
    /// camera; 2 keeps a hint of it.
    /// </summary>
    public float Absorption { get; set; } = 0.4f;
    public float FacingDepth { get; set; } = 2f;
    /// <summary>0 = synthetic lighting only; 1 = brightness taken from the game's lit item around each strand root.</summary>
    public float SceneLighting { get; set; } = 1f;
    /// <summary>1 / the lit coat's brightness in full sun (about 0.66 linear here), so sunlit fur keeps its colour and shadows darken it.</summary>
    public float SceneGain { get; set; } = 1.5f;
    public float OutputGamma { get; set; } = 2.2f;
    /// <summary>Linear exposure before tone mapping; 0..1 blend from plain gamma output to the reference render's AgX view transform.</summary>
    public float Exposure { get; set; } = 1.6f;
    public float AgxToneMap { get; set; } = 1f;

    public bool UseSceneDepth { get; set; } = true;
    /// <summary>Skip strands outside the view or hidden behind the scene (the character's own body included) before drawing.</summary>
    public bool CullHiddenStrands { get; set; } = true;
    /// <summary>Leave out strands rooted under something resting on the item within the fur's reach (hair, a tail), which then lies on the fur.</summary>
    public bool HideBuriedStrands { get; set; } = true;
    /// <summary>
    /// Where to draw in the game's render commands: -1 = auto (just before the first back-buffer bind without depth that follows
    /// the scene-depth binds: after the scene is composited, before nameplates/UI), N = right after the Nth back-buffer bind,
    /// 0 = at Present (over the UI).
    /// </summary>
    public int BackBufferBind { get; set; } = -1;
    public float DepthBiasMeters { get; set; } = 0.01f;
    /// <summary>0 = automatic (back buffer size vs depth texture size).</summary>
    public float DepthUvScaleOverride { get; set; }
    public bool TemporalDither { get; set; }
    /// <summary>Draw strands opaque magenta, ignoring opacity, to check placement.</summary>
    public bool DebugSolid { get; set; }
}
