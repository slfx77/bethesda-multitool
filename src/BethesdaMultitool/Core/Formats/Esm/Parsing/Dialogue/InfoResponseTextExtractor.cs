using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;

/// <summary>
///     Extracts every INFO NAM1 with its TRDT response identity. Shared by the converter's
///     companion Audio Transcriber so a multi-response INFO is never flattened to response 1.
/// </summary>
internal static class InfoResponseTextExtractor
{
    public static IReadOnlyDictionary<int, string> Extract(ParsedMainRecord record)
    {
        var result = new Dictionary<int, string>();
        var trdtOrdinal = 0;
        var nam1Ordinal = 0;
        int? pendingResponseNumber = null;
        (int Ordinal, string Text)? pendingText = null;

        void Add(int responseNumber, string text)
        {
            if (responseNumber > 0 && !string.IsNullOrWhiteSpace(text))
            {
                result.TryAdd(responseNumber, text);
            }
        }

        foreach (var subrecord in record.Subrecords)
        {
            switch (subrecord.Signature)
            {
                case "TRDT":
                    trdtOrdinal++;
                    var storedNumber = subrecord.Data.Length > 12
                        ? subrecord.Data[12]
                        : (byte)0;
                    var responseNumber = storedNumber != 0 ? storedNumber : trdtOrdinal;
                    if (pendingText is { } precedingText)
                    {
                        // Some recovered Xbox INFO halves store NAM1 before its TRDT.
                        Add(responseNumber, precedingText.Text);
                        pendingText = null;
                        pendingResponseNumber = null;
                    }
                    else
                    {
                        pendingResponseNumber = responseNumber;
                    }

                    break;

                case "NAM1":
                    nam1Ordinal++;
                    var text = subrecord.DataAsString;
                    if (pendingResponseNumber is { } precedingResponseNumber)
                    {
                        Add(precedingResponseNumber, text);
                        pendingResponseNumber = null;
                    }
                    else
                    {
                        // Hold an unnumbered NAM1 for a possible following TRDT. If another
                        // NAM1 arrives first, this is a response-only split record and the
                        // prior line falls back to its own ordinal.
                        if (pendingText is { } priorText)
                        {
                            Add(priorText.Ordinal, priorText.Text);
                        }

                        pendingText = (nam1Ordinal, text);
                    }

                    break;
            }
        }

        if (pendingText is { } finalText)
        {
            Add(finalText.Ordinal, finalText.Text);
        }

        return result;
    }
}
