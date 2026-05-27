using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal readonly record struct VhgtHeightmapFingerprint(uint HeightOffsetBits, int DeltaHash)
{
    public static VhgtHeightmapFingerprint From(DetectedVhgtHeightmap heightmap)
    {
        return new VhgtHeightmapFingerprint(
            BitConverter.SingleToUInt32Bits(heightmap.HeightOffset),
            HashDeltas(heightmap.HeightDeltas));
    }

    public static VhgtHeightmapFingerprint From(LandHeightmap heightmap)
    {
        return new VhgtHeightmapFingerprint(
            BitConverter.SingleToUInt32Bits(heightmap.HeightOffset),
            HashDeltas(heightmap.HeightDeltas));
    }

    private static int HashDeltas(sbyte[] deltas)
    {
        unchecked
        {
            var hash = (int)2166136261u;
            hash = (hash ^ deltas.Length) * 16777619;
            foreach (var delta in deltas)
            {
                hash = (hash ^ (byte)delta) * 16777619;
            }

            return hash;
        }
    }
}
