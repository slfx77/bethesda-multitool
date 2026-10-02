using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

internal sealed record LoadOrderRecordVersion(string Plugin, string FilePath, uint FileLocalFormId,
    uint LoadOrderFormId, string Signature, string? EditorId, uint Flags, long Offset, bool WasClamped = false);

internal sealed record LoadOrderRecordIdentity(uint LoadOrderFormId, string? OwnerPlugin,
    IReadOnlyList<LoadOrderRecordVersion> Versions)
{
    public LoadOrderRecordVersion Winner => Versions[^1];
    public bool DeletedByWinner => (Winner.Flags & 0x20) != 0;
    public bool TypeConflict => Versions.Select(v => v.Signature).Distinct(StringComparer.Ordinal).Skip(1).Any();
    public bool HasAmbiguousWinningRecords => Versions.Count(v =>
        v.Plugin.Equals(Winner.Plugin, StringComparison.OrdinalIgnoreCase)) > 1;
}

/// <summary>Raw on-disk identities; independent of typed-parser coverage and retained deleted records.</summary>
internal sealed class LoadOrderRecordIndex
{
    private LoadOrderRecordIndex(IReadOnlyDictionary<uint, LoadOrderRecordIdentity> records) { Records = records; }
    public IReadOnlyDictionary<uint, LoadOrderRecordIdentity> Records { get; }

    internal static LoadOrderRecordIndex Build(PluginLoadOrder order, CancellationToken cancellationToken = default)
    {
        var versions = new List<LoadOrderRecordVersion>();
        foreach (var entry in order.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(entry.Path);
            var scan = EsmDescriptorScanner.Scan(bytes);
            var editorIdsByOffset = scan.ScanResult.EditorIds.GroupBy(e => e.Offset)
                .ToDictionary(g => g.Key, g => g.Last().Name);
            foreach (var record in scan.ScanResult.MainRecords)
            {
                if (record.FormId == 0) { continue; }
                var id = order.Map(entry.Path, record.FormId);
                versions.Add(new(entry.Name, entry.Path, record.FormId, id.LoadOrderFormId,
                    record.RecordType, editorIdsByOffset.GetValueOrDefault(record.Offset), record.Flags, record.Offset, id.WasClamped));
            }
        }
        return Create(order, versions);
    }

    internal static LoadOrderRecordIndex Create(PluginLoadOrder order, IEnumerable<LoadOrderRecordVersion> versions)
    {
        return new(versions.GroupBy(v => v.LoadOrderFormId).ToDictionary(g => g.Key,
            g => new LoadOrderRecordIdentity(g.Key, order.GetOwner(g.Key), g.ToList())));
    }

    internal IReadOnlyList<LoadOrderRecordIdentity> FindByEditorId(string editorId)
    {
        return Records.Values.Where(r => string.Equals(r.Winner.EditorId, editorId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.LoadOrderFormId).ToList();
    }

    internal uint ResolveTarget(string selector, PluginLoadOrder order)
    {
        var separator = selector.LastIndexOf(':');
        if (separator > 0)
        {
            var plugin = selector[..separator];
            var entry = order.Entries.FirstOrDefault(e => e.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"Plugin is not loaded: {plugin}");
            if (!TryHex(selector[(separator + 1)..], out var local))
            {
                throw new ArgumentException("A plugin-qualified selector requires a hexadecimal FormID.");
            }
            return order.Map(entry.Path, local).LoadOrderFormId;
        }
        if (TryHex(selector, out var global)) { return global; }
        var matches = FindByEditorId(selector);
        if (matches.Count == 0) { throw new ArgumentException($"No record found for EditorID {selector}."); }
        if (matches.Count > 1)
        {
            throw new ArgumentException($"Ambiguous EditorID {selector}: " + string.Join(", ", matches.Select(m =>
                $"{m.Winner.Plugin}:0x{m.Winner.FileLocalFormId:X8} (load-order 0x{m.LoadOrderFormId:X8})")));
        }
        return matches[0].LoadOrderFormId;
    }

    private static bool TryHex(string value, out uint result)
    {
        return uint.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }
}
