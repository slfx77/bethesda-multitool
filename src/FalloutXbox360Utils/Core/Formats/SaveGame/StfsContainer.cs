using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Parser for Xbox 360 STFS (Secure Transacted File System) containers.
///     Handles CON/LIVE/PIRS packages wrapping save files (.fxs).
///
///     References:
///     - Xenia: src/xenia/vfs/devices/stfs_container_device.cc
///     - Velocity: XboxInternals/Stfs/StfsPackage.cpp
///     - Free60: https://free60.org/System-Software/Formats/STFS/
/// </summary>
internal static class StfsContainer
{
    // STFS constants
    private const int BlockSize = 0x1000;
    private const int BlocksPerHashLevel = 170; // 0xAA
    private const int HeaderSize = 0xA000; // Male package (block_sep=0)
    private const int InitialHashBlocks = 2; // L0 + L1 before first data block

    // Hash block counts at group boundaries (empirically verified with 7 working saves)
    private const int FirstGroupHashBlocks = 4;
    private const int SubsequentGroupHashBlocks = 2;

    // STFS header offsets (big-endian)
    private const int OffsetContentType = 0x344;
    private const int OffsetMetadataVersion = 0x348;
    private const int OffsetVolumeDescriptor = 0x379;

    // Volume descriptor field offsets (relative to 0x379)
    private const int VdBlockSeparation = 2;
    private const int VdFileTableBlockCount = 3; // 2 bytes LE
    private const int VdFileTableBlockNumber = 5; // 3 bytes LE
    private const int VdTotalAllocated = 28; // 4 bytes BE

    // File table entry (0x40 bytes)
    private const int FileEntrySize = 0x40;
    private const int FileEntryNameLength = 0x28;
    private const int FileEntryFlags = 0x28;
    private const int FileEntryValidBlocks = 0x29; // 3 bytes LE
    private const int FileEntryAllocatedBlocks = 0x2C; // 3 bytes LE
    private const int FileEntryStartBlock = 0x2F; // 3 bytes LE
    private const int FileEntryFileSize = 0x34; // 4 bytes BE

    private static readonly byte[] FO3SavegameMagic = "FO3SAVEGAME"u8.ToArray();
    private static readonly byte[] SavegameDatName = "Savegame.dat"u8.ToArray();

    /// <summary>
    ///     Try to extract the FO3SAVEGAME payload from an STFS container.
    ///     Returns an extraction result with diagnostics even on failure.
    /// </summary>
    public static StfsExtractionResult TryExtract(ReadOnlySpan<byte> data)
    {
        var diagnostics = new List<string>();

        // Check for CON/LIVE/PIRS magic
        if (data.Length < HeaderSize)
        {
            return StfsExtractionResult.Fail("File too small for STFS container", diagnostics);
        }

        string magic = Encoding.ASCII.GetString(data[..4]);
        if (magic is not ("CON " or "LIVE" or "PIRS"))
        {
            return StfsExtractionResult.Fail($"Not an STFS container (magic: {magic})", diagnostics);
        }

        diagnostics.Add($"STFS magic: {magic.Trim()}");

        // Parse header info
        var header = ParseHeader(data, diagnostics);

        // Try primary extraction path: file table at expected block
        var fileEntry = TryReadFileTable(data, header, diagnostics);
        if (fileEntry != null)
        {
            var payload = ExtractFilePayload(data, fileEntry, diagnostics);
            if (payload != null && payload.Length >= FO3SavegameMagic.Length &&
                payload.AsSpan(0, FO3SavegameMagic.Length).SequenceEqual(FO3SavegameMagic))
            {
                diagnostics.Add("FO3SAVEGAME magic confirmed at payload byte 0");
                return new StfsExtractionResult(payload, header, fileEntry, diagnostics, StfsExtractionMethod.Standard);
            }

            if (payload != null)
            {
                diagnostics.Add($"Extracted {payload.Length} bytes but FO3SAVEGAME magic not at start");
            }
        }

        // Recovery: scan all blocks for a valid file table entry
        diagnostics.Add("Primary extraction failed, attempting recovery scan...");
        var recoveredEntry = ScanForFileTableEntry(data, diagnostics);
        if (recoveredEntry != null)
        {
            var payload = ExtractFilePayload(data, recoveredEntry, diagnostics);
            if (payload != null && payload.Length >= FO3SavegameMagic.Length &&
                payload.AsSpan(0, FO3SavegameMagic.Length).SequenceEqual(FO3SavegameMagic))
            {
                diagnostics.Add($"Recovery: found valid Savegame.dat via block scan");
                return new StfsExtractionResult(payload, header, recoveredEntry, diagnostics,
                    StfsExtractionMethod.RecoveryScan);
            }
        }

        // Last resort: brute-force scan for FO3SAVEGAME magic
        diagnostics.Add("Recovery scan failed, attempting brute-force magic search...");
        var brutePayload = BruteForceMagicScan(data, diagnostics);
        if (brutePayload != null)
        {
            return new StfsExtractionResult(brutePayload, header, null, diagnostics,
                StfsExtractionMethod.BruteForce);
        }

        return StfsExtractionResult.Fail("All extraction methods failed — data blocks appear corrupted", diagnostics,
            header);
    }

    /// <summary>
    ///     Parse STFS header info from the container.
    /// </summary>
    public static StfsHeaderInfo ParseHeader(ReadOnlySpan<byte> data, List<string>? diagnostics = null)
    {
        string magic = Encoding.ASCII.GetString(data[..4]);
        uint contentType = BinaryUtils.ReadUInt32BE(data, OffsetContentType);
        uint metadataVersion = BinaryUtils.ReadUInt32BE(data, OffsetMetadataVersion);

        byte blockSep = data[OffsetVolumeDescriptor + VdBlockSeparation];
        int ftBlockCount = BinaryUtils.ReadUInt16LE(data, OffsetVolumeDescriptor + VdFileTableBlockCount);
        int ftBlockNumber = ReadInt24LE(data, OffsetVolumeDescriptor + VdFileTableBlockNumber);
        int totalAllocated = (int)BinaryUtils.ReadUInt32BE(data, OffsetVolumeDescriptor + VdTotalAllocated);
        int totalUnallocated = OffsetVolumeDescriptor + VdTotalAllocated + 4 + 4 <= data.Length
            ? (int)BinaryUtils.ReadUInt32BE(data, OffsetVolumeDescriptor + VdTotalAllocated + 4)
            : 0;

        diagnostics?.Add(
            $"Volume descriptor: block_sep={blockSep}, ft_blocks={ftBlockCount}, ft_start_block={ftBlockNumber}, " +
            $"allocated={totalAllocated}, unallocated={totalUnallocated}");

        return new StfsHeaderInfo(
            magic,
            contentType,
            metadataVersion,
            blockSep,
            ftBlockCount,
            ftBlockNumber,
            totalAllocated,
            totalUnallocated);
    }

    /// <summary>
    ///     Convert an STFS data block number to its raw file offset.
    ///     Accounts for hash blocks interleaved between data block groups.
    /// </summary>
    public static int DataBlockToRawOffset(int blockNum)
    {
        int area = blockNum / BlocksPerHashLevel;
        int hashAdj = area switch
        {
            0 => InitialHashBlocks,
            1 => InitialHashBlocks + FirstGroupHashBlocks,
            _ => InitialHashBlocks + FirstGroupHashBlocks + SubsequentGroupHashBlocks * (area - 1)
        };
        return HeaderSize + (blockNum + hashAdj) * BlockSize;
    }

    /// <summary>
    ///     Convert a raw file offset to an STFS data block number.
    ///     Returns -1 if the offset corresponds to a hash block or header area.
    /// </summary>
    public static int RawOffsetToDataBlock(int rawOffset)
    {
        if (rawOffset < HeaderSize)
        {
            return -1;
        }

        int backing = (rawOffset - HeaderSize) / BlockSize;

        for (int area = 0; area < 100; area++)
        {
            int hashAdj = area switch
            {
                0 => InitialHashBlocks,
                1 => InitialHashBlocks + FirstGroupHashBlocks,
                _ => InitialHashBlocks + FirstGroupHashBlocks + SubsequentGroupHashBlocks * (area - 1)
            };

            int block = backing - hashAdj;
            if (block >= area * BlocksPerHashLevel && block < (area + 1) * BlocksPerHashLevel)
            {
                return block;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Try to read the file table at the expected block and find "Savegame.dat".
    /// </summary>
    private static StfsFileEntry? TryReadFileTable(ReadOnlySpan<byte> data, StfsHeaderInfo header,
        List<string> diagnostics)
    {
        int ftOffset = DataBlockToRawOffset(header.FileTableBlockNumber);
        if (ftOffset + BlockSize > data.Length)
        {
            diagnostics.Add(
                $"File table block {header.FileTableBlockNumber} at offset 0x{ftOffset:X} extends beyond file");
            return null;
        }

        diagnostics.Add($"File table block at offset 0x{ftOffset:X}");

        // Read entries from the file table block(s)
        int entriesPerBlock = BlockSize / FileEntrySize;
        int totalEntries = header.FileTableBlockCount * entriesPerBlock;

        for (int i = 0; i < totalEntries; i++)
        {
            int blockIndex = i / entriesPerBlock;
            int entryIndex = i % entriesPerBlock;
            int blockOffset = blockIndex == 0
                ? ftOffset
                : DataBlockToRawOffset(header.FileTableBlockNumber + blockIndex);
            int entryOffset = blockOffset + entryIndex * FileEntrySize;

            if (entryOffset + FileEntrySize > data.Length)
            {
                break;
            }

            var entry = TryParseFileEntry(data, entryOffset);
            if (entry != null)
            {
                diagnostics.Add(
                    $"Found: {entry.Filename} — {entry.ValidBlocks} blocks, {entry.FileSize:N0} bytes, " +
                    $"start_block={entry.StartBlock}, consecutive={entry.IsConsecutive}");
                if (entry.Filename.Equals("Savegame.dat", StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        diagnostics.Add("No 'Savegame.dat' entry found in file table");
        return null;
    }

    /// <summary>
    ///     Try to parse an STFS file table entry at the given offset.
    ///     Returns null if the entry appears invalid (empty filename, etc.).
    /// </summary>
    private static StfsFileEntry? TryParseFileEntry(ReadOnlySpan<byte> data, int offset)
    {
        // Read filename (null-terminated, padded with zeros)
        var nameBytes = data.Slice(offset, FileEntryNameLength);
        int nameEnd = nameBytes.IndexOf((byte)0);
        if (nameEnd <= 0)
        {
            return null; // Empty or all-zero name
        }

        string filename = Encoding.ASCII.GetString(nameBytes[..nameEnd]);

        // Validate filename contains only printable ASCII
        if (!filename.All(c => c >= 0x20 && c < 0x7F))
        {
            return null;
        }

        byte flags = data[offset + FileEntryFlags];
        bool isConsecutive = (flags & 0x40) != 0;

        int validBlocks = ReadInt24LE(data, offset + FileEntryValidBlocks);
        int allocatedBlocks = ReadInt24LE(data, offset + FileEntryAllocatedBlocks);
        int startBlock = ReadInt24LE(data, offset + FileEntryStartBlock);
        int fileSize = (int)BinaryUtils.ReadUInt32BE(data, offset + FileEntryFileSize);

        // Basic sanity checks
        if (fileSize <= 0 || startBlock < 0 || validBlocks < 0)
        {
            return null;
        }

        return new StfsFileEntry(filename, isConsecutive, validBlocks, allocatedBlocks, startBlock, fileSize);
    }

    /// <summary>
    ///     Extract file data using the file entry's block chain.
    /// </summary>
    private static byte[]? ExtractFilePayload(ReadOnlySpan<byte> data, StfsFileEntry entry, List<string> diagnostics)
    {
        if (entry.FileSize <= 0 || entry.StartBlock < 0)
        {
            diagnostics.Add($"Invalid file entry: size={entry.FileSize}, start_block={entry.StartBlock}");
            return null;
        }

        try
        {
            using var ms = new MemoryStream(entry.FileSize);

            if (entry.IsConsecutive)
            {
                // Read blocks sequentially
                int remaining = entry.FileSize;
                int block = entry.StartBlock;

                while (remaining > 0)
                {
                    int rawOffset = DataBlockToRawOffset(block);
                    if (rawOffset + BlockSize > data.Length)
                    {
                        // Read partial last block
                        int available = data.Length - rawOffset;
                        if (available > 0)
                        {
                            int toRead = Math.Min(remaining, available);
                            ms.Write(data.Slice(rawOffset, toRead));
                        }

                        break;
                    }

                    int bytesToRead = Math.Min(remaining, BlockSize);
                    ms.Write(data.Slice(rawOffset, bytesToRead));
                    remaining -= bytesToRead;
                    block++;
                }
            }
            else
            {
                // Follow hash chain (non-consecutive allocation)
                diagnostics.Add("Following hash chain for non-consecutive allocation");
                int remaining = entry.FileSize;
                int block = entry.StartBlock;

                while (remaining > 0 && block >= 0 && block < 0xFFFFFF)
                {
                    int rawOffset = DataBlockToRawOffset(block);
                    if (rawOffset + BlockSize > data.Length)
                    {
                        break;
                    }

                    int bytesToRead = Math.Min(remaining, BlockSize);
                    ms.Write(data.Slice(rawOffset, bytesToRead));
                    remaining -= bytesToRead;

                    // Read next block from L0 hash table
                    block = GetNextBlock(data, block);
                }
            }

            var result = ms.ToArray();
            diagnostics.Add($"Extracted {result.Length:N0} bytes (expected {entry.FileSize:N0})");
            return result;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Extraction error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Get the next block number from the L0 hash table entry for a given block.
    ///     Each L0 hash entry is 0x18 bytes: 20-byte SHA1 hash + 1-byte status + 3-byte next block.
    /// </summary>
    private static int GetNextBlock(ReadOnlySpan<byte> data, int currentBlock)
    {
        // Find which L0 hash table covers this block
        int l0Group = currentBlock / BlocksPerHashLevel;
        int l0Entry = currentBlock % BlocksPerHashLevel;

        // L0 hash table position: the hash block just before this group's data blocks
        int l0RawOffset;
        if (l0Group == 0)
        {
            l0RawOffset = HeaderSize; // First L0 at 0xA000
        }
        else
        {
            // L0 hash table is at the block boundary between groups
            // It's the hash block just before the group's data blocks
            int firstDataBlock = l0Group * BlocksPerHashLevel;
            l0RawOffset = DataBlockToRawOffset(firstDataBlock) - BlockSize;
        }

        // Each hash entry: 20 bytes SHA1 + 1 byte status + 3 bytes next block (BE)
        int entryOffset = l0RawOffset + l0Entry * 0x18;
        int nextBlockOffset = entryOffset + 0x14 + 1; // Skip hash + status

        if (nextBlockOffset + 3 > data.Length)
        {
            return -1;
        }

        // 3-byte big-endian next block pointer
        int nextBlock = (data[nextBlockOffset] << 16) | (data[nextBlockOffset + 1] << 8) | data[nextBlockOffset + 2];
        return nextBlock == 0xFFFFFF ? -1 : nextBlock; // 0xFFFFFF = end of chain
    }

    /// <summary>
    ///     Recovery: scan every 0x1000-aligned block in the file for a valid file table entry
    ///     containing "Savegame.dat".
    /// </summary>
    private static StfsFileEntry? ScanForFileTableEntry(ReadOnlySpan<byte> data, List<string> diagnostics)
    {
        for (int offset = HeaderSize; offset + FileEntrySize <= data.Length; offset += BlockSize)
        {
            // Quick check: does this block start with "Savegame.dat"?
            if (offset + SavegameDatName.Length <= data.Length &&
                data.Slice(offset, SavegameDatName.Length).SequenceEqual(SavegameDatName))
            {
                var entry = TryParseFileEntry(data, offset);
                if (entry != null && entry.FileSize > 0 && entry.StartBlock >= 0)
                {
                    diagnostics.Add(
                        $"Recovery scan: found file table entry at offset 0x{offset:X} — " +
                        $"{entry.Filename}, {entry.FileSize:N0} bytes, start_block={entry.StartBlock}");
                    return entry;
                }
            }
        }

        diagnostics.Add("Recovery scan: no valid 'Savegame.dat' file table entry found in any block");
        return null;
    }

    /// <summary>
    ///     Last resort: brute-force scan for FO3SAVEGAME magic at any 0x1000-aligned position.
    ///     Extracts payload using sequential block reading from that point.
    /// </summary>
    private static byte[]? BruteForceMagicScan(ReadOnlySpan<byte> data, List<string> diagnostics)
    {
        // First try block-aligned positions
        for (int offset = BlockSize; offset < data.Length - FO3SavegameMagic.Length; offset += BlockSize)
        {
            if (data.Slice(offset, FO3SavegameMagic.Length).SequenceEqual(FO3SavegameMagic))
            {
                diagnostics.Add($"Brute-force: found FO3SAVEGAME at offset 0x{offset:X}");
                return ExtractFromMagicOffset(data, offset, diagnostics);
            }
        }

        // Then try unaligned (very last resort)
        int unaligned = data.IndexOf(FO3SavegameMagic.AsSpan());
        if (unaligned > 0)
        {
            diagnostics.Add($"Brute-force: found FO3SAVEGAME at unaligned offset 0x{unaligned:X}");
            return ExtractFromMagicOffset(data, unaligned, diagnostics);
        }

        diagnostics.Add("Brute-force: FO3SAVEGAME magic not found anywhere in file");
        return null;
    }

    /// <summary>
    ///     Extract payload from a raw offset where FO3SAVEGAME magic was found.
    ///     Tries block-aware extraction first, falls back to contiguous read.
    /// </summary>
    private static byte[] ExtractFromMagicOffset(ReadOnlySpan<byte> data, int magicOffset, List<string> diagnostics)
    {
        int startBlock = RawOffsetToDataBlock(magicOffset);
        if (startBlock < 0)
        {
            // Magic is not in a recognized data block — just read contiguously
            diagnostics.Add("Magic not in STFS data block area; using contiguous read");
            return data[magicOffset..].ToArray();
        }

        int expectedRaw = DataBlockToRawOffset(startBlock);
        int blockInternalOffset = magicOffset - expectedRaw;

        using var ms = new MemoryStream();
        int block = startBlock;
        while (true)
        {
            int rawOff = DataBlockToRawOffset(block);
            if (rawOff + BlockSize > data.Length)
            {
                if (rawOff < data.Length)
                {
                    ms.Write(data[rawOff..]);
                }

                break;
            }

            ms.Write(data.Slice(rawOff, BlockSize));
            block++;
        }

        var result = ms.ToArray();

        if (blockInternalOffset > 0 && blockInternalOffset < result.Length)
        {
            return result[blockInternalOffset..];
        }

        return result;
    }

    /// <summary>
    ///     Read a 24-bit little-endian integer from 3 bytes.
    /// </summary>
    private static int ReadInt24LE(ReadOnlySpan<byte> data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
    }
}

/// <summary>
///     STFS header information parsed from the container.
/// </summary>
internal sealed record StfsHeaderInfo(
    string Magic,
    uint ContentType,
    uint MetadataVersion,
    byte BlockSeparation,
    int FileTableBlockCount,
    int FileTableBlockNumber,
    int TotalAllocatedBlocks,
    int TotalUnallocatedBlocks)
{
    /// <summary>Whether the STFS header appears valid for a save game.</summary>
    public bool IsValidSaveGame => Magic.StartsWith("CON", StringComparison.Ordinal) || Magic.StartsWith("LIVE", StringComparison.Ordinal) || Magic.StartsWith("PIRS", StringComparison.Ordinal);
}

/// <summary>
///     STFS file table entry for a file within the container.
/// </summary>
internal sealed record StfsFileEntry(
    string Filename,
    bool IsConsecutive,
    int ValidBlocks,
    int AllocatedBlocks,
    int StartBlock,
    int FileSize);

/// <summary>
///     How the payload was extracted from the STFS container.
/// </summary>
internal enum StfsExtractionMethod
{
    /// <summary>Standard STFS file table extraction.</summary>
    Standard,

    /// <summary>Found via scanning blocks for file table entry.</summary>
    RecoveryScan,

    /// <summary>Found via brute-force magic string search.</summary>
    BruteForce,

    /// <summary>All extraction methods failed.</summary>
    Failed
}

/// <summary>
///     Result of attempting to extract save data from an STFS container.
///     Always includes diagnostics; payload is null on failure.
/// </summary>
internal sealed class StfsExtractionResult
{
    public byte[]? Payload { get; }
    public StfsHeaderInfo? Header { get; }
    public StfsFileEntry? FileEntry { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public StfsExtractionMethod Method { get; }

    /// <summary>Whether extraction succeeded (payload is available).</summary>
    public bool Success => Payload != null;

    /// <summary>Human-readable summary for error messages.</summary>
    public string DiagnosticSummary => string.Join("; ", Diagnostics);

    public StfsExtractionResult(byte[]? payload, StfsHeaderInfo? header, StfsFileEntry? fileEntry,
        IReadOnlyList<string> diagnostics, StfsExtractionMethod method)
    {
        Payload = payload;
        Header = header;
        FileEntry = fileEntry;
        Diagnostics = diagnostics;
        Method = method;
    }

    public static StfsExtractionResult Fail(string reason, List<string> diagnostics,
        StfsHeaderInfo? header = null)
    {
        diagnostics.Add(reason);
        return new StfsExtractionResult(null, header, null, diagnostics, StfsExtractionMethod.Failed);
    }
}
