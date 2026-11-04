using System.Numerics;
using System.Text;

namespace FurRenderer;

/// <summary>
/// The game's pre-bone (race) deformer, <c>chara/xls/boneDeformer/human.pbd</c>. Equipment is modelled for one race
/// (c0201 for the coat) and the game moves its vertices to the wearer's race with per-bone 3x4 matrices before skinning;
/// strands grown on that model need the same step or they float off or sink into the rendered coat.
/// Format and chain walk as in Meddle's PbdFile (Xande).
/// </summary>
internal static class RaceDeformer
{
    /// <summary>Per-bone matrices for each step from <paramref name="from"/> to <paramref name="to"/>, in application order;
    /// empty when the races match, null when <paramref name="to"/> does not derive from <paramref name="from"/>.</summary>
    public static List<Dictionary<string, Matrix4x4>>? Chain(byte[] pbd, ushort from, ushort to)
    {
        var chain = new List<Dictionary<string, Matrix4x4>>();
        var count = BitConverter.ToInt32(pbd, 0);
        int Header(int i) => 4 + i * 12;            // u16 id, u16 link index, i32 deformer offset, f32
        int Link(int i) => 4 + count * 12 + i * 8;  // u16 parent link, first child, next sibling, header index
        for (var current = to; current != from;)
        {
            var h = 0;
            while (h < count && BitConverter.ToUInt16(pbd, Header(h)) != current) h++;
            if (h == count) return null;
            var offset = BitConverter.ToInt32(pbd, Header(h) + 4);
            if (offset <= 0) return null;
            chain.Add(ReadDeformer(pbd, offset));
            var parentLink = BitConverter.ToUInt16(pbd, Link(BitConverter.ToUInt16(pbd, Header(h) + 2)));
            if (parentLink == ushort.MaxValue) return null;
            current = BitConverter.ToUInt16(pbd, Header(BitConverter.ToUInt16(pbd, Link(parentLink) + 6)));
        }
        chain.Reverse();
        return chain;
    }

    private static Dictionary<string, Matrix4x4> ReadDeformer(byte[] pbd, int start)
    {
        var bones = BitConverter.ToInt32(pbd, start);
        var matrices = start + 4 + bones * 2 + bones * 2 % 4;
        var result = new Dictionary<string, Matrix4x4>(bones);
        for (var i = 0; i < bones; i++)
        {
            var nameAt = start + BitConverter.ToInt16(pbd, start + 4 + i * 2);
            var name = Encoding.UTF8.GetString(pbd, nameAt, Array.IndexOf(pbd, (byte)0, nameAt) - nameAt);
            var m = new float[12];
            for (var k = 0; k < 12; k++) m[k] = BitConverter.ToSingle(pbd, matrices + (i * 12 + k) * 4);
            // Stored row-major for column vectors (x' = m0 x + m1 y + m2 z + m3); System.Numerics uses row vectors.
            result[name] = new Matrix4x4(m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1);
        }
        return result;
    }

    /// <summary>One matrix per bone name: the chain's steps composed, each bone falling back to its nearest ancestor's matrix
    /// (as Meddle/Xande do). The game applies it per bone ahead of the skinning matrix, so it goes into the skin palette.</summary>
    public static Matrix4x4[] PerBone(IReadOnlyList<string> boneNames, List<Dictionary<string, Matrix4x4>> chain, IReadOnlyDictionary<string, string> parents)
        => boneNames.Select(name => chain.Aggregate(Matrix4x4.Identity, (acc, step) =>
        {
            for (string? n = name; n != null; n = parents.GetValueOrDefault(n))
                if (step.TryGetValue(n, out var m)) return acc * m;
            return acc;
        })).ToArray();
}
