using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.NavMesh;
using BethesdaMultitool.Core.Minidump;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     End-to-end sparse-minidump tests for runtime NAVM discovery. Logical objects are either
///     split across VA-contiguous/file-noncontiguous regions or interrupted by a VA gap while
///     their old flat-file continuation remains valid bait.
/// </summary>
public sealed class RuntimeNavMeshDiscoverySparseTests
{
    [Fact]
    public void Discovery_StitchesCapturedObjectsAndProjectsCanonicalSubrecords()
    {
        var fixture = new Fixture(splitEveryObject: true);

        var fromCell = Assert.Single(fixture.Discovery.DiscoverForCellVa(Fixture.CellVa, Fixture.FallbackCellFormId));
        AssertProjectedNavMesh(fromCell);

        var fromInfoMap = Assert.Single(fixture.Discovery.Discover(fixture.NaviEntry));
        AssertProjectedNavMesh(fromInfoMap);

        var direct = fixture.Discovery.DiscoverForNavMeshVa(Fixture.NavMeshVa, Fixture.FallbackCellFormId);
        Assert.NotNull(direct);
        AssertProjectedNavMesh(direct!);
    }

    [Theory]
    [InlineData(GapTarget.CellPointer)]
    [InlineData(GapTarget.NavMeshArray)]
    [InlineData(GapTarget.NavMeshPointerArray)]
    public void CellDiscovery_FailsClosedAcrossRequiredTraversalGap(GapTarget gap)
    {
        var fixture = new Fixture(gap: gap);

        Assert.Empty(fixture.Discovery.DiscoverForCellVa(Fixture.CellVa, Fixture.FallbackCellFormId));
    }

    [Theory]
    [InlineData(GapTarget.NavMesh)]
    [InlineData(GapTarget.Vertices)]
    [InlineData(GapTarget.Triangles)]
    [InlineData(GapTarget.DoorPortals)]
    public void DirectDiscovery_RejectsIncompleteDeclaredGeometry(GapTarget gap)
    {
        var fixture = new Fixture(gap: gap);

        Assert.Null(fixture.Discovery.DiscoverForNavMeshVa(Fixture.NavMeshVa, Fixture.FallbackCellFormId));
    }

    [Fact]
    public void DirectDiscovery_UsesFallbackWhenParentFormIdCrossesGap()
    {
        var fixture = new Fixture(gap: GapTarget.ParentForm);

        var record = fixture.Discovery.DiscoverForNavMeshVa(Fixture.NavMeshVa, Fixture.FallbackCellFormId);

        Assert.NotNull(record);
        Assert.Equal(Fixture.FallbackCellFormId, record!.CellFormId);
    }

    [Fact]
    public void DirectDiscovery_ZeroesDoorFormIdWhenTargetHeaderCrossesGap()
    {
        var fixture = new Fixture(gap: GapTarget.DoorForm);

        var record = fixture.Discovery.DiscoverForNavMeshVa(Fixture.NavMeshVa, Fixture.FallbackCellFormId);

        Assert.NotNull(record);
        var nvdp = Assert.Single(record!.RawSubrecords, subrecord => subrecord.Signature == "NVDP");
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(nvdp.Bytes));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(nvdp.Bytes.AsSpan(4, 2)));
    }

    [Theory]
    [InlineData(GapTarget.NaviRoot)]
    [InlineData(GapTarget.BucketArray)]
    [InlineData(GapTarget.MapItem)]
    [InlineData(GapTarget.NavMeshInfo)]
    public void InfoMapDiscovery_FailsClosedAcrossTraversalGap(GapTarget gap)
    {
        var fixture = new Fixture(gap: gap);

        Assert.Empty(fixture.Discovery.Discover(fixture.NaviEntry));
    }

    [Fact]
    public void InfoMapDiscovery_PreservesTrustedStubWhenNavMeshBodyCrossesGap()
    {
        var fixture = new Fixture(gap: GapTarget.NavMesh);

        var record = Assert.Single(fixture.Discovery.Discover(fixture.NaviEntry));

        Assert.Equal(Fixture.NavMeshFormId, record.FormId);
        Assert.Equal(Fixture.InfoParentFormId, record.CellFormId);
        Assert.Empty(record.RawSubrecords);
        Assert.True(record.IsBigEndian);
    }

    [Fact]
    public void StructuralValidator_RejectsNavMeshWindowAcrossGapDespiteFlatBait()
    {
        var fixture = new Fixture(gap: GapTarget.NavMesh);
        var validator = new BsNavMeshStructuralValidator(
            fixture.Context,
            new HashSet<uint> { Fixture.ParentFormVa },
            BsNavMeshValidationMode.Permissive);

        Assert.False(validator.LooksLikeBsNavMesh(Fixture.NavMeshVa));
    }

    private static void AssertProjectedNavMesh(NavMeshRecord record)
    {
        Assert.Equal(Fixture.NavMeshFormId, record.FormId);
        Assert.Equal(Fixture.ParentCellFormId, record.CellFormId);
        Assert.Equal(1u, record.VertexCount);
        Assert.Equal(1u, record.TriangleCount);
        Assert.Equal(1, record.DoorPortalCount);
        Assert.False(record.IsBigEndian);

        var data = Assert.Single(record.RawSubrecords, subrecord => subrecord.Signature == "DATA").Bytes;
        Assert.Equal(Fixture.ParentCellFormId, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4)));

        var nvvx = Assert.Single(record.RawSubrecords, subrecord => subrecord.Signature == "NVVX").Bytes;
        Assert.Equal(1.25f, BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(0, 4)));
        Assert.Equal(-2.5f, BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(4, 4)));
        Assert.Equal(3.75f, BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(8, 4)));

        var nvtr = Assert.Single(record.RawSubrecords, subrecord => subrecord.Signature == "NVTR").Bytes;
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(0, 2)));
        Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(2, 2)));
        Assert.Equal((short)3, BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(4, 2)));
        Assert.Equal((short)-1, BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(6, 2)));
        Assert.Equal((short)-2, BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(8, 2)));
        Assert.Equal((short)-3, BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(10, 2)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(nvtr.AsSpan(12, 4)));

        var nvdp = Assert.Single(record.RawSubrecords, subrecord => subrecord.Signature == "NVDP").Bytes;
        Assert.Equal(Fixture.DoorFormId, BinaryPrimitives.ReadUInt32LittleEndian(nvdp));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(nvdp.AsSpan(4, 2)));
    }

    public enum GapTarget
    {
        NaviRoot,
        BucketArray,
        MapItem,
        NavMeshInfo,
        CellPointer,
        NavMeshArray,
        NavMeshPointerArray,
        NavMesh,
        Vertices,
        Triangles,
        DoorPortals,
        ParentForm,
        DoorForm
    }

    private sealed class Fixture
    {
        public const uint NaviVa = 0x40001000;
        public const uint BucketArrayVa = 0x40002000;
        public const uint MapItemVa = 0x40003000;
        public const uint NavMeshInfoVa = 0x40004000;
        public const uint CellVa = 0x40005000;
        public const uint NavMeshArrayVa = 0x40006000;
        public const uint NavMeshPointerArrayVa = 0x40007000;
        public const uint NavMeshVa = 0x40008000;
        public const uint VerticesVa = 0x40009000;
        public const uint TrianglesVa = 0x4000A000;
        public const uint DoorPortalsVa = 0x4000B000;
        public const uint ParentFormVa = 0x4000C000;
        public const uint DoorFormVa = 0x4000D000;

        public const uint NaviFormId = 0x00014B92;
        public const uint NavMeshFormId = 0x0100A001;
        public const uint ParentCellFormId = 0x0100C001;
        public const uint FallbackCellFormId = 0x0100C0F0;
        public const uint InfoParentFormId = 0x0100C0A0;
        public const uint DoorFormId = 0x0100D001;

        private const uint ModuleVtable = 0x82010000;

        public Fixture(GapTarget? gap = null, bool splitEveryObject = false)
        {
            var image = new SparseImage();

            image.Map(NaviVa, BuildNavi(), 50, ModeFor(GapTarget.NaviRoot));
            image.Map(BucketArrayVa, BuildBucketArray(), 2, ModeFor(GapTarget.BucketArray));
            image.Map(MapItemVa, BuildMapItem(), 6, ModeFor(GapTarget.MapItem));
            image.Map(NavMeshInfoVa, BuildNavMeshInfo(), 86, ModeFor(GapTarget.NavMeshInfo));
            image.Map(CellVa, BuildCell(), 118, ModeFor(GapTarget.CellPointer));
            image.Map(NavMeshArrayVa, BuildNavMeshArray(), 10, ModeFor(GapTarget.NavMeshArray));
            image.Map(NavMeshPointerArrayVa, BuildNavMeshPointerArray(), 2,
                ModeFor(GapTarget.NavMeshPointerArray));
            image.Map(NavMeshVa, BuildNavMesh(), 140, ModeFor(GapTarget.NavMesh));
            image.Map(VerticesVa, BuildVertices(), 6, ModeFor(GapTarget.Vertices));
            image.Map(TrianglesVa, BuildTriangles(), 8, ModeFor(GapTarget.Triangles));
            image.Map(DoorPortalsVa, BuildDoorPortals(), 4, ModeFor(GapTarget.DoorPortals));
            image.Map(ParentFormVa, BuildTesForm(0x39, ParentCellFormId), 14, ModeFor(GapTarget.ParentForm));
            image.Map(DoorFormVa, BuildTesForm(0x1C, DoorFormId), 14, ModeFor(GapTarget.DoorForm));

            Context = image.BuildContext();
            Discovery = new RuntimeNavMeshDiscovery(Context);
            NaviEntry = new RuntimeEditorIdEntry
            {
                EditorId = "NavMeshInfoMap",
                FormId = NaviFormId,
                FormType = 0x38,
                TesFormOffset = image.FileOffsetForVa(NaviVa),
                TesFormPointer = NaviVa
            };

            return;

            SparseMapMode ModeFor(GapTarget target)
            {
                if (gap == target)
                {
                    return SparseMapMode.GapWithFlatBait;
                }

                return splitEveryObject ? SparseMapMode.SplitCaptured : SparseMapMode.Contiguous;
            }
        }

        public RuntimeMemoryContext Context { get; }
        public RuntimeNavMeshDiscovery Discovery { get; }
        public RuntimeEditorIdEntry NaviEntry { get; }

        private static byte[] BuildNavi()
        {
            var bytes = BuildTesForm(0x38, NaviFormId, size: 80);
            WriteUInt32BE(bytes, 48, 1); // InfoMap +4: hash size.
            WriteUInt32BE(bytes, 52, BucketArrayVa); // InfoMap +8: bucket table.
            return bytes;
        }

        private static byte[] BuildBucketArray()
        {
            var bytes = new byte[4];
            WriteUInt32BE(bytes, 0, MapItemVa);
            return bytes;
        }

        private static byte[] BuildMapItem()
        {
            var bytes = new byte[12];
            WriteUInt32BE(bytes, 0, 0);
            WriteUInt32BE(bytes, 4, NavMeshFormId);
            WriteUInt32BE(bytes, 8, NavMeshInfoVa);
            return bytes;
        }

        private static byte[] BuildNavMeshInfo()
        {
            var bytes = new byte[92];
            WriteUInt32BE(bytes, 0, NavMeshFormId);
            WriteUInt32BE(bytes, 4, InfoParentFormId);
            WriteUInt32BE(bytes, 84, NavMeshVa);
            return bytes;
        }

        private static byte[] BuildCell()
        {
            var bytes = BuildTesForm(0x39, ParentCellFormId, size: 192);
            WriteUInt32BE(bytes, 116, NavMeshArrayVa);
            return bytes;
        }

        private static byte[] BuildNavMeshArray()
        {
            var bytes = new byte[16];
            WriteUInt32BE(bytes, 4, NavMeshPointerArrayVa);
            WriteUInt32BE(bytes, 8, 1);
            WriteUInt32BE(bytes, 12, 1);
            return bytes;
        }

        private static byte[] BuildNavMeshPointerArray()
        {
            var bytes = new byte[4];
            WriteUInt32BE(bytes, 0, NavMeshVa);
            return bytes;
        }

        private static byte[] BuildNavMesh()
        {
            var bytes = BuildTesForm(0x43, NavMeshFormId, size: 280);
            WriteUInt32BE(bytes, 0, ModuleVtable);
            WriteUInt32BE(bytes, 52, ParentFormVa);
            WriteArrayHeader(bytes, 56, VerticesVa, 1);
            WriteArrayHeader(bytes, 72, TrianglesVa, 1);
            WriteArrayHeader(bytes, 104, DoorPortalsVa, 1);
            return bytes;
        }

        private static byte[] BuildVertices()
        {
            var bytes = new byte[12];
            WriteFloatBE(bytes, 0, 1.25f);
            WriteFloatBE(bytes, 4, -2.5f);
            WriteFloatBE(bytes, 8, 3.75f);
            return bytes;
        }

        private static byte[] BuildTriangles()
        {
            var bytes = new byte[16];
            WriteUInt16BE(bytes, 0, 1);
            WriteUInt16BE(bytes, 2, 2);
            WriteUInt16BE(bytes, 4, 3);
            WriteUInt16BE(bytes, 6, unchecked((ushort)-1));
            WriteUInt16BE(bytes, 8, unchecked((ushort)-2));
            WriteUInt16BE(bytes, 10, unchecked((ushort)-3));
            WriteUInt32BE(bytes, 12, 0x12345678);
            return bytes;
        }

        private static byte[] BuildDoorPortals()
        {
            var bytes = new byte[8];
            WriteUInt32BE(bytes, 0, DoorFormVa);
            WriteUInt16BE(bytes, 4, 7);
            return bytes;
        }

        private static byte[] BuildTesForm(byte formType, uint formId, int size = 16)
        {
            var bytes = new byte[size];
            bytes[4] = formType;
            WriteUInt32BE(bytes, 12, formId);
            return bytes;
        }

        private static void WriteArrayHeader(byte[] bytes, int offset, uint dataVa, uint count)
        {
            WriteUInt32BE(bytes, offset, ModuleVtable);
            WriteUInt32BE(bytes, offset + 4, dataVa);
            WriteUInt32BE(bytes, offset + 8, count);
            WriteUInt32BE(bytes, offset + 12, count);
        }
    }

    private enum SparseMapMode
    {
        Contiguous,
        SplitCaptured,
        GapWithFlatBait
    }

    private sealed class SparseImage
    {
        private const int SlotSize = 0x800;
        private const int SplitTailOffset = 0x400;

        private readonly byte[] _file = new byte[0x10000];
        private readonly List<MinidumpMemoryRegion> _regions = [];
        private readonly Dictionary<uint, long> _fileOffsets = [];
        private int _slot;

        public void Map(uint va, byte[] bytes, int splitAt, SparseMapMode mode)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(splitAt, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(splitAt, bytes.Length);

            var baseOffset = checked((long)_slot++ * SlotSize);
            _fileOffsets.Add(va, baseOffset);
            bytes.CopyTo(_file, checked((int)baseOffset));

            switch (mode)
            {
                case SparseMapMode.Contiguous:
                    AddRegion(va, baseOffset, bytes.Length);
                    break;

                case SparseMapMode.SplitCaptured:
                    Array.Fill(_file, (byte)0xDE, checked((int)baseOffset + splitAt), bytes.Length - splitAt);
                    Array.Copy(bytes, splitAt, _file, checked((int)baseOffset + SplitTailOffset),
                        bytes.Length - splitAt);
                    AddRegion(va, baseOffset, splitAt);
                    AddRegion(va + (uint)splitAt, baseOffset + SplitTailOffset, bytes.Length - splitAt);
                    break;

                case SparseMapMode.GapWithFlatBait:
                    // Keep the complete valid object physically contiguous so the old
                    // VA->file-offset->ReadBytes path succeeds. Only the leading VA fragment is
                    // captured; ReadBytesAtVa must reject the logical range.
                    AddRegion(va, baseOffset, splitAt);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        public long FileOffsetForVa(uint va)
        {
            return _fileOffsets[va];
        }

        public RuntimeMemoryContext BuildContext()
        {
            var info = new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
                MemoryRegions = _regions.OrderBy(region => region.VirtualAddress).ToList()
            };
            return new RuntimeMemoryContext(new ByteArrayMemoryAccessor(_file), _file.Length, info);
        }

        private void AddRegion(uint va, long fileOffset, int size)
        {
            _regions.Add(new MinidumpMemoryRegion
            {
                VirtualAddress = va,
                FileOffset = fileOffset,
                Size = size
            });
        }
    }
}
