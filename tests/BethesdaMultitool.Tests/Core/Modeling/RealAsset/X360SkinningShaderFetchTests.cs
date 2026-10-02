using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The evidence under the X360 engine skin lanes (<c>NifPackedEngineLanes</c>): every skinning vertex shader in the
///     retail X360 <c>Data/Shaders/shaderpackage.sdp</c> fetches BLENDWEIGHT with at most three written components and
///     never selects the stored fourth half, while it fetches BLENDINDICES with all four, the fourth included. So the
///     engine never reads the stored fourth weight and does read the fourth bone index, which is what makes the reader
///     derive the fourth weight as 1 - ((w0 + w1) + w2) on the stored slot-3 bone.
/// </summary>
/// <remarks>
///     <para>
///         The package (big-endian): u32, u32 record count, u32, then records of a 256-byte NUL-padded name, a u32 size
///         and the blob. A vertex shader (<c>.vso</c>) blob is Xenos microcode (magic 0x102A11xx): word 1 is the data
///         section's offset, word 2 its size (together the blob's length), word 6 the offset of a program-info block whose
///         first two words are the microcode's offset from the data section and its size (the microcode ends the blob).
///         The vertex-input table is a run of words <c>(usage &lt;&lt; 12) | fetch address</c> (D3DDECLUSAGE numbering:
///         0 POSITION, 1 BLENDWEIGHT, 2 BLENDINDICES, 3 NORMAL, 5 TEXCOORD, 6 TANGENT, 7 BINORMAL, 10 COLOR), found as the
///         longest run of at least two whose addresses each hold a vertex fetch. The microcode is 96-bit instructions; a
///         vertex fetch has opcode bits 0-4 of dword 0 clear and bit 19 set, and dword 1 bits 0-11 are its destination
///         swizzle, three bits per x, y, z, w: 0-3 select a source component, 4 is 0.0, 5 is 1.0 and 7 leaves the
///         component unwritten (the Adreno a2xx encoding, freedreno instr-a2xx.h). The stored microcode carries no
///         stride, offset or format (the runtime patches them from the declaration), so only the swizzle is read.
///     </para>
///     <para>
///         This is the design receipt's parse (TestOutput/gate1b-gap-designs-20260927/one-offs/c_x360_vs_weightfetch.py)
///         ported, and its Python mirror (TestOutput/gap-impl-20260927/console-weights/measurement/x360_fetch_mirror.py)
///         measured every number pinned here, 2026-09-27. Controls, so a parser that could not see a fourth component or
///         that reported BLENDWEIGHT everywhere fails: BLENDINDICES in the same 93 shaders writes four components and
///         selects the source w on every fetch; TEXCOORD writes exactly two on every one of its 71 fetches there; and
///         288 parsed vertex shaders fetch POSITION without BLENDWEIGHT, SLS1000.vso among them (POSITION xyzw, TEXCOORD
///         xy__). 24 blobs yield no vertex-input table; the design found none of them skinned on PC.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class X360SkinningShaderFetchTests
{
    private const string ShaderPackage =
        "Builds/Fallout - New Vegas (2010-8-22, X360 - Final)/Data/Shaders/shaderpackage.sdp";

    private const string ShaderPackageSha256 = "9d0505ca6c547a5a06355e0e21a1748603438da3d715ab896d7bbc3b6fe39db8";
    private const int Position = 0;
    private const int BlendWeight = 1;
    private const int BlendIndices = 2;
    private const int TexCoord = 5;
    private const int SourceW = 3;
    private const int Unwritten = 7;
    private const string Selectors = "xyzw01?_";
    private static readonly int[] KnownUsages = [0, 1, 2, 3, 5, 6, 7, 10];

    [Fact]
    public void SkinningVertexShaders_NeverFetchTheFourthBlendWeight_AndFetchAllFourBlendIndices()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = RealAssetPaths.SampleFile(ShaderPackage);
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage(ShaderPackage));
        var package = File.ReadAllBytes(path!);
        Assert.Equal(ShaderPackageSha256, Convert.ToHexStringLower(SHA256.HashData(package)));

        var shaders = VertexShaders(package);
        Assert.Equal(405, shaders.Count);
        Assert.Equal(24, shaders.Count(static shader => shader.Fetches is null));
        var skinned = shaders.Where(static shader => shader.Fetches?.ContainsKey(BlendWeight) == true)
            .Select(static shader => (shader.Name, Fetches: shader.Fetches!)).ToList();
        Assert.Equal(93, skinned.Count);

        // The claim: BLENDWEIGHT writes at most three components and none of them is the stored fourth half.
        var weights = skinned.SelectMany(static shader => shader.Fetches[BlendWeight]).ToList();
        Assert.Equal(93, weights.Count);
        Assert.All(weights, static swizzle =>
        {
            Assert.True(Written(swizzle) <= 3, $"a BLENDWEIGHT fetch writes {Swizzle(swizzle)}");
            Assert.False(Selects(swizzle, SourceW), $"a BLENDWEIGHT fetch selects the fourth half: {Swizzle(swizzle)}");
        });
        Assert.Equal(new[] { ("_xyz", 8), ("xyz_", 85) }, Census(weights));

        // Parser control: the same shaders fetch POSITION, and BLENDINDICES with four components, the fourth included.
        Assert.All(skinned, static shader =>
        {
            Assert.True(shader.Fetches.ContainsKey(Position), $"{shader.Name} fetches no POSITION");
            Assert.True(shader.Fetches.ContainsKey(BlendIndices), $"{shader.Name} fetches no BLENDINDICES");
        });
        var indices = skinned.SelectMany(static shader => shader.Fetches[BlendIndices]).ToList();
        Assert.All(indices, static swizzle =>
        {
            Assert.Equal(4, Written(swizzle));
            Assert.True(Selects(swizzle, SourceW), $"a BLENDINDICES fetch skips the fourth index: {Swizzle(swizzle)}");
        });
        Assert.Equal(new[] { ("wxyz", 62), ("xyzw", 31) }, Census(indices));

        // Control: TEXCOORD in the same shaders writes exactly two components.
        var texCoords = skinned.SelectMany(static shader => shader.Fetches.GetValueOrDefault(TexCoord) ?? new List<uint>()).ToList();
        Assert.All(texCoords, static swizzle => Assert.Equal(2, Written(swizzle)));
        Assert.Equal(new[] { ("_xy_", 11), ("xy__", 56), ("yx__", 4) }, Census(texCoords));

        // Control: static shaders parse and carry no BLENDWEIGHT, so the parser does not report it everywhere.
        var statics = shaders.Where(static shader => shader.Fetches is { } fetches && !fetches.ContainsKey(BlendWeight)).ToList();
        Assert.Equal(288, statics.Count);
        Assert.All(statics, static shader => Assert.True(shader.Fetches!.ContainsKey(Position), shader.Name));
        var sls1000 = Assert.Single(shaders, static shader => shader.Name == "SLS1000.vso").Fetches;
        Assert.NotNull(sls1000);
        Assert.Equal(new[] { Position, TexCoord }, sls1000.Keys.Order());
        Assert.Equal("xyzw", Swizzle(Assert.Single(sls1000[Position])));
        Assert.Equal("xy__", Swizzle(Assert.Single(sls1000[TexCoord])));
    }

    /// <summary>Every <c>.vso</c> record of the package, with its fetch swizzles per usage, or null when no table parses.</summary>
    private static List<(string Name, Dictionary<int, List<uint>>? Fetches)> VertexShaders(byte[] package)
    {
        var count = U32(package, 4);
        var offset = 12;
        var shaders = new List<(string, Dictionary<int, List<uint>>?)>();
        for (var record = 0u; record < count; record++)
        {
            var raw = package.AsSpan(offset, 0x100);
            var end = raw.IndexOf((byte)0);
            var name = Encoding.Latin1.GetString(end < 0 ? raw : raw[..end]);
            var size = (int)U32(package, offset + 0x100);
            var blob = package.AsSpan(offset + 0x104, size).ToArray();
            offset += 0x104 + size;
            if (name.EndsWith(".vso", StringComparison.OrdinalIgnoreCase))
            {
                shaders.Add((name, Fetches(blob)));
            }
        }

        Assert.Equal(package.Length, offset);
        return shaders;
    }

    /// <summary>One vertex shader's fetch swizzles per usage (see the type remarks), or null when its table does not parse.</summary>
    private static Dictionary<int, List<uint>>? Fetches(byte[] blob)
    {
        if (blob.Length < 28 || (U32(blob, 0) & 0xFFFFFF00u) != 0x102A1100u)
        {
            return null;
        }

        long data = U32(blob, 4), size = U32(blob, 8), info = U32(blob, 24);
        if (info + 8 > blob.Length)
        {
            return null;
        }

        var microcode = data + U32(blob, (int)info);
        long microcodeSize = U32(blob, (int)info + 4);
        if (data + size != blob.Length || microcode + microcodeSize != blob.Length || microcodeSize % 12 != 0)
        {
            return null;
        }

        var instructions = microcodeSize / 12;
        List<uint>? best = null;
        var at = info + 8;
        while (at < data)
        {
            var run = new List<uint>();
            var k = at;
            while (k < data && k + 4 <= blob.Length)
            {
                var entry = U32(blob, (int)k);
                var address = entry & 0xFFFu;
                if ((entry >> 24) != 0 || Array.IndexOf(KnownUsages, (int)((entry >> 12) & 0xF)) < 0 ||
                    Fetch(blob, microcode, instructions, address) is null ||
                    run.Exists(previous => (previous & 0xFFFu) == address))
                {
                    break;
                }

                run.Add(entry);
                k += 4;
            }

            if (run.Count >= 2 && (best is null || run.Count > best.Count))
            {
                best = run;
            }

            at = run.Count > 0 ? k + 4 : at + 4;
        }

        if (best is null)
        {
            return null;
        }

        var fetches = new Dictionary<int, List<uint>>();
        foreach (var entry in best)
        {
            var swizzle = Fetch(blob, microcode, instructions, entry & 0xFFFu)!.Value;
            var usage = (int)((entry >> 12) & 0xF);
            if (!fetches.TryGetValue(usage, out var list))
            {
                fetches[usage] = list = [];
            }

            list.Add(swizzle);
        }

        return fetches;
    }

    /// <summary>The destination swizzle (dword 1) of the vertex fetch at an instruction address, or null when none is there.</summary>
    private static uint? Fetch(byte[] blob, long microcode, long instructions, uint address)
    {
        if (address >= instructions)
        {
            return null;
        }

        var at = (int)(microcode + 12L * address);
        var first = U32(blob, at);
        if ((first & 0x1F) != 0 || ((first >> 19) & 1) == 0)
        {
            return null;
        }

        return U32(blob, at + 4);
    }

    /// <summary>The selector of destination component i (0 x .. 3 w).</summary>
    private static int Selector(uint swizzle, int component)
    {
        return (int)((swizzle >> (3 * component)) & 7);
    }

    /// <summary>The destination components a fetch writes.</summary>
    private static int Written(uint swizzle)
    {
        var written = 0;
        for (var component = 0; component < 4; component++)
        {
            if (Selector(swizzle, component) != Unwritten)
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>Whether any destination component selects the given source component.</summary>
    private static bool Selects(uint swizzle, int source)
    {
        for (var component = 0; component < 4; component++)
        {
            if (Selector(swizzle, component) == source)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The swizzle as four selector characters (x, y, z, w, 0, 1, ?, _).</summary>
    private static string Swizzle(uint swizzle)
    {
        var text = new char[4];
        for (var component = 0; component < 4; component++)
        {
            text[component] = Selectors[Selector(swizzle, component)];
        }

        return new string(text);
    }

    /// <summary>Swizzle counts in ordinal order of the swizzle text.</summary>
    private static List<(string Swizzle, int Count)> Census(IEnumerable<uint> swizzles)
    {
        return swizzles.GroupBy(Swizzle).Select(static group => (group.Key, group.Count()))
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal).ToList();
    }

    private static uint U32(byte[] bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    }
}
