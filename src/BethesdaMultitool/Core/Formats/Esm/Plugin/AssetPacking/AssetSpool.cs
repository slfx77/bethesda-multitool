using BethesdaMultitool.Core.Formats.Bsa;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>One private, delete-on-close file replaces retained converted payload arrays.</summary>
internal sealed class AssetSpool : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _gate = new();
    private bool _sealed;
    internal string Path { get; }

    internal AssetSpool()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bmt-assets-{Guid.NewGuid():N}.tmp");
        _stream = new FileStream(Path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, 65536, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    internal PackedAsset Append(string path, byte[] data, CancellationToken token)
    {
        lock (_gate)
        {
            if (_sealed) throw new InvalidOperationException("Asset spool is sealed.");
            token.ThrowIfCancellationRequested();
            var offset = _stream.Position;
            using var input = new MemoryStream(data, writable: false);
            BsaWriter.CopyExactly(input, _stream, data.LongLength, token);
            return new PackedAsset(path, data.LongLength, Path, offset);
        }
    }

    internal void Seal()
    {
        lock (_gate) { _stream.Flush(); _sealed = true; }
    }

    public void Dispose() => _stream.Dispose();
}
