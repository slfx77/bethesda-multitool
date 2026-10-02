namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>
///     A read-only, seekable <see cref="Stream" /> over a CHD's logical bytes, so the ISO 9660 and
///     XDVDFS readers that walk a <c>.iso</c> through a stream walk a <c>.chd</c> the same way.
///     Decoded hunks are kept in a small most-recently-used cache: a directory walk revisits the
///     same few hunks, a file copy marches forward through fresh ones.
/// </summary>
internal sealed class ChdStream : Stream
{
    private const int CacheHunks = 32;

    private readonly ChdFile _chd;
    private readonly bool _ownsFile;
    private readonly Dictionary<int, byte[]> _cache = new();
    private readonly LinkedList<int> _order = new();
    private readonly Lock _lock = new();
    private long _position;

    public ChdStream(ChdFile chd, bool ownsFile)
    {
        _chd = chd;
        _ownsFile = ownsFile;
    }

    public ChdFile File => _chd;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _chd.LogicalBytes;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            var hunk = (int)(_position / _chd.HunkBytes);
            var within = (int)(_position % _chd.HunkBytes);
            var available = (int)Math.Min(_chd.HunkBytes - within, Length - _position);
            var take = Math.Min(available, buffer.Length);
            lock (_lock)
            {
                Hunk(hunk).AsSpan(within, take).CopyTo(buffer);
            }

            buffer = buffer[take..];
            _position += take;
            total += take;
        }

        return total;
    }

    /// <summary>Positioned read that leaves <see cref="Position" /> alone, for callers that share the stream.</summary>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        lock (_lock)
        {
            var saved = _position;
            _position = offset;
            try
            {
                return ReadUnlocked(buffer);
            }
            finally
            {
                _position = saved;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("Cannot seek before the start of the image.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsFile)
        {
            _chd.Dispose();
        }

        base.Dispose(disposing);
    }

    private int ReadUnlocked(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            var hunk = (int)(_position / _chd.HunkBytes);
            var within = (int)(_position % _chd.HunkBytes);
            var available = (int)Math.Min(_chd.HunkBytes - within, Length - _position);
            var take = Math.Min(available, buffer.Length);
            Hunk(hunk).AsSpan(within, take).CopyTo(buffer);
            buffer = buffer[take..];
            _position += take;
            total += take;
        }

        return total;
    }

    private byte[] Hunk(int index)
    {
        if (_cache.TryGetValue(index, out var cached))
        {
            _order.Remove(index);
            _order.AddFirst(index);
            return cached;
        }

        var buffer = new byte[_chd.HunkBytes];
        _chd.ReadHunk(index, buffer);
        _cache[index] = buffer;
        _order.AddFirst(index);
        if (_order.Count > CacheHunks)
        {
            var evict = _order.Last!.Value;
            _order.RemoveLast();
            _cache.Remove(evict);
        }

        return buffer;
    }
}
