using BethesdaMultitool.Core.EsmView;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     Whether the viewer offers a visibility toggle for a selected placement, and what authored
///     state it reports.
///     <para>
///         The rule exists to avoid a success-looking no-op: an actor ref or a modelless ref has
///         nothing for the toggle to act on. Previously this was covered by asserting the control's
///         source text still contained <c>"RenderableReference.TryBuild("</c> — which says nothing
///         about which placements are actually eligible.
///     </para>
/// </summary>
public class ReferencePreviewEligibilityTests
{
    private const uint BaseFormId = 0x700;
    private const uint RefFormId = 0x1000;

    [Fact]
    public void CanPreview_StaticRefWithAModel_IsEligible()
    {
        var reference = MakeReference();

        Assert.True(ReferencePreviewEligibility.CanPreview(reference, MakeData()));
    }

    /// <summary>A zero FormID is a malformed placement — there is no instance to override.</summary>
    [Fact]
    public void CanPreview_ZeroFormId_IsNotEligible()
    {
        var reference = MakeReference() with { FormId = 0 };

        Assert.False(ReferencePreviewEligibility.CanPreview(reference, MakeData()));
    }

    [Fact]
    public void CanPreview_NoWorldData_IsNotEligible()
    {
        Assert.False(ReferencePreviewEligibility.CanPreview(MakeReference(), data: null));
    }

    /// <summary>v3 renders static meshes only, so skinned actors have no renderable output.</summary>
    [Theory]
    [InlineData("ACHR", "placed NPC")]
    [InlineData("ACRE", "placed creature")]
    public void CanPreview_ActorReference_IsNotEligible(string recordType, string because)
    {
        _ = because;

        var reference = MakeReference() with { RecordType = recordType };

        Assert.False(ReferencePreviewEligibility.CanPreview(reference, MakeData()));
    }

    [Fact]
    public void CanPreview_OrdinaryRefWithNoModel_IsNotEligible()
    {
        var reference = MakeReference() with { ModelPath = null };

        Assert.False(ReferencePreviewEligibility.CanPreview(reference, MakeData()));
    }

    /// <summary>
    ///     A modelless ref still counts when its base is a LIGH that emits — the toggle then acts on
    ///     the emitter rather than a mesh.
    /// </summary>
    [Fact]
    public void CanPreview_ModellessRefBackedByAnEmittingLight_IsEligible()
    {
        var reference = MakeReference() with { ModelPath = null };
        var data = MakeData(lights: new Dictionary<uint, LightRecord>
        {
            [BaseFormId] = MakeLight(radius: 512u, flags: 0u)
        });

        Assert.True(ReferencePreviewEligibility.CanPreview(reference, data));
    }

    /// <summary>
    ///     A LIGH that produces no emission is as inert as a missing mesh, so it must not earn a
    ///     toggle either.
    /// </summary>
    [Fact]
    public void CanPreview_ModellessRefBackedByANonEmittingLight_IsNotEligible()
    {
        var reference = MakeReference() with { ModelPath = null };
        var data = MakeData(lights: new Dictionary<uint, LightRecord>
        {
            [BaseFormId] = MakeLight(radius: 0u, flags: 0u)
        });

        Assert.False(ReferencePreviewEligibility.CanPreview(reference, data));
    }

    [Fact]
    public void IsAuthoredEnabled_PlainReference_IsEnabled()
    {
        Assert.True(ReferencePreviewEligibility.IsAuthoredEnabled(MakeReference(), MakeData()));
    }

    [Fact]
    public void IsAuthoredEnabled_InitiallyDisabledFlag_IsDisabled()
    {
        var reference = MakeReference() with { IsInitiallyDisabled = true };

        Assert.False(ReferencePreviewEligibility.IsAuthoredEnabled(reference, MakeData()));
    }

    /// <summary>The XESP parent chain is resolved upstream; membership alone means disabled.</summary>
    [Fact]
    public void IsAuthoredEnabled_XespDisabledReference_IsDisabled()
    {
        var data = MakeData(xespDisabled: [RefFormId]);

        Assert.False(ReferencePreviewEligibility.IsAuthoredEnabled(MakeReference(), data));
    }

    /// <summary>
    ///     The base LIGH's Off By Default bit governs the emitter only. Letting it speak for the
    ///     placement would wrongly report an attached lantern's mesh as hidden.
    /// </summary>
    [Fact]
    public void IsAuthoredEnabled_IgnoresTheBaseLightsOffByDefaultBit()
    {
        var data = MakeData(lights: new Dictionary<uint, LightRecord>
        {
            [BaseFormId] = MakeLight(radius: 512u, flags: PlacedLight.OffByDefaultFlag)
        });

        Assert.True(ReferencePreviewEligibility.IsAuthoredEnabled(MakeReference(), data));
        Assert.False(ReferencePreviewEligibility.IsBaseLightAuthoredEnabled(MakeReference(), data));
    }

    [Fact]
    public void IsBaseLightAuthoredEnabled_NoBackingLightRecord_IsNull()
    {
        Assert.Null(ReferencePreviewEligibility.IsBaseLightAuthoredEnabled(MakeReference(), MakeData()));
    }

    [Fact]
    public void IsBaseLightAuthoredEnabled_LightWithoutTheOffBit_IsEnabled()
    {
        var data = MakeData(lights: new Dictionary<uint, LightRecord>
        {
            [BaseFormId] = MakeLight(radius: 512u, flags: 0u)
        });

        Assert.True(ReferencePreviewEligibility.IsBaseLightAuthoredEnabled(MakeReference(), data));
    }

    private static PlacedReference MakeReference()
    {
        return new PlacedReference
        {
            FormId = RefFormId,
            BaseFormId = BaseFormId,
            RecordType = "REFR",
            ModelPath = @"meshes\clutter\test.nif",
            X = 100f,
            Y = 200f,
            Z = 0f
        };
    }

    /// <summary>A white LIGH. Radius 0 is the "emits nothing" case.</summary>
    private static LightRecord MakeLight(uint radius, uint flags)
    {
        return new LightRecord
        {
            FormId = BaseFormId,
            Radius = radius,
            Flags = flags,
            Color = 0x00FFFFFF
        };
    }

    private static WorldViewData MakeData(
        IReadOnlyDictionary<uint, LightRecord>? lights = null,
        IReadOnlyCollection<uint>? xespDisabled = null)
    {
        return new WorldViewData
        {
            Worldspaces = [],
            InteriorCells = [],
            BoundsIndex = [],
            CategoryIndex = [],
            Resolver = FormIdResolver.Empty,
            MapMarkers = [],
            MarkersByWorldspace = new Dictionary<uint, List<PlacedReference>>(),
            AllCells = [],
            CellByFormId = new Dictionary<uint, CellRecord>(),
            PlacedRefs = PlacedRefIndex.Empty,
            UnlinkedExteriorCells = [],
            UnlinkedMapMarkers = [],
            Game = BethesdaGame.FalloutNewVegas,
            LightsByFormId = lights ?? new Dictionary<uint, LightRecord>(),
            XespDisabledRefs = xespDisabled ?? new HashSet<uint>()
        };
    }
}
