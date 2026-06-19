using BethesdaMultitool.CLI.Rendering.Npc;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>The options that drive how a creature is composed for render/export (weapon, pose, morphs, animation override); used as a cache key.</summary>
internal sealed class CreatureCompositionOptions : IEquatable<CreatureCompositionOptions>
{
    public bool IncludeWeapon { get; init; } = true;
    public bool BindPose { get; init; }
    public bool ApplyEgm { get; init; } = true;
    public bool ApplyEgt { get; init; } = true;
    public string? AnimOverride { get; init; }

    public bool Equals(CreatureCompositionOptions? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return IncludeWeapon == other.IncludeWeapon &&
               BindPose == other.BindPose &&
               ApplyEgm == other.ApplyEgm &&
               ApplyEgt == other.ApplyEgt &&
               string.Equals(AnimOverride, other.AnimOverride, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as CreatureCompositionOptions);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            IncludeWeapon,
            BindPose,
            ApplyEgm,
            ApplyEgt,
            AnimOverride?.ToUpperInvariant());
    }

    /// <summary>Derives composition options from the render-command settings.</summary>
    public static CreatureCompositionOptions From(NpcRenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new CreatureCompositionOptions
        {
            IncludeWeapon = !settings.NoEquip && !settings.NoWeapon,
            BindPose = settings.BindPose,
            ApplyEgm = !settings.NoEgm,
            ApplyEgt = !settings.NoEgt,
            AnimOverride = settings.AnimOverride
        };
    }

    /// <summary>Derives composition options from the export-command settings.</summary>
    public static CreatureCompositionOptions From(NpcExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new CreatureCompositionOptions
        {
            IncludeWeapon = !settings.NoEquip && settings.IncludeWeapon,
            BindPose = settings.BindPose,
            ApplyEgm = !settings.NoEgm,
            ApplyEgt = !settings.NoEgt,
            AnimOverride = settings.AnimOverride
        };
    }
}
