using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.RenderWare;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

/// <summary>
///     Explicit source-graph acceptance, paired with portable malformed-input/ownership tests.
///     Counts and fixed resource hashes were measured independently with a bounded Python parser
///     on 2026-09-20. No proprietary payload is checked in, emitted, or needed by the portable tests.
///     Decoding a source graph does not establish rendering, texture, skin, or camera semantics.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RwClumpRetailTests(ITestOutputHelper output)
{
    private static readonly string[] ArrowTextures = ["Ebony_Arrow01", "Ebony_Arrow02"];
    private static readonly int[] ArrowSplitLengths = [77, 99];
    private static readonly int[] BowMaterialTriangleCounts = [52, 112, 60];
    private static readonly string[] BowTextures = ["daedricbow_01", "daedricbow_02", "daedricbow_03"];

    [Theory]
    [InlineData("2006-6-9", 5, 93, 11, 2, 0)]
    [InlineData("2006-11-21", 153, 1928, 153, 95, 1)]
    [InlineData("2007-1-11", 214, 3388, 222, 108, 0)]
    [InlineData("2007-1-31", 240, 3788, 248, 122, 0)]
    [InlineData("2007-2-1", 240, 3788, 248, 122, 0)]
    [InlineData("2007-4-27", 111, 1810, 111, 53, 0)]
    public void DatedBuildMatchesIndependentCompleteGraphCensus(
        string date, int expectedClumps, int expectedFrames, int expectedGeometries,
        int expectedWithoutSkin, int expectedDeclines)
    {
        var pack = RequirePack(date);
        var archive = OblivionPspArchive.Parse(pack);
        using var stream = File.OpenRead(pack);
        var clumps = 0;
        var frames = 0;
        var geometries = 0;
        var atomics = 0;
        var withoutSkin = 0;
        var inspectedClumps = 0;
        var inspectedFrames = 0;
        var inspectedGeometries = 0;
        var inspectedAtomics = 0;
        var declines = 0;
        foreach (var entry in archive.Entries)
        {
            var bytes = ReadEntry(stream, entry);
            var resources = OblivionPspResourceReader.ReadResources(bytes);
            for (var ordinal = 0; ordinal < resources.Count; ordinal++)
            {
                var resource = resources[ordinal];
                if (resource.TypeName != "rwID_CLUMP")
                {
                    continue;
                }

                var source = ClumpBytes(bytes, resource);
                var declared = DeclaredCounts(source);
                inspectedClumps++;
                inspectedFrames += declared.Frames;
                inspectedGeometries += declared.Geometries;
                inspectedAtomics += declared.Atomics;
                var decoded = RwClumpReader.TryRead(source, out var error);
                if (decoded is null)
                {
                    AssertPinnedInvalidNormals(date, entry.Name, ordinal, resource, source, error);
                    Assert.Equal((31, 1, 1), declared);
                    declines++;
                    continue;
                }

                Assert.Equal(declared, (decoded.Frames.Count, decoded.Geometries.Count, decoded.Atomics.Count));
                clumps++;
                frames += decoded.Frames.Count;
                geometries += decoded.Geometries.Count;
                atomics += decoded.Atomics.Count;
                if (!decoded.Geometries.Any(g => g.Extensions.Any(e => e.Type == RwChunk.SkinPlugin)))
                {
                    withoutSkin++;
                }

                AssertDecodedGeometry(decoded);
            }
        }

        Assert.Equal(expectedClumps, inspectedClumps);
        Assert.Equal(expectedFrames, inspectedFrames);
        Assert.Equal(expectedGeometries, inspectedGeometries);
        Assert.Equal(expectedGeometries, inspectedAtomics);
        Assert.Equal(expectedDeclines, declines);
        // The only permitted rejection has exactly 31 frames, one geometry and one atomic.
        Assert.Equal(expectedClumps - expectedDeclines, clumps);
        Assert.Equal(expectedFrames - 31 * expectedDeclines, frames);
        Assert.Equal(expectedGeometries - expectedDeclines, geometries);
        Assert.Equal(expectedGeometries - expectedDeclines, atomics);
        Assert.Equal(expectedWithoutSkin, withoutSkin);
        output.WriteLine($"Inspected: {inspectedClumps} CLUMPs, {inspectedFrames} frames, " +
            $"{inspectedGeometries} geometries, {inspectedAtomics} atomics. " +
            $"Decoded: {clumps} CLUMPs, {frames} frames, {geometries} geometries, {atomics} atomics. " +
            $"Pinned invalid-source declines: {declines}; decoded without Skin: {withoutSkin}.");
    }

    [Fact]
    public void FixedEbonyArrowMatchesIndependentGeometryMaterialsAndChunkBoundaries()
    {
        var clump = Fixed("2007-4-27", "Weapons", 39, 1_292_860, 5_514,
            "A4CEA62C4591415C9CAC4DFC9EE5CED2838A9EC5666F095D73198D087C405998");
        AssertDecodedHashes(clump,
            "7C06EA0764A2BD4230AB57BCF91F2BFB0AF2CE36D37C2587E7B7C7D8A571016B",
            "92DFE72FFB82C1E4009D1328B7A0087EAA890B1E5555A861E322F1266365B08C",
            "A622C5C57B036574836AD70D7F9DC916347A3C5D76D7A0959BC9ADEA4F6CB614",
            "24E790716C1BD0AC603E0C1007AF6B3F028BA6918F19907A24F634308EBC041A",
            "F2FB3CC516DCF1DA3DE9DB626D66458E21C28190198545C55203B069CE821CEA");
        Assert.Equal(new[] { -1, 0 }, clump.Frames.Select(f => f.ParentIndex));
        Assert.All(clump.Frames, frame => Assert.Equal(Matrix4x4.Identity, frame.LocalTransform));
        var atomic = Assert.Single(clump.Atomics);
        Assert.Equal((1, 0, 5u, 0u), (atomic.FrameIndex, atomic.GeometryIndex, atomic.Flags, atomic.Unused));
        var mesh = Assert.Single(clump.Geometries);
        Assert.Equal(0x10037u, mesh.Geometry.Flags);
        Assert.Equal(99, mesh.Geometry.Positions.Length);
        Assert.Equal(98, mesh.Geometry.Triangles.Length);
        Assert.Equal(new RwTriangle(29, 31, 28, 0), mesh.Geometry.Triangles[0]);
        AssertVector(new Vector3(0, 0.000199999995f, 0.2009000033f), mesh.Geometry.Positions[0]);
        Assert.NotNull(mesh.Geometry.Normals);
        AssertVector(new Vector3(0, 0.5781310797f, -0.8159438968f), mesh.Geometry.Normals[0]);
        var uvs = Assert.Single(mesh.Geometry.UvSets);
        Assert.InRange(Vector2.Distance(new Vector2(0.171299994f, 2.658100128f), uvs[0]), 0, 0.000001f);
        Assert.Equal(new Vector4(0, 0, 0, 0.3593942225f), mesh.Geometry.BoundingSphere);
        Assert.Null(mesh.Geometry.Colours);
        Assert.Equal(new[] { -1, -1 }, mesh.MaterialMap);
        Assert.Equal(ArrowTextures, mesh.Materials.Select(m => m.Texture!.Name));
        Assert.All(mesh.Materials, material =>
        {
            Assert.Equal((0u, uint.MaxValue, 921618676u, 1u),
                (material.Flags, material.ColorRgba, material.Unused, material.Textured));
            Assert.Equal((1f, 1f, 1f), (material.Ambient, material.Specular, material.Diffuse));
            Assert.NotNull(material.Texture);
            Assert.Equal(0x11102u, material.Texture.Sampler);
            Assert.Empty(material.Texture.MaskName);
        });
        Assert.Equal(32, mesh.Geometry.Triangles.Count(t => t.MaterialIndex == 0));
        Assert.Equal(66, mesh.Geometry.Triangles.Count(t => t.MaterialIndex == 1));
        Assert.True(mesh.BinMesh.IsTriangleStrip);
        Assert.Equal(176u, mesh.BinMesh.DeclaredIndexCount);
        Assert.Equal(ArrowSplitLengths, mesh.BinMesh.Splits.Select(s => s.Indices.Length));
        var binMesh = Assert.Single(mesh.Extensions, e => e.Type == 0x50E);
        Assert.Equal((4632, 732), (binMesh.HeaderOffset, binMesh.Payload.Length));
        var userData = Assert.Single(mesh.Extensions, e => e.Type == 0x11F);
        Assert.Equal((5376, 62), (userData.HeaderOffset, userData.Payload.Length));
        var hierarchy = Assert.Single(clump.Frames[1].Extensions);
        Assert.Equal((0x11Eu, 200, 32), (hierarchy.Type, hierarchy.HeaderOffset, hierarchy.Payload.Length));
        Assert.Contains(clump.Diagnostics, d => d.ChunkType == 0x11E);
        Assert.Contains(clump.Diagnostics, d => d.ChunkType == 0x11F);
        Assert.DoesNotContain(clump.Diagnostics, d => d.ChunkType == 0x116);
        Assert.Empty(clump.Attachments);
    }

    [Fact]
    public void FixedTranslatedRigidClumpRetainsParentPlacementAndUntexturedMaterial()
    {
        var clump = Fixed("2006-11-21", "RhaltaOb_2", 9, 3_192_296, 2_082,
            "535A8E819DEC0A607D01658A738DF28D3740B5F8BCAD9343EBC0E349035BBC3B");
        AssertDecodedHashes(clump,
            "517E693EC1CB0F388165FCFC06A4185D3F06B98C403B8A729F87A046E3E7DD79",
            "381CC0E9666097262FE962C8E319E33D868D5048A2A4EBE43587C00292300F11",
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            "EAA271761A26F716327DEBBF0AFC1266031168B364E7E4BD4377A28528146201",
            "C6FA62984DEC07DA9236582A1CDB105B05981619430D7C1A3937BF4AD94DC7A8");
        Assert.Equal(new[] { -1, 0 }, clump.Frames.Select(f => f.ParentIndex));
        Assert.Equal(Matrix4x4.CreateTranslation(0, 2.0744433403f, 0), clump.Frames[0].LocalTransform);
        Assert.Equal(Matrix4x4.Identity, clump.Frames[1].LocalTransform);
        Assert.Equal(1, Assert.Single(clump.Atomics).FrameIndex);
        var mesh = Assert.Single(clump.Geometries);
        Assert.Equal(0x73u, mesh.Geometry.Flags);
        Assert.Equal(42, mesh.Geometry.Positions.Length);
        Assert.Equal(24, mesh.Geometry.Triangles.Length);
        Assert.Empty(mesh.Geometry.UvSets);
        Assert.Null(mesh.Geometry.Colours);
        var material = Assert.Single(mesh.Materials);
        Assert.Equal(4278190284u, material.ColorRgba);
        Assert.Null(material.Texture);
        Assert.DoesNotContain(clump.Diagnostics, d => d.ChunkType == 0x116);
    }

    [Fact]
    public void FixedDaedricBowRetainsSkinBytesWithoutClaimingSkinEvaluation()
    {
        var clump = Fixed("2007-4-27", "Weapons", 6, 783_716, 14_412,
            "16246753D8882CF61DE72AC6F98DA1F5B3464ED773037DFC6354FDE6CDE2A1DB");
        AssertDecodedHashes(clump,
            "9FD90C8362CDEE27D685EA9BC7034681A1B349E880DAFEE49A4CDE7AD5E1B8E7",
            "853EEA4D81E20492EB97F3575C0262DB5FDAC919D6458B9F2C6B1FBF14BA5652",
            "1C262A4FA0B9CC7FB3B7F49ED59CB748CD81EBA38E45D7864459ECFB7F8F14EC",
            "3D369E9C863F67C7617800D9ECDDBF43DD945772E22AE2C0FCCD1487769B6A7F",
            "081049A65EC803591B7C7E79D4C21AC8E6D2602412C262D6C7E036F3FBCB6F8C");
        Assert.Equal(new[] { -1, 0, 1, 1, 3, 4 }, clump.Frames.Select(f => f.ParentIndex));
        var mesh = Assert.Single(clump.Geometries);
        Assert.Equal(182, mesh.Geometry.Positions.Length);
        Assert.Equal(224, mesh.Geometry.Triangles.Length);
        Assert.Equal(0x10037u, mesh.Geometry.Flags);
        Assert.Single(mesh.Geometry.UvSets);
        Assert.Null(mesh.Geometry.Colours);
        Assert.Equal(BowMaterialTriangleCounts, Enumerable.Range(0, 3)
            .Select(material => mesh.Geometry.Triangles.Count(t => t.MaterialIndex == material)));
        Assert.Equal(BowTextures,
            mesh.Materials.Select(m => m.Texture!.Name));
        var skin = Assert.Single(mesh.Extensions, extension => extension.Type == 0x116);
        Assert.Equal((10264, 3978), (skin.HeaderOffset, skin.Payload.Length));
        Assert.Contains(clump.Diagnostics, d => d.Scope == "geometry[0]" && d.ChunkType == 0x116);
        Assert.Equal(5, clump.Frames.Count(f => f.Extensions.Any(e => e.Type == 0x11E)));
        Assert.Contains(Assert.Single(clump.Atomics).Extensions, e => e.Type == 0x1F);
    }

    [Fact]
    public void FixedPlayerClumpRetainsCameraAndReportsUnimplementedSemantics()
    {
        var clump = Fixed("2006-11-21", "CPlayerBehaviour.Oblivion", 195, 5_998_380, 52_969,
            "357A0BBCA4279EEAEFC9EDDBF6093D3A5A42920B4A16544DB7B2F53E47F4F239");
        AssertDecodedHashes(clump,
            "F42E7F241D45DFF9223FF55A3909DA831825793B374E94E09865EE1294A524FA",
            "AEDF14F8304CB6F8773C67E10DF0FACFF1D48C30EE7ED49D22B5AE6FC2702127",
            "6D59936C83C69ECAE653FA3FD4E13C5D64E3606DFD40E7681B90F1B134615DA4",
            "A1C21C5E5F4EA8E4B0F73B90A4F709E8E0DEB64074BC4BDCD0149CBBBE438848",
            "38DDBBDBB50F151B2655C21756473242A203DFA438EF789528C8FB47D1B678F8");
        Assert.Equal(29, clump.Frames.Count);
        var rotated = clump.Frames[14];
        Assert.Equal(13, rotated.ParentIndex);
        Assert.Equal(0u, rotated.Flags);
        Assert.Equal(new Matrix4x4(
            0.5f, 0.8660253882408142f, -5.936166558306866E-10f, 0,
            -0.8660253882408142f, 0.5f, -4.4175568758575423E-10f, 0,
            4.322516997112835E-11f, -4.3313849729109677E-10f, 1, 0,
            0.049680937081575394f, -0.01724143885076046f, 0.004498037975281477f, 1), rotated.LocalTransform);
        var mesh = Assert.Single(clump.Geometries);
        Assert.Equal(604, mesh.Geometry.Positions.Length);
        Assert.Equal(866, mesh.Geometry.Triangles.Length);
        Assert.Equal(0x1003Fu, mesh.Geometry.Flags);
        Assert.Single(mesh.Geometry.UvSets);
        Assert.NotNull(mesh.Geometry.Colours);
        Assert.Equal(2416, mesh.Geometry.Colours.Length);
        Assert.Equal("B6E51697CE7F457D10C94FA9D44858C37F7D1422D3B7BC7590657F2D461E122B",
            Convert.ToHexString(SHA256.HashData(mesh.Geometry.Colours)));
        var camera = Assert.Single(clump.Attachments);
        Assert.Equal(5u, camera.Chunk.Type);
        Assert.Equal(10, camera.FrameIndex);
        Assert.Equal((52889, 56), (camera.Chunk.HeaderOffset, camera.Chunk.Payload.Length));
        Assert.Equal("C9EC099614C1C230C88637AC2762D9D29585063B17E74CD8BDD342C80C6ACC11",
            Convert.ToHexString(SHA256.HashData(camera.Chunk.Payload.Span)));
        Assert.Contains(clump.Diagnostics, d => d.ChunkType == 5);
        Assert.Contains(clump.Diagnostics, d => d.ChunkType == 0x116);
    }

    private static void AssertDecodedGeometry(RwClump decoded)
    {
        Assert.Equal(0x1C020065u, decoded.LibraryId);
        Assert.All(decoded.Geometries, geometry =>
        {
            Assert.False(geometry.Geometry.IsNative);
            Assert.True(geometry.BinMesh.TotalAgrees);
            Assert.InRange(geometry.Geometry.UvSets.Length, 0, 1);
        });
    }

    private static (int Frames, int Geometries, int Atomics) DeclaredCounts(byte[] source)
    {
        Assert.True(RwChunk.TryRead(source, 0, source.Length, out var root));
        Assert.Equal(source.Length, root.End);
        Assert.True(RwChunk.TryFindChild(source, root.PayloadOffset, root.End, RwChunk.FrameList, out var frames));
        Assert.True(RwChunk.TryFindChild(source, root.PayloadOffset, root.End, RwChunk.GeometryList, out var geometries));
        return (FirstStructCount(source, frames), FirstStructCount(source, geometries), FirstStructCount(source, root));
    }

    private static int FirstStructCount(byte[] source, RwChunkHeader parent)
    {
        Assert.True(RwChunk.TryRead(source, parent.PayloadOffset, parent.End, out var header));
        Assert.Equal(RwChunk.Struct, header.Type);
        Assert.True(header.Size >= sizeof(uint));
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(header.PayloadOffset)));
    }

    private static void AssertPinnedInvalidNormals(string date, string entryName, int ordinal,
        OblivionPspResource resource, byte[] source, string? error)
    {
        // Independently read from the raw GR.ARC and source CLUMP, without the production decoder.
        // Any additional decline, changed fixture or different failure remains an acceptance failure.
        Assert.Equal("2006-11-21", date);
        Assert.Equal("CEnemyBehaviourLoot.Villager2", entryName);
        Assert.Equal(50, ordinal);
        Assert.Equal(2_556_688, resource.PayloadOffset);
        Assert.Equal(82_006, source.Length);
        Assert.Equal("12B8F3B6E3C202DFCF0CA60980BFA07FC74A569B9B75692EA8A7BC518141B1CB",
            Convert.ToHexString(SHA256.HashData(source)));
        Assert.Equal("Nonfinite geometry normal.", error);
        Assert.Equal(0x1007Fu, BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4_180)));
        Assert.Equal(1_030u, BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4_184)));
        Assert.Equal(995u, BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4_188)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4_192)));
        AssertPinnedNormalWords(source);
    }

    private static void AssertPinnedNormalWords(byte[] source)
    {
        const int normalsOffset = 36_340;
        var nonfiniteComponents = 0;
        for (var vertex = 0; vertex < 995; vertex++)
        {
            var expectedNonfinite = vertex is >= 69 and <= 80 or >= 656 and <= 687;
            for (var component = 0; component < 3; component++)
            {
                var offset = normalsOffset + (vertex * 3 + component) * sizeof(float);
                var word = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset));
                var value = BitConverter.UInt32BitsToSingle(word);
                Assert.Equal(expectedNonfinite, !float.IsFinite(value));
                if (expectedNonfinite)
                {
                    Assert.Equal(0xFFC00000u, word);
                    nonfiniteComponents++;
                }
            }
        }

        Assert.Equal(132, nonfiniteComponents);
    }

    private static RwClump Fixed(string date, string entryName, int ordinal, int offset, int length, string hash)
    {
        var pack = RequirePack(date);
        var archive = OblivionPspArchive.Parse(pack);
        var entry = Assert.Single(archive.Entries, e => e.Name == entryName);
        using var stream = File.OpenRead(pack);
        var bytes = ReadEntry(stream, entry);
        var resources = OblivionPspResourceReader.ReadResources(bytes);
        Assert.InRange(ordinal, 0, resources.Count - 1);
        var resource = resources[ordinal];
        Assert.Equal("rwID_CLUMP", resource.TypeName);
        Assert.Equal(offset, resource.PayloadOffset);
        var source = ClumpBytes(bytes, resource);
        Assert.Equal(length, source.Length);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(source)));
        var decoded = RwClumpReader.TryRead(source, out var error);
        Assert.True(decoded is not null, error);
        Assert.NotNull(decoded);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(source)));
        Assert.Equal(source, decoded.Source.ToArray());
        return decoded;
    }

    private static byte[] ClumpBytes(byte[] entry, OblivionPspResource resource)
    {
        Assert.True(resource.IsRenderWareStream);
        Assert.True(RwChunk.TryRead(entry, resource.PayloadOffset,
            resource.PayloadOffset + resource.PayloadLength, out var root));
        Assert.Equal(RwChunk.Clump, root.Type);
        return entry.AsSpan(resource.PayloadOffset, RwChunk.HeaderLength + root.Size).ToArray();
    }

    private static byte[] ReadEntry(FileStream stream, OblivionPspArchiveEntry entry)
    {
        var bytes = new byte[checked((int)entry.Size)];
        stream.Position = entry.Offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string RequirePack(string date)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var build = RealAssetPaths.Travels.OblivionPspBuild(date);
        Assert.SkipWhen(build is null, RealAssetPaths.SkipMessage($"Oblivion PSP {date}"));
        Assert.NotNull(build);
        return Assert.Single(Directory.GetFiles(build, "GR.ARC", SearchOption.AllDirectories));
    }

    private static void AssertVector(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.000001f);

    // The expected digests come from an independent Python walk of the pinned raw resources,
    // not RwGeometry or this reader. Hash decoded values in a documented canonical LE layout so
    // a reader that repeats a first vertex, changes a later UV, or transposes frames cannot pass.
    private static void AssertDecodedHashes(RwClump clump, string positions, string normals,
        string uvs, string triangles, string frames)
    {
        var geometry = Assert.Single(clump.Geometries).Geometry;
        Assert.Equal(positions, Hash(writer =>
        {
            foreach (var value in geometry.Positions)
            {
                WriteVector(writer, value);
            }
        }));
        var normalValues = geometry.Normals;
        Assert.NotNull(normalValues);
        Assert.Equal(normals, Hash(writer =>
        {
            foreach (var value in normalValues)
            {
                WriteVector(writer, value);
            }
        }));
        Assert.Equal(uvs, Hash(writer =>
        {
            foreach (var set in geometry.UvSets)
            {
                Assert.Equal(geometry.Positions.Length, set.Length);
                foreach (var value in set)
                {
                    writer.Write(value.X);
                    writer.Write(value.Y);
                }
            }
        }));
        Assert.Equal(triangles, Hash(writer =>
        {
            foreach (var value in geometry.Triangles)
            {
                writer.Write(value.V0);
                writer.Write(value.V1);
                writer.Write(value.V2);
                writer.Write(value.MaterialIndex);
            }
        }));
        Assert.Equal(frames, Hash(writer =>
        {
            foreach (var frame in clump.Frames)
            {
                var matrix = frame.LocalTransform;
                WriteVector(writer, new Vector3(matrix.M11, matrix.M12, matrix.M13));
                WriteVector(writer, new Vector3(matrix.M21, matrix.M22, matrix.M23));
                WriteVector(writer, new Vector3(matrix.M31, matrix.M32, matrix.M33));
                WriteVector(writer, matrix.Translation);
                writer.Write(frame.ParentIndex);
                writer.Write(frame.Flags);
                Assert.Equal((0f, 0f, 0f, 1f), (matrix.M14, matrix.M24, matrix.M34, matrix.M44));
            }
        }));
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static string Hash(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            write(writer);
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}
