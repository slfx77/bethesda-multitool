using System.Buffers.Binary;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Builds byte-valid minidump files (header + stream directory + Memory64List stream + region
///     payloads) for parser and scanner tests. Unlike <see cref="DmpSnippetMinidumpInfo" /> (which
///     fabricates a MinidumpInfo directly), this produces real MDMP bytes so tests can exercise
///     MinidumpParser end-to-end — including BaseRva parity (the file-offset vs virtual-address
///     alignment delta that hid the gap-scanner parity bug), large range counts, and regions whose
///     declared size runs past EOF.
/// </summary>
internal sealed class SyntheticMinidumpBuilder
{
    private const int HeaderSize = 32;
    private const int DirectoryEntrySize = 12;
    private const int SystemInfoSize = 64;
    private const int Memory64HeaderSize = 16;
    private const int Memory64DescriptorSize = 16;

    private readonly List<Region> _regions = [];
    private int _baseRvaAlignmentModulus = 1;
    private int _baseRvaAlignmentRemainder;
    private ushort? _processorArchitecture;

    private sealed record Region(long VirtualAddress, long DeclaredSize, byte[] Payload);

    /// <summary>Add a region whose declared size matches its payload.</summary>
    public SyntheticMinidumpBuilder AddRegion(long virtualAddress, byte[] payload)
    {
        _regions.Add(new Region(virtualAddress, payload.Length, payload));
        return this;
    }

    /// <summary>
    ///     Add a region that declares <paramref name="declaredSize" /> bytes but only carries
    ///     <paramref name="payload" /> in the file — i.e. the dump is truncated inside this region.
    ///     Only meaningful as the last region (later payloads would land at wrong offsets).
    /// </summary>
    public SyntheticMinidumpBuilder AddTruncatedRegion(long virtualAddress, long declaredSize, byte[] payload)
    {
        if (declaredSize <= payload.Length)
        {
            throw new ArgumentException("declaredSize must exceed the payload for a truncated region.");
        }

        _regions.Add(new Region(virtualAddress, declaredSize, payload));
        return this;
    }

    /// <summary>
    ///     Pad the pre-payload area so that BaseRva ≡ <paramref name="remainder" /> (mod
    ///     <paramref name="modulus" />). The Debug-era corpus dumps have BaseRva ≡ 2 (mod 4);
    ///     Release dumps are ≡ 0 — this knob reproduces both classes.
    /// </summary>
    public SyntheticMinidumpBuilder WithBaseRvaAlignment(int modulus, int remainder)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(modulus, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(remainder);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(remainder, modulus);
        _baseRvaAlignmentModulus = modulus;
        _baseRvaAlignmentRemainder = remainder;
        return this;
    }

    /// <summary>Include a SystemInfo stream carrying this processor architecture (0x03 = PowerPC/Xbox 360).</summary>
    public SyntheticMinidumpBuilder WithProcessorArchitecture(ushort architecture)
    {
        _processorArchitecture = architecture;
        return this;
    }

    /// <summary>Emit the minidump bytes.</summary>
    public byte[] Build()
    {
        if (_regions.Count == 0)
        {
            throw new InvalidOperationException("At least one region is required.");
        }

        var streamCount = _processorArchitecture.HasValue ? 2 : 1;
        var directoryOffset = HeaderSize;
        var systemInfoOffset = directoryOffset + streamCount * DirectoryEntrySize;
        var memory64Offset = systemInfoOffset + (_processorArchitecture.HasValue ? SystemInfoSize : 0);
        var descriptorsEnd = memory64Offset + Memory64HeaderSize + _regions.Count * Memory64DescriptorSize;

        var padding = 0;
        while ((descriptorsEnd + padding) % _baseRvaAlignmentModulus != _baseRvaAlignmentRemainder)
        {
            padding++;
        }

        var baseRva = descriptorsEnd + padding;
        var payloadBytes = _regions.Sum(r => (long)r.Payload.Length);
        var data = new byte[baseRva + payloadBytes];

        // Header
        "MDMP"u8.CopyTo(data);
        data[4] = 0x93;
        data[5] = 0xA7;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)streamCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)directoryOffset);

        // Directory
        var entryOffset = directoryOffset;
        if (_processorArchitecture.HasValue)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset), 7); // SystemInfoStream
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 4), SystemInfoSize);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 8), (uint)systemInfoOffset);
            entryOffset += DirectoryEntrySize;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(systemInfoOffset), _processorArchitecture.Value);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset), 9); // Memory64ListStream
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(entryOffset + 4),
            (uint)(Memory64HeaderSize + _regions.Count * Memory64DescriptorSize));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 8), (uint)memory64Offset);

        // Memory64List: count, BaseRva, then (VA, size) descriptors
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(memory64Offset), (ulong)_regions.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(memory64Offset + 8), (ulong)baseRva);

        var descriptorOffset = memory64Offset + Memory64HeaderSize;
        foreach (var region in _regions)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(descriptorOffset), (ulong)region.VirtualAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(descriptorOffset + 8), (ulong)region.DeclaredSize);
            descriptorOffset += Memory64DescriptorSize;
        }

        // Payloads, sequential from BaseRva in descriptor order
        var payloadOffset = baseRva;
        foreach (var region in _regions)
        {
            region.Payload.CopyTo(data, payloadOffset);
            payloadOffset += region.Payload.Length;
        }

        return data;
    }
}
