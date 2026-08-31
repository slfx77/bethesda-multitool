using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Materials;

public sealed class BgsmMaterialGlowTests
{
    [Fact]
    public void Parse_Fallout76ExplicitGlow_RequiresEofFlagAndReadsRgbaPayload()
    {
        var material = Assert.IsType<BgsmMaterial>(ParseFixture(
            version: 22,
            emitEnabled: true,
            glowFlag: true,
            glowPath: "textures/test/glow.dds",
            explicitColor: new Vector3(0.25f, 0.5f, 0.75f),
            scale: 2.5f));

        Assert.True(material.EmissiveEnabled);
        Assert.True(material.GlowEnabled);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), material.EmissiveColor);
        Assert.Equal(2.5f, material.EmissiveScale);
    }

    [Fact]
    public void Parse_Fallout76GlowMapWithoutExplicitEmit_UsesWhiteScalarFallback()
    {
        var material = Assert.IsType<BgsmMaterial>(ParseFixture(
            version: 22,
            emitEnabled: false,
            glowFlag: true,
            glowPath: "textures/test/glow.dds",
            explicitColor: Vector3.Zero,
            scale: 3f));

        Assert.Equal("textures/test/glow.dds", material.GetTexturePath(BgsmMaterial.SlotGlow));
        Assert.True(material.EmissiveEnabled);
        Assert.Equal(Vector3.One, material.EmissiveColor);
        Assert.Equal(3f, material.EmissiveScale);
    }

    [Fact]
    public void Parse_Fallout76PayloadWithoutEofGlowFlag_IsDisabled()
    {
        var material = Assert.IsType<BgsmMaterial>(ParseFixture(
            version: 22,
            emitEnabled: true,
            glowFlag: false,
            glowPath: "textures/test/glow.dds",
            explicitColor: new Vector3(1f, 0.5f, 0.25f),
            scale: 2f));

        Assert.False(material.EmissiveEnabled);
        Assert.False(material.GlowEnabled);
        Assert.Equal(Vector3.Zero, material.EmissiveColor);
        Assert.Equal(0f, material.EmissiveScale);
    }

    [Fact]
    public void Parse_Fallout4GlowMapWithoutExplicitEmit_HasNoFallback()
    {
        var material = Assert.IsType<BgsmMaterial>(ParseFixture(
            version: 2,
            emitEnabled: false,
            glowFlag: true,
            glowPath: "textures/test/glow.dds",
            explicitColor: Vector3.Zero,
            scale: 3f));

        Assert.Equal("textures/test/glow.dds", material.GetTexturePath(BgsmMaterial.SlotGlow));
        Assert.False(material.EmissiveEnabled);
        Assert.Equal(Vector3.Zero, material.EmissiveColor);
        Assert.Equal(0f, material.EmissiveScale);
    }

    [Fact]
    public void Parse_ShortEmissivePayload_FailsClosed()
    {
        var data = BuildFixture(
            version: 22,
            emitEnabled: true,
            glowFlag: true,
            glowPath: "textures/test/glow.dds",
            explicitColor: Vector3.One,
            scale: 2f);
        var emitOffset = data.Length - 24 - 17; // final tail + complete emit block
        Array.Resize(ref data, emitOffset + 5); // emit byte + one float, short of RGB + scale
        data[^24] = 1; // keep the EOF-relative gate set; only the payload guard rejects this input

        var material = Assert.IsType<BgsmMaterial>(BgsmMaterial.Parse(data));

        Assert.False(material.EmissiveEnabled);
        Assert.Equal(Vector3.Zero, material.EmissiveColor);
        Assert.Equal(0f, material.EmissiveScale);
    }

    private static BgsmMaterial? ParseFixture(
        byte version,
        bool emitEnabled,
        bool glowFlag,
        string glowPath,
        Vector3 explicitColor,
        float scale) => BgsmMaterial.Parse(BuildFixture(
            version, emitEnabled, glowFlag, glowPath, explicitColor, scale));

    private static byte[] BuildFixture(
        byte version,
        bool emitEnabled,
        bool glowFlag,
        string glowPath,
        Vector3 explicitColor,
        float scale)
    {
        var isFallout4 = version == 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("BGSM"));
        writer.Write((uint)version);
        writer.Write(new byte[(isFallout4 ? 63 : 60) - 8]);

        // Non-gradient BGSM slot maps: FO4 routes the sixth path to slot 2; FO76 routes the fifth.
        var pathCount = isFallout4 ? 9 : 10;
        var glowPathIndex = isFallout4 ? 5 : 4;
        for (var index = 0; index < pathCount; index++)
        {
            WritePath(writer, index == glowPathIndex ? glowPath : string.Empty);
        }

        writer.Write(new byte[isFallout4 ? 15 : 24]);
        writer.Write((byte)0); // specular disabled
        writer.Write(new byte[20]); // specular RGB + strength + smoothness
        writer.Write(new byte[isFallout4 ? 28 : 30]);
        writer.Write(0u); // empty root material
        writer.Write((byte)0); // anisotropic lighting
        writer.Write(emitEnabled ? (byte)1 : (byte)0);
        WriteSingle(writer, explicitColor.X);
        WriteSingle(writer, explicitColor.Y);
        WriteSingle(writer, explicitColor.Z);
        WriteSingle(writer, scale);
        writer.Write(new byte[isFallout4 ? 45 : 24]);
        writer.Flush();

        var data = stream.ToArray();
        data[data.Length - (isFallout4 ? 45 : 24)] = glowFlag ? (byte)1 : (byte)0;
        return data;
    }

    private static void WritePath(BinaryWriter writer, string path)
    {
        var bytes = Encoding.ASCII.GetBytes(path);
        writer.Write((uint)(bytes.Length + 1));
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static void WriteSingle(BinaryWriter writer, float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        writer.Write(bytes);
    }
}
