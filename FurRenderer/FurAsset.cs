using System.Runtime.InteropServices;
using System.Text;

namespace FurRenderer;

/// <summary>
/// .furasset v1 (little endian), written by tools/pack_furasset.py:
///   char[4] "FURA"; u32 version = 1; u32 strandCount; u32 pointsPerStrand (= 4); u32 boneCount;
///   boneCount x { u8 byteLength; utf8 name }; zero padding to a 4-byte boundary;
///   strandCount x pointsPerStrand x { float3 position (game model/bind space, Y-up, metres); float radius };
///   strandCount x { u8[4] boneIndex (into the bone list); float4 weights (sum 1) }.
/// Strand order is shuffled so any prefix is a uniform subsample (density control = draw fewer instances).
/// </summary>
internal sealed class FurAsset
{
    public const int PointsPerStrand = 4;

    public required string[] BoneNames { get; init; }
    public required int StrandCount { get; init; }
    public required StrandPoint[] Points { get; init; }
    public required StrandSkin[] Skins { get; init; }

    public static FurAsset Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var span = bytes.AsSpan();
        if (!span[..4].SequenceEqual("FURA"u8)) throw new InvalidDataException($"{path}: not a .furasset");
        var version = BitConverter.ToUInt32(bytes, 4);
        var strands = checked((int)BitConverter.ToUInt32(bytes, 8));
        var ppc = BitConverter.ToUInt32(bytes, 12);
        var boneCount = checked((int)BitConverter.ToUInt32(bytes, 16));
        if (version != 1 || ppc != PointsPerStrand) throw new InvalidDataException($"{path}: unsupported version {version} / points {ppc}");
        var offset = 20;
        var names = new string[boneCount];
        for (var i = 0; i < boneCount; i++)
        {
            int len = bytes[offset++];
            names[i] = Encoding.UTF8.GetString(bytes, offset, len);
            offset += len;
        }
        offset = (offset + 3) & ~3;
        var pointBytes = checked(strands * PointsPerStrand * Marshal.SizeOf<StrandPoint>());
        var skinBytes = checked(strands * Marshal.SizeOf<StrandSkin>());
        if (bytes.Length != offset + pointBytes + skinBytes) throw new InvalidDataException($"{path}: size mismatch");
        var points = MemoryMarshal.Cast<byte, StrandPoint>(span.Slice(offset, pointBytes)).ToArray();
        var skins = MemoryMarshal.Cast<byte, StrandSkin>(span.Slice(offset + pointBytes, skinBytes)).ToArray();
        foreach (var skin in skins)
            if (skin.B0 >= boneCount || skin.B1 >= boneCount || skin.B2 >= boneCount || skin.B3 >= boneCount)
                throw new InvalidDataException($"{path}: bone index out of range");
        return new FurAsset { BoneNames = names, StrandCount = strands, Points = points, Skins = skins };
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct StrandPoint
{
    public float X, Y, Z, Radius;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct StrandSkin
{
    public byte B0, B1, B2, B3;
    public float W0, W1, W2, W3;
}
