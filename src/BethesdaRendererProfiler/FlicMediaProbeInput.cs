using System.Security.Cryptography;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaRendererProfiler;

/// <summary>Holds exactly two pinned original files read-only and charges every original read before allocation.</summary>
internal sealed class FlicMediaProbeInput : IDisposable
{
    internal const long MaximumOriginalBytesRead = 8L * 1024 * 1024;
    private long _readBytes;
    private int _reads;

    /// <summary>Opens the two original handles without reading payloads or enumerating their surrounding installation.</summary>
    internal FlicMediaProbeInput(string mage, string king)
    {
        Mage = new Fixture(this, mage, "MAGE.CEL", 27206,
            "47314ef76a02f0ed9461098b02807eedef5f4d9e1f7963e21dcb48a4f3898e90", 110, 119, 15, 71);
        try
        {
            King = new Fixture(this, king, "KING.FLC", 2061420,
                "849ef103ca47ed7fe935eb1a1870dbaf860c1dd84ec695aea668bca2889bf524", 320, 200, 90, 114);
        }
        catch { Mage.Dispose(); throw; }
    }

    internal Fixture Mage { get; }
    internal Fixture King { get; }
    internal long OriginalBytesRead => Interlocked.Read(ref _readBytes);
    internal int OriginalReadCount => Volatile.Read(ref _reads);

    /// <summary>Closes originals only after the probe has retired every source/decoder prerequisite.</summary>
    public void Dispose() { Mage.Dispose(); King.Dispose(); }

    /// <summary>One immutable physical occurrence; decoded bytes are never exported or cached by this probe.</summary>
    internal sealed class Fixture : IDisposable
    {
        private readonly FlicMediaProbeInput _owner;
        private readonly FileStream _input;
        private readonly Lock _gate = new();

        internal Fixture(FlicMediaProbeInput owner, string path, string name, int length, string hash,
            int width, int height, int frames, uint milliseconds)
        {
            _owner = owner;
            Path = System.IO.Path.GetFullPath(path);
            Name = name;
            Length = length;
            Sha256 = hash;
            Width = width;
            Height = height;
            Frames = frames;
            Milliseconds = milliseconds;
            if (!System.IO.Path.GetFileName(Path).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The FLC probe requires the exact named original fixture.", nameof(path));
            }
            _input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (_input.Length != length || length > 2 * 1024 * 1024)
            {
                _input.Dispose();
                throw new InvalidDataException("The original fixture length differs from its pin.");
            }
        }

        internal string Path { get; }
        internal string Name { get; }
        internal int Length { get; }
        internal string Sha256 { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int Frames { get; }
        internal uint Milliseconds { get; }
        internal TimeSpan Duration => TimeSpan.FromMilliseconds((long)Frames * Milliseconds);

        /// <summary>Charges initial hash, VFS decode, blocked preparation and final hash identically.</summary>
        internal byte[] ReadVerified(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_input.Length != Length) { throw new InvalidDataException("Original fixture length changed."); }
                if (Interlocked.Add(ref _owner._readBytes, Length) > MaximumOriginalBytesRead)
                {
                    throw new InvalidDataException("The aggregate original-read allowance was exceeded.");
                }
                Interlocked.Increment(ref _owner._reads);
                var bytes = new byte[Length];
                _input.Position = 0;
                _input.ReadExactly(bytes);
                cancellationToken.ThrowIfCancellationRequested();
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The original fixture hash differs from its pin.");
                }
                return bytes;
            }
        }

        public void Dispose() => _input.Dispose();
    }

    /// <summary>Exposes only one real physical leaf through the ordinary Core VFS seam, observing its actual retirement.</summary>
    internal sealed class Source(Fixture fixture) : IGameFileSystem
    {
        private int _disposed;
        internal int DisposalCount => Volatile.Read(ref _disposed);
        internal Action? AfterRead { get; set; }
        public string Label => System.IO.Path.GetDirectoryName(fixture.Path)!;
        public bool Exists(string path) => path.Equals(fixture.Name, StringComparison.OrdinalIgnoreCase);
        public GameFileEntry? TryStat(string path) => Exists(path) ? new GameFileEntry(fixture.Name, fixture.Length, Label) : null;
        public byte[]? TryReadAllBytes(string path) => throw new InvalidOperationException("Probe reads must use actual-provenance admission.");
        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
        {
            ObjectDisposedException.ThrowIf(DisposalCount != 0, this);
            var entry = TryStat(path);
            if (entry is null || maximumBytes < fixture.Length) { return null; }
            var bytes = fixture.ReadVerified(CancellationToken.None);
            AfterRead?.Invoke();
            return new GameFileReadResult(entry, bytes);
        }
        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
        {
            if (string.IsNullOrEmpty(prefix) || fixture.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return TryStat(fixture.Name)!;
            }
        }
        public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumEntries);
            var entries = EnumerateFiles(prefix).ToArray();
            return new GameFileEnumerationPage(entries.Take(maximumEntries).ToArray(), entries.Length > maximumEntries);
        }
        public void Dispose() => Interlocked.Increment(ref _disposed);

        /// <summary>Mounts the real original leaf with exact builder-owned identity and normal browser source leasing.</summary>
        internal BethesdaBrowseSource Wrap() => new(new AssetBrowseSession(this, fixture.Name,
            fixture.Path, AssetTreeBuilder.Build(this, fixture.Name)));
    }
}
