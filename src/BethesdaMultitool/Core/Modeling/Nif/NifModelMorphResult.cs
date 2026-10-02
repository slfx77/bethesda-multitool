using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What <see cref="NifModelMorphReader" /> found for one geometry.</summary>
internal sealed class NifModelMorphResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="targets">The typed targets (morphs 1..n), empty when there are none or they cannot be typed.</param>
    /// <param name="typedDataBlock">The NiMorphData block the targets came from, or null.</param>
    /// <param name="dispositions">Coverage decisions for the morph data blocks this geometry reached.</param>
    /// <param name="facts">The native facts, or null when the geometry has no morpher controller.</param>
    public NifModelMorphResult(IReadOnlyList<SceneMorphTarget> targets, int? typedDataBlock,
        IReadOnlyDictionary<int, NifModelBlockDisposition> dispositions, JsonObject? facts)
    {
        Targets = targets;
        TypedDataBlock = typedDataBlock;
        Dispositions = dispositions;
        Facts = facts;
    }

    /// <summary>No morpher controller on the geometry.</summary>
    public static NifModelMorphResult None { get; } = new([], null, new Dictionary<int, NifModelBlockDisposition>(), null);

    /// <summary>The typed targets in morph order (morph 0, the base, is not a target).</summary>
    public IReadOnlyList<SceneMorphTarget> Targets { get; }

    /// <summary>The NiMorphData block the targets came from, or null.</summary>
    public int? TypedDataBlock { get; }

    /// <summary>Coverage decisions for the morph data blocks this geometry reached.</summary>
    public IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions { get; }

    /// <summary>The native facts for the primitive row, or null when there is no morpher controller.</summary>
    /// <remarks>Owned by one primitive row: <see cref="None" /> carries none, every other result is built per geometry.</remarks>
    public JsonObject? Facts { get; }
}
