using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     One archive family behind the <see cref="Bsa.Index.ArchiveReader" /> facade. The reader's
///     public surface is the stable contract every consumer (CLI <c>archive</c> group, GUI, VFS,
///     transcriber) programs against; a backend supplies the per-format mechanics. Grew out of the
///     original closed BSA/BA2 union when the classic-game families (Arena/XnGine BSA, Fallout
///     DAT1/DAT2, Tactics BOS/PCK) made a third member inevitable.
///     <para>
///         Contract: a backend is IMMUTABLE after construction and all members are safe for
///         unsynchronised concurrent use (the <c>Core/Vfs</c> lock-free read contract). Per-instance
///         mutable state (the legacy BSA conversion toggles) is what <see cref="MarkShared" />
///         guards — new backends must not add any, keeping the default no-op honest.
///     </para>
/// </summary>
internal interface IArchiveBackend : IDisposable
{
    /// <summary>Short display label: <c>"BSA"</c>, <c>"BA2"</c>, later <c>"DAT1"</c>, <c>"BOS"</c>, …</summary>
    string FormatName { get; }

    /// <summary>Platform label for display (<c>"PC"</c>, <c>"Xbox 360"</c>, …).</summary>
    string PlatformLabel { get; }

    /// <summary>Total entry count across the container.</summary>
    int TotalFiles { get; }

    /// <summary>All entries, folder trees flattened. Called once per index build; may allocate.</summary>
    IReadOnlyList<ArchiveEntry> ListFiles();

    /// <summary>Extracts an entry produced by this backend's <see cref="ListFiles" />. Thread-safe.</summary>
    byte[] Extract(ArchiveEntry entry);

    /// <summary>
    ///     Called when the handle joins the shared <c>ArchiveHandleRegistry</c>: from then on the
    ///     instance is visible to every lease holder, so per-instance mutable toggles must be locked
    ///     out. Default no-op — correct for any backend that is immutable after open.
    /// </summary>
    void MarkShared()
    {
    }

    /// <summary>
    ///     Extracts an entry to <paramref name="outputDir" /> under its virtual path. Returns whether
    ///     the file was written (false when it exists and <paramref name="overwrite" /> is false).
    ///     The default extracts to memory and writes; backends with streaming writers override.
    /// </summary>
    async Task<bool> ExtractToDiskAsync(ArchiveEntry entry, string outputDir, bool overwrite)
    {
        var relative = entry.FullPath.Replace('/', '\\').TrimStart('\\');
        if (relative.Length == 0 || Path.IsPathRooted(relative) ||
            relative.Split('\\').Any(static part => part == ".."))
        {
            throw new InvalidOperationException($"Archive entry path is not extractable: '{entry.FullPath}'.");
        }

        var target = Path.Combine(outputDir, relative);
        if (!overwrite && File.Exists(target))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, Extract(entry)).ConfigureAwait(false);
        return true;
    }

    /// <summary>File-extension histogram. Default derives from <see cref="ListFiles" />.</summary>
    Dictionary<string, int> GetExtensionStats()
    {
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ListFiles())
        {
            var extension = string.IsNullOrEmpty(entry.Extension) ? "(none)" : entry.Extension;
            stats.TryGetValue(extension, out var count);
            stats[extension] = count + 1;
        }

        return stats;
    }

    /// <summary>
    ///     Files-per-folder histogram, derived from entry paths so flat containers present the same
    ///     grouping as folder-tree ones. Backends with a real folder tree override with their own.
    /// </summary>
    Dictionary<string, int> GetFolderStats()
    {
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ListFiles())
        {
            var folder = string.IsNullOrEmpty(entry.FolderPath) ? "(root)" : entry.FolderPath;
            stats.TryGetValue(folder, out var count);
            stats[folder] = count + 1;
        }

        return stats.OrderByDescending(static kv => kv.Value)
            .ToDictionary(static kv => kv.Key, static kv => kv.Value);
    }
}
