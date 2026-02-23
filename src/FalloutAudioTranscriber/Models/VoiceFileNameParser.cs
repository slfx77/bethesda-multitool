using System.Globalization;

namespace FalloutAudioTranscriber.Models;

/// <summary>
///     Parses Bethesda voice filenames of the format:
///     {topicEditorId}_{formId:8hex}_{index}.{ext}
///     The FormID is always 8 hex chars, and the index is a single digit before the extension.
///     We parse from the right to handle underscores in editor IDs.
/// </summary>
public static class VoiceFileNameParser
{
    /// <summary>
    ///     Try to parse a voice filename into its components.
    /// </summary>
    /// <param name="fileName">
    ///     Just the filename, not the full path (e.g.,
    ///     "rscommrangerfoxtr_rscommrangerfoxtrottopic018_00126f4c_1.xma").
    /// </param>
    /// <param name="formId">Parsed FormID.</param>
    /// <param name="responseIndex">Parsed response index.</param>
    /// <param name="topicEditorId">Everything before the FormID (the topic editor ID fragment).</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParse(string fileName, out uint formId, out int responseIndex, out string topicEditorId)
    {
        formId = 0;
        responseIndex = 0;
        topicEditorId = "";

        // Strip extension
        var dotIndex = fileName.LastIndexOf('.');
        if (dotIndex < 0)
        {
            return false;
        }

        var baseName = fileName[..dotIndex];

        // Find the last underscore — this separates the index
        var lastUnderscore = baseName.LastIndexOf('_');
        if (lastUnderscore < 0)
        {
            return false;
        }

        var indexPart = baseName[(lastUnderscore + 1)..];
        if (!int.TryParse(indexPart, out responseIndex))
        {
            return false;
        }

        // Find the second-to-last underscore — this separates the FormID
        var formIdEnd = lastUnderscore;
        var formIdUnderscore = baseName.LastIndexOf('_', formIdEnd - 1);
        if (formIdUnderscore < 0)
        {
            return false;
        }

        var formIdPart = baseName[(formIdUnderscore + 1)..formIdEnd];
        if (formIdPart.Length != 8 || !uint.TryParse(formIdPart, NumberStyles.HexNumber, null, out formId))
        {
            return false;
        }

        // Everything before the FormID is the topic editor ID
        topicEditorId = baseName[..formIdUnderscore];

        return true;
    }
}
