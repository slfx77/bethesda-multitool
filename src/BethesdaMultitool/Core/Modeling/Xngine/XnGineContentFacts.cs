using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     What an XnGine <c>.3D</c> record's own bytes say about its game (cut-1c plan section 6.2, step 4): the version
///     tag, the header counts, header +16 and +20 (the fields the <c>.3DC</c> shape test and the Daggerfall
///     discriminator read), and whether the plane list walks under the 8-byte (Daggerfall, Redguard) and the 10-byte
///     (Battlespire) plane-header layouts. Measured on the corpus: 0 of 4,755 Battlespire meshes walk with 8-byte
///     headers, 0 of 4,719 Redguard static meshes walk with 10-byte headers, 20 of 10,251 ARCH3D records walk both
///     ways, and header +20 is 0 on all 10,251 ARCH3D records but non-zero on 52 of 52 loose Redguard files, 4,352 of
///     4,667 ROB meshes and 147 of 147 <c>.3DC</c> files (64, the frame block offset).
/// </summary>
/// <remarks>
///     The walk is written from the plan's stated acceptance rule and shares no code with
///     <see cref="XnGineMesh.Parse" />; it agrees with the parser on what "walks" means (the same list, header, corner
///     and point-address checks, and the v2.5 point-offset tripling) without parsing a plane. It touches the header and
///     the plane list only, so a 7 MB <c>.3DC</c> costs no more than its plane count.
/// </remarks>
/// <param name="Tag">The record's 4-byte tag up to its first NUL (<c>v2.7</c>, <c>v5.0</c>, <c>MZ</c>), or empty when the record is shorter than 4 bytes.</param>
/// <param name="PointCount">Header +4.</param>
/// <param name="PlaneCount">Header +8.</param>
/// <param name="HeaderPlus16">Header +16: a <c>.3DC</c>'s frame count; 0 on every static mesh but MENU.ROB's four +16 = 9 segments and the ARCH3.EXE stray.</param>
/// <param name="HeaderPlus20">Header +20: a <c>.3DC</c>'s frame block offset (64); the one-way Daggerfall discriminator on static meshes (0 on every ARCH3D record).</param>
/// <param name="HeaderPlus44">Header +44: non-zero only on the 147 <c>.3DC</c> files.</param>
/// <param name="DaggerfallWalk">The plane walk under 8-byte plane headers.</param>
/// <param name="BattlespireWalk">The plane walk under 10-byte plane headers.</param>
internal sealed record XnGineContentFacts(
    string Tag,
    int PointCount,
    int PlaneCount,
    int HeaderPlus16,
    int HeaderPlus20,
    int HeaderPlus44,
    XnGineLayoutWalk DaggerfallWalk,
    XnGineLayoutWalk BattlespireWalk)
{
    /// <summary>Bytes in the fixed header.</summary>
    public const int HeaderLength = 64;

    /// <summary>Bytes per point and per normal.</summary>
    public const int PointLength = 12;

    /// <summary>Bytes per plane corner: i32 point byte offset, i16 u, i16 v.</summary>
    public const int PlanePointLength = 8;

    /// <summary>The Daggerfall and Redguard plane header size.</summary>
    public const int DaggerfallPlaneHeaderLength = 8;

    /// <summary>The Battlespire plane header size.</summary>
    public const int BattlespirePlaneHeaderLength = 10;

    /// <summary>The tags of the mesh family the cut-1c readers decode.</summary>
    public static IReadOnlyList<string> MeshTags { get; } = ["v2.5", "v2.6", "v2.7"];

    /// <summary>True when the tag is one of <see cref="MeshTags" />.</summary>
    public bool IsMeshTag => MeshTags.Contains(Tag, StringComparer.Ordinal);

    /// <summary>True when the plane list walks under 8-byte plane headers.</summary>
    public bool WalksWithDaggerfallLayout => DaggerfallWalk == XnGineLayoutWalk.Walks;

    /// <summary>True when the plane list walks under 10-byte plane headers.</summary>
    public bool WalksWithBattlespireLayout => BattlespireWalk == XnGineLayoutWalk.Walks;

    /// <summary>True when both walks succeed (20 of 10,251 ARCH3D records).</summary>
    public bool WalksWithBothLayouts => WalksWithDaggerfallLayout && WalksWithBattlespireLayout;

    /// <summary>True when neither walk succeeds (a stray, a 3dfx mesh, or a truncated prefix).</summary>
    public bool WalksWithNeitherLayout => !WalksWithDaggerfallLayout && !WalksWithBattlespireLayout;

    /// <summary>True when at least one walk ran past the end of an incomplete prefix, so the content cannot decide.</summary>
    public bool IsInconclusive =>
        DaggerfallWalk == XnGineLayoutWalk.Incomplete || BattlespireWalk == XnGineLayoutWalk.Incomplete;

    /// <summary>Whether the plane list walks under the given layout.</summary>
    public bool WalksWith(XnGineMeshLayout layout)
    {
        return layout == XnGineMeshLayout.Battlespire ? WalksWithBattlespireLayout : WalksWithDaggerfallLayout;
    }

    /// <summary>The plane header size of a layout, for evidence text.</summary>
    public static int PlaneHeaderLengthOf(XnGineMeshLayout layout)
    {
        return layout == XnGineMeshLayout.Battlespire ? BattlespirePlaneHeaderLength : DaggerfallPlaneHeaderLength;
    }

    /// <summary>
    ///     Measures a record (or, for a probe, its bounded prefix). With <paramref name="isComplete" /> false a walk that
    ///     runs past the end reports <see cref="XnGineLayoutWalk.Incomplete" /> instead of failing; a record shorter than
    ///     the 64-byte header, or whose tag is not a mesh tag, fails both walks (or is Incomplete when the prefix is not
    ///     complete and shorter than the header).
    /// </summary>
    public static XnGineContentFacts Measure(ReadOnlySpan<byte> bytes, bool isComplete = true)
    {
        var tag = ReadTag(bytes);
        if (bytes.Length < HeaderLength)
        {
            var walk = isComplete ? XnGineLayoutWalk.Fails : XnGineLayoutWalk.Incomplete;
            return new XnGineContentFacts(tag, 0, 0, 0, 0, 0, walk, walk);
        }

        var pointCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var planeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var plus16 = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        var plus20 = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        var plus44 = BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]);
        if (!MeshTags.Contains(tag, StringComparer.Ordinal))
        {
            return new XnGineContentFacts(tag, pointCount, planeCount, plus16, plus20, plus44, XnGineLayoutWalk.Fails,
                XnGineLayoutWalk.Fails);
        }

        return new XnGineContentFacts(tag, pointCount, planeCount, plus16, plus20, plus44,
            Walk(bytes, DaggerfallPlaneHeaderLength, isComplete), Walk(bytes, BattlespirePlaneHeaderLength, isComplete));
    }

    /// <summary>The census's plane walk under one plane-header size (see the type remarks).</summary>
    private static XnGineLayoutWalk Walk(ReadOnlySpan<byte> bytes, int planeHeaderLength, bool isComplete)
    {
        var length = bytes.Length;
        var pointCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var planeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var pointListOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]);
        var normalListOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[52..]);
        var planeListOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]);
        var pastEnd = isComplete ? XnGineLayoutWalk.Fails : XnGineLayoutWalk.Incomplete;
        var v25 = bytes[..4].SequenceEqual("v2.5"u8);

        if (pointCount < 0 || pointListOffset < 0 || planeCount < 0 || normalListOffset < 0 || planeListOffset < 0)
        {
            return XnGineLayoutWalk.Fails;
        }

        if (pointListOffset + (long)pointCount * PointLength > length ||
            normalListOffset + (long)planeCount * PointLength > length ||
            planeListOffset > length)
        {
            return pastEnd;
        }

        long position = planeListOffset;
        for (var k = 0; k < planeCount; k++)
        {
            if (position + planeHeaderLength > length)
            {
                return pastEnd;
            }

            int cornerCount = bytes[(int)position];
            position += planeHeaderLength;
            for (var q = 0; q < cornerCount; q++)
            {
                if (position + PlanePointLength > length)
                {
                    return pastEnd;
                }

                var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[(int)position..]);
                position += PlanePointLength;
                var byteOffset = v25 ? offset * 3L : offset;
                if (byteOffset < 0 || byteOffset % PointLength != 0 || byteOffset / PointLength >= pointCount)
                {
                    return XnGineLayoutWalk.Fails;
                }
            }
        }

        return XnGineLayoutWalk.Walks;
    }

    /// <summary>The first four bytes up to a NUL, as ASCII; empty when fewer than four bytes exist.</summary>
    private static string ReadTag(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return string.Empty;
        }

        var head = bytes[..4];
        var end = head.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? head : head[..end]);
    }
}
