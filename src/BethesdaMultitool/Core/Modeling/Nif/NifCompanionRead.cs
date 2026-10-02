namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>The outcome of <see cref="NifModelReadCache.ReadCompanion" />: bytes with their digest, or a reason.</summary>
internal sealed class NifCompanionRead
{
    private NifCompanionRead(byte[]? bytes, string? sha256, string? failureReason)
    {
        Bytes = bytes;
        Sha256 = sha256;
        FailureReason = failureReason;
    }

    /// <summary>The companion bytes, or null on failure.</summary>
    public byte[]? Bytes { get; }

    /// <summary>The lowercase SHA-256 of <see cref="Bytes" />, or null on failure.</summary>
    public string? Sha256 { get; }

    /// <summary>Why the companion was not read, or null on success.</summary>
    public string? FailureReason { get; }

    /// <summary>A successful read.</summary>
    public static NifCompanionRead Succeeded(byte[] bytes, string sha256)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        return new NifCompanionRead(bytes, sha256, null);
    }

    /// <summary>A failed read.</summary>
    public static NifCompanionRead Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new NifCompanionRead(null, null, reason);
    }
}
