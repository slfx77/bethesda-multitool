using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pins the three era layouts of NiTexturingProperty after NiObjectNET (nif.xml 12469-12481):
///     ≤ 10.0.1.2 carries BOTH a leading Flags ushort AND the Apply Mode uint (6 bytes) —
///     Oblivion's nine default GroundCover* terrain grasses are authored at exactly 10.0.1.2, and
///     the old 4-byte skip read Has Base Texture from the middle of Texture Count (always 0x00),
///     so the entire default-grass set rendered as untextured white cards; 10.0.1.3–20.1.0.1 is
///     Apply Mode only (4 bytes); ≥ 20.1.0.2 is a TexturingFlags ushort (2 bytes). Byte layouts
///     mirror the hex-verified retail groundcovermediumgrass01.nif stream.
/// </summary>
public sealed class NifTexturingPropertyReaderVersionTests
{
    private const uint Nif10012 = 0x0A000102; // 10.0.1.2 — the GroundCover* grass version
    private const uint Nif200004 = 0x14000004; // 20.0.0.4 — most Oblivion meshes
    private const uint Modern = 0x14020007; // 20.2.0.7 — FO3/FNV (string-table names)

    private const string TexturePath = @"textures\plants\groundcovermediumgrass01su.dds";

    [Theory]
    [InlineData(Nif10012, true)] // Flags + ApplyMode (6 bytes) — the fixed arm
    [InlineData(Nif200004, false)] // ApplyMode only (4 bytes) — must stay byte-identical
    public void ResolveBaseTexturePath_OblivionEraLayouts_ResolveTheSourceTexture(
        uint version, bool leadingFlags)
    {
        var (data, nif) = BuildNif(version, leadingFlags, 2);

        var path = NifTexturingPropertyReader.ResolveBaseTexturePath(data, nif, [0]);

        Assert.Equal(TexturePath, path);
    }

    [Fact]
    public void ReadApplyMode_LeadingFlagsLayout_ReadsTheRealApplyMode()
    {
        // Before the gate this read Flags | ApplyMode<<16 garbage — a silent parallax-marker miss.
        var (data, nif) = BuildNif(Nif10012, true, 3); // APPLY_HILIGHT

        Assert.Equal(3u, NifTexturingPropertyReader.ReadApplyMode(data, nif, [0]));
    }

    [Fact]
    public void ReadApplyMode_ModernStringTableNif_ReturnsNull()
    {
        var (data, nif) = BuildNif(Modern, false, 2);

        Assert.Null(NifTexturingPropertyReader.ReadApplyMode(data, nif, [0]));
    }

    // Block 0 = NiTexturingProperty, block 1 = NiSourceTexture. Inline-string form (Oblivion):
    // NiObjectNET = name len 0 (4) + num extra 0 (4) + controller -1 (4) = 12 bytes.
    private static (byte[] Data, NifInfo Nif) BuildNif(uint version, bool leadingFlags, uint applyMode)
    {
        var inlineStrings = version < 0x14010001;
        var texturing = new List<byte>();
        AppendObjectNet(texturing, inlineStrings);
        if (leadingFlags)
        {
            texturing.Add(0);
            texturing.Add(0); // leading Flags ushort (≤ 10.0.1.2 only)
        }

        if (inlineStrings)
        {
            AppendUInt(texturing, applyMode); // Apply Mode (3.3.0.13 – 20.1.0.1)
        }
        else
        {
            texturing.Add(0);
            texturing.Add(0); // TexturingFlags ushort (≥ 20.1.0.2)
        }

        AppendUInt(texturing, 7); // Texture Count
        texturing.Add(1); // Has Base Texture
        AppendInt(texturing, 1); // base TexDesc source ref → block 1

        var source = new List<byte>();
        AppendObjectNet(source, inlineStrings);
        source.Add(1); // Use External
        if (inlineStrings)
        {
            AppendUInt(source, (uint)TexturePath.Length);
            source.AddRange(Encoding.ASCII.GetBytes(TexturePath));
        }
        else
        {
            AppendInt(source, 0); // string-table index
        }

        var data = new byte[texturing.Count + source.Count];
        texturing.CopyTo(data, 0);
        source.CopyTo(data, texturing.Count);

        var nif = new NifInfo
        {
            BinaryVersion = version,
            HasInlineStrings = inlineStrings,
            BlockCount = 2
        };
        nif.Blocks.Add(new BlockInfo
        {
            Index = 0, TypeName = "NiTexturingProperty", DataOffset = 0, Size = texturing.Count
        });
        nif.Blocks.Add(new BlockInfo
        {
            Index = 1, TypeName = "NiSourceTexture", DataOffset = texturing.Count, Size = source.Count
        });
        if (!inlineStrings)
        {
            nif.Strings.Add(TexturePath);
        }

        return (data, nif);
    }

    private static void AppendObjectNet(List<byte> bytes, bool inlineStrings)
    {
        if (inlineStrings)
        {
            AppendUInt(bytes, 0); // name length 0 (inline SizedString)
        }
        else
        {
            AppendInt(bytes, -1); // name string index (absent)
        }

        AppendUInt(bytes, 0); // num extra data
        AppendInt(bytes, -1); // controller
    }

    private static void AppendUInt(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void AppendInt(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }
}