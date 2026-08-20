using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace BethesdaMultitool.Core.Formats.Esm.Land.Btd;

/// <summary>
///     Random-access byte source behind <see cref="BtdFile" />. A <c>.btd</c> reaches us two ways:
///     as a loose file on disk (Fallout 76 ships <c>Data\Terrain\Appalachia.btd</c> at 1.48 GB, which
///     must stay memory-mapped rather than copied into the heap) and as an archive entry (Starfield
///     ships every <c>terrain\*.btd</c> inside <c>Starfield - Terrain*.ba2</c>, and the VFS hands those
///     back as bytes). One interface keeps a single decode implementation for both.
///     <para>
///         Dispatch cost is negligible: the decoder only reaches for the source when reading the header
///         / offset chain and when pulling one compressed block per tile. Every per-sample loop
///         (<c>LoadBlockLines16</c> and friends) runs over the already-decompressed tile arrays.
///     </para>
/// </summary>
internal interface IBtdBytes : IDisposable
{
    /// <summary>Total length of the BTD payload in bytes.</summary>
    long Length { get; }

    byte ReadU8(long offset);
    ushort ReadU16(long offset);
    uint ReadU32(long offset);
    int ReadI32(long offset);
    float ReadF32(long offset);

    /// <summary>Copies <paramref name="count" /> bytes at <paramref name="offset" /> into <paramref name="destination" />.</summary>
    void ReadArray(long offset, byte[] destination, int destinationIndex, int count);
}

/// <summary>
///     Memory-mapped view over a loose <c>.btd</c> file — the zero-copy path, so a multi-gigabyte
///     Fallout 76 terrain file never materializes on the managed heap.
/// </summary>
internal sealed class MappedBtdBytes : IBtdBytes
{
    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _view;
    private bool _disposed;

    public MappedBtdBytes(string fileName)
    {
        Length = new FileInfo(fileName).Length;
        _mappedFile = MemoryMappedFile.CreateFromFile(fileName, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public long Length { get; }

    public byte ReadU8(long offset)
    {
        return _view.ReadByte(offset);
    }

    public ushort ReadU16(long offset)
    {
        return _view.ReadUInt16(offset);
    }

    public uint ReadU32(long offset)
    {
        return _view.ReadUInt32(offset);
    }

    public int ReadI32(long offset)
    {
        return _view.ReadInt32(offset);
    }

    public float ReadF32(long offset)
    {
        return _view.ReadSingle(offset);
    }

    public void ReadArray(long offset, byte[] destination, int destinationIndex, int count)
    {
        _view.ReadArray(offset, destination, destinationIndex, count);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _view.Dispose();
        _mappedFile.Dispose();
        _disposed = true;
    }
}

/// <summary>
///     In-memory source for a <c>.btd</c> pulled out of an archive. Reads go through
///     <see cref="BinaryPrimitives" /> little-endian helpers, which also pins down the format's
///     endianness explicitly — <see cref="MemoryMappedViewAccessor" /> reads host-endian and only
///     happens to be right on the little-endian platforms this ships on.
/// </summary>
internal sealed class ArrayBtdBytes(byte[] data) : IBtdBytes
{
    private readonly byte[] _data = data;

    public long Length => _data.Length;

    public byte ReadU8(long offset)
    {
        return _data[checked((int)offset)];
    }

    public ushort ReadU16(long offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
    }

    public uint ReadU32(long offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
    }

    public int ReadI32(long offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(Slice(offset, 4));
    }

    public float ReadF32(long offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(Slice(offset, 4));
    }

    public void ReadArray(long offset, byte[] destination, int destinationIndex, int count)
    {
        Buffer.BlockCopy(_data, checked((int)offset), destination, destinationIndex, count);
    }

    public void Dispose()
    {
        // Nothing to release: the array is owned by the caller / the GC.
    }

    private ReadOnlySpan<byte> Slice(long offset, int count)
    {
        return _data.AsSpan(checked((int)offset), count);
    }
}
