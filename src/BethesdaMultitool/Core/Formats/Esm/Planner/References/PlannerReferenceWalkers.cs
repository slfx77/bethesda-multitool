using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References;

/// <summary>
///     Central catalog of top-level reference walkers used by the production planner. Keeping this
///     list outside the pipeline constructor makes walker coverage directly testable.
/// </summary>
public static class PlannerReferenceWalkers
{
    public static IEnumerable<IRecordReferenceWalker> BuildAll()
    {
        yield return new ScriptReferenceWalker();
        yield return new ImageSpaceModifierReferenceWalker();
        yield return new PackageReferenceWalker();
        yield return new TerminalReferenceWalker();
        yield return new InfoReferenceWalker();
        yield return new NpcReferenceWalker();
        yield return new CreatureReferenceWalker();
        yield return new PerkReferenceWalker();
        yield return new ConsumableReferenceWalker();
        yield return new EnchantmentReferenceWalker();
        yield return new SpellReferenceWalker();
    }
}
