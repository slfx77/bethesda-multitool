namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

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
