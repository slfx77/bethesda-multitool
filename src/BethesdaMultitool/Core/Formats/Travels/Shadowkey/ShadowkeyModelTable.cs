namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     A parsed <c>models.txt</c> (237 rows) or per-zone <c>&lt;zone&gt;_models.txt</c> (236 rows,
///     168 to 236 of them blanked to <c>NULL.bin</c>). Built by
///     <see cref="ShadowkeyTextTables.ParseModels(byte[], string)" />.
///     <para>
///         Both files share one index space, so the same type serves both: the zone file is a
///         residency mask over the global list. <see cref="Find" /> returns <see langword="null" />
///         for an index the file does not reach — which is how a global index of 236 behaves
///         against a 236-row zone list — rather than throwing, so a caller can report the miss.
///     </para>
/// </summary>
internal sealed class ShadowkeyModelTable
{
    internal ShadowkeyModelTable(IReadOnlyList<ShadowkeyModelDef> models)
    {
        Models = models;
    }

    /// <summary>The rows, in file order; indices are contiguous from zero.</summary>
    public IReadOnlyList<ShadowkeyModelDef> Models { get; }

    /// <summary>Rows whose file is not <c>NULL.bin</c> — on a zone list, the models it loads.</summary>
    public int ResidentCount
    {
        get
        {
            var resident = 0;
            foreach (var model in Models)
            {
                if (!model.IsUnused)
                {
                    resident++;
                }
            }

            return resident;
        }
    }

    /// <summary>The row at <paramref name="index" />, or <see langword="null" /> when out of range.</summary>
    public ShadowkeyModelDef? Find(int index) =>
        index >= 0 && index < Models.Count ? Models[index] : null;
}
