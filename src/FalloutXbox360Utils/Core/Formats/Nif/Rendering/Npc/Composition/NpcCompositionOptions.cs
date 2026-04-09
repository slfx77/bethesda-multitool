using FalloutXbox360Utils.CLI.Rendering.Npc;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class NpcCompositionOptions : IEquatable<NpcCompositionOptions>
{
    public bool HeadOnly { get; init; }
    public bool IncludeEquipment { get; init; } = true;
    public bool IncludeWeapon { get; init; } = true;
    public bool BindPose { get; init; }
    public bool ApplyEgm { get; init; } = true;
    public bool ApplyEgt { get; init; } = true;
    public bool IncludeHair { get; init; } = true;
    public string? AnimOverride { get; init; }

    public bool Equals(NpcCompositionOptions? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return HeadOnly == other.HeadOnly &&
               IncludeEquipment == other.IncludeEquipment &&
               IncludeWeapon == other.IncludeWeapon &&
               BindPose == other.BindPose &&
               ApplyEgm == other.ApplyEgm &&
               ApplyEgt == other.ApplyEgt &&
               string.Equals(AnimOverride, other.AnimOverride, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as NpcCompositionOptions);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            HeadOnly,
            IncludeEquipment,
            IncludeWeapon,
            BindPose,
            ApplyEgm,
            ApplyEgt,
            AnimOverride?.ToUpperInvariant());
    }

    public static NpcCompositionOptions From(NpcRenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new NpcCompositionOptions
        {
            HeadOnly = settings.HeadOnly,
            IncludeEquipment = !settings.NoEquip,
            IncludeWeapon = !settings.NoEquip && !settings.NoWeapon,
            BindPose = settings.BindPose,
            ApplyEgm = !settings.NoEgm,
            ApplyEgt = !settings.NoEgt,
            IncludeHair = true,
            AnimOverride = settings.AnimOverride
        };
    }

    public static NpcCompositionOptions From(NpcExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new NpcCompositionOptions
        {
            HeadOnly = settings.HeadOnly,
            IncludeEquipment = !settings.NoEquip,
            IncludeWeapon = !settings.NoEquip && settings.IncludeWeapon,
            BindPose = settings.BindPose,
            ApplyEgm = !settings.NoEgm,
            ApplyEgt = !settings.NoEgt,
            IncludeHair = !settings.NoHair,
            AnimOverride = settings.AnimOverride
        };
    }

}
