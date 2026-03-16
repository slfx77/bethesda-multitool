using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Resolved weapon render decision.
/// </summary>
internal sealed class WeaponVisual
{
    public uint? WeaponFormId { get; init; }
    public string? EditorId { get; init; }
    public WeaponVisualSourceKind SourceKind { get; init; }
    public bool IsVisible { get; init; }
    public WeaponType WeaponType { get; init; }
    public WeaponAttachmentMode AttachmentMode { get; init; }
    public string? MeshPath { get; init; }
    public string? HolsterProfileKey { get; init; }
    public uint? RuntimeActorFormId { get; init; }
    public uint? AmmoFormId { get; init; }
    public bool IsEmbeddedWeapon { get; init; }
    public string? EmbeddedWeaponNode { get; init; }
    public string? EquippedPoseKfPath { get; init; }
    public bool PreferEquippedForearmMount { get; init; }
    public bool RenderStandaloneMesh { get; init; } = true;
    public List<WeaponAddonVisual>? AddonMeshes { get; init; }
}
