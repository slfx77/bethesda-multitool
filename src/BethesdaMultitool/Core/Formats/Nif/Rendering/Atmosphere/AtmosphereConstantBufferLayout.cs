namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Register layout shared by every CPU writer of the scene <c>b3</c> atmosphere constant
///     buffer. Keep this append-only and in lockstep with <c>Shaders/Include/atmosphere.hlsli</c>.
/// </summary>
internal static class AtmosphereConstantBufferLayout
{
    internal const int Float4ByteSize = 16;

    internal const int BaseFloat4Count = 10;
    internal const int ShadowMatrixFloat4Count = 4 * 4;
    internal const int ShadowParameterFloat4Count = 4;
    internal const int DirectionalAmbientCubeFloat4Count = 6;

    internal const int GrassSunColorScaleFloat4Slot =
        BaseFloat4Count + ShadowMatrixFloat4Count + ShadowParameterFloat4Count +
        DirectionalAmbientCubeFloat4Count;

    internal const int ClipPlaneFloat4Slot = GrassSunColorScaleFloat4Slot + 1;
    internal const int SkyrimDirectionalAmbientRow0Float4Slot = ClipPlaneFloat4Slot + 1;
    internal const int SkyrimDirectionalAmbientRow1Float4Slot = SkyrimDirectionalAmbientRow0Float4Slot + 1;
    internal const int SkyrimDirectionalAmbientRow2Float4Slot = SkyrimDirectionalAmbientRow1Float4Slot + 1;
    internal const int DirectionalAmbientModeFloat4Slot = SkyrimDirectionalAmbientRow2Float4Slot + 1;

    internal const int Float4Count = DirectionalAmbientModeFloat4Slot + 1;
    internal const uint ByteSize = Float4Count * Float4ByteSize;
}
