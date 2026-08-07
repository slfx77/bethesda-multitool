using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

/// <summary>
///     Registry mapping ESM record-type signatures to encoders.
/// </summary>
public sealed class RecordEncoderRegistry
{
    private readonly Dictionary<string, IRecordEncoder> _byType = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> SupportedRecordTypes => _byType.Keys;

    /// <summary>Registers an encoder under its own declared <see cref="IRecordEncoder.RecordType" />.</summary>
    public void Register(IRecordEncoder encoder)
    {
        _byType[encoder.RecordType] = encoder;
    }

    /// <summary>
    ///     Register an encoder under a specific record type, overriding its declared
    ///     <see cref="IRecordEncoder.RecordType" />. Used by encoders that handle multiple
    ///     signatures (e.g., <see cref="Encoders.Item.LvliEncoder" /> handles LVLI/LVLN/LVLC).
    /// </summary>
    public void Register(string recordType, IRecordEncoder encoder)
    {
        _byType[recordType] = encoder;
    }

    private void RegisterAll(params IRecordEncoder[] encoders)
    {
        foreach (var encoder in encoders)
        {
            Register(encoder);
        }
    }

    /// <summary>Tries to get the encoder registered for a record type.</summary>
    public bool TryGet(string recordType, out IRecordEncoder? encoder)
    {
        return _byType.TryGetValue(recordType, out encoder);
    }

    /// <summary>Gets the encoder registered for a record type, or null if none is registered.</summary>
    public IRecordEncoder? Get(string recordType)
    {
        return _byType.GetValueOrDefault(recordType);
    }

    /// <summary>
    ///     Builds the full encoder registry. Every record type with a runtime reader has an
    ///     encoder so its records can be emitted to the output ESP.
    ///     Encoders either emit overrides (re-emit a subrecord with new model data and let
    ///     the merge engine retain unmapped subrecords verbatim from the master ESM) or full
    ///     new records (build the entire subrecord stream from scratch via <c>EncodeNew</c>).
    ///     Placed-reference types (REFR/ACHR/ACRE) and CELL live inside cell-children GRUPs
    ///     and are routed through the cell-children pipeline rather than top-level emission.
    ///     Still deferred:
    ///     - NAVI — global pathfinding lookup table; master FNV.esm's NAVI covers every vanilla
    ///       navmesh, and the DMP→ESM pipeline emits NAVM records as overrides of master,
    ///       never as new. Omitting NAVI is therefore safe under current scope. Full NAVI
    ///       support would require reverse-engineering NVMI/NVCI binary layout (undocumented)
    ///       and recovering potentially-uninitialized engine state from the DMP. Revisit only
    ///       if the converter ever adds new NAVM records that need to appear in the navmesh
    ///       info map.
    /// </summary>
    public static RecordEncoderRegistry CreateDefault()
    {
        var registry = new RecordEncoderRegistry();

        registry.RegisterAll(
            // Misc
            new GmstEncoder(),
            new GlobEncoder(),
            new FlstEncoder(),
            new MuscEncoder(),
            new ChalEncoder(),
            new SounEncoder(),
            new TxstEncoder(),
            new LtexEncoder(),
            new GrasEncoder(),
            new TreeEncoder(),
            new ImgsEncoder(),
            new ImadEncoder(),
            new AlocEncoder(),
            new MicnEncoder(),
            new IpctEncoder(),
            new IngrEncoder(),
            new CcrdEncoder(),
            new CmnyEncoder(),
            new CdckEncoder(),
            new RcctEncoder(),
            // SCPT MUST be registered before any record type that carries a SCRI subrecord
            // (NPC_, CREA, QUST, ACTI, etc.). EsmAssembler emits GRUPs in registration order,
            // and the FNV engine resolves SCRI inline during load — forward references to a
            // SCPT GRUP that hasn't been read yet log "MASTERFILE: Unable to find script (X)
            // on owner object (Y)" and null the script binding. Vanilla FalloutNV.esm has
            // SCPT at file position 13 (before NPC_ at 36); we must mirror that ordering.
            new ScptEncoder(),
            // Item
            new WeapEncoder(),
            new ArmoEncoder(),
            new ArmaEncoder(),
            new AmmoEncoder(),
            new AlchEncoder(),
            new BookEncoder(),
            new MiscEncoder(),
            new KeymEncoder(),
            new ContEncoder(),
            new NoteEncoder(),
            new CobjEncoder(),
            new RcpeEncoder(),
            new ImodEncoder(),
            // Magic
            new SpelEncoder(),
            new EnchEncoder(),
            new MgefEncoder(),
            new PerkEncoder(),
            new ExplEncoder(),
            new ProjEncoder(),
            new AvifEncoder(),
            // Character / AI
            // PACK before NPC_ so the NPC's PKID list resolves against (master ∪ emitted) PACK
            // FormIDs at file-load time, matching the same inline-resolution constraint as SCRI.
            new PackEncoder(),
            new NpcEncoder(),
            new CreaEncoder(),
            new RaceEncoder(),
            new ClasEncoder(),
            new EyesEncoder(),
            new HairEncoder(),
            new BptdEncoder(),
            new HdptEncoder(),
            new FactEncoder(),
            new RepuEncoder(),
            new VtypEncoder(),
            new CstyEncoder(),
            // Quest / Dialogue / Message — QUST carries SCRI but its target SCPT is already
            // emitted above. DIAL/INFO carry result-script SCDA bytecode (no forward FormID
            // refs into SCPT GRUP), so file position is unconstrained.
            new QustEncoder(),
            new DialEncoder(),
            new InfoEncoder(),
            new MesgEncoder(),
            // World / Cell / Placed
            new WrldEncoder(),
            new CellEncoder(),
            new RefrEncoder(),
            new AchrEncoder(),
            new AcreEncoder(),
            new RegnEncoder(),
            new StatEncoder(),
            new ScolEncoder(),
            new DoorEncoder(),
            new LighEncoder(),
            new ActiEncoder(),
            new FurnEncoder(),
            new TermEncoder(),
            new WatrEncoder(),
            // PWAT after WATR: its DNAM names a parent WATR, the same inline-resolution
            // constraint the SCPT comment above describes. It previously sat in the Misc
            // block far ahead of WATR, which was harmless only because it never emitted.
            new PwatEncoder(),
            new WthrEncoder(),
            // CLMT has a typed model (ClimateRecord) populated by the ESM carve path; it was
            // parsed on every load and never written. Yielded after WTHR so its WLST weather
            // references resolve against already-emitted new WTHR FormIDs.
            new ClmtEncoder(),
            new LgtmEncoder(),
            new IdleEncoder(),
            // Generic-only types: decoded on every DMP load by RuntimeGenericReader into
            // RecordCollection.GenericRecords, previously dropped at the writer boundary because
            // no encoder existed. Each emits from GenericEsmRecord exactly like FLOR.
            //
            // Registered LAST of the reference-bearing types because every subrecord they emit
            // is an inline-resolved forward reference under the same constraint the SCPT comment
            // above describes: FLOR.SCRI and TACT.SCRI need SCPT, MSTT/TACT/ASPC/ADDN.SNAM need
            // SOUN, TACT.VNAM needs VTYP, ASPC.RDAT needs REGN, and ANIO.DATA needs IDLE. Their
            // first home was the Misc block at the top of this list, which put all five of those
            // edges backwards.
            new FlorEncoder(),
            new MsttEncoder(),
            new AnioEncoder(),
            new TactEncoder(),
            new AspcEncoder(),
            new AddnEncoder(),
            new DebrEncoder(),
            new EczEncoder(),
            new CpthEncoder(),
            new LsctEncoder());

        // LvliEncoder handles all three leveled-list signatures (LVLI/LVLN/LVLC). It declares
        // itself as "LVLI"; register it explicitly under the other two so override-path lookups
        // resolve to the same instance.
        var lvli = new LvliEncoder();
        registry.Register(lvli);
        registry.Register("LVLN", lvli);
        registry.Register("LVLC", lvli);

        // SurvivalStageEncoder declares "RADS" as its RecordType; register it explicitly under
        // the other three survival signatures so override-path lookups resolve.
        var survival = new SurvivalStageEncoder();
        registry.Register(survival);
        registry.Register("DEHY", survival);
        registry.Register("HUNG", survival);
        registry.Register("SLPD", survival);

        return registry;
    }

    /// <summary>
    ///     Returns true if the record type is a placed-reference type that lives inside a
    ///     parent CELL's child GRUP. These types are excluded from top-level GRUP emission and
    ///     are routed through the cell-children pipeline instead.
    /// </summary>
    public static bool IsCellChildRecordType(string recordType)
    {
        return recordType is "REFR" or "ACHR" or "ACRE";
    }

    /// <summary>
    ///     Returns true if the record type is a CELL — it's also routed outside the top-level
    ///     emission loop (it appears inside the cell hierarchy with its child GRUP).
    /// </summary>
    public static bool IsCellRecordType(string recordType)
    {
        return recordType == "CELL";
    }
}
