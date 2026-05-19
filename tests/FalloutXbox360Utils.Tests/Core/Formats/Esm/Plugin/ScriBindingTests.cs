using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v22: <see cref="PluginBuilder.IsValidScriTarget" /> determines whether an SCRI
///     subrecord's target FormID is allowed to reach the output ESP. The pre-v22 predicate
///     only checked master ESM FormIDs, which meant reintroduced prototype NPCs pointing at
///     reintroduced prototype scripts had their SCRI dropped (the new SCPT FormID isn't in
///     the master ESM). v22 also accepts FormIDs being emitted via the new-record path in
///     the same Build run, so SCPT-before-NPC GRUP ordering keeps the bindings intact.
/// </summary>
public class ScriBindingTests
{
    [Fact]
    public void IsValidScriTarget_ZeroSentinel_AlwaysAllowed()
    {
        Assert.True(PluginBuilder.IsValidScriTarget(0u, masterFormIds: null, emittedNewFormIds: null));
        Assert.True(PluginBuilder.IsValidScriTarget(0u, new HashSet<uint>(), new HashSet<uint>()));
    }

    [Fact]
    public void IsValidScriTarget_AllOnesSentinel_AlwaysAllowed()
    {
        Assert.True(PluginBuilder.IsValidScriTarget(0xFFFFFFFFu, null, null));
    }

    [Fact]
    public void IsValidScriTarget_MasterFormId_Allowed()
    {
        var masterFormIds = new HashSet<uint> { 0x00012345 };
        Assert.True(PluginBuilder.IsValidScriTarget(0x00012345, masterFormIds, emittedNewFormIds: null));
    }

    [Fact]
    public void IsValidScriTarget_NewlyEmittedFormId_Allowed_v22()
    {
        // This is the v22 fix: a prototype-only SCPT being freshly emitted in the same
        // Build run is a valid SCRI target even though it's not in the master ESM.
        var emittedNew = new HashSet<uint> { 0x0F123456 };
        Assert.True(PluginBuilder.IsValidScriTarget(0x0F123456, masterFormIds: null, emittedNewFormIds: emittedNew));
    }

    [Fact]
    public void IsValidScriTarget_UnknownFormId_Rejected()
    {
        // Anything not in master, not in emittedNew, not a sentinel = dangling reference;
        // gets dropped by ValidateScriRefs so the runtime doesn't null-deref on bind.
        var masterFormIds = new HashSet<uint> { 0x00012345 };
        var emittedNew = new HashSet<uint> { 0x0F123456 };
        Assert.False(PluginBuilder.IsValidScriTarget(0xDEADBEEF, masterFormIds, emittedNew));
    }

    [Fact]
    public void IsValidScriTarget_BothSetsNull_OnlySentinelsAllowed()
    {
        // Defensive: when neither set is populated (e.g. ValidateScriRefs called before
        // Build initialized the FormID sets), only sentinel FormIDs pass.
        Assert.True(PluginBuilder.IsValidScriTarget(0u, null, null));
        Assert.True(PluginBuilder.IsValidScriTarget(0xFFFFFFFFu, null, null));
        Assert.False(PluginBuilder.IsValidScriTarget(0x00012345, null, null));
    }
}
