using System.Buffers.Binary;
using System.Text;
using Spectre.Console;

namespace EsmAnalyzer.Helpers;

/// <summary>
///     Shared display and formatting helpers for ESM analysis commands.
/// </summary>
public static class EsmDisplayHelpers
{
    /// <summary>
    ///     Renders a hex dump to AnsiConsole with optional highlighting.
    /// </summary>
    public static void RenderHexDump(byte[] data, long baseOffset, int? highlightStart = null,
        int? highlightLength = null)
    {
        for (var i = 0; i < data.Length; i += 16)
        {
            var lineOffset = baseOffset + i;
            var hexParts = new List<string>();
            var asciiParts = new List<char>();

            for (var j = 0; j < 16 && i + j < data.Length; j++)
            {
                var b = data[i + j];
                var byteOffset = i + j;

                // Check if this byte should be highlighted
                var isHighlight = highlightStart.HasValue && highlightLength.HasValue &&
                                  byteOffset >= highlightStart.Value &&
                                  byteOffset < highlightStart.Value + highlightLength.Value;

                if (isHighlight)
                {
                    hexParts.Add($"[green]{b:X2}[/]");
                    asciiParts.Add(b >= 32 && b < 127 ? (char)b : '.');
                }
                else
                {
                    hexParts.Add($"{b:X2}");
                    asciiParts.Add(b >= 32 && b < 127 ? (char)b : '.');
                }
            }

            var hexStr = string.Join(" ", hexParts);
            if (hexParts.Count < 16)
            {
                // Pad to align ASCII column (need to account for ANSI codes if highlighting)
                var rawLen = hexParts.Count * 3 - 1;
                hexStr += new string(' ', 47 - rawLen);
            }

            var asciiStr = Markup.Escape(new string([.. asciiParts]));
            AnsiConsole.MarkupLine($"  [grey]0x{lineOffset:X8}[/]: {hexStr}  {asciiStr}");
        }
    }

    /// <summary>
    ///     Renders a hex dump as a Panel.
    /// </summary>
    public static void RenderHexDumpPanel(byte[] data, int maxBytes = 128, string title = "Hex Dump")
    {
        var displayData = data.Length > maxBytes ? data.Take(maxBytes).ToArray() : data;
        var panel = new Panel(EsmHelpers.HexDump(displayData, maxBytes))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Header(title);

        AnsiConsole.Write(panel);
    }

    /// <summary>
    ///     Creates a styled rule/header for a record.
    /// </summary>
    public static void WriteRecordHeader(string signature, uint formId, uint? offset = null)
    {
        var text = offset.HasValue
            ? $"[cyan]{signature}[/] FormID: [yellow]0x{formId:X8}[/] @ [grey]0x{offset.Value:X8}[/]"
            : $"[cyan]{signature}[/] FormID: [yellow]0x{formId:X8}[/]";

        var rule = new Rule(text);
        rule.LeftJustified();
        AnsiConsole.Write(rule);
    }

    /// <summary>
    ///     Creates a basic info table for a record.
    /// </summary>
    public static Table CreateRecordInfoTable(uint offset, uint dataSize, uint flags)
    {
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Key")
            .AddColumn("Value");

        table.AddRow("Offset", $"0x{offset:X8}");
        table.AddRow("Data Size", $"{dataSize:N0} bytes");
        table.AddRow("Flags", $"0x{flags:X8}");
        table.AddRow("Compressed", (flags & 0x00040000) != 0 ? "[yellow]Yes[/]" : "No");

        return table;
    }

    /// <summary>
    ///     Creates a subrecord listing table.
    /// </summary>
    public static Table CreateSubrecordTable(bool includePreview = false)
    {
        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("[bold]Signature[/]")
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Offset[/]").RightAligned());

        if (includePreview) table.AddColumn("[bold]Preview[/]");

        return table;
    }

    /// <summary>
    ///     Generates a human-readable preview of subrecord data.
    /// </summary>
    public static string GenerateSubrecordPreview(string signature, byte[] data, bool bigEndian)
    {
        if (data.Length == 0) return "(empty)";

        return signature switch
        {
            "EDID" or "FULL" or "NAME" or "ICON" or "MICO" or "TX00" or "TX01" or "TX02" or "TX03" or "TX04"
                or "TX05" =>
                TryGetString(data),
            "NAM1" or "NAM2" or "SCTX" or "RNAM" =>
                TryGetString(data),
            "DATA" when data.Length == 4 =>
                bigEndian
                    ? $"uint32: {BinaryPrimitives.ReadUInt32BigEndian(data)}"
                    : $"uint32: {BinaryPrimitives.ReadUInt32LittleEndian(data)}",
            "QSTI" or "PNAM" or "TCLT" or "SCRO" when data.Length == 4 =>
                bigEndian
                    ? $"FormID: 0x{BinaryPrimitives.ReadUInt32BigEndian(data):X8}"
                    : $"FormID: 0x{BinaryPrimitives.ReadUInt32LittleEndian(data):X8}",
            "NAM3" when data.Length == 1 =>
                $"byte: {data[0]}",
            "TRDT" when data.Length >= 8 =>
                $"ResponseData ({data.Length} bytes)",
            "CTDA" when data.Length >= 24 =>
                $"Condition ({data.Length} bytes)",
            "SCHR" when data.Length >= 16 =>
                $"ScriptHeader ({data.Length} bytes)",
            "SCDA" when data.Length >= 4 =>
                $"CompiledScript ({data.Length} bytes)",
            _ => FormatBinaryPreview(data)
        };
    }

    /// <summary>
    ///     Formats raw bytes as a preview string.
    /// </summary>
    public static string FormatBinaryPreview(byte[] data, int maxBytes = 12)
    {
        if (data.Length <= 16) return BitConverter.ToString(data).Replace("-", " ");

        var preview = BitConverter.ToString(data.Take(maxBytes).ToArray()).Replace("-", " ");
        return $"{preview}... ({data.Length} bytes)";
    }

    /// <summary>
    ///     Formats bytes as hex string.
    /// </summary>
    public static string FormatBytes(byte[] data, int offset, int count)
    {
        return string.Join(" ", data.Skip(offset).Take(count).Select(b => b.ToString("X2")));
    }

    public static bool TryFormatSubrecordDetails(string signature, byte[] data, bool bigEndian, out string details)
    {
        details = string.Empty;

        switch (signature)
        {
            case "CTDA" when data.Length >= 24:
                {
                    var type = data[0];
                    var compareValue = ReadSingle(data, 4, bigEndian);
                    var compareValueRaw = ReadUInt32(data, 4, bigEndian);
                    var function = ReadUInt16(data, 8, bigEndian);
                    var param1 = ReadUInt32(data, 12, bigEndian);
                    var param2 = ReadUInt32(data, 16, bigEndian);
                    var runOn = ReadUInt32(data, 20, bigEndian);
                    var reference = data.Length >= 28 ? ReadUInt32(data, 24, bigEndian) : 0u;

                    details = string.Join(", ",
                        FormatConditionType(type),
                        $"Func=0x{function:X4}",
                        $"Comp={FormatFloat(compareValue)} (0x{compareValueRaw:X8})",
                        $"Param1=0x{param1:X8}",
                        $"Param2=0x{param2:X8}",
                        $"RunOn={FormatRunOn(runOn)} (0x{runOn:X8})",
                        $"Ref=0x{reference:X8}");
                    return true;
                }
            case "TRDT" when data.Length >= 24:
                {
                    var emotionType = ReadUInt32(data, 0, bigEndian);
                    var emotionValue = ReadInt32(data, 4, bigEndian);
                    var responseNumber = data[12];
                    var sound = ReadUInt32(data, 16, bigEndian);
                    var useAnim = data[20] != 0;

                    details = string.Join(", ",
                        $"Emotion={FormatEmotionType(emotionType)} (0x{emotionType:X8})",
                        $"Value={emotionValue}",
                        $"Response#={responseNumber}",
                        $"Sound=0x{sound:X8}",
                        $"UseAnim={(useAnim ? "Yes" : "No")}");
                    return true;
                }
            case "SCHR" when data.Length >= 16:
                {
                    uint refCount;
                    uint compiledSize;
                    uint variableCount;
                    ushort scriptType;
                    ushort flags;

                    if (data.Length >= 20)
                    {
                        refCount = ReadUInt32(data, 4, bigEndian);
                        compiledSize = ReadUInt32(data, 8, bigEndian);
                        variableCount = ReadUInt32(data, 12, bigEndian);
                        scriptType = ReadUInt16(data, 16, bigEndian);
                        flags = ReadUInt16(data, 18, bigEndian);
                    }
                    else
                    {
                        refCount = ReadUInt32(data, 0, bigEndian);
                        compiledSize = ReadUInt32(data, 4, bigEndian);
                        variableCount = ReadUInt32(data, 8, bigEndian);
                        scriptType = ReadUInt16(data, 12, bigEndian);
                        flags = ReadUInt16(data, 14, bigEndian);
                    }

                    details = string.Join(", ",
                        $"RefCount={refCount}",
                        $"CompiledSize={compiledSize}",
                        $"VarCount={variableCount}",
                        $"Type={FormatScriptType(scriptType)} (0x{scriptType:X4})",
                        $"Flags=0x{flags:X4}");
                    return true;
                }
            case "RNAM":
                {
                    details = $"Prompt={FormatStringValue(data)}";
                    return true;
                }
            case "ANAM" when data.Length == 4:
                {
                    var formId = ReadUInt32(data, 0, bigEndian);
                    details = $"Speaker=0x{formId:X8}";
                    return true;
                }
            case "KNAM" when data.Length == 4:
                {
                    var formId = ReadUInt32(data, 0, bigEndian);
                    details = $"ActorValue/Perk=0x{formId:X8}";
                    return true;
                }
            case "DNAM" when data.Length == 4:
                {
                    var value = ReadUInt32(data, 0, bigEndian);
                    details = $"SpeechChallenge={FormatSpeechChallenge(value)} (0x{value:X8})";
                    return true;
                }
        }

        return false;
    }

    private static string TryGetString(byte[] data)
    {
        var nullIdx = Array.IndexOf(data, (byte)0);
        var len = nullIdx >= 0 ? nullIdx : data.Length;
        len = Math.Min(len, 50);

        var str = Encoding.UTF8.GetString(data, 0, len);

        if (str.All(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t'))
            return str.Length < data.Length - 1 ? $"\"{str}\"..." : $"\"{str}\"";

        return FormatBinaryPreview(data);
    }

    private static string FormatStringValue(byte[] data)
    {
        var nullIdx = Array.IndexOf(data, (byte)0);
        var len = nullIdx >= 0 ? nullIdx : data.Length;
        len = Math.Min(len, 200);

        var str = Encoding.UTF8.GetString(data, 0, len);
        str = str.Replace("\r", "\\r").Replace("\n", "\\n");
        return str.Length < data.Length - 1 ? $"\"{str}\"..." : $"\"{str}\"";
    }

    private static string FormatConditionType(byte type)
    {
        var opBits = type & 0xE0;
        var compare = opBits switch
        {
            0x00 => "Equal",
            0x40 => "Greater",
            0x60 => "GreaterOrEqual",
            0x80 => "Less",
            0xA0 => "LessOrEqual",
            _ => $"Unknown(0x{opBits:X2})"
        };

        var flags = new List<string>();
        if ((type & 0x01) != 0) flags.Add("Or");
        if ((type & 0x02) != 0) flags.Add("UseAliases");
        if ((type & 0x04) != 0) flags.Add("UseGlobal");
        if ((type & 0x08) != 0) flags.Add("UsePackData");
        if ((type & 0x10) != 0) flags.Add("SwapSubjectTarget");

        var flagText = flags.Count == 0 ? "None" : string.Join("|", flags);
        return $"Type=0x{type:X2} ({compare}; {flagText})";
    }

    private static string FormatRunOn(uint runOn)
    {
        return runOn switch
        {
            0 => "Subject",
            1 => "Target",
            2 => "Reference",
            3 => "CombatTarget",
            4 => "LinkedRef",
            _ => "Unknown"
        };
    }

    private static string FormatEmotionType(uint emotionType)
    {
        return emotionType switch
        {
            0 => "Neutral",
            1 => "Anger",
            2 => "Disgust",
            3 => "Fear",
            4 => "Sad",
            5 => "Happy",
            6 => "Surprise",
            7 => "Pained",
            _ => "Unknown"
        };
    }

    private static string FormatScriptType(ushort scriptType)
    {
        return scriptType switch
        {
            0 => "Object",
            1 => "Quest",
            0x100 => "Effect",
            _ => "Unknown"
        };
    }

    private static string FormatSpeechChallenge(uint value)
    {
        return value switch
        {
            0 => "None",
            1 => "Very Easy",
            2 => "Easy",
            3 => "Average",
            4 => "Hard",
            5 => "Very Hard",
            _ => "Unknown"
        };
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.######");
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static int ReadInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static float ReadSingle(byte[] data, int offset, bool bigEndian)
    {
        var value = ReadInt32(data, offset, bigEndian);
        return BitConverter.Int32BitsToSingle(value);
    }

    /// <summary>
    ///     Displays a complete record with info table, subrecords, and optional hex dump.
    /// </summary>
    public static void DisplayRecord(AnalyzerRecordInfo rec, byte[] fileData, bool bigEndian, bool showHex,
        bool showPreview = false)
    {
        WriteRecordHeader(rec.Signature, rec.FormId, rec.Offset);
        AnsiConsole.Write(CreateRecordInfoTable(rec.Offset, rec.DataSize, rec.Flags));

        // Get decompressed data if needed
        byte[] recordData;
        try
        {
            recordData = EsmHelpers.GetRecordData(fileData, rec, bigEndian);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to decompress:[/] {ex.Message}");
            return;
        }

        // Parse and display subrecords
        var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Subrecords ({subrecords.Count}):[/]");

        var subTable = CreateSubrecordTable(showPreview);
        foreach (var sub in subrecords)
            if (showPreview)
            {
                var preview = GenerateSubrecordPreview(sub.Signature, sub.Data, bigEndian);
                subTable.AddRow(
                    $"[cyan]{sub.Signature}[/]",
                    sub.Data.Length.ToString("N0"),
                    $"0x{sub.Offset:X4}",
                    Markup.Escape(preview));
            }
            else
            {
                subTable.AddRow(
                    $"[cyan]{sub.Signature}[/]",
                    sub.Data.Length.ToString("N0"),
                    $"0x{sub.Offset:X4}");
            }

        AnsiConsole.Write(subTable);

        if (showHex && recordData.Length > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Hex Dump (first 256 bytes):[/]");
            RenderHexDumpPanel(recordData, 256);
        }

        AnsiConsole.WriteLine();
    }
}