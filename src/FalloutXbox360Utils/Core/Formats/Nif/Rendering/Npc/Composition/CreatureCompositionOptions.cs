using FalloutXbox360Utils.CLI.Rendering.Npc;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

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
