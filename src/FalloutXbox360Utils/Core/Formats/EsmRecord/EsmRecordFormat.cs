using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Converters.Esm.Schema;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     ESM Record format handler for detecting and parsing Bethesda ESM records
///     from memory dumps. Supports both PC (little-endian) and Xbox 360 (big-endian) formats.
/// </summary>
public sealed partial class EsmRecordFormat : FileFormatBase, IDumpScanner
{
    #region Properties

    public override string DisplayName => "ESM Records";
    public override string FormatId => "esmrecord";
    public override string Extension => ".esm";
    public override string OutputFolder => "esm_records";
    public override FileCategory Category => FileCategory.EsmData;
    public override int MinSize => 8;
    public override int MaxSize => 64 * 1024;
    public override bool ShowInFilterUI => false;
    public override IReadOnlyList<FormatSignature> Signatures { get; } = [];

    #endregion

    #region Constants and Static Fields

    private const uint SigEdid = 0x44494445;
    private const uint SigGmst = 0x54534D47;
    private const uint SigSctx = 0x58544353;
    private const uint SigScro = 0x4F524353;
    private const uint SigName = 0x454D414E;
    private const uint SigData = 0x41544144;
    private const uint SigAcbs = 0x53424341;
    private const uint SigNam1 = 0x314D414E;
    private const uint SigTrdt = 0x54445254;
    private const uint SigFull = 0x4C4C5546;
    private const uint SigDesc = 0x43534544;
    private const uint SigModl = 0x4C444F4D;
    private const uint SigIcon = 0x4E4F4349;
    private const uint SigMico = 0x4F43494D;
    private const uint SigScri = 0x49524353;
    private const uint SigEnam = 0x4D414E45;
    private const uint SigSnam = 0x4D414E53;
    private const uint SigQnam = 0x4D414E51;
    private const uint SigCtda = 0x41445443;
    private const uint SigVhgt = 0x54474856;
    private const uint SigTghv = 0x56484754;
    private const uint SigXclc = 0x434C4358;
    private const uint SigClcx = 0x58434C43;

    private static readonly string[] RuntimeRecordTypes =
    [
        // Placed objects (most common in runtime memory)
        "REFR", // Placed Object
        "ACHR", // Placed NPC
        "ACRE", // Placed Creature
        "PMIS", // Placed Missile
        "PGRE", // Placed Grenade

        // Actor definitions (loaded for nearby NPCs)
        "NPC_", // NPC
        "CREA", // Creature

        // Item definitions (player inventory, nearby loot)
        "WEAP", // Weapon
        "ARMO", // Armor
        "AMMO", // Ammo
        "ALCH", // Ingestible
        "MISC", // Misc Item
        "NOTE", // Note
        "KEYM", // Key
        "BOOK", // Book
        "CONT", // Container
        "DOOR", // Door
        "LIGH", // Light
        "STAT", // Static
        "TERM", // Terminal
        "FURN", // Furniture

        // World structure
        "CELL", // Cell
        "WRLD", // Worldspace
        "LAND", // Landscape
        "NAVM", // Navigation Mesh
        "NAVI", // Navigation Mesh Info Map
        "PGRD", // Pathgrid (legacy)

        // Quest/Dialog (active gameplay)
        "QUST", // Quest
        "DIAL", // Dialog Topic
        "INFO", // Dialog Response
        "PACK", // AI Package

        // Scripts
        "SCPT", // Script

        // Effects and sounds
        "MGEF", // Magic Effect
        "ENCH", // Enchantment
        "SPEL", // Actor Effect
        "SOUN", // Sound
        "MUSC", // Music Type

        // Factions and relationships
        "FACT", // Faction
        "RACE", // Race
        "CLAS", // Class

        // Leveled lists
        "LVLI", // Leveled Item
        "LVLN", // Leveled NPC
        "LVLC", // Leveled Creature

        // Game settings and globals
        "GMST", // Game Setting
        "GLOB", // Global Variable
        "WTHR", // Weather
        "CLMT", // Climate
        "REGN", // Region
        "IMAD", // Image Space Adapter
        "IMGS", // Image Space

        // FNV-specific gameplay records
        "IMOD", // Weapon Mod
        "RCPE", // Recipe
        "CHAL", // Challenge
        "REPU", // Reputation
        "PROJ", // Projectile
        "EXPL", // Explosion
        "MESG", // Message

        // Perks (already reconstructed, ensure scanned)
        "PERK" // Perk
    ];

    private static readonly HashSet<uint> RuntimeRecordMagicLE = BuildMagicSet(false);
    private static readonly HashSet<uint> RuntimeRecordMagicBE = BuildMagicSet(true);

    private static readonly Lazy<HashSet<string>> SchemaSignaturesLE =
        new(() => SubrecordSchemaRegistry.GetAllSignatures().ToHashSet());

    private static readonly Lazy<HashSet<string>> SchemaSignaturesBE = new(() => SubrecordSchemaRegistry
        .GetAllSignatures()
        .Select(SubrecordSchemaRegistry.GetReversedSignature)
        .ToHashSet());

    private static readonly string[] KnownFalsePositivePatterns =
    [
        "VGT_", // GPU Vertex Grouper/Tessellator debug (VGT_DEBUG_*)
        "SX_D", // Shader export debug
        "SQ_D", // Sequencer debug
        "DB_D", // Depth buffer debug
        "CB_D", // Color buffer debug
        "PA_D", // Primitive Assembly debug
        "PA_S", // Primitive Assembly state
        "SC_D", // Scan converter debug
        "SPI_", // Shader Processor Interpolator
        "TA_D", // Texture Addressing debug
        "TD_D", // Texture Data debug
        "TCP_", // Texture Cache Pipe
        "TCA_" // Texture Cache Address
    ];

    private static readonly Dictionary<byte, int> FullNameOffsetByFormType = new()
    {
        [0x08] = 44, // FACT - TESFaction
        [0x0A] = 44, // HAIR - TESHair (TESForm->TESFullName->TESModel->TESHair)
        [0x0B] = 44, // EYES - TESEyes (TESForm->TESFullName->TESModel->TESEyes)
        [0x0C] = 44, // RACE - TESRace
        [0x15] = 68, // ACTI - TESObjectACTI
        [0x18] = 68, // ARMO - TESObjectARMO
        [0x19] = 68, // BOOK - TESObjectBOOK
        [0x1B] = 80, // CONT - TESObjectCONT
        [0x1C] = 68, // DOOR - TESObjectDOOR
        [0x1F] = 68, // MISC - TESObjectMISC
        [0x28] = 68, // WEAP - TESObjectWEAP
        [0x29] = 68, // AMMO - TESAmmo
        [0x2A] = 228, // NPC_ - TESNPC
        [0x2E] = 68, // KEYM - TESKey
        [0x2F] = 68, // ALCH - AlchemyItem
        [0x33] = 68 // PROJ - BGSProjectile (TESBoundObject->TESFullName->TESModel->...)
    };

    private const int InfoPromptOffset = 44;

    #endregion

    #region Nested Types

    /// <summary>
    ///     Represents a candidate hash table found during dynamic scanning.
    /// </summary>
    private readonly record struct HashTableCandidate(
        long FileOffset,
        long VirtualAddress,
        uint HashSize,
        long BucketArrayVa,
        long BucketArrayFileOffset,
        int ValidationScore);

    /// <summary>
    ///     Describes a PE section from the module's in-memory PE headers.
    /// </summary>
    internal readonly record struct PeSectionInfo(
        int Index,
        string Name,
        uint VirtualAddress,
        uint VirtualSize,
        uint Characteristics);

    #endregion

    #region Public Methods

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        return null;
    }

    public object ScanDump(MemoryMappedViewAccessor accessor, long fileSize)
    {
        return ScanForRecordsMemoryMapped(accessor, fileSize);
    }

    /// <summary>
    ///     Export ESM records to files. Delegates to EsmRecordExporter.
    /// </summary>
    public static Task ExportRecordsAsync(
        EsmRecordScanResult records,
        Dictionary<uint, string> formIdMap,
        string outputDir,
        List<ReconstructedCell>? cells = null,
        List<ReconstructedWorldspace>? worldspaces = null)
    {
        return EsmRecordExporter.ExportRecordsAsync(records, formIdMap, outputDir, cells, worldspaces);
    }

    #endregion

    #region Helper Methods

    private static HashSet<uint> BuildMagicSet(bool bigEndian)
    {
        var set = new HashSet<uint>();
        foreach (var type in RuntimeRecordTypes)
        {
            var bytes = Encoding.ASCII.GetBytes(type);
            var leValue = BitConverter.ToUInt32(bytes, 0);
            set.Add(bigEndian ? BinaryPrimitives.ReverseEndianness(leValue) : leValue);
        }

        return set;
    }

    /// <summary>
    ///     Read a 16-bit unsigned integer in little-endian from a span.
    /// </summary>
    private static ushort ReadUInt16LE(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    #endregion
}
