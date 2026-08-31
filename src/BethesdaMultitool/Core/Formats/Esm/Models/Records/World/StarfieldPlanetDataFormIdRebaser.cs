namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Deep-clones one PNDT while rebasing only proven FormIDs. Coordinates, top-level GNAM bits,
///     and the body SystemId/ParentPlanetId/PlanetId values are scalar data and deliberately bypass
///     the mapper.
/// </summary>
internal static class StarfieldPlanetDataFormIdRebaser
{
    internal static StarfieldPlanetDataRecord Rebase(
        StarfieldPlanetDataRecord record,
        Func<uint, uint> rebaseFormId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rebaseFormId);

        return record with
        {
            FormId = RebaseNonzero(record.FormId, rebaseFormId),
            MasterWorldspaces = Array.AsReadOnly(record.MasterWorldspaces
                .Select(entry => Rebase(entry, rebaseFormId))
                .ToArray()),
            WorldspaceOverrides = Array.AsReadOnly(record.WorldspaceOverrides
                .Select(delta => delta with { Entry = Rebase(delta.Entry, rebaseFormId) })
                .ToArray()),
            Body = record.Body is null
                ? null
                : record.Body with
                {
                    Atmosphere = record.Body.Atmosphere with
                    {
                        AtmosphereFormId = RebaseNonzero(
                            record.Body.Atmosphere.AtmosphereFormId,
                            rebaseFormId)
                    }
                }
        };
    }

    private static StarfieldPlanetWorldspaceEntry Rebase(
        StarfieldPlanetWorldspaceEntry entry,
        Func<uint, uint> rebaseFormId) =>
        entry with
        {
            WorldspaceFormId = RebaseNonzero(entry.WorldspaceFormId, rebaseFormId)
        };

    private static uint RebaseNonzero(uint value, Func<uint, uint> rebaseFormId) =>
        value == 0 ? 0 : rebaseFormId(value);
}
