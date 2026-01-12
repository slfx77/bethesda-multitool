using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Commands;

/// <summary>
///     Raw hex dump command.
/// </summary>
internal static class HexCommands
{
    public static int Hex(string path, long offset, int length)
    {
        var data = File.ReadAllBytes(path);

        if (offset < 0 || offset >= data.Length)
        {
            Console.Error.WriteLine($"Offset 0x{offset:X} out of range (file size: {data.Length})");
            return 1;
        }

        length = (int)Math.Min(length, data.Length - offset);
        Console.WriteLine($"File: {Path.GetFileName(path)}");
        Console.WriteLine($"Offset: 0x{offset:X4}, Length: {length}");
        Console.WriteLine();

        HexDump(data, (int)offset, length);
        return 0;
    }
}