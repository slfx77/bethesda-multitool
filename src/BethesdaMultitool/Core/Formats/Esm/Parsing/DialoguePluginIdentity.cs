using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>Resolves original plugin ownership from a complete Fallout TES4 master table.</summary>
public static class DialoguePluginIdentity
{
    /// <summary>Resolves plugin-local FormID ownership only from a complete Fallout plugin header.</summary>
    /// <param name="path">The original FO3/FNV plugin path, never a reconstructed dump or allocated output identity.</param>
    /// <param name="formId">The original plugin-local record identity.</param>
    /// <returns>The declared master or owning plugin filename; null for incomplete headers or invalid indices.</returns>
    /// <exception cref="IOException">The source cannot be opened or its header changes while being read.</exception>
    public static string? ResolveOwner(string path, uint formId)
    {
        var owners = ReadOwners(path);
        var index = formId >> 24;
        return owners != null && index < owners.Count ? owners[(int)index] : null;
    }

    /// <summary>Reads the master slots once for joining a complete plugin's voice identities.</summary>
    /// <param name="path">The original Fallout plugin path.</param>
    /// <returns>The complete declared master slots followed by the source plugin, or null.</returns>
    public static IReadOnlyList<string>? ReadOwners(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 24) return null;
        var prefix = new byte[24];
        stream.ReadExactly(prefix);
        var little = prefix.AsSpan(0, 4).SequenceEqual("TES4"u8);
        var big = prefix.AsSpan(0, 4).SequenceEqual("4SET"u8);
        if (!little && !big) return null;
        var size = big ? BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));
        if (size > 4 * 1024 * 1024 || size > stream.Length - 24) return null;
        var bytes = new byte[24 + checked((int)size)];
        prefix.CopyTo(bytes, 0);
        stream.ReadExactly(bytes.AsSpan(24));
        var header = EsmParser.ParseFileHeader(bytes);
        if (header is null) return null;
        return [.. header.Masters, Path.GetFileName(path)];
    }

}
