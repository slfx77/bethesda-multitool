namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Shared remap-or-skip helper for optional FormID-bearing fields across encoders. Try
///     the runtime→emitted alias table FIRST, fall back to the validity check, otherwise
///     return null so the caller can skip the subrecord entirely.
///     <para>
///     The remap-first ordering matters because <c>_emittedNewFormIds</c> in PluginBuilder
///     tracks BOTH source and allocated FormIDs. A source FormID looks "valid" but its
///     bytes won't resolve when the engine reads the emitted record under a different
///     allocated FormID — so a remap when one exists is always preferred over a verbatim
///     source emit.
///     </para>
/// </summary>
internal static class FormIdReferenceResolver
{
    /// <summary>
    ///     Resolve an optional FormID against the master ∪ emitted set + the alias remap table.
    ///     Returns:
    ///     <list type="bullet">
    ///         <item>The original value when <paramref name="formId" /> is 0 or
    ///         <paramref name="validFormIds" /> is null (backward-compat path).</item>
    ///         <item>The remapped value when the alias table has an entry pointing at a
    ///         valid target.</item>
    ///         <item>The original value when it's already in the validity set.</item>
    ///         <item><c>null</c> when the FormID is dangling and has no remap — callers
    ///         should skip emitting the optional subrecord.</item>
    ///     </list>
    /// </summary>
    public static uint? Resolve(
        uint formId,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        if (formId == 0 || validFormIds is null)
        {
            return formId;
        }

        if (remapTable is not null
            && remapTable.TryGetValue(formId, out var remapped)
            && remapped != formId
            && validFormIds.Contains(remapped))
        {
            return remapped;
        }

        return validFormIds.Contains(formId) ? formId : null;
    }
}
