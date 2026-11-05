using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace FurRenderer;

/// <summary>
/// .furasset v3 (little endian), written by tools/pack_furasset.py:
///   char[4] "FURA"; u32 version = 3; u32 strandCount; u32 pointsPerStrand (= 4); u32 boneCount;
///   boneCount x { u8 byteLength; utf8 name }; zero padding to a 4-byte boundary;
///   strandCount x pointsPerStrand x { float3 position (game model/bind space, Y-up, metres); float radius };
///   strandCount x { u8[8] boneIndex (into the bone list); float[8] weights (sum 1; unused slots index 0, weight 0) };
///   u32 meshVertexCount; u32 meshIndexCount; meshVertexCount x { float3 position; float3 normal };
///   meshVertexCount x skin (as strands); meshIndexCount x u32 (triangle list). The mesh is the coat surface the fur grows
///   on, drawn as the undercoat.
/// Strand order is shuffled so any prefix is a uniform subsample (density control = draw fewer instances).
/// </summary>
internal sealed class FurAsset
{
    public const int PointsPerStrand = 4;

    public required string[] BoneNames { get; init; }
    public required int StrandCount { get; init; }
    public required StrandPoint[] Points { get; init; }
    public required StrandSkin[] Skins { get; init; }
    public required MeshVertex[] MeshVertices { get; init; }
    public required StrandSkin[] MeshSkins { get; init; }
    public required uint[] MeshIndices { get; init; }

    public static FurAsset Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var span = bytes.AsSpan();
        if (!span[..4].SequenceEqual("FURA"u8)) throw new InvalidDataException($"{path}: not a .furasset");
        var version = BitConverter.ToUInt32(bytes, 4);
        var strands = checked((int)BitConverter.ToUInt32(bytes, 8));
        var ppc = BitConverter.ToUInt32(bytes, 12);
        var boneCount = checked((int)BitConverter.ToUInt32(bytes, 16));
        if (version != 3 || ppc != PointsPerStrand) throw new InvalidDataException($"{path}: unsupported version {version} / points {ppc}");
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
        var meshAt = checked(offset + pointBytes + skinBytes);
        if (bytes.Length < meshAt + 8) throw new InvalidDataException($"{path}: size mismatch");
        var meshVertices = checked((int)BitConverter.ToUInt32(bytes, meshAt));
        var meshIndices = checked((int)BitConverter.ToUInt32(bytes, meshAt + 4));
        var vertexBytes = checked(meshVertices * Marshal.SizeOf<MeshVertex>());
        var meshSkinBytes = checked(meshVertices * Marshal.SizeOf<StrandSkin>());
        if (bytes.Length != meshAt + 8 + vertexBytes + meshSkinBytes + meshIndices * 4L || meshIndices % 3 != 0)
            throw new InvalidDataException($"{path}: size mismatch");
        var points = MemoryMarshal.Cast<byte, StrandPoint>(span.Slice(offset, pointBytes)).ToArray();
        var skins = MemoryMarshal.Cast<byte, StrandSkin>(span.Slice(offset + pointBytes, skinBytes)).ToArray();
        var vertices = MemoryMarshal.Cast<byte, MeshVertex>(span.Slice(meshAt + 8, vertexBytes)).ToArray();
        var meshSkins = MemoryMarshal.Cast<byte, StrandSkin>(span.Slice(meshAt + 8 + vertexBytes, meshSkinBytes)).ToArray();
        var indices = MemoryMarshal.Cast<byte, uint>(span.Slice(meshAt + 8 + vertexBytes + meshSkinBytes, meshIndices * 4)).ToArray();
        foreach (var skin in skins.Concat(meshSkins))
            for (var k = 0; k < 32; k += 8)
                if ((skin.Bones0 >> k & 0xFF) >= boneCount || (skin.Bones1 >> k & 0xFF) >= boneCount)
                    throw new InvalidDataException($"{path}: bone index out of range");
        if (indices.Any(i => i >= meshVertices)) throw new InvalidDataException($"{path}: mesh index out of range");
        return new FurAsset
        {
            BoneNames = names, StrandCount = strands, Points = points, Skins = skins,
            MeshVertices = vertices, MeshSkins = meshSkins, MeshIndices = indices,
        };
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct StrandPoint
{
    public float X, Y, Z, Radius;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct MeshVertex
{
    public Vector3 Position, Normal;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct StrandSkin
{
    public uint Bones0, Bones1; // u8 bone indices 0-3 and 4-7, little endian
    public Vector4 W0, W1;
}
