using System.Buffers.Binary;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.RenderWare;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

/// <summary>Correlates original CLUMP material slots with an explicitly selected dictionary, without rendering policy.</summary>
/// <remarks>Independent raw chunk, attribute and indexed-pixel hashes were measured on September 20, 2026.
/// These are candidate/byte preservation checks, not sampler, lighting, alpha, culling or cross-entry admission.
/// Every case reads at most two named entries (2 MiB each); no source payload is written or globally cached.</remarks>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RwPspMaterialBindingRetailTests
{
    private const int MaximumEntryBytes = 2 * 1024 * 1024;
    private const string WeaponsDictionaryHash = "AD3ED27148B230FB55BB77E2D54DB0D05F62B8693DF2CD1559493B443B1E01DC";
    private const string EmptyDictionaryHash = "BE3C72F9B44563157785FA9ABE3B873C49AADC060A1A633007F888DE002969A6";
    private const string ClothDictionaryHash = "A9BFCBF170CCEB3C8D638368085DF186C4F5FBBE77182F94C8F9007EC43BDAE1";
    private static readonly EntryPin Weapons = new(86, "Weapons", 37540384, 1528480,
        "C0DD35608095DA1C61A6A552E34E394E4ABA72FB540F2887D6FC9683280B84D9");
    private static readonly EntryPin Goodies = new(28, "CContainerBehaviour.Goodies", 5814272, 9188,
        "EFBCA800155F656CDAC6FE5F7543BEED53F25CF312E1CEE284D4601570A99F44");
    private static readonly EntryPin ClothSack = new(27, "CContainerBehaviour.ClothSack", 5803232, 11040,
        "044D093C2223A220D69FE7C39661EEB845F9B3A49F6786C8A2F8E15A341864A2");

    private static readonly ClumpPin Arrow = new(39, 1292860, 5514,
        "A4CEA62C4591415C9CAC4DFC9EE5CED2838A9EC5666F095D73198D087C405998", 0x10037, 99, null, 0,
        "A622C5C57B036574836AD70D7F9DC916347A3C5D76D7A0959BC9ADEA4F6CB614",
        [
            new(new("Ebony_Arrow01", 42, 193472, "6CB2E380F3DE83A698CF48132654435C251B244656993E8D7E1F3698571D7B0D", 64, 128,
                "5660AF1830471DB634ABCD0A671885E6A5840623A565F5EF3BCCCADE9B0C3A80", RwPspPixelFormat.Indexed8), 32),
            new(new("Ebony_Arrow02", 41, 184048, "D0C5B903666F63678D87D78F43934A61D6CEE30015539BBE294AF4711857CC89", 64, 128,
                "4E385E6623CA1256C07EF121B1074E39CCC7784B9362F4F792CD2A238532DF19", RwPspPixelFormat.Indexed8), 66)
        ]);
    private static readonly ClumpPin LongSword = new(12, 881148, 19657,
        "E75F9E48FF86A69A785379D3F281ABEF9686B4ACF036E06FF6BF0FF7A7162A29", 0x1003F, 298,
        "9B2C8C3F564B9FE2785F6F1FE884C8CDE06801AB307E9E97501191BD6E770F9A", 17,
        "CA132B4CA4348606A2BCD7FA265743C828794BDB54401F1A9D76C83E8D4229BD",
        [
            new(new("ELS_handle02", 144, 640288, "65C62A01A4FE3A82BB51D968BC2B0036E031CDAA063656AB3594599393417676", 64, 64,
                "DBC7A1B302E5C3A46F92D18EA92716FB50B3EECAE032DB65593C7EF84A79323C"), 102),
            new(new("ELS_handle03", 142, 635648, "3B19D725FF5B6C1D08BC6FA024DCF494AB5ABD5A02966AF8A5B24B8BD3D7AA03", 64, 64,
                "0F30A5FBCFEE53992B55DDDD288A12034DFF39AC4069BCEB3CF992C7637A48F9"), 104),
            new(new("ELS_blade02", 141, 633328, "778C83468286ED2A02548E7EC71CFD0570E626308F50CED99CB018E0F60FB521", 64, 64,
                "C1CEC5CE322A2ADACFDB7A61CA77B3BA19DD05D59C8CB37D0581E78A6AA83FF6"), 16),
            new(new("ELS_blade01", 140, 631008, "AE3ECDFBC9DBDA6BDC8065A7E51FC5F821F4AC2CFFF71815EA46189081C4780D", 64, 64,
                "ED4F1EE1E27AE3D1E6337B8966B445F1044AD21D15B29D83DD053A82554CB5B8"), 24),
            new(new("ELS_blade03", 139, 628688, "F1304C6DF8CBD7E371AF2E7337A7A183B5302325CE5A2B6FB0DE34AF9A5592F9", 64, 64,
                "3F9F43449B4A60543C10CF6FEAE3F1D848085372EF274589F5FC6CFD70FB634A"), 28),
            new(new("ELS_handle01", 138, 626368, "633193709CCC8DF7129FF3D0F93C6647C1FF03C71A30569E184BC017E47A2437", 64, 64,
                "285EC73DCF4E159A677D9EA457486F9A538CDC92C4F3F6B8D6BCD68BE1990A77"), 80)
        ]);
    private static readonly ClumpPin Shield = new(34, 1242448, 8882,
        "D409F262011437AFF04D62987E5FF565A32BC77B8845EE74DC4454E4AB6FB517", 0x1003F, 137,
        "985971DCE023602B226067C2B1AE95C7F53102303BF88994149F6E9CBC45340E", 1,
        "EB635D336A1D0B7A959413CE08583EC874A7A46E9133369C9DC2B7D7960E2163",
        [
            new(new("IronShieldBack02", 62, 285888, "4E1B49CA1D36404EA6DB03CD7121EAD06BF2003D2E8C9442C6EFE7C7D4FFFDD1", 64, 128,
                "AA4999C02EF84878D9F4F080C7DDAB3715CD763E4DA1DF42270F1C71A3F6F272"), 12),
            new(new("IronShield03", 61, 281520, "491BDD8AB9E6D76057B827C0CEECAFF96DE9BA1A0732E1619A8813679E067294", 128, 64,
                "2B5AF7CA11060AE649622679E7FCA2E5F11F305C758E4257E2804E68779E267B"), 32),
            new(new("IronShield01", 60, 277152, "E90DBCF9F021ADA47DED92B5EB19C6FF385F552167D051C7DB17E7B0D6C4C959", 64, 128,
                "2A18FB5EAF93C79FFFFA537EFEE5CA9531C1B4F9CD8FD81FF3360809C65AF129"), 12),
            new(new("IronShield02", 59, 272784, "C377FF94DFA9116A78C93AE583085C0BF8B8A1D38A890DF8CA85EAD6B72D9109", 64, 128,
                "A3B06D36808FD46C09528569C9BEDB479E2AD729458D03BC5BDF62FBDDA4675E"), 32),
            new(new("IronShieldBack01", 58, 268416, "0C0ED094BAD9D0FE4E05600736D64CFDDB0EF101867F6DB7EA5C5813AF4F783F", 64, 128,
                "CAE5F7B9673F734164F465162AB97BC1890EB49ABA8C4490A842999CE5DB6D80"), 12)
        ]);
    private static readonly ClumpPin IronArrow = new(37, 1279736, 5730,
        "CAD65D24A84BFB76EB8C35E7FD37690D2B878FA9AEA2E9675EBEB6D5BE75426D", 0x10077, 114, null, 0,
        "8E6F08B9EE9BD88827B46D0A9298DCE63D9C2EE1DAE7728DBE203BA73E2F13CF",
        [
            new(new("ironquiver", 47, 220368, "DB3FE2E1F67C9484D2E59AC0FC7681D7BD44E486FDEC1871A4913B7D41F5655F", 64, 128,
                "3913FC22FE7FDE857846706426D98DD105B9E1C51A07DEFF764EA4BDF50CA7CF"), 40),
            new(new("ironquiver2", 46, 216000, "AF83C572E58B3FC6F9A5546BC2081FBCEC78285B66F49754E20FA7FDEAAA2A13", 128, 64,
                "213BF4158B23F7F4411CEE96C6151C8F5ECCC631DEBE0652AF9F91C479D23A85"), 15),
            new(null, 3, 0xFF666666)
        ]);
    private static readonly RasterPin ClothRaster = new("Cloth_Sack", 0, 316,
        "C9F5F4020ED5155D22E13CFC4B942F142815DD083A73B4B35B86F44CD7A917C8", 64, 64,
        "75CDD833C77495511C7498D285B7FEB2D62E12913C3421DD42759AF215325BFC");

    [Fact]
    public void EbonyArrowKeepsMaterialSlotToExactRasterAndOriginalSamplerWords() => AssertWeapons(Arrow);

    [Theory]
    [InlineData(12)]
    [InlineData(34)]
    public void PrelitMulticolorAndBlackMeshesRetainColorsDuringTexturedBinding(int resourceOrdinal)
    {
        var pin = resourceOrdinal == 12 ? LongSword : Shield;
        var geometry = AssertWeapons(pin);
        var colors = geometry.Geometry.Colours;
        Assert.NotNull(colors);
        if (resourceOrdinal == 34)
        {
            for (var vertex = 0; vertex < pin.Vertices; vertex++)
            {
                Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(colors.AsSpan(vertex * 4, 4)));
            }
        }
    }

    [Fact]
    public void IronArrowKeepsGrayUntexturedSlotBesideTwoTextureCandidates() => AssertWeapons(IronArrow);

    [Fact]
    public void GoodiesMissingTextureRemainsMissingWhenOtherEntryHasCandidate()
    {
        var local = ReadEntry(Goodies);
        var external = ReadEntry(ClothSack);
        var clump = ReadClump(local, 1, 556, 6884,
            "185BA620907356519E6363C1348CA74F68C4B30CCFF1AEFA252C2816778D7F2F");
        var geometry = Assert.Single(clump.Geometries);
        var material = Assert.Single(geometry.Materials);
        Assert.NotNull(material.Texture);
        Assert.Equal("Cloth_Sack", material.Texture.Name);
        Assert.Equal("144ED98885798C31DDC0659AB2F2B51C7F3CDECC24358612CE6A4689413979F1", HashBytes(geometry.Geometry.Colours!));
        var localDictionary = ReadDictionary(local, 264, 40, EmptyDictionaryHash);
        Assert.Empty(localDictionary.Rasters);
        Assert.Equal(RwPspTextureCandidateStatus.Missing, localDictionary.FindExactName(material.Texture.Name).Status);

        // The other scope is selected explicitly; its existence must not change the first result.
        var externalDictionary = ReadDictionary(external, 264, 2360, ClothDictionaryHash);
        Assert.NotEqual(localDictionary.Origin.EntryOrdinal, externalDictionary.Origin.EntryOrdinal);
        Assert.Equal(localDictionary.Origin.SourceIdentity, externalDictionary.Origin.SourceIdentity);
        AssertRaster(externalDictionary, ClothRaster);
        var after = localDictionary.FindExactName(material.Texture.Name);
        Assert.Equal(RwPspTextureCandidateStatus.Missing, after.Status);
        Assert.Empty(after.Occurrences);
        Assert.Equal(EmptyDictionaryHash, HashBytes(localDictionary.Source.Span));
        Assert.Equal(ClothDictionaryHash, HashBytes(externalDictionary.Source.Span));
        Assert.Equal("185BA620907356519E6363C1348CA74F68C4B30CCFF1AEFA252C2816778D7F2F", HashBytes(clump.Source.Span));
        Assert.Equal(Goodies.Hash, HashBytes(local.Bytes));
        Assert.Equal(ClothSack.Hash, HashBytes(external.Bytes));
    }

    private static RwClumpGeometry AssertWeapons(ClumpPin pin)
    {
        var entry = ReadEntry(Weapons);
        var clump = ReadClump(entry, pin.Ordinal, pin.Offset, pin.Length, pin.Hash);
        var dictionary = ReadDictionary(entry, 236, 760296, WeaponsDictionaryHash);
        var mesh = Assert.Single(clump.Geometries);
        Assert.Equal(pin.Flags, mesh.Geometry.Flags);
        Assert.Equal(pin.Vertices, mesh.Geometry.Positions.Length);
        Assert.Equal(pin.Bindings.Length, mesh.Materials.Count);
        Assert.Equal(pin.Bindings.Length, mesh.MaterialMap.Count);
        Assert.All(mesh.MaterialMap, static reference => Assert.Equal(-1, reference));
        Assert.Equal(pin.Bindings.Sum(static binding => binding.Triangles), mesh.Geometry.Triangles.Length);
        AssertAttributes(mesh.Geometry, pin);
        for (var slot = 0; slot < pin.Bindings.Length; slot++)
        {
            var expected = pin.Bindings[slot];
            var material = mesh.Materials[slot];
            Assert.Equal(expected.Triangles, mesh.Geometry.Triangles.Count(triangle => triangle.MaterialIndex == slot));
            Assert.Equal(expected.Color, material.ColorRgba);
            Assert.Equal((0u, 1f, 1f, 1f), (material.Flags, material.Ambient, material.Specular, material.Diffuse));
            if (expected.Raster is null)
            {
                Assert.Equal(0u, material.Textured);
                Assert.Null(material.Texture);
                continue;
            }
            Assert.Equal(1u, material.Textured);
            Assert.NotNull(material.Texture);
            Assert.Equal(expected.Raster.Name, material.Texture.Name);
            Assert.Empty(material.Texture.MaskName);
            Assert.Equal(0x11102u, material.Texture.Sampler);
            AssertRaster(dictionary, expected.Raster);
        }
        // Opaque semantics are still reported; a unique raster is not material or rendering admission.
        Assert.NotEmpty(clump.Diagnostics);
        AssertAttributes(mesh.Geometry, pin);
        Assert.Equal(pin.Hash, HashBytes(clump.Source.Span));
        Assert.Equal(WeaponsDictionaryHash, HashBytes(dictionary.Source.Span));
        Assert.Equal(Weapons.Hash, HashBytes(entry.Bytes));
        return mesh;
    }

    private static void AssertAttributes(RwGeometry geometry, ClumpPin pin)
    {
        if (pin.ColorHash is null)
        {
            Assert.Null(geometry.Colours);
        }
        else
        {
            Assert.NotNull(geometry.Colours);
            var colors = geometry.Colours;
            Assert.Equal(pin.Vertices * 4, colors.Length);
            Assert.Equal(pin.ColorHash, HashBytes(colors));
            Assert.Equal(pin.UniqueColors, Enumerable.Range(0, pin.Vertices)
                .Select(vertex => BinaryPrimitives.ReadUInt32LittleEndian(colors.AsSpan(vertex * 4, 4))).Distinct().Count());
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var uv in Assert.Single(geometry.UvSets))
            {
                writer.Write(uv.X);
                writer.Write(uv.Y);
            }
        }
        Assert.Equal(pin.UvHash, HashBytes(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void AssertRaster(RwPspTextureDictionary dictionary, RasterPin pin)
    {
        var candidates = dictionary.FindExactName(pin.Name);
        Assert.Equal(RwPspTextureCandidateStatus.Unique, candidates.Status);
        var raster = Assert.Single(candidates.Occurrences);
        Assert.Same(dictionary.Rasters[pin.Ordinal], raster);
        Assert.Equal(pin.Ordinal, raster.Ordinal);
        Assert.Equal(dictionary.Origin, raster.Origin);
        Assert.Equal(pin.BodyOffset, dictionary.Origin.PayloadOffset + raster.Structure.HeaderOffset + RwChunk.HeaderLength);
        Assert.Equal(pin.BodyHash, HashBytes(raster.Structure.Payload.Span));
        // This raw native word differs from 0x11102 in CLUMP. Neither is interpreted as normalized sampling.
        Assert.Equal(0x1102u, BinaryPrimitives.ReadUInt32LittleEndian(raster.Structure.Payload.Span[0x68..]));
        var texture = raster.TryDecode(128 * 1024, out var error);
        Assert.Null(error);
        Assert.NotNull(texture);
        Assert.Equal(pin.Format, texture.Format);
        var mip = Assert.Single(texture.MipLevels);
        Assert.Equal(1, texture.MipCount);
        Assert.Equal((pin.Width, pin.Height), (mip.Width, mip.Height));
        Assert.Equal(pin.PixelHash, HashBytes(mip.Pixels));
        for (var pixel = 3; pixel < mip.Pixels.Length; pixel += 4)
        {
            Assert.Equal(byte.MaxValue, mip.Pixels[pixel]);
        }
        var forwarded = Assert.Single(texture.ToDecodedTexture().MipLevels);
        Assert.Equal((mip.Width, mip.Height), (forwarded.Width, forwarded.Height));
        Assert.Equal(pin.PixelHash, HashBytes(forwarded.Pixels));
        Assert.Equal(pin.BodyHash, HashBytes(raster.Structure.Payload.Span));
    }

    private static EntryData ReadEntry(EntryPin pin)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var build = RealAssetPaths.Travels.OblivionPspBuild("2007-4-27");
        Assert.SkipWhen(build is null, RealAssetPaths.SkipMessage("Oblivion PSP 2007-4-27 material bindings"));
        Assert.NotNull(build);
        var path = Path.Combine(build, "PSP_GAME", "USRDIR", "GR.ARC");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(39070668L, stream.Length);
        Span<byte> header = stackalloc byte[20];
        stream.ReadExactly(header);
        Assert.True(header[..4].SequenceEqual("A2.0"u8));
        Assert.InRange(BinaryPrimitives.ReadUInt32LittleEndian(header[4..]), 1u, 256u);
        Assert.InRange(BinaryPrimitives.ReadUInt32LittleEndian(header[16..]), 1u, 16u * 1024);
        var archive = OblivionPspArchive.Parse(path);
        Assert.True(archive.IsTagged);
        Assert.Equal(87, archive.Entries.Count);
        var entry = archive.Entries[pin.Ordinal];
        Assert.Equal(pin.Name, entry.Name);
        Assert.Equal((pin.Offset, (long)pin.Length), (entry.Offset, entry.Size));
        Assert.InRange(entry.Size, 1L, MaximumEntryBytes);
        var bytes = new byte[checked((int)entry.Size)];
        stream.Position = entry.Offset;
        stream.ReadExactly(bytes);
        Assert.Equal(pin.Hash, HashBytes(bytes));
        return new EntryData(path, pin, bytes, OblivionPspResourceReader.ReadResources(bytes));
    }

    private static RwClump ReadClump(EntryData entry, int ordinal, int offset, int length, string hash)
    {
        var resource = entry.Resources[ordinal];
        Assert.Equal("rwID_CLUMP", resource.TypeName);
        Assert.Equal(offset, resource.PayloadOffset);
        // These original wrappers pad the complete CLUMP to four bytes with 0x58; padding is not part of the root.
        Assert.Equal((length + 3) & ~3, resource.PayloadLength);
        Assert.True(RwChunk.TryRead(entry.Bytes, offset, offset + resource.PayloadLength, out var root));
        Assert.Equal(RwChunk.Clump, root.Type);
        Assert.Equal(offset + length, root.End);
        for (var padding = length; padding < resource.PayloadLength; padding++)
        {
            Assert.Equal((byte)0x58, entry.Bytes[offset + padding]);
        }
        var source = entry.Bytes.AsSpan(offset, length);
        Assert.Equal(hash, HashBytes(source));
        var clump = RwClumpReader.TryRead(source, out var error);
        Assert.Null(error);
        Assert.NotNull(clump);
        Assert.Equal(hash, HashBytes(source));
        Assert.Equal(hash, HashBytes(clump.Source.Span));
        return clump;
    }

    private static RwPspTextureDictionary ReadDictionary(EntryData entry, int offset, int length, string hash)
    {
        var resource = entry.Resources[0];
        Assert.Equal("rwID_TEXDICTIONARY", resource.TypeName);
        Assert.Equal((offset, length), (resource.PayloadOffset, resource.PayloadLength));
        var origin = new RwPspTextureDictionaryOrigin(entry.Path, entry.Pin.Ordinal, entry.Pin.Name,
            entry.Pin.Offset, entry.Pin.Length, 0, resource.PayloadOffset, resource.AuthoringPath);
        var source = entry.Bytes.AsSpan(offset, length);
        Assert.Equal(hash, HashBytes(source));
        var dictionary = RwPspTextureDictionaryReader.TryRead(source, origin, MaximumEntryBytes, out var error);
        Assert.Null(error);
        Assert.NotNull(dictionary);
        Assert.Equal(origin, dictionary.Origin);
        Assert.Equal(hash, HashBytes(source));
        Assert.Equal(hash, HashBytes(dictionary.Source.Span));
        return dictionary;
    }

    private static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record EntryPin(int Ordinal, string Name, long Offset, int Length, string Hash);
    private sealed record EntryData(string Path, EntryPin Pin, byte[] Bytes, IReadOnlyList<OblivionPspResource> Resources);
    private sealed record RasterPin(string Name, int Ordinal, int BodyOffset, string BodyHash, int Width, int Height,
        string PixelHash, RwPspPixelFormat Format = RwPspPixelFormat.Indexed4);
    private sealed record BindingPin(RasterPin? Raster, int Triangles, uint Color = uint.MaxValue);
    private sealed record ClumpPin(int Ordinal, int Offset, int Length, string Hash, uint Flags, int Vertices,
        string? ColorHash, int UniqueColors, string UvHash, BindingPin[] Bindings);
}
