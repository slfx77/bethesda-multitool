using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

/// <summary>
///     Collapses result-script slots from repeated captures of one INFO FormID. Unlike
///     parser-fragment merging, each duplicate INFO is an independent observation: a
///     demonstrably complete executable bundle therefore outranks a partial observation,
///     while two conflicting complete observations must fail closed.
/// </summary>
internal static class DialogueResultScriptDuplicateMerger
{
    public static List<DialogueResultScript> Merge(
        IReadOnlyList<IReadOnlyList<DialogueResultScript>> captures)
    {
        var slotCount = captures.Count == 0
            ? 0
            : captures.Max(static capture => capture.Count);
        var merged = new List<DialogueResultScript>(slotCount);
        for (var slot = 0; slot < slotCount; slot++)
        {
            var observations = captures
                .Where(capture => slot < capture.Count)
                .Select(capture => capture[slot])
                .ToList();
            merged.Add(MergeSlot(observations));
        }

        // Keep interior placeholders because list position distinguishes Begin and End.
        // Empty trailing slots carry no information and need not be serialized.
        while (merged.Count > 0 && !merged[^1].HasContent)
        {
            merged.RemoveAt(merged.Count - 1);
        }

        return merged;
    }

    private static DialogueResultScript MergeSlot(
        IReadOnlyList<DialogueResultScript> observations)
    {
        if (observations.Count == 0)
        {
            return new DialogueResultScript();
        }

        var hasNextSeparator = observations.Any(static script => script.HasNextSeparator);
        var complete = observations
            .Where(static script => Classify(script) == BundleQuality.Complete)
            .ToList();
        if (complete.Count > 0)
        {
            var template = complete[0];
            if (complete.Skip(1).Any(script => !ExecutableBundlesEqual(template, script)))
            {
                return IncompleteSentinel(hasNextSeparator);
            }

            var sourceText = SelectUnambiguousText(
                complete.Select(static script => script.SourceText));
            var sourceOwner = sourceText is null
                ? null
                : complete.First(script => string.Equals(
                    script.SourceText, sourceText, StringComparison.Ordinal));

            // A separately complete capture is authoritative. In particular, neither an
            // incomplete flag nor SCTX from a source-only/incomplete sibling may contaminate
            // it. Source provenance is accepted only from a complete observation that owns
            // the same SCDA/local/reference bundle.
            return template with
            {
                SourceText = sourceText,
                SourceTextOrigin = sourceOwner?.SourceTextOrigin
                                   ?? ScriptSourceTextOrigin.None,
                IsDmpDerived = complete.Any(static script => script.IsDmpDerived),
                DecompiledText = SelectUnambiguousText(
                    complete.Select(static script => script.DecompiledText)),
                HasNextSeparator = hasNextSeparator
            };
        }

        if (observations.Any(static script => Classify(script) == BundleQuality.Incomplete))
        {
            return IncompleteSentinel(hasNextSeparator);
        }

        // Neither observation declares executable state. Preserve one source
        // deterministically without fabricating or combining bytecode tables.
        var sourceOnly = observations
            .Where(static script => !string.IsNullOrWhiteSpace(script.SourceText))
            .OrderBy(static script => script.SourceText, StringComparer.Ordinal)
            .FirstOrDefault() ?? observations[0];
        return new DialogueResultScript
        {
            SourceText = sourceOnly.SourceText,
            SourceTextOrigin = sourceOnly.SourceTextOrigin,
            IsDmpDerived = observations.Any(static script => script.IsDmpDerived),
            DecompiledText = SelectCanonicalText(
                observations.Select(static script => script.DecompiledText)),
            HasNextSeparator = hasNextSeparator
        };
    }

    private static BundleQuality Classify(DialogueResultScript script)
    {
        if (script.IsIncompleteExecutableBundle
            || (script.CompiledData is not { Length: > 0 }
                && (script.Variables.Count > 0 || script.ReferencedObjects.Count > 0)))
        {
            return BundleQuality.Incomplete;
        }

        if (script.CompiledData is { Length: > 0 })
        {
            return BundleQuality.Complete;
        }

        return script.HasContent ? BundleQuality.SourceOnly : BundleQuality.Empty;
    }

    private static bool ExecutableBundlesEqual(
        DialogueResultScript left,
        DialogueResultScript right) =>
        left.CompiledData!.AsSpan().SequenceEqual(right.CompiledData)
        && left.Variables.SequenceEqual(right.Variables)
        && left.ReferencedObjects.SequenceEqual(right.ReferencedObjects)
        && left.IsBigEndianBytecode == right.IsBigEndianBytecode;

    private static DialogueResultScript IncompleteSentinel(bool hasNextSeparator) =>
        new()
        {
            HasNextSeparator = hasNextSeparator,
            IsIncompleteExecutableBundle = true
        };

    private static string? SelectUnambiguousText(IEnumerable<string?> values)
    {
        var distinct = values
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static string? SelectCanonicalText(IEnumerable<string?> values) =>
        values
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

    private enum BundleQuality
    {
        Empty,
        SourceOnly,
        Incomplete,
        Complete
    }
}
