using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Single source of truth for which ESM record types the semantic parser produces typed records for.
///     <para>
///         Drives the "Other (not parsed)" / "Other (not reconstructed)" display categorization: any
///         scanned record type whose code is NOT in <see cref="Codes" /> is reported as unparsed
///         (see <c>RecordParser</c>'s <c>UnparsedTypeCounts</c> computation).
///     </para>
///     <para>
///         Each entry pairs a 4-char record signature with the <see cref="RecordCollection" /> property its
///         parsed records land in (via <c>nameof</c>, so a renamed property breaks the build). Completeness is
///         enforced by <c>EsmParsedRecordTypesTests</c>: every <c>List&lt;*Record&gt;</c> collection on
///         <see cref="RecordCollection" /> must be claimed by at least one entry here, so adding a new parser
///         without registering its type fails CI instead of silently mislabeling the type as unparsed.
///     </para>
/// </summary>
public static class EsmParsedRecordTypes
{
    /// <summary>
    ///     A parsed record type: its 4-char signature and the <see cref="RecordCollection" /> property its
    ///     records land in. <paramref name="Collection" /> is <c>null</c> for the file header and for records
    ///     nested inside other structures (placed refs live inside cells, terrain inside cells) that have no
    ///     dedicated top-level list.
    /// </summary>
    public readonly record struct Entry(string Code, string? Collection);

    /// <summary>The authoritative table of parsed record types.</summary>
    public static readonly IReadOnlyList<Entry> All =
    [
        // === Characters ===
        new("NPC_", nameof(RecordCollection.Npcs)),
        new("CREA", nameof(RecordCollection.Creatures)),
        new("RACE", nameof(RecordCollection.Races)),
        new("FACT", nameof(RecordCollection.Factions)),
        new("ECZN", nameof(RecordCollection.EncounterZones)),

        // === Quests / dialogue / text ===
        new("QUST", nameof(RecordCollection.Quests)),
        new("DIAL", nameof(RecordCollection.DialogTopics)),
        new("INFO", nameof(RecordCollection.Dialogues)),
        new("NOTE", nameof(RecordCollection.Notes)),
        new("BOOK", nameof(RecordCollection.Books)),
        new("TERM", nameof(RecordCollection.Terminals)),
        new("SCPT", nameof(RecordCollection.Scripts)),

        // === Items ===
        new("WEAP", nameof(RecordCollection.Weapons)),
        new("ARMO", nameof(RecordCollection.Armor)),
        new("AMMO", nameof(RecordCollection.Ammo)),
        new("ALCH", nameof(RecordCollection.Consumables)),
        new("MISC", nameof(RecordCollection.MiscItems)),
        new("KEYM", nameof(RecordCollection.Keys)),
        new("CONT", nameof(RecordCollection.Containers)),
        new("IMOD", nameof(RecordCollection.WeaponMods)),
        new("RCPE", nameof(RecordCollection.Recipes)),
        new("RCCT", nameof(RecordCollection.RecipeCategories)),
        new("COBJ", nameof(RecordCollection.ConstructibleObjects)),
        new("INGR", nameof(RecordCollection.Ingredients)),
        new("LVLI", nameof(RecordCollection.LeveledLists)),
        new("LVLN", nameof(RecordCollection.LeveledLists)),
        new("LVLC", nameof(RecordCollection.LeveledLists)),

        // === Abilities / effects ===
        new("PERK", nameof(RecordCollection.Perks)),
        new("SPEL", nameof(RecordCollection.Spells)),
        new("ENCH", nameof(RecordCollection.Enchantments)),
        new("MGEF", nameof(RecordCollection.BaseEffects)),

        // === World ===
        new("CELL", nameof(RecordCollection.Cells)),
        new("WRLD", nameof(RecordCollection.Worldspaces)),
        new("PGRE", nameof(RecordCollection.PlacedGrenades)),
        new("NAVM", nameof(RecordCollection.NavMeshes)),
        new("NAVI", nameof(RecordCollection.NavMeshInfoMaps)),
        new("REGN", nameof(RecordCollection.Regions)),
        // Nested inside cells / terrain — no dedicated top-level list, but acknowledged as parsed.
        new("LAND", null),
        new("REFR", null),
        new("ACHR", null),
        new("ACRE", null),

        // === Game data ===
        new("GMST", nameof(RecordCollection.GameSettings)),
        new("GLOB", nameof(RecordCollection.Globals)),
        new("CHAL", nameof(RecordCollection.Challenges)),
        new("REPU", nameof(RecordCollection.Reputations)),
        new("PROJ", nameof(RecordCollection.Projectiles)),
        new("EXPL", nameof(RecordCollection.Explosions)),
        new("MESG", nameof(RecordCollection.Messages)),
        new("CLAS", nameof(RecordCollection.Classes)),
        new("FLST", nameof(RecordCollection.FormLists)),
        new("MICN", nameof(RecordCollection.MenuIcons)),
        new("CCRD", nameof(RecordCollection.CaravanCards)),
        new("CMNY", nameof(RecordCollection.CaravanMoney)),
        new("CDCK", nameof(RecordCollection.CaravanDecks)),
        new("RADS", nameof(RecordCollection.RadiationStages)),
        new("DEHY", nameof(RecordCollection.DehydrationStages)),
        new("HUNG", nameof(RecordCollection.HungerStages)),
        new("SLPD", nameof(RecordCollection.SleepDeprivationStages)),

        // === Character appearance ===
        new("EYES", nameof(RecordCollection.Eyes)),
        new("HAIR", nameof(RecordCollection.Hair)),
        new("HDPT", nameof(RecordCollection.HeadParts)),
        new("VTYP", nameof(RecordCollection.VoiceTypes)),

        // === World objects ===
        new("ACTI", nameof(RecordCollection.Activators)),
        new("LIGH", nameof(RecordCollection.Lights)),
        new("DOOR", nameof(RecordCollection.Doors)),
        new("STAT", nameof(RecordCollection.Statics)),
        new("SCOL", nameof(RecordCollection.StaticCollections)),
        new("FURN", nameof(RecordCollection.Furniture)),
        new("DEBR", nameof(RecordCollection.Debris)),
        // FLOR lands in GenericRecords — parsed ESM-side via the genericTypes loop and (when a DMP
        // is supplied) also via the runtime FLOR reader. No dedicated typed list.
        new("FLOR", nameof(RecordCollection.GenericRecords)),

        // === AI ===
        new("PACK", nameof(RecordCollection.Packages)),
        new("IDLE", nameof(RecordCollection.IdleAnimations)),
        new("CPTH", nameof(RecordCollection.CameraPaths)),

        // === Specialized / environment ===
        new("SOUN", nameof(RecordCollection.Sounds)),
        new("MUSC", nameof(RecordCollection.MusicTypes)),
        new("TXST", nameof(RecordCollection.TextureSets)),
        new("LTEX", nameof(RecordCollection.LandTextures)),
        new("GRAS", nameof(RecordCollection.Grasses)),
        new("ARMA", nameof(RecordCollection.ArmorAddons)),
        new("WATR", nameof(RecordCollection.Water)),
        new("BPTD", nameof(RecordCollection.BodyPartData)),
        new("AVIF", nameof(RecordCollection.ActorValueInfos)),
        new("CSTY", nameof(RecordCollection.CombatStyles)),
        new("LGTM", nameof(RecordCollection.LightingTemplates)),
        new("WTHR", nameof(RecordCollection.Weather)),
        new("CLMT", nameof(RecordCollection.Climate)),
        new("LSCT", nameof(RecordCollection.LoadScreenTypes)),
        new("IPCT", nameof(RecordCollection.ImpactData)),
        new("ALOC", nameof(RecordCollection.AudioLocationControllers)),

        // === File header (no list) ===
        new("TES4", null),

        // === Generic typed records (parsed into the shared GenericRecords list, keyed by RecordType) ===
        new("MSTT", nameof(RecordCollection.GenericRecords)),
        new("TACT", nameof(RecordCollection.GenericRecords)),
        new("CAMS", nameof(RecordCollection.GenericRecords)),
        new("ANIO", nameof(RecordCollection.GenericRecords)),
        new("IPDS", nameof(RecordCollection.GenericRecords)),
        new("EFSH", nameof(RecordCollection.GenericRecords)),
        new("RGDL", nameof(RecordCollection.GenericRecords)),
        new("LSCR", nameof(RecordCollection.GenericRecords)),
        new("ASPC", nameof(RecordCollection.GenericRecords)),
        new("MSET", nameof(RecordCollection.GenericRecords)),
        new("CHIP", nameof(RecordCollection.GenericRecords)),
        new("CSNO", nameof(RecordCollection.GenericRecords)),
        new("DOBJ", nameof(RecordCollection.GenericRecords)),
        new("ADDN", nameof(RecordCollection.GenericRecords)),
        new("TREE", nameof(RecordCollection.GenericRecords)),
        new("IMAD", nameof(RecordCollection.GenericRecords)),
        new("IDLM", nameof(RecordCollection.GenericRecords)),
        new("PWAT", nameof(RecordCollection.GenericRecords)),
        new("IMGS", nameof(RecordCollection.GenericRecords)),
        new("AMEF", nameof(RecordCollection.GenericRecords)),
    ];

    /// <summary>Set of parsed type codes (case-insensitive), derived from <see cref="All" />.</summary>
    public static readonly IReadOnlySet<string> Codes =
        All.Select(e => e.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
