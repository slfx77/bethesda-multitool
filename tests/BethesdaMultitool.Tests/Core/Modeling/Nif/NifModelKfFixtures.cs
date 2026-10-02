using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationReaderTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Fixtures for the cut-1b slice 10 <c>.kf</c> tests (<see cref="NifModelReaderKfTests" />, the <c>--skeleton</c> CLI
///     tests): a skeleton <c>.nif</c> (Bip01 over Bip01 Pelvis and, optionally, Bip01 Spine) and a <c>.kf</c> whose one
///     sequence 'Idle' drives the named targets with a LINEAR translation each, written field by field through
///     <see cref="NifModelAnimationReaderTestSupport" /> (nif.xml, never the reader's own decoder), plus an in-memory
///     companion resolver that answers exactly one name.
/// </summary>
internal static class NifModelKfFixtures
{
    /// <summary>The skeleton root's name.</summary>
    public const string RootName = "Bip01";

    /// <summary>A target every fixture skeleton holds.</summary>
    public const string PelvisName = "Bip01 Pelvis";

    /// <summary>A target only the full skeleton holds.</summary>
    public const string SpineName = "Bip01 Spine";

    /// <summary>An attachment name no skeleton holds (the '##' weapon nodes of plan section 0.3).</summary>
    public const string WeaponName = "##Weapon";

    /// <summary>
    ///     A skeleton: 0 NiNode <paramref name="rootName" /> over 1 NiNode 'Bip01 Pelvis' and, when
    ///     <paramref name="withSpine" />, 2 NiNode 'Bip01 Spine' (node indices follow block indices).
    /// </summary>
    public static byte[] Skeleton(bool bigEndian, bool withSpine = true, string rootName = RootName)
    {
        var builder = new NifTestFileBuilder(bigEndian, Bs);
        var root = builder.AddString(rootName);
        var pelvis = builder.AddString(PelvisName);
        if (withSpine)
        {
            var spine = builder.AddString(SpineName);
            Add(builder, 0, "NiNode", Node(root, [1, 2]));
            Add(builder, 1, "NiNode", Node(pelvis, []));
            Add(builder, 2, "NiNode", Node(spine, []));
        }
        else
        {
            Add(builder, 0, "NiNode", Node(root, [1]));
            Add(builder, 1, "NiNode", Node(pelvis, []));
        }

        return builder.Build();
    }

    /// <summary>
    ///     A <c>.kf</c>: 0 NiControllerSequence 'Idle' with one NiTransformController controlled block per target, in
    ///     order; target k's NiTransformInterpolator at block 1 + 2k and its NiTransformData (LINEAR translation keys
    ///     (0, 0, 0, 0) and (1, 1 + k, 2, 3)) at block 2 + 2k.
    /// </summary>
    public static byte[] Kf(bool bigEndian, uint bs, params string[] targets)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        var idle = builder.AddString("Idle");
        var type = builder.AddString(TransformController);
        var controlled = new (int Interpolator, int Controller, byte Priority, int Node, int PropertyType,
            int ControllerType, int ControllerId, int InterpolatorId)[targets.Length];
        for (var k = 0; k < targets.Length; k++)
        {
            controlled[k] = Controlled(1 + 2 * k, builder.AddString(targets[k]), type);
        }

        Add(builder, 0, "NiControllerSequence", Sequence(idle, controlled));
        for (var k = 0; k < targets.Length; k++)
        {
            Add(builder, 1 + 2 * k, "NiTransformInterpolator", TransformInterpolator(2 + 2 * k));
            Add(builder, 2 + 2 * k, "NiTransformData",
                TransformData(NoRotation, (0f, 0f, 0f, 0f), (1f, 1f + k, 2f, 3f)));
        }

        return builder.Build();
    }

    /// <summary>
    ///     A companion resolver over an in-memory source holding one file at <paramref name="name" />: that exact name
    ///     (ordinal) yields its occurrence, every other name nothing.
    /// </summary>
    public static ModelCompanionResolver OneFile(string name, byte[] bytes)
    {
        var source = new InMemoryAssetSource("skeletons");
        var item = new ModelSourceItem(source, source.Add(name, bytes));
        return (_, requested, _) => ValueTask.FromResult<IReadOnlyList<ModelSourceItem>>(
            string.Equals(requested, name, StringComparison.Ordinal) ? new[] { item } : Array.Empty<ModelSourceItem>());
    }
}
