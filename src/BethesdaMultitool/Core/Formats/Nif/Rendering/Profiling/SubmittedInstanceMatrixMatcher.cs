using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

/// <summary>Matches placement matrices only within the CPU instance window submitted by a draw.</summary>
internal static class SubmittedInstanceMatrixMatcher
{
    /// <summary>Returns a draw-local exact match index, or -1 for absent, empty, or invalid windows.</summary>
    internal static int FindExactMatch(
        ReadOnlySpan<Matrix4x4> instances, int start, int count, Matrix4x4 expected)
    {
        if (start < 0 || count <= 0 || start > instances.Length || count > instances.Length - start)
        {
            return -1;
        }

        var submitted = instances.Slice(start, count);
        for (var index = 0; index < submitted.Length; index++)
        {
            if (submitted[index] == expected)
            {
                return index;
            }
        }

        return -1;
    }
}
