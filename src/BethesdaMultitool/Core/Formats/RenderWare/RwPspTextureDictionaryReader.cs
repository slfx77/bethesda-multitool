using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>Strict structural reader for one source-scoped PSP texture dictionary, with selected-only raster decoding.</summary>
/// <remarks>The observed upper word 9 and library 0x1C020065 gate decoding; this does not assign them sampler,
/// lighting, alpha, platform or dependency semantics. Unknown children remain opaque and never select a material.
/// Chunk walking retains the existing RwChunk 0x1300 size correction; its original header remains available.</remarks>
internal static class RwPspTextureDictionaryReader
{
    private const uint ObservedLibraryId = 0x1C020065;
    private const uint ObservedUpperWord = 9;

    /// <summary>Reads exactly one root after checking caller admission and occurrence bounds, then owns its source bytes.</summary>
    /// <param name="source">Exactly one complete dictionary root, excluding resource wrapper and other dictionaries.</param>
    /// <param name="origin">Caller-proven source occurrence; names and authoring paths are diagnostic only.</param>
    /// <param name="maxSourceBytes">Explicit caller limit checked before allocating the private source copy.</param>
    /// <param name="error">Structural/admission failure reason, or null when the complete dictionary was retained.</param>
    /// <returns>The complete structural dictionary, or null for malformed/truncated core layout.</returns>
    public static RwPspTextureDictionary? TryRead(ReadOnlySpan<byte> source,
        RwPspTextureDictionaryOrigin origin, int maxSourceBytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin.SourceIdentity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSourceBytes);
        error = "The dictionary exceeds the caller's encoded source limit.";
        if (source.Length > maxSourceBytes) return null;
        error = "Dictionary occurrence metadata lies outside its containing archive entry.";
        if (origin.EntryOrdinal < 0 || origin.ResourceOrdinal < 0 || origin.EntryOffset < 0 ||
            origin.EntryLength < 0 || origin.EntryOffset > long.MaxValue - origin.EntryLength ||
            origin.PayloadOffset < 0 || origin.PayloadOffset > origin.EntryLength - source.Length) return null;
        error = "Expected exactly one complete texture dictionary root.";
        if (!RwChunk.TryRead(source, 0, source.Length, out var root) ||
            root.Type != RwChunk.TextureDictionary || root.End != source.Length) return null;
        try
        {
            var result = ReadOwned(source.ToArray(), origin, root);
            error = null;
            return result;
        }
        catch (InvalidDataException failure)
        {
            error = failure.Message;
            return null;
        }
    }

    /// <summary>Validates exact child tiling and counts without allocating from untrusted declared counts.</summary>
    /// <param name="data">The private root copy.</param>
    /// <param name="origin">Validated source location.</param>
    /// <param name="root">The already bounded root header.</param>
    /// <returns>Owned structure and original native occurrence order.</returns>
    private static RwPspTextureDictionary ReadOwned(byte[] data, RwPspTextureDictionaryOrigin origin, RwChunkHeader root)
    {
        var children = Children(data, root.PayloadOffset, root.End);
        Require(children.Count >= 2 && children[0].Type == RwChunk.Struct, "Missing dictionary structure.");
        var structure = Single(children, RwChunk.Struct);
        Require(structure.Payload.Length == 4, "Unsupported dictionary structure size.");
        var declaration = BinaryPrimitives.ReadUInt32LittleEndian(structure.Payload.Span);
        _ = Single(children, RwChunk.Extension);
        var natives = children.Where(chunk => chunk.Type == RwChunk.TextureNative).ToArray();
        Require(natives.Length == (declaration & 0xFFFF), "Dictionary native count disagrees with its complete child stream.");
        var diagnostics = new List<string>();
        var opaque = InspectOpaque(children, "dictionary", true, diagnostics);
        var restriction = root.LibraryId != ObservedLibraryId || structure.LibraryId != ObservedLibraryId ||
                          (declaration >> 16) != ObservedUpperWord
            ? "Dictionary metadata differs from the observed PSP library/upper-word combination; pixels remain undecoded." : null;
        if (opaque) restriction ??= "Uninterpreted dictionary children require interpretation before PSP pixel decoding.";
        if (restriction is not null) diagnostics.Add(restriction);
        var rasters = new RwPspTextureDictionaryRaster[natives.Length];
        for (var ordinal = 0; ordinal < natives.Length; ordinal++)
        {
            rasters[ordinal] = ReadRaster(data, origin, natives[ordinal], ordinal, restriction, diagnostics);
        }
        return new RwPspTextureDictionary(origin, root.LibraryId, declaration, children, Array.AsReadOnly(rasters),
            diagnostics.AsReadOnly(), data);
    }

    /// <summary>Retains one native Struct and name without invoking a pixel decoder or collapsing duplicate names.</summary>
    /// <param name="data">Private source bytes.</param>
    /// <param name="origin">The exact source occurrence.</param>
    /// <param name="native">Original native envelope.</param>
    /// <param name="ordinal">Original native occurrence index.</param>
    /// <param name="restriction">An outer dictionary interpretation restriction.</param>
    /// <param name="diagnostics">Owned list collecting uninterpreted source features.</param>
    /// <returns>The retained native occurrence, independently of whether its pixels can decode.</returns>
    private static RwPspTextureDictionaryRaster ReadRaster(byte[] data, RwPspTextureDictionaryOrigin origin,
        RwPspTextureDictionaryChunk native, int ordinal, string? restriction, List<string> diagnostics)
    {
        var children = Children(data, native.HeaderOffset + RwChunk.HeaderLength, native.HeaderOffset + native.Bytes.Length);
        Require(children.Count >= 2 && children[0].Type == RwChunk.Struct, "Missing native raster structure.");
        var structure = Single(children, RwChunk.Struct);
        _ = Single(children, RwChunk.Extension);
        var opaque = InspectOpaque(children, $"raster[{ordinal}]", false, diagnostics);
        if (native.LibraryId != ObservedLibraryId || structure.LibraryId != ObservedLibraryId)
            restriction = "Native raster metadata differs from the observed PSP library; pixels remain undecoded.";
        if (opaque) restriction ??= "Uninterpreted native children require interpretation before PSP pixel decoding.";
        var nameBytes = structure.Payload.Length >= RwPspTexture.ClutOffset
            ? structure.Payload.Slice(RwPspTexture.NameOffset, RwPspTexture.NameLength) : ReadOnlyMemory<byte>.Empty;
        var nul = nameBytes.Span.IndexOf((byte)0);
        var name = nul >= 0 ? Encoding.Latin1.GetString(nameBytes.Span[..nul]) : null;
        var nameError = name is null ? "The fixed raster name field is truncated or lacks a proven NUL terminator." : null;
        if (nameError is not null) diagnostics.Add($"raster[{ordinal}]: {nameError}");
        if (restriction is not null) diagnostics.Add($"raster[{ordinal}]: {restriction}");
        return new RwPspTextureDictionaryRaster(origin, ordinal, native, structure, children,
            nameBytes, name, nameError, restriction);
    }

    /// <summary>Retains the complete direct stream or rejects a malformed tail instead of silently truncating it.</summary>
    /// <param name="data">The owned dictionary bytes.</param>
    /// <param name="offset">First child header.</param>
    /// <param name="end">Exact parent end.</param>
    /// <returns>All child envelopes as read-only slices of the owned source.</returns>
    private static ReadOnlyCollection<RwPspTextureDictionaryChunk> Children(byte[] data, int offset, int end)
    {
        var result = new List<RwPspTextureDictionaryChunk>();
        while (offset < end)
        {
            Require(RwChunk.TryRead(data, offset, end, out var child), "Malformed or truncated dictionary child stream.");
            result.Add(new RwPspTextureDictionaryChunk(child.Type, child.LibraryId, offset,
                data.AsMemory(offset, child.End - offset)));
            offset = child.End;
        }
        return result.AsReadOnly();
    }

    /// <summary>Requires one structural owner rather than electing the first duplicate.</summary>
    /// <param name="children">Complete validated child stream.</param>
    /// <param name="type">Required structural chunk type.</param>
    /// <returns>The sole structural chunk.</returns>
    private static RwPspTextureDictionaryChunk Single(IReadOnlyList<RwPspTextureDictionaryChunk> children, uint type)
    {
        var matches = children.Where(chunk => chunk.Type == type).ToArray();
        Require(matches.Length == 1, $"Expected one dictionary/native child of type 0x{type:X}.");
        return matches[0];
    }

    /// <summary>Records unknown children and nonempty extension bodies without assigning plugin or material meaning.</summary>
    /// <param name="children">Original complete child stream.</param>
    /// <param name="scope">The source occurrence receiving diagnostics.</param>
    /// <param name="allowNative">Whether native children are structural dictionary members at this level.</param>
    /// <param name="diagnostics">Owned diagnostic list.</param>
    /// <returns>Whether unproven child semantics must block selected pixel decoding.</returns>
    private static bool InspectOpaque(IReadOnlyList<RwPspTextureDictionaryChunk> children, string scope, bool allowNative, List<string> diagnostics)
    {
        var opaque = false;
        foreach (var child in children)
        {
            if (child.Type is not (RwChunk.Struct or RwChunk.Extension) && (!allowNative || child.Type != RwChunk.TextureNative) ||
                child.Type == RwChunk.Extension && !child.Payload.IsEmpty)
            {
                opaque = true;
                diagnostics.Add($"{scope}: uninterpreted child 0x{child.Type:X} at dictionary byte {child.HeaderOffset}.");
            }
        }
        return opaque;
    }

    /// <summary>Produces a stable structural decline without swallowing resource exhaustion or programming errors.</summary>
    /// <param name="condition">Required source invariant.</param>
    /// <param name="message">The violated invariant.</param>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
