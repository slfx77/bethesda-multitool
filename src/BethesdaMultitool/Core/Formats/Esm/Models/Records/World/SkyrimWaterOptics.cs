using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Exact Skyrim LE WATR optical source values, independent of FNV's normalized shoreline feather.
///     TESV 0054CD27 loads DNAM into TESWaterForm+80; UpdateWaterMaterial 0054D9B0 and the
///     named depth getters prove these offsets. DepthControl contains four independent effect lanes,
///     not an RGB tint. This bounded decoder does not opt SSE or Xbox layouts into the PC consumer.
/// </summary>
public sealed record SkyrimWaterOptics(
    float AboveWaterFogNear,
    float AboveWaterFogFar,
    float AboveWaterFogAmount,
    (float Reflections, float Refraction, float Normals, float Specular) DepthControl)
{
    public bool IsFinite =>
        float.IsFinite(AboveWaterFogNear) && float.IsFinite(AboveWaterFogFar) &&
        float.IsFinite(AboveWaterFogAmount) &&
        float.IsFinite(AboveWaterFogFar - AboveWaterFogNear) &&
        float.IsFinite(4f * AboveWaterFogAmount) &&
        float.IsFinite(DepthControl.Reflections) && float.IsFinite(DepthControl.Refraction) &&
        float.IsFinite(DepthControl.Normals) && float.IsFinite(DepthControl.Specular);

    internal static SkyrimWaterOptics? TryRead(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        if (isBigEndian || data.Length != 228) return null;

        static float Read(ReadOnlySpan<byte> bytes, int offset) =>
            BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, sizeof(float)));

        var result = new SkyrimWaterOptics(
            Read(data, 32), Read(data, 36), Read(data, 132),
            (Read(data, 208), Read(data, 212), Read(data, 216), Read(data, 220)));
        return result.IsFinite ? result : null;
    }
}
