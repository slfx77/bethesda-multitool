using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal readonly record struct PlacedReferenceLocation(
    PlacedReference Ref,
    uint CellFormId);
