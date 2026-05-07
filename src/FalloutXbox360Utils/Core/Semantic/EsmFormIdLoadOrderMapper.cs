namespace FalloutXbox360Utils.Core.Semantic;

/// <summary>
///     Maps file-local FormIDs through an ESM file's master list.
/// </summary>
internal sealed class EsmFormIdLoadOrderMapper
{
    private readonly IReadOnlyDictionary<string, int> _loadIndexByFileName;
    private readonly EsmLoadOrderFile _file;
    private readonly bool _flattenToBase;

    public EsmFormIdLoadOrderMapper(
        EsmLoadOrderFile file,
        IReadOnlyDictionary<string, int> loadIndexByFileName,
        bool flattenToBase)
    {
        _file = file;
        _loadIndexByFileName = loadIndexByFileName;
        _flattenToBase = flattenToBase;
    }

    public uint Map(uint formId)
    {
        if (formId == 0)
        {
            return 0;
        }

        var localId = formId & 0x00FFFFFFu;
        if (_flattenToBase)
        {
            return localId;
        }

        var fileIndex = (byte)(formId >> 24);
        if (fileIndex < _file.Header.Masters.Count)
        {
            var masterName = _file.Header.Masters[fileIndex];
            return _loadIndexByFileName.TryGetValue(masterName, out var masterLoadIndex)
                ? ((uint)masterLoadIndex << 24) | localId
                : formId;
        }

        if (fileIndex == _file.Header.Masters.Count)
        {
            return ((uint)_file.LoadIndex << 24) | localId;
        }

        return formId;
    }
}
