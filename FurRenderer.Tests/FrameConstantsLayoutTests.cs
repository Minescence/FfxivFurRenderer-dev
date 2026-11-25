using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace FurRenderer.Tests;

public class FrameConstantsLayoutTests
{
    // HLSL vectors the C# struct spells as separate scalars.
    private static readonly Dictionary<string, string[]> Split = new()
    {
        ["HiZSize"] = ["HiZWidth", "HiZHeight"],
        ["SurfaceLightSize"] = ["SurfaceLightWidth", "SurfaceLightHeight"],
    };

    [Fact]
    public void FrameConstants_matches_the_Fur_hlsl_cbuffer()
    {
        var type = Assembly.Load("FurRenderer").GetType("FurRenderer.FrameConstants", throwOnError: true)!;
        var cs = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => $"{f.Name} @{Marshal.OffsetOf(type, f.Name)} size {Marshal.SizeOf(f.FieldType)}").ToList();

        var (hlsl, hlslSize) = ParseCbuffer(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fur.hlsl")));

        Assert.Equal(hlsl, cs);
        Assert.Equal(hlslSize, (Marshal.SizeOf(type) + 15) / 16 * 16);
    }

    // Packs the "Frame" cbuffer by HLSL rules: 4-byte scalars, a vector never straddles a 16-byte register, matrices start a register.
    private static (List<string> Fields, int Size) ParseCbuffer(string source)
    {
        var body = Regex.Match(source, @"cbuffer\s+Frame\b[^{]*\{([^}]*)\}").Groups[1].Value;
        body = Regex.Replace(body, @"//[^\n]*", "");
        var fields = new List<string>();
        var offset = 0;
        foreach (var decl in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = decl.Replace("row_major", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Assert.True(parts.Length == 2, $"Unparsed cbuffer declaration: '{decl}'");
            var (type, name) = (parts[0], parts[1]);
            var size = type switch
            {
                "float" or "uint" or "int" => 4,
                "float2" or "uint2" => 8,
                "float3" => 12,
                "float4x4" => 64,
                _ => throw new NotSupportedException($"Unknown cbuffer type '{type}' for {name}: teach ParseCbuffer its packing."),
            };
            if (size == 64 || offset % 16 + size > 16) offset = (offset + 15) / 16 * 16;
            if (type is "uint2" or "float2" && Split.TryGetValue(name, out var parts2))
                for (var i = 0; i < parts2.Length; i++) fields.Add($"{parts2[i]} @{offset + 4 * i} size 4");
            else
                fields.Add($"{name} @{offset} size {size}");
            offset += size;
        }
        return (fields, (offset + 15) / 16 * 16);
    }
}
