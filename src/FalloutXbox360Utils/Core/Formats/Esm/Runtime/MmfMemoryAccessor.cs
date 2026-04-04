using System.IO.MemoryMappedFiles;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Default <see cref="IMemoryAccessor" /> backed by a <see cref="MemoryMappedViewAccessor" />.
/// </summary>
public sealed class MmfMemoryAccessor(MemoryMappedViewAccessor accessor) : IMemoryAccessor
{
    public int ReadArray(long position, byte[] array, int offset, int count)
    {
        return accessor.ReadArray(position, array, offset, count);
    }
}
