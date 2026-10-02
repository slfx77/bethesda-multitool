using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

internal sealed record PluginLoadOrderEntry(string Path, string Name, int Index, IReadOnlyList<string> Masters);

internal sealed record LoadOrderFormIdReference(uint FileLocalFormId, uint LoadOrderFormId,
    string? OwnerPlugin, bool OwnerLoaded, bool WasClamped, bool IsEngineReserved);

/// <summary>An explicit TES4 full-plugin order. Missing masters occupy separate, stable reserved slots.</summary>
internal sealed class PluginLoadOrder
{
    private readonly Dictionary<string, PluginLoadOrderEntry> _entries;
    private readonly Dictionary<string, int> _slots;
    private readonly Dictionary<int, string> _owners;

    private PluginLoadOrder(IReadOnlyList<PluginLoadOrderEntry> entries, IReadOnlyList<string> missing)
    {
        Entries = entries;
        MissingMasters = missing;
        _entries = entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        _slots = entries.ToDictionary(e => e.Name, e => e.Index, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < missing.Count; i++)
        {
            _slots.Add(missing[i], entries.Count + i);
        }
        _owners = _slots.ToDictionary(e => e.Value, e => e.Key);
    }

    public IReadOnlyList<PluginLoadOrderEntry> Entries { get; }
    public IReadOnlyList<string> MissingMasters { get; }

    internal static PluginLoadOrder Open(IReadOnlyList<string> paths, bool allowMissingMasters = false)
    {
        var order = Create(paths, allowMissingMasters, ReadMasters);
        var games = paths.Select(path => GameDetector.DetectFromFile(path).Game).Distinct().ToList();
        if (games.Count != 1)
        {
            throw new ArgumentException("Explicit load order cannot mix plugins from different games.");
        }
        if (games[0] is not (BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas))
        {
            throw new NotSupportedException("Explicit load-order inspection currently supports Fallout 3 and New Vegas full plugins.");
        }
        return order;
    }

    internal static PluginLoadOrder Create(IReadOnlyList<string> paths, bool allowMissingMasters,
        Func<string, IReadOnlyList<string>> readMasters)
    {
        if (paths.Count is 0 or > 255)
        {
            throw new ArgumentException("Load order must contain 1–255 full plugins.");
        }
        var entries = paths.Select((path, index) => new PluginLoadOrderEntry(
            System.IO.Path.GetFullPath(path), System.IO.Path.GetFileName(path), index, readMasters(path))).ToList();
        var duplicate = entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new ArgumentException($"Duplicate plugin filename in load order: {duplicate.Key}");
        }
        var slots = entries.ToDictionary(e => e.Name, e => e.Index, StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Masters.Count > 254 || entry.Masters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Masters.Count)
            {
                throw new ArgumentException($"Invalid master list in {entry.Name}.");
            }
            foreach (var master in entry.Masters)
            {
                if (slots.TryGetValue(master, out var slot))
                {
                    if (slot >= entry.Index)
                    {
                        throw new ArgumentException($"Master {master} must precede {entry.Name} in the load order.");
                    }
                }
                else if (!allowMissingMasters)
                {
                    throw new ArgumentException($"Missing master {master}, required by {entry.Name}. Supply it or use --allow-missing-masters.");
                }
                else if (!missing.Contains(master, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(master);
                }
            }
        }
        if (entries.Count + missing.Count > 255)
        {
            throw new ArgumentException("Loaded plugins plus missing-master reservations exceed the 255 full-plugin slots.");
        }
        return new PluginLoadOrder(entries, missing);
    }

    internal LoadOrderFormIdReference Map(string sourcePath, uint fileLocal)
    {
        var entry = _entries[System.IO.Path.GetFileName(sourcePath)];
        if (fileLocal is 0 or 0xFFFFFFFF || (fileLocal >> 24) == 0xFF)
        {
            return new(fileLocal, fileLocal, null, false, false, false);
        }
        // Engine-created forms (Player, PlayerRef, etc.) are not owned by a DLC's slot.
        if (fileLocal < 0x800)
        {
            return new(fileLocal, fileLocal, null, true, false, true);
        }
        var localIndex = (int)(fileLocal >> 24);
        var owner = localIndex < entry.Masters.Count ? entry.Masters[localIndex] : entry.Name;
        var mapped = ((uint)_slots[owner] << 24) | (fileLocal & 0x00FFFFFF);
        return new(fileLocal, mapped, owner, _entries.ContainsKey(owner), localIndex > entry.Masters.Count, false);
    }

    internal string? GetOwner(uint globalId)
    {
        return globalId < 0x800 || globalId == uint.MaxValue
            ? null
            : _owners.GetValueOrDefault((int)(globalId >> 24));
    }

    /// <summary>Read the complete header rather than silently truncating a long MAST list at 8 KiB.</summary>
    internal static IReadOnlyList<string> ReadMasters(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var prefix = new byte[24];
        input.ReadExactly(prefix);
        if (!prefix.AsSpan(0, 4).SequenceEqual("TES4"u8) && !prefix.AsSpan(0, 4).SequenceEqual("4SET"u8))
        {
            throw new InvalidDataException($"Explicit load order requires TES4-family plugins: {path}");
        }
        var bigEndian = EsmParser.IsBigEndian(prefix);
        var size = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));
        if (size > 64 * 1024 * 1024 || size + 20L > input.Length)
        {
            throw new InvalidDataException($"Invalid or truncated TES4 header: {path}");
        }
        // Twenty extra bytes also cover Oblivion's shorter record header. ParseFileHeader chooses framing.
        var bytes = new byte[(int)Math.Min(size + 24L, input.Length)];
        input.Position = 0;
        input.ReadExactly(bytes);
        if (size + (long)PluginFormat.Detect(bytes).RecordHeaderSize > input.Length)
        {
            throw new InvalidDataException($"Truncated TES4 header: {path}");
        }
        var header = EsmParser.ParseFileHeader(bytes) ?? throw new InvalidDataException($"Invalid plugin header: {path}");
        if ((header.RecordFlags & 0x200) != 0 || System.IO.Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Explicit load-order inspection currently supports full plugins, not light plugins.");
        }
        return header.Masters;
    }
}
