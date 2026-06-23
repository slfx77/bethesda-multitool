using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Support;

internal readonly record struct PlacedReferenceLocation(
    PlacedReference Ref,
    uint CellFormId);
