namespace EsmAnalyzer.Commands.Dmp;

// ---------------- Authority loading (light: just what we need) ----------------

internal sealed class AuthorityView
{
    public Dictionary<uint, string> WorldspaceNames { get; } = [];
    public Dictionary<uint, CellInfo> Cells { get; } = [];
    public Dictionary<(int, int), List<CellInfo>> CellsByGrid { get; } = [];
    public Dictionary<uint, int> WorldspaceCellCounts { get; } = [];

    /// <summary>
    ///     ESM-derived ref→cell map (from `references` section). Authoritative when present —
    ///     means the FormID exists as a child REFR of that CELL in at least one Sample ESM.
    /// </summary>
    public Dictionary<uint, uint> RefToCell { get; } = [];
}

internal sealed record CellInfo(
    uint FormId,
    uint? WorldspaceFormId,
    int? GridX,
    int? GridY,
    bool IsInterior,
    string EditorId);

// ---------------- Sweep CSV loading ----------------

internal sealed record SweepRow(
    string Dump,
    int GridX,
    int GridY,
    int TotalRefrs,
    int NullParentRefrs,
    int DistinctFormIds,
    string SampleFormIds);

// ---------------- Attribution heuristic ----------------

internal sealed class AttributionResult
{
    public Dictionary<(int Gx, int Gy), GridAttribution> GridAttributions { get; } = [];
    public List<CutRegion> CutRegions { get; } = [];

    /// <summary>Per-REFR position records, populated when --positions is supplied.</summary>
    public List<PositionAttribution> Positions { get; } = [];
}

internal sealed class PositionAttribution
{
    public uint FormId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float Scale { get; init; }
    public int GridX { get; init; }
    public int GridY { get; init; }
    public int FoundInDumps { get; init; }
    public uint? WorldspaceFormId { get; init; }
    public uint CellFormId { get; init; }
    public string CellEditorId { get; init; } = "";
    public uint BaseFormId { get; init; }
    public byte BaseFormType { get; init; }

    /// <summary>
    ///     Attribution source / strength. "ESM" = exact ref-to-cell from a Sample ESM
    ///     (authoritative). "HIGH"/"STRONG"/"MEDIUM"/"LOW" = grid-heuristic fallback.
    ///     "CUT" = no cell at this grid in any worldspace.
    /// </summary>
    public string Confidence { get; init; } = "";

    // -- ESM enrichment (populated when --esm matched this FormID in a Sample ESM) --

    /// <summary>REFR's own EDID subrecord (uncommon — most REFRs have no name).</summary>
    public string? EditorId { get; init; }

    /// <summary>Base record's EDID (e.g. "Mr_HouseNV").</summary>
    public string? BaseEditorId { get; init; }

    /// <summary>Base record's FULL name (e.g. "Mr. House").</summary>
    public string? BaseFullName { get; init; }

    /// <summary>Base record's model path (NIF).</summary>
    public string? ModelPath { get; init; }

    /// <summary>"REFR" / "ACHR" / "ACRE" per the ESM record type.</summary>
    public string? RecordType { get; init; }

    /// <summary>True if the ESM REFR carries an XMRK subrecord.</summary>
    public bool IsMapMarker { get; init; }

    /// <summary>Map marker display name (FULL on the REFR for XMRK records).</summary>
    public string? MarkerName { get; init; }

    /// <summary>Map marker type byte (1=City … 14=Vault).</summary>
    public ushort? MarkerType { get; init; }

    /// <summary>True if any of the enrichment fields were populated from an ESM.</summary>
    public bool HasEnrichment =>
        EditorId is not null || BaseEditorId is not null || BaseFullName is not null ||
        ModelPath is not null || RecordType is not null || IsMapMarker ||
        MarkerName is not null || MarkerType.HasValue;
}

/// <summary>
///     ESM-side enrichment for a placed reference, indexed by REFR FormID. Built once per
///     attribute-dangling run by walking every Sample ESM's parsed PlacedReference
///     records. Later ESMs only fill gaps left by earlier ones, so the first ESM listed wins
///     when the same FormID appears in multiple builds.
/// </summary>
internal sealed record ReferenceEnrichment(
    string? EditorId,
    uint BaseFormId,
    string? BaseEditorId,
    string? BaseFullName,
    string? ModelPath,
    string RecordType,
    bool IsMapMarker,
    string? MarkerName,
    ushort? MarkerType);

internal sealed class GridAttribution
{
    public int RefCount; // mutable: accumulate across dumps
    public uint WorldspaceFormId { get; init; }
    public string WorldspaceEditorId { get; init; } = "";
    public uint CellFormId { get; init; }
    public string CellEditorId { get; init; } = "";
    public int CandidateCount { get; init; }
    public string Confidence { get; init; } = "";
    public HashSet<string> EvidenceDumps { get; } = new(StringComparer.Ordinal);
    public HashSet<uint> SampleFormIds { get; } = [];
}

internal sealed class CutRegion
{
    public int RefCount;
    public int Gx { get; init; }
    public int Gy { get; init; }
    public HashSet<string> EvidenceDumps { get; } = new(StringComparer.Ordinal);
    public HashSet<uint> SampleFormIds { get; } = [];
}

// ---------------- Per-REFR positions ----------------

internal sealed record PositionRow(
    uint FormId,
    float X,
    float Y,
    float Z,
    float Scale,
    int GridX,
    int GridY,
    uint BaseFormId,
    byte BaseFormType,
    int FoundInDumps);