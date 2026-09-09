using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Png;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of a retail Battlespire save slot against oracles
///     OUTSIDE the save: the character the user described, the level's BS6, TXT.BSA's item tables
///     and conversation files, and UESP's lists.
///     <para>
///         Structural assertions run on ANY slot. The exact pins (record census, item counts, the
///         name "Biggus Dickus" and its attribute octet) are for the SAVE0 written 2026-09-07 00:42
///         — 373,887-byte SAVETREE.DAT, save name "bd1" — and skip on any other save, so a
///         re-saved slot reports skipped rather than a false failure.
///     </para>
///     <para>
///         ⚠ Depends on the user having PLAYED: every test skips when no slot holds a SAVETREE.DAT.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BattlespireSaveRetailTests
{
    private const int FixtureTreeLength = 373_887;
    private const string FixtureSaveName = "bd1";
    private static readonly char[] TxtQuotes = ['"', '', '', '“', '”', ' '];

    private static string RequireSlot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var slot = RealAssetPaths.Classics.BattlespireSaveSlot();
        Assert.SkipWhen(slot is null,
            RealAssetPaths.SkipMessage("a Battlespire save slot (SAVE0..SAVE9 beside GAMEDATA)"));
        return slot;
    }

    private static string RequireGameData()
    {
        var root = RealAssetPaths.Classics.Battlespire();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Battlespire (GAMEDATA)"));
        return root;
    }

    private static string RequireArchive(string name)
    {
        var path = Path.Combine(RequireGameData(), name);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(name));
        return path;
    }

    private static bool IsFixture(string directory)
    {
        var tree = new FileInfo(Path.Combine(directory, BattlespireSaveSlot.TreeFileName));
        var namePath = Path.Combine(directory, BattlespireSaveSlot.NameFileName);
        return tree.Length == FixtureTreeLength
               && File.Exists(namePath)
               && BattlespireSaveSlot.ReadSaveName(File.ReadAllBytes(namePath), "SAVENAME.DAT") == FixtureSaveName;
    }

    private static void SkipUnlessFixture(string directory)
    {
        Assert.SkipWhen(!IsFixture(directory),
            $"{directory} is not the 2026-09-07 '{FixtureSaveName}' fixture the exact pins were measured on.");
    }

    private static BattlespireSaveVars RequireVars(BattlespireSaveSlot slot)
    {
        Assert.SkipWhen(slot.Vars is null, RealAssetPaths.SkipMessage("SAVEVARS.DAT"));
        return slot.Vars!;
    }

    private static Bs6File LoadLevel(BattlespireSaveVars vars)
    {
        using var archive = ArchiveReader.Open(RequireArchive("BS6.BSA"));
        var name = $"L{vars.CurrentLevel}.BS6";
        var bytes = archive.ReadFile(name);
        Assert.SkipWhen(bytes is null, RealAssetPaths.SkipMessage(name));
        return Bs6File.Parse(bytes, name);
    }

    [Fact]
    public void TheTreeTilesExactlyIntoUespsRecordSizes()
    {
        var directory = RequireSlot();
        var tree = BattlespireSaveTree.Parse(
            File.ReadAllBytes(Path.Combine(directory, BattlespireSaveSlot.TreeFileName)), "SAVETREE.DAT");

        Assert.Equal(BattlespireSaveTree.ExpectedVersion, tree.Version);
        Assert.NotEmpty(tree.Records);

        // Every documented type at exactly UESP's size (the Automap may vary on interiors, so it
        // alone is allowed to differ), and no two records share an id.
        Assert.DoesNotContain(tree.SizeMismatches(), r => r.Type != BattlespireSaveRecordType.Automap);
        Assert.Equal(tree.Records.Count, tree.Records.Select(r => r.RecordId).Distinct().Count());
        Assert.NotNull(tree.Player);
        Assert.Equal(BattlespireSaveRecord.PlayerRecordId, tree.Player!.RecordId);
        Assert.Equal(0u, tree.Player.ParentId);

        // ParentID grammar: the player and options carry 0, every Object sits on the map (1).
        Assert.All(tree.Records.Where(r => r.Type == BattlespireSaveRecordType.Object),
            r => Assert.Equal(BattlespireSaveRecord.MapParentId, r.ParentId));

        SkipUnlessFixture(directory);
        Assert.Equal(557, tree.Records.Count);
        Assert.Equal<(byte, int)[]>(
            [(0, 1), (2, 323), (3, 1), (6, 131), (9, 2), (18, 97), (23, 1), (51, 1)],
            [.. tree.Census().Select(kv => (kv.Key, kv.Value))]);
        Assert.Equal(373_700, tree.TrailerOffset);
        Assert.Equal(187, tree.Trailer.Length);

        // The trailer is undecoded; what is pinned is that it is zeros with five 0x02 bytes.
        var trailer = tree.Trailer.ToArray();
        Assert.Equal<int[]>([10, 27, 97, 104, 160],
            [.. Enumerable.Range(0, trailer.Length).Where(i => trailer[i] != 0)]);
        Assert.All(trailer.Where(b => b != 0), b => Assert.Equal(2, b));

        // ⚠ Header +37 (Unknown25) and +45 (Unknown2D) are not clocks. Counted from the raw file:
        // +37 is non-zero on 8 records and reaches 2,000,001,074; +45 on 3, reaching 8,707,024.
        // The PAIR's maximum is the former — 8,707,024 is +45's alone, which the doc comment used
        // to quote as the pair's. Both overshoot the save clock (1,169,900), which is the point.
        Assert.Equal(8, tree.Records.Count(r => r.Unknown25 != 0));
        Assert.Equal(2_000_001_074u, tree.Records.Max(r => r.Unknown25));
        Assert.Equal(3, tree.Records.Count(r => r.Unknown2D != 0));
        Assert.Equal(8_707_024u, tree.Records.Max(r => r.Unknown2D));

        // The type-0 record is a spell-shaped tombstone carried by the player.
        var tombstone = Assert.Single(tree.Records, r => r.Type == BattlespireSaveRecordType.Tombstone);
        Assert.Equal(184, tombstone.TotalLength);
        Assert.Equal(BattlespireSaveRecord.PlayerRecordId, tombstone.ParentId);
        Assert.Equal("Cure Health", tombstone.AsSpell()!.Name);
        Assert.Equal(0, tombstone.Flags);
    }

    [Fact]
    public void ThePlayerCarriesTheStatedNameAndAttributes()
    {
        var directory = RequireSlot();
        SkipUnlessFixture(directory);
        var slot = BattlespireSaveSlot.Load(directory);
        var vars = RequireVars(slot);
        var player = slot.Tree.Player!.AsCharacter()!;

        // ⚑ THE ORACLES ARE OUTSIDE THE FILE: the user stated the name and the attribute octet.
        Assert.Equal("Biggus Dickus", player.Name);
        Assert.Equal<uint[]>([67, 50, 35, 40, 52, 55, 50, 75], [.. player.Attributes]);
        Assert.Equal(player.Attributes, player.BaseAttributes);
        Assert.Equal(("Monk", "Monk"), (player.ClassName, player.BaseClassName));
        Assert.Equal("Breton", player.RaceName);
        Assert.Equal(1u, player.Level);
        Assert.Equal((6, 70, 70), (player.Wounds, player.WoundsMax, player.WoundsBase));
        Assert.Equal((2, 50, 50), (player.SpellPoints, player.SpellPointsMax, player.SpellPointsBase));
        Assert.Equal("Cure Health", BattlespireSaveLists.StoredSpellName(player.Hotkeys[0]));
        Assert.Equal(-2, player.CombatState);
        Assert.False(player.IsDead);

        // ⚠⚠ +803 is a MONSTER field. The player's word is 0xED07ED00 (bytes FF 80 00 00 ED 07 ED 07
        // at +800..+807), which as a SAVEVARID would be StaticEnemy ordinal 3,976,719,615 — while the
        // 97 monsters carry exactly 0..56. So it is exposed raw and SaveVarId is null on a player.
        Assert.Equal(BattlespireSaveCharacterKind.Player, player.Kind);
        Assert.Equal(0xED07ED00u, player.SaveVarWord);
        Assert.Null(player.SaveVarId);

        // The game's own message corroborates the wounds maximum.
        Assert.Contains(vars.LogMessages, m => m.EndsWith("22 out of 70.", StringComparison.Ordinal));

        // SAVEVARS bytes 0..790 equal the tree player body byte for byte. ⛔ This does NOT settle
        // UESP's 787 against 791: body bytes 743..790 are zero and SAVEVARS 791..1050 is a zero gap,
        // so any copy length in 743..1,051 passes this assertion identically. The assertion is still
        // a real one (it fails if any byte of the copy differs) — it just cannot decide the length.
        Assert.True(vars.RawBytes[..BattlespireSaveCharacter.BodyLength]
            .SequenceEqual(slot.Tree.Player.Body.Span[..BattlespireSaveCharacter.BodyLength]));
        Assert.Equal(player.Name, vars.Player.Name);
        Assert.Equal(player.Attributes, vars.Player.Attributes);
        Assert.Equal(FixtureSaveName, slot.SaveName);
    }

    [Fact]
    public void ItemIdsNameTheStoredItems()
    {
        var directory = RequireSlot();
        var tree = BattlespireSaveSlot.Load(directory).Tree;
        var items = tree.Records.Where(r => r.Type == BattlespireSaveRecordType.Item)
            .Select(r => (Record: r, Item: r.AsItem()!)).ToList();
        Assert.NotEmpty(items);

        // ⚑ UESP's ItemList names the stored item in one of three ways: equal outright (275), the
        // SINGULAR of a plural entry (31 — Greave/Greaves, Gauntlet/Gauntlets, Pauldron/Pauldrons,
        // Boot/Boots), or Clothes (19), whose name is the BSI sub-type (17). ⚠ The StartsWith clause
        // below is LOAD-BEARING for those 31 records, not defensive slack — the partition is pinned
        // after the fixture gate. IsContainer is set on sacks and chests and on nothing else.
        string[] clothes = ["Arm Bands", "Shirt", "Pants", "Cape"];
        foreach (var (_, item) in items)
        {
            if (item.ItemId == 19)
            {
                Assert.Contains(item.Name, clothes);
            }
            else
            {
                Assert.NotNull(item.ItemTypeName);
                Assert.True(
                    item.ItemTypeName == item.Name ||
                    item.ItemTypeName!.StartsWith(item.Name, StringComparison.Ordinal),
                    $"item id {item.ItemId} ({item.ItemTypeName}) stored as '{item.RawName}'");
            }

            Assert.Equal(item.ItemId is 16 or 17 or 18, item.IsContainer);
        }

        SkipUnlessFixture(directory);
        Assert.Equal(323, items.Count);

        // The three-way partition, measured word for word on this fixture: 275 + 31 + 17 = 323.
        // Falsifier: if the singular rule were slack rather than real, the middle bucket would be 0.
        var exact = items.Count(i => i.Item.ItemTypeName == i.Item.Name);
        var singular = items.Where(i => i.Item.ItemTypeName != i.Item.Name && i.Item.ItemId != 19)
            .Select(i => (i.Item.Name, i.Item.ItemTypeName)).ToList();
        Assert.Equal((275, 31, 17), (exact, singular.Count, items.Count(i => i.Item.ItemId == 19)));
        Assert.Equal<(string, string)[]>(
            [("Boot", "Boots"), ("Gauntlet", "Gauntlets"), ("Greave", "Greaves"), ("Pauldron", "Pauldrons")],
            [
                .. singular.Distinct().OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => (p.Name, p.ItemTypeName!))
            ]);
        Assert.Equal<(string, int)[]>([("Boot", 6), ("Gauntlet", 8), ("Greave", 10), ("Pauldron", 7)],
        [
            .. singular.GroupBy(p => p.Name).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Count()))
        ]);

        var census = items.GroupBy(i => i.Item.ItemTypeName).ToDictionary(g => g.Key!, g => g.Count());
        Assert.Equal((78, 73, 24, 18), (census["Sack"], census["Sigil Amulet"], census["Potion"], census["Arrow"]));
        Assert.Equal(59,
            items.Count(i => i.Item.RawName.EndsWith(BattlespireSaveItem.BsiNameSuffix, StringComparison.Ordinal)));

        var carried = items.Where(i => i.Record.ParentId == BattlespireSaveRecord.PlayerRecordId).ToList();
        Assert.Equal<string[]>(["Potion", "Sigil Amulet", "Arm Bands", "Shirt", "Pants"],
            [.. carried.Select(i => i.Item.Name)]);
        var amulet = carried.Single(i => i.Item.Name == "Sigil Amulet").Item;
        Assert.Equal((9004u, "Doht Sigil of Entry"), (amulet.EnchantmentId, amulet.EnchantmentName));
        Assert.Equal("MSHRT206", carried.Single(i => i.Item.Name == "Shirt").Item.FileName);
    }

    [Fact]
    public void EnchantmentIdsResolveInTxtBsaWithMatchingNames()
    {
        var directory = RequireSlot();
        var tree = BattlespireSaveSlot.Load(directory).Tree;

        // ⚑ The oracle is TXT.BSA: an enchantment id is the ID# of a record in MG0_GEN/MG2_SPC and
        // the save's own enchantment name must equal that record's Name.
        var table = new Dictionary<int, string>();
        using (var archive = ArchiveReader.Open(RequireArchive("TXT.BSA")))
        {
            foreach (var file in new[] { "MG0_GEN.TXT", "MG2_SPC.TXT" })
            {
                var bytes = archive.ReadFile(file);
                Assert.SkipWhen(bytes is null, RealAssetPaths.SkipMessage(file));
                foreach (var entry in BattlespireItemTable.Parse(Encoding.Latin1.GetString(bytes)))
                {
                    if (entry.Id is { } id)
                    {
                        table[id] = entry.Name.Trim(TxtQuotes);
                    }
                }
            }
        }

        Assert.NotEmpty(table);
        var enchanted = tree.Records.Where(r => r.Type == BattlespireSaveRecordType.Item)
            .Select(r => r.AsItem()!).Where(i => i.IsEnchanted).ToList();
        Assert.NotEmpty(enchanted);

        foreach (var item in enchanted)
        {
            Assert.True(table.TryGetValue((int)item.EnchantmentId, out var expected),
                $"enchantment {item.EnchantmentId} is not in TXT.BSA");
            Assert.Equal(expected, item.EnchantmentName);
        }

        SkipUnlessFixture(directory);
        Assert.Equal(105, enchanted.Count);
    }

    [Fact]
    public void MonsterTypesNameTheMonstersAndSaveVarIdsAreOrdinalPlusOne()
    {
        var directory = RequireSlot();
        var slot = BattlespireSaveSlot.Load(directory);
        var vars = RequireVars(slot);
        var monsters = slot.Tree.Records.Where(r => r.Type == BattlespireSaveRecordType.Monster)
            .Select(r => (Record: r, Body: r.AsCharacter()!)).ToList();
        Assert.NotEmpty(monsters);

        Assert.All(monsters, m => Assert.Equal(m.Body.Name, m.Body.EnemyTypeName));

        // ⚠ SAVEVARID = StaticEnemy ordinal + 1; 0 = no entry. Both halves must hold.
        var staticIds = vars.StaticEnemies.Records.Select(e => e.RecordId).ToHashSet();
        var linked = 0;
        foreach (var (record, body) in monsters)
        {
            if (body.StaticEnemyOrdinal is { } ordinal)
            {
                var entry = vars.StaticEnemies.Records[ordinal];
                Assert.Equal(record.RecordId, entry.RecordId);
                Assert.Equal((int)body.EnemyType, entry.EnemyListIndex);
                linked++;
            }
            else
            {
                Assert.DoesNotContain(record.RecordId, staticIds);
            }
        }

        Assert.Equal(vars.StaticEnemies.Records.Count, linked);

        SkipUnlessFixture(directory);
        Assert.Equal(97, monsters.Count);
        Assert.Equal<(string, int)[]>([("Scamp", 41), ("Vermai", 42), ("Dremora", 14)],
        [
            .. monsters.GroupBy(m => m.Body.Name).OrderBy(g => g.First().Body.EnemyType).Select(g => (g.Key, g.Count()))
        ]);
        Assert.Equal(56, linked);
        Assert.Equal(1, monsters.Count(m => m.Body.IsDead));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "S1244",
        Justification = "Compares unchanged serialized placement coordinates exactly; tolerances would hide altered fixture values.")]
    public void ObjectsAreTheLevelsBs6PlacementsInRawUnits()
    {
        var directory = RequireSlot();
        var slot = BattlespireSaveSlot.Load(directory);
        var vars = RequireVars(slot);
        var level = LoadLevel(vars);
        var byId = level.Objects.ToDictionary(o => o.Id);

        // ⚑ THE ORACLE IS THE LEVEL FILE: a level-id Object's RecordID is an OBJD.IDNB, its FileID
        // that placement's IDFI (mesh index), and its header XYZ the RAW POSI integers — not /256.
        var objects = slot.Tree.Records.Where(r => r.Type == BattlespireSaveRecordType.Object).ToList();
        Assert.NotEmpty(objects);
        var levelIds = 0;
        var meshMatches = 0;
        var positionMatches = 0;
        var angleMatches = 0;
        foreach (var record in objects.Where(r => r.HasLevelId))
        {
            Assert.True(byId.TryGetValue((int)record.RecordId, out var placed),
                $"object 0x{record.RecordId:X} is not an L{vars.CurrentLevel} IDNB");
            levelIds++;
            if ((int)record.FileId == placed.MeshIndex)
            {
                meshMatches++;
            }

            if (record.X == placed.Position.X && record.Y == placed.Position.Y && record.Z == placed.Position.Z)
            {
                positionMatches++;
            }

            if (record.Pitch == (placed.Angles.X & 0xFFFF) && record.Yaw == (placed.Angles.Y & 0xFFFF) &&
                record.Roll == (placed.Angles.Z & 0xFFFF))
            {
                angleMatches++;
            }
        }

        Assert.Equal(levelIds, meshMatches);
        Assert.True(positionMatches > levelIds / 2, $"only {positionMatches}/{levelIds} raw positions match POSI");

        // Level-placed items obey the same rule.
        var placedItems = slot.Tree.Records.Where(r => r.Type == BattlespireSaveRecordType.Item && r.HasLevelId)
            .ToList();
        foreach (var record in placedItems)
        {
            Assert.True(byId.TryGetValue((int)record.RecordId, out var placed));
            Assert.Equal(placed.MeshIndex, (int)record.FileId);
            Assert.Equal((placed.Position.X, placed.Position.Y, placed.Position.Z),
                ((int)record.X, (int)record.Y, (int)record.Z));
        }

        SkipUnlessFixture(directory);
        Assert.Equal(1u, vars.CurrentLevel);
        Assert.Equal((131, 130, 130, 125, 126), (objects.Count, levelIds, meshMatches, positionMatches, angleMatches));
        Assert.Equal(46, placedItems.Count);
    }

    [Fact]
    public void SaveVarsCountWordsEqualPopulatedSlotsAndIdsResolve()
    {
        var directory = RequireSlot();
        var slot = BattlespireSaveSlot.Load(directory);
        var vars = RequireVars(slot);

        // ⚑ What discriminates the block layout: every count word equals its block's non-zero slots.
        Assert.True(vars.ConversationMap.CountsAgree, "ConversationMap");
        Assert.True(vars.StaticEnemies.CountsAgree, "StaticEnemy");
        Assert.True(vars.HpSpModifiers.CountsAgree, "HP/SPModify");
        Assert.True(vars.Sigils.CountsAgree, "Sigil");
        Assert.True(vars.GlobalVariables.CountsAgree, "GlobalVariable");
        Assert.Equal(0, vars.Block5.Span.IndexOfAnyExcept((byte)0) >= 0 ? 1 : 0);

        // Every global hash is in UESP's list, and the gender globals agree with the character.
        Assert.All(vars.GlobalVariables.Records, v => Assert.NotNull(v.Name));
        var globals = vars.GlobalVariables.Records.ToDictionary(v => v.Name!, v => v.Value);
        Assert.Equal((1u, 0u), (globals["PCMale"], globals["PCFemale"]));

        // The clock is at or after every logged message. ⚠⚠ Only the LEADING run of the 534-word
        // region is timestamps: the region also holds 1,169,900 / 127 / 127 / 0x271000 at index
        // 16..19, and 0x271000 = 2,560,000 is PAST the clock, so reading all 534 as timestamps
        // (which this reader used to do) makes this assertion false on retail.
        Assert.NotEmpty(vars.LogTimestamps);
        Assert.True(vars.CurrentTimestamp >= vars.LogTimestamps.Max());
        Assert.Equal(BattlespireSaveVars.LogTimestampCount, vars.LogTimestampWords.Count);

        // Conversation stems name files in TXT.BSA (<stem>B/M/F/T.TXT).
        using (var archive = ArchiveReader.Open(RequireArchive("TXT.BSA")))
        {
            var names = archive.ListFiles().Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var conversation in vars.ConversationMap.Records)
            {
                Assert.Contains(new[] { "B", "M", "F", "T" },
                    suffix => names.Contains(conversation.Stem + suffix + ".TXT"));
            }
        }

        SkipUnlessFixture(directory);
        Assert.Equal((21, 56, 7, 3, 26), (
            vars.ConversationMap.DeclaredCount, vars.StaticEnemies.DeclaredCount, vars.HpSpModifiers.DeclaredCount,
            vars.Sigils.DeclaredCount, vars.GlobalVariables.DeclaredCount));
        Assert.Equal((1u, 1_169_900u), (vars.CurrentLevel, vars.CurrentTimestamp));
        Assert.Equal<uint[]>([0, 0, 4, 4], [.. vars.MiscUnknown]);
        Assert.Equal(3, vars.MonsterTypeCounts[0]);
        Assert.Equal(15, vars.LogMessages.Count);

        // ⚠ 15 log runs but only 5 non-zero leading words — the region is not a parallel array.
        // ⛔ Those five are NOT ordered: [1] ties [2] and [3] (1,117,955) is BELOW [4] (1,120,830),
        // which is why no "newest first" rule is claimed or asserted. Words 16..19 are the
        // undecoded quartet, pinned as they read.
        Assert.Equal<uint[]>([1_166_615, 1_146_280, 1_146_280, 1_117_955, 1_120_830], [.. vars.LogTimestamps]);
        Assert.False(vars.LogTimestamps.Zip(vars.LogTimestamps.Skip(1)).All(p => p.First >= p.Second));
        Assert.Equal<uint[]>([1_169_900, 127, 127, 0x0027_1000], [.. vars.LogTimestampWords.Skip(16).Take(4)]);
        Assert.Equal(9, vars.LogTimestampWords.Count(w => w != 0));
        Assert.Equal<int[]>([126, 127], [.. vars.LocalVariables.Select(l => l.Slot)]);
        Assert.All(vars.LocalVariables,
            l => Assert.Equal<string?[]>(["SKNoTalk", "ScampMad"], [.. l.Variables.Select(v => v.Name)]));
        Assert.Equal<(uint, int)[]>([(0x128, -192), (0x12B, -491), (0x12C, -480)],
            [.. vars.Sigils.Records.Select(s => (s.RecordId, s.Y))]);
    }

    [Fact]
    public void SigilsAndHpSpRecordsPointAtLevelObjects()
    {
        var directory = RequireSlot();
        var slot = BattlespireSaveSlot.Load(directory);
        var vars = RequireVars(slot);
        var level = LoadLevel(vars);
        var byId = level.Objects.ToDictionary(o => o.Id);

        // ⚑ A sigil's Y is the placement's raw POSI.y and its mesh is a sigil; an HP/SP record
        // points at a gem (or the "1hurt" trap).
        Assert.NotEmpty(vars.Sigils.Records);
        foreach (var sigil in vars.Sigils.Records)
        {
            Assert.True(byId.TryGetValue((int)sigil.RecordId, out var placed));
            Assert.Equal(placed.Position.Y, sigil.Y);
            Assert.StartsWith("sigil", level.MeshNames[placed.MeshIndex], StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotEmpty(vars.HpSpModifiers.Records);
        foreach (var modifier in vars.HpSpModifiers.Records)
        {
            Assert.True(byId.TryGetValue((int)modifier.RecordId, out var placed));
            var mesh = level.MeshNames[placed.MeshIndex];
            Assert.True(
                mesh.StartsWith("gem", StringComparison.OrdinalIgnoreCase) ||
                mesh.Contains("hurt", StringComparison.OrdinalIgnoreCase), mesh);
            Assert.Equal(modifier.Value < 0, mesh.Contains("hurt", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheThumbnailIsX555AndTheSaveNameReads()
    {
        var directory = RequireSlot();
        var slot = BattlespireSaveSlot.Load(directory);
        Assert.SkipWhen(slot.Image is null, RealAssetPaths.SkipMessage("IMAGE.RAW"));
        var image = slot.Image!;

        // Bit 15 is never set (x555); a PNG of the right size comes out.
        Assert.All(image.Pixels, p => Assert.True(p < 0x8000));
        var info = PngImageDecoder.ReadInfo(image.EncodePng());
        Assert.NotNull(info);
        Assert.Equal((80, 50), (info.Value.Width, info.Value.Height));
        Assert.NotNull(slot.SaveName);

        SkipUnlessFixture(directory);
        Assert.Equal(0x10A4, image.PixelAt(0, 0));
        Assert.Equal((4, 5, 4), BattlespireSaveImage.Split(image.PixelAt(0, 0)));

        // ⚠ Bit 0 is set on 1,804 pixels — which refutes the HICL packing (bit 0 spare) that
        // BsiFile uses for its 15-bit colours; do not share that expansion.
        Assert.Equal(1_804, image.Pixels.Count(p => (p & 1) != 0));
        Assert.Equal(FixtureSaveName, slot.SaveName);

        // ⚠⚠ Channel ORDER (R in bits 10-14, not B) is settled by the SCENE. Counted from the raw
        // file in Python: the image is red-dominant overall, the dungeon floor of row 35 has no
        // blue-dominant pixel at all, and the brightest pixel is a warm lamp that a BGR reading
        // would turn into (14, 31, 31) cyan. Each of these flips under BGR, so the test can fail.
        var sums = image.Pixels.Select(BattlespireSaveImage.Split)
            .Aggregate((0, 0, 0), (a, c) => (a.Item1 + c.R, a.Item2 + c.G, a.Item3 + c.B));
        Assert.Equal((21_750, 21_133, 14_522), sums);
        Assert.Equal((31, 31, 14), BattlespireSaveImage.Split(image.PixelAt(75, 3)));
        Assert.Equal((76, 0), CountWarmCold(image, 35));

        // ⛔ The HUD strip is NOT the control: counted, its rows lean the other way and would
        // select BGR. Pinned so the refuted argument cannot quietly come back.
        Assert.Equal((23, 37), CountWarmCold(image, 45));
        Assert.Equal((12, 53), CountWarmCold(image, 49));
    }

    /// <summary>Pixels in one row with R &gt; B, and with B &gt; R, under the x555 split.</summary>
    private static (int Warm, int Cold) CountWarmCold(BattlespireSaveImage image, int row)
    {
        var warm = 0;
        var cold = 0;
        for (var x = 0; x < BattlespireSaveImage.Width; x++)
        {
            var (r, _, b) = BattlespireSaveImage.Split(image.PixelAt(x, row));
            if (r > b)
            {
                warm++;
            }
            else if (b > r)
            {
                cold++;
            }
        }

        return (warm, cold);
    }
}
