namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Shared contract for the 32-frame <c>textures\water\water00..31.dds</c> animation used by
///     Morrowind and Oblivion. Morrowind samples the frames as its diffuse surface; Oblivion binds
///     them as WATER000's global NormalMap. In both games the shipped INI selects 12 frames/second.
/// </summary>
internal static class LegacyWaterAnimation
{
    internal const int FrameCount = 32;

    internal static string FramePath(int frameIndex)
    {
        if ((uint)frameIndex >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        return $@"textures\water\water{frameIndex:D2}.dds";
    }

    /// <summary>
    ///     The shipped frame paths that ACTUALLY exist in the loaded game data, in frame order.
    ///     <para>
    ///         Callers MUST gate on this rather than on a null bindless index. The texture cache never
    ///         reports a miss: <c>GpuTextureCache12.GetOrUpload</c> hands back a pinned FlatNormal /
    ///         WhitePixel PLACEHOLDER for any non-empty path and resolves the real payload in the
    ///         background, so a resolve of a file that does not exist still yields a perfectly valid,
    ///         non-null, permanently-placeholder bindless index. Retail Oblivion ships no
    ///         <c>water00-31.dds</c> at all (the engine generates its surface at runtime), so probing
    ///         by index produced 32 usable-looking frames that were all the same flat normal — the
    ///         animation advanced every frame and the water never moved.
    ///     </para>
    /// </summary>
    internal static List<string> ExistingFramePaths(Func<string, bool> frameExists)
    {
        ArgumentNullException.ThrowIfNull(frameExists);

        var paths = new List<string>(FrameCount);
        for (var i = 0; i < FrameCount; i++)
        {
            var path = FramePath(i);
            if (frameExists(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    ///     Selects the current looping frame. The elapsed-time guard keeps device-reset or clock
    ///     anomalies from producing a negative/out-of-range array index.
    /// </summary>
    internal static int SelectFrame(float elapsedSeconds, float framesPerSecond, int frameCount)
    {
        if (frameCount <= 0 || !float.IsFinite(elapsedSeconds) || !float.IsFinite(framesPerSecond) ||
            elapsedSeconds <= 0f || framesPerSecond <= 0f)
        {
            return 0;
        }

        var framePosition = elapsedSeconds * framesPerSecond;
        if (!float.IsFinite(framePosition)) return 0;

        var absoluteFrame = (long)MathF.Floor(framePosition);
        return (int)(absoluteFrame % frameCount);
    }
}
