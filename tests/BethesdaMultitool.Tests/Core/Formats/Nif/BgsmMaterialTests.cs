using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Tests the Fallout 4 / Fallout 76 BGSM material texture-path parser. Fallout 4/76 NIFs reference
///     a material file (under materials\) instead of an inline texture set, so resolving its diffuse +
///     normal paths is what lets those meshes render textured. Verified end-to-end against the real
///     BottleBourbon01.bgsm; these synthesize the layout to lock the version-gated offsets/maps.
/// </summary>
public class BgsmMaterialTests
{
    [Fact]
    public void Parse_Fallout76Bgsm_ExtractsDiffuseAndNormal()
    {
        const string diffuse = "SetDressing/Test/thing_d.dds";
        const string normal = "Shared/flat_n.dds";

        var mat = BgsmMaterial.Parse(BuildBgsm(22, false, 60, diffuse, normal));

        Assert.NotNull(mat);
        Assert.False(mat!.IsEffect);
        Assert.Equal(22, mat.Version);
        Assert.Equal(diffuse, mat.Diffuse);
        Assert.Equal(normal, mat.Normal);
    }

    [Fact]
    public void Parse_Fallout4Bgsm_ExtractsDiffuseAndNormal()
    {
        const string diffuse = "Ammo/10mm/10mmCartridge_d.dds";
        const string normal = "Ammo/10mm/10mmCartridge_n.dds";

        // Fallout 4 (version 2): the gradient flag is at offset 62 and texture paths start at 63.
        var mat = BgsmMaterial.Parse(BuildBgsm(2, false, 63, diffuse, normal));

        Assert.NotNull(mat);
        Assert.Equal(2, mat!.Version);
        Assert.Equal(diffuse, mat.Diffuse);
        Assert.Equal(normal, mat.Normal);
    }

    [Theory]
    [InlineData("XXXX")] // bad magic
    [InlineData("BGSM")] // valid magic but unsupported version below
    public void Parse_InvalidOrUnsupported_ReturnsNull(string magic)
    {
        var data = new byte[64];
        Encoding.ASCII.GetBytes(magic).CopyTo(data, 0);
        data[4] = 99; // unsupported version (not 2, not 20..23)
        Assert.Null(BgsmMaterial.Parse(data));
    }

    [Fact]
    public void Parse_Fallout4Bgsm_ReadsAlphaBlockAndTwoSided()
    {
        // Mirror the real Vine.BGSM header the FO4 foliage fix depends on: opacity 1, blend disabled,
        // src/dst 6/7, alpha-test threshold 92, alpha-test enabled, two-sided. The engine gives these
        // priority over the NIF's inline NiAlphaProperty (which authors 128), so parsing them correctly
        // is what stops the leaf/wire cards from being over-eroded and backface-culled.
        var data = BuildBgsm(2, false, 63, "leaf_d.dds", "leaf_n.dds");
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0x1C), 1f); // opacity
        data[0x20] = 0; // blend enable
        data[0x21] = 6; // source blend
        data[0x25] = 7; // destination blend
        data[0x29] = 92; // alpha-test threshold
        data[0x2A] = 1; // alpha-test enable
        data[0x30] = 1; // two-sided

        var mat = BgsmMaterial.Parse(data);

        Assert.NotNull(mat);
        Assert.Equal(1f, mat!.Alpha);
        Assert.False(mat.AlphaBlendEnabled);
        Assert.Equal(6, mat.SourceBlendMode);
        Assert.Equal(7, mat.DestinationBlendMode);
        Assert.Equal(92, mat.AlphaTestThreshold);
        Assert.True(mat.AlphaTestEnabled);
        Assert.True(mat.TwoSided);
        // Texture paths still resolve after the alpha-block reads.
        Assert.Equal("leaf_d.dds", mat.Diffuse);
        Assert.Equal("leaf_n.dds", mat.Normal);
    }

    /// <summary>
    ///     Builds a minimal BGSM with the given version, a header of <paramref name="headerLength" />
    ///     zero bytes (so the gradient flag reads 0 → the non-gradient texture-path map, whose first two
    ///     slots are diffuse then normal), then the supplied texture-path strings.
    /// </summary>
    private static byte[] BuildBgsm(byte version, bool isEffect, int headerLength, params string[] paths)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes(isEffect ? "BGEM" : "BGSM"));
        bw.Write((uint)version);
        bw.Write(new byte[headerLength - 8]); // pad the rest of the header (8 bytes written so far)
        foreach (var path in paths)
        {
            var bytes = Encoding.ASCII.GetBytes(path);
            bw.Write((uint)(bytes.Length + 1)); // length includes the null terminator
            bw.Write(bytes);
            bw.Write((byte)0);
        }

        bw.Flush();
        return ms.ToArray();
    }
}