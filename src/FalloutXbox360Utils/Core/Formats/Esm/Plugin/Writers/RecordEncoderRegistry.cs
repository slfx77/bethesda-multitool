using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

/// <summary>
///     Registry mapping ESM record-type signatures to encoders.
/// </summary>
public sealed class RecordEncoderRegistry
{
    private readonly Dictionary<string, IRecordEncoder> _byType = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> SupportedRecordTypes => _byType.Keys;

    public void Register(IRecordEncoder encoder)
    {
        _byType[encoder.RecordType] = encoder;
    }

    /// <summary>
    ///     Register an encoder under a specific record type, overriding its declared
    ///     <see cref="IRecordEncoder.RecordType" />. Used by encoders that handle multiple
    ///     signatures (e.g., <see cref="Encoders.LvliEncoder" /> handles LVLI/LVLN/LVLC).
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

    public bool TryGet(string recordType, out IRecordEncoder? encoder)
    {
        return _byType.TryGetValue(recordType, out encoder);
    }

    public IRecordEncoder? Get(string recordType)
    {
        return _byType.GetValueOrDefault(recordType);
    }

    /// <summary>
    ///     Builds a registry pre-populated with the v1 encoder set.
    ///     Each encoder emits only the subrecord(s) whose byte layout is fully captured by the
    ///     parsed model — everything else is retained from the source ESM by the merge engine.
    ///     This conservative strategy guarantees that v1 cannot corrupt unmapped fields.
    ///     Coverage:
    ///     GMST  (DATA, numeric only)            GLOB  (FLTV)
    ///     WEAP  (DATA)                          ARMO  (DATA)
    ///     AMMO  (DATA)                          ALCH  (DATA)
    ///     BOOK  (DATA)                          MISC  (DATA)
    ///     KEYM  (DATA)                          FACT  (DATA flags)
    ///     NPC_  (ACBS)
    ///     CONT is intentionally excluded from v1 overrides: <see cref="Models.Records.Item.ContainerRecord" />
    ///     historically did not carry the Weight field needed to reconstruct the 5-byte DATA
    ///     payload. v7 adds Weight to the model and a CONT new-record encoder.
    /// </summary>
    public static RecordEncoderRegistry CreateV1Default()
    {
        var registry = new RecordEncoderRegistry();
        registry.RegisterAll(
            new GmstEncoder(),
            new GlobEncoder(),
            new WeapEncoder(),
            new ArmoEncoder(),
            new AmmoEncoder(),
            new AlchEncoder(),
            new BookEncoder(),
            new MiscEncoder(),
            new KeymEncoder(),
            new FactEncoder(),
            new NpcEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry pre-populated with the v2 encoder set, which extends v1 with
    ///     placed-reference encoders (REFR/ACHR/ACRE) used by the cell-children override path.
    ///     These three are NOT emitted as top-level GRUP records — they live inside their
    ///     parent CELL's child GRUP hierarchy and are dispatched separately from the simple-
    ///     type override loop.
    /// </summary>
    public static RecordEncoderRegistry CreateV2Default()
    {
        var registry = CreateV1Default();
        registry.RegisterAll(
            new RefrEncoder(),
            new AchrEncoder(),
            new AcreEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v3 — extends v2 with the <see cref="Encoders.CellEncoder" />
    ///     used to emit synthetic CELL records for DMP cells that don't exist in the master.
    ///     Like REFR/ACHR/ACRE, CELL is not a top-level emission type — it's routed through
    ///     the cell-children pipeline.
    /// </summary>
    public static RecordEncoderRegistry CreateV3Default()
    {
        var registry = CreateV2Default();
        registry.Register(new CellEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v6 — extends v3 with the script and dialogue encoders
    ///     (SCPT, DIAL, INFO) plus quest and AI-package encoders (QUST, PACK). All five
    ///     are new-record-only types: their <see cref="IRecordEncoder.Encode" /> path
    ///     returns no subrecords, so override merging is a no-op (the master ESM retains
    ///     verbatim bytes). The <c>EncodeNew</c> static methods build full subrecord streams
    ///     for FormIDs not present in the master.
    /// </summary>
    public static RecordEncoderRegistry CreateV6Default()
    {
        var registry = CreateV3Default();
        registry.RegisterAll(
            new ScptEncoder(),
            new DialEncoder(),
            new InfoEncoder(),
            new QustEncoder(),
            new PackEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v7 — extends v6 with world-object new-record encoders
    ///     (ACTI, DOOR, LIGH, STAT, CONT, FURN, TERM). All seven are new-record-only types:
    ///     <see cref="IRecordEncoder.Encode" /> is a no-op (master ESM bytes retained verbatim)
    ///     and <see cref="Encoders.LighEncoder.EncodeNew" /> / sibling methods build the full
    ///     subrecord stream for FormIDs not present in the master.
    ///     TERM is a simplified path — embedded result-script bytecode inside menu items is
    ///     deferred to a future phase (warns when encountered).
    /// </summary>
    public static RecordEncoderRegistry CreateV7Default()
    {
        var registry = CreateV6Default();
        registry.RegisterAll(
            new ActiEncoder(),
            new DoorEncoder(),
            new LighEncoder(),
            new StatEncoder(),
            new ContEncoder(),
            new FurnEncoder(),
            new TermEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v11 — extends v7 with PROJ (projectile), EXPL (explosion),
    ///     and IMOD (weapon mod) new-record encoders. All three are new-record-only types
    ///     whose <see cref="IRecordEncoder.Encode" /> path is a no-op.
    /// </summary>
    public static RecordEncoderRegistry CreateV11Default()
    {
        var registry = CreateV7Default();
        registry.RegisterAll(
            new ProjEncoder(),
            new ExplEncoder(),
            new ImodEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v12 — extends v11 with ARMA (armor addon) and RCPE (recipe)
    ///     new-record encoders. v12 also enables WEAP VATS subrecord emission (no registry
    ///     change needed — the existing WeapEncoder now emits VATS when the model carries
    ///     <see cref="Models.VatsAttackData" />, replacing the v6-deferred warning).
    /// </summary>
    public static RecordEncoderRegistry CreateV12Default()
    {
        var registry = CreateV11Default();
        registry.RegisterAll(
            new ArmaEncoder(),
            new RcpeEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v14 — extends v12 with RCCT (recipe category) and COBJ
    ///     (constructible object) new-record encoders. v14 also enables several subrecord
    ///     emissions on existing encoders (no registry change needed):
    ///     - ARMA now emits MODT/MO2T/MO3T/MO4T texture hashes, ICON/MIC2 inventory icons,
    ///     and DNAM detection sound level when the model carries them.
    ///     - QUST now emits top-level CTDA + CIS1/CIS2 condition string parameters.
    /// </summary>
    public static RecordEncoderRegistry CreateV14Default()
    {
        var registry = CreateV12Default();
        registry.RegisterAll(
            new RcctEncoder(),
            new CobjEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v16 — extends v14 with 9 trivial/small new-record encoders:
    ///     EYES, HAIR, REPU, AVIF, MUSC, MESG, NOTE, FLST, and the LeveledList trio
    ///     (LVLI/LVLN/LVLC, sharing one encoder). v15 added subrecord emissions to existing
    ///     encoders (no registry change), so we skip v15 and jump to v16.
    /// </summary>
    public static RecordEncoderRegistry CreateV16Default()
    {
        var registry = CreateV14Default();
        registry.RegisterAll(
            new EyesEncoder(),
            new HairEncoder(),
            new RepuEncoder(),
            new AvifEncoder(),
            new MuscEncoder(),
            new MesgEncoder(),
            new NoteEncoder(),
            new FlstEncoder());

        // LvliEncoder handles all three leveled-list signatures (LVLI/LVLN/LVLC).
        // The encoder declares itself as "LVLI"; register it explicitly under the other
        // two so the override-path registry lookup finds it.
        var lvli = new LvliEncoder();
        registry.Register(lvli);
        registry.Register("LVLN", lvli);
        registry.Register("LVLC", lvli);
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v17 — extends v16 with 9 medium-complexity new-record encoders:
    ///     CREA (creature), CLAS (class), SOUN (sound), TXST (texture set), CHAL (challenge),
    ///     BPTD (body part data), ENCH (enchantment), SPEL (spell), PERK (perk).
    ///     PERK PRKE entry chains and CREA FaceGen are documented gaps — they warn during emission.
    /// </summary>
    public static RecordEncoderRegistry CreateV17Default()
    {
        var registry = CreateV16Default();
        registry.RegisterAll(
            new CreaEncoder(),
            new ClasEncoder(),
            new SounEncoder(),
            new TxstEncoder(),
            new LtexEncoder(),
            new ChalEncoder(),
            new BptdEncoder(),
            new EnchEncoder(),
            new SpelEncoder(),
            new PerkEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v18 — extends v17 with the three large new-record encoders:
    ///     MGEF (base effect, DATA 72B), WRLD (worldspace header — child cells flow via the
    ///     cell-children pipeline), RACE (the largest encoder by field count: gendered head/body
    ///     part hierarchies, FaceGen morphs, hair/eyes references, skill boosts).
    ///     Parser-deficient types (CSTY/LGTM/WTHR/WATR/NAVI) still pending — they emit nothing
    ///     until their parsers are fixed in a future phase.
    /// </summary>
    public static RecordEncoderRegistry CreateV18Default()
    {
        var registry = CreateV17Default();
        registry.RegisterAll(
            new MgefEncoder(),
            new WrldEncoder(),
            new RaceEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v18b — extends v18 with previously-blocked parser-deficient
    ///     types. CSTY/LGTM/WATR use a shared schema-dictionary serializer
    ///     (<see cref="Encoders.SchemaDictionarySerializer" />) to re-emit the dictionaries the
    ///     parser already populates. WTHR emits only its narrow typed fields (warns that
    ///     visual subrecords are missing). NAVI remains unencoded — the parser only captures
    ///     counts, not the vertex/triangle/portal arrays needed for a real navmesh.
    /// </summary>
    public static RecordEncoderRegistry CreateV18bDefault()
    {
        var registry = CreateV18Default();
        registry.RegisterAll(
            new CstyEncoder(),
            new LgtmEncoder(),
            new WatrEncoder(),
            new WthrEncoder());
        return registry;
    }

    /// <summary>
    ///     Builds a registry for v22 — extends v18b with SCOL (Static Collection) emission.
    ///     SCOL groups multiple instances of one or more STAT bases under a single record
    ///     so prototype-only collections can carry their (ONAM + DATA placement-list)* part
    ///     stream forward; the override path stays a no-op (master bytes retained verbatim).
    /// </summary>
    public static RecordEncoderRegistry CreateV22Default()
    {
        var registry = CreateV18bDefault();
        registry.Register(new ScolEncoder());
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
