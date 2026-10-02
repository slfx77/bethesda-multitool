using BethesdaMultitool.Core.Actors;

namespace BethesdaMultitool;

/// <summary>
///     Captures one selected actor preview/export, including its exact generated inventory when supplied.
///     A null generation preserves existing static resolution; an empty generation clears equipment.
/// </summary>
/// <param name="HeadOnly">Whether composition omits the body.</param>
/// <param name="NoEquip">Whether composition omits armor and clothing.</param>
/// <param name="NoWeapon">Whether composition omits the selected weapon.</param>
/// <param name="BindPose">Whether the native scene retains the skeleton bind pose.</param>
/// <param name="PreviewPlayerLevel">Explicit level supplied to legacy level-aware resolution.</param>
/// <param name="Generation">Retained concrete inventory shared by the selected preview and exports; null uses authored resolution.</param>
internal sealed record NpcRenderOptions(
    bool HeadOnly,
    bool NoEquip,
    bool NoWeapon,
    bool BindPose,
    ushort? PreviewPlayerLevel = null,
    ActorInventoryGeneration? Generation = null);
