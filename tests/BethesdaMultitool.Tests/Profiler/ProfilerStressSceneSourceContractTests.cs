using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class ProfilerStressSceneSourceContractTests
{
    [Fact]
    public void WastelandHeavyFinder_IsGuardedBySelectedMojaveWorldspace()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Camera.cs");
        var apply = SourceContract.Extract(
            source,
            "private void ApplyStressSceneBookmarkIfRequested()",
            "private bool IsWastelandNvHeavyStressScene()");

        SourceContract.AssertOrder(
            apply,
            "var selectedWorldspace = CurrentSelectedExteriorWorldspace();",
            "!WorldspaceLooksLikeWastelandNv(selectedWorldspace)",
            "return;",
            "WorldViewStressBookmarkFinder.FindWastelandNvHeavyBookmark(");
    }
}
