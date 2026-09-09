using System.Numerics;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Pins Daggerfall dungeon-block assembly: the 2,048-per-turn angle scale, the Y-then-X-then-Z
///     order, and the model-slot lookup. Retail figures come from an independent Python walk of all
///     187 RDB blocks (2026-09-06).
/// </summary>
public sealed class DaggerfallRdbSceneAssemblerTests
{
    private static XnGineTriangleMesh Mesh()
    {
        return new XnGineTriangleMesh(1, 1f, Vector3.One, [
            new XnGineSubMesh(0, 0,
                [new XnGineVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero)], [0, 0, 0])
        ]);
    }

    private static DaggerfallRdbModelResource Model(ushort index, int yRotation = 0, int xRotation = 0,
        int zRotation = 0)
    {
        return new DaggerfallRdbModelResource
        {
            XRotation = xRotation, YRotation = yRotation, ZRotation = zRotation, ModelIndex = index,
            TriggerFlagStartingLock = 0, SoundIndex = 0, ActionOffset = 0, Action = null
        };
    }

    private static DaggerfallRdbObject Placed(DaggerfallRdbModelResource? model, int x = 0, int y = 0, int z = 0)
    {
        return new DaggerfallRdbObject
        {
            Position = 0, Next = -1, Previous = -1, Index = 0, XPos = x, YPos = y, ZPos = z,
            Type = model is null ? DaggerfallRdbResourceType.Flat : DaggerfallRdbResourceType.Model,
            ResourceOffset = 0, Model = model, Flat = null, Light = null
        };
    }

    private static List<DaggerfallRdbModelReference> References(params string[] ids)
    {
        var list = new List<DaggerfallRdbModelReference>();
        foreach (var id in ids)
        {
            list.Add(new DaggerfallRdbModelReference(
                id, uint.TryParse(id, out var number) ? number : null, "GHF"));
        }

        return list;
    }

    [Fact]
    public void Assemble_PlacesEveryModelledObjectAndSkipsLightsAndFlats()
    {
        // 39,467 retail objects but only 22,961 are modelled — a lights-and-flats block must not
        // count toward Placed, or the resolve rate reads far worse than it is.
        var result = DaggerfallRdbSceneAssembler.Assemble(
            "N0000002.RDB",
            References("00100", "00200"),
            [Placed(Model(0)), Placed(null), Placed(Model(1))],
            _ => Mesh());

        Assert.Equal(2, result.Placed);
        Assert.Equal(2, result.Resolved);
        Assert.Empty(result.MissingIds);
    }

    [Fact]
    public void Assemble_ReportsUnresolvedIdsOnceWithoutFailing()
    {
        var result = DaggerfallRdbSceneAssembler.Assemble(
            "N0000002.RDB",
            References("00100", "00999"),
            [Placed(Model(0)), Placed(Model(1)), Placed(Model(1))],
            id => id == 100 ? Mesh() : null);

        Assert.Equal(3, result.Placed);
        Assert.Equal(1, result.Resolved);
        Assert.Equal<uint[]>([999], [.. result.MissingIds]);
    }

    [Fact]
    public void ModelIdOf_ReadsTheSlotDirectlyRatherThanScalingTheIndex()
    {
        // ⚠ ModelIndex is a SLOT NUMBER into the 750-entry reference table, not a byte offset.
        // Dividing or multiplying it lands on a neighbouring model and still resolves, which is
        // exactly the kind of wrong that renders convincingly.
        var references = References("00100", "00200", "00300");

        Assert.Equal(100u, DaggerfallRdbSceneAssembler.ModelIdOf(references, Model(0)));
        Assert.Equal(200u, DaggerfallRdbSceneAssembler.ModelIdOf(references, Model(1)));
        Assert.Equal(300u, DaggerfallRdbSceneAssembler.ModelIdOf(references, Model(2)));
    }

    [Fact]
    public void ModelIdOf_ReturnsNullForASlotHoldingLeftoverBytes()
    {
        // A block fills only the first few of its 750 slots; the tail is authoring leftovers whose
        // five characters need not parse. That is a skipped placement, never an exception.
        var references = References("00100", "junk");

        Assert.Null(DaggerfallRdbSceneAssembler.ModelIdOf(references, Model(1)));
        Assert.Null(DaggerfallRdbSceneAssembler.ModelIdOf(references, Model(700)));
    }

    [Theory]
    [InlineData(0, 100f, 0f)]
    [InlineData(512, 0f, 100f)]
    [InlineData(1024, -100f, 0f)]
    [InlineData(1536, 0f, -100f)]
    public void TransformFor_TurnsTheFourCardinalsIntoRightAngles(int rotation, float expectedX, float expectedZ)
    {
        // 2,048 units per turn: 96.0% of the 22,961 retail RDB Y rotations are multiples of the
        // 512-unit right angle. Written as literals, never derived from the constant under test —
        // under the 63.28-per-turn reading 512 becomes 32.7 degrees and every case here fails.
        // ⚠ Asserted on the ORIENTATION, not the translation: unlike an RMB sub-block, an RDB
        // object's rotation lives on its model resource and does not orbit its position, so the
        // translation is identical for all four and could not discriminate anything.
        var facing = Vector3.TransformNormal(
            new Vector3(100, 0, 0),
            DaggerfallRdbSceneAssembler.TransformFor(Placed(Model(0)), Model(0, rotation)));

        Assert.Equal(expectedX, facing.X, 3);
        Assert.Equal(expectedZ, facing.Z, 3);
    }

    [Fact]
    public void TransformFor_KeepsNegativeYBecauseDungeonsDescend()
    {
        // ⚠ Retail Y spans −4,608..3,009. Clamping or mirroring Y would hide the half of every
        // dungeon that lies below its entrance.
        Assert.Equal(-4608f, DaggerfallRdbSceneAssembler.TransformFor(
            Placed(Model(0), y: -4608), Model(0)).Translation.Y, 3);
    }

    [Fact]
    public void TransformFor_MirrorsZAboutTheDungeonBlockSideNotTheCityBlockSide()
    {
        // ⚠ A dungeon block is 2,048 units, HALF the RMB block's 4,096. Reusing the RMB mirror
        // offsets every dungeon by a full block and it still looks internally consistent.
        Assert.Equal(2048f, DaggerfallRdbSceneAssembler.TransformFor(
            Placed(Model(0), z: 0), Model(0)).Translation.Z, 3);
        Assert.Equal(1048f, DaggerfallRdbSceneAssembler.TransformFor(
            Placed(Model(0), z: 1000), Model(0)).Translation.Z, 3);
    }

    [Fact]
    public void TransformFor_AppliesTheRotationsBeforeTheTranslation()
    {
        // Row-vector composition: a rotation placed AFTER the translation would orbit the object
        // about the block origin instead of spinning it where it stands. A quarter-turned object at
        // x=100 must stay at x=100 — orbiting would send it to x=0.
        var spun = DaggerfallRdbSceneAssembler.TransformFor(
            Placed(Model(0), 100, z: 0), Model(0, 512));

        Assert.Equal(100f, spun.Translation.X, 3);
        Assert.Equal(DaggerfallRdbBlock.UnitsPerBlock, spun.Translation.Z, 3);
    }

    [Fact]
    public void AngleUnitsPerTurn_MatchesTheRmbAndBattlespireAssemblers()
    {
        // One engine, one angle unit. A divergence here is a decode bug, not a per-format quirk.
        Assert.Equal(2048f, DaggerfallRdbSceneAssembler.AngleUnitsPerTurn);
        Assert.Equal(
            DaggerfallBlockSceneAssembler.AngleUnitsPerTurn, DaggerfallRdbSceneAssembler.AngleUnitsPerTurn);
    }
}