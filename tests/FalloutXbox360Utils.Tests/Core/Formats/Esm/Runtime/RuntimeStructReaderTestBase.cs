using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Shared fixture for RuntimeStructReader tests that work against a synthetic memory-mapped
///     heap. Owns the MMF/accessor/temp-file lifecycle and provides the small set of helpers
///     (CreateReader, FileOffsetToVa, WriteTesFormHeader) that every derived test needs.
///
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
    private string? _tempFilePath;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _accessor?.Dispose();
        _mmf?.Dispose();

        if (_tempFilePath != null && File.Exists(_tempFilePath))
        {
            try
            {
                File.Delete(_tempFilePath);
            }
            catch
            {
                // Best-effort cleanup; temp files are cleaned by OS eventually.
            }
        }
    }

    /// <summary>
    ///     Low-level lifecycle primitive: writes <paramref name="data"/> to a temp file,
    ///     memory-maps it, and returns the read-only accessor. The MMF / accessor / temp file
    ///     are owned by this base class and torn down in <see cref="Dispose"/>.
    ///     Use this overload when the test needs to build a custom <see cref="MinidumpInfo"/>
    ///     (e.g. multi-region layouts for tests that exercise both heap and module regions, or
    ///     callers that invoke <c>RuntimeStructReader.CreateWithAutoDetect</c> directly).
    ///     For the common single-heap-region case use <see cref="CreateReader"/> instead.
    /// </summary>
    protected MemoryMappedViewAccessor MapSyntheticBytes(byte[] data)
    {
        if (_accessor is not null)
        {
            throw new InvalidOperationException(
                "Synthetic heap is already set up for this test. The base class owns one MMF per test instance.");
        }

        _tempFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(_tempFilePath, data);

        _mmf = MemoryMappedFile.CreateFromFile(_tempFilePath, FileMode.Open, null, data.Length,
            MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.Read);
        return _accessor;
    }

    /// <summary>
    ///     Writes <paramref name="data"/> to a temp file, memory-maps it, and returns a reader
    ///     pointed at a single memory region spanning the file at VA <see cref="HeapBaseVa"/>.
    ///     Subsequent <see cref="Dispose"/> tears the MMF / accessor / temp file down.
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
    ///     Write a TESForm header at <paramref name="fileOffset"/>. Layout:
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
