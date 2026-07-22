using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Shared fixture for RuntimeStructReader tests that work against a synthetic memory-mapped
///     heap. Owns the MMF/accessor lifecycle and provides the small set of helpers
///     (CreateReader, FileOffsetToVa, WriteTesFormHeader) that every derived test needs.
///     Per-test specifics — MakeEntry variants, extra-data writers, struct-offset constants,
///     and DataSize — stay in the derived classes because they're shaped to each suite's needs.
/// </summary>
public abstract class RuntimeStructReaderTestBase : IDisposable
{
    /// <summary>
    ///     Xbox 360 heap base VA. <c>VaToLong(0x40000000)</c> = 0x40000000 (positive, no sign
    ///     extension), so file offsets map linearly into the synthetic region.
    /// </summary>
    protected const uint HeapBaseVa = 0x40000000;

    private MemoryMappedViewAccessor? _accessor;
    private MemoryMappedFile? _mmf;
    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!disposing)
        {
            return;
        }

        _accessor?.Dispose();
        _mmf?.Dispose();
    }

    /// <summary>
    ///     Low-level lifecycle primitive: copies <paramref name="data" /> into an in-memory
    ///     MMF and returns the accessor. The MMF / accessor are owned by this base class and
    ///     torn down in <see cref="Dispose()" />.
    ///     Use this overload when the test needs to build a custom <see cref="MinidumpInfo" />
    ///     (e.g. multi-region layouts for tests that exercise both heap and module regions, or
    ///     callers that invoke <c>RuntimeStructReader.CreateWithAutoDetect</c> directly).
    ///     For the common single-heap-region case use <see cref="CreateReader" /> instead.
    /// </summary>
    protected MemoryMappedViewAccessor MapSyntheticBytes(byte[] data)
    {
        if (_accessor is not null)
        {
            throw new InvalidOperationException(
                "Synthetic heap is already set up for this test. The base class owns one MMF per test instance.");
        }

        _mmf = MemoryMappedFile.CreateNew(null, data.Length);
        _accessor = _mmf.CreateViewAccessor(0, data.Length);
        _accessor.WriteArray(0, data, 0, data.Length);
        return _accessor;
    }

    /// <summary>
    ///     Copies <paramref name="data" /> into an in-memory MMF and returns a reader
    ///     pointed at a single memory region spanning the data at VA <see cref="HeapBaseVa" />.
    ///     Subsequent <see cref="Dispose()" /> tears the MMF / accessor down.
    /// </summary>
    protected RuntimeStructReader CreateReader(byte[] data)
    {
        var accessor = MapSyntheticBytes(data);

        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03, // PowerPC
            NumberOfStreams = 1,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(HeapBaseVa),
                    Size = data.Length,
                    FileOffset = 0
                }
            ]
        };

        return new RuntimeStructReader(accessor, data.Length, minidumpInfo);
    }

    /// <summary>Convert a file offset to the corresponding Xbox 360 VA in the synthetic heap.</summary>
    protected static uint FileOffsetToVa(int fileOffset)
    {
        return HeapBaseVa + (uint)fileOffset;
    }

    /// <summary>
    ///     Write a TESForm header at <paramref name="fileOffset" />. Layout:
    ///     <c>byte[0-3]</c> = vtable pointer (big-endian), <c>byte[4]</c> = formType,
    ///     <c>byte[12-15]</c> = formId (big-endian).
    /// </summary>
    protected static void WriteTesFormHeader(byte[] data, int fileOffset, uint vtable, byte formType, uint formId)
    {
        WriteUInt32BE(data, fileOffset, vtable);
        data[fileOffset + 4] = formType;
        WriteUInt32BE(data, fileOffset + 12, formId);
    }
}