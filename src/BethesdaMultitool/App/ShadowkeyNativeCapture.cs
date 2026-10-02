using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Render;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.Core.Media;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Operations;
using Slfx77.Multitool.Core.Presentation;
using Slfx77.Multitool.Media.Images;
using Slfx77.Multitool.WinUI.Direct3D12.Scenes;

namespace BethesdaMultitool;

/// <summary>Captures one admitted browser-policy scene without a window, dispatcher, or interchange roundtrip.</summary>
/// <remarks>This diagnostic proves only offscreen native rendering. It does not exercise GUI enumeration,
/// input, replacement, localization, or window close. Cancellation never bypasses required native retirement.</remarks>
internal static class ShadowkeyNativeCapture
{
    private const long Mebibyte = 1024L * 1024;
    private static readonly JsonSerializerOptions ReceiptJson = new() { WriteIndented = true, IncludeFields = true };

    /// <summary>Locks three bounded real inputs, prepares the exact catalog selection, and publishes new outputs.</summary>
    /// <param name="options">Canonical paths and explicit frame, skin, slot, and color-key choice.</param>
    /// <param name="token">Cooperative command cancellation, also checked by image publication.</param>
    /// <returns>Completion only after native disposal and both output publications succeed.</returns>
    /// <exception cref="NotSupportedException">The existing source adapter declines the complete selected record.</exception>
    /// <exception cref="IOException">Input/output admission, reading, or publication fails.</exception>
    /// <remarks>PNG and receipt are separately atomic, not a two-file transaction. A receipt failure leaves
    /// the already complete PNG for inspection and reports failure; it never deletes or overwrites that file.</remarks>
    internal static async Task RunAsync(ShadowkeyNativeCaptureOptions options, CancellationToken token)
    {
        options.EnsureDestinationsAbsent();
        token.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(options.PackPath)!;
        using var pack = OpenBounded(options.PackPath, ShadowkeyPackPreviewSource.MaximumPackBytes);
        using var index = OpenBounded(Path.Combine(directory, "models.idx"), ShadowkeyPackPreviewSource.MaximumIndexBytes);
        using var names = OpenBounded(Path.Combine(directory, "models.txt"), ShadowkeyPackPreviewSource.MaximumNamesBytes);
        var packHash = await HashAsync(pack, token).ConfigureAwait(false);
        var indexHash = await HashAsync(index, token).ConfigureAwait(false);
        var namesHash = await HashAsync(names, token).ConfigureAwait(false);

        await using var owner = new BrowserSession();
        var source = new BethesdaBrowseSource(CreateSession(directory, Path.GetFileName(options.PackPath), pack.Length));
        try { await owner.ReplaceAsync(source, token).ConfigureAwait(false); }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var snapshot = owner.Current ?? throw new InvalidOperationException("The capture source was not published.");
        var catalog = await ShadowkeyPackPreviewSource.OpenAsync(snapshot, source.Session.Root.Children[0], token)
            .ConfigureAwait(false);
        var selection = catalog.CreateSelection(options.Slot, options.Frame, options.Skin, options.MagentaKey);
        var preview = await Task.Run(() => catalog.Prepare(selection, token), token).ConfigureAwait(false);
        if (preview.Scene is null)
            throw new NotSupportedException(preview.UnsupportedReason ?? "The selected Shadowkey slot has no admitted scene.");
        var presentation = ShadowkeyNativePresentation.Create(preview, token);
        var camera = presentation.FrameCamera(presentation.Bounds, 1);
        var request = CreateRequest(camera);
        var image = await CaptureAsync(snapshot, presentation.Document, request, token).ConfigureAwait(false);

        await VerifyUnchangedAsync(pack, packHash, token).ConfigureAwait(false);
        await VerifyUnchangedAsync(index, indexHash, token).ConfigureAwait(false);
        await VerifyUnchangedAsync(names, namesHash, token).ConfigureAwait(false);
        options.EnsureDestinationsAbsent();
        await PngExporter.SavePngAsync(image, options.OutputPath, overwrite: false, token).ConfigureAwait(false);
        using var png = OpenBounded(options.OutputPath, 8 * Mebibyte);
        var pngHash = await HashAsync(png, token).ConfigureAwait(false);
        var entry = catalog.Entries[selection.Slot];
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "bmt-shadowkey-native-capture-v1",
            scope = "Native offscreen only; GUI and release acceptance unrun.",
            sourceDirectory = directory,
            inputs = new[]
            {
                new { path = pack.Name, length = pack.Length, sha256 = packHash },
                new { path = index.Name, length = index.Length, sha256 = indexHash },
                new { path = names.Name, length = names.Length, sha256 = namesHash }
            },
            sourceId = snapshot.Source.Id,
            sourceGeneration = snapshot.Generation,
            reference = selection.Reference,
            packProvenance = catalog.PackProvenance,
            indexProvenance = catalog.IndexProvenance,
            namesProvenance = catalog.NamesProvenance,
            slot = selection.Slot,
            entry.Offset,
            entry.Size,
            entry.FileName,
            frame = selection.Frame,
            skin = selection.Skin,
            magentaKey = selection.MagentaIsTransparent,
            preview.FrameCount,
            preview.SkinCount,
            sceneIdentity = presentation.Document.SourceIdentity,
            sceneIndex = presentation.Document.DefaultSceneIndex,
            samplers = presentation.Document.Samplers,
            materials = presentation.Document.Materials,
            images = presentation.Document.Images.Select(encoded => new
            {
                encoded.Name,
                encoded.ByteLength,
                pngSha256 = Convert.ToHexStringLower(SHA256.HashData(encoded.CopyContent()))
            }),
            palettePolicy = "Existing Shadowkey 0RGB444 palette expanded to RGBA; hypothetical magenta key disabled unless explicitly selected.",
            samplerPolicy = "Existing adapter Repeat/Repeat sampler and authored out-of-range UVs.",
            cameraPolicy = "ShadowkeyNativePresentation.FrameCamera at output aspect 1; initial browser framing.",
            appearance = request.Appearance,
            camera,
            width = image.Width,
            height = image.Height,
            rgbaSha256 = Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
            pngPath = options.OutputPath,
            pngSha256 = pngHash,
            nativeDisposalSucceeded = true,
            inputHashesUnchanged = true,
            ignoreOutputAlpha = request.IgnoreOutputAlpha
        }, ReceiptJson);
        using var receiptStream = new MemoryStream(receipt, writable: false);
        await StagedStreamExport.WriteAsync(receiptStream, options.ReceiptPath, receipt.Length,
            overwrite: false, cancellationToken: token).ConfigureAwait(false);
    }

    /// <summary>Builds a diagnostic tree containing only the statted real pack without recursively enumerating its directory.</summary>
    /// <param name="directory">The exact loose-file root used for catalog sibling resolution.</param>
    /// <param name="packName">The verified real pack basename, never a display slot label.</param>
    /// <param name="packLength">The locked input's measured length.</param>
    /// <returns>A source-owned session whose only leaf is the real model pack.</returns>
    private static AssetBrowseSession CreateSession(string directory, string packName, long packLength)
    {
        var filesystem = new LooseFileSystem(directory);
        try
        {
            var entry = filesystem.TryStat(packName) ?? throw new IOException("The locked pack is unavailable to the VFS.");
            if (entry.Size != packLength) throw new IOException("The locked pack length changed before source composition.");
            var label = Path.GetFileName(directory);
            var root = new AssetNode(label, "", AssetNodeKind.Folder, 0);
            root.AddChild(new AssetNode(packName, entry.Path, AssetNodeKind.Model, entry.Size));
            return new AssetBrowseSession(filesystem, label, directory, root);
        }
        catch
        {
            filesystem.Dispose();
            throw;
        }
    }

    /// <summary>Reuses the native browser's initial appearance and resource budgets at a fixed diagnostic resolution.</summary>
    /// <param name="camera">The exact browser presentation's framing result.</param>
    /// <returns>A static, opaque-output request with no animation or additional coordinate conversion.</returns>
    private static NativeSceneCaptureRequest CreateRequest(CameraNavigationState camera) =>
        new(ShadowkeyNativeCaptureOptions.Dimension, ShadowkeyNativeCaptureOptions.Dimension,
            new NativeSceneAppearance(camera.Pose, camera.Lens, new Vector3(0.04f), Vector3.UnitY, 1, Vector3.One))
        {
            Preparation = new NativeScenePreparationOptions(256 * Mebibyte, 16 * (int)Mebibyte, 256 * Mebibyte),
            Limits = new NativeSceneLimits(256UL * 1024 * 1024, 512UL * 1024 * 1024, 128UL * 1024 * 1024),
            MaximumTargetBytes = 4 * Mebibyte,
            MaximumReadbackBytes = 4 * Mebibyte,
            IgnoreOutputAlpha = true,
            LoopClip = false
        };

    /// <summary>Acquires the exact source retention only when native preparation actually invokes its factory.</summary>
    /// <param name="snapshot">The still-current catalog source opening.</param>
    /// <param name="scene">The already admitted immutable document, used directly.</param>
    /// <param name="token">Native preparation cancellation.</param>
    /// <returns>One native-owned input, including ownership of its source lease.</returns>
    private static async Task<NativeSceneInput> AcquireAsync(BrowserSnapshot snapshot, ModelDocument scene,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        snapshot.CancellationToken.ThrowIfCancellationRequested();
        var lease = snapshot.AcquireLease();
        try { return new NativeSceneInput(scene, scene.DefaultSceneIndex, lease); }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Returns owned pixels only after native retirement, preserving capture and cleanup failures together.</summary>
    /// <param name="snapshot">The exact catalog source whose lease is acquired by the input factory.</param>
    /// <param name="scene">The browser-admitted normalized document.</param>
    /// <param name="request">Fixed bounded output, camera, appearance, and allocation policy.</param>
    /// <param name="token">Cooperative cancellation; disposal itself is not canceled.</param>
    /// <returns>The detached RGBA image after successful capture-owner disposal.</returns>
    private static async Task<DecodedImage> CaptureAsync(BrowserSnapshot snapshot, ModelDocument scene,
        NativeSceneCaptureRequest request, CancellationToken token)
    {
        var capture = new NativeSceneCapture();
        DecodedImage? image = null;
        Exception? failure = null;
        try
        {
            image = await capture.CaptureAsync(current => AcquireAsync(snapshot, scene, current), request, token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        try { await capture.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup)
        {
            if (failure is null) throw;
            throw new AggregateException("Native capture and retirement both failed.", failure, cleanup);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return image ?? throw new InvalidOperationException("Native capture completed without pixels.");
    }

    /// <summary>Opens a nonempty bounded file read-only and prevents cooperating Windows writers from changing it.</summary>
    /// <param name="path">One explicit source or completed PNG path.</param>
    /// <param name="maximumBytes">Inclusive per-file byte limit before hashing or decoding.</param>
    /// <returns>A caller-owned read handle; no input payload is copied to an artifact.</returns>
    private static FileStream OpenBounded(string path, long maximumBytes)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length is <= 0 || stream.Length > maximumBytes)
                throw new InvalidDataException($"'{path}' exceeds the nonempty {maximumBytes}-byte capture admission.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Hashes the entire bounded handle and restores its position for the next verification.</summary>
    /// <param name="stream">An owned seekable handle already admitted by length.</param>
    /// <param name="token">Cancellation during bounded streaming.</param>
    /// <returns>The lowercase raw-byte SHA-256 digest.</returns>
    private static async Task<string> HashAsync(FileStream stream, CancellationToken token)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Proves source byte preservation before any capture output is published.</summary>
    /// <param name="stream">The same locked source handle used for its original digest.</param>
    /// <param name="expected">The pre-preparation raw digest.</param>
    /// <param name="token">Cancellation during the final streaming comparison.</param>
    /// <returns>Completion only if the source bytes are unchanged.</returns>
    private static async Task VerifyUnchangedAsync(FileStream stream, string expected, CancellationToken token)
    {
        if (await HashAsync(stream, token).ConfigureAwait(false) != expected)
            throw new IOException($"Source bytes changed during capture: '{stream.Name}'.");
    }
}
