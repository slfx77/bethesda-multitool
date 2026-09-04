using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Every runtime object the dump sweep could locate, as a searchable set of address spans.
///     <para>
///         Built from the words that equal a known vtable address. Ownership containment has so far
///         only been able to ask "is this pointer inside a known TESForm?", which on xex44 named
///         1,528 more strings — small, because most referrers are not in TESForms at all. This
///         inventory is the general form of the same question: any class the build defines, not just
///         the 116 with a FormType.
///     </para>
/// </summary>
internal sealed class RuntimeObjectInventory
{
    /// <summary>
    ///     Cap on an inferred extent. Objects in a pool are allocated adjacently, so the next base
    ///     bounds this one — but a lone object followed by a large free block would otherwise claim
    ///     everything up to the next allocation, so the bound is also capped outright.
    /// </summary>
    internal const uint MaxInferredSpan = 512;

    private readonly string[] _classNames;
    private readonly RuntimeObjectSpan[] _spans;
    private readonly uint[] _starts;

    private RuntimeObjectInventory(
        RuntimeObjectSpan[] spans, string[] classNames, int ambiguousBaseCount, int rawHitCount)
    {
        _spans = spans;
        _classNames = classNames;
        _starts = [.. spans.Select(s => s.BaseVa)];
        AmbiguousBaseCount = ambiguousBaseCount;
        RawHitCount = rawHitCount;
        DeclaredSizeCount = spans.Count(s => s.SizeIsDeclared);
    }

    internal int ObjectCount => _spans.Length;

    /// <summary>Vtable words seen, before collapsing to distinct object bases.</summary>
    internal int RawHitCount { get; }

    /// <summary>
    ///     Bases that more than one class claimed. Expected and benign for multiple inheritance —
    ///     a complete object's primary and secondary vtables both resolve to it — but counted so a
    ///     pathological number is visible rather than silently arbitrated.
    /// </summary>
    internal int AmbiguousBaseCount { get; }

    internal int DeclaredSizeCount { get; }

    internal IReadOnlyList<RuntimeObjectSpan> Spans => _spans;

    internal string ClassName(int classId)
    {
        return classId >= 0 && classId < _classNames.Length ? _classNames[classId] : "?";
    }

    /// <summary>
    ///     Collapse raw vtable-word hits into object spans. Where several classes claim one base the
    ///     one with a declared size wins, because a declared extent is the only kind that can be
    ///     checked.
    /// </summary>
    internal static RuntimeObjectInventory Build(
        DumpRttiIndex rtti, List<(uint BaseVa, int ClassId)> hits)
    {
        var classNames = rtti.Classes.Select(c => c.ClassName).ToArray();
        var declaredSizes = BuildDeclaredSizes(classNames);

        if (hits.Count == 0)
        {
            return new RuntimeObjectInventory([], classNames, 0, 0);
        }

        hits.Sort((left, right) => left.BaseVa != right.BaseVa
            ? left.BaseVa.CompareTo(right.BaseVa)
            : left.ClassId.CompareTo(right.ClassId));

        var chosen = new List<(uint BaseVa, int ClassId, int Size)>(hits.Count);
        var ambiguous = 0;
        var i = 0;
        while (i < hits.Count)
        {
            var baseVa = hits[i].BaseVa;
            var bestClass = hits[i].ClassId;
            var bestSize = SizeOf(bestClass);
            var lastClass = bestClass;
            var distinctClasses = 1;

            var j = i + 1;
            while (j < hits.Count && hits[j].BaseVa == baseVa)
            {
                var classId = hits[j].ClassId;
                if (classId != lastClass)
                {
                    distinctClasses++;
                    lastClass = classId;

                    // Prefer whichever claimant has a declared size, and the larger of two — for
                    // multiple inheritance that is the complete object rather than an embedded base.
                    var size = SizeOf(classId);
                    if (size > bestSize)
                    {
                        bestClass = classId;
                        bestSize = size;
                    }
                }

                j++;
            }

            if (distinctClasses > 1)
            {
                ambiguous++;
            }

            chosen.Add((baseVa, bestClass, bestSize));
            i = j;
        }

        int SizeOf(int classId)
        {
            return classId >= 0 && classId < declaredSizes.Length ? declaredSizes[classId] : 0;
        }

        var spans = new RuntimeObjectSpan[chosen.Count];
        for (var s = 0; s < chosen.Count; s++)
        {
            var (baseVa, classId, size) = chosen[s];
            uint endVa;
            bool declared;

            if (size > 0)
            {
                endVa = baseVa + (uint)size;
                declared = true;
            }
            else
            {
                var nextBase = s + 1 < chosen.Count ? chosen[s + 1].BaseVa : uint.MaxValue;
                var gap = nextBase > baseVa ? nextBase - baseVa : 0;
                endVa = baseVa + Math.Min(gap, MaxInferredSpan);
                declared = false;
            }

            if (endVa < baseVa)
            {
                endVa = baseVa; // wrapped; an empty span claims nothing
            }

            spans[s] = new RuntimeObjectSpan(baseVa, endVa, classId, declared);
        }

        return new RuntimeObjectInventory(spans, classNames, ambiguous, hits.Count);
    }

    /// <summary>
    ///     Declared struct size per class, from the PDB layouts we already ship — the 116 FormType
    ///     records plus the auxiliary structs. Zero means "no declared size", not "zero-sized".
    /// </summary>
    private static int[] BuildDeclaredSizes(string[] classNames)
    {
        var sizes = new int[classNames.Length];
        for (var i = 0; i < classNames.Length; i++)
        {
            if (PdbStructLayouts.TryGetFormTypeByClassName(classNames[i], out var formType))
            {
                sizes[i] = PdbStructLayouts.Get(formType)?.StructSize ?? 0;
                if (sizes[i] > 0)
                {
                    continue;
                }
            }

            if (PdbStructLayouts.TryGetAuxStruct(classNames[i], out var aux))
            {
                sizes[i] = aux.StructSize;
            }
        }

        return sizes;
    }

    /// <summary>
    ///     The object containing <paramref name="va" />, if any. Takes the last span starting at or
    ///     before the address, so a nested object inside another is reported as the inner one; an
    ///     outer container is therefore under-reported rather than guessed at.
    /// </summary>
    internal bool TryFind(uint va, out RuntimeObjectSpan span)
    {
        span = default;
        if (_starts.Length == 0)
        {
            return false;
        }

        var idx = Array.BinarySearch(_starts, va);
        if (idx < 0)
        {
            idx = ~idx - 1;
        }

        if (idx < 0)
        {
            return false;
        }

        var candidate = _spans[idx];
        if (va >= candidate.EndVa)
        {
            return false;
        }

        span = candidate;
        return true;
    }
}
