using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>A bounded, detached catalog over one real Shadowkey pack in an exact browser opening.</summary>
/// <remarks>
/// Slot rows are not VFS files or export checks. This object owns only managed catalog/pack data; it borrows
/// the snapshot identity and rejects preparation after retirement. The native consumer acquires its own
/// input lease. No parser cache is shared between concurrent selected-record preparations.
/// </remarks>
internal sealed class ShadowkeyPackPreviewSource
{
    internal const int MaximumSlots = 4096;
    internal const int MaximumIndexBytes = ShadowkeyModelPack.IndexHeaderLength +
        MaximumSlots * ShadowkeyModelPack.IndexEntryLength;
    internal const int MaximumPackBytes = 32 * 1024 * 1024;
    internal const int MaximumNamesBytes = 1024 * 1024;
    private readonly ShadowkeyModelPack _pack;

    private ShadowkeyPackPreviewSource(BrowserSnapshot snapshot, AssetNode node, ShadowkeyModelPack pack,
        GameFileEntry packProvenance, GameFileEntry indexProvenance, GameFileEntry? namesProvenance)
    {
        Snapshot = snapshot;
        PackReference = new AssetReference(snapshot.Source.Id, node.VirtualPath);
        _pack = pack;
        Entries = Array.AsReadOnly(pack.Entries.ToArray());
        PackProvenance = packProvenance;
        IndexProvenance = indexProvenance;
        NamesProvenance = namesProvenance;
    }

    /// <summary>The borrowed exact source generation; no long-lived lease is owned by this catalog.</summary>
    internal BrowserSnapshot Snapshot { get; }
    /// <summary>The original real VFS asset, without a fabricated child path.</summary>
    internal AssetReference PackReference { get; }
    /// <summary>Immutable slot order, including empty entries and duplicate display labels.</summary>
    internal IReadOnlyList<ShadowkeyModelPackEntry> Entries { get; }
    /// <summary>The actual readable layer that supplied the pack bytes.</summary>
    internal GameFileEntry PackProvenance { get; }
    /// <summary>The actual readable layer that supplied the required index.</summary>
    internal GameFileEntry IndexProvenance { get; }
    /// <summary>The actual readable layer that supplied optional names, or null when absent.</summary>
    internal GameFileEntry? NamesProvenance { get; }

    /// <summary>Identifies only the known container name, without reading payloads on the UI thread.</summary>
    internal static bool IsCandidate(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Kind != AssetNodeKind.Folder &&
            node.Name.Equals("models.huge", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads one catalog off-thread while retaining its exact browser source through completion.</summary>
    /// <param name="snapshot">The source opening that owns the selected real tree node.</param>
    /// <param name="node">The real models.huge node, never a slot label or a reconstructed path-only node.</param>
    /// <param name="cancellationToken">The caller's selection/pane lifetime, linked with source retirement.</param>
    /// <returns>A detached catalog with actual per-file provenance.</returns>
    /// <exception cref="ArgumentException">The source type, candidate name, or node ownership is incorrect.</exception>
    /// <exception cref="InvalidDataException">Required input is unavailable, over budget, or structurally invalid.</exception>
    /// <exception cref="OperationCanceledException">Selection cancellation or source retirement supersedes the read.</exception>
    internal static async Task<ShadowkeyPackPreviewSource> OpenAsync(
        BrowserSnapshot snapshot, AssetNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(node);
        if (snapshot.Source is not BethesdaBrowseSource source)
            throw new ArgumentException("A Shadowkey preview requires a Bethesda browser source.", nameof(snapshot));
        var root = node;
        while (root.Parent is not null) { root = root.Parent; }
        if (!ReferenceEquals(root, source.Session.Root) || !IsCandidate(node))
            throw new ArgumentException("The selected pack is not a real node of this source tree.", nameof(node));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(snapshot.CancellationToken, cancellationToken);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        try
        {
            await using var lease = snapshot.AcquireLease();
            var catalog = await Task.Run(() => Read(snapshot, source.Session.FileSystem, node, token), token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return catalog;
        }
        catch (InvalidOperationException) when (token.IsCancellationRequested)
        {
            // Retirement can win the narrow race between the first cancellation check and lease acquisition.
            throw new OperationCanceledException(token);
        }
    }

    /// <summary>Captures immutable source/slot/options identity without treating a slot as another VFS entry.</summary>
    internal ShadowkeyPackSelection CreateSelection(int slot, int frame = 0, int skin = 0,
        bool magentaIsTransparent = false)
    {
        Snapshot.CancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Entries.Count);
        return new ShadowkeyPackSelection(this, slot, frame, skin, magentaIsTransparent);
    }

    /// <summary>Parses only the selected record and snapshots it through the existing fidelity adapter.</summary>
    /// <remarks>Call off the UI thread. Corrupt records throw without invalidating other catalog slots.
    /// Empty/unsupported records retain a reason and selection identity; animation admission is unchanged.</remarks>
    internal ShadowkeyPackPreview Prepare(ShadowkeyPackSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!ReferenceEquals(selection.Source, this))
            throw new ArgumentException("The selection belongs to another pack catalog.", nameof(selection));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Snapshot.CancellationToken, cancellationToken);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        var entry = Entries[selection.Slot];
        if (entry.IsEmpty)
            return new ShadowkeyPackPreview(selection, 0, 0, null, "The selected Shadowkey pack slot is empty.");

        // GetEntryBytes is a read-only slice. Do not use GetMesh: its lazy mutable cache has no concurrency contract.
        var mesh = ShadowkeyMesh.Parse(_pack.GetEntryBytes(selection.Slot).Span,
            entry.FileName ?? $"models.huge[{entry.Index}]");
        token.ThrowIfCancellationRequested();
        var identity = new JsonObject
        {
            ["sourceId"] = selection.Reference.SourceId,
            ["sourceGeneration"] = Snapshot.Generation,
            ["packPath"] = selection.Reference.Path,
            ["occurrenceId"] = selection.Reference.OccurrenceId,
            ["slot"] = selection.Slot,
            ["packSource"] = PackProvenance.Source,
            ["indexSource"] = IndexProvenance.Source,
            ["namesSource"] = NamesProvenance?.Source
        }.ToJsonString();
        ShadowkeyNeutralSceneAdapter.TryAdapt(mesh, selection.Frame, selection.Skin,
            out var scene, out var reason, token, identity, selection.MagentaIsTransparent);
        token.ThrowIfCancellationRequested();
        return new ShadowkeyPackPreview(selection, mesh.FrameCount, mesh.Textures.Skins.Count, scene, reason);
    }

    private static ShadowkeyPackPreviewSource Read(BrowserSnapshot snapshot, IGameFileSystem filesystem,
        AssetNode node, CancellationToken token)
    {
        var path = VfsPath.Normalize(node.VirtualPath);
        var prefix = path[..(path.LastIndexOf('\\') + 1)];
        var index = ReadFile(filesystem, prefix + "models.idx", MaximumIndexBytes, required: true, token)!;
        var payload = ReadFile(filesystem, path, MaximumPackBytes, required: true, token)!;
        var names = ReadFile(filesystem, prefix + "models.txt", MaximumNamesBytes, required: false, token);
        string? text;
        try { text = names is null ? null : new UTF8Encoding(false, true).GetString(names.Data); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The Shadowkey names file is not valid UTF-8.", exception);
        }
        if (text?.StartsWith('\uFEFF') == true) { text = text[1..]; }
        token.ThrowIfCancellationRequested();
        // VFS implementations may retain their returned arrays. Own the only payload retained by the catalog.
        var pack = ShadowkeyModelPack.Parse(index.Data, payload.Data.ToArray(), text, node.Name);
        token.ThrowIfCancellationRequested();
        return new ShadowkeyPackPreviewSource(snapshot, node, pack, payload.Entry, index.Entry, names?.Entry);
    }

    private static GameFileReadResult? ReadFile(IGameFileSystem filesystem, string path, int maximumBytes,
        bool required, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var read = filesystem.TryReadAllBytesBounded(path, maximumBytes);
        token.ThrowIfCancellationRequested();
        if (read is null)
        {
            if (required || filesystem.Exists(path))
                throw new InvalidDataException($"'{path}' is unavailable, unreadable, or exceeds the {maximumBytes}-byte preview limit.");
            return null;
        }
        if (read.Data.Length > maximumBytes ||
            !VfsPath.Comparer.Equals(VfsPath.Normalize(read.Entry.Path), VfsPath.Normalize(path)))
            throw new InvalidDataException($"The bounded read for '{path}' returned an invalid payload or path.");
        return read;
    }
}

/// <summary>An immutable exact catalog/slot selection; its asset path remains the real pack path.</summary>
internal sealed class ShadowkeyPackSelection
{
    internal ShadowkeyPackSelection(ShadowkeyPackPreviewSource source, int slot,
        int frame, int skin, bool magentaIsTransparent)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, source.Entries.Count);
        Source = source;
        Reference = new AssetReference(source.PackReference.SourceId, source.PackReference.Path,
            "shadowkey-slot:" + slot.ToString(CultureInfo.InvariantCulture));
        Slot = slot;
        Frame = frame;
        Skin = skin;
        MagentaIsTransparent = magentaIsTransparent;
    }

    internal ShadowkeyPackPreviewSource Source { get; }
    internal AssetReference Reference { get; }
    internal int Slot { get; }
    internal int Frame { get; }
    internal int Skin { get; }
    internal bool MagentaIsTransparent { get; }
}

/// <summary>One preparation result, including honest empty/unsupported status and available selection ranges.</summary>
internal sealed record ShadowkeyPackPreview(
    ShadowkeyPackSelection Selection, int FrameCount, int SkinCount, ModelDocument? Scene, string? UnsupportedReason);
