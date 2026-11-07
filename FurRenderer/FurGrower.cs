using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace FurRenderer;

/// <summary>
/// Grows the reference fur on the fur meshes of a V6 .mdl: the recipe of tools/grow_coat_fur.py (TheSleeveExperiment's Curves.002
/// stack) without Blender, so any mod using Hikari's fur works without baking or shipping its mesh.
/// </summary>
internal static class FurGrower
{
    /// <summary>Items at or above this are skipped, except the coat: 980k strands on the female model, 1.2M on the male.</summary>
    public const int MaxStrands = 980_000, MaxCoatStrands = 1_300_000;

    // Mod files set the time and memory taken. Far above the largest surveyed (84,789 vertices, 127,488 triangles).
    private const int MaxVertices = 2_000_000, MaxIndices = 6_000_000;

    internal const float StrandsPerM2 = 152_525f; // the baked assets: 980,124 strands on coat_ril_f's 6.426 m2
    private const float MaxLength = 0.05f, RandomTrim = 0.025f;
    private static readonly float[] Radii = [0.0003f, 0.0002535f, 0.0001902f, 0f]; // Set Hair Curve Profile, as baked
    // Clump Hair Curves: guides merged at 2.5 mm, each with a random sideways tip offset of up to 1.5 cm per axis, mixed in at
    // factor 0.3 and about 0.65 falloff for the usual ~1.5 mm from a guide. Convergence on the guide itself is under a millimetre.
    private const float ClumpCell = 0.0025f, ClumpOffset = 0.015f * 0.3f * 0.65f;
    // Strands near an open border lean out across it, so slits between sheets are covered like a real pelt's edge.
    private const float OverhangDistance = 0.045f, OverhangRadians = 85f * MathF.PI / 180f;

    // Full fur buries the face between a hat and a collar (both closed meshes, so no border to lean from). Strands skinned to
    // these bones are shorter, don't point at the face from above or below, and lean back from a border like the trim of a hood.
    private static readonly HashSet<string> AboveFaceBones = ["j_kao", "j_ex_met_va"];
    private static readonly HashSet<string> BelowFaceBones = ["j_kubi", "j_ago"];
    private const float HeadLengthFactor = 0.5f;

    /// <summary>Null when there is no fur surface or it would take <paramref name="maxStrands"/> or more strands.</summary>
    /// <param name="furMaterials">Bit per model material: the ones whose meshes grow fur.</param>
    /// <param name="enabledAttributes">Model.EnabledAttributeIndexMask: the game keeps a submesh if all its attribute bits are enabled.</param>
    public static FurAsset? Grow(byte[] file, uint furMaterials, uint enabledAttributes, int maxStrands = MaxStrands)
    {
        var surface = Surface.Read(file, furMaterials, enabledAttributes);
        var triangles = surface.Triangles;
        var triangleCount = triangles.Count / 3;
        var cumulative = new double[triangleCount];
        var area = 0.0;
        for (var t = 0; t < triangleCount; t++)
        {
            var a = surface.Positions[triangles[t * 3]];
            area += 0.5 * Vector3.Cross(surface.Positions[triangles[t * 3 + 1]] - a, surface.Positions[triangles[t * 3 + 2]] - a).Length();
            cumulative[t] = area;
        }
        var strands = (int)Math.Round(area * StrandsPerM2);
        if (strands == 0 || strands >= maxStrands) return null;

        var border = surface.BorderSamples();
        var rng = new Random(246);
        var points = new StrandPoint[strands * FurAsset.PointsPerStrand];
        var skins = new StrandSkin[strands];
        var surfaces = new StrandSurface[strands];
        var mergedBones = new byte[24];
        var mergedWeights = new float[24];
        // Picked at random over the whole surface, so strand order is already shuffled: any prefix is a uniform subsample.
        for (var i = 0; i < strands; i++)
        {
            var t = Array.BinarySearch(cumulative, rng.NextDouble() * area);
            t = Math.Min(t < 0 ? ~t : t, triangleCount - 1);
            float u = rng.NextSingle(), v = rng.NextSingle();
            if (u + v > 1) (u, v) = (1 - u, 1 - v);
            var w = 1 - u - v;
            int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
            var root = surface.Positions[i0] * w + surface.Positions[i1] * u + surface.Positions[i2] * v;
            var normal = Unit(surface.Normals[i0] * w + surface.Normals[i1] * u + surface.Normals[i2] * v);
            var above = surface.AboveFace[i0] * w + surface.AboveFace[i1] * u + surface.AboveFace[i2] * v;
            var below = surface.BelowFace[i0] * w + surface.BelowFace[i1] * u + surface.BelowFace[i2] * v;
            var head = above + below;
            normal.Y *= 1 - (normal.Y < 0 ? above : below);
            normal = Unit(normal);
            var side = Unit(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var (clumpX, clumpY) = ClumpDirection(root);
            var length = (MaxLength - rng.NextSingle() * RandomTrim) * (1 - (1 - HeadLengthFactor) * head);
            var strand = (normal * MaxLength + (side * clumpX + Vector3.Cross(normal, side) * clumpY) * ClumpOffset) * (length / MaxLength);
            if (border.Nearest(root, OverhangDistance, out var edge, out var distance))
                strand = Lean(strand, normal, edge - root, OverhangRadians * (1 - distance / OverhangDistance) * (1 - 2 * head));
            for (var k = 0; k < FurAsset.PointsPerStrand; k++)
            {
                var p = root + strand * (k / (FurAsset.PointsPerStrand - 1f));
                points[i * FurAsset.PointsPerStrand + k] = new StrandPoint { X = p.X, Y = p.Y, Z = p.Z, Radius = Radii[k] };
            }
            skins[i] = surface.RootSkin(i0, i1, i2, w, u, v, mergedBones, mergedWeights);
            var uv = surface.Uvs[i0] is { } uv0 && surface.Uvs[i1] is { } uv1 && surface.Uvs[i2] is { } uv2 ? uv0 * w + uv1 * u + uv2 * v : (Vector2?)null;
            surfaces[i] = new StrandSurface(surface.Materials[i0], uv);
        }

        // Undercoat: the fur surface itself.
        var remap = new uint[surface.Positions.Count];
        Array.Fill(remap, uint.MaxValue);
        var meshVertices = new List<MeshVertex>();
        var meshSkins = new List<StrandSkin>();
        var meshSurfaces = new List<StrandSurface>();
        var meshIndices = new uint[triangles.Count];
        for (var i = 0; i < triangles.Count; i++)
        {
            var vertex = triangles[i];
            var index = remap[vertex];
            if (index == uint.MaxValue)
            {
                remap[vertex] = index = (uint)meshVertices.Count;
                meshVertices.Add(new MeshVertex { Position = surface.Positions[vertex], Normal = surface.Normals[vertex] });
                meshSkins.Add(surface.VertexSkin(vertex));
                meshSurfaces.Add(new StrandSurface(surface.Materials[vertex], surface.Uvs[vertex]));
            }
            meshIndices[i] = index;
        }
        return new FurAsset
        {
            BoneNames = surface.BoneNames.ToArray(), StrandCount = strands, Points = points, Skins = skins, StrandSurfaces = surfaces,
            MeshVertices = meshVertices.ToArray(), MeshSurfaces = meshSurfaces.ToArray(), MeshSkins = meshSkins.ToArray(), MeshIndices = meshIndices,
        };
    }

    private static Vector3 Unit(Vector3 v)
    {
        var length = v.Length();
        return length > 1e-6f ? v / length : Vector3.UnitY;
    }

    private static (float X, float Y) ClumpDirection(Vector3 root)
    {
        // A fixed hash (lowbias32 finaliser): HashCode is seeded per process, so the clumps changed with every game start.
        var bits = unchecked((uint)(int)MathF.Floor(root.X / ClumpCell) * 73856093u ^ (uint)(int)MathF.Floor(root.Y / ClumpCell) * 19349663u
                             ^ (uint)(int)MathF.Floor(root.Z / ClumpCell) * 83492791u);
        bits ^= bits >> 16;
        bits *= 0x7FEB352Du;
        bits ^= bits >> 15;
        bits *= 0x846CA68Bu;
        bits ^= bits >> 16;
        return ((bits & 0xFFFF) / 32767.5f - 1f, (bits >> 16) / 32767.5f - 1f);
    }

    /// <summary>Turns <paramref name="strand"/> from <paramref name="normal"/> towards <paramref name="towards"/> (Rodrigues).</summary>
    private static Vector3 Lean(Vector3 strand, Vector3 normal, Vector3 towards, float radians)
    {
        var across = towards - normal * Vector3.Dot(towards, normal);
        if (across.LengthSquared() < 1e-12f) return strand;
        var axis = Vector3.Cross(normal, Vector3.Normalize(across));
        var (sin, cos) = MathF.SinCos(radians);
        return strand * cos + Vector3.Cross(axis, strand) * sin + axis * (Vector3.Dot(strand, axis) * (1 - cos));
    }

    /// <summary>Nearest-point lookup within the cell size.</summary>
    private sealed class PointGrid(float cell)
    {
        private readonly Dictionary<(int, int, int), List<Vector3>> cells = [];

        private (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

        public void Add(Vector3 p)
        {
            var key = Cell(p);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(p);
        }

        public bool Nearest(Vector3 p, float within, out Vector3 nearest, out float distance)
        {
            nearest = default;
            var best = within * within;
            var found = false;
            var (cx, cy, cz) = Cell(p);
            for (var x = cx - 1; x <= cx + 1; x++)
            for (var y = cy - 1; y <= cy + 1; y++)
            for (var z = cz - 1; z <= cz + 1; z++)
            {
                if (!cells.TryGetValue((x, y, z), out var list)) continue;
                foreach (var q in list)
                {
                    var d = Vector3.DistanceSquared(p, q);
                    if (d >= best) continue;
                    (best, nearest, found) = (d, q, true);
                }
            }
            distance = MathF.Sqrt(best);
            return found;
        }
    }

    /// <summary>The fur meshes of a model's first LOD, merged, with smooth normals and only the bones they use.</summary>
    private sealed class Surface
    {
        public readonly List<Vector3> Positions = [];
        public readonly List<Vector3> Normals = [];
        public readonly List<byte> Bones = [];    // 8 per vertex
        public readonly List<float> Weights = []; // 8 per vertex
        public readonly List<uint> Materials = []; // per vertex: rank of its mesh's material among the fur materials
        public readonly List<Vector2?> Uvs = [];    // per vertex: UV0, if its mesh has one
        public readonly List<int> Triangles = [];
        public readonly List<string> BoneNames = [];
        public float[] AboveFace = [], BelowFace = []; // skin weight on those bones, per vertex
        private int[] weld = [];
        private Vector3[] weldPositions = [];

        public static Surface Read(byte[] file, uint furMaterials, uint enabledAttributes)
        {
            using var r = new BinaryReader(new MemoryStream(file));
            void Skip(long bytes) => r.BaseStream.Position += bytes;

            var version = r.ReadUInt32();
            if (version != 0x01000006) throw new InvalidDataException($"model version 0x{version:X} is not supported");
            Skip(8);
            int declarationCount = r.ReadUInt16();
            Skip(2);
            var vertexStart = r.ReadUInt32();
            Skip(8);
            var indexStart = r.ReadUInt32();
            Skip(8 + 24 + 4);
            var declarations = new Dictionary<byte, (byte Stream, byte Offset, byte Type)>[declarationCount];
            for (var d = 0; d < declarationCount; d++)
            {
                declarations[d] = [];
                var ended = false;
                for (var e = 0; e < 17; e++)
                {
                    byte stream = r.ReadByte(), offset = r.ReadByte(), type = r.ReadByte(), usage = r.ReadByte(), usageIndex = r.ReadByte();
                    Skip(3);
                    ended |= stream == 255;
                    if (!ended && usageIndex == 0) declarations[d][usage] = (stream, offset, type);
                }
            }
            Skip(4);
            var strings = r.ReadBytes(r.ReadInt32());
            Skip(4);
            int meshCount = r.ReadUInt16(), attributeCount = r.ReadUInt16(), submeshCount = r.ReadUInt16(), materialCount = r.ReadUInt16(), boneCount = r.ReadUInt16(), boneTableCount = r.ReadUInt16();
            Skip(6 + 2);
            int elementIdCount = r.ReadUInt16(), terrainShadowMeshCount = r.ReadByte(), flags2 = r.ReadByte();
            Skip(8 + 2);
            int terrainShadowSubmeshCount = r.ReadUInt16();
            Skip(4 + 2 + 10);
            Skip(elementIdCount * 32);
            int firstMesh = r.ReadUInt16(), lodMeshCount = r.ReadUInt16();
            Skip(56 + 2 * 60);
            if ((flags2 & 0x10) != 0) Skip(3 * 40);
            var meshes = new (int Vertices, int Indices, int Material, int SubStart, int SubCount, int BoneTable, int Start, uint[] Offsets, byte[] Strides)[meshCount];
            for (var m = 0; m < meshCount; m++)
            {
                int vertices = r.ReadUInt16();
                Skip(2);
                var indices = r.ReadInt32();
                int material = r.ReadUInt16(), subStart = r.ReadUInt16(), subCount = r.ReadUInt16(), boneTable = r.ReadUInt16();
                var start = r.ReadInt32();
                uint[] offsets = [r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()];
                byte[] strides = [r.ReadByte(), r.ReadByte(), r.ReadByte()];
                Skip(1);
                meshes[m] = (vertices, indices, material, subStart, subCount, boneTable, start, offsets, strides);
            }
            Skip(attributeCount * 4 + terrainShadowMeshCount * 20);
            var submeshes = new (int Offset, int Count, uint Attributes)[submeshCount];
            for (var s = 0; s < submeshCount; s++)
            {
                submeshes[s] = (r.ReadInt32(), r.ReadInt32(), r.ReadUInt32());
                Skip(4);
            }
            Skip(terrainShadowSubmeshCount * 12 + materialCount * 4);
            var boneNames = new string[boneCount];
            for (var b = 0; b < boneCount; b++)
            {
                var at = r.ReadInt32();
                boneNames[b] = Encoding.UTF8.GetString(strings, at, Array.IndexOf(strings, (byte)0, at) - at);
            }
            var tablesAt = (int)r.BaseStream.Position;
            var boneTables = new ushort[boneTableCount][];
            for (var t = 0; t < boneTableCount; t++)
            {
                var header = tablesAt + t * 4;
                var table = new ushort[BitConverter.ToUInt16(file, header + 2)];
                for (var k = 0; k < table.Length; k++)
                    table[k] = BitConverter.ToUInt16(file, header + BitConverter.ToUInt16(file, header) * 4 + k * 2);
                boneTables[t] = table;
            }

            var surface = new Surface();
            var usedBones = new Dictionary<int, byte>();
            for (var m = firstMesh; m < firstMesh + lodMeshCount; m++)
            {
                var mesh = meshes[m];
                if (mesh.Material >= 32 || (furMaterials >> mesh.Material & 1) == 0) continue;
                // Vertex layouts of all 47 fur models surveyed: Single3 position and normal, 4 influences (UByte4/NByte4) or 8 (UShort4 holding 8 bytes).
                var position = Element(declarations[m], 0, 2);
                var normal = Element(declarations[m], 3, 2);
                var weights = Element(declarations[m], 1);
                var joints = Element(declarations[m], 2);
                var influences = Influences(weights.Type);
                if (Influences(joints.Type) != influences) throw new InvalidDataException("skin weights and indices differ in size");
                var table = boneTables[mesh.BoneTable];
                int At((byte Stream, byte Offset, byte Type) e, int vertex) => (int)(vertexStart + mesh.Offsets[e.Stream]) + vertex * mesh.Strides[e.Stream] + e.Offset;
                var first = surface.Positions.Count;
                if (first + mesh.Vertices > MaxVertices) throw new InvalidDataException($"fur meshes have more than {MaxVertices:N0} vertices");
                var materialRank = (uint)BitOperations.PopCount(furMaterials & ((1u << mesh.Material) - 1));
                // All surveyed fur meshes have one (Single2 or Single4); without it the strands take the material's first colour.
                var hasUv = declarations[m].TryGetValue(4, out var uv) && uv.Type is 1 or 3 or 13 or 14;
                for (var v = 0; v < mesh.Vertices; v++)
                {
                    var vertexPosition = ReadVector3(file, At(position, v));
                    var vertexNormal = ReadVector3(file, At(normal, v));
                    // Written so that NaN fails it too.
                    if (!(vertexPosition.LengthSquared() < 1e6f && vertexNormal.LengthSquared() < 1e6f)) throw new InvalidDataException($"vertex {v} is out of range");
                    surface.Positions.Add(vertexPosition);
                    surface.Normals.Add(vertexNormal);
                    surface.Materials.Add(materialRank);
                    surface.Uvs.Add(hasUv ? ReadUv(file, At(uv, v), uv.Type) : null);
                    int weightsAt = At(weights, v), jointsAt = At(joints, v);
                    for (var k = 0; k < 8; k++)
                    {
                        var weight = k < influences ? file[weightsAt + k] : (byte)0;
                        byte bone = 0;
                        if (weight > 0)
                        {
                            int global = table[Math.Min(file[jointsAt + k], table.Length - 1)];
                            if (!usedBones.TryGetValue(global, out bone))
                            {
                                if (usedBones.Count == Renderer.MaxBones) throw new InvalidDataException($"fur meshes use more than {Renderer.MaxBones} bones");
                                usedBones[global] = bone = (byte)usedBones.Count;
                                surface.BoneNames.Add(boneNames[global]);
                            }
                        }
                        surface.Bones.Add(bone);
                        surface.Weights.Add(weight / 255f);
                    }
                }
                void AddIndices(int offset, int count)
                {
                    if (surface.Triangles.Count + count > MaxIndices) throw new InvalidDataException($"fur meshes have more than {MaxIndices:N0} indices");
                    for (var i = 0; i < count - count % 3; i++)
                        surface.Triangles.Add(first + BitConverter.ToUInt16(file, (int)indexStart + (offset + i) * 2));
                }
                if (mesh.SubCount == 0) AddIndices(mesh.Start, mesh.Indices);
                for (var s = mesh.SubStart; s < mesh.SubStart + mesh.SubCount; s++)
                {
                    var (offset, count, attributes) = submeshes[s];
                    if ((attributes & enabledAttributes) == attributes) AddIndices(offset, count);
                }
            }
            surface.SmoothNormals();
            surface.AboveFace = surface.WeightOn(AboveFaceBones);
            surface.BelowFace = surface.WeightOn(BelowFaceBones);
            return surface;
        }

        private float[] WeightOn(HashSet<string> bones)
        {
            var on = BoneNames.Select(bones.Contains).ToArray();
            var weights = new float[Positions.Count];
            for (var v = 0; v < weights.Length; v++)
            for (var k = 0; k < 8; k++)
                if (Weights[v * 8 + k] > 0 && on[Bones[v * 8 + k]])
                    weights[v] += Weights[v * 8 + k];
            return weights;
        }

        private static (byte Stream, byte Offset, byte Type) Element(Dictionary<byte, (byte Stream, byte Offset, byte Type)> declaration, byte usage, int requiredType = -1)
        {
            if (!declaration.TryGetValue(usage, out var element)) throw new InvalidDataException($"mesh has no vertex element of usage {usage}");
            if (requiredType >= 0 && element.Type != requiredType) throw new InvalidDataException($"vertex element of usage {usage} has unsupported type {element.Type}");
            return element;
        }

        private static int Influences(byte type) => type switch
        {
            5 or 8 => 4,
            17 => 8,
            _ => throw new InvalidDataException($"unsupported skin element type {type}"),
        };

        private static Vector2 ReadUv(byte[] file, int at, byte type) => type is 13 or 14
            ? new((float)BitConverter.ToHalf(file, at), (float)BitConverter.ToHalf(file, at + 2))
            : new(BitConverter.ToSingle(file, at), BitConverter.ToSingle(file, at + 4));

        private static Vector3 ReadVector3(byte[] file, int at)
            => new(BitConverter.ToSingle(file, at), BitConverter.ToSingle(file, at + 4), BitConverter.ToSingle(file, at + 8));

        // The game splits vertices at UV and material seams, which would part the fur there: average over vertices sharing a
        // position. Only those facing the same way: the double-sided pants would cancel to nothing, and each side has its own borders.
        private void SmoothNormals()
        {
            weld = new int[Positions.Count];
            var byPosition = new Dictionary<(int, int, int), List<int>>();
            var sums = new List<Vector3>();
            var firstNormals = new List<Vector3>();
            var firstPositions = new List<Vector3>();
            for (var v = 0; v < Positions.Count; v++)
            {
                var p = Positions[v];
                var key = ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f));
                if (!byPosition.TryGetValue(key, out var candidates)) byPosition[key] = candidates = [];
                // Not > 0: every zero normal would open a group of its own, and a file of them would take hours here.
                var id = candidates.FindIndex(c => Vector3.Dot(firstNormals[c], Normals[v]) >= 0);
                id = id >= 0 ? candidates[id] : sums.Count;
                if (id == sums.Count)
                {
                    candidates.Add(id);
                    sums.Add(Vector3.Zero);
                    firstNormals.Add(Normals[v]);
                    firstPositions.Add(p);
                }
                weld[v] = id;
                sums[id] += Normals[v];
            }
            for (var v = 0; v < Normals.Count; v++) Normals[v] = Unit(sums[weld[v]]);
            weldPositions = firstPositions.ToArray();
        }

        /// <summary>Six points along every edge used by one triangle only.</summary>
        public PointGrid BorderSamples()
        {
            var uses = new Dictionary<(int, int), int>();
            for (var t = 0; t < Triangles.Count; t += 3)
            for (var e = 0; e < 3; e++)
            {
                int a = weld[Triangles[t + e]], b = weld[Triangles[t + (e + 1) % 3]];
                var key = a < b ? (a, b) : (b, a);
                CollectionsMarshal.GetValueRefOrAddDefault(uses, key, out _)++;
            }
            var grid = new PointGrid(OverhangDistance);
            foreach (var ((a, b), count) in uses)
            {
                if (count != 1) continue;
                for (var k = 0; k < 6; k++)
                    grid.Add(Vector3.Lerp(weldPositions[a], weldPositions[b], k / 5f));
            }
            return grid;
        }

        public StrandSkin VertexSkin(int vertex)
        {
            Span<byte> bones = stackalloc byte[8];
            Span<float> weights = stackalloc float[8];
            for (var k = 0; k < 8; k++)
            {
                bones[k] = Bones[vertex * 8 + k];
                weights[k] = Weights[vertex * 8 + k];
            }
            return Pack(bones, weights);
        }

        /// <summary>Barycentric blend of the corners' skins, keeping the 8 largest influences like the game's Dawntrail skinning.</summary>
        public StrandSkin RootSkin(int i0, int i1, int i2, float w0, float w1, float w2, byte[] mergedBones, float[] mergedWeights)
        {
            var merged = 0;
            void Merge(int vertex, float corner)
            {
                for (var k = 0; k < 8; k++)
                {
                    var weight = Weights[vertex * 8 + k];
                    if (weight <= 0) continue;
                    var bone = Bones[vertex * 8 + k];
                    var slot = Array.IndexOf(mergedBones, bone, 0, merged);
                    if (slot < 0)
                    {
                        slot = merged++;
                        mergedBones[slot] = bone;
                        mergedWeights[slot] = 0;
                    }
                    mergedWeights[slot] += weight * corner;
                }
            }
            Merge(i0, w0);
            Merge(i1, w1);
            Merge(i2, w2);
            Span<byte> bones = stackalloc byte[8];
            Span<float> weights = stackalloc float[8];
            var total = 0f;
            for (var k = 0; k < Math.Min(merged, 8); k++)
            {
                var largest = k;
                for (var j = k + 1; j < merged; j++)
                    if (mergedWeights[j] > mergedWeights[largest]) largest = j;
                (bones[k], weights[k]) = (mergedBones[largest], mergedWeights[largest]);
                (mergedBones[largest], mergedWeights[largest]) = (mergedBones[k], mergedWeights[k]);
                total += weights[k];
            }
            if (total > 0)
                for (var k = 0; k < 8; k++) weights[k] /= total;
            return Pack(bones, weights);
        }

        private static StrandSkin Pack(ReadOnlySpan<byte> bones, ReadOnlySpan<float> weights) => new()
        {
            Bones0 = (uint)(bones[0] | bones[1] << 8 | bones[2] << 16 | bones[3] << 24),
            Bones1 = (uint)(bones[4] | bones[5] << 8 | bones[6] << 16 | bones[7] << 24),
            W0 = new Vector4(weights[0], weights[1], weights[2], weights[3]),
            W1 = new Vector4(weights[4], weights[5], weights[6], weights[7]),
        };
    }
}
