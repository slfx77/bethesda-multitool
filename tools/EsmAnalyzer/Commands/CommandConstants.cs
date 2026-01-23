namespace EsmAnalyzer.Commands;

/// <summary>
///     Shared constants for command-line arguments and options.
/// </summary>
internal static class CommandConstants
{
    // Argument descriptions
    public const string FilePathDescription = "Path to the ESM file";
    public const string XboxFileDescription = "Path to Xbox 360 ESM file";
    public const string PcFileDescription = "Path to PC ESM file";

    // Option descriptions
    public const string OutputDescription = "Output file path";
    public const string VerboseDescription = "Enable verbose output";

    // Common display text
    public const string NotAvailable = "[dim]N/A[/]";
    public const string RecordType = "Record type to filter by";
}
