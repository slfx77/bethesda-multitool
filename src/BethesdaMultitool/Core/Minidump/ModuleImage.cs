using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Minidump;

/// <summary>
///     The captured bytes of the game module, coalesced into virtual-address-contiguous runs.
///     <para>
///         RTTI tables live entirely inside the module image, so walking them forward — type
///         descriptor strings, then the complete object locators that point at them, then the
///         vtables that point at those — only ever needs these ~20 MB rather than the whole
///         220 MB dump. Materializing the runs once turns three whole-image passes into array
///         scans instead of hundreds of thousands of region lookups.
///     </para>
///     <para>
///         The image is normally short-lived: build it, derive the RTTI index, drop it.
///     </para>
/// </summary>
internal sealed class ModuleImage
{
    private ModuleImage(Run[] runs, uint moduleStart, uint moduleEnd, long declaredSize)
    {
        Runs = runs;
        ModuleStart = moduleStart;
        ModuleEnd = moduleEnd;
        BytesDeclared = declaredSize;
        BytesCaptured = runs.Sum(r => (long)r.Bytes.Length);
    }

    /// <summary>VA-contiguous captured runs, ordered by start address.</summary>
    internal Run[] Runs { get; }

    internal uint ModuleStart { get; }

    internal uint ModuleEnd { get; }

    /// <summary>
    ///     Bytes actually present in the dump. Less than <see cref="BytesDeclared" /> whenever
    ///     the capture truncated part of the image — measured at ~88.7% on the corpus dumps, with the
    ///     shortfall entirely in the image tail.
    /// </summary>
    internal long BytesCaptured { get; }

    internal long BytesDeclared { get; }

    /// <summary>
    ///     Materialize the game module's captured bytes, or null when the dump names no game module
    ///     or none of it was captured.
    /// </summary>
    internal static ModuleImage? Build(MinidumpInfo info, IMemoryAccessor accessor, long fileSize)
    {
        var module = MinidumpAnalyzer.FindGameModule(info);
        if (module is null || module.Size <= 0)
        {
            return null;
        }

        var moduleStart = module.BaseAddress32;
        var moduleEnd = moduleStart + (uint)module.Size;
        if (moduleEnd <= moduleStart)
        {
            return null; // declared size wraps the address space; not a module we can walk
        }

        var slices = new List<(uint StartVa, long FileOffset, int Length)>();
        foreach (var region in info.MemoryRegions)
        {
            if (region.Size <= 0)
            {
                continue;
            }

            // Module-space VAs are stored sign-extended; compare in 32-bit space.
            var regionStart = unchecked((uint)region.VirtualAddress);
            var regionEnd = regionStart + (uint)region.Size;
            if (regionEnd <= regionStart)
            {
                continue;
            }

            var startVa = Math.Max(regionStart, moduleStart);
            var endVa = Math.Min(regionEnd, moduleEnd);
            if (endVa <= startVa)
            {
                continue;
            }

            var fileOffset = region.FileOffset + (startVa - regionStart);
            var length = (int)(endVa - startVa);
            if (fileOffset < 0 || fileOffset > fileSize - length)
            {
                continue; // truncated capture: the descriptor claims bytes the file does not hold
            }

            slices.Add((startVa, fileOffset, length));
        }

        if (slices.Count == 0)
        {
            return null;
        }

        slices.Sort((left, right) => left.StartVa.CompareTo(right.StartVa));

        var runs = new List<Run>();
        var groupStart = 0;
        for (var i = 1; i <= slices.Count; i++)
        {
            var contiguous = i < slices.Count
                             && slices[i].StartVa == slices[i - 1].StartVa + (uint)slices[i - 1].Length;
            if (contiguous)
            {
                continue;
            }

            var totalLength = 0L;
            for (var j = groupStart; j < i; j++)
            {
                totalLength += slices[j].Length;
            }

            var bytes = new byte[totalLength];
            var written = 0;
            var complete = true;
            for (var j = groupStart; j < i; j++)
            {
                var read = accessor.ReadArray(slices[j].FileOffset, bytes, written, slices[j].Length);
                if (read != slices[j].Length)
                {
                    complete = false;
                    break;
                }

                written += read;
            }

            if (complete)
            {
                runs.Add(new Run(slices[groupStart].StartVa, bytes));
            }

            groupStart = i;
        }

        return runs.Count == 0 ? null : new ModuleImage([.. runs], moduleStart, moduleEnd, module.Size);
    }

    /// <summary>
    ///     Read a big-endian uint32 at <paramref name="va" />. Returns false for any address outside
    ///     a captured run — module-range-but-uncaptured is a drop, never a flat read, so a type
    ///     descriptor or locator that landed in the truncated image tail simply fails to resolve.
    /// </summary>
    internal bool TryReadUInt32(uint va, out uint value)
    {
        foreach (var run in Runs)
        {
            if (va < run.StartVa)
            {
                break; // runs are ordered, so nothing later can contain it
            }

            var offset = va - run.StartVa;
            if (offset + 4 <= (uint)run.Bytes.Length)
            {
                value = BinaryUtils.ReadUInt32BE(run.Bytes, (int)offset);
                return true;
            }
        }

        value = 0;
        return false;
    }

    internal bool Contains(uint va)
    {
        foreach (var run in Runs)
        {
            if (va >= run.StartVa && va - run.StartVa < (uint)run.Bytes.Length)
            {
                return true;
            }
        }

        return false;
    }

    internal readonly record struct Run(uint StartVa, byte[] Bytes);
}
