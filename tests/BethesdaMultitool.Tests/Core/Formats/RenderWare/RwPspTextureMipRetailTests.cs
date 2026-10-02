using System.Buffers.Binary;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.RenderWare;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

/// <summary>Checks one original indexed PSP raster against an independent raw-byte decode of every authored mip.</summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RwPspTextureMipRetailTests
{
    private const string EntryHash = "4E65F40CF5584BB074A4665E69CE27E3BFAB6D4CA5A09427C3D5C8FC4AAF8991";
    private const string BodyHash = "4D24000295AF419E4F10AB4DB42E2FD5B3BD5E690EC1947B4E29FC4A50E24C75";
    private static readonly (int Size, int Pitch, int Offset, string Hash)[] ExpectedMips =
    [
        (128, 64, 236, "33C0A38674BE2F184DA4E60147ACEFFE768C2DF902D1627125CE97954C23D443"),
        (64, 32, 8428, "60AF34325DCC61ADC0764AC26F82AA5C6FE78FD08E7E07537A5F646DDB89B6D2"),
        (32, 16, 10476, "0BC8A362E6E3AC28EB6ECCFF3E5981FD92846E449D7712161C0BD9B919048218"),
        (16, 16, 10988, "C7572DCDCB64369AB9F9EFE8AA41FB6DB98FED04990AAAFAF1DE1A4132DB1650"),
        (8, 16, 11244, "5557A364C4F951E1FB6B3BDEBA1CC63E690315FE53E50BB22D4CC6148DF8B736"),
        (4, 16, 11372, "7321F3EC0458CA75C163169931B102BD648AC26BA48BE3B93199E1F611F31171"),
        (2, 16, 11436, "F300506D47F6F8393019747E1ECDF718636DD1D8FB2C21CDE96D29282DBC204F"),
        (1, 16, 11468, "262D83065411C4C066B1800745D2712358115F884134F967FC5EB4A7035F0759")
    ];

    /// <summary>Requires exact source identity, nested bounds, all eight authored RGBA levels and independent decoded ownership.</summary>
    [Fact]
    public void OriginalHubRockRetainsEveryIndexedMipAndSourceBytes()
    {
        var bytes = ReadPinnedEntry();
        var resources = OblivionPspResourceReader.ReadResources(bytes);
        Assert.NotEmpty(resources);
        var resource = resources[0];
        Assert.Equal("rwID_TEXDICTIONARY", resource.TypeName);
        Assert.Equal(204, resource.HeaderLength);
        Assert.Equal(584, resource.PayloadOffset);
        Assert.Equal(456408, resource.PayloadLength);
        Assert.True(resource.IsRenderWareStream);
        Assert.Equal(RwChunk.TextureDictionary, resource.RootChunkType);
        Assert.Equal("DF228FDCFAE01F1B089BC7D34F43699E896F4BCADDF9F4C0F65CF68609DF6DFE",
            Hash(bytes.AsSpan(resource.PayloadOffset, resource.PayloadLength)));

        var origin = new RwPspTextureDictionaryOrigin("original:OblivionPSP:2007-1-11:GR.ARC",
            87, "Hub_1", 98032192, bytes.Length, 0, resource.PayloadOffset, resource.AuthoringPath);
        var dictionary = RwPspTextureDictionaryReader.TryRead(
            bytes.AsSpan(resource.PayloadOffset, resource.PayloadLength), origin, 4 * 1024 * 1024, out var dictionaryError);
        Assert.Null(dictionaryError);
        Assert.NotNull(dictionary);
        Assert.Equal(0x1C020065u, dictionary.LibraryId);
        Assert.Equal(0x00090040u, dictionary.RawStructureWord);
        Assert.Equal(64, dictionary.Rasters.Count);
        var candidates = dictionary.FindExactName("ob_Rock2");
        Assert.Equal(RwPspTextureCandidateStatus.Unique, candidates.Status);
        var selectedRaster = Assert.Single(candidates.Occurrences);
        Assert.Same(dictionary.Rasters[21], selectedRaster);
        Assert.Equal(21, selectedRaster.Ordinal);
        Assert.Equal((124652, 124664), (selectedRaster.Chunk.HeaderOffset, selectedRaster.Structure.HeaderOffset));
        Assert.Equal(BodyHash, Hash(selectedRaster.Structure.Payload.Span));
        var selectedTexture = selectedRaster.TryDecode(1024 * 1024, out var decodeError);
        Assert.Null(decodeError);
        Assert.NotNull(selectedTexture);
        AssertAuthoredMips(selectedTexture);

        var bodyHeader = PinnedRasterBody(bytes, resource);
        var body = bytes.AsSpan(bodyHeader.PayloadOffset, bodyHeader.Size);
        Assert.Equal(BodyHash, Hash(body));
        Assert.Equal(0x00800080u, BinaryPrimitives.ReadUInt32LittleEndian(body[4..]));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(body[0x5C..]));
        var texture = RwPspTexture.TryParse(body);
        Assert.NotNull(texture);
        Assert.Equal("ob_Rock2", texture.Name);
        Assert.Equal(RwPspPixelFormat.Indexed4, texture.Format);
        Assert.Equal((128, 128), (texture.Width, texture.Height));
        Assert.Equal(BodyHash, Hash(body));
        Assert.Equal(EntryHash, Hash(bytes));
        AssertAuthoredMips(texture);

        // The source entry can be released or reused after parsing without changing any level.
        Array.Fill(bytes, (byte)0);
        AssertAuthoredMips(texture);
        AssertAuthoredMips(selectedTexture);
        Assert.Equal("DF228FDCFAE01F1B089BC7D34F43699E896F4BCADDF9F4C0F65CF68609DF6DFE", Hash(dictionary.Source.Span));
        Assert.Equal(BodyHash, Hash(selectedRaster.Structure.Payload.Span));
    }

    /// <summary>Reads only the fixed Hub_1 entry, never the full pack.</summary>
    /// <returns>The bounded original entry after exact metadata and content checks.</returns>
    private static byte[] ReadPinnedEntry()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var build = RealAssetPaths.Travels.OblivionPspBuild("2007-1-11");
        Assert.SkipWhen(build is null, RealAssetPaths.SkipMessage("Oblivion PSP 2007-1-11"));
        Assert.NotNull(build);
        var pack = Path.Combine(build, "PSP_GAME", "USRDIR", "GR.ARC");
        Assert.True(File.Exists(pack), $"The named original PSP archive is missing: {pack}");
        Assert.Equal(180338268L, new FileInfo(pack).Length);
        var archive = OblivionPspArchive.Parse(pack);
        Assert.True(archive.IsTagged);
        var entry = Assert.Single(archive.Entries, static value => value.Name == "Hub_1");
        Assert.Equal(entry, archive.Entries[87]);
        Assert.Equal(98032192L, entry.Offset);
        Assert.Equal(1989324L, entry.Size);
        Assert.InRange(entry.Size, 1, 4 * 1024 * 1024);
        var bytes = new byte[checked((int)entry.Size)];
        using var stream = File.OpenRead(pack);
        stream.Position = entry.Offset;
        stream.ReadExactly(bytes);
        Assert.Equal(EntryHash, Hash(bytes));
        return bytes;
    }

    /// <summary>Checks complete dictionary and raster child streams before selecting the pinned Struct body.</summary>
    /// <param name="bytes">The bounded original archive entry.</param>
    /// <param name="resource">Named resource zero, whose complete texture dictionary is hash-pinned.</param>
    /// <returns>The exact raster Struct header within the entry.</returns>
    private static RwChunkHeader PinnedRasterBody(byte[] bytes, OblivionPspResource resource)
    {
        Assert.True(RwChunk.TryRead(bytes, resource.PayloadOffset,
            resource.PayloadOffset + resource.PayloadLength, out var dictionary));
        Assert.Equal(RwChunk.TextureDictionary, dictionary.Type);
        Assert.Equal((596, 456396, 456992), (dictionary.PayloadOffset, dictionary.Size, dictionary.End));
        var children = CompleteChildren(bytes, dictionary);
        Assert.Equal(66, children.Count);
        Assert.Equal(RwChunk.Struct, children[0].Type);
        Assert.Equal(4, children[0].Size);
        Assert.Equal(0x00090040u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(children[0].PayloadOffset)));
        var rasters = children.Where(static child => child.Type == RwChunk.TextureNative).ToArray();
        Assert.Equal(64, rasters.Length);
        var raster = rasters[21];
        Assert.Equal((125248, 11508, 136756), (raster.PayloadOffset, raster.Size, raster.End));
        var rasterChildren = CompleteChildren(bytes, raster);
        Assert.Equal(2, rasterChildren.Count);
        var body = rasterChildren[0];
        Assert.Equal(RwChunk.Struct, body.Type);
        Assert.Equal((125260, 11484, 136744), (body.PayloadOffset, body.Size, body.End));
        Assert.Equal(RwChunk.Extension, rasterChildren[1].Type);
        Assert.Equal(0, rasterChildren[1].Size);
        return body;
    }

    /// <summary>Requires every child to fit its exact parent bounds with no unparsed tail.</summary>
    /// <param name="bytes">The source entry whose child headers are inspected.</param>
    /// <param name="parent">The exact enclosing dictionary or native-raster chunk.</param>
    /// <returns>All validated direct children in authored order.</returns>
    private static List<RwChunkHeader> CompleteChildren(byte[] bytes, RwChunkHeader parent)
    {
        var result = new List<RwChunkHeader>();
        var offset = parent.PayloadOffset;
        while (offset < parent.End)
        {
            Assert.True(RwChunk.TryRead(bytes, offset, parent.End, out var child));
            Assert.Equal(0x1C020065u, child.LibraryId);
            result.Add(child);
            offset = child.End;
        }
        Assert.Equal(parent.End, offset);
        return result;
    }

    /// <summary>Checks hashes from an independent Python low-nibble-first CLUT lookup of each raw padded level.</summary>
    /// <param name="texture">The actual reader result, before or after overwriting the caller's source buffer.</param>
    private static void AssertAuthoredMips(RwPspTexture texture)
    {
        Assert.Equal(8, texture.MipCount);
        var decoded = texture.ToDecodedTexture();
        Assert.Equal(8, decoded.MipCount);
        Assert.Same(texture.Rgba, decoded.Pixels);
        var expectedOffset = 0xAC + 16 * 4;
        for (var level = 0; level < ExpectedMips.Length; level++)
        {
            var expected = ExpectedMips[level];
            var originalMip = texture.MipLevels[level];
            var viewerMip = decoded.MipLevels[level];
            Assert.Equal(expected.Offset, expectedOffset);
            expectedOffset += expected.Pitch * expected.Size;
            Assert.Equal((expected.Size, expected.Size), (originalMip.Width, originalMip.Height));
            Assert.Equal(expected.Size * expected.Size * 4, originalMip.Pixels.Length);
            Assert.Equal(expected.Hash, Hash(originalMip.Pixels));
            Assert.Equal((originalMip.Width, originalMip.Height), (viewerMip.Width, viewerMip.Height));
            Assert.Same(originalMip.Pixels, viewerMip.Pixels);
        }
        Assert.Equal(11484, expectedOffset);
    }

    /// <summary>Computes a stable full-buffer SHA256 without retaining or copying private payloads.</summary>
    /// <param name="bytes">The source or decoded bytes to identify.</param>
    /// <returns>The uppercase hexadecimal content digest.</returns>
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
