using FalloutXbox360Utils.Tests.Core.Formats.Script;

[assembly: Xunit.AssemblyFixture(typeof(FalloutXbox360Utils.Tests.SampleFileFixture))]

namespace FalloutXbox360Utils.Tests;

/// <summary>
///     Assembly-level fixture that checks sample file availability once per test run.
///     Tests inject this via constructor and call Assert.SkipWhen() for proper skip reporting.
/// </summary>
public class SampleFileFixture
{
    /// <summary>Xbox 360 final ESM (full retail).</summary>
    public string? Xbox360FinalEsm { get; } = FindSamplePath(@"Sample\ESM\360_final\FalloutNV.esm");

    /// <summary>Xbox 360 proto ESM (development build).</summary>
    public string? Xbox360ProtoEsm { get; } = FindSamplePath(@"Sample\ESM\360_proto\FalloutNV.esm");

    /// <summary>PC final ESM (retail).</summary>
    public string? PcFinalEsm { get; } = FindSamplePath(@"Sample\ESM\pc_final\FalloutNV.esm");

    /// <summary>Debug build memory dump.</summary>
    public string? DebugDump { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");

    /// <summary>Release beta memory dump.</summary>
    public string? ReleaseDump { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Release_Beta.xex.dmp");

    private static string? FindSamplePath(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return null;
    }
}
