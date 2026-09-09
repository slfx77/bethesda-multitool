using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>Authored TES4 102-byte DATA inputs, before WaterAppearance's numeric fallbacks.</summary>
internal sealed record OblivionWaterSimulationInputs
{
    private OblivionWaterSimulationInputs(uint waterFormId, string dataSha256, bool bigEndian,
        OblivionWaterSimulationCoefficients rain, OblivionWaterSimulationCoefficients wading)
    {
        WaterFormId = waterFormId;
        DataSha256 = dataSha256;
        BigEndian = bigEndian;
        Rain = rain;
        Wading = wading;
    }

    internal uint WaterFormId { get; }
    internal string DataSha256 { get; }
    internal bool BigEndian { get; }
    internal OblivionWaterSimulationCoefficients Rain { get; }
    internal OblivionWaterSimulationCoefficients Wading { get; }

    /// <summary>
    ///     Reads only the full authored cohort through the existing size-versioned DATA decoder.
    ///     Older 86-byte data has different offsets and lacks both dampeners/starting sizes.
    ///     Authored zero and negative finite values survive; this descriptor does not tune them.
    /// </summary>
    internal static OblivionWaterSimulationInputs? Read(
        BethesdaGame game, uint waterFormId, ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (game != BethesdaGame.Oblivion || data.Length != 102 || waterFormId == 0)
        {
            return null;
        }

        var properties = MiscEnvironmentHandler.ReadOblivionWaterData(data, bigEndian);
        var rain = ReadCoefficients(properties, "Rain");
        var wading = ReadCoefficients(properties, "Displacement");
        return rain.HasValue && wading.HasValue
            ? new OblivionWaterSimulationInputs(waterFormId, Convert.ToHexString(SHA256.HashData(data)),
                bigEndian, rain.Value, wading.Value)
            : null;
    }

    private static OblivionWaterSimulationCoefficients? ReadCoefficients(
        Dictionary<string, object?> properties, string prefix)
    {
        // The canonical decoder produces float values. Missing or differently typed properties
        // cannot be repaired by WaterAppearance's fallback or numeric coercion here.
        if (!ReadFloat(properties, prefix + "Force", out var force) ||
            !ReadFloat(properties, prefix + "Velocity", out var velocity) ||
            !ReadFloat(properties, prefix + "Falloff", out var falloff) ||
            !ReadFloat(properties, prefix + "Dampener", out var dampener) ||
            !ReadFloat(properties, prefix + "StartingSize", out var startingSize))
        {
            return null;
        }

        return new OblivionWaterSimulationCoefficients(force, velocity, falloff, dampener, startingSize);
    }

    private static bool ReadFloat(Dictionary<string, object?> properties, string name, out float value)
    {
        value = 0f;
        if (!properties.TryGetValue(name, out var raw) || raw is not float number || !float.IsFinite(number))
        {
            return false;
        }

        value = number;
        return true;
    }
}

internal readonly record struct OblivionWaterSimulationCoefficients(
    float Force,
    float Velocity,
    float Falloff,
    float Dampener,
    float StartingSize);
