using System.Resources;

namespace Xbox360MemoryCarver.Tools;

/// <summary>
/// Strongly-typed accessor for CLI tool localized strings.
/// Provides centralized string management for all command-line tools.
/// </summary>
/// <remarks>
/// Spectre.Console markup ([red], [bold], etc.) should be applied in code,
/// not in the resource strings, to keep resources markup-agnostic.
/// </remarks>
public static class CliStrings
{
    private static readonly ResourceManager _rm =
        new("Xbox360MemoryCarver.Tools.CliStrings", typeof(CliStrings).Assembly);

    /// <summary>Gets a localized string by key.</summary>
    public static string Get(string key) => _rm.GetString(key) ?? key;

    /// <summary>Gets a localized string and formats it with arguments.</summary>
    public static string GetFormat(string key, params object[] args)
        => string.Format(Get(key), args);

    // ===== Common Argument Descriptions =====
    public static string Arg_FilePath => Get("Arg_FilePath");
    public static string Arg_EsmFilePath => Get("Arg_EsmFilePath");
    public static string Arg_XboxFilePath => Get("Arg_XboxFilePath");
    public static string Arg_PcFilePath => Get("Arg_PcFilePath");
    public static string Arg_NifFilePath => Get("Arg_NifFilePath");
    public static string Arg_BsaFilePath => Get("Arg_BsaFilePath");
    public static string Arg_MinidumpPath => Get("Arg_MinidumpPath");
    public static string Arg_InputDirectory => Get("Arg_InputDirectory");
    public static string Arg_OutputDirectory => Get("Arg_OutputDirectory");

    // ===== Common Option Descriptions =====
    public static string Opt_Output => Get("Opt_Output");
    public static string Opt_Verbose => Get("Opt_Verbose");
    public static string Opt_RecordType => Get("Opt_RecordType");
    public static string Opt_FormId => Get("Opt_FormId");
    public static string Opt_Offset => Get("Opt_Offset");
    public static string Opt_Limit => Get("Opt_Limit");
    public static string Opt_All => Get("Opt_All");
    public static string Opt_Overwrite => Get("Opt_Overwrite");
    public static string Opt_Recursive => Get("Opt_Recursive");

    // ===== Common Table Headers =====
    public static string Table_RecordType => Get("Table_RecordType");
    public static string Table_Count => Get("Table_Count");
    public static string Table_Size => Get("Table_Size");
    public static string Table_Offset => Get("Table_Offset");
    public static string Table_FormId => Get("Table_FormId");
    public static string Table_EditorId => Get("Table_EditorId");
    public static string Table_Name => Get("Table_Name");
    public static string Table_Value => Get("Table_Value");
    public static string Table_Status => Get("Table_Status");
    public static string Table_Path => Get("Table_Path");

    // ===== Common Status Messages =====
    public static string Status_Loading(string file) => GetFormat("Status_Loading", file);
    public static string Status_Processing => Get("Status_Processing");
    public static string Status_Complete => Get("Status_Complete");
    public static string Status_Found(int count) => GetFormat("Status_Found", count);
    public static string Status_NoResults => Get("Status_NoResults");
    public static string Status_WritingOutput(string path) => GetFormat("Status_WritingOutput", path);
    public static string Status_Scanning => Get("Status_Scanning");
    public static string Status_Converting => Get("Status_Converting");

    // ===== Common Error Messages =====
    public static string Error_FileNotFound(string path) => GetFormat("Error_FileNotFound", path);
    public static string Error_DirectoryNotFound(string path) => GetFormat("Error_DirectoryNotFound", path);
    public static string Error_InvalidFormId(string value) => GetFormat("Error_InvalidFormId", value);
    public static string Error_InvalidRecordType(string value) => GetFormat("Error_InvalidRecordType", value);
    public static string Error_FailedToLoad(string message) => GetFormat("Error_FailedToLoad", message);
    public static string Error_FailedToWrite(string message) => GetFormat("Error_FailedToWrite", message);
    public static string Error_InvalidOffset(string value) => GetFormat("Error_InvalidOffset", value);
    public static string Error_UnexpectedError(string message) => GetFormat("Error_UnexpectedError", message);

    // ===== Common Display Text =====
    public static string Display_NotAvailable => Get("Display_NotAvailable");
    public static string Display_Yes => Get("Display_Yes");
    public static string Display_No => Get("Display_No");
    public static string Display_True => Get("Display_True");
    public static string Display_False => Get("Display_False");
    public static string Display_Unknown => Get("Display_Unknown");
    public static string Display_None => Get("Display_None");
    public static string Display_Empty => Get("Display_Empty");

    // ===== EsmAnalyzer Command Descriptions =====
    public static string Esm_Cmd_Stats => Get("Esm_Cmd_Stats");
    public static string Esm_Cmd_Dump => Get("Esm_Cmd_Dump");
    public static string Esm_Cmd_Trace => Get("Esm_Cmd_Trace");
    public static string Esm_Cmd_Locate => Get("Esm_Cmd_Locate");
    public static string Esm_Cmd_Convert => Get("Esm_Cmd_Convert");
    public static string Esm_Cmd_Compare => Get("Esm_Cmd_Compare");
    public static string Esm_Cmd_Diff => Get("Esm_Cmd_Diff");
    public static string Esm_Cmd_SemanticDiff => Get("Esm_Cmd_SemanticDiff");

    // ===== NifAnalyzer Command Descriptions =====
    public static string Nif_Cmd_Info => Get("Nif_Cmd_Info");
    public static string Nif_Cmd_Convert => Get("Nif_Cmd_Convert");
    public static string Nif_Cmd_List => Get("Nif_Cmd_List");

    // ===== BsaAnalyzer Command Descriptions =====
    public static string Bsa_Cmd_List => Get("Bsa_Cmd_List");
    public static string Bsa_Cmd_Extract => Get("Bsa_Cmd_Extract");
    public static string Bsa_Cmd_Info => Get("Bsa_Cmd_Info");
}
