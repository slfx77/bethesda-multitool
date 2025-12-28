using System.Buffers;
using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Minidump;

/// <summary>
///     Parses Microsoft Minidump files to extract module information.
///     Reference: https://docs.microsoft.com/en-us/windows/win32/api/minidumpapiset/
/// </summary>
public static class MinidumpParser
{
    private const uint ModuleListStream = 4;
    private const uint SystemInfoStream = 7;
    private const uint Memory64ListStream = 9;

    private static readonly byte[] MinidumpSignature = [0x4D, 0x44, 0x4D, 0x50]; // "MDMP"

    /// <summary>
    ///     Parse a minidump file to extract module information.
    /// </summary>
    public static MinidumpInfo Parse(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(fs);
    }

    /// <summary>
    ///     Parse a minidump from a stream.
    /// </summary>
    public static MinidumpInfo Parse(Stream stream)
    {
        var headerBuffer = new byte[32];
        if (stream.Read(headerBuffer, 0, 32) < 32)
        {
            return new MinidumpInfo { IsValid = false };
        }

        if (!headerBuffer.AsSpan(0, 4).SequenceEqual(MinidumpSignature))
        {
            return new MinidumpInfo { IsValid = false };
        }

        var numberOfStreams = BinaryUtils.ReadUInt32LE(headerBuffer, 8);
        var streamDirectoryRva = BinaryUtils.ReadUInt32LE(headerBuffer, 12);

        if (numberOfStreams == 0 || numberOfStreams > 100 || streamDirectoryRva == 0)
        {
            return new MinidumpInfo { IsValid = false };
        }

        var directorySize = (int)(numberOfStreams * 12);
        var directoryBuffer = ArrayPool<byte>.Shared.Rent(directorySize);
        try
        {
            stream.Seek(streamDirectoryRva, SeekOrigin.Begin);
            if (stream.Read(directoryBuffer, 0, directorySize) < directorySize)
            {
                return new MinidumpInfo { IsValid = false };
            }

            var result = new MinidumpInfo { IsValid = true, NumberOfStreams = numberOfStreams };

            for (var i = 0; i < numberOfStreams; i++)
            {
                var entryOffset = i * 12;
                var streamType = BinaryUtils.ReadUInt32LE(directoryBuffer, entryOffset);
                var dataSize = BinaryUtils.ReadUInt32LE(directoryBuffer, entryOffset + 4);
                var rva = BinaryUtils.ReadUInt32LE(directoryBuffer, entryOffset + 8);

                switch (streamType)
                {
                    case SystemInfoStream: ParseSystemInfo(stream, rva, result); break;
                    case ModuleListStream: ParseModuleList(stream, rva, result); break;
                    case Memory64ListStream: ParseMemory64List(stream, rva, dataSize, result); break;
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(directoryBuffer);
        }
    }

    private static void ParseSystemInfo(Stream stream, uint rva, MinidumpInfo result)
    {
        var buffer = new byte[4];
        stream.Seek(rva, SeekOrigin.Begin);
        if (stream.Read(buffer, 0, 4) >= 4)
        {
            result.ProcessorArchitecture = BinaryUtils.ReadUInt16LE(buffer);
        }
    }

    private static void ParseModuleList(Stream stream, uint rva, MinidumpInfo result)
    {
        stream.Seek(rva, SeekOrigin.Begin);

        var countBuffer = new byte[4];
        if (stream.Read(countBuffer, 0, 4) < 4) return;

        var numberOfModules = BinaryUtils.ReadUInt32LE(countBuffer);
        if (numberOfModules == 0 || numberOfModules > 1000) return;

        const int moduleEntrySize = 108;
        var modulesBuffer = ArrayPool<byte>.Shared.Rent((int)(numberOfModules * moduleEntrySize));

        try
        {
            var bytesToRead = (int)(numberOfModules * moduleEntrySize);
            if (stream.Read(modulesBuffer, 0, bytesToRead) < bytesToRead) return;

            for (var i = 0; i < numberOfModules; i++)
            {
                var offset = i * moduleEntrySize;
                var module = ParseModule(stream, modulesBuffer, offset);
                if (module != null)
                {
                    result.Modules.Add(module);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(modulesBuffer);
        }
    }

#pragma warning disable S1172
    private static void ParseMemory64List(Stream stream, uint rva, uint _, MinidumpInfo result)
#pragma warning restore S1172
    {
        stream.Seek(rva, SeekOrigin.Begin);

        var headerBuffer = new byte[16];
        if (stream.Read(headerBuffer, 0, 16) < 16) return;

        var numberOfRanges = BinaryUtils.ReadUInt64LE(headerBuffer);
        var baseRva = (long)BinaryUtils.ReadUInt64LE(headerBuffer, 8);

        if (numberOfRanges == 0 || numberOfRanges > 10000) return;

        const int descriptorSize = 16;
        var descriptorsSize = (int)(numberOfRanges * descriptorSize);
        var descriptorsBuffer = ArrayPool<byte>.Shared.Rent(descriptorsSize);

        try
        {
            if (stream.Read(descriptorsBuffer, 0, descriptorsSize) < descriptorsSize) return;

            var currentFileOffset = baseRva;

            for (var i = 0; i < (int)numberOfRanges; i++)
            {
                var offset = i * descriptorSize;
                var virtualAddress = (long)BinaryUtils.ReadUInt64LE(descriptorsBuffer, offset);
                var regionSize = (long)BinaryUtils.ReadUInt64LE(descriptorsBuffer, offset + 8);

                result.MemoryRegions.Add(new MinidumpMemoryRegion
                {
                    VirtualAddress = virtualAddress,
                    Size = regionSize,
                    FileOffset = currentFileOffset
                });

                currentFileOffset += regionSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(descriptorsBuffer);
        }
    }

    private static MinidumpModule? ParseModule(Stream stream, byte[] buffer, int offset)
    {
        var baseAddress = (long)BinaryUtils.ReadUInt64LE(buffer, offset);
        var size = (int)BinaryUtils.ReadUInt32LE(buffer, offset + 0x08);
        var checksum = BinaryUtils.ReadUInt32LE(buffer, offset + 0x0C);
        var timestamp = BinaryUtils.ReadUInt32LE(buffer, offset + 0x10);
        var nameRva = BinaryUtils.ReadUInt32LE(buffer, offset + 0x14);

        if (nameRva == 0 || size == 0) return null;

        var name = ReadMinidumpString(stream, nameRva);
        if (string.IsNullOrEmpty(name)) return null;

        return new MinidumpModule
        {
            Name = name,
            BaseAddress = baseAddress,
            Size = size,
            Checksum = checksum,
            TimeDateStamp = timestamp
        };
    }

    private static string? ReadMinidumpString(Stream stream, uint rva)
    {
        var currentPos = stream.Position;
        try
        {
            stream.Seek(rva, SeekOrigin.Begin);

            var lengthBuffer = new byte[4];
            if (stream.Read(lengthBuffer, 0, 4) < 4) return null;

            var length = (int)BinaryUtils.ReadUInt32LE(lengthBuffer);
            if (length == 0 || length > 520) return null;

            var stringBuffer = new byte[length];
            if (stream.Read(stringBuffer, 0, length) < length) return null;

            return Encoding.Unicode.GetString(stringBuffer).TrimEnd('\0');
        }
        finally
        {
            stream.Seek(currentPos, SeekOrigin.Begin);
        }
    }
}
