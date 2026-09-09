using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Opt-in checks of the retail Shadowkey N-Gage tree (<c>RUN_BUCKET_B=1</c>). Neither global
///     pack carries a magic number, so the only proof a reader is right is that its walk consumes
///     every byte of every entry — these tests therefore decode WHOLE records and pin the counts
///     measured on 2026-09-05, not signatures.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ShadowkeyPackRetailTests
{
    private static string RequireRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Shadowkey (system/apps/6R51)"));
        return root;
    }

    private static ShadowkeyModelPack LoadModelPack(string root)
    {
        return ShadowkeyModelPack.Parse(
            File.ReadAllBytes(Path.Combine(root, "models.idx")),
            File.ReadAllBytes(Path.Combine(root, "models.huge")),
            File.ReadAllText(Path.Combine(root, "models.txt")),
            "models.huge");
    }

    [Fact]
    public void ModelIndex_Tiles_ModelsHuge_Exactly()
    {
        var root = RequireRoot();

        var models = LoadModelPack(root);

        Assert.Equal(237, models.Count);
        Assert.Equal(11, models.Entries.Count(e => e.IsEmpty));
        Assert.Equal(
            [19, 24, 26, 28, 29, 54, 57, 133, 221, 227, 236],
            models.Entries.Where(e => e.IsEmpty).Select(e => e.Index));

        var last = models.Entries[^1];
        Assert.Equal(4_907_880u, last.Offset + last.Size);
        Assert.Equal(4_907_880, new FileInfo(Path.Combine(root, "models.huge")).Length);

        // models.txt line i names slot i — the pack has no names of its own.
        Assert.Equal("fern.bin", models.Entries[0].FileName);
        Assert.Equal("NULL_LEAVE_SOMETHING_HERE.bin", models.Entries[236].FileName);
    }

    [Fact]
    public void EveryMeshRecord_ParsesAndTilesItsEntry()
    {
        var root = RequireRoot();
        var models = LoadModelPack(root);

        var meshes = 0;
        var texels = 0;
        var sequences = 0;
        var skins = 0;
        var animated = 0;
        foreach (var entry in models.Entries)
        {
            var mesh = models.GetMesh(entry.Index);
            if (entry.IsEmpty)
            {
                Assert.Null(mesh);
                continue;
            }

            Assert.NotNull(mesh);
            meshes++;
            if (mesh.FrameCount > 1)
            {
                animated++;
            }

            foreach (var face in mesh.Faces)
            {
                Assert.True(face.V0 < mesh.VertexCount && face.V1 < mesh.VertexCount && face.V2 < mesh.VertexCount);
                Assert.True(face.T0 < mesh.Uvs.Count && face.T1 < mesh.Uvs.Count && face.T2 < mesh.Uvs.Count);
            }

            Assert.Equal(mesh.FrameCount * mesh.VertexCount, mesh.Vertices.Count);

            foreach (var skin in mesh.Textures.Skins)
            {
                Assert.Equal(mesh.Textures.Width * mesh.Textures.Height, skin.Length);
                foreach (var texel in skin)
                {
                    // 0x0RGB: the top nibble is unused on every retail texel.
                    Assert.Equal(0, texel >> 12);
                }

                texels += skin.Length;
                skins++;
            }

            foreach (var sequence in mesh.Sequences)
            {
                Assert.True(sequence.Start < sequence.EndExclusive);
                Assert.True(sequence.EndExclusive <= mesh.FrameCount);
            }

            Assert.Contains(mesh.Sequences, s => s.EndExclusive == mesh.FrameCount);
            sequences += mesh.Sequences.Count;
        }

        Assert.Equal(226, meshes);
        Assert.Equal(33, animated);
        Assert.Equal(319, skins);
        Assert.Equal(1_302_848, texels);
        Assert.Equal(402, sequences);
    }

    [Fact]
    public void MeshSkins_DecodeThroughTheirOwnFourFourFourPalette()
    {
        var root = RequireRoot();
        var models = LoadModelPack(root);

        var decoded = 0;
        var withMagenta = 0;
        foreach (var entry in models.Entries)
        {
            var mesh = models.GetMesh(entry.Index);
            if (mesh is null)
            {
                continue;
            }

            var image = mesh.DecodeSkin(0);
            Assert.Equal(mesh.Textures.Width, image.Bitmap.Width);
            Assert.Equal(mesh.Textures.Height, image.Bitmap.Height);
            decoded++;

            if (mesh.Textures.Skins.Any(s => s.Contains(ShadowkeyMesh.MagentaColourKey)))
            {
                withMagenta++;
            }
        }

        Assert.Equal(226, decoded);

        // No retail skin exceeds 256 distinct colours (the worst is 233), so an indexed decode
        // is always possible; 44 records carry the magenta colour key.
        Assert.Equal(44, withMagenta);
    }

    [Fact]
    public void SpritePack_SolvesTo384Slots_AndEveryBlobTiles()
    {
        var root = RequireRoot();
        var bytes = File.ReadAllBytes(Path.Combine(root, "global.spr"));

        var sprites = ShadowkeySpritePack.Parse(bytes, "global.spr");

        Assert.Equal(1_646_707, bytes.Length);
        Assert.Equal(384, sprites.Count);
        Assert.Equal(253, sprites.Sizes.Count(s => s != 0));
        Assert.Equal(253, sprites.Sizes.TakeWhile(s => s != 0).Count());

        var decoded = 0;
        var emptyRows = 0;
        var spanPixels = 0;
        var sizes = new HashSet<(int Width, int Height)>();
        for (var i = 0; i < sprites.Count; i++)
        {
            var sprite = sprites.GetSprite(i);
            if (sprite is null)
            {
                Assert.Equal(0u, sprites.Sizes[i]);
                continue;
            }

            var used = sprite.UsedPaletteLength;
            foreach (var row in sprite.Rows)
            {
                foreach (var index in row.Indices.Span)
                {
                    Assert.True(index < used, $"sprite {i} uses index {index} of {used} palette entries");
                }
            }

            emptyRows += sprite.EmptyRowCount;
            spanPixels += sprite.SpanPixelCount;
            sizes.Add((sprite.Width, sprite.Height));
            decoded++;
        }

        Assert.Equal(253, decoded);
        Assert.Equal(12_581, emptyRows);
        Assert.Equal(1_386_555, spanPixels);
        Assert.Contains((176, 208), sizes);
    }

    [Fact]
    public void Sprites_DecodeWithTheUnspannedPixelsTransparent()
    {
        var root = RequireRoot();
        var sprites = ShadowkeySpritePack.Parse(
            File.ReadAllBytes(Path.Combine(root, "global.spr")), "global.spr");

        var aliasing = 0;
        for (var i = 0; i < sprites.Count; i++)
        {
            var sprite = sprites.GetSprite(i);
            if (sprite is null)
            {
                continue;
            }

            var image = sprite.ToImage();
            Assert.Equal(sprite.Width * sprite.Height, image.Bitmap.Indices.Length);
            Assert.Equal(0, image.Palette.GetEntry(image.TransparentIndex).A);
            if (image.TransparentIndexAliasesPixels)
            {
                aliasing++;
            }
        }

        // Only two retail sprites use all 256 palette slots, leaving no spare for transparency.
        Assert.Equal(2, aliasing);
    }

    [Theory]
    [InlineData("StringTable.eng", "eng")]
    [InlineData("StringTable.euk", "euk")]
    [InlineData("StringTable.fre", "fre")]
    [InlineData("StringTable.ger", "ger")]
    [InlineData("StringTable.ita", "ita")]
    [InlineData("StringTable.spa", "spa")]
    public void EveryStringTable_TilesAndHolds4082Entries(string fileName, string language)
    {
        var root = RequireRoot();

        var table = ShadowkeyStringTable.Parse(
            File.ReadAllBytes(Path.Combine(root, fileName)), fileName);

        Assert.Equal(language, table.Language);
        Assert.Equal(ShadowkeyStringTable.RetailEntryCount, table.Strings.Count);
        Assert.All(
            table.Strings,
            s => Assert.True(s.IndexOf(char.MinValue) < 0, "an entry carries an embedded terminator"));
    }

    [Fact]
    public void EnglishStringTable_CarriesTheKnownIndices()
    {
        var root = RequireRoot();

        var table = ShadowkeyStringTable.Parse(
            File.ReadAllBytes(Path.Combine(root, "StringTable.eng")), "StringTable.eng");

        Assert.Equal(4082, table.Strings.Count);
        Assert.Equal("YOU ARE DEAD.", table.Strings[0]);
        Assert.Equal("KILLCAM", table.Strings[3]);
        Assert.Equal("Assassin", table.Strings[32]);
        Assert.Equal("Thief", table.Strings[40]);
        Assert.Equal("Bludgeon", table.Strings[2772]);
        Assert.Equal("Game terminated by the host ", table.Strings[4081]);
    }

    [Fact]
    public void ProductTable_Holds279RecordsNamingTheStringTable()
    {
        var root = RequireRoot();
        var strings = ShadowkeyStringTable.Parse(
            File.ReadAllBytes(Path.Combine(root, "StringTable.eng")), "StringTable.eng");

        var products = ShadowkeyProductTable.Parse(
            File.ReadAllBytes(Path.Combine(root, "products.dat")), "products.dat", strings.Strings.Count);

        Assert.Equal(ShadowkeyProductTable.RetailFormatTag, products.FormatTag);
        Assert.Equal(279, products.Products.Count);
        Assert.Equal(83, products.Products.Count(p => p.Type == ShadowkeyProductType.Weapon));
        Assert.Equal(99, products.Products.Count(p => p.Type == ShadowkeyProductType.Armor));
        Assert.Equal(38, products.Products.Count(p => p.Type == ShadowkeyProductType.Spell));
        Assert.Equal(58, products.Products.Count(p => p.Type == ShadowkeyProductType.Consumable));
        Assert.Equal(133, products.Products.Count(p => p.UsableBy == ShadowkeyClasses.All));
        Assert.All(products.Products, p => Assert.NotEqual(ShadowkeyClasses.None, p.UsableBy));

        // Ids are the entities.txt ids and rise strictly.
        Assert.Equal(50, products.Products[0].Id);
        Assert.Equal(4905, products.Products[^1].Id);
        Assert.All(
            products.Products.Zip(products.Products.Skip(1)),
            pair => Assert.True(pair.First.Id < pair.Second.Id));

        // Armour is the only type carrying a slot.
        Assert.All(
            products.Products.Where(p => p.Type != ShadowkeyProductType.Armor),
            p => Assert.Equal(0, p.ArmorSlot));
    }
}