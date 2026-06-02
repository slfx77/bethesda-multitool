using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Synthetic in-memory tests for <see cref="BsNavMeshStructuralValidator" />: the
///     shape-only predicate that gates Phase 2d's speculative Path 4 candidates. Each
///     test plants a 280-byte BSNavMesh-shaped struct with controlled vfptr / array
///     headers / parent pointer, then asserts the validator's verdict.
/// </summary>
public sealed class BsNavMeshStructuralValidatorTests
{
    private const uint HeapBaseVa = 0x40000000;
    private const uint ModuleVtable = 0x82010000;
    private const uint HeapVtable = 0x40FF0000;

    private const int BsNavMeshSize = 280;
    private const int ParentCellOffset = 52;
    private const int VerticesOffset = 56;
    private const int TrianglesOffset = 72;
    private const int DoorPortalsOffset = 104;
    private const int BsSimpleArrayCountFieldOffset = 8;
    private const int BsSimpleArrayReservedFieldOffset = 12;

    [Fact]
    public void Rejects_NonModulePointer_Vfptr()
    {
        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceBsNavMesh(vfptr: HeapVtable);

        var validator = heap.BuildValidator(BsNavMeshValidationMode.Permissive, knownCellVas: []);
        Assert.False(validator.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Rejects_Vertices_Implausible_iSize()
    {
        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceBsNavMesh(verticesSize: 2_000_000, verticesReserved: 2_000_000);

        var validator = heap.BuildValidator(BsNavMeshValidationMode.Permissive, knownCellVas: []);
        Assert.False(validator.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Rejects_Vertices_iReservedSize_BelowSize()
    {
        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceBsNavMesh(verticesSize: 100, verticesReserved: 50);

        var validator = heap.BuildValidator(BsNavMeshValidationMode.Permissive, knownCellVas: []);
        Assert.False(validator.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Rejects_Triangles_Implausible_iSize()
    {
        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceBsNavMesh(trianglesSize: 2_000_000, trianglesReserved: 2_000_000);

        var validator = heap.BuildValidator(BsNavMeshValidationMode.Permissive, knownCellVas: []);
        Assert.False(validator.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Rejects_DoorPortals_Implausible_iSize()
    {
        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceBsNavMesh(doorPortalsSize: 2_000_000, doorPortalsReserved: 2_000_000);

        var validator = heap.BuildValidator(BsNavMeshValidationMode.Permissive, knownCellVas: []);
        Assert.False(validator.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Rejects_AllEmptyArrays()
    {
        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceBsNavMesh(
            verticesSize: 0, trianglesSize: 0, doorPortalsSize: 0);

        var validator = heap.BuildValidator(BsNavMeshValidationMode.Permissive, knownCellVas: []);
        Assert.False(validator.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Accepts_ValidBsNavMesh_NoParent_InPermissiveMode()
    {
        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceBsNavMesh(
            verticesSize: 12, verticesReserved: 16,
            trianglesSize: 8, trianglesReserved: 16,
            parentCellVa: 0);

        var validator = heap.BuildValidator(BsNavMeshValidationMode.Permissive, knownCellVas: []);
        Assert.True(validator.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Accepts_ValidBsNavMesh_WithKnownParent_InBothModes()
    {
        var heap = new HeapBuilder(0x4000);
        var fakeCellVa = HeapBaseVa + 0x800;   // placeholder VA the validator only set-compares
        var navmVa = heap.PlaceBsNavMesh(
            verticesSize: 12, verticesReserved: 16,
            trianglesSize: 8, trianglesReserved: 16,
            parentCellVa: fakeCellVa);

        var knownCells = new HashSet<uint> { fakeCellVa };
        var permissive = heap.BuildValidator(BsNavMeshValidationMode.Permissive, knownCells);
        var strict = heap.BuildValidator(BsNavMeshValidationMode.Strict, knownCells);

        Assert.True(permissive.LooksLikeBsNavMesh(navmVa));
        Assert.True(strict.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Strict_Rejects_NullParent()
    {
        var heap = new HeapBuilder(0x4000);
        var navmVa = heap.PlaceBsNavMesh(
            verticesSize: 12, verticesReserved: 16,
            parentCellVa: 0);

        var validator = heap.BuildValidator(BsNavMeshValidationMode.Strict, knownCellVas: []);
        Assert.False(validator.LooksLikeBsNavMesh(navmVa));
    }

    [Fact]
    public void Strict_Rejects_UnknownParent()
    {
        var heap = new HeapBuilder(0x4000);
        var trustedCellVa = HeapBaseVa + 0x800;
        var unknownCellVa = HeapBaseVa + 0x1200;   // a VA NOT in KnownCellVas
        var navmVa = heap.PlaceBsNavMesh(
            verticesSize: 12, verticesReserved: 16,
            parentCellVa: unknownCellVa);

        var knownCells = new HashSet<uint> { trustedCellVa };
        var validator = heap.BuildValidator(BsNavMeshValidationMode.Strict, knownCells);
        Assert.False(validator.LooksLikeBsNavMesh(navmVa));
    }

    // ============================================================================
    // Test helpers — HeapBuilder pattern matches RuntimeCellEnumeratorTests.
    // ============================================================================

    private sealed class HeapBuilder
    {
        private readonly byte[] _buffer;
        private int _cursor;

        public HeapBuilder(int sizeBytes)
        {
            _buffer = new byte[sizeBytes];
            _cursor = 0x100;
        }

        /// <summary>
        ///     Plant a 280-byte BSNavMesh-shaped struct with optional overrides for the fields
        ///     the validator inspects. Defaults produce a "passes all checks" baseline so
        ///     reject tests only need to mutate the field under test.
        /// </summary>
        public uint PlaceBsNavMesh(
            uint vfptr = ModuleVtable,
            uint parentCellVa = 0,
            uint verticesSize = 4, uint verticesReserved = 4,
            uint trianglesSize = 4, uint trianglesReserved = 4,
            uint doorPortalsSize = 0, uint doorPortalsReserved = 0)
        {
            var va = AllocateAligned(BsNavMeshSize);
            var offset = OffsetForVa(va);

            WriteUInt32BE(_buffer, offset + 0, vfptr);
            WriteUInt32BE(_buffer, offset + ParentCellOffset, parentCellVa);

            // Each BSSimpleArray header is 16 bytes; we only populate the size fields the
            // validator reads (count at +8, reserved at +12). vfptr/pBuffer are left zero.
            WriteUInt32BE(_buffer, offset + VerticesOffset + BsSimpleArrayCountFieldOffset, verticesSize);
            WriteUInt32BE(_buffer, offset + VerticesOffset + BsSimpleArrayReservedFieldOffset, verticesReserved);

            WriteUInt32BE(_buffer, offset + TrianglesOffset + BsSimpleArrayCountFieldOffset, trianglesSize);
            WriteUInt32BE(_buffer, offset + TrianglesOffset + BsSimpleArrayReservedFieldOffset, trianglesReserved);

            WriteUInt32BE(_buffer, offset + DoorPortalsOffset + BsSimpleArrayCountFieldOffset, doorPortalsSize);
            WriteUInt32BE(_buffer, offset + DoorPortalsOffset + BsSimpleArrayReservedFieldOffset, doorPortalsReserved);

            return va;
        }

        public BsNavMeshStructuralValidator BuildValidator(
            BsNavMeshValidationMode mode,
            HashSet<uint> knownCellVas)
        {
            var accessor = new SparseMemoryAccessor();
            accessor.AddRange(0, _buffer);

            var minidumpInfo = new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
                MemoryRegions =
                [
                    new MinidumpMemoryRegion
                    {
                        VirtualAddress = HeapBaseVa,
                        FileOffset = 0,
                        Size = _buffer.Length
                    }
                ]
            };
            var context = new RuntimeMemoryContext(accessor, _buffer.Length, minidumpInfo);
            return new BsNavMeshStructuralValidator(context, knownCellVas, mode);
        }

        private uint AllocateAligned(int size)
        {
            var alignedCursor = (_cursor + 3) & ~3;
            var va = HeapBaseVa + (uint)alignedCursor;
            _cursor = alignedCursor + size;
            if (_cursor > _buffer.Length)
            {
                throw new InvalidOperationException(
                    $"Heap exhausted: tried to allocate {size}B at +0x{alignedCursor:X4} (limit 0x{_buffer.Length:X4}).");
            }

            return va;
        }

        private static int OffsetForVa(uint va) => unchecked((int)(va - HeapBaseVa));
    }
}
