using System.Security.Cryptography;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The per-item cache scope <see cref="NifModelReader" /> requires. The operation creates one per item and disposes
///     it after the read and all companion work; it retains nothing across items, so the first read binds it to its
///     occurrence and a read of another occurrence is refused.
/// </summary>
/// <remarks>
///     Texture companions (plan section 4, "Resolution") are read through <see cref="ReadCompanion" />, bounded by what is
///     left of <see cref="MaximumCompanionBytes" />, hashed with SHA-256 and accounted together with every DDX relayout
///     output (<see cref="TryAccount" />). The texture source reads each occurrence once (it memoizes by request, by
///     occurrence and by digest). The scope keeps the accounting and the digests, not the payloads: each image owns its
///     single retained copy, so the budget bounds what one read may pull in without doubling it.
/// </remarks>
public sealed class NifModelReadCache : IModelReadCacheScope
{
    /// <summary>The default companion budget: encoded texture bytes plus DDX relayout outputs per item.</summary>
    public const long DefaultMaximumCompanionBytes = 256L * 1024 * 1024;

    private readonly Dictionary<AssetReference, string> _digests = [];
    private bool _disposed;
    private AssetReference? _owner;

    /// <summary>Creates a scope with the default companion budget.</summary>
    public NifModelReadCache()
        : this(DefaultMaximumCompanionBytes)
    {
    }

    /// <summary>Creates a scope with an explicit companion budget.</summary>
    /// <param name="maximumCompanionBytes">The positive budget for texture bytes and relayout outputs.</param>
    public NifModelReadCache(long maximumCompanionBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCompanionBytes);
        MaximumCompanionBytes = maximumCompanionBytes;
    }

    /// <summary>The occurrence this scope serves, once a read has bound it; null before the first read.</summary>
    public AssetReference? Owner => _owner;

    /// <summary>The companion budget in bytes.</summary>
    public long MaximumCompanionBytes { get; }

    /// <summary>The companion bytes accounted so far.</summary>
    public long AccountedBytes { get; private set; }

    /// <summary>The SHA-256 of every companion occurrence read so far.</summary>
    public IReadOnlyDictionary<AssetReference, string> Digests => _digests;

    /// <summary>Releases this scope; later reads through it are refused.</summary>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _owner = null;
        _digests.Clear();
        AccountedBytes = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>Binds the scope to the occurrence being read, or confirms it is the one already bound.</summary>
    /// <exception cref="ObjectDisposedException">The scope was disposed.</exception>
    /// <exception cref="ArgumentException">The scope already served another occurrence.</exception>
    internal void Bind(AssetReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_owner is null)
        {
            _owner = reference;
            return;
        }

        if (!_owner.Equals(reference))
        {
            throw new ArgumentException(
                "This NIF read cache already served another item; the operation must create one per item.",
                nameof(reference));
        }
    }

    /// <summary>
    ///     Reads one companion occurrence under the remaining budget and hashes it. The borrowed stream is disposed; the
    ///     source stays owned by the resolver's caller.
    /// </summary>
    /// <returns>The bytes and digest, or a failure reason (budget exhausted, unreadable).</returns>
    internal NifCompanionRead ReadCompanion(ModelSourceItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = MaximumCompanionBytes - AccountedBytes;
        if (item.Length is { } declared && declared > remaining)
        {
            return NifCompanionRead.Failed(
                $"its {declared} bytes exceed the {remaining} bytes left of the {MaximumCompanionBytes}-byte companion budget");
        }

        byte[] bytes;
        try
        {
            using var input = item.OpenReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
            var limit = (int)Math.Min(remaining, Array.MaxLength);
            bytes = NifModelReader.ReadBytes(input, limit, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return NifCompanionRead.Failed(
                $"it exceeds the {remaining} bytes left of the {MaximumCompanionBytes}-byte companion budget");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return NifCompanionRead.Failed("it could not be read: " + failure.Message);
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _digests[item.Reference] = sha256;
        AccountedBytes += bytes.Length;
        return NifCompanionRead.Succeeded(bytes, sha256);
    }

    /// <summary>Accounts produced bytes (a DDX relayout output) against the budget.</summary>
    /// <returns>False, and nothing accounted, when the bytes would exceed the budget.</returns>
    internal bool TryAccount(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > MaximumCompanionBytes - AccountedBytes)
        {
            return false;
        }

        AccountedBytes += bytes;
        return true;
    }
}
