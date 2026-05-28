using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Synthetic test fixture for runtime-reader tests. Wraps a
///     <see cref="SparseMemoryAccessor" /> + synthetic
///     <see cref="MinidumpInfo" /> + heap-base VA and builds a
///     <see cref="RuntimeMemoryContext" /> on demand.
///     <para>
///         Replaces the captured-DMP snippet harness for runtime tests. Per
///         <c>feedback_test_discipline</c>: minimal in-memory data, no real
///         DMPs, no rate floors, exact-value assertions only.
///     </para>
///     <para>Usage:</para>
///     <code>
/// var buffer = SyntheticStructFactory.BuildNpc(formId: 0x1234, racePtr: 0x40200000);
/// var raceStub = SyntheticStructFactory.BuildRefr(formId: 0x5678);
/// var fixture = RuntimeReaderTestFixture.Default()
///     .WithStruct(buffer, va: 0x40100000)
///     .WithPointerTarget(targetVa: 0x40200000, raceStub);
/// var entry = fixture.MakeEntry(formId: 0x1234, formType: 0x2A, structVa: 0x40100000);
/// var context = fixture.BuildContext();
///     </code>
/// </summary>
internal sealed class RuntimeReaderTestFixture
{
    /// <summary>
    ///     Synthetic heap base address. Tests pick VAs in
    ///     [HeapBaseVa, HeapBaseVa + 0x10000000) for struct placement; the
    ///     accessor maps file-offset = (va - HeapBaseVa).
    /// </summary>
    public const uint HeapBaseVa = 0x40000000;

    private const long HeapRegionSize = 0x10_000_000L;

    private readonly SparseMemoryAccessor _accessor = new();

    private RuntimeReaderTestFixture() { }

    /// <summary>
    ///     Creates a fixture with an empty heap. Subsequent <c>.With*</c> calls
    ///     populate the sparse accessor; <see cref="BuildContext" /> finalises
    ///     it into a usable <see cref="RuntimeMemoryContext" />.
    /// </summary>
    public static RuntimeReaderTestFixture Default() => new();

    /// <summary>
    ///     Places a struct buffer at the given virtual address. The buffer's
    ///     bytes become readable at file-offset = <paramref name="va" /> -
    ///     <see cref="HeapBaseVa" />.
    /// </summary>
    public RuntimeReaderTestFixture WithStruct(byte[] buffer, uint va)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (va < HeapBaseVa)
        {
            throw new ArgumentOutOfRangeException(nameof(va),
                $"VA 0x{va:X8} is below HeapBaseVa 0x{HeapBaseVa:X8}");
        }

        _accessor.AddRange(va - HeapBaseVa, buffer);
        return this;
    }

    /// <summary>
    ///     Places raw bytes at the given virtual address. Use for pointer
    ///     targets (e.g. a TESForm struct that another struct's pointer
    ///     references, or a null-terminated string for a BSStringT).
    /// </summary>
    public RuntimeReaderTestFixture WithPointerTarget(uint va, byte[] bytes)
        => WithStruct(bytes, va);

    /// <summary>
    ///     Builds a <see cref="RuntimeMemoryContext" /> over the current sparse
    ///     heap. The synthetic <see cref="MinidumpInfo" /> declares a single
    ///     contiguous region at <see cref="HeapBaseVa" /> covering
    ///     <see cref="HeapRegionSize" /> bytes — the
    ///     <see cref="SparseMemoryAccessor" /> handles the actual sparsity.
    /// </summary>
    public RuntimeMemoryContext BuildContext()
    {
        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03, // PowerPC
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = HeapBaseVa,
                    FileOffset = 0,
                    Size = HeapRegionSize
                }
            ]
        };
        return new RuntimeMemoryContext(_accessor, HeapRegionSize, minidumpInfo);
    }

    /// <summary>
    ///     Builds a <see cref="RuntimeEditorIdEntry" /> referencing the struct
    ///     at <paramref name="structVa" />. Mirrors what the production
    ///     hash-table scan would produce for a real entry.
    /// </summary>
    public RuntimeEditorIdEntry MakeEntry(uint formId, byte formType, uint structVa,
        string editorId = "TestEntry")
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = editorId,
            FormId = formId,
            FormType = formType,
            TesFormOffset = structVa - HeapBaseVa
        };
    }
}
