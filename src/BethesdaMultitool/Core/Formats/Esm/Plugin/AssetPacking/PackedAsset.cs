using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Bsa;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>A packaged asset backed by a bounded spool region; byte inputs remain available for small callers.</summary>
internal sealed record PackedAsset(string Path, long Length, string? SourcePath, long SourceOffset, byte[]? Data = null)
{
    internal static PackedAsset FromBytes(string path, byte[] data) => new(path, data.LongLength, null, 0, data);

    /// <summary>Checks writer-normalized paths before any output is created; only identical bytes coalesce.</summary>
    internal static List<PackedAsset> CoalesceOutputPaths(IEnumerable<PackedAsset> files, CancellationToken token)
    {
        var result = new List<PackedAsset>();
        foreach (var group in files.GroupBy(f => f.Path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant())
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var copies = group.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
            var first = copies[0];
            if (copies.Length > 1)
            {
                var firstHash = first.HashPayload(token);
                foreach (var copy in copies.Skip(1))
                    if (copy.Length != first.Length || !copy.HashPayload(token).AsSpan().SequenceEqual(firstHash))
                        throw new InvalidDataException($"Conflicting asset payloads target '{group.Key}' ({copies.Length} prepared assets).");
            }
            result.Add(first);
        }
        return result;
    }

    private byte[] HashPayload(CancellationToken token)
    {
        using var hash = SHA256.Create();
        using var output = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        CopyTo(output, token);
        output.FlushFinalBlock();
        return hash.Hash!;
    }

    internal void AddTo(BsaWriter writer)
    {
        if (Data is not null) writer.AddFile(Path, Data);
        else writer.AddFileFromDisk(Path, SourcePath!, SourceOffset, Length);
    }

    internal void CopyTo(Stream output, CancellationToken token)
    {
        using var input = Data is not null ? (Stream)new MemoryStream(Data, writable: false)
            : new FileStream(SourcePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                65536, FileOptions.SequentialScan);
        input.Position = SourceOffset;
        BsaWriter.CopyExactly(input, output, Length, token);
    }
}
