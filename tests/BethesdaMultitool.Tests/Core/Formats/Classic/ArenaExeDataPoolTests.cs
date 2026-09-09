using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the name pools hardcoded in Arena's <c>A.EXE</c>,
///     read from the PKLITE-unpacked image.
///     <para>
///         ⚑ The oracle is the COUNT the game is known to have — 8 attributes, 18 classes, 8 races,
///         8 armour materials — combined with the entries reading as the right words. A pool found
///         at the wrong address gives neither.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ArenaExeDataPoolTests
{
    private static byte[] RequireUnpackedExe()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var arena = RealAssetPaths.Classics.Arena();
        Assert.SkipWhen(arena is null, RealAssetPaths.SkipMessage("Arena"));

        var exe = Path.Combine(arena, "A.EXE");
        Assert.SkipWhen(!File.Exists(exe), RealAssetPaths.SkipMessage("A.EXE"));

        var packed = File.ReadAllBytes(exe);
        Assert.SkipWhen(!ArenaExeUnpacker.LooksPacked(packed), RealAssetPaths.SkipMessage("a PKLITE A.EXE"));
        return ArenaExeUnpacker.Unpack(packed, "A.EXE");
    }

    [Fact]
    public void TheAttributePoolIsTheGamesEightAttributesInOrder()
    {
        var attributes = ArenaExeData.TryReadAttributes(RequireUnpackedExe());

        Assert.NotNull(attributes);
        Assert.Equal(
            ["Strength", "Intelligence", "Willpower", "Agility", "Speed", "Endurance", "Personality", "Luck"],
            attributes);
    }

    [Fact]
    public void TheClassPoolIsTheGamesEighteenClasses()
    {
        var classes = ArenaExeData.TryReadClasses(RequireUnpackedExe());

        Assert.NotNull(classes);
        Assert.Equal(ArenaExeData.ClassCount, classes.Count);
        Assert.Equal("Mage", classes[0]);
        Assert.Equal("Knight", classes[^1]);
        Assert.Contains("Nightblade", classes);

        // ⚠ The pool stops at 18. A nineteenth string "BattleMage" follows with different casing to
        // the "Battlemage" inside it; taking 19 would silently add a duplicate-looking class.
        Assert.Single(classes, c => c.Equals("Battlemage", StringComparison.Ordinal));
        Assert.DoesNotContain("BattleMage", classes);
    }

    [Fact]
    public void RacesComeInTwoPoolsAndTheSingularOneIsNotThePlural()
    {
        var exe = RequireUnpackedExe();
        var plural = ArenaExeData.TryReadRacesPlural(exe);
        var singular = ArenaExeData.TryReadRacesSingular(exe);

        Assert.NotNull(plural);
        Assert.NotNull(singular);
        Assert.Equal(ArenaExeData.RaceCount, plural.Count);
        Assert.Equal(ArenaExeData.RaceCount, singular.Count);

        // ⚑ Two pools sit back to back. Reading the first and assuming singular yields "Bretons".
        Assert.Equal("Bretons", plural[0]);
        Assert.Equal("Breton", singular[0]);
        Assert.Equal("Argonians", plural[^1]);
        Assert.Equal("Argonian", singular[^1]);

        // Khajiit has no plural form, which is why the two pools are not a simple 's' apart.
        Assert.Contains("Khajiit", plural);
        Assert.Contains("Khajiit", singular);
    }

    [Fact]
    public void TheMaterialPoolIsTheEightArmourMaterialsWeakestFirst()
    {
        var materials = ArenaExeData.TryReadMaterials(RequireUnpackedExe());

        Assert.NotNull(materials);
        Assert.Equal(
            ["Iron", "Steel", "Silver", "Elven", "Dwarven", "Mithril", "Adamantium", "Ebony"],
            materials);
    }

    [Fact]
    public void APoolIsRejectedWhenTheAnchorIsAbsentRatherThanReadFromTheWrongPlace()
    {
        // A build without these strings must yield null, not a pool scavenged from elsewhere.
        var noise = Encoding.ASCII.GetBytes(new string('x', 4096));

        Assert.Null(ArenaExeData.TryReadAttributes(noise));
        Assert.Null(ArenaExeData.TryReadClasses(noise));
        Assert.Null(ArenaExeData.TryReadRacesPlural(noise));
        Assert.Null(ArenaExeData.TryReadMaterials(noise));
    }


    [Fact]
    public void TheWeaponPoolIsTheGamesEighteenWeaponsInOrder()
    {
        var weapons = ArenaExeData.TryReadWeapons(RequireUnpackedExe());

        Assert.NotNull(weapons);
        Assert.Equal(
            [
                "Staff", "Dagger", "Shortsword", "Broadsword", "Saber", "Longsword", "Claymore", "Tanto",
                "Wakizashi", "Katana", "Dai-Katana", "Mace", "Flail", "War Hammer", "War Axe", "Battle Axe",
                "Short Bow", "Long Bow"
            ],
            weapons);
    }

    [Fact]
    public void TheArmorPoolStopsAtTheShieldsAndNotAtTheBodyPartWords()
    {
        // ⚠ The run CONTINUES past the 11 pieces into body-part words (Chest, Hands, Legs, Shoulder
        // twice, Head, Foot) and four entries reading "General". Reading to the end of the run gives
        // 22 "armour pieces", more than half of them not armour.
        var armor = ArenaExeData.TryReadArmorPieces(RequireUnpackedExe());

        Assert.NotNull(armor);
        Assert.Equal(
            [
                "Cuirass", "Gauntlets", "Greaves", "Pauldron (L)", "Pauldron (R)", "Helm", "Boots",
                "Buckler", "Round Shield", "Kite Shield", "Tower Shield"
            ],
            armor);
        Assert.DoesNotContain("Chest", armor);
        Assert.DoesNotContain("General", armor);
    }

    [Fact]
    public void TheCreaturePoolStopsWhereTheClassBasedEnemiesBegin()
    {
        // ⚠ The run continues into 20 class names that duplicate the class pool. ⚑ The join is
        // visible in the data: the tail spells "BattleMage" with a capital M where the class list
        // spells it "Battlemage" — so the two lists were authored separately and concatenated.
        var creatures = ArenaExeData.TryReadCreatures(RequireUnpackedExe());

        Assert.NotNull(creatures);
        Assert.Equal(ArenaExeData.CreatureCount, creatures.Count);
        Assert.Equal("Rat", creatures[0]);
        Assert.Equal("Lich", creatures[^1]);
        Assert.DoesNotContain("Spellsword", creatures);
        Assert.DoesNotContain("BattleMage", creatures);
    }

    [Fact]
    public void TheCalendarPoolsAreTwelveMonthsAndSevenDays()
    {
        // ⚑ These counts are EXTERNAL facts — twelve months, seven days — which is what makes them
        // an oracle rather than a fit: a pool found at the wrong address will not yield exactly
        // twelve month names reading as the Tamrielic calendar.
        var exe = RequireUnpackedExe();
        var months = ArenaExeData.TryReadMonths(exe);
        var days = ArenaExeData.TryReadDays(exe);

        Assert.NotNull(months);
        Assert.Equal(12, months.Count);
        Assert.Equal("Morning Star", months[0]);
        Assert.Equal("Evening Star", months[^1]);

        Assert.NotNull(days);
        Assert.Equal(["Morndas", "Tirdas", "Middas", "Turdas", "Fredas", "Loredas", "Sundas"], days);
    }

    [Fact]
    public void TheSpellEffectPoolIsTheWholeRun()
    {
        var effects = ArenaExeData.TryReadSpellEffects(RequireUnpackedExe());

        Assert.NotNull(effects);
        Assert.Equal(ArenaExeData.SpellEffectCount, effects.Count);
        Assert.Equal("Cause", effects[0]);
        Assert.Equal("Spell Resistance", effects[^1]);
        Assert.Contains("Levitate", effects);
    }

    [Fact]
    public void TheNobleTitlePoolStopsBeforeTheMetalDescriptors()
    {
        // ⚠ The run continues into the SAME metals the material pool holds, but carrying their
        // articles — "a Brass", "an Iron", … "a Crystal". Reading to the end gives 26 "titles".
        var titles = ArenaExeData.TryReadNobleTitles(RequireUnpackedExe());

        Assert.NotNull(titles);
        Assert.Equal(
            [
                "Lord", "Duke", "Baron", "Count", "Prince", "King", "Emperor",
                "Lady", "Duchess", "Baroness", "Countess", "Princess", "Queen", "Empress"
            ],
            titles);
        Assert.DoesNotContain("a Brass", titles);
    }

    [Fact]
    public void EveryPoolAnchorIsUniqueInTheImage()
    {
        // ⚠⚠ The reason every anchor spells TWO entries. A one-word anchor ("Strength") matched an
        // unrelated earlier occurrence at 0x03D751 rather than the pool at 0x03E1FC, and the count
        // check cannot catch that — the wrong site still yields the right NUMBER of strings.
        var exe = RequireUnpackedExe();
        string[] anchors =
        [
            "Staff\0Dagger", "Cuirass\0Gauntlets", "Rat\0Goblin", "Morning Star\0Sun's Dawn",
            "Morndas\0Tirdas", "Cause\0Continuous Damage", "Lord\0Duke"
        ];

        foreach (var anchor in anchors)
        {
            var needle = Encoding.Latin1.GetBytes(anchor);
            var hits = 0;
            for (var i = 0; i + needle.Length <= exe.Length; i++)
            {
                if (exe.AsSpan(i, needle.Length).SequenceEqual(needle))
                {
                    hits++;
                }
            }

            Assert.Equal(1, hits);
        }
    }
}