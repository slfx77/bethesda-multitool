using BethesdaMultitool.Core.Formats.InstallShield;
using Microsoft.Win32.SafeHandles;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     InstallShield 5 cabinets (<c>DATA1.CAB</c>) behind the backend seam — the container Redguard's
///     Disc 1 keeps the whole install in. Immutable after open: the tables are parsed once by
///     <see cref="InstallShieldCabinet.Parse" /> and every extraction uses positioned reads through
///     one shared handle with its own per-call state, so members are safe for unsynchronised
///     concurrent use (the <c>Core/Vfs</c> contract).
///     <para>
///         Only live entries are surfaced: unshield's <c>unshield_file_is_valid</c> excludes the
///         placeholders that carry the invalid flag or no name/data, and so does this listing.
///         Paths are the cabinet's directory plus the file name, forward-slashed.
///     </para>
/// </summary>
internal sealed class InstallShieldBackend : IArchiveBackend
{
    private readonly InstallShieldCabinet _cabinet;
    private readonly List<InstallShieldFileDescriptor> _files;
    private readonly List<string> _folders;
    private readonly SafeFileHandle _handle;

    public InstallShieldBackend(InstallShieldCabinet cabinet)
    {
        ArgumentNullException.ThrowIfNull(cabinet);
        _cabinet = cabinet;
        _files = cabinet.Files.Where(static f => f.IsValid).ToList();
        _folders = cabinet.Directories
            .Select(static d => d.Replace('\\', '/').Trim('/'))
            .ToList();
        _handle = File.OpenHandle(cabinet.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public string FormatName => "InstallShield CAB";

    public string PlatformLabel => "PC";

    public int TotalFiles => _files.Count;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var list = new List<ArchiveEntry>(_files.Count);
        foreach (var file in _files)
        {
            var folder = _folders[file.DirectoryIndex];
            var extension = Path.GetExtension(file.Name);
            list.Add(new ArchiveEntry(
                FullPathOf(folder, file.Name),
                folder,
                file.Name,
                string.IsNullOrEmpty(extension) ? string.Empty : extension.ToLowerInvariant(),
                file.ExpandedSize,
                file.DataOffset,
                file.IsCompressed,
                file));
        }

        return list;
    }

    public IEnumerable<string> EnumerateFilePaths()
    {
        foreach (var file in _files)
        {
            yield return FullPathOf(_folders[file.DirectoryIndex], file.Name);
        }
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        if (entry.Record is not InstallShieldFileDescriptor file)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        return _cabinet.Extract(_handle, file);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private static string FullPathOf(string folder, string name) =>
        folder.Length == 0 ? name : folder + "/" + name;
}
