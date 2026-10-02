using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Scanning;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeScanRepresentativeTests
{
    [Theory]
    [InlineData("mesh")]
    [InlineData("texture")]
    [InlineData("gpu-texture")]
    [InlineData("scene")]
    public void Representative_preserves_lowest_physical_candidate_across_arrival_orders(string kind)
    {
        object[] candidates = [Candidate(kind, 0x3000, 7), Candidate(kind, 0x1000, 7),
            Candidate(kind, 0x2000, 7), Candidate(kind, 0x4000, 9)];
        int[][] orders = [[0, 1, 2, 3], [3, 2, 1, 0], [2, 0, 3, 1]];
        foreach (var order in orders)
        {
            var selected = new ConcurrentDictionary<long, object>();
            var unique = 0;
            foreach (var index in order)
            {
                if (RuntimeCandidateSelection.KeepLowestOffset(
                        selected, Key(candidates[index]), candidates[index], Offset)) unique++;
            }
            Assert.Equal(2, unique);
            Assert.Same(candidates[1], selected[7]);
            Assert.Same(candidates[3], selected[9]);
            // A second decode of the same physical occurrence does not replace its payload
            // or count as another logical item, even when its arrays are separate objects.
            var sameOccurrence = Candidate(kind, 0x1000, 7);
            Assert.False(RuntimeCandidateSelection.KeepLowestOffset(selected, 7, sameOccurrence, Offset));
            Assert.Same(candidates[1], selected[7]);
        }

        var parallel = new ConcurrentDictionary<long, object>();
        var parallelUnique = 0;
        Parallel.ForEach(Enumerable.Range(0, 64), new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var candidate = candidates[i % candidates.Length];
            if (RuntimeCandidateSelection.KeepLowestOffset(parallel, Key(candidate), candidate, Offset))
                Interlocked.Increment(ref parallelUnique);
        });
        Assert.Equal(2, parallelUnique);
        Assert.Same(candidates[1], parallel[7]);
        Assert.Same(candidates[3], parallel[9]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_scanners_keep_canonical_payload_node_and_filename_with_reversed_va_regions(bool reverseRegions)
    {
        var file = new byte[0x5000];
        var regions = Enumerable.Range(0, 5).Select(i => new MinidumpMemoryRegion
        {
            FileOffset = i * 0x1000,
            VirtualAddress = 0x40000000L + (reverseRegions ? 4 - i : i) * 0x2000,
            Size = 0x1000
        }).ToList();
        uint Va(int offset) => checked((uint)(regions[offset / 0x1000].VirtualAddress + offset % 0x1000));
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(offset), value);
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(offset), value);
        void Float(int offset, float value) => U32(offset, BitConverter.SingleToUInt32Bits(value));
        void Text(int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(file, offset);

        foreach (var (source, data) in new[] { (0x1100, 0x800), (0x3100, 0x3800) })
        {
            U32(source + 4, 1);
            U16(source + 8, 3);
            Float(source + 28, 2);
            U32(source + 32, Va(data));
            U32(source + 36, Va(data + 0x80));
            U16(source + 64, 1);
            U32(source + 68, 3);
            U32(source + 72, Va(data + 0xC0));
            float[] vertices = [0, 0, 0, 1, 0, 0, 0, 1, 0];
            for (var i = 0; i < vertices.Length; i++) Float(data + i * 4, vertices[i]);
            for (var i = 0; i < 3; i++) Float(data + 0x80 + i * 12 + 8, 1);
            U16(data + 0xC0, 0); U16(data + 0xC2, 1); U16(data + 0xC4, 2);
        }

        foreach (var (source, data) in new[] { (0x1200, 0x900), (0x3200, 0x3900) })
        {
            U32(source + 4, 1);
            U32(source + 12, (uint)NiTextureFormat.DXT1);
            U32(source + 80, Va(data));
            U32(source + 84, Va(data + 0x20));
            U32(source + 88, Va(data + 0x24));
            U32(source + 96, 1);
            U32(source + 108, 1);
            file.AsSpan(data, 8).Fill(0xA5);
            U32(data + 0x20, 4); U32(data + 0x24, 4);
        }

        foreach (var (source, name, x) in new[] { (0x1400, 0xB00, 10), (0x3400, 0xB40, 20) })
        {
            U32(source + 4, 1);
            U32(source + 8, Va(name));
            U32(source + 220, Va(0x1100));
            Float(source + 176, x);
        }
        Text(0xB00, "Low physical node"); Text(0xB40, "High physical node");

        // The lower physical NiSourceTexture owns the higher filename pointer. An earlier
        // empty alias must not prevent resolving the first valid canonical name.
        foreach (var (source, name) in new[] { (0x1500, 0xA00), (0x1600, 0xA80), (0x3600, 0x980) })
        {
            U32(source + 4, 1);
            U32(source + 48, Va(name));
            U32(source + 60, Va(0x1200));
        }
        Text(0xA80, "LowOwner.dds"); Text(0x980, "HighOwner.dds");

        var info = new MinidumpInfo { IsValid = true, ProcessorArchitecture = 3, MemoryRegions = regions };
        var context = new RuntimeMemoryContext(new ByteArrayMemoryAccessor(file), file.Length, info);
        var geometry = new RuntimeGeometryScanner(context);
        var mesh = Assert.Single(geometry.ScanForMeshes());
        Assert.Equal(1, geometry.MeshesFound);
        Assert.Equal(0x1100, mesh.SourceOffset);
        Assert.Equal(0x800, mesh.VertexDataFileOffset);
        Assert.Equal(0x880, mesh.NormalDataFileOffset);
        Assert.Equal(0x8C0, mesh.IndexDataFileOffset);
        Assert.Equal(new ushort[] { 0, 1, 2 }, mesh.TriangleIndices);

        var textureScanner = new RuntimeTextureScanner(context);
        var texture = Assert.Single(textureScanner.ScanForTextures());
        Assert.Equal(1, textureScanner.TexturesFound);
        Assert.Equal(0x1200, texture.SourceOffset);
        Assert.Equal(0x900, texture.PixelDataFileOffset);
        Assert.Equal("LowOwner.dds", texture.Filename);
        Assert.Equal(8, texture.DataSize);

        var nodes = new RuntimeSceneGraphWalker(context).WalkSceneGraph([mesh]);
        var node = Assert.Single(nodes).Value;
        Assert.Equal(0x1400, node.NiTriShapeFileOffset);
        Assert.Equal("Low physical node", node.NodeName);
        Assert.Equal(10, node.WorldX);
    }

    private static object Candidate(string kind, long offset, long key) => kind switch
    {
        "mesh" => new ExtractedMesh { SourceOffset = offset, VertexHash = key,
            VertexDataFileOffset = offset + 0x80, Vertices = [1, 2, 3] },
        "texture" or "gpu-texture" => new ExtractedTexture { SourceOffset = offset, DataHash = key,
            PixelDataFileOffset = offset + 0x80, PixelData = [1, 2, 3] },
        "scene" => new SceneGraphInfo { NiTriShapeFileOffset = offset, RootNodeVa = (uint)key,
            ParentNames = ["physical " + offset] },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static long Key(object value) => value switch
    {
        ExtractedMesh mesh => mesh.VertexHash,
        ExtractedTexture texture => texture.DataHash,
        SceneGraphInfo scene => scene.RootNodeVa,
        _ => throw new ArgumentException("Unknown runtime candidate", nameof(value))
    };

    private static long Offset(object value) => value switch
    {
        ExtractedMesh mesh => mesh.SourceOffset,
        ExtractedTexture texture => texture.SourceOffset,
        SceneGraphInfo scene => scene.NiTriShapeFileOffset,
        _ => throw new ArgumentException("Unknown runtime candidate", nameof(value))
    };
}
