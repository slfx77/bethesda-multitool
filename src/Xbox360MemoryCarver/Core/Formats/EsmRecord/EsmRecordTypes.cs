using System.Text;

namespace Xbox360MemoryCarver.Core.Formats.EsmRecord;

/// <summary>
///     All Fallout: New Vegas ESM record type signatures.
///     Based on xEdit (FNVEdit) wbDefinitionsFNV.pas wbFormTypeEnum.
/// </summary>
public static class EsmRecordTypes
{
    /// <summary>
    ///     Main record types in Fallout: New Vegas (from xEdit).
    ///     Records are stored as 4-byte ASCII signatures in little-endian order.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RecordTypeInfo> MainRecordTypes =
        new Dictionary<string, RecordTypeInfo>
        {
            // File header (special)
            ["TES4"] = new("File Header", RecordCategory.System),

            // Groups
            ["GRUP"] = new("Group", RecordCategory.System),

            // Core types (0x04-0x15)
            ["TXST"] = new("Texture Set", RecordCategory.Graphics, 0x04),
            ["MICN"] = new("Menu Icon", RecordCategory.Graphics, 0x05),
            ["GLOB"] = new("Global", RecordCategory.GameData, 0x06),
            ["CLAS"] = new("Class", RecordCategory.Actor, 0x07),
            ["FACT"] = new("Faction", RecordCategory.Actor, 0x08),
            ["HDPT"] = new("Head Part", RecordCategory.Actor, 0x09),
            ["HAIR"] = new("Hair", RecordCategory.Actor, 0x0A),
            ["EYES"] = new("Eyes", RecordCategory.Actor, 0x0B),
            ["RACE"] = new("Race", RecordCategory.Actor, 0x0C),
            ["SOUN"] = new("Sound", RecordCategory.Audio, 0x0D),
            ["ASPC"] = new("Acoustic Space", RecordCategory.Audio, 0x0E),
            ["SKIL"] = new("Skill", RecordCategory.GameData, 0x0F),
            ["MGEF"] = new("Base Effect", RecordCategory.Magic, 0x10),
            ["SCPT"] = new("Script", RecordCategory.Script, 0x11),
            ["LTEX"] = new("Landscape Texture", RecordCategory.World, 0x12),
            ["ENCH"] = new("Object Effect", RecordCategory.Magic, 0x13),
            ["SPEL"] = new("Actor Effect", RecordCategory.Magic, 0x14),
            ["ACTI"] = new("Activator", RecordCategory.Object, 0x15),

            // Objects (0x16-0x31)
            ["TACT"] = new("Talking Activator", RecordCategory.Object, 0x16),
            ["TERM"] = new("Terminal", RecordCategory.Object, 0x17),
            ["ARMO"] = new("Armor", RecordCategory.Item, 0x18),
            ["BOOK"] = new("Book", RecordCategory.Item, 0x19),
            ["CLOT"] = new("Clothing", RecordCategory.Item, 0x1A),
            ["CONT"] = new("Container", RecordCategory.Object, 0x1B),
            ["DOOR"] = new("Door", RecordCategory.Object, 0x1C),
            ["INGR"] = new("Ingredient", RecordCategory.Item, 0x1D),
            ["LIGH"] = new("Light", RecordCategory.Object, 0x1E),
            ["MISC"] = new("Misc Item", RecordCategory.Item, 0x1F),
            ["STAT"] = new("Static", RecordCategory.Object, 0x20),
            ["SCOL"] = new("Static Collection", RecordCategory.Object, 0x21),
            ["MSTT"] = new("Movable Static", RecordCategory.Object, 0x22),
            ["PWAT"] = new("Placeable Water", RecordCategory.Object, 0x23),
            ["GRAS"] = new("Grass", RecordCategory.Object, 0x24),
            ["TREE"] = new("Tree", RecordCategory.Object, 0x25),
            ["FLOR"] = new("Flora", RecordCategory.Object, 0x26),
            ["FURN"] = new("Furniture", RecordCategory.Object, 0x27),
            ["WEAP"] = new("Weapon", RecordCategory.Item, 0x28),
            ["AMMO"] = new("Ammo", RecordCategory.Item, 0x29),
            ["NPC_"] = new("NPC", RecordCategory.Actor, 0x2A),
            ["CREA"] = new("Creature", RecordCategory.Actor, 0x2B),
            ["LVLC"] = new("Leveled Creature", RecordCategory.Actor, 0x2C),
            ["LVLN"] = new("Leveled NPC", RecordCategory.Actor, 0x2D),
            ["KEYM"] = new("Key", RecordCategory.Item, 0x2E),
            ["ALCH"] = new("Ingestible", RecordCategory.Item, 0x2F),
            ["IDLM"] = new("Idle Marker", RecordCategory.Object, 0x30),
            ["NOTE"] = new("Note", RecordCategory.Item, 0x31),

            // World (0x32-0x44)
            ["COBJ"] = new("Constructible Object", RecordCategory.Object, 0x32),
            ["PROJ"] = new("Projectile", RecordCategory.Object, 0x33),
            ["LVLI"] = new("Leveled Item", RecordCategory.Item, 0x34),
            ["WTHR"] = new("Weather", RecordCategory.World, 0x35),
            ["CLMT"] = new("Climate", RecordCategory.World, 0x36),
            ["REGN"] = new("Region", RecordCategory.World, 0x37),
            ["NAVI"] = new("Navmesh Info Map", RecordCategory.Navigation, 0x38),
            ["CELL"] = new("Cell", RecordCategory.World, 0x39),
            ["REFR"] = new("Placed Object", RecordCategory.Reference, 0x3A),
            ["ACHR"] = new("Placed NPC", RecordCategory.Reference, 0x3B),
            ["ACRE"] = new("Placed Creature", RecordCategory.Reference, 0x3C),
            ["PMIS"] = new("Placed Missile", RecordCategory.Reference, 0x3D),
            ["PGRE"] = new("Placed Grenade", RecordCategory.Reference, 0x3E),
            ["PBEA"] = new("Placed Beam", RecordCategory.Reference, 0x3F),
            ["PFLA"] = new("Placed Flame", RecordCategory.Reference, 0x40),
            ["NAVM"] = new("Navmesh", RecordCategory.Navigation, 0x43),
            ["WRLD"] = new("World", RecordCategory.World, 0x44),

            // Quest/Dialog (0x45-0x49)
            ["LAND"] = new("Landscape", RecordCategory.World, 0x45),
            ["INFO"] = new("Dialog Response", RecordCategory.Dialog, 0x46),
            ["QUST"] = new("Quest", RecordCategory.Quest, 0x47),
            ["IDLE"] = new("Idle Animation", RecordCategory.Animation, 0x48),
            ["PACK"] = new("Package", RecordCategory.AI, 0x49),

            // Combat/Style (0x4A-0x52)
            ["CSTY"] = new("Combat Style", RecordCategory.Combat, 0x4A),
            ["LSCR"] = new("Load Screen", RecordCategory.UI, 0x4B),
            ["LVSP"] = new("Leveled Spell", RecordCategory.Magic, 0x4C),
            ["ANIO"] = new("Animated Object", RecordCategory.Animation, 0x4D),
            ["WATR"] = new("Water Type", RecordCategory.World, 0x4E),
            ["EFSH"] = new("Effect Shader", RecordCategory.Graphics, 0x4F),
            ["EXPL"] = new("Explosion", RecordCategory.Combat, 0x51),
            ["DEBR"] = new("Debris", RecordCategory.Combat, 0x52),

            // Image/Effects (0x53-0x66)
            ["IMGS"] = new("Image Space", RecordCategory.Graphics, 0x53),
            ["IMAD"] = new("Image Space Modifier", RecordCategory.Graphics, 0x54),
            ["FLST"] = new("FormID List", RecordCategory.GameData, 0x55),
            ["PERK"] = new("Perk", RecordCategory.GameData, 0x56),
            ["BPTD"] = new("Body Part Data", RecordCategory.Actor, 0x57),
            ["ADDN"] = new("Addon Node", RecordCategory.Graphics, 0x58),
            ["AVIF"] = new("Actor Value Info", RecordCategory.GameData, 0x59),
            ["RADS"] = new("Radiation Stage", RecordCategory.GameData, 0x5A),
            ["CAMS"] = new("Camera Shot", RecordCategory.Graphics, 0x5B),
            ["CPTH"] = new("Camera Path", RecordCategory.Graphics, 0x5C),
            ["VTYP"] = new("Voice Type", RecordCategory.Audio, 0x5D),
            ["IPCT"] = new("Impact", RecordCategory.Combat, 0x5E),
            ["IPDS"] = new("Impact Data Set", RecordCategory.Combat, 0x5F),
            ["ARMA"] = new("Armor Addon", RecordCategory.Item, 0x60),
            ["ECZN"] = new("Encounter Zone", RecordCategory.World, 0x61),
            ["MESG"] = new("Message", RecordCategory.UI, 0x62),
            ["RGDL"] = new("Ragdoll", RecordCategory.Actor, 0x63),
            ["DOBJ"] = new("Default Object Manager", RecordCategory.System, 0x64),
            ["LGTM"] = new("Lighting Template", RecordCategory.Graphics, 0x65),
            ["MUSC"] = new("Music Type", RecordCategory.Audio, 0x66),

            // FNV-specific (0x67-0x73)
            ["IMOD"] = new("Item Mod", RecordCategory.Item, 0x67),
            ["REPU"] = new("Reputation", RecordCategory.GameData, 0x68),
            ["PCBE"] = new("Placed Projectile Beam", RecordCategory.Reference, 0x69),
            ["RCPE"] = new("Recipe", RecordCategory.GameData, 0x6A),
            ["RCCT"] = new("Recipe Category", RecordCategory.GameData, 0x6B),
            ["CHIP"] = new("Casino Chip", RecordCategory.Item, 0x6C),
            ["CSNO"] = new("Casino", RecordCategory.GameData, 0x6D),
            ["LSCT"] = new("Load Screen Type", RecordCategory.UI, 0x6E),
            ["MSET"] = new("Media Set", RecordCategory.Audio, 0x6F),
            ["ALOC"] = new("Media Location Controller", RecordCategory.Audio, 0x70),
            ["CHAL"] = new("Challenge", RecordCategory.GameData, 0x71),
            ["AMEF"] = new("Ammo Effect", RecordCategory.Item, 0x72),
            ["CCRD"] = new("Caravan Card", RecordCategory.Item, 0x73),
            ["CMNY"] = new("Caravan Money", RecordCategory.Item, 0x74),
            ["CDCK"] = new("Caravan Deck", RecordCategory.Item, 0x75),
            ["DEHY"] = new("Dehydration Stage", RecordCategory.GameData, 0x76),
            ["HUNG"] = new("Hunger Stage", RecordCategory.GameData, 0x77),
            ["SLPD"] = new("Sleep Deprivation Stage", RecordCategory.GameData, 0x78),

            // Dialog
            ["DIAL"] = new("Dialog Topic", RecordCategory.Dialog)
        };

    /// <summary>
    ///     Common subrecord types found within main records.
    ///     Based on xEdit wbDefinitionsFNV.pas and wbDefinitionsCommon.pas.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, SubrecordTypeInfo> CommonSubrecordTypes =
        new Dictionary<string, SubrecordTypeInfo>
        {
            // Identification
            ["EDID"] = new("Editor ID", SubrecordDataType.NullTermString),
            ["FULL"] = new("Name", SubrecordDataType.LocalizedString),
            ["MODL"] = new("Model", SubrecordDataType.NullTermString),
            ["ICON"] = new("Large Icon", SubrecordDataType.NullTermString),
            ["MICO"] = new("Small Icon", SubrecordDataType.NullTermString),
            ["DESC"] = new("Description", SubrecordDataType.LocalizedString),

            // Scripts
            ["SCRI"] = new("Script", SubrecordDataType.FormId),
            ["SCHR"] = new("Script Header", SubrecordDataType.Struct, 20),
            ["SCDA"] = new("Compiled Script Data", SubrecordDataType.ByteArray),
            ["SCTX"] = new("Script Source", SubrecordDataType.String),
            ["SCRO"] = new("Script Reference", SubrecordDataType.FormId),
            ["SCRV"] = new("Script Local Variable", SubrecordDataType.UInt32),

            // Object bounds
            ["OBND"] = new("Object Bounds", SubrecordDataType.Struct, 12),

            // Destructible
            ["DEST"] = new("Destructible Header", SubrecordDataType.Struct, 8),
            ["DSTD"] = new("Destruction Stage Data", SubrecordDataType.Struct, 20),
            ["DSTF"] = new("Destruction Stage End", SubrecordDataType.Empty),
            ["DMDL"] = new("Destruction Model", SubrecordDataType.NullTermString),
            ["DMDT"] = new("Destruction Model Data", SubrecordDataType.ByteArray),

            // Items
            ["DATA"] = new("Data", SubrecordDataType.ByteArray),
            ["DNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["ANAM"] = new("Data", SubrecordDataType.ByteArray),
            ["BNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["CNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["ENAM"] = new("Data", SubrecordDataType.ByteArray),
            ["FNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["GNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["HNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["INAM"] = new("Data", SubrecordDataType.ByteArray),
            ["JNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["KNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["LNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["MNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["NNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["ONAM"] = new("Data", SubrecordDataType.ByteArray),
            ["PNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["QNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["RNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["SNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["TNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["UNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["VNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["WNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["XNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["YNAM"] = new("Data", SubrecordDataType.ByteArray),
            ["ZNAM"] = new("Data", SubrecordDataType.ByteArray),

            // Landscape
            ["VHGT"] = new("Vertex Heights", SubrecordDataType.ByteArray),
            ["VNML"] = new("Vertex Normals", SubrecordDataType.ByteArray),
            ["VCLR"] = new("Vertex Colors", SubrecordDataType.ByteArray),
            ["BTXT"] = new("Base Layer Header", SubrecordDataType.Struct, 8),
            ["ATXT"] = new("Alpha Layer Header", SubrecordDataType.Struct, 8),
            ["VTXT"] = new("Alpha Layer Data", SubrecordDataType.ByteArray),

            // Conditions
            ["CTDA"] = new("Condition", SubrecordDataType.Struct),
            ["CTDT"] = new("Condition (old)", SubrecordDataType.Struct),

            // References
            ["NAME"] = new("Base", SubrecordDataType.FormId),
            ["XEZN"] = new("Encounter Zone", SubrecordDataType.FormId),
            ["XLCM"] = new("Level Modifier", SubrecordDataType.Int32),
            ["XOWN"] = new("Owner", SubrecordDataType.FormId),
            ["XRNK"] = new("Faction Rank", SubrecordDataType.Int32),
            ["XSCL"] = new("Scale", SubrecordDataType.Float),
            ["XLOC"] = new("Lock Data", SubrecordDataType.Struct),
            ["XESP"] = new("Enable Parent", SubrecordDataType.Struct, 8),
            ["XTEL"] = new("Teleport Destination", SubrecordDataType.Struct),
            ["XMRK"] = new("Map Marker Data", SubrecordDataType.Struct),
            ["XCNT"] = new("Count", SubrecordDataType.Int32),
            ["XAPD"] = new("Activate Parents Flags", SubrecordDataType.UInt8),
            ["XAPR"] = new("Activate Parent Ref", SubrecordDataType.Struct, 8),

            // Cell/Worldspace
            ["XCLC"] = new("Cell Grid", SubrecordDataType.Struct, 12),
            ["XCLL"] = new("Cell Lighting", SubrecordDataType.Struct),
            ["XCLW"] = new("Water Height", SubrecordDataType.Float),
            ["XCLR"] = new("Regions", SubrecordDataType.FormIdArray),
            ["XCIM"] = new("Image Space", SubrecordDataType.FormId),
            ["XCCM"] = new("Climate", SubrecordDataType.FormId),
            ["XCWT"] = new("Water", SubrecordDataType.FormId),

            // AI Packages
            ["PKID"] = new("AI Package", SubrecordDataType.FormId),
            ["PKDT"] = new("Package Data", SubrecordDataType.Struct),
            ["PLDT"] = new("Package Location", SubrecordDataType.Struct),
            ["PTDT"] = new("Package Target", SubrecordDataType.Struct),

            // Effects
            ["EFID"] = new("Effect ID", SubrecordDataType.FormId),
            ["EFIT"] = new("Effect Data", SubrecordDataType.Struct, 20),

            // Quests
            ["QSTI"] = new("Quest Target", SubrecordDataType.FormId),
            ["QSTR"] = new("Quest Target", SubrecordDataType.FormId),
            ["INDX"] = new("Index", SubrecordDataType.Struct),

            // Model texture swap
            ["MODS"] = new("Model Texture Swap", SubrecordDataType.ByteArray),
            ["MODT"] = new("Model Texture Data", SubrecordDataType.ByteArray),
            ["MODD"] = new("FaceGen Model Flags", SubrecordDataType.UInt8),
            ["MO2T"] = new("Model 2 Texture Data", SubrecordDataType.ByteArray),
            ["MO2S"] = new("Model 2 Texture Swap", SubrecordDataType.ByteArray),
            ["MO3T"] = new("Model 3 Texture Data", SubrecordDataType.ByteArray),
            ["MO3S"] = new("Model 3 Texture Swap", SubrecordDataType.ByteArray),
            ["MO4T"] = new("Model 4 Texture Data", SubrecordDataType.ByteArray),
            ["MO4S"] = new("Model 4 Texture Swap", SubrecordDataType.ByteArray)
        };

    /// <summary>
    ///     Converts a 4-byte signature to its string representation.
    /// </summary>
    public static string SignatureToString(ReadOnlySpan<byte> sig)
    {
        if (sig.Length < 4)
        {
            return "????";
        }

        return Encoding.ASCII.GetString(sig[..4]);
    }
}

/// <summary>
///     Categories for main record types.
/// </summary>
public enum RecordCategory
{
    System,
    GameData,
    Actor,
    Object,
    Item,
    Magic,
    Script,
    World,
    Reference,
    Dialog,
    Quest,
    Combat,
    Animation,
    Navigation,
    AI,
    Audio,
    Graphics,
    UI
}

/// <summary>
///     Data types for subrecord content.
/// </summary>
public enum SubrecordDataType
{
    ByteArray,
    NullTermString,
    LocalizedString,
    String,
    FormId,
    FormIdArray,
    UInt8,
    UInt16,
    UInt32,
    Int32,
    Float,
    Struct,
    Empty
}

/// <summary>
///     Information about a main record type.
/// </summary>
public record RecordTypeInfo(string Name, RecordCategory Category, int? FormTypeId = null);

/// <summary>
///     Information about a subrecord type.
/// </summary>
public record SubrecordTypeInfo(string Name, SubrecordDataType DataType, int? FixedSize = null);
