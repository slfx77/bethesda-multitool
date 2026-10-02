using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.RenderWare;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

/// <summary>Adversarial synthetic source dictionaries, with independent raw chunk and pixel construction.</summary>
public sealed class RwPspTextureDictionaryTests
{
    private const uint Library = 0x1C020065;
    private static readonly byte[] LowerMip = [200, 50, 40, 30];
    private static readonly byte[] BasePixels = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    /// <summary>Preserves authored order and distinct occurrence identity without case folding or scope fallback.</summary>
    [Fact]
    public void DuplicateNamesRemainAmbiguousOnlyInsideTheSelectedDictionary()
    {
        var bytes = Dictionary(Native("same"), Native("same"), Native("Same"));
        var dictionary = Read(bytes);
        Assert.Equal(0x00090003u, dictionary.RawStructureWord);
        Assert.Equal(3, dictionary.Rasters.Count);
        var duplicates = dictionary.FindExactName("same");
        Assert.Equal(RwPspTextureCandidateStatus.Ambiguous, duplicates.Status);
        Assert.Equal(2, duplicates.Occurrences.Count);
        Assert.Same(dictionary.Rasters[0], duplicates.Occurrences[0]);
        Assert.Same(dictionary.Rasters[1], duplicates.Occurrences[1]);
        Assert.Equal((0, 1), (duplicates.Occurrences[0].Ordinal, duplicates.Occurrences[1].Ordinal));
        Assert.Equal(28, dictionary.Rasters[0].Chunk.HeaderOffset);
        Assert.Equal(40, dictionary.Rasters[0].Structure.HeaderOffset);
        Assert.Equal(RwPspTextureCandidateStatus.Unique, dictionary.FindExactName("Same").Status);
        Assert.Equal(RwPspTextureCandidateStatus.Missing, dictionary.FindExactName("SAME").Status);
        Assert.Equal(RwPspTextureCandidateStatus.Missing, dictionary.FindExactName("path/same").Status);
        var otherBytes = Dictionary(Native("same"), Native("elsewhere"));
        var other = Read(otherBytes, Origin(otherBytes.Length) with { EntryOrdinal = 8, EntryName = "Other" });
        Assert.Equal(RwPspTextureCandidateStatus.Unique, other.FindExactName("same").Status);
        Assert.Equal(RwPspTextureCandidateStatus.Unique, other.FindExactName("elsewhere").Status);
        Assert.Equal(RwPspTextureCandidateStatus.Missing, dictionary.FindExactName("elsewhere").Status);
    }

    /// <summary>Uses a byte-preserving name representation even where the existing decoder's display string is lossy.</summary>
    [Fact]
    public void HighByteNamesDoNotCollapseIntoAsciiReplacementCharacters()
    {
        var dictionary = Read(Dictionary(Native("\u00E9"), Native("?")));
        Assert.Equal((byte)0xE9, dictionary.Rasters[0].NameBytes.Span[0]);
        Assert.Equal(RwPspTextureCandidateStatus.Unique, dictionary.FindExactName("\u00E9").Status);
        Assert.Equal(RwPspTextureCandidateStatus.Unique, dictionary.FindExactName("?").Status);
        Assert.Equal(0, Assert.Single(dictionary.FindExactName("\u00E9").Occurrences).Ordinal);
        Assert.Equal(1, Assert.Single(dictionary.FindExactName("?").Occurrences).Ordinal);
    }

    /// <summary>Owns raw source bytes and independently decodes every authored level, without a mutable shared pixel cache.</summary>
    [Fact]
    public void SelectedDecodeRetainsEveryAuthoredMipAfterSourceOverwrite()
    {
        var bytes = Dictionary(Native("mips"));
        var original = (byte[])bytes.Clone();
        var dictionary = Read(bytes);
        Assert.Equal(original, bytes);
        var raster = Assert.Single(dictionary.Rasters);
        Assert.Equal(0xFEEDBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(raster.Structure.Payload.Span));
        Assert.Same(dictionary.Origin, raster.Origin);
        Assert.Equal((7, 2, 96, 1024L), (raster.Origin.EntryOrdinal, raster.Origin.ResourceOrdinal,
            raster.Origin.PayloadOffset, raster.Origin.EntryOffset));
        Array.Fill(bytes, (byte)0);
        Assert.Equal(original, dictionary.Source.ToArray());
        var first = raster.TryDecode(220 * 8, out var error);
        Assert.Null(error);
        Assert.NotNull(first);
        Assert.Equal(2, first.MipCount);
        Assert.Equal(BasePixels, first.MipLevels[0].Pixels);
        Assert.Equal(LowerMip, first.MipLevels[1].Pixels);
        Assert.Equal((1, 1), (first.MipLevels[1].Width, first.MipLevels[1].Height));
        Assert.Equal(2, first.ToDecodedTexture().MipCount);
        Array.Fill(first.Rgba, (byte)0);
        var second = raster.TryDecode(220 * 8, out error);
        Assert.NotNull(second);
        Assert.Null(error);
        Assert.Equal(BasePixels, second.Rgba);
        Assert.Null(raster.TryDecode(220 * 8 - 1, out error));
        Assert.Contains("budget", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unproven metadata remains an exact-name candidate but cannot enter the PSP pixel decoder.</summary>
    /// <param name="unknownDictionary">Whether to change the dictionary upper word instead of a native library.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownMetadataRetainsAUniqueUndecodedOccurrence(bool unknownDictionary)
    {
        var bytes = Dictionary(Native("candidate", unknownDictionary ? Library : 0xDEADBEEF));
        if (unknownDictionary) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 0xCAFE0001);
        var dictionary = Read(bytes);
        var candidate = Assert.Single(dictionary.FindExactName("candidate").Occurrences);
        Assert.Equal(RwPspTextureCandidateStatus.Unique, dictionary.FindExactName("candidate").Status);
        Assert.NotNull(candidate.DecodeRestriction);
        Assert.Null(candidate.TryDecode(long.MaxValue, out var error));
        Assert.NotNull(error);
        Assert.Equal(bytes, dictionary.Source.ToArray());
        Assert.NotEmpty(dictionary.Diagnostics);
    }

    /// <summary>An unknown raster format is preserved structurally and declined explicitly instead of becoming a missing name.</summary>
    [Fact]
    public void UnsupportedRasterFormatDoesNotDisappearFromCandidates()
    {
        var body = RasterBody("format");
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x5C), 99);
        var dictionary = Read(Dictionary(Chunk(0x15, Join(Chunk(1, body), Chunk(3, [])))));
        var candidate = Assert.Single(dictionary.FindExactName("format").Occurrences);
        Assert.Null(candidate.TryDecode(long.MaxValue, out var error));
        Assert.Contains("format", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(99u, BinaryPrimitives.ReadUInt32LittleEndian(candidate.Structure.Payload.Span[0x5C..]));
    }

    /// <summary>Opaque plugins and non-terminated names remain available without inventing an alternate resolver key.</summary>
    [Fact]
    public void UnknownExtensionAndUnterminatedNameRemainRawAndDiagnosed()
    {
        var body = RasterBody("ignored");
        body.AsSpan(0x6C, 64).Fill(0x41);
        var plugin = Chunk(0xF101, [7, 8, 9]);
        var native = Chunk(0x15, Join(Chunk(1, body), Chunk(3, plugin)));
        var dictionary = Read(Dictionary(Native("known"), native));
        Assert.Equal(2, dictionary.Rasters.Count);
        var raster = dictionary.Rasters[1];
        Assert.Null(raster.ExactName);
        Assert.NotNull(raster.NameError);
        Assert.Equal(64, raster.NameBytes.Length);
        Assert.Equal(plugin, raster.Children[1].Payload.ToArray());
        Assert.Equal(RwPspTextureCandidateStatus.IncompleteNames, dictionary.FindExactName(new string('A', 64)).Status);
        var known = dictionary.FindExactName("known");
        Assert.Equal(RwPspTextureCandidateStatus.IncompleteNames, known.Status);
        Assert.True(known.HasUnresolvedNames);
        Assert.Same(dictionary.Rasters[0], Assert.Single(known.Occurrences));
        Assert.Empty(dictionary.FindExactName("absent").Occurrences);
        Assert.Equal(RwPspTextureCandidateStatus.IncompleteNames, dictionary.FindExactName("absent").Status);
        Assert.Contains(dictionary.Diagnostics, text => text.Contains("uninterpreted", StringComparison.Ordinal));
    }

    /// <summary>Unknown direct children restrict exactly their proven source scope while retaining unique names.</summary>
    /// <param name="dictionaryScope">Whether opaque data belongs to the whole dictionary or one native occurrence.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpaqueSemanticsBlockOnlyTheAffectedDecodeScope(bool dictionaryScope)
    {
        var unknown = Chunk(0xF123, [1, 2, 3]);
        var blocked = dictionaryScope ? Native("blocked") :
            Chunk(0x15, Join(Chunk(1, RasterBody("blocked")), Chunk(3, unknown)));
        var source = Chunk(0x16, Join(Chunk(1, [2, 0, 9, 0]), Native("control"), blocked,
            dictionaryScope ? unknown : [], Chunk(3, [])));
        var dictionary = Read(source);
        var result = dictionary.FindExactName("blocked");
        Assert.Equal(RwPspTextureCandidateStatus.Unique, result.Status);
        var raster = Assert.Single(result.Occurrences);
        Assert.NotNull(raster.DecodeRestriction);
        Assert.Null(raster.TryDecode(long.MaxValue, out var reason));
        Assert.NotNull(reason);
        var control = Assert.Single(dictionary.FindExactName("control").Occurrences).TryDecode(long.MaxValue, out _);
        if (dictionaryScope) Assert.Null(control);
        else Assert.NotNull(control);
        Assert.Equal(source, dictionary.Source.ToArray());
    }

    /// <summary>A truncated native header retains its ordinal and raw body instead of shrinking the dictionary.</summary>
    [Fact]
    public void TruncatedRasterHeaderRemainsAnExplicitUndecodableOccurrence()
    {
        var dictionary = Read(Dictionary(Chunk(0x15, Join(Chunk(1, new byte[20]), Chunk(3, [])))));
        var raster = Assert.Single(dictionary.Rasters);
        Assert.Equal(0, raster.Ordinal);
        Assert.Equal(20, raster.Structure.Payload.Length);
        Assert.True(raster.NameBytes.IsEmpty);
        Assert.NotNull(raster.NameError);
        Assert.Equal(RwPspTextureCandidateStatus.IncompleteNames, dictionary.FindExactName("absent").Status);
        Assert.Null(raster.TryDecode(160, out var error));
        Assert.NotNull(error);
    }

    /// <summary>The empty observed dictionary is complete and carries the same unmodified upper metadata word.</summary>
    [Fact]
    public void EmptyDictionaryIsNotAnExternalTextureDependency()
    {
        var dictionary = Read(Dictionary());
        Assert.Equal(40, dictionary.Source.Length);
        Assert.Equal(0x00090000u, dictionary.RawStructureWord);
        Assert.Empty(dictionary.Rasters);
        Assert.Equal(RwPspTextureCandidateStatus.Missing, dictionary.FindExactName("Cloth_Sack").Status);
    }

    /// <summary>Rejects incomplete streams, ambiguous structural owners and malicious counts before treating the dictionary as complete.</summary>
    /// <param name="corruption">A distinct structural violation.</param>
    [Theory]
    [InlineData("short-root")]
    [InlineData("trailing-root")]
    [InlineData("native-count")]
    [InlineData("native-child-size")]
    [InlineData("missing-extension")]
    [InlineData("duplicate-structure")]
    public void MalformedDictionariesDeclineRatherThanReturnPartialRasters(string corruption)
    {
        var bytes = Dictionary(Native("bounded"));
        switch (corruption)
        {
            case "short-root": bytes = bytes[..^1]; break;
            case "trailing-root": bytes = Join(bytes, [0]); break;
            case "native-count": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 0x0009FFFF); break;
            case "native-child-size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), uint.MaxValue); break;
            case "missing-extension": bytes = Chunk(0x16, Chunk(1, [0, 0, 9, 0])); break;
            case "duplicate-structure": bytes = Chunk(0x16, Join(Chunk(1, [0, 0, 9, 0]), Chunk(1, [0, 0, 9, 0]), Chunk(3, []))); break;
        }
        var before = (byte[])bytes.Clone();
        Assert.Null(RwPspTextureDictionaryReader.TryRead(bytes, Origin(bytes.Length), bytes.Length, out var error));
        Assert.NotNull(error);
        Assert.NotEmpty(error);
        Assert.Equal(before, bytes);
    }

    /// <summary>Admission and provenance bounds precede source ownership, including long-offset overflow.</summary>
    [Fact]
    public void EncodedLimitAndInvalidOccurrenceBoundsAreExplicitDeclines()
    {
        var bytes = Dictionary();
        var origin = Origin(bytes.Length);
        Assert.Null(RwPspTextureDictionaryReader.TryRead(bytes, origin, bytes.Length - 1, out var limit));
        Assert.Contains("limit", limit, StringComparison.OrdinalIgnoreCase);
        Assert.Null(RwPspTextureDictionaryReader.TryRead(bytes, origin with { EntryOffset = long.MaxValue }, bytes.Length, out _));
        Assert.Null(RwPspTextureDictionaryReader.TryRead(bytes, origin with { ResourceOrdinal = -1 }, bytes.Length, out _));
        Assert.Null(RwPspTextureDictionaryReader.TryRead(bytes, origin with { PayloadOffset = 97 }, bytes.Length, out _));
    }

    /// <summary>Reads only an independently constructed complete dictionary.</summary>
    /// <param name="bytes">The authored dictionary root.</param>
    /// <param name="origin">Explicit alternative source occurrence, or the default synthetic location.</param>
    /// <returns>The complete structural reader result.</returns>
    private static RwPspTextureDictionary Read(byte[] bytes, RwPspTextureDictionaryOrigin? origin = null)
    {
        var result = RwPspTextureDictionaryReader.TryRead(bytes, origin ?? Origin(bytes.Length), bytes.Length, out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        return result;
    }

    /// <summary>Supplies distinct archive and resource ordinals without deriving identity from a display name.</summary>
    /// <param name="length">The selected dictionary byte count.</param>
    /// <returns>Caller-proven synthetic source metadata.</returns>
    private static RwPspTextureDictionaryOrigin Origin(int length) =>
        new("source-generation:A", 7, "Entry", 1024, length + 96, 2, 96, @"z:\authoring\source.txd");

    /// <summary>Writes the four-byte declaration and complete native/extension stream independently of the reader.</summary>
    /// <param name="natives">Original-order native envelopes.</param>
    /// <returns>A complete original-style dictionary root.</returns>
    private static byte[] Dictionary(params byte[][] natives)
    {
        var declaration = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(declaration, 0x00090000u | (uint)natives.Length);
        return Chunk(0x16, Join(Chunk(1, declaration), Join(natives), Chunk(3, [])));
    }

    /// <summary>Wraps one two-level native raster in the measured Struct/Extension envelope.</summary>
    /// <param name="name">Byte-preserving fixed field content.</param>
    /// <param name="library">Explicit native and Struct library metadata.</param>
    /// <returns>The complete native envelope.</returns>
    private static byte[] Native(string name, uint library = Library) =>
        Chunk(0x15, Join(Chunk(1, RasterBody(name), library), Chunk(3, [])), library);

    /// <summary>Writes 2x2 and 1x1 authored RGBA images with different pixels and nonimage row padding.</summary>
    /// <param name="name">The fixed field content before its NUL terminator.</param>
    /// <returns>The independently authored PSP Struct body.</returns>
    private static byte[] RasterBody(string name)
    {
        var body = new byte[220]; // 172-byte header, two 16-byte base rows, one 16-byte lower row.
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xFEEDBEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 0x00020002);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x5C), 3);
        Encoding.Latin1.GetBytes(name).CopyTo(body, 0x6C);
        body.AsSpan(172).Fill(0xCC);
        BasePixels.AsSpan(0, 8).CopyTo(body.AsSpan(172));
        BasePixels.AsSpan(8, 8).CopyTo(body.AsSpan(188));
        LowerMip.CopyTo(body, 204);
        return body;
    }

    /// <summary>Writes one LE chunk envelope without invoking production chunk helpers.</summary>
    /// <param name="type">Explicit raw chunk type.</param>
    /// <param name="body">Exact payload without implicit padding.</param>
    /// <param name="library">Raw library metadata.</param>
    /// <returns>The complete header and payload.</returns>
    private static byte[] Chunk(uint type, byte[] body, uint library = Library)
    {
        var result = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, type);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), library);
        body.CopyTo(result, 12);
        return result;
    }

    /// <summary>Concatenates authored chunks without introducing alignment or inferred padding.</summary>
    /// <param name="parts">Exact ordered byte sequences.</param>
    /// <returns>The concatenated source bytes.</returns>
    private static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts) stream.Write(part);
        return stream.ToArray();
    }
}
