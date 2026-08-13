using BethesdaMultitool.Tests.Helpers;
using EsmAnalyzer.Commands.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Tools.EsmAnalyzer;

/// <summary>
///     Pins worldspace resolution for the two EsmAnalyzer terrain commands. The deleted
///     <c>FalloutWorldspaces</c> table hardcoded Fallout: New Vegas FormIDs and aliased
///     <c>"Wasteland"</c> to FNV's <c>WastelandNV</c> (0x000DA726) — but Fallout 3's exterior
///     worldspace is literally named <c>Wasteland</c> with FormID 0x0000003C, so
///     <c>-w Wasteland</c> against Fallout3.esm resolved to an ID that file does not contain and
///     the commands reported it as missing terrain. Resolution now matches the file's own WRLD
///     records; these facts exist because the table and both call sites had zero tests.
/// </summary>
public sealed class WorldspaceSelectorTests
{
    private const uint Fo3WastelandFormId = 0x0000003C;
    private const uint FnvWastelandNvFormId = 0x000DA726;

    /// <summary>A file shaped like Fallout3.esm: one exterior worldspace named "Wasteland".</summary>
    private static byte[] BuildFo3LikeFile() =>
        new EsmTestFileBuilder()
            .AddWorldspace(new EsmTestFileBuilder.WorldspaceData
            {
                FormId = Fo3WastelandFormId,
                EditorId = "Wasteland",
                FullName = "Capital Wasteland",
            })
            .Build();

    /// <summary>A file shaped like FalloutNV.esm: "WastelandNV" plus a second worldspace.</summary>
    private static byte[] BuildFnvLikeFile() =>
        new EsmTestFileBuilder()
            .AddWorldspace(new EsmTestFileBuilder.WorldspaceData
            {
                FormId = FnvWastelandNvFormId,
                EditorId = "WastelandNV",
                FullName = "Mojave Wasteland",
            })
            .AddWorldspace(new EsmTestFileBuilder.WorldspaceData
            {
                FormId = 0x00108E2D,
                EditorId = "FreesideWorld",
            })
            .Build();

    /// <summary>
    ///     The regression this exists for: FO3's "Wasteland" must resolve to FO3's own FormID, not
    ///     to the FNV worldspace the old alias table pointed at.
    /// </summary>
    [Fact]
    public void Fo3WastelandResolvesToTheFilesOwnFormIdNotTheFnvAlias()
    {
        Assert.True(WorldspaceSelector.TryResolve(
            BuildFo3LikeFile(), bigEndian: false, "Wasteland", out var name, out var formId));

        Assert.Equal("Wasteland", name);
        Assert.Equal(Fo3WastelandFormId, formId);
        Assert.NotEqual(FnvWastelandNvFormId, formId);
    }

    [Fact]
    public void NameMatchIsCaseInsensitive()
    {
        Assert.True(WorldspaceSelector.TryResolve(
            BuildFnvLikeFile(), bigEndian: false, "wastelandnv", out var name, out var formId));

        Assert.Equal("WastelandNV", name);
        Assert.Equal(FnvWastelandNvFormId, formId);
    }

    [Fact]
    public void FormIdPresentInTheFileResolvesAndReportsItsRealName()
    {
        Assert.True(WorldspaceSelector.TryResolve(
            BuildFo3LikeFile(), bigEndian: false, "0x0000003C", out var name, out var formId));

        Assert.Equal("Wasteland", name);
        Assert.Equal(Fo3WastelandFormId, formId);
    }

    /// <summary>
    ///     A syntactically valid FormID that the file does not contain must FAIL here. Previously it
    ///     was accepted and used as a filter, so the command scanned for a record that could never
    ///     match and blamed the terrain ("Found 0 CELL records").
    /// </summary>
    [Fact]
    public void FormIdAbsentFromTheFileIsRejected()
    {
        Assert.False(WorldspaceSelector.TryResolve(
            BuildFo3LikeFile(), bigEndian: false, "0x000DA726", out var name, out var formId));

        Assert.Equal(string.Empty, name);
        Assert.Equal(0u, formId);
    }

    [Fact]
    public void UnknownNameIsRejected()
    {
        Assert.False(WorldspaceSelector.TryResolve(
            BuildFo3LikeFile(), bigEndian: false, "NotAWorldspace", out _, out _));
    }

    /// <summary>
    ///     The no-argument default is a NAME preference list resolved against the file, so each game
    ///     lands on its own worldspace with no hardcoded FormIDs anywhere.
    /// </summary>
    [Fact]
    public void DefaultPrefersWastelandNvThenFallsBackToFo3sWasteland()
    {
        Assert.True(WorldspaceSelector.TryResolve(
            BuildFnvLikeFile(), bigEndian: false, null, out var fnvName, out var fnvFormId));
        Assert.Equal("WastelandNV", fnvName);
        Assert.Equal(FnvWastelandNvFormId, fnvFormId);

        Assert.True(WorldspaceSelector.TryResolve(
            BuildFo3LikeFile(), bigEndian: false, "", out var fo3Name, out var fo3FormId));
        Assert.Equal("Wasteland", fo3Name);
        Assert.Equal(Fo3WastelandFormId, fo3FormId);
    }

    [Fact]
    public void DefaultFailsWhenNeitherPreferredNameExists()
    {
        var file = new EsmTestFileBuilder()
            .AddWorldspace(new EsmTestFileBuilder.WorldspaceData
            {
                FormId = 0x00000C1B,
                EditorId = "SomeOtherWorld",
            })
            .Build();

        Assert.False(WorldspaceSelector.TryResolve(file, bigEndian: false, null, out _, out _));
    }

    [Fact]
    public void FileWithNoWorldspacesFails()
    {
        Assert.False(WorldspaceSelector.TryResolve(
            new EsmTestFileBuilder().Build(), bigEndian: false, "Wasteland", out _, out _));
    }
}
