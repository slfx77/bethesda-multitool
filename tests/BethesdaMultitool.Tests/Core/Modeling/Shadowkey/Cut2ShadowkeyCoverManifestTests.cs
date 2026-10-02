using System.Text.Json.Nodes;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     Default-suite checks of the cut-2 Shadowkey cover (plan section 9, slice 2): the checked-in manifest's shape and
///     counts, the expectations' pairing with it, and the parser's refusals on synthetic manifests.
/// </summary>
public class Cut2ShadowkeyCoverManifestTests
{
    [Fact]
    public void TheManifest_IsThePairwiseCoverWithItsEdgesAndControls()
    {
        Assert.SkipWhen(Cut2ShadowkeyCoverManifest.Path is null,
            $"{Cut2ShadowkeyCoverManifest.RelativePath} is not in this checkout.");
        var cover = Cut2ShadowkeyCoverManifest.Cover;

        // Plan section 9: 17 cover slots and 5 edge slots (1,534,782 bytes), 7 cover zones and 2 edge zones.
        Assert.Equal(17, cover.Slots.Count(static s => s.Role == "cover"));
        Assert.Equal(5, cover.Slots.Count(static s => s.Role == "edge"));
        Assert.Equal(1_534_782, cover.Slots.Sum(static s => s.Size));
        Assert.Equal(7, cover.Zones.Count(static z => z.Role == "cover"));
        Assert.Equal(2, cover.Zones.Count(static z => z.Role == "edge"));
        Assert.Equal(11, cover.Declines.Count(static d => d.Kind == Cut2ShadowkeyDeclineRow.EmptySlotKind));
        Assert.Equal(13, cover.Declines.Count(static d => d.Kind == Cut2ShadowkeyDeclineRow.FileKind));
        Assert.Equal([19, 24, 26, 28, 29, 54, 57, 133, 221, 227, 236],
            cover.Declines.Where(static d => d.Slot is not null).Select(static d => d.Slot!.Value));
        foreach (var edge in new[] { "smallest", "largest", "most-frames", "most-skins", "most-vertices", "most-faces",
                     "widest-uv-wrap", "most-sequences", "largest-frame-delta" })
        {
            Assert.Contains(cover.Slots, s => s.Edges.Contains(edge));
        }

        Assert.Contains(cover.Zones, static z => z.Stem == "raiders" && z.Edges.Contains("most-placements"));
        Assert.Contains(cover.Zones, static z => z.Stem == "azra" && z.Files.ContainsKey("azra.sta"));
        Assert.All(cover.Zones, static z => Assert.True(z.Files.Count >= 13, z.Name));
        Assert.Equal(4_907_880, cover.Pack["models.huge"].Size);
        // The multi-skin rows hop B's GLB control runs on (cut-2 review finding 1): every one is animated.
        Assert.Equal(new[] { 20, 22, 56, 59, 63, 69, 230 },
            cover.Slots.Where(static s => Cut2ShadowkeyCoverManifest.IsMultiSkin(s.Cell)).Select(static s => s.Slot).Order());
        Assert.All(cover.Slots.Where(static s => Cut2ShadowkeyCoverManifest.IsMultiSkin(s.Cell)),
            static s => Assert.DoesNotContain("frames=1|", s.Cell, StringComparison.Ordinal));
    }

    [Fact]
    public void TheExpectations_PairWithEveryRow()
    {
        Assert.SkipWhen(Cut2ShadowkeyCoverManifest.Path is null || Cut2ShadowkeyExpectations.Path is null,
            "The cut-2 Shadowkey manifest or expectations are not in this checkout.");
        var cover = Cut2ShadowkeyCoverManifest.Cover;
        var records = Cut2ShadowkeyExpectations.Records;

        foreach (var slot in cover.Slots)
        {
            var record = records[slot.Sha256];
            Assert.Equal("slot", record["kind"]!.GetValue<string>());
            Assert.Equal(slot.Slot, record["slot"]!.GetValue<int>());
            Assert.Equal("Supported", record["probe"]!["kind"]!.GetValue<string>());
            Assert.Equal(slot.Size < 65536, record["probe"]!["complete"]!.GetValue<bool>());
        }

        foreach (var zone in cover.Zones)
        {
            var record = records[zone.Sha256];
            Assert.Equal(zone.Stem, record["stem"]!.GetValue<string>());
            Assert.Equal("Confirmed", record["probe"]!["confidence"]!.GetValue<string>());
        }

        foreach (var decline in cover.Declines)
        {
            var record = records[decline.Sha256];
            Assert.Equal("NotAModel", record["probe"]!["kind"]!.GetValue<string>());
            Assert.Equal("NotAModel", record["zoneProbe"]!["kind"]!.GetValue<string>());
        }

        Assert.Equal(cover.Slots.Count + cover.Zones.Count + cover.Declines.Count - 10, records.Count);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("uncovered")]
    [InlineData("sha")]
    [InlineData("duplicateName")]
    [InlineData("entry")]
    [InlineData("zoneDigest")]
    [InlineData("declineExpectsSupported")]
    [InlineData("emptySlotSize")]
    [InlineData("pack")]
    public void Parse_RefusesAManifestThatBreaksARule(string rule)
    {
        var manifest = SyntheticManifest();
        var parsed = Cut2ShadowkeyCoverManifest.Parse(manifest.ToJsonString());
        Assert.Single(parsed.Slots);
        var slot = manifest["slots"]![0]!.AsObject();
        switch (rule)
        {
            case "schema":
                manifest["schema"] = "cut2-shadowkey-cover/0";
                break;
            case "uncovered":
                manifest["uncoveredItems"] = new JsonArray("tag:magenta");
                break;
            case "sha":
                slot["sha256"] = "XYZ";
                break;
            case "duplicateName":
                manifest["zones"]![0]!["name"] = slot["name"]!.GetValue<string>();
                break;
            case "entry":
                slot["entry"] = "176_arrow.bin";
                break;
            case "zoneDigest":
                manifest["zones"]![0]!["sha256"] = new string('a', 64);
                break;
            case "declineExpectsSupported":
                manifest["declineControls"]![0]!["expect"] = "Supported";
                break;
            case "emptySlotSize":
                manifest["declineControls"]![0]!["size"] = 4;
                break;
            case "pack":
                manifest["pack"]!.AsObject().Remove("entities.txt");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, null);
        }

        Assert.Throws<InvalidDataException>(() => Cut2ShadowkeyCoverManifest.Parse(manifest.ToJsonString()));
    }

    private static JsonObject SyntheticManifest()
    {
        static JsonObject Pin(char digit, long size)
        {
            return new JsonObject { ["size"] = size, ["sha256"] = new string(digit, 64) };
        }

        return new JsonObject
        {
            ["schema"] = Cut2ShadowkeyCoverManifest.Schema,
            ["uncoveredItems"] = new JsonArray(),
            ["pack"] = new JsonObject
            {
                ["models.idx"] = Pin('1', 12), ["models.huge"] = Pin('2', 678), ["models.txt"] = Pin('3', 20),
                ["entities.txt"] = Pin('4', 20)
            },
            ["slots"] = new JsonArray(new JsonObject
            {
                ["role"] = "cover", ["name"] = "slot 175 arrow.bin", ["slot"] = 175, ["entry"] = "175_arrow.bin",
                ["offset"] = 0, ["size"] = 678, ["sha256"] = new string('5', 64), ["alsoInSlots"] = new JsonArray(),
                ["cell"] = "frames=1", ["tags"] = new JsonArray(), ["edges"] = new JsonArray("smallest")
            }),
            ["zones"] = new JsonArray(new JsonObject
            {
                ["role"] = "cover", ["name"] = "zone raiders", ["stem"] = "raiders", ["sha256"] = new string('6', 64),
                ["size"] = 100, ["cell"] = "grid=128x128", ["tags"] = new JsonArray(), ["edges"] = new JsonArray(),
                ["files"] = new JsonObject { ["raiders.zmp"] = Pin('6', 100), ["raiders.zcp"] = Pin('7', 50) }
            }),
            ["declineControls"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "empty slot 19", ["kind"] = "empty-slot", ["slot"] = 19, ["size"] = 0,
                    ["sha256"] = Cut2ShadowkeyCoverManifest.EmptySha256, ["expect"] = "NotAModel"
                },
                new JsonObject
                {
                    ["name"] = "file 6r51.app", ["kind"] = "file", ["path"] = "6r51.app", ["size"] = 10,
                    ["sha256"] = new string('8', 64), ["expect"] = "NotAModel"
                })
        };
    }
}
