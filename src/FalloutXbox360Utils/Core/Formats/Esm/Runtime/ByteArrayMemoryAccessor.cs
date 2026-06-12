namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     <see cref="IMemoryAccessor" /> backed by an in-memory byte array.
/// </summary>
public sealed class ByteArrayMemoryAccessor(byte[] data) : IMemoryAccessor
{
    public int ReadArray(long position, byte[] array, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(array);

        if (position < 0 || position > data.LongLength)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (offset < 0 || count < 0 || offset + count > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var available = (int)Math.Min(count, data.LongLength - position);
        if (available <= 0)
        {
            return 0;
        }

        Buffer.BlockCopy(data, checked((int)position), array, offset, available);
        return available;
    }
}
