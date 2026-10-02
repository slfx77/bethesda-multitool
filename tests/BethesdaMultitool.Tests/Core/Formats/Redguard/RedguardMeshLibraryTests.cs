using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Tests.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic pins for <see cref="RedguardMeshLibrary" />'s placeholder rule, measured 2026-09-28 by a read-only
///     Python census over all 41 retail ROBs (<c>tools/scripts/redguard/rob_placeholder_census.py</c>): an EMPTY
///     segment (size 0) stands for the loose <c>3dart\NAME.3DC</c> of its name, a non-empty segment keeps its inline
///     mesh, and a name the ROB does not hold stays unresolved. Every fixture is written here from the documented
///     layouts (the ROB container, the <c>.3DC</c> frame table, the <c>.3D</c> record through
///     <see cref="XnGineMeshFixture" />) and laid out like an install: a data root holding <c>3dart</c>, with the ROB
///     and the loose files side by side in it.
///     <para>
///         The controls are what make the rule testable rather than merely exercised: a placeholder with no loose
///         file, or with only a loose <c>.3D</c>, stays unresolved; a non-empty segment beats a loose file of its
///         name; a placeholder whose type word is not 512, or is 0, still resolves (the rule keys on size); and a
///         loose file that exists and will not parse, or declares more points than it can hold, is a FAILURE, never
///         a silent miss and never an exception that stops the level.
///     </para>
/// </summary>
public sealed class RedguardMeshLibraryTests : IDisposable
{
    /// <summary>The type word every retail placeholder but one carries: header byte +13 = 2.</summary>
    private const uint PlaceholderType = 0x0000_0200;

    /// <summary>The one retail exception, ISLAND.ROB <c>BEAMA001</c>: byte +13 = 2 and byte +15 = 0x5A.</summary>
    private const uint BeamPlaceholderType = 0x5A00_0200;

    private const string RobName = "TESTMAP.ROB";

    /// <summary>An empty placeholder's payload: nothing, so its size word reads 0.</summary>
    private static readonly byte[] NoPayload = [];

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "bmt-redguard-meshes-" + Guid.NewGuid().ToString("N"));

    public RedguardMeshLibraryTests()
    {
        Directory.CreateDirectory(ArtDirectory);
    }

    private string ArtDirectory => Path.Combine(_dataRoot, RedguardMeshLibrary.ArtDirectoryName);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AnEmptyPlaceholderDrawsTheLoose3dcOfItsName_InItsKeyframePose()
    {
        // A lower-case loose name: the rule matches without regard to case (retail mixes both).
        WriteLoose("wagon.3dc", Animated(44, 2));
        using var library = Open(("WAGON", PlaceholderType, NoPayload));

        var mesh = library.Resolve("WAGON");

        Assert.NotNull(mesh);
        var sub = Assert.Single(mesh.SubMeshes);
        Assert.Equal((44, 2), (sub.TextureArchive, sub.TextureRecord));

        // The keyframe (frame 0) is (0,0,0), (512,0,0), (0,512,0) native; frame 1 adds 256 to every X. A reader
        // that drew frame 1, or read the .3DC as a plain .3D through the header's frame-1 offsets, lands elsewhere.
        Vector3[] keyframe = [new(0, 0, 0), new(0, 2, 0), new(2, 0, 0)];
        Assert.Equal(keyframe, Positions(mesh));
        Assert.DoesNotContain(new Vector3(3, 0, 0), Positions(mesh));

        Assert.Equal(new RedguardMeshOrigin(RedguardMeshSource.LooseAnimatedKeyframe, "wagon.3dc"),
            library.Origins["WAGON"]);
        Assert.Empty(library.Failures);

        // The archive itself still holds nothing by that name; only Resolve follows the placeholder.
        Assert.False(library.Contains("WAGON"));
        Assert.NotNull(library.Resolve("WAGON.3D"));
    }

    [Theory]
    [InlineData(PlaceholderType)]
    [InlineData(BeamPlaceholderType)]
    [InlineData(0u)]
    public void Resolve_KeysThePlaceholderOnItsSize_NotOnItsTypeWord(uint type)
    {
        // BEAMA001 carries 0x5A000200: a "type == 512" test would leave it unresolved. Type 0 is an EMPTY segment
        // whose header byte +13 is 0, which no retail placeholder carries: a rule keyed on that byte, or on the
        // 0x200 bit, would leave it unresolved. Retail cannot separate those rules from the size rule (they
        // coincide on 1,203 of 1,203), so this case is what pins that the rule keys on the size.
        WriteLoose("BEAMA001.3DC", Animated(44, 2));
        using var library = Open(("BEAMA001", type, NoPayload));

        Assert.NotNull(library.Resolve("BEAMA001"));
        Assert.Equal(RedguardMeshSource.LooseAnimatedKeyframe, library.Origins["BEAMA001"].Source);
    }

    [Fact]
    public void Resolve_APlaceholderWithNoLooseFileIsAMiss_NotAFailure()
    {
        using var library = Open(("GHOST", PlaceholderType, NoPayload));

        Assert.Null(library.Resolve("GHOST"));
        Assert.Empty(library.Failures);
        Assert.Empty(library.Origins);
    }

    [Fact]
    public void Resolve_APlaceholderDoesNotFallBackToALoose3dOfItsName()
    {
        // The rule names the .3DC only: no retail placeholder has a loose .3D, so nothing measured supports one.
        WriteLoose("GHOST.3D", Inline(24, 3));
        using var library = Open(("GHOST", PlaceholderType, NoPayload));

        Assert.Null(library.Resolve("GHOST"));
        Assert.Empty(library.Failures);
        Assert.Empty(library.Origins);
    }

    [Fact]
    public void Resolve_ANonEmptySegmentWinsOverALooseFileOfTheSameName()
    {
        // CV_FISH is the retail case: inline in CAVERNS.ROB and MKTEST.ROB, and also a loose .3DC.
        WriteLoose("CV_FISH.3DC", Animated(44, 2));
        using var library = Open(("CV_FISH", 0, Inline(24, 3)));

        var mesh = library.Resolve("CV_FISH");

        Assert.NotNull(mesh);
        var sub = Assert.Single(mesh.SubMeshes);
        Assert.Equal((24, 3), (sub.TextureArchive, sub.TextureRecord));
        Assert.Equal(new RedguardMeshOrigin(RedguardMeshSource.RobSegment, RobName), library.Origins["CV_FISH"]);
        Assert.True(library.Contains("CV_FISH"));
    }

    [Fact]
    public void Resolve_ALooseFileThatExistsAndWillNotParseIsAFailure_ReportedOnce()
    {
        WriteLoose("BROKEN.3DC", "v2.6 but nothing else"u8.ToArray());
        using var library = Open(("BROKEN", PlaceholderType, NoPayload));

        Assert.Null(library.Resolve("BROKEN"));
        Assert.Null(library.Resolve("BROKEN"));

        var (name, reason) = Assert.Single(library.Failures);
        Assert.Equal("BROKEN", name);
        Assert.Contains("placeholder", reason, StringComparison.Ordinal);
        Assert.Contains("BROKEN.3DC", reason, StringComparison.Ordinal);
        Assert.Empty(library.Origins);
    }

    [Fact]
    public void Resolve_ALooseFileThatTilesButCarriesAnUnknownTagIsAFailure_NotAFailedLevel()
    {
        // The .3DC tiling accepts it; only the keyframe parse sees the tag. That must surface as this name's
        // failure, not as an exception out of Resolve that would stop the whole map from loading.
        var bytes = Animated(44, 2);
        "v9.9"u8.CopyTo(bytes);
        WriteLoose("ODDTAG.3DC", bytes);
        using var library = Open(("ODDTAG", PlaceholderType, NoPayload));

        Assert.Null(library.Resolve("ODDTAG"));

        var (name, reason) = Assert.Single(library.Failures);
        Assert.Equal("ODDTAG", name);
        Assert.Contains("ODDTAG.3DC", reason, StringComparison.Ordinal);
        Assert.Empty(library.Origins);
    }

    [Fact]
    public void Resolve_ALooseFileDeclaringMorePointsThanItCanHoldIsAFailure_NotAnException()
    {
        // This file TILES under the reader's int block arithmetic (OversizePointCount shows the sum), so without the
        // point-count guard the keyframe read asks for int.MaxValue points, the OutOfMemoryException escapes
        // Resolve, and the whole level fails to load. No array can hold int.MaxValue elements, so that failing run
        // throws at once rather than allocating.
        WriteLoose("HUGE.3DC", OversizePointCount());
        using var library = Open(("HUGE", PlaceholderType, NoPayload));

        Assert.Null(library.Resolve("HUGE"));

        var (name, reason) = Assert.Single(library.Failures);
        Assert.Equal("HUGE", name);
        Assert.Contains("HUGE.3DC", reason, StringComparison.Ordinal);
        Assert.Contains(int.MaxValue.ToString(CultureInfo.InvariantCulture) + " declared points", reason,
            StringComparison.Ordinal);
        Assert.Empty(library.Origins);
    }

    [Fact]
    public void DescribeLooseKeyframes_NamesTheLooseFileAndItsApproximations_AndNothingForInlineMeshes()
    {
        WriteLoose("wagon.3dc", Animated(44, 2));
        using var library = Open(("CRATE", 0, Inline(24, 3)), ("WAGON", PlaceholderType, NoPayload));

        // Only an inline mesh so far: nothing was approximated, so there is nothing to report.
        Assert.NotNull(library.Resolve("CRATE"));
        Assert.Null(RedguardMeshLibrary.DescribeLooseKeyframes(library.Origins));

        Assert.NotNull(library.Resolve("WAGON"));
        var note = RedguardMeshLibrary.DescribeLooseKeyframes(library.Origins);
        Assert.NotNull(note);
        Assert.StartsWith("1 empty ROB placeholder(s)", note, StringComparison.Ordinal);
        Assert.Contains("keyframe pose (frame 0, where the placeholder bounds name frame 1)", note,
            StringComparison.Ordinal);
        Assert.Contains("reference UVs", note, StringComparison.Ordinal);
        Assert.EndsWith(": wagon.3dc", note, StringComparison.Ordinal);
        Assert.DoesNotContain("CRATE", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeLooseKeyframes_ListsTwelveFilesInNameOrder_ThenCountsTheRest()
    {
        var segments = new (string Name, uint Type, byte[] Payload)[13];
        for (var i = 0; i < segments.Length; i++)
        {
            // Written in reverse, so the listing's order comes from the sort, not from resolution order.
            var stem = $"P{segments.Length - 1 - i:D2}";
            segments[i] = (stem, PlaceholderType, NoPayload);
            WriteLoose(stem + ".3DC", Animated(44, 2));
        }

        using var library = Open(segments);
        foreach (var (stem, _, _) in segments)
        {
            Assert.NotNull(library.Resolve(stem));
        }

        var note = RedguardMeshLibrary.DescribeLooseKeyframes(library.Origins);
        Assert.NotNull(note);
        Assert.StartsWith("13 empty ROB placeholder(s)", note, StringComparison.Ordinal);
        var listed = string.Join(", ", Enumerable.Range(0, 12).Select(i => $"P{i:D2}.3DC"));
        Assert.EndsWith(": " + listed + " and 1 more", note, StringComparison.Ordinal);
        Assert.DoesNotContain("P12.3DC", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ANameTheArchiveDoesNotHoldStaysUnresolved_EvenWithALooseFileOfThatName()
    {
        WriteLoose("STRAY.3DC", Animated(44, 2));
        using var library = Open(("OTHER", 0, Inline(24, 3)));

        Assert.Null(library.Resolve("STRAY"));
        Assert.Empty(library.Failures);
        Assert.Empty(library.Origins);
    }

    private RedguardMeshLibrary Open(params (string Name, uint Type, byte[] Payload)[] segments)
    {
        var path = Path.Combine(ArtDirectory, RobName);
        File.WriteAllBytes(path, Rob(segments));
        return RedguardMeshLibrary.Open(path, _dataRoot);
    }

    private void WriteLoose(string fileName, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(ArtDirectory, fileName), bytes);
    }

    /// <summary>Distinct vertex positions of a resolved mesh, in world units, ordered for comparison.</summary>
    private static List<Vector3> Positions(XnGineTriangleMesh mesh)
    {
        return
        [
            .. mesh.SubMeshes.SelectMany(s => s.Vertices).Select(v => v.Position).Distinct()
                .OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z)
        ];
    }

    /// <summary>
    ///     A ROB: <c>"OARC"</c> with a big-endian length of 4 and a little-endian segment count, <c>"OARD"</c> with a
    ///     big-endian length over the segments, then 80-byte segment headers (forward pointer, 8-byte name, type word at
    ///     +12, size at +76) each followed by its payload, and an <c>"END "</c> terminator.
    /// </summary>
    private static byte[] Rob((string Name, uint Type, byte[] Payload)[] segments)
    {
        var body = new List<byte>();
        foreach (var (name, type, payload) in segments)
        {
            var header = new byte[80];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(80 + payload.Length));
            Encoding.ASCII.GetBytes(name).CopyTo(header, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), type);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), (uint)payload.Length);
            body.AddRange(header);
            body.AddRange(payload);
        }

        var prologue = new byte[20];
        "OARC"u8.CopyTo(prologue);
        BinaryPrimitives.WriteUInt32BigEndian(prologue.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(prologue.AsSpan(8), (uint)segments.Length);
        "OARD"u8.CopyTo(prologue.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(prologue.AsSpan(16), (uint)body.Count);
        return [.. prologue, .. body, .. "END "u8.ToArray()];
    }

    /// <summary>An inline <c>.3D</c>: one triangle textured with the given material.</summary>
    private static byte[] Inline(int archive, int record)
    {
        return XnGineMeshFixture.Build("v2.7",
            [(0, 0, 0), (256, 0, 0), (0, 256, 0)],
            [
                new XnGineMeshFixture.Plane(XnGineMeshFixture.Texture(archive, record),
                    [(0, 0, 0), (1, 0, 0), (2, 0, 0)], (0, 0, 256))
            ]);
    }

    /// <summary>
    ///     A two-frame "narrow" <c>.3DC</c> that tiles exactly: the 64-byte header (whose +24/+48/+52 carry FRAME 1's
    ///     offsets, as retail does), the six-dword frame block, a three-dword frame table, one textured triangle, then
    ///     each frame's point, normal and plane-data blocks. Frame 0 is the int32 keyframe (0,0,0), (512,0,0),
    ///     (0,512,0); frame 1 is int16 deltas of +256 on every X.
    /// </summary>
    private static byte[] Animated(int archive, int record)
    {
        const int pointCount = 3;
        const int planeCount = 1;
        const int frameCount = 2;
        const int frameBlockOffset = 64;
        const int tableOffset = frameBlockOffset + 6 * 4;
        const int recordDwords = 3;
        const int planeListOffset = tableOffset + frameCount * recordDwords * 4;
        const int planeListEnd = planeListOffset + 8 + pointCount * 8;
        const int normalLength = planeCount * 4;
        const int planeDataLength = planeCount * 12;

        const int f0Points = planeListEnd;
        const int f0Normals = f0Points + pointCount * 12;
        const int f0PlaneData = f0Normals + normalLength;
        const int f1Points = f0PlaneData + planeDataLength;
        const int f1Normals = f1Points + pointCount * 6;
        const int f1PlaneData = f1Normals + normalLength;
        const int size = f1PlaneData + planeDataLength;

        var b = new byte[size];
        Encoding.ASCII.GetBytes("v2.6").CopyTo(b, 0);
        Write(b, 4, pointCount);
        Write(b, 8, planeCount);
        Write(b, 12, 512);
        Write(b, 16, frameCount);
        Write(b, 20, frameBlockOffset);
        Write(b, 24, f1PlaneData);
        Write(b, 44, 1);
        Write(b, 48, f1Points);
        Write(b, 52, f1Normals);
        Write(b, 60, planeListOffset);

        Write(b, frameBlockOffset, tableOffset);
        Write(b, tableOffset, f0Points);
        Write(b, tableOffset + 4, f0Normals);
        Write(b, tableOffset + 8, f0PlaneData);
        Write(b, tableOffset + recordDwords * 4, f1Points);
        Write(b, tableOffset + recordDwords * 4 + 4, f1Normals);
        Write(b, tableOffset + recordDwords * 4 + 8, f1PlaneData);

        b[planeListOffset] = pointCount;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(planeListOffset + 2),
            XnGineMeshFixture.Texture(archive, record));
        for (var q = 0; q < pointCount; q++)
        {
            Write(b, planeListOffset + 8 + q * 8, q * 12);
        }

        int[] keyframe = [0, 0, 0, 512, 0, 0, 0, 512, 0];
        for (var i = 0; i < keyframe.Length; i++)
        {
            Write(b, f0Points + i * 4, keyframe[i]);
        }

        for (var q = 0; q < pointCount; q++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(f1Points + q * 6), 256);
        }

        return b;
    }

    /// <summary>
    ///     A single-frame narrow <c>.3DC</c> of 160 bytes declaring <see cref="int.MaxValue" /> points, laid out so the
    ///     reader's <see cref="int" /> block arithmetic still tiles it: the 64-byte header, the six-dword frame block,
    ///     one three-dword frame record, one triangle plane, a 4-byte normal block and a 12-byte plane-data block,
    ///     then 12 bytes where the keyframe starts. The keyframe block's end, <c>148 + int.MaxValue * 12</c>, wraps to
    ///     136, below its own start, so the block covers nothing, and those last 12 bytes are the one unaccounted
    ///     region the frame block declares.
    /// </summary>
    private static byte[] OversizePointCount()
    {
        const int frameBlockOffset = 64;
        const int tableOffset = frameBlockOffset + 6 * 4;
        const int planeListOffset = tableOffset + 3 * 4;
        const int planeListEnd = planeListOffset + 8 + 3 * 8;
        const int normals = planeListEnd;
        const int planeData = normals + 4;
        const int points = planeData + 12;
        const int size = points + 12;

        var b = new byte[size];
        Encoding.ASCII.GetBytes("v2.6").CopyTo(b, 0);
        Write(b, 4, int.MaxValue);
        Write(b, 8, 1);
        Write(b, 16, 1);
        Write(b, 20, frameBlockOffset);
        Write(b, 60, planeListOffset);

        Write(b, frameBlockOffset, tableOffset);
        Write(b, frameBlockOffset + 8, size - points);
        Write(b, tableOffset, points);
        Write(b, tableOffset + 4, normals);
        Write(b, tableOffset + 8, planeData);

        b[planeListOffset] = 3;
        for (var q = 0; q < 3; q++)
        {
            Write(b, planeListOffset + 8 + q * 8, q * 12);
        }

        return b;
    }

    private static void Write(byte[] bytes, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}
