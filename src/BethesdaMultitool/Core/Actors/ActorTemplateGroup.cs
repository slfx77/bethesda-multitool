namespace BethesdaMultitool.Core.Actors;

/// <summary>FO3/FNV ACBS template groups; each follows its own inheritance chain.</summary>
internal enum ActorTemplateGroup : ushort
{
    UseTraits = 0x001,
    UseStats = 0x002,
    UseFactions = 0x004,
    UseActorEffectList = 0x008,
    UseAIData = 0x010,
    UseAIPackages = 0x020,
    UseModelAnimation = 0x040,
    UseBaseData = 0x080,
    UseInventory = 0x100,
    UseScript = 0x200
}
