using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Png;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of a retail Daggerfall save slot, against oracles
///     OUTSIDE the save: ARENA2's <c>CLASS??.CFG</c>, <c>SPELLS.STD</c> and <c>FACTION.TXT</c>, the
///     game's own calendar, and the standalone <c>AT&lt;LocationId&gt;.AMF</c> copy of the automap
///     record.
///     <para>
///         Structural assertions (exact tiling, trailer ownership, faction table) run on ANY slot.
///         The value pins — 158 records, "Hans" the level-1 Nord Monk, 12 rumours — are for the
///         SAVE0 measured 2026-09-07 (64,836-byte SAVETREE.DAT, save name "Hans") and skip on any
///         other save, so a re-saved slot reports skipped rather than a false failure.
///     </para>
///     <para>
///         ⚠ Depends on the user having PLAYED: Steam ships SAVE0..SAVE5 as autocloud stubs, so
///         every test skips when no slot holds a SAVETREE.DAT.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallSaveRetailTests
{
    private const int FixtureTreeLength = 64_836;
    private const string FixtureSaveName = "Hans";

    private static string RequireSlot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var slot = RealAssetPaths.Classics.DaggerfallSaveSlot();
        Assert.SkipWhen(slot is null,
            RealAssetPaths.SkipMessage("a Daggerfall save slot (SAVE0..SAVE5 beside ARENA2)"));
        return slot;
    }

    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root;
    }

    private static byte[] RequireArena2File(string name)
    {
        var path = Path.Combine(RequireArena2(), name);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(name));
        return File.ReadAllBytes(path);
    }

    private static DaggerfallSaveSlot LoadSlot()
    {
        return DaggerfallSaveSlot.Load(RequireSlot());
    }

    private static bool IsFixture(DaggerfallSaveSlot slot)
    {
        return slot.Tree.Length == FixtureTreeLength && slot.SaveName == FixtureSaveName;
    }

    private static DaggerfallSaveSlot RequireFixture()
    {
        var slot = LoadSlot();
        Assert.SkipUnless(IsFixture(slot),
            $"the measured SAVE0 ({FixtureTreeLength}-byte SAVETREE.DAT, save name \"{FixtureSaveName}\")");
        return slot;
    }

    /// <summary>
    ///     EXACT TILING: the walk consumes the whole file — header, records, separators and the
    ///     count-prefixed trailer — or <see cref="DaggerfallSaveTree.Parse" /> throws. This is the
    ///     assertion the reference cannot make: its "type 7 records are 39 x length" rule reads the
    ///     trailer's first entry as a record and its break-on-overrun then discards the rest.
    /// </summary>
    [Fact]
    public void SaveTree_TilesTheRetailFileExactly()
    {
        var slot = LoadSlot();
        var tree = slot.Tree;

        Assert.Equal(DaggerfallSaveTree.Version, tree.Header.Version);
        Assert.True(tree.HasTrailer, "a retail SAVETREE.DAT ends in the count-prefixed trailer");

        var consumed = DaggerfallSaveTree.HeaderLength
                       + 4 + tree.BuildingRecords.Count * DaggerfallSaveTree.BuildingRecordLength
                       + tree.Records.Sum(r => 4 + r.DeclaredLength)
                       + tree.SeparatorCount * 4
                       + 4 + tree.TrailerEntries.Count * DaggerfallSaveTree.TrailerEntryLength
                       + 4;
        Assert.Equal(tree.Length, consumed);
        Assert.Equal(0, tree.UnresolvedTrailerOwnerCount);
        Assert.All(tree.Records, r => Assert.True(r.DeclaredLength >= DaggerfallSaveTree.RecordRootLength));
    }

    /// <summary>
    ///     Every trailer entry is owned by a record whose root byte 35 is 0xFF, and those are ALL
    ///     of them — a two-way set equality that chance does not produce, and the evidence that
    ///     byte 35 is the flag rather than the trailer being some other list.
    /// </summary>
    [Fact]
    public void TrailerEntries_AreExactlyTheRecordsFlaggedInRootByte35()
    {
        var tree = LoadSlot().Tree;

        var flagged = tree.Records.Where(r => r.HasTrailerFlag).Select(r => r.RecordId).ToHashSet();
        var owners = tree.TrailerEntries.Select(e => e.OwnerId).ToHashSet();

        Assert.NotEmpty(owners);
        Assert.Equal(flagged, owners);
        Assert.All(tree.TrailerEntries, e => Assert.NotNull(e.Owner));
        Assert.All(tree.TrailerEntries, e => Assert.Equal((ushort)e.OwnerId, e.OwnerIdLow));
    }

    /// <summary>The measured census of the 2026-09-07 SAVE0.</summary>
    [Fact]
    public void SaveTree_MatchesTheMeasuredCensus()
    {
        var tree = RequireFixture().Tree;

        Assert.Equal(158, tree.Records.Count);
        Assert.Equal(2, tree.SeparatorCount);
        Assert.Equal(23, tree.TrailerEntries.Count);
        Assert.Equal(64_836, tree.Length);
        Assert.Equal(1, tree.DuplicateIdCount);
        Assert.Equal(124, tree.RootRecords.Count);
        Assert.Empty(tree.BuildingRecords);

        Assert.Equal(30, tree.Header.DungeonIndex);
        Assert.Equal(DaggerfallSaveEnvironment.Dungeon, tree.Header.EnvironmentKind);
        Assert.Equal(3_591_871, tree.Header.X);
        Assert.Equal(-256, tree.Header.Y);
        Assert.Equal(11_188_666, tree.Header.Z);

        Assert.Equal(15, tree.RecordsOfType(DaggerfallSaveRecordType.Item).Count());
        Assert.Equal(58, tree.RecordsOfType(DaggerfallSaveRecordType.Door).Count());
        Assert.Equal(40, tree.RecordsOfType(DaggerfallSaveRecordType.Marker).Count());
        Assert.Equal(8, tree.RecordsOfType(DaggerfallSaveRecordType.Container).Count());
        Assert.Equal(6, tree.RecordsOfType(DaggerfallSaveRecordType.Spell).Count());
        Assert.Equal(3, tree.RecordsOfType(DaggerfallSaveRecordType.Corpse).Count());

        // Record ids are (LocationId << 16) | serial for world objects; the whole census is
        // 125 + 17 + 11 + 3 + 1 + 1 = 158. ⛔ The EXTERIOR LocationId 50049 (0xC381) is on NO
        // record — the survey note that claimed one was wrong, and this counts every family so
        // that a stray would show up as an unaccounted record rather than pass unnoticed.
        Assert.Equal(125, tree.Records.Count(r => r.RecordIdHigh == 0xC382));
        Assert.Equal(17, tree.Records.Count(r => r.RecordIdHigh == 0x0001));
        Assert.Equal(11, tree.Records.Count(r => r.RecordIdHigh == 0x0064));
        Assert.Equal(3, tree.Records.Count(r => r.RecordIdHigh == 0x02BC));
        Assert.Single(tree.Records, r => r.RecordIdHigh == 0x5439);
        Assert.Single(tree.Records, r => r.RecordIdHigh == 0x5513);
        Assert.DoesNotContain(tree.Records, r => r.RecordIdHigh == 0xC381);
    }

    /// <summary>
    ///     The player's career block is byte-identical to one <c>CLASS??.CFG</c> — and to exactly
    ///     one: the runner-up matches at most 47 of the 74 bytes over all 19 files, so this cannot
    ///     pass by accident.
    /// </summary>
    [Fact]
    public void CharacterCareer_IsByteIdenticalToOneClassFile()
    {
        var slot = LoadSlot();
        var character = slot.Tree.Character;
        Assert.SkipWhen(character is null, "the slot's SAVETREE.DAT holds no Character record");

        var arena2 = RequireArena2();
        var matches = new List<(string File, int Same)>();
        foreach (var path in Directory.EnumerateFiles(arena2, "CLASS??.CFG"))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != DaggerfallCareer.Length)
            {
                continue;
            }

            var same = bytes.Where((b, i) => b == character.Career.Raw.Span[i]).Count();
            matches.Add((Path.GetFileName(path), same));
        }

        Assert.SkipWhen(matches.Count == 0, RealAssetPaths.SkipMessage("ARENA2 CLASS??.CFG files"));
        var ordered = matches.OrderByDescending(m => m.Same).ToList();
        Assert.Equal(DaggerfallCareer.Length, ordered[0].Same);
        Assert.True(ordered[1].Same < DaggerfallCareer.Length, $"a second class file also matched: {ordered[1].File}");
    }

    /// <summary>The fixture's character, pinned against SAVENAME.TXT and CLASS12.CFG.</summary>
    [Fact]
    public void Character_IsTheMeasuredHans()
    {
        var slot = RequireFixture();
        var character = slot.Tree.Character!;

        Assert.Equal("Hans", character.Name);
        Assert.Equal(slot.SaveName, character.Name);
        Assert.Equal("Nord", character.RaceName);
        Assert.Equal("Monk", character.Career.Name);
        Assert.Equal(1, character.Level);
        Assert.Equal(22, character.CurrentHealth);
        Assert.Equal(39, character.MaxHealth);
        Assert.Equal(102u, character.Gold);
        Assert.Equal(162, character.StartingLevelUpSkillSum);
        Assert.Equal(7_491, character.Fatigue);
        Assert.Equal(65, character.CurrentAttributes[0]);
        Assert.Equal(56, character.CurrentAttributes[4]);

        var careerFile = RequireArena2File("CLASS12.CFG");
        Assert.Equal(careerFile, character.Career.Raw.ToArray());
    }

    /// <summary>
    ///     Fatigue is stored in 1/64 units and cannot exceed (STR + END) x 64 — 7,491 against a
    ///     ceiling of 7,744 here. A byte-swapped or mis-offset read blows straight through it.
    /// </summary>
    [Fact]
    public void CharacterFatigue_FitsItsStrengthEnduranceCeiling()
    {
        var character = LoadSlot().Tree.Character;
        Assert.SkipWhen(character is null, "the slot's SAVETREE.DAT holds no Character record");

        var ceiling = (character.CurrentAttributes[0] + character.CurrentAttributes[4]) * 64;
        Assert.InRange(character.Fatigue, 0, ceiling);
    }

    /// <summary>
    ///     The career's slot ids applied to the character's own skill array reproduce the record's
    ///     stated <c>StartingLevelUpSkillSum</c>: the three primaries, the best two majors and the
    ///     best minor. This pins the 6-byte skill stride AND the career's slot layout at once —
    ///     a wrong stride gives a different sum.
    /// </summary>
    [Fact]
    public void StartingLevelUpSkillSum_IsThePrimariesTopTwoMajorsAndTopMinor()
    {
        var character = LoadSlot().Tree.Character;
        Assert.SkipWhen(character is null, "the slot's SAVETREE.DAT holds no Character record");

        int Value(byte skillId)
        {
            return character.Skills[skillId].Value;
        }

        var sum = character.Career.PrimarySkillIds.Sum(Value)
                  + character.Career.MajorSkillIds.Select(Value).OrderByDescending(v => v).Take(2).Sum()
                  + character.Career.MinorSkillIds.Select(Value).Max();

        Assert.Equal(character.StartingLevelUpSkillSum, sum);
    }

    /// <summary>
    ///     Every Spell record's 89-byte body is byte-identical to SOME <c>SPELLS.STD</c> record —
    ///     an oracle entirely outside the save, and one a wrong body length or a wrong root size
    ///     would break at once.
    ///     <para>
    ///         ⛔ The record is found by SEARCHING, not by indexing: <c>Index</c> is a spell id and
    ///         the id drifts away from the file position (10 -&gt; 9, 7 -&gt; 6, 29 -&gt; 27,
    ///         44 -&gt; 41), so the "position + 1" rule an earlier pass recorded is refuted here.
    ///     </para>
    /// </summary>
    [Fact]
    public void Spells_AreByteIdenticalToSpellsStdRecords()
    {
        var tree = LoadSlot().Tree;
        var std = RequireArena2File("SPELLS.STD");
        Assert.Equal(0, std.Length % DaggerfallSaveSpell.DataLength);
        var recordCount = std.Length / DaggerfallSaveSpell.DataLength;

        var spells = tree.Spells.ToList();
        Assert.NotEmpty(spells);
        var positions = new List<int>();
        foreach (var (record, spell) in spells)
        {
            var body = record.Data.Span[..DaggerfallSaveSpell.DataLength];
            var found = -1;
            for (var position = 0; position < recordCount && found < 0; position++)
            {
                if (body.SequenceEqual(std.AsSpan(position * DaggerfallSaveSpell.DataLength,
                        DaggerfallSaveSpell.DataLength)))
                {
                    found = position;
                }
            }

            Assert.True(found >= 0, $"spell \"{spell.Name}\" (id {spell.Index}) matches no SPELLS.STD record");
            positions.Add(found);
        }

        // ⚠ NOT "assert the found record's byte 73 equals Index" — byte-identity makes that true by
        // construction, so it could never fail. These six positions are a literal measured from the
        // retail bytes instead (sorted, so tree order does not enter into it); the ids beside them
        // are 7, 10, 10, 29, 44, 44, which is the drift SpellsStd_IdBytesDriftFromTheirPositions
        // pins from the other side.
        positions.Sort();
        Assert.Equal<int>([6, 9, 9, 27, 41, 41], positions);
    }

    /// <summary>
    ///     The measured id-to-position drift in retail SPELLS.STD: ids run ahead of the position
    ///     from record 20 on, so treating <c>Index</c> as a position (or position + 1) is wrong.
    /// </summary>
    [Theory]
    [InlineData(10, 9, "Free Action")]
    [InlineData(7, 6, "Wizard's Fire")]
    [InlineData(29, 27, "Toxic Cloud")]
    [InlineData(44, 41, "Chameleon")]
    public void SpellsStd_IdBytesDriftFromTheirPositions(byte id, int position, string name)
    {
        var std = RequireArena2File("SPELLS.STD");
        var record = std.AsSpan(position * DaggerfallSaveSpell.DataLength, DaggerfallSaveSpell.DataLength);

        Assert.Equal(id, record[73]);
        Assert.Equal(name, DaggerfallSaveSpell.Parse(record).Name);
    }

    [Fact]
    public void Spells_AreTheMeasuredSix()
    {
        var tree = RequireFixture().Tree;

        Assert.Equal<string>(
            ["Free Action", "Chameleon", "Wizard's Fire", "Chameleon", "Toxic Cloud", "Free Action"],
            tree.Spells.Select(s => s.Spell.Name).ToArray());
        Assert.Equal<byte>([10, 44, 7, 44, 29, 10], tree.Spells.Select(s => s.Spell.Index).ToArray());
    }

    /// <summary>Item names read as prose, and the three Parchments carry _BRISIEN.QRC message ids.</summary>
    [Fact]
    public void Items_ReadAsProseAndCarryQuestMessageIds()
    {
        var tree = RequireFixture().Tree;
        var items = tree.Items.Select(i => i.Item).ToList();

        Assert.Equal(15, items.Count);
        Assert.Contains(items, i => i.Name == "War axe");
        Assert.Contains(items, i => i.Name == "Spellbook");
        Assert.Contains(items, i => i.Name == "Gold pieces");
        Assert.All(items, i => Assert.Matches("^[A-Z][A-Za-z' -]+$", i.Name));

        var parchments = items.Where(i => i.Name == "Parchment").OrderBy(i => i.Message).ToList();
        Assert.Equal(3, parchments.Count);
        Assert.Equal<ushort>([1_020, 1_021, 1_022], parchments.Select(p => p.Message).ToArray());
    }

    /// <summary>
    ///     SAVEVARS states its faction count and the table ends on EOF; ALL 366 records agree with
    ///     FACTION.TXT's id-to-name pairing, which is why the assertion below is
    ///     <c>Assert.Empty</c> and must not be weakened to tolerate one miss. The 366 records span
    ///     365 distinct ids: retail declares id 77 twice ("The Guildmaster", "The Master of
    ///     Initiates") and the save carries BOTH, at slots 129 and 135. Measured 2026-09-07 over
    ///     the three readings — every declared name + TrimEnd on both sides: 0 mismatches;
    ///     first-name-wins: 1 (id 77); no trim: 1 (id 420, "The Dust Witches ").
    /// </summary>
    [Fact]
    public void SaveVars_FactionTableResolvesAgainstFactionText()
    {
        var vars = LoadSlot().Vars;
        Assert.SkipWhen(vars is null, "the slot holds no SAVEVARS.DAT");

        var names = DaggerfallSaveVars.ParseFactionText(RequireArena2File("FACTION.TXT"));
        Assert.NotEmpty(names);

        // ⚠ Trailing spaces are trimmed on BOTH sides: save faction 420 is "The Dust Witches "
        // with a space the game never trimmed. Trimming only the oracle would still mismatch.
        var mismatched = vars.Factions
            .Where(f => !names.TryGetValue(f.Id, out var declared)
                        || !declared.Any(n => string.Equals(n.TrimEnd(), f.Name.TrimEnd(), StringComparison.Ordinal)))
            .Select(f => $"{f.Id} \"{f.Name}\"")
            .ToList();

        Assert.Equal(vars.DeclaredFactionCount, vars.Factions.Count);
        Assert.Empty(mismatched);
    }

    [Fact]
    public void SaveVars_MatchesTheMeasuredState()
    {
        var vars = RequireFixture().Vars!;

        Assert.Equal(366, vars.DeclaredFactionCount);
        Assert.Equal(366, vars.Factions.Count);
        Assert.Equal(62, vars.Regions.Count);
        Assert.Equal(17, vars.CurrentRegionIndex);

        // Region 17 is the region the player is in and 61 is the LAST block, whose PriceAdjustment
        // is the u16 at 0x3DA + 61 x 80 + 78 = 0x1738 — the two bytes an earlier pass ALSO
        // surfaced as "Unknown1738". Both literals were measured off the retail file by hand.
        Assert.Equal(813, vars.Regions[17].PriceAdjustment);
        Assert.Equal(886, vars.Regions[61].PriceAdjustment);
        Assert.Equal(0xC3810001u, vars.CurrentWorldRecordId);
        Assert.Equal(50_049, vars.CurrentLocationId);
        Assert.Equal(524_043u, vars.GameTimeMinutes);
        Assert.Equal("22:03, 4 Morning Star 3E405", vars.GameTime.ToString());
        // 22:03 is night, and the file agrees.
        Assert.Equal(0, vars.IsDay);
        Assert.True(vars.IsWeaponDrawn);
        Assert.Equal(0x219, vars.TravelFlags);
        Assert.Equal("Cephorus", vars.EmperorSonName);
        Assert.Equal(524_036u, vars.LastSkillCheckTime);
        Assert.Equal("Northmoor", vars.FindFaction(208)!.Name);
        Assert.Equal("Clavicus Vile", vars.Factions[0].Name);
    }

    /// <summary>
    ///     The region table's BASE, settled by a control drawn from the near-miss population
    ///     rather than asserted. At <see cref="DaggerfallSaveVars.RegionsOffset" /> (0x3DA) the 62
    ///     price adjustments are 757..1246 — percentages scaled by 10, exactly what the field is —
    ///     and region 17 reads 813. Eight bytes lower, at 0x3D2 (the base implied by the "region
    ///     blocks end at 0x1732" sentence this class's production doc used to carry), all 62 price
    ///     adjustments, legal reputations and persecuted-temple ids read 0. So the wrong base is
    ///     not merely different, it is empty, and this test fails if the base or the 80-byte stride
    ///     moves in either direction.
    /// </summary>
    [Fact]
    public void SaveVars_RegionBaseIsSettledAgainstTheNearMissBase()
    {
        var slot = RequireFixture();
        var raw = File.ReadAllBytes(Path.Combine(RequireSlot(), DaggerfallSaveVars.FileName));

        var atRightBase = new List<int>();
        var atWrongBase = new List<int>();
        for (var i = 0; i < DaggerfallSaveVars.RegionCount; i++)
        {
            var right = DaggerfallSaveVars.RegionsOffset + i * DaggerfallSaveRegion.RecordLength;
            var wrong = 0x3D2 + i * DaggerfallSaveRegion.RecordLength;
            atRightBase.Add(raw[right + 78] | (raw[right + 79] << 8));
            atWrongBase.Add(raw[wrong + 74] | (raw[wrong + 75] << 8));
            atWrongBase.Add(raw[wrong + 76] | (raw[wrong + 77] << 8));
            atWrongBase.Add(raw[wrong + 78] | (raw[wrong + 79] << 8));
        }

        Assert.Equal(757, atRightBase.Min());
        Assert.Equal(1_246, atRightBase.Max());
        Assert.Equal(813, atRightBase[17]);
        Assert.All(atWrongBase, value => Assert.Equal(0, value));

        // And the parser reads the same numbers the raw bytes hold.
        Assert.Equal<int>(atRightBase, slot.Vars!.Regions.Select(r => (int)r.PriceAdjustment).ToList());
    }

    /// <summary>
    ///     The character record's stamp at +0x1FD is the same game minute SAVEVARS records — the
    ///     cross-file agreement that fixes that offset (one byte lower reads 134,155,008).
    /// </summary>
    [Fact]
    public void CharacterTimeStamp_EqualsTheSaveVarsGameClock()
    {
        var slot = LoadSlot();
        Assert.SkipWhen(slot.Vars is null || slot.Tree.Character is null,
            "the slot holds no SAVEVARS.DAT or no Character record");

        Assert.Equal(slot.Vars!.GameTimeMinutes, slot.Tree.Character!.TimeStamp1FD);
    }

    /// <summary>RUMOR.DAT tiles, and its region rumours name real factions and read as prose.</summary>
    [Fact]
    public void Rumors_TileAndReadAsProse()
    {
        var slot = LoadSlot();
        Assert.SkipWhen(slot.RumorFile is null, "the slot holds no RUMOR.DAT");

        var file = slot.RumorFile!;
        var consumed = file.Rumors.Sum(r => DaggerfallSaveRumor.HeaderLength + (int)r.TextLength);
        Assert.Equal(file.Length, consumed);
        Assert.All(file.Rumors, r => Assert.DoesNotContain('\0', r.Text));
    }

    [Fact]
    public void Rumors_AreTheMeasuredTwelve()
    {
        var slot = RequireFixture();
        var rumors = slot.RumorFile!.Rumors;

        Assert.Equal(12, rumors.Count);
        Assert.Equal(1_481, slot.RumorFile.Length);
        Assert.Equal(208, rumors[0].Faction1);
        Assert.Equal(12u, rumors[0].RumorType);
        Assert.Equal(32, rumors[0].RegionId);
        Assert.Equal(
            "The noble ruler, Lolelle R'on was found murdered in his bed.\n Long live Rariba "
            + "Barastae, newly made Duke of Northmoor.\n",
            rumors[0].Text);
        Assert.Equal(120u, rumors[0].TextLength);
        Assert.Equal("Northmoor", slot.Vars!.FindFaction(rumors[0].Faction1)!.Name);
        Assert.Contains(rumors, r => r.QuestName == "_BRISIEN" && r.QuestMessageId == 1_005);
    }

    /// <summary>BIO.DAT reads as English prose, one line per NUL-terminated run.</summary>
    [Fact]
    public void Biography_ReadsAsProse()
    {
        var slot = LoadSlot();
        Assert.SkipWhen(slot.BiographyLines.Count == 0, "the slot holds no BIO.DAT");

        Assert.All(slot.BiographyLines, line => Assert.Matches("^[ -~]+$", line));
        if (IsFixture(slot))
        {
            Assert.Equal(72, slot.BiographyLines.Count);
            Assert.Equal("You have vague memories of living in a city with a young", slot.BiographyLines[0]);
        }
    }

    /// <summary>
    ///     The standalone AMF is the same automap the tree carries: its 10,240 payload bytes equal
    ///     the DungeonAutomap record's first 10,240, except the six root bytes (the parent id at
    ///     39..41 and the parent type at 67..69) the standalone file zeroes.
    /// </summary>
    [Fact]
    public void Automap_MatchesTheDungeonAutomapRecord()
    {
        var slot = LoadSlot();
        Assert.SkipWhen(slot.Automaps.Count == 0, "the slot holds no AT*.AMF");
        var map = slot.Automaps[0];

        var record = slot.Tree.RecordsOfType(DaggerfallSaveRecordType.DungeonAutomap).FirstOrDefault();
        Assert.SkipWhen(record is null, "the slot's SAVETREE.DAT holds no DungeonAutomap record");
        Assert.True(record.DeclaredLength >= DaggerfallSaveAutomapFile.PayloadLength);

        var whole = new byte[record.DeclaredLength];
        record.Root.Span.CopyTo(whole);
        record.Data.Span.CopyTo(whole.AsSpan(DaggerfallSaveTree.RecordRootLength));

        var differing = Enumerable.Range(0, DaggerfallSaveAutomapFile.PayloadLength)
            .Where(i => whole[i] != map.Payload.Span[i])
            .ToList();

        Assert.Equal<int>([39, 40, 41, 67, 68, 69], differing);
        Assert.Equal(slot.Vars!.GameTimeMinutes, map.TimeStamp);
    }

    /// <summary>
    ///     The 80 x 50 thumbnail renders through ARENA2's ART_PAL.COL and round-trips as a PNG. The
    ///     output is left at a stable path so it can be looked at: a Daggerfall dungeon corridor
    ///     with the classic HUD along the bottom.
    /// </summary>
    [Fact]
    public void Thumbnail_RendersThroughArtPalCol()
    {
        var slot = LoadSlot();
        Assert.SkipWhen(slot.Image is null, "the slot holds no IMAGE.RAW");

        var palette = slot.TryLoadPalette();
        Assert.SkipWhen(palette is null, RealAssetPaths.SkipMessage("ARENA2\\ART_PAL.COL"));

        var png = slot.EncodeImagePng(palette);
        var decoded = PngImageDecoder.Decode(png);
        Assert.Equal(DaggerfallSaveSlot.ImageWidth, decoded.Width);
        Assert.Equal(DaggerfallSaveSlot.ImageHeight, decoded.Height);

        // The HUD strip along the bottom is bright artwork, the dungeon view above it is not
        // (measured 86.41 against 39.29, a ratio of 2.199 — the MEAN OF COMPONENTS this method
        // computes, not weighted luma, which would read 88.47 / 39.71 / 2.228).
        // ⛔ This rules out a GROSSLY wrong stride ONLY, and the comment here used to claim more
        // than it can deliver. Measured over the NEAR-MISS population on the retail IMAGE.RAW:
        // 76 -> 2.042, 78 -> 2.187, 79 -> 2.201, 80 -> 2.199 — every one of them clears the 1.8
        // gate below, and 79 scores fractionally HIGHER than the truth. What the gate does catch
        // is a stride wrong by a factor: 40 -> 0.866, 50 -> 0.921, 64 -> 1.239, 72 -> 1.782. The
        // stride itself is settled by Thumbnail_StrideIsSettledByVerticalCoherence; the palette
        // SCALE, which multiplies both halves alike and so cannot show up in a ratio at all, by
        // the component-range assertion below.
        double MeanLuma(int firstRow, int lastRow)
        {
            var total = 0.0;
            var count = 0;
            for (var y = firstRow; y <= lastRow; y++)
            {
                for (var x = 0; x < decoded.Width; x++)
                {
                    var at = (y * decoded.Width + x) * 4;
                    total += decoded.Pixels[at] + decoded.Pixels[at + 1] + decoded.Pixels[at + 2];
                    count += 3;
                }
            }

            return total / count;
        }

        Assert.True(
            MeanLuma(40, 49) > MeanLuma(5, 30) * 1.8,
            "the HUD strip should be far brighter than the dungeon view above it");

        // ART_PAL.COL is FULL-RANGE 8-bit, not 6-bit VGA: promoting it would make every component
        // roughly four times too bright. Components above 63 prove which it is.
        var paletteFile = RequireArena2File("ART_PAL.COL");
        Assert.Contains(paletteFile.Skip(8), component => component > 63);

        var directory = Path.Combine(Path.GetTempPath(), "bmt-daggerfall-save-thumb");
        Directory.CreateDirectory(directory);
        slot.SaveImagePng(Path.Combine(directory, "SAVE0_IMAGE.png"), palette);
    }

    /// <summary>
    ///     The 80-pixel stride, settled by a control that can FAIL on a near miss — which the
    ///     brightness ratio above cannot (79 scores 2.201 against 80's 2.199).
    ///     <para>
    ///         A headerless image has no stated width, so the width is recovered the way this repo
    ///         already recovers Redguard's WLD row stride: by vertical coherence. Lay the 4,000
    ///         indices out at each candidate stride, resolve them through ART_PAL.COL and take the
    ///         mean absolute difference between vertically adjacent pixels. The true stride keeps
    ///         each column looking at the same part of the picture; a stride off by one shears the
    ///         frame by a pixel per row, which cuts across the HUD's vertical bars and panel edges.
    ///     </para>
    ///     <para>
    ///         Measured over 60..120 (the whole near-miss population, not two absurd extremes): the
    ///         minimum is UNIQUE at 80 (11.60), with the runners-up at 81 (12.67) and 79 (12.70),
    ///         and the score rising monotonically away from there (100 -> 15.40). This fails if the
    ///         stride constant moves by ONE in either direction, and — because the score is an
    ///         absolute number of palette levels — also if the palette were promoted as 6-bit VGA,
    ///         which would multiply it by roughly four.
    ///     </para>
    /// </summary>
    [Fact]
    public void Thumbnail_StrideIsSettledByVerticalCoherence()
    {
        var slot = RequireFixture();
        Assert.SkipWhen(slot.Image is null, "the slot holds no IMAGE.RAW");
        var palette = slot.TryLoadPalette();
        Assert.SkipWhen(palette is null, RealAssetPaths.SkipMessage("ARENA2\\ART_PAL.COL"));

        var indices = slot.Image!.Indices;
        Assert.Equal(4_000, indices.Length);

        double Brightness(int index)
        {
            var (r, g, b, _) = palette.GetEntry(index);
            return (r + g + b) / 3.0;
        }

        double VerticalMeanAbsoluteDifference(int stride)
        {
            var rows = indices.Length / stride;
            var total = 0.0;
            var count = 0;
            for (var y = 0; y < rows - 1; y++)
            {
                for (var x = 0; x < stride; x++)
                {
                    total += Math.Abs(Brightness(indices[y * stride + x]) - Brightness(indices[(y + 1) * stride + x]));
                    count++;
                }
            }

            return total / count;
        }

        var scores = Enumerable.Range(60, 61).ToDictionary(stride => stride, VerticalMeanAbsoluteDifference);
        var best = scores.OrderBy(entry => entry.Value).First().Key;

        Assert.Equal(80, best);
        Assert.Equal(DaggerfallSaveSlot.ImageWidth, best);
        Assert.Equal(11.6, scores[80], 1);

        var runnerUp = scores.Where(entry => entry.Key != 80).Min(entry => entry.Value);
        Assert.True(
            scores[80] < runnerUp * 0.95,
            $"stride 80 scored {scores[80]:F3}; the best of the other 60 candidates scored {runnerUp:F3}");
    }
}
