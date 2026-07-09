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
        // The decal byte was untouched (0) — not a decal.
        Assert.False(mat.Decal);
        // Texture paths still resolve after the alpha-block reads.
        Assert.Equal("leaf_d.dds", mat.Diffuse);
        Assert.Equal("leaf_n.dds", mat.Normal);
    }

    [Fact]
    public void Parse_Fallout4Bgsm_ReadsDecalByte()
    {
        // Decal byte @0x2F (fo76utils loadBGSMFile: u32 z-write/z-test/SSR @0x2B, u8 decal, u8
        // two-sided). Grime/crack overlay materials set it; the renderer keys a depth-biased PSO
        // off the flag so the overlay stops z-fighting its backing surface.
        var data = BuildBgsm(2, false, 63, "grime_d.dds", "grime_n.dds");
        data[0x2F] = 1;

        var mat = BgsmMaterial.Parse(data);

        Assert.NotNull(mat);
        Assert.True(mat!.Decal);
        Assert.False(mat.TwoSided); // neighbor byte untouched — offsets don't bleed
    }

    [Fact]
    public void Parse_Fallout4Bgem_ReadsEffectLightingTintAndFalloff()
    {
        // FO4 BGEM tail (fo76utils loadBGEMFile): after the texture-path table, a 6-byte bool block
        // ([1] = Effect Lighting, [2]|[3] = falloff enabled), then base color RGB + scale (4 floats),
        // falloff params (4 floats: startAngle/stopAngle/startOpacity/stopOpacity), and the lighting
        // influence float. Values mirror AmbBeamMistRoundDusty.BGEM — the terms whose absence
        // rendered mist blobs blinding white. The non-gradient FO4 BGEM path map (0x000514F0) reads
        // FIVE strings, so five paths pad the table.
        var head = BuildBgsm(2, true, 63, "fx_d.dds", "grad.dds", "", "", "");
        using var ms = new MemoryStream();
        ms.Write(head);
        ms.Write(new byte[] { 0, 1, 1, 0, 0, 0 }); // bools: effect lighting ON, falloff ON
        Span<byte> floats = stackalloc byte[36];    // base color (16) + falloff (16) + influence (4)
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[..4], 0.478f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[4..8], 0.478f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[8..12], 0.478f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[12..16], 0.75f);  // scale
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[16..20], 0.98481f); // start angle
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[20..24], 0.17365f); // stop angle
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[24..28], 1f);       // start opacity
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[28..32], 0f);       // stop opacity
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[32..], 0.95f);      // influence
        ms.Write(floats);

        var mat = BgsmMaterial.Parse(ms.ToArray());

        Assert.NotNull(mat);
        Assert.True(mat!.IsEffect);
        Assert.True(mat.EffectLightingEnabled);
        Assert.True(mat.FalloffEnabled);
        Assert.Equal(0.478f, mat.BaseColor.X, 3);
        Assert.Equal(0.75f, mat.BaseColorScale, 3);
        Assert.Equal(0.98481f, mat.FalloffStartAngle, 4);
        Assert.Equal(0.17365f, mat.FalloffStopAngle, 4);
        Assert.Equal(1f, mat.FalloffStartOpacity, 3);
        Assert.Equal(0f, mat.FalloffStopOpacity, 3);
        Assert.Equal(0.95f, mat.LightingInfluence, 3);
    }

    [Fact]
    public void Parse_Fallout4Bgsm_ReadsEnvironmentMapAndScale()
    {
        // FO4 environment mapping (fo76utils loadBGSMFile): enable byte @57 gates the f32 scale @58;
        // the final EnvironmentMapScale = min(envScale × RAW specular strength, 8) — computed from
        // the strength AS READ, before the specular-enable normalization. The FO4 non-gradient path
        // map routes file entry 4 into slot 4 (env map); entry 2 is the _s specular map (slot 6).
        // The map reads NINE entries, so all nine pad the table (empties keep offsets aligned).
        var head = BuildBgsm(
            2, false, 63,
            "metal_d.dds", "metal_n.dds", "metal_s.dds", "",
            "shared/cubemaps/mipblur_defaultoutside1.dds", "", "", "", "");
        head[57] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(head.AsSpan(58), 1.5f);

        using var ms = new MemoryStream();
        ms.Write(head);
        ms.Write(new byte[15]); // FO4 enable-flag block between the path table and specular
        ms.WriteByte(0);        // specular ENABLE off — env mapping must still survive
        Span<byte> floats = stackalloc byte[20]; // specular RGB + strength + smoothness
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[..4], 0.8f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[4..8], 0.9f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[8..12], 1.0f);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[12..16], 2.0f); // raw strength
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[16..], 0.6f);   // smoothness
        ms.Write(floats);

        var mat = BgsmMaterial.Parse(ms.ToArray());

        Assert.NotNull(mat);
        Assert.Equal("shared/cubemaps/mipblur_defaultoutside1.dds", mat!.EnvironmentMap);
        Assert.Equal("metal_s.dds", mat.GetTexturePath(6));
        Assert.Equal(3.0f, mat.EnvironmentMapScale, 3); // min(1.5 × 2.0, 8)
        Assert.False(mat.SpecularEnabled);
        Assert.Equal(0f, mat.SpecularStrength); // enable off zeroes strength AFTER the env product
        Assert.Equal(0.6f, mat.SpecularSmoothness, 3);
    }

    [Fact]
    public void Parse_Fallout4Bgsm_EnvironmentMapDisabledByte_ScaleStaysZero()
    {
        // Enable byte @57 clear ⇒ the scale float is ignored and the term stays off even when a
        // slot-4 path is present (matching fo76utils, which only reads @58 when @57 is set).
        var head = BuildBgsm(
            2, false, 63,
            "metal_d.dds", "metal_n.dds", "metal_s.dds", "",
            "shared/cubemaps/mipblur_defaultoutside1.dds", "", "", "", "");
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(head.AsSpan(58), 1.5f);

        using var ms = new MemoryStream();
        ms.Write(head);
        ms.Write(new byte[15]);
        ms.WriteByte(1); // specular enabled
        Span<byte> floats = stackalloc byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(floats[12..16], 2.0f);
        ms.Write(floats);

        var mat = BgsmMaterial.Parse(ms.ToArray());

        Assert.NotNull(mat);
        Assert.Equal(0f, mat!.EnvironmentMapScale);
        Assert.Equal("shared/cubemaps/mipblur_defaultoutside1.dds", mat.EnvironmentMap);
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