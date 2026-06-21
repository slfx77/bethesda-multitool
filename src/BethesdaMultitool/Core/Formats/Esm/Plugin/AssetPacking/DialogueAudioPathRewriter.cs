namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Filename-token rewriting helpers for dialogue voice paths consumed by
///     <see cref="DialogueAudioCsvAssetCollector" />. Swaps the embedded source-FormID hex
///     token in a <c>&lt;stem&gt;_&lt;formid&gt;_&lt;resp&gt;.&lt;ext&gt;</c> filename for the
///     allocated FormID's hex, without touching any other hex-looking token in the path.
/// </summary>
internal static class DialogueAudioPathRewriter
{
    internal static string ReplaceSourceFormIdInFilename(string tail, string sourceHex, string allocatedHex)
    {
        // Only replace the formid token that sits between the response number and the rest of
        // the filename to avoid accidentally rewriting an unrelated hex token elsewhere.
        // Filename shape: <stem>_<formid>_<resp>.<ext>
        var lastSep = tail.LastIndexOf('\\');
        if (lastSep < 0)
        {
            return tail;
        }

        var dirPart = tail[..lastSep];
        var fileName = tail[(lastSep + 1)..];

        var dot = fileName.LastIndexOf('.');
        var stemAndResp = dot >= 0 ? fileName[..dot] : fileName;
        var ext = dot >= 0 ? fileName[dot..] : string.Empty;

        var underscoreBeforeResp = stemAndResp.LastIndexOf('_');
        if (underscoreBeforeResp < 0)
        {
            return tail;
        }

        var stemAndFid = stemAndResp[..underscoreBeforeResp];
        var resp = stemAndResp[(underscoreBeforeResp + 1)..];

        var underscoreBeforeFid = stemAndFid.LastIndexOf('_');
        if (underscoreBeforeFid < 0)
        {
            return tail;
        }

        var stem = stemAndFid[..underscoreBeforeFid];
        var fidToken = stemAndFid[(underscoreBeforeFid + 1)..];

        // Only swap when the token actually matches the source FormID — defensive guard
        // against accidentally rewriting differently-shaped filenames.
        if (!string.Equals(fidToken, sourceHex, StringComparison.OrdinalIgnoreCase))
        {
            return tail;
        }

        return dirPart + "\\" + stem + "_" + allocatedHex + "_" + resp + ext;
    }
}
