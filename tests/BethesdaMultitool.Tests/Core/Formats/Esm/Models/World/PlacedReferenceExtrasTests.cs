using System.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models.World;

/// <summary>
///     Guards the side-table split on <see cref="PlacedReference" />.
///     <para>
///         Moving the rare fields into <see cref="PlacedReferenceExtras" /> removed about 244 bytes
///         from every one of Fallout 76's 5.1M references — but it did so by putting a shared
///         REFERENCE where inline value fields used to be, and <c>PlacedReference</c> is a record
///         that the codebase copies with <c>with { … }</c> in dozens of places. A record's generated
///         copy constructor copies that reference. If the side object were ever mutable, the
///         <c>init</c> accessor running after the copy would write straight through it and corrupt
///         the instance being copied FROM — a silent, action-at-a-distance data loss of exactly the
///         kind the <c>cell with { … }</c> bug was.
///     </para>
///     <para>
///         So the two tests that matter here are the aliasing one and the reflection round-trip. The
///         round-trip is written to fail on a property somebody ADDS later and forgets, which is the
///         only way a guard like this stays useful.
///     </para>
/// </summary>
public sealed class PlacedReferenceExtrasTests
{
    /// <summary>
    ///     Collection-valued members are shared instances so that two fixtures compare equal.
    ///     Records compare a <c>uint[]</c> or an <c>IReadOnlyList</c> by REFERENCE — that was true
    ///     of these properties before they moved into the side table and is unchanged by it, so two
    ///     separately-allocated arrays would make the equality test below fail for a reason that has
    ///     nothing to do with what it is checking.
    /// </summary>
    private static readonly uint[] SharedLinkedChildren = [1, 2, 3];

    private static readonly PlacedReferenceStructuralData SharedStructuralData = new()
    {
        Subrecords = [new PlacedReferenceStructuralSubrecord("XRMR", [1, 2, 3, 4])]
    };

    /// <summary>
    ///     A reference with every single public settable property set to a non-default value.
    ///     <see cref="Every_settable_property_is_covered_by_the_fully_populated_fixture" /> proves
    ///     this stays exhaustive.
    /// </summary>
    private static PlacedReference CreateFullyPopulated() => new()
    {
        Bounds = new ObjectBounds { X1 = -1, Y1 = -2, Z1 = -3, X2 = 4, Y2 = 5, Z2 = 6 },
        ModelPath = "meshes\\test\\thing.nif",
        FormId = 0x0001A2B3,
        BaseFormId = 0x000C4D5E,
        BaseEditorId = "TestBase",
        EditorId = "TestRef",
        RecordType = "ACHR",
        X = 11f,
        Y = 22f,
        Z = 33f,
        RotX = 0.1f,
        RotY = 0.2f,
        RotZ = 0.3f,
        Scale = 2.5f,
        IsMapMarker = true,
        IsPersistent = true,
        IsInitiallyDisabled = true,
        Offset = 123456L,
        IsBigEndian = true,
        Radius = 9.5f,
        Count = 7,
        RadioData = new RadioData { Radius = 3f, RangeType = 1, StaticPercentage = 0.5f },
        OwnerFormId = 0x11111111,
        EncounterZoneFormId = 0x22222222,
        MaterialSwapFormId = 0x33333333,
        EmittanceFormId = 0x44444444,
        LockLevel = 50,
        LockKeyFormId = 0x55555555,
        LockFlags = 0x04,
        LockNumTries = 3,
        LockTimesUnlocked = 2,
        EnableParentFormId = 0x66666666,
        EnableParentFlags = 0x01,
        PersistentCellFormId = 0x77777777,
        StartingPosition = new PositionSubrecord(1, 2, 3, 4, 5, 6, 7L, true),
        StartingWorldOrCellFormId = 0x88888888,
        PackageStartLocation = new RuntimePackageStartLocation(0x99999999, 1, 2, 3, 4),
        MerchantContainerFormId = 0xAAAAAAAA,
        LeveledCreatureOriginalBaseFormId = 0xBBBBBBBB,
        LeveledCreatureTemplateFormId = 0xCCCCCCCC,
        DestinationDoorFormId = 0xDDDDDDDD,
        DestinationCellFormId = 0xEEEEEEEE,
        TeleportPosRot = new PositionSubrecord(9, 8, 7, 6, 5, 4, 3L, false),
        TeleportFlags = 0x01,
        MarkerType = MapMarkerType.Vault,
        MarkerName = "Test Marker",
        OriginCellFormId = 0x0F0F0F0F,
        SpecialRenderingFlags = 0x2,
        LinkedRefKeywordFormId = 0x12121212,
        LinkedRefFormId = 0x13131313,
        LinkedRefChildrenFormIds = SharedLinkedChildren,
        StructuralData = SharedStructuralData,
        AssignmentSource = "GridMap"
    };

    private static IEnumerable<PropertyInfo> SettableProperties() =>
        typeof(PlacedReference)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite);

    [Fact]
    public void Every_settable_property_is_covered_by_the_fully_populated_fixture()
    {
        // The keystone. If someone adds a property and does not add it above, its value here equals
        // the default one's and this names it — which is what stops the round-trip test below from
        // quietly covering less over time.
        var populated = CreateFullyPopulated();
        var bare = new PlacedReference();

        var uncovered = SettableProperties()
            .Where(p => Equals(p.GetValue(populated), p.GetValue(bare)))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(uncovered.Length == 0,
            "properties left at their default by CreateFullyPopulated (add them): " +
            string.Join(", ", uncovered));
    }

    [Fact]
    public void A_with_expression_that_changes_nothing_preserves_every_property()
    {
        // The generated copy constructor has to carry the side object across. A property that failed
        // to survive this would be silently dropped by any of the codebase's `ref with { … }` sites.
        var original = CreateFullyPopulated();
        var clone = original with { };

        foreach (var property in SettableProperties())
        {
            Assert.Equal(property.GetValue(original), property.GetValue(clone));
        }
    }

    [Fact]
    public void Changing_one_extra_through_with_does_not_touch_the_original()
    {
        // THE test. A mutable side object would make this fail: the copy shares the reference, and
        // the init accessor would write through it into `original`.
        var original = CreateFullyPopulated();

        var modified = original with { OwnerFormId = 0xFEEDFACE };

        Assert.Equal(0x11111111u, original.OwnerFormId);
        Assert.Equal(0xFEEDFACEu, modified.OwnerFormId);
        // And nothing else moved with it.
        Assert.Equal(original.LockKeyFormId, modified.LockKeyFormId);
        Assert.Equal(original.MarkerName, modified.MarkerName);
        Assert.NotSame(original.Extras, modified.Extras);
    }

    [Fact]
    public void Chained_with_expressions_accumulate_rather_than_replace()
    {
        var a = new PlacedReference { FormId = 1, OwnerFormId = 10 };
        var b = a with { LockLevel = 25 };
        var c = b with { MarkerName = "Somewhere" };

        Assert.Equal(10u, c.OwnerFormId);
        Assert.Equal((byte)25, c.LockLevel);
        Assert.Equal("Somewhere", c.MarkerName);
        // The intermediates are untouched.
        Assert.Null(a.LockLevel);
        Assert.Null(b.MarkerName);
    }

    [Fact]
    public void An_ordinary_reference_allocates_no_side_object_at_all()
    {
        // The entire point of the change: a plain STAT placement — position, scale, model — must
        // carry a null reference where 244 bytes of mostly-null padding used to sit.
        var plain = new PlacedReference
        {
            FormId = 0x1234,
            BaseFormId = 0x5678,
            ModelPath = "meshes\\clutter\\rock01.nif",
            X = 1f, Y = 2f, Z = 3f,
            Scale = 1f
        };

        Assert.Null(plain.Extras);
    }

    [Fact]
    public void Assigning_null_or_empty_does_not_force_a_side_object()
    {
        // Parser paths assign these unconditionally from a nullable local, so "set to null" is the
        // common case, not an edge one. If it allocated, nothing would ever be extras-free.
        var reference = new PlacedReference
        {
            FormId = 1,
            OwnerFormId = null,
            LockLevel = null,
            MarkerName = null,
            LinkedRefChildrenFormIds = [],
            StructuralData = null
        };

        Assert.Null(reference.Extras);
        Assert.Null(reference.OwnerFormId);
        Assert.Empty(reference.LinkedRefChildrenFormIds);
    }

    [Fact]
    public void Linked_ref_children_read_as_empty_rather_than_null_when_unset()
    {
        // Behaviour preserved from the inline field, whose default was the shared empty array.
        var reference = new PlacedReference { FormId = 1 };

        Assert.NotNull(reference.LinkedRefChildrenFormIds);
        Assert.Empty(reference.LinkedRefChildrenFormIds);
    }

    [Fact]
    public void Value_equality_still_compares_the_extras_by_value()
    {
        // PlacedReference is a record and stays one. Equality now runs through the side object, so
        // it has to be a record too — a plain class would compare by reference and make two
        // identically-populated references unequal.
        var left = CreateFullyPopulated();
        var right = CreateFullyPopulated();

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, left with { OwnerFormId = 1 });
    }

    [Fact]
    public void Is_imposter_still_reads_through_the_side_table()
    {
        // A computed property over a moved field — the lifted-null trap it documents is easy to
        // reintroduce when the backing store changes.
        Assert.False(new PlacedReference { FormId = 1 }.IsImposter);
        Assert.False(new PlacedReference { SpecialRenderingFlags = 0x4 }.IsImposter);
        Assert.True(new PlacedReference { SpecialRenderingFlags = 0x2 }.IsImposter);
        Assert.True(new PlacedReference { SpecialRenderingFlags = 0x6 }.IsImposter);
    }
}
