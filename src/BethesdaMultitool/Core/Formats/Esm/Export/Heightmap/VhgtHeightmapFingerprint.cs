using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;

/// <summary>Compact fingerprint (base-height bits + hashed deltas) used to match VHGT heightmaps to LAND records.</summary>
internal readonly record struct VhgtHeightmapFingerprint(uint HeightOffsetBits, int DeltaHash)
{
    /// <summary>Computes a fingerprint from a detected standalone VHGT heightmap.</summary>
    public static VhgtHeightmapFingerprint From(DetectedVhgtHeightmap heightmap)
    {
        return new VhgtHeightmapFingerprint(
            BitConverter.SingleToUInt32Bits(heightmap.HeightOffset),
            HashDeltas(heightmap.HeightDeltas));
    }

    /// <summary>Computes a fingerprint from a LAND record's parsed heightmap.</summary>
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
