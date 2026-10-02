using System.Globalization;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Reconstructs the selected engine's ACBS level routine; it does not simulate spawning.</summary>
internal static class ActorLevelCalculation
{
    private const string Rule = "PC:0047DED0 (ACBS level); divisor 01017B70 = 1000; truncate, low UInt16, min then max";

    internal static ActorStatisticValue Evaluate(ActorInspection actor, ActorEngineProfile? profile,
        ushort? playerLevel, string? scenarioExecutableHash)
    {
        ActorStatisticValue Missing(params string[] dependencies) => new("EffectiveLevel", "Unavailable", null,
            profile is null ? null : Rule, dependencies) { EngineProfile = profile?.Id };
        if (profile is null) return Missing("engine-profile");
        if (actor.Game != BethesdaGame.FalloutNewVegas) return Missing("verified Fallout: New Vegas source identity");
        if (scenarioExecutableHash is not null && !string.Equals(scenarioExecutableHash, profile.ExecutableSha256,
                StringComparison.OrdinalIgnoreCase)) return Missing("scenario executable does not match engine profile");
        var group = actor.TemplateGroups.SingleOrDefault(item => item.Group == ActorTemplateGroup.UseStats);
        if (group?.IsResolved != true) return Missing($"UseStats:{group?.Status ?? "unresolved"}");
        var fields = actor.EffectiveStatistics.Where(item => item.Group == ActorTemplateGroup.UseStats)
            .ToDictionary(item => item.Key, StringComparer.Ordinal);
        var scaled = fields.ContainsKey("LevelEncoded");
        var key = scaled ? "LevelEncoded" : "Level";
        if (!fields.TryGetValue(key, out var level) || !short.TryParse(level.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var raw)) return Missing($"effective ACBS.{key}");
        var inputs = new List<ActorCalculationInput>
        {
            new(key, raw, level.Provenance, level.SourceActor, level.SourcePlugin)
        };
        ushort? minimum = null;
        ushort? maximum = null;
        if (scaled)
        {
            if (playerLevel is null) return Missing("scenario.playerLevel (player base ACBS level)");
            if (!ReadBound("MinimumLevel", out var low) || !ReadBound("MaximumLevel", out var high))
                return Missing("effective ACBS minimum and maximum levels");
            minimum = low;
            maximum = high;
            inputs.Add(new("PlayerLevel", playerLevel.Value, "Scenario", null, null));
        }
        return new("EffectiveLevel", "Calculated", Calculate(raw, scaled, playerLevel, minimum, maximum), Rule, [])
        {
            EngineProfile = profile.Id,
            CalculationBasis = "Reconstruction",
            Inputs = inputs
        };

        bool ReadBound(string name, out ushort value)
        {
            value = 0;
            if (!fields.TryGetValue(name, out var field) || !ushort.TryParse(field.Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;
            inputs.Add(new(name, value, field.Provenance, field.SourceActor, field.SourcePlugin));
            return true;
        }
    }

    /// <summary>Exact integer and floating stages from the retained PC routine, including malformed bounds.</summary>
    internal static ushort Calculate(short rawLevel, bool scaled, ushort? playerLevel,
        ushort? minimum, ushort? maximum)
    {
        var value = unchecked((ushort)rawLevel);
        if (!scaled) return value;
        if (playerLevel is null || minimum is null || maximum is null)
            throw new ArgumentException("Scaled calculation requires player, minimum and maximum levels.");
        // The game-thread FNSTCW observation is 0x007F: 24-bit arithmetic precision, nearest rounding.
        // FDIV stores a float multiplier, and FMUL rounds its product before the routine switches
        // to truncation for FISTP. Multiplying as double instead makes 1.3f * 10 truncate to 12.
        var multiplier = (float)(value / 1000d);
        var product = playerLevel.Value * multiplier;
        value = unchecked((ushort)(int)MathF.Truncate(product));
        // The engine returns immediately on the lower clamp; contradictory bounds are not normalized.
        if (minimum.Value > 0 && value < minimum.Value) return minimum.Value;
        if (maximum.Value > 0 && value > maximum.Value) return maximum.Value;
        return value;
    }
}
