using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reusable probe builder for runtime struct readers. Readers declare their probeable
///     fields with validation rules, and this class generates candidates, scores samples,
///     and returns the best-matching layout shift per field group.
///
///     Field groups correspond to C++ inheritance boundaries — fields from the same base
///     class shift together when parent class sizes change between builds.
/// </summary>
internal static class RuntimeReaderFieldProbe
{
    /// <summary>
    ///     A probeable field with its baseline PDB offset, group assignment, and validation rule.
    /// </summary>
    /// <param name="Name">Human-readable field name for diagnostics.</param>
    /// <param name="BaseOffset">Offset from MemDebug PDB (July 2010 baseline).</param>
    /// <param name="Group">
    ///     Inheritance group index. Group 0 = TESForm (always shift 0, anchor).
    ///     Higher groups = deeper inheritance levels that shift independently.
    /// </param>
    /// <param name="Check">Validation rule to apply when scoring.</param>
    /// <param name="Weight">Score weight for a successful check (default 1).</param>
    /// <param name="CheckArg">Optional argument for the check (e.g., expected FormType byte, float range).</param>
    public readonly record struct FieldSpec(
        string Name,
        int BaseOffset,
        int Group,
        FieldCheck Check,
        int Weight = 1,
        object? CheckArg = null);

    /// <summary>Validation rules for probe fields.</summary>
    public enum FieldCheck
    {
        /// <summary>Follow 4-byte BE pointer → must resolve to a valid TESForm with non-zero FormID.</summary>
        PointerToForm,

        /// <summary>Follow pointer → must resolve to TESForm with specific FormType. CheckArg = (byte) expectedType.</summary>
        PointerToFormType,

        /// <summary>Read 4-byte BE float, must not be NaN or Infinity.</summary>
        NormalFloat,

        /// <summary>Read 4-byte BE float, must be within [min, max]. CheckArg = (float min, float max).</summary>
        RangedFloat,

        /// <summary>Read 4-byte BE uint32, must be non-zero.</summary>
        NonZeroUInt32,

        /// <summary>Read 4-byte BE int32, must be in [min, max]. CheckArg = (int min, int max).</summary>
        Int32Range,

        /// <summary>Read 1-byte value, must be in [min, max]. CheckArg = (byte min, byte max).</summary>
        ByteRange,

        /// <summary>Read BSStringT (8 bytes: pointer + length), must resolve to a non-empty ASCII string.</summary>
        BSStringT
    }

    /// <summary>
    ///     Run a complete probe: generate shift candidates, score them against real dump samples,
    ///     and return the best-scoring shift array.
    /// </summary>
    /// <param name="context">Memory context for pointer validation and string resolution.</param>
    /// <param name="entries">Runtime entries to use as samples (filtered to correct FormType by caller).</param>
    /// <param name="fields">Declared probeable fields with validation rules.</param>
    /// <param name="groupCount">Number of independent shift groups (excluding group 0 which is always 0).</param>
    /// <param name="shiftOptions">Shift values to try per group (e.g., [-4, 0, 4]).</param>
    /// <param name="baseStructSize">Baseline struct size from PDB.</param>
    /// <param name="probeName">Name for diagnostic logging.</param>
    /// <param name="maxSamples">Maximum samples to use (default 12).</param>
    /// <param name="log">Optional diagnostic log callback.</param>
    /// <returns>Probe result with winning shift array, or null if insufficient samples.</returns>
    public static RuntimeLayoutProbeResult<int[]>? Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry> entries,
        IReadOnlyList<FieldSpec> fields,
        int groupCount,
        int[] shiftOptions,
        int baseStructSize,
        string probeName,
        int maxSamples = 12,
        Action<string>? log = null)
    {
        // Filter to entries with valid offsets
        var samples = entries
            .Where(e => e.TesFormOffset.HasValue)
            .Take(maxSamples)
            .ToList();

        if (samples.Count == 0)
        {
            return null;
        }

        // Generate candidates as cross-product of per-group shifts
        // Group 0 is always shift=0 (TESForm anchor), so we only vary groups 1..groupCount
        var candidates = GenerateCandidates(groupCount, shiftOptions);

        return RuntimeLayoutProbeEngine.Probe(
            samples,
            candidates,
            (sample, candidate) => ScoreSample(context, sample, fields, candidate.Layout, baseStructSize),
            probeName,
            log,
            sample => $"0x{sample.FormId:X8} ({sample.EditorId})");
    }

    /// <summary>
    ///     Generate candidate shift arrays as cross-product of per-group shift options.
    ///     Group 0 is always 0 (TESForm anchor). Groups 1..N get independent shifts.
    /// </summary>
    public static IReadOnlyList<RuntimeLayoutProbeCandidate<int[]>> GenerateCandidates(
        int groupCount,
        int[] shiftOptions)
    {
        var results = new List<RuntimeLayoutProbeCandidate<int[]>>();

        // groupCount = number of variable groups (1-based: group 1, 2, ...)
        // Total array size = groupCount + 1 (index 0 = TESForm anchor = always 0)
        var totalGroups = groupCount + 1;
        var indices = new int[groupCount]; // indices into shiftOptions for groups 1..N

        while (true)
        {
            var shifts = new int[totalGroups];
            shifts[0] = 0; // TESForm anchor
            var labelParts = new List<string>();

            for (var g = 0; g < groupCount; g++)
            {
                shifts[g + 1] = shiftOptions[indices[g]];
                if (shiftOptions[indices[g]] != 0)
                {
                    labelParts.Add($"G{g + 1}={shiftOptions[indices[g]]:+0;-0}");
                }
            }

            var label = labelParts.Count == 0 ? "Default" : string.Join(",", labelParts);
            results.Add(new RuntimeLayoutProbeCandidate<int[]>(label, shifts));

            // Increment indices (odometer-style)
            var carry = true;
            for (var g = groupCount - 1; g >= 0 && carry; g--)
            {
                indices[g]++;
                if (indices[g] < shiftOptions.Length)
                {
                    carry = false;
                }
                else
                {
                    indices[g] = 0;
                }
            }

            if (carry)
            {
                break; // All combinations exhausted
            }
        }

        return results;
    }

    /// <summary>
    ///     Score a single sample entry against a candidate shift array using the declared field specs.
    /// </summary>
    public static RuntimeLayoutProbeScore ScoreSample(
        RuntimeMemoryContext context,
        RuntimeEditorIdEntry entry,
        IReadOnlyList<FieldSpec> fields,
        int[] shifts,
        int baseStructSize)
    {
        if (entry.TesFormOffset == null)
        {
            return new RuntimeLayoutProbeScore(0);
        }

        var offset = entry.TesFormOffset.Value;

        // Compute effective struct size: base + max shift applied to any group
        var maxShift = 0;
        for (var i = 0; i < shifts.Length; i++)
        {
            if (shifts[i] > maxShift)
            {
                maxShift = shifts[i];
            }
        }

        var effectiveSize = baseStructSize + maxShift;
        if (offset + effectiveSize > context.FileSize)
        {
            return new RuntimeLayoutProbeScore(0);
        }

        var buffer = new byte[effectiveSize];
        try
        {
            context.Accessor.ReadArray(offset, buffer, 0, effectiveSize);
        }
        catch
        {
            return new RuntimeLayoutProbeScore(0);
        }

        // Verify FormID anchor (always at offset 12, group 0)
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId || formId == 0)
        {
            return new RuntimeLayoutProbeScore(0);
        }

        var points = 0;
        var maxPoints = 0;

        foreach (var field in fields)
        {
            if (field.Group >= shifts.Length)
            {
                continue;
            }

            var effectiveOffset = field.BaseOffset + shifts[field.Group];
            if (effectiveOffset < 0 || effectiveOffset + 4 > buffer.Length)
            {
                continue;
            }

            maxPoints += field.Weight;
            points += ScoreField(context, offset, buffer, effectiveOffset, field);
        }

        return new RuntimeLayoutProbeScore(points, maxPoints);
    }

    private static int ScoreField(
        RuntimeMemoryContext context,
        long structFileOffset,
        byte[] buffer,
        int effectiveOffset,
        FieldSpec field)
    {
        switch (field.Check)
        {
            case FieldCheck.PointerToForm:
            {
                var formId = context.FollowPointerToFormId(buffer, effectiveOffset);
                return formId.HasValue ? field.Weight : 0;
            }

            case FieldCheck.PointerToFormType:
            {
                if (field.CheckArg is not byte expectedType)
                {
                    return 0;
                }

                var formId = context.FollowPointerToFormId(buffer, effectiveOffset, expectedType);
                return formId.HasValue ? field.Weight : 0;
            }

            case FieldCheck.NormalFloat:
            {
                var value = BinaryUtils.ReadFloatBE(buffer, effectiveOffset);
                return RuntimeMemoryContext.IsNormalFloat(value) ? field.Weight : 0;
            }

            case FieldCheck.RangedFloat:
            {
                if (field.CheckArg is not (float min, float max))
                {
                    return 0;
                }

                var value = BinaryUtils.ReadFloatBE(buffer, effectiveOffset);
                return RuntimeMemoryContext.IsNormalFloat(value) && value >= min && value <= max
                    ? field.Weight
                    : 0;
            }

            case FieldCheck.NonZeroUInt32:
            {
                var value = BinaryUtils.ReadUInt32BE(buffer, effectiveOffset);
                return value != 0 ? field.Weight : 0;
            }

            case FieldCheck.Int32Range:
            {
                if (field.CheckArg is not (int min, int max))
                {
                    return 0;
                }

                var value = (int)BinaryUtils.ReadUInt32BE(buffer, effectiveOffset);
                return value >= min && value <= max ? field.Weight : 0;
            }

            case FieldCheck.ByteRange:
            {
                if (field.CheckArg is not (byte min, byte max))
                {
                    return 0;
                }

                if (effectiveOffset >= buffer.Length)
                {
                    return 0;
                }

                var value = buffer[effectiveOffset];
                return value >= min && value <= max ? field.Weight : 0;
            }

            case FieldCheck.BSStringT:
            {
                var str = context.ReadBSStringT(structFileOffset, effectiveOffset);
                return !string.IsNullOrEmpty(str) ? field.Weight : 0;
            }

            default:
                return 0;
        }
    }

    /// <summary>
    ///     Apply a shift array to a base offset, looking up the group for the field.
    /// </summary>
    public static int ApplyShift(int baseOffset, int group, int[] shifts)
    {
        return group < shifts.Length ? baseOffset + shifts[group] : baseOffset;
    }
}
