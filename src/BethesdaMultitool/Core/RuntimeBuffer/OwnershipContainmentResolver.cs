using System.Buffers.Binary;
using BethesdaMultitool.Core.Utils;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Names the owner of a referenced string by asking which runtime object physically contains
///     the pointer that references it.
///     <para>
///         Every other second-pass strategy needs to recognise something first — a BSStringT
///         wrapper, a vtable it can resolve, text that equals a known EditorID. Each therefore
///         fails on the ordinary case of a string held in a field we have no layout for, which is
///         most of what remains: after the 2026-09-03 pass that opened analysis to unclassified
///         text, 129,091 strings on xex44 still had live inbound pointers and no owner.
///     </para>
///     <para>
///         Containment needs none of that. A runtime TESForm occupies
///         <c>[TesFormPointer, TesFormPointer + StructSize)</c> — the start is a confirmed form and
///         the size comes from the PDB — so a referrer inside that span is, by construction, a
///         pointer field of that form. The claim is the form's identity plus the raw field offset,
///         which is weaker than a named field but strictly better than "owner unknown", and it is
///         real evidence rather than proximity: the address is inside the object or it is not.
///     </para>
/// </summary>
internal sealed class OwnershipContainmentResolver
{
    /// <summary>
    ///     Object spans sorted by start VA, for binary search. Disjoint by construction — see
    ///     <see cref="Build" />, which drops overlaps rather than guessing between them.
    /// </summary>
    private readonly (uint StartVa, uint EndVa, RuntimeEditorIdEntry Entry, string? RecordCode)[] _spans;

    private readonly RuntimeMemoryContext _memory;
    public OwnershipContainmentResolver(BufferAnalysisContext ctx)
    {
        _memory = new RuntimeMemoryContext(new MmfMemoryAccessor(ctx.Accessor), ctx.FileSize, ctx.MinidumpInfo);
        _spans = Build(ctx.RuntimeEditorIds);
    }

    /// <summary>True when there is anything to search.</summary>
    public bool HasSpans => _spans.Length > 0;

    private static (uint StartVa, uint EndVa, RuntimeEditorIdEntry Entry, string? RecordCode)[] Build(
        IReadOnlyList<RuntimeEditorIdEntry>? entries)
    {
        if (entries is not { Count: > 0 })
        {
            return [];
        }

        var layouts = PdbStructLayouts.Layouts;
        var spans = new List<(uint StartVa, uint EndVa, RuntimeEditorIdEntry Entry, string? RecordCode)>(
            entries.Count);
        foreach (var entry in entries)
        {
            if (entry.TesFormPointer is not { } pointer || pointer == 0 ||
                pointer < int.MinValue || pointer > uint.MaxValue
                || !layouts.TryGetValue(entry.FormType, out var layout)
                || layout.StructSize <= 0)
            {
                continue;
            }

            var tesFormVa = unchecked((uint)pointer);
            var interior = (uint)PdbStructLayouts.GetTesFormInteriorOffset(layout);
            if (tesFormVa < interior) continue;
            var start = tesFormVa - interior;
            var end = start + (uint)layout.StructSize;
            if (end <= start)
            {
                continue; // wrapped; a bad size, not an object
            }

            spans.Add((start, end, entry, layout.RecordCode));
        }

        spans.Sort((left, right) => left.StartVa.CompareTo(right.StartVa));

        // Overlapping spans mean at least one struct size is wrong for this build, and a referrer
        // in the overlap has two equally-supported owners. Dropping both is the honest response —
        // this strategy's whole claim to strength is that containment is unambiguous.
        var disjoint =
            new List<(uint StartVa, uint EndVa, RuntimeEditorIdEntry Entry, string? RecordCode)>(spans.Count);
        for (var i = 0; i < spans.Count; i++)
        {
            var overlapsPrevious = i > 0 && spans[i].StartVa < spans[i - 1].EndVa;
            var overlapsNext = i + 1 < spans.Count && spans[i + 1].StartVa < spans[i].EndVa;
            if (!overlapsPrevious && !overlapsNext)
            {
                disjoint.Add(spans[i]);
            }
        }

        return [.. disjoint];
    }

    /// <summary>
    ///     Find the object containing <paramref name="referrerVa" />, or null.
    ///     <paramref name="fieldOffset" /> is the referrer's offset within it.
    /// </summary>
    private (RuntimeEditorIdEntry Entry, string? RecordCode)? Find(uint referrerVa, out int fieldOffset)
    {
        fieldOffset = 0;
        if (_spans.Length == 0)
        {
            return null;
        }

        var low = 0;
        var high = _spans.Length - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var span = _spans[mid];
            if (referrerVa < span.StartVa)
            {
                high = mid - 1;
            }
            else if (referrerVa >= span.EndVa)
            {
                low = mid + 1;
            }
            else
            {
                fieldOffset = (int)(referrerVa - span.StartVa);
                return (span.Entry, span.RecordCode);
            }
        }

        return null;
    }

    /// <summary>
    ///     Try to claim <paramref name="hit" /> for whichever runtime form holds one of its
    ///     referrers. Retain distinct containing owners instead of choosing the first shared owner.
    /// </summary>
    internal RuntimeStringOwnershipClaim? TryContainmentMatch(RuntimeStringHit hit) =>
        ResolveCandidates(hit).FirstOrDefault();

    internal List<RuntimeStringOwnershipClaim> ResolveCandidates(RuntimeStringHit hit)
    {
        var candidates = new List<RuntimeStringOwnershipClaim>();
        if (_spans.Length == 0 || hit.OwnerResolution is not { } resolution)
        {
            return candidates;
        }

        foreach (var referrerVa in EnumerateReferrerVas(resolution))
        {
            var found = Find(referrerVa, out var fieldOffset);
            if (found is not { } owner || fieldOffset < 4)
            {
                // Offset 0 is the vtable slot: a "pointer" there is the vtable itself, not a field
                // holding this string.
                continue;
            }

            var (entry, recordType) = owner;
            var header = _memory.ReadBytesAtVa(entry.TesFormPointer!.Value, 16);
            if (header == null || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)) != entry.FormId ||
                entry.FormId == 0 || header[4] != (entry.OriginalFormType ?? entry.FormType)) continue;
            candidates.Add(new RuntimeStringOwnershipClaim(
                hit.FileOffset,
                hit.VirtualAddress,
                "RuntimeObjectContainment",
                string.IsNullOrEmpty(entry.EditorId)
                    ? $"{recordType ?? "TESForm"} 0x{entry.FormId:X8}"
                    : entry.EditorId,
                entry.FormId != 0 ? entry.FormId : null,
                _memory.VaToFileOffset(referrerVa - (uint)fieldOffset),
                ClaimSource.SecondPassContainment,
                recordType,
                $"+0x{fieldOffset:X}", Xbox360MemoryUtils.VaToLong(referrerVa),
                _memory.VaToFileOffset(referrerVa), "validated TESForm identity; containment only"));
        }

        return candidates;
    }

    private static IEnumerable<uint> EnumerateReferrerVas(RuntimeStringOwnerResolution resolution)
    {
        if (resolution.AllReferrers is { Count: > 0 } all)
        {
            foreach (var (_, referrerVa, _) in all)
            {
                if (referrerVa is > 0 and <= uint.MaxValue)
                {
                    yield return (uint)referrerVa;
                }
            }

            yield break;
        }

        if (resolution.ReferrerVa is > 0 and <= uint.MaxValue)
        {
            yield return (uint)resolution.ReferrerVa.Value;
        }
    }
}
