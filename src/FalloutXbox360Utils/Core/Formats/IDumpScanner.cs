using System.IO.MemoryMappedFiles;

namespace FalloutXbox360Utils.Core.Formats;

/// <summary>
///     Optional interface for formats that can scan entire dumps for records.
///     Used by MinidumpAnalyzer to gather format-specific information.
/// </summary>
public interface IDumpScanner
{
    /// <summary>
    ///     Scan the entire dump for records of this format type using memory-mapped access.
    ///     This avoids loading the entire file into memory.
    /// </summary>
    /// <param name="accessor">Memory-mapped view accessor for the dump file.</param>
    /// <param name="fileSize">Total size of the dump file.</param>
    /// <returns>Scan results specific to this format.</returns>
    object ScanDump(MemoryMappedViewAccessor accessor, long fileSize);
}
