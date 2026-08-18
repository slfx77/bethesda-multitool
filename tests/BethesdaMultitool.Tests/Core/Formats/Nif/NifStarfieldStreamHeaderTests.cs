using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Pins the <c>BSStreamHeader</c> field set per bsVersion, which is what decides whether the
///     block-type table is found at the right offset.
///     <para>
///         Regression guard for "every Starfield NIF returns null": Starfield (bsVersion 170+) drops
///         <c>Max Filepath</c> and appends an <c>ExportDataSF</c> field, and the parser skipped neither
///         correctly. Being short by <c>1 + Length</c> bytes made the next read (<c>Num Block Types</c>)
///         land inside the ExportDataSF payload, so <see cref="NifParser.Parse" /> bailed on the
///         <c>strLen &gt; 256</c> guard for 100% of the game's 38,610 authored meshes.
///     </para>
///     Field order and conditions are nif.xml's <c>BSStreamHeader</c>: Author, Unknown Int
///     (bsVersion &gt; 130), Process Script (&lt; 131), Export Script, Max Filepath (103..169),
///     Unknown Data / ExportDataSF (&gt;= 170).
/// </summary>
public class NifStarfieldStreamHeaderTests
{
    private const uint Gamebryo202007 = 0x14020007;
    private const uint StarfieldBsVersion = 173; // the value retail rockflatsmalla.nif declares
    private const uint Fallout76BsVersion = 155;
    private const uint Fallout4BsVersion = 130;

    private static readonly string[] StarfieldBlockTypes =
    [
        "NiNode", "BSXFlags", "BSGeometry", "NiIntegerExtraData", "BSLightingShaderProperty"
    ];

    [Fact]
    public void Starfield_BsVersion173_FindsEveryBlockType()
    {
        var nif = BuildNif(StarfieldBsVersion, StarfieldBlockTypes);

        var info = NifParser.Parse(nif);

        Assert.NotNull(info);
        Assert.Equal(StarfieldBsVersion, info!.BsVersion);
        Assert.Equal(StarfieldBlockTypes, info.BlockTypeNames);
        Assert.Equal(StarfieldBlockTypes.Length, info.BlockCount);
    }

    /// <summary>
    ///     The ExportDataSF payload is deliberately built to contain a plausible-looking little-endian
    ///     16-bit value, so a parser that fails to skip it reads a block-type count out of that payload
    ///     instead of the real one. Without the skip this returns null (or the wrong names).
    /// </summary>
    [Fact]
    public void Starfield_ExportDataSfPayload_IsNotMistakenForTheBlockTypeTable()
    {
        var nif = BuildNif(
            StarfieldBsVersion,
            StarfieldBlockTypes,
            exportDataSf: [0x21, 0xF0, 0xD8, 0x95, 0x22, 0x02, 0x00]); // retail bytes' shape

        var info = NifParser.Parse(nif);

        Assert.NotNull(info);
        Assert.Equal(StarfieldBlockTypes, info!.BlockTypeNames);
    }

    /// <summary>
    ///     Fallout 4 / 76 carry Max Filepath and NO ExportDataSF. Guards the other direction: adding the
    ///     170+ skip must not consume a byte on the versions that were already working.
    /// </summary>
    [Theory]
    [InlineData(Fallout4BsVersion)]
    [InlineData(Fallout76BsVersion)]
    public void PreStarfield_StillParses(uint bsVersion)
    {
        var nif = BuildNif(bsVersion, ["NiNode", "BSTriShape"]);

        var info = NifParser.Parse(nif);

        Assert.NotNull(info);
        Assert.Equal(bsVersion, info!.BsVersion);
        Assert.Equal(["NiNode", "BSTriShape"], info.BlockTypeNames);
    }

    /// <summary>
    ///     Builds a structurally valid modern-path NIF (20.2.0.7 / user version 12) whose BSStreamHeader
    ///     is written with exactly the fields <paramref name="bsVersion" /> calls for. Block bodies are
    ///     zero-filled — these tests assert header navigation, not geometry.
    /// </summary>
    private static byte[] BuildNif(
        uint bsVersion,
        string[] blockTypes,
        byte[]? exportDataSf = null)
    {
        var w = new List<byte>();
        w.AddRange(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        w.AddRange(BitConverter.GetBytes(Gamebryo202007));
        w.Add(1); // endian: little
        w.AddRange(BitConverter.GetBytes(12u)); // user version
        w.AddRange(BitConverter.GetBytes((uint)blockTypes.Length)); // block count
        w.AddRange(BitConverter.GetBytes(bsVersion));

        AddExportString(w, "test");                                    // Author
        if (bsVersion > 130) w.AddRange(BitConverter.GetBytes(3u));     // Unknown Int
        if (bsVersion < 131) AddExportString(w, "proc");                // Process Script
        AddExportString(w, "exp");                                      // Export Script
        if (bsVersion is >= 103 and < 170) AddExportString(w, "C:\\x");  // Max Filepath
        if (bsVersion >= 170)
        {
            // ExportDataSF: 1-byte length INCLUDING the terminator, then that many bytes.
            var payload = exportDataSf ?? [0x01, 0x02, 0x03, 0x00];
            w.Add((byte)payload.Length);
            w.AddRange(payload);
        }

        w.AddRange(BitConverter.GetBytes((ushort)blockTypes.Length));
        foreach (var t in blockTypes)
        {
            w.AddRange(BitConverter.GetBytes((uint)t.Length));
            w.AddRange(Encoding.ASCII.GetBytes(t));
        }

        for (var i = 0; i < blockTypes.Length; i++) w.AddRange(BitConverter.GetBytes((ushort)i));
        for (var i = 0; i < blockTypes.Length; i++) w.AddRange(BitConverter.GetBytes(16u)); // block sizes

        w.AddRange(BitConverter.GetBytes(0u)); // string table: num strings
        w.AddRange(BitConverter.GetBytes(0u)); // string table: max string length
        w.AddRange(BitConverter.GetBytes(0u)); // groups

        w.AddRange(new byte[16 * blockTypes.Length]); // zero-filled block bodies
        return [.. w];
    }

    /// <summary>An ExportString: one length byte counting the null terminator, then the bytes.</summary>
    private static void AddExportString(List<byte> w, string value)
    {
        w.Add((byte)(value.Length + 1));
        w.AddRange(Encoding.ASCII.GetBytes(value));
        w.Add(0);
    }
}
