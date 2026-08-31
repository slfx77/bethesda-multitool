namespace BethesdaMultitool.Core.Formats;

/// <summary>
///     Result of a file format conversion operation.
/// </summary>
public class ConversionResult
{
    public bool Success { get; init; }
    public byte[]? OutputData { get; init; }
    public byte[]? AtlasData { get; init; }
    /// <summary>
    ///     Set by a converter when its <b>own</b> output is incomplete for a reason the carver
    ///     cannot see from the source bytes — not when the source was gapped, which the carver
    ///     already knows from <c>CarveResidency</c> and ORs in.
    ///     <para>
    ///         No converter sets this today. DDX/NIF/XMA conversion is currently all-or-nothing:
    ///         each either produces a complete output or fails, and neither the DDX parser nor
    ///         <c>NifConverter</c> reports "succeeded, but dropped something". Leaving it unset is
    ///         accurate; setting it speculatively would report partiality that was never measured.
    ///     </para>
    /// </summary>
    public bool IsPartial { get; init; }
    public string? Notes { get; init; }
    public string? ConsoleOutput { get; init; }

    /// <summary>
    ///     Creates a failed conversion result carrying the given explanatory notes.
    /// </summary>
    public static ConversionResult Failure(string notes)
    {
        return new ConversionResult { Success = false, Notes = notes };
    }
}
