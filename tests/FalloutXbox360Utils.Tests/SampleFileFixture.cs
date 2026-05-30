using FalloutXbox360Utils.Tests;
using Xunit;

[assembly: AssemblyFixture(typeof(SampleFileFixture))]

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

    /// <summary>Xbox 360 July 21, 2010 prototype build ESM.</summary>
    public string? Xbox360July2010Esm { get; } =
        FindSamplePath(@"Sample\Full_Builds\Fallout New Vegas (July 21, 2010)\FalloutNV\Data\FalloutNV.esm");

    /// <summary>Xbox 360 August 22, 2010 prototype build ESM.</summary>
    public string? Xbox360Aug2010Esm { get; } =
        FindSamplePath(@"Sample\Full_Builds\Fallout New Vegas (Aug 22, 2010)\Diskuild_1.0.0.252\Data\FalloutNV.esm");

    /// <summary>Debug build memory dump.</summary>
    public string? DebugDump { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Debug.xex.dmp");

    /// <summary>Release beta memory dump.</summary>
    public string? ReleaseDump { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Release_Beta.xex.dmp");

    /// <summary>Earliest snippeted Release Beta variant (xex1, Dec 3 2009).</summary>
    public string? ReleaseDumpXex1 { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Release_Beta.xex1.dmp");

    /// <summary>Early Release Beta variant (xex2, Dec 4 2009).</summary>
    public string? ReleaseDumpXex2 { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Release_Beta.xex2.dmp");

    /// <summary>Early Release Beta variant (xex3, Dec 11 2009).</summary>
    public string? ReleaseDumpXex3 { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Release_Beta.xex3.dmp");

    /// <summary>Early release beta memory dump variant.</summary>
    public string? ReleaseDumpXex4 { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Release_Beta.xex4.dmp");

    /// <summary>Late release beta memory dump variant.</summary>
    public string? ReleaseDumpXex44 { get; } = FindSamplePath(@"Sample\MemoryDump\Fallout_Release_Beta.xex44.dmp");

    public static string? FindSamplePath(string relativePath)
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