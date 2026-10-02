using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A0 on the runtime readers (cut-1b slice 1): decodes one file's animation blocks with the extended runtime
///     readers' lossless views and compares them with the independent Python probe's record
///     (<see cref="Cut1bProbeExpectations" />), bit for bit.
/// </summary>
/// <remarks>
///     <para>
///         Every entry of the record's <c>payloads</c> is compared whole: NiTransformData (rotation with TBC triples,
///         Euler axes with their own key types, translations and scales with tangents), NiFloatData, NiPosData,
///         NiBoolData, NiColorData, the NiBSpline*Interpolators, NiBSplineData and NiBSplineBasisData. A payload type
///         with no view, or a block a view refuses, is a difference, never a skip.
///     </para>
///     <para>
///         Of the record's <c>animation</c> facts, these are compared whole (every member but the type and kind labels):
///         NiControllerSequence (controlled blocks with Priority and names, the raw clock, the anim-note tail; on a
///         20.0.0.4 stream the Oblivion view's inline names, palette refs and stored offsets beside the resolved
///         strings, cut 2), NiStringPalette (cut 2), NiTextKeyExtraData, NiTransformInterpolator and
///         BSRotAccumTransfInterpolator, the B-spline blocks and the key data blocks. For every controller the 26-byte NiTimeController header is compared, member by member from a
///         fixed list (links, flags and their named bits, the clock as bits including sentinels), so a header member the
///         renderer stops emitting is a difference.
///     </para>
///     <para>
///         Outside slice 1, and reported rather than compared: every controller's type-specific members (each counted in
///         <see cref="NifAnimationViewOracleReport.NotComparedControllerMembers" />, as type.member), and the animation
///         block types in <see cref="OutOfScopeFactTypes" /> (blend, float, bool and point3 interpolators, palettes, anim
///         notes; counted in <see cref="NifAnimationViewOracleReport.NotCompared" />). That list is explicit: a fact block
///         of any other type with no slice-1 view is a difference, so a dropped case in the switch cannot turn a compared
///         type into a skipped one.
///     </para>
///     <para>
///         Every view also has to consume its block exactly where the probe's walk did (the probe fails a block it does
///         not consume); the views only report exactness, so this is asserted here, on retail bytes.
///     </para>
/// </remarks>
internal static class NifAnimationViewOracle
{
    private static readonly string[] IgnoredFactMembers = ["type", "kind"];

    /// <summary>The NiTimeController header members every controller's facts carry, compared for every controller.</summary>
    private static readonly string[] ControllerHeaderMembers =
    [
        "nextController", "flags", "animType", "cycleType", "active", "playBackwards", "managerControlled",
        "frequencyBits", "phaseBits", "startTimeBits", "stopTimeBits", "target"
    ];

    /// <summary>
    ///     The animation-fact block types slice 1 leaves out, as measured on the cut-1b manifest (3,988 blocks of these
    ///     twelve types). Adding a type here is a scope decision; every other fact type must be compared.
    /// </summary>
    internal static readonly IReadOnlySet<string> OutOfScopeFactTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "BSAnimNotes",
        "BSTreadTransfInterpolator",
        "NiBlendBoolInterpolator",
        "NiBlendFloatInterpolator",
        "NiBlendPoint3Interpolator",
        "NiBoolInterpolator",
        "NiBoolTimelineInterpolator",
        "NiDefaultAVObjectPalette",
        "NiFloatInterpolator",
        "NiLookAtInterpolator",
        "NiPathInterpolator",
        "NiPoint3Interpolator"
    };

    /// <summary>Compares every payload and the slice-1 animation facts of one record with the views over its bytes.</summary>
    public static NifAnimationViewOracleReport Compare(
        byte[] bytes,
        JsonObject record,
        NifAnimationViewJsonOptions options)
    {
        var report = new NifAnimationViewOracleReport();
        var nif = NifParser.Parse(bytes);
        if (nif is null)
        {
            report.Diffs.Add("NifParser could not parse the file.");
            return report;
        }

        var strings = NifHeaderLayout.Read(bytes, nif).Strings;
        var payloads = record["payloads"]?.AsArray() ?? new JsonArray();
        report.ExpectedPayloads = payloads.Count;
        foreach (var node in payloads)
        {
            var entry = node!.AsObject();
            var index = entry["i"]!.GetValue<int>();
            var type = entry["type"]!.GetValue<string>();
            var path = $"payload {index.ToString(CultureInfo.InvariantCulture)} {type}";
            if (!TryGetBlock(nif, index, type, path, report, out var block))
            {
                continue;
            }

            var actual = RenderPayload(bytes, nif, block, options, report, path);
            if (actual is null)
            {
                continue;
            }

            NifAnimationJsonComparer.Compare(entry["payload"], actual, path, report.Diffs);
            report.ComparedPayloads++;
        }

        var facts = record["animation"]?.AsObject() ?? new JsonObject();
        report.ExpectedAnimationBlocks = facts.Count;
        foreach (var (key, node) in facts)
        {
            var expected = node!.AsObject();
            var index = int.Parse(key, CultureInfo.InvariantCulture);
            var type = expected["type"]!.GetValue<string>();
            var path = $"animation {key} {type}";
            if (!TryGetBlock(nif, index, type, path, report, out var block))
            {
                continue;
            }

            if (expected.ContainsKey("nextController"))
            {
                if (!NifTimeControllerReader.TryRead(bytes, block, nif.IsBigEndian, out var header))
                {
                    report.Diffs.Add($"{path}: the controller header reader refused the block");
                    continue;
                }

                var rendered = NifAnimationViewJson.ControllerHeader(header);
                NifAnimationJsonComparer.CompareMembers(expected, rendered, path, report.Diffs, ControllerHeaderMembers);
                CountControllerMembersNotCompared(expected, type, report);
                report.ComparedAnimationBlocks++;
                continue;
            }

            var actual = RenderFacts(bytes, nif, block, strings, path, report);
            if (actual is null)
            {
                continue;
            }

            NifAnimationJsonComparer.CompareMembers(expected, actual, path, report.Diffs,
                expected.Select(static member => member.Key)
                    .Where(static name => !IgnoredFactMembers.Contains(name, StringComparer.Ordinal))
                    .ToList());
            report.ComparedAnimationBlocks++;
        }

        return report;
    }

    private static JsonObject? RenderPayload(
        byte[] bytes,
        NifInfo nif,
        BlockInfo block,
        NifAnimationViewJsonOptions options,
        NifAnimationViewOracleReport report,
        string path)
    {
        var blockEnd = block.DataOffset + block.Size;
        switch (block.TypeName)
        {
            case "NiTransformData":
            case "NiKeyframeData":
            {
                if (!NifKeyframeDataTrackReader.TryReadView(bytes, nif, block, out var view))
                {
                    return Refused(report, path);
                }

                RequireExact(view.ConsumedExactly, report, path);
                CountTbc(view.Rotation.IsEuler ? default : view.Rotation.Keys, report);
                CountTbc(view.Translations, report);
                CountTbc(view.Scales, report);
                if (view.Rotation.IsEuler)
                {
                    CountTbc(view.Rotation.EulerX, report);
                    CountTbc(view.Rotation.EulerY, report);
                    CountTbc(view.Rotation.EulerZ, report);
                }

                return NifAnimationViewJson.TransformDataPayload(view, options);
            }
            case "NiFloatData":
            case "NiPosData":
            case "NiBoolData":
            case "NiColorData":
            {
                if (!NifKeyGroupReader.TryReadDataBlockView(bytes, nif, block, out var view))
                {
                    return Refused(report, path);
                }

                RequireExact(view.EndOffset == blockEnd, report, path);
                CountTbc(view, report);
                return NifAnimationViewJson.KeyGroup(view, options);
            }
            case "NiBSplineData":
            {
                if (!NifBsplineTransformReader.TryReadDataView(bytes, nif, block, out var view))
                {
                    return Refused(report, path);
                }

                RequireExact(view.ConsumedExactly, report, path);
                return NifAnimationViewJson.BsplineDataPayload(view);
            }
            case "NiBSplineBasisData":
            {
                if (!NifBsplineTransformReader.TryReadBasisView(bytes, nif, block, out var count))
                {
                    return Refused(report, path);
                }

                RequireExact(block.Size == sizeof(uint), report, path);
                return new JsonObject { ["numControlPoints"] = count };
            }
            default:
            {
                if (!block.TypeName.StartsWith("NiBSpline", StringComparison.Ordinal))
                {
                    report.Diffs.Add($"{path}: no runtime view decodes this payload type");
                    return null;
                }

                // The interpolator views require their layout's exact size, so a match is exact consumption.
                return NifBsplineTransformReader.TryReadInterpolatorView(bytes, nif, block, out var view)
                    ? NifAnimationViewJson.BsplinePayload(view)
                    : Refused(report, path);
            }
        }
    }

    private static JsonObject? RenderFacts(
        byte[] bytes,
        NifInfo nif,
        BlockInfo block,
        NifHeaderStringTable strings,
        string path,
        NifAnimationViewOracleReport report)
    {
        switch (block.TypeName)
        {
            case "NiControllerSequence":
            {
                if (nif.HasInlineStrings)
                {
                    // A 20.0.0.4 stream (cut 2): the Oblivion view, which the 20.2.0.7 view refuses.
                    if (!NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView(bytes, nif, block,
                            out var legacy))
                    {
                        return Refused(report, path);
                    }

                    RequireExact(legacy.TailExact, report, path);
                    return NifAnimationViewJson.OblivionSequence(legacy);
                }

                if (!NifControllerSequenceNameTrackReader.TryReadSequenceView(bytes, nif, block, out var view))
                {
                    return Refused(report, path);
                }

                RequireExact(view.TailExact, report, path);
                return NifAnimationViewJson.Sequence(view, strings);
            }
            case "NiStringPalette":
            {
                if (!NifControllerSequenceNameTrackReader.TryReadStringPaletteView(bytes, nif, block, out var palette))
                {
                    return Refused(report, path);
                }

                RequireExact(palette.ConsumedExactly, report, path);
                return NifAnimationViewJson.StringPalette(palette);
            }
            case "NiTextKeyExtraData":
            {
                if (!NifTextKeyReader.TryReadView(bytes, nif, block, out var view))
                {
                    return Refused(report, path);
                }

                RequireExact(view.ConsumedExactly, report, path);
                return NifAnimationViewJson.TextKeys(view, strings);
            }
            case "NiTransformInterpolator":
            case "BSRotAccumTransfInterpolator":
            {
                if (!NifControllerSequenceNameTrackReader.TryReadTransformInterpolatorView(bytes, nif, block,
                        out var view))
                {
                    return Refused(report, path);
                }

                RequireExact(block.Size == 36, report, path);
                return NifAnimationViewJson.TransformInterpolator(view);
            }
            case "NiTransformData":
            case "NiKeyframeData":
                return NifKeyframeDataTrackReader.TryReadView(bytes, nif, block, out var keyframes)
                    ? NifAnimationViewJson.TransformDataFacts(keyframes)
                    : Refused(report, path);
            case "NiFloatData":
            case "NiPosData":
            case "NiBoolData":
            case "NiColorData":
                return NifKeyGroupReader.TryReadDataBlockView(bytes, nif, block, out var group)
                    ? NifAnimationViewJson.KeyDataFacts(group)
                    : Refused(report, path);
            case "NiBSplineData":
                return NifBsplineTransformReader.TryReadDataView(bytes, nif, block, out var store)
                    ? NifAnimationViewJson.BsplineDataFacts(store)
                    : Refused(report, path);
            case "NiBSplineBasisData":
                return NifBsplineTransformReader.TryReadBasisView(bytes, nif, block, out var count)
                    ? new JsonObject { ["numControlPoints"] = count }
                    : Refused(report, path);
            default:
                if (block.TypeName.StartsWith("NiBSpline", StringComparison.Ordinal) &&
                    block.TypeName.EndsWith("Interpolator", StringComparison.Ordinal))
                {
                    return NifBsplineTransformReader.TryReadInterpolatorView(bytes, nif, block, out var spline)
                        ? NifAnimationViewJson.BsplineFacts(spline)
                        : Refused(report, path);
                }

                if (!OutOfScopeFactTypes.Contains(block.TypeName))
                {
                    report.Diffs.Add($"{path}: no slice-1 view renders this block type, and it is not declared out of scope");
                    return null;
                }

                report.NotCompared[block.TypeName] =
                    report.NotCompared.TryGetValue(block.TypeName, out var seen) ? seen + 1 : 1;
                return null;
        }
    }

    /// <summary>
    ///     Counts a controller's type-specific members (everything but the header and the type and kind labels), which
    ///     slice 1 does not compare.
    /// </summary>
    private static void CountControllerMembersNotCompared(
        JsonObject expected, string type, NifAnimationViewOracleReport report)
    {
        foreach (var (name, _) in expected)
        {
            if (ControllerHeaderMembers.Contains(name, StringComparer.Ordinal) ||
                IgnoredFactMembers.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var key = type + "." + name;
            report.NotComparedControllerMembers[key] =
                report.NotComparedControllerMembers.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }
    }

    private static bool TryGetBlock(
        NifInfo nif,
        int index,
        string type,
        string path,
        NifAnimationViewOracleReport report,
        out BlockInfo block)
    {
        block = null!;
        if (index < 0 || index >= nif.Blocks.Count)
        {
            report.Diffs.Add($"{path}: block index outside NifParser's {nif.Blocks.Count} blocks");
            return false;
        }

        block = nif.Blocks[index];
        if (!string.Equals(block.TypeName, type, StringComparison.Ordinal))
        {
            report.Diffs.Add($"{path}: NifParser calls the block {block.TypeName}");
            return false;
        }

        return true;
    }

    private static JsonObject? Refused(NifAnimationViewOracleReport report, string path)
    {
        report.Diffs.Add($"{path}: the view refused the block");
        return null;
    }

    private static void RequireExact(bool exact, NifAnimationViewOracleReport report, string path)
    {
        if (!exact)
        {
            report.Diffs.Add($"{path}: the view did not end exactly at the block's end, where the probe's walk did");
        }
    }

    private static void CountTbc(in NifKeyGroupView group, NifAnimationViewOracleReport report)
    {
        if (!group.HasTbc)
        {
            return;
        }

        for (var key = 0; key < group.Count; key++)
        {
            report.TbcKeys++;
            if (group.ContinuityBits(key) != group.BiasBits(key))
            {
                report.TbcKeysWithContinuityNotBias++;
            }
        }
    }
}
