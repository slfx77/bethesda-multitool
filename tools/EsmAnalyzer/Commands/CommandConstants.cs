using Xbox360MemoryCarver.Tools;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Shared constants for command-line arguments and options.
///     Delegates to CliStrings for localized values.
/// </summary>
internal static class CommandConstants
{
    // Argument descriptions (delegated to CliStrings)
    public static string FilePathDescription => CliStrings.Arg_EsmFilePath;
    public static string XboxFileDescription => CliStrings.Arg_XboxFilePath;
    public static string PcFileDescription => CliStrings.Arg_PcFilePath;

    // Option descriptions (delegated to CliStrings)
    public static string OutputDescription => CliStrings.Opt_Output;
    public static string VerboseDescription => CliStrings.Opt_Verbose;
    public static string RecordType => CliStrings.Opt_RecordType;

    // Common display text (markup applied here, base text from CliStrings)
    public static string NotAvailable => $"[dim]{CliStrings.Display_NotAvailable}[/]";
}