using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Builds synthetic <c>.CLP</c> clumps the way the disc lays them out: page 0 the header and
///     authoring string, sections from page 1 back to back (each padded to the page unit), the
///     hash table at <c>pageCount × unit</c> with every slot placed by the engine's probe chain,
///     the CRC-32 of the table at <c>+12</c>, and the file zero-padded to a 2048-byte sector.
///     <para>
///         The CRC is computed with <c>BosClumpFile.Crc32</c>, which is pinned independently
///         against the zlib check value; the placement uses <c>BosAssetHash.Rehash</c>, pinned
///         against hand-computed values. Nothing else here comes from the code under test.
///     </para>
/// </summary>
internal static class BosClumpFixture
{
    /// <summary>The refuted reading of <c>+12</c>: the hash of the clump's own path.</summary>
    public static uint PathHash(string path)
    {
        return BosAssetHash.Compute(path);
    }

    public static byte[] Build(
        int pageLength,
        IReadOnlyList<Entry> entries,
        string path = "c1/BAR/BAR.clp",
        int? declaredCount = null,
        int pageBias = 0,
        int extraSectors = 0,
        bool crcOfPathInstead = false,
        bool misplaceFirst = false,
        Action<byte[]>? mutateTable = null)
    {
        var starts = new int[entries.Count];
        var page = 1;
        for (var i = 0; i < entries.Count; i++)
        {
            starts[i] = page;
            page += (entries[i].Payload.Length + pageLength - 1) / pageLength;
        }

        var pageCount = page + pageBias;
        var slots = BosAssetHash.SlotCountFor(entries.Count);
        var tableOffset = pageCount * pageLength;
        var tableLength = slots * BosClumpFile.SlotLength;
        var length = (tableOffset + tableLength + BosClumpFile.SectorLength - 1) / BosClumpFile.SectorLength *
                     BosClumpFile.SectorLength
                     + extraSectors * BosClumpFile.SectorLength;
        var b = new byte[length];

        // ⚠ Retail stores 50 4D 4C 43, i.e. a LITTLE-endian dword. Writing it big-endian here would
        // make fixture and reader agree with each other and disagree with the disc.
        BinaryPrimitives.WriteUInt32LittleEndian(b, BosClumpFile.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), pageCount);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), declaredCount ?? entries.Count);
        Encoding.ASCII.GetBytes(path).CopyTo(b.AsSpan(BosClumpFile.StringTableOffset));

        var table = new byte[tableLength];
        var mask = (uint)slots - 1;
        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].Payload.CopyTo(b.AsSpan(starts[i] * pageLength));

            var h = entries[i].Tag;
            var probes = 0;
            while (BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan((int)(h & mask) * BosClumpFile.SlotLength +
                                                                       4)) != 0)
            {
                h = BosAssetHash.Rehash(h);
                probes++;
            }

            var slot = (int)(h & mask);
            if (misplaceFirst && i == 0)
            {
                // Somewhere off the probe chain: the chain visits at most 61 slots, so walk the
                // table for an empty slot the chain never reaches.
                slot = FindSlotOffChain(entries[i].Tag, slots, table);
            }

            var at = slot * BosClumpFile.SlotLength;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), entries[i].Tag);
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(at + 4), starts[i]);
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(at + 12), entries[i].Payload.Length);
        }

        mutateTable?.Invoke(table);
        table.CopyTo(b.AsSpan(tableOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12),
            crcOfPathInstead ? PathHash(path) : BosClumpFile.Crc32(table));
        return b;
    }

    private static int FindSlotOffChain(uint tag, int slots, byte[] table)
    {
        var onChain = new HashSet<int>();
        var h = tag;
        for (var step = 0; step <= BosClumpFile.MaxProbes; step++)
        {
            onChain.Add((int)(h & (uint)(slots - 1)));
            h = BosAssetHash.Rehash(h);
        }

        for (var slot = 0; slot < slots; slot++)
        {
            if (!onChain.Contains(slot) &&
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(slot * BosClumpFile.SlotLength + 4)) == 0)
            {
                return slot;
            }
        }

        throw new InvalidOperationException("The table has no empty slot off the probe chain; use more slots.");
    }

    /// <summary>One section to lay out: its tag and payload.</summary>
    internal readonly record struct Entry(uint Tag, byte[] Payload);
}