using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Reads the ARMO/ARMA <c>DNAM</c> armor-defense fields. Both records place Damage Resistance (Int16)
///     at +0 and the Damage Threshold (float) at +4 (FNV ARMO DNAM is 12 bytes, ARMA's is 8). Fallout 3 —
///     the format the earliest Xbox 360 crash dumps carry — uses a short 4-byte DR-only block with no DT.
///     DT presence is therefore a subrecord-LENGTH property, not a game/version flag: read DT iff the DNAM
///     reaches the field. This keeps both on-disk ESMs and carved little-endian dump fragments correct
///     without a version axis. The normalizer clamps out-of-range / garbage values to 0.
/// </summary>
internal static class ArmorDefenseData
{
    public static (int DamageResistance, float DamageThreshold) Read(ReadOnlySpan<byte> data, bool bigEndian)
    {
        var damageResistance = bigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(data)
            : BinaryPrimitives.ReadInt16LittleEndian(data);
        var damageThreshold = 0f;
        if (data.Length >= 8)
        {
            damageThreshold = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(data[4..])
                : BinaryPrimitives.ReadSingleLittleEndian(data[4..]);
        }

        return (
            GameStatNormalizer.ArmorDamageResistance(damageResistance),
            GameStatNormalizer.ArmorDamageThreshold(damageThreshold));
    }
}
