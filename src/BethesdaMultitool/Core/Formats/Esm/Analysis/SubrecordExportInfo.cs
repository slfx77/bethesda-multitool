namespace BethesdaMultitool.Core.Formats.Esm.Analysis;

/// <summary>Signature and byte size of a single subrecord, for export listings.</summary>
public sealed class SubrecordExportInfo
{
    public string Signature { get; set; } = "";
    public int Size { get; set; }
}
