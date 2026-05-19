using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

internal sealed record NewTopLevelRecordEncodingContext(
    IReadOnlySet<uint> MasterFormIds,
    IReadOnlySet<uint> EmittedNewStatFormIds,
    IReadOnlyDictionary<uint, uint> MasterNpcByRace);

internal static class NewTopLevelRecordEncoderDispatcher
{
    private delegate EncodedRecord Encoder(object model, NewTopLevelRecordEncodingContext context);

    private static readonly IReadOnlyDictionary<string, Encoder> Encoders =
        new Dictionary<string, Encoder>(StringComparer.Ordinal)
        {
            ["GMST"] = (model, _) => GmstEncoder.EncodeNew((GameSettingRecord)model),
            ["GLOB"] = (model, _) => GlobEncoder.EncodeNew((GlobalRecord)model),
            ["MISC"] = (model, _) => MiscEncoder.EncodeNew((MiscItemRecord)model),
            ["KEYM"] = (model, _) => KeymEncoder.EncodeNew((KeyRecord)model),
            ["ALCH"] = (model, _) => AlchEncoder.EncodeNew((ConsumableRecord)model),
            ["BOOK"] = (model, _) => BookEncoder.EncodeNew((BookRecord)model),
            ["AMMO"] = (model, _) => AmmoEncoder.EncodeNew((AmmoRecord)model),
            ["WEAP"] = (model, _) => WeapEncoder.EncodeNew((WeaponRecord)model),
            ["ARMO"] = (model, _) => ArmoEncoder.EncodeNew((ArmorRecord)model),
            ["FACT"] = (model, _) => FactEncoder.EncodeNew((FactionRecord)model),
            ["NPC_"] = (model, context) => NpcEncoder.EncodeNew(
                (NpcRecord)model,
                context.MasterFormIds,
                context.MasterNpcByRace),
            ["SCPT"] = (model, _) => ScptEncoder.EncodeNew((ScriptRecord)model),
            ["DIAL"] = (model, _) => DialEncoder.EncodeNew((DialogTopicRecord)model),
            ["INFO"] = (model, _) => InfoEncoder.EncodeNew((DialogueRecord)model),
            ["QUST"] = (model, _) => QustEncoder.EncodeNew((QuestRecord)model),
            ["PACK"] = (model, _) => PackEncoder.EncodeNew((PackageRecord)model),
            ["ACTI"] = (model, _) => ActiEncoder.EncodeNew((ActivatorRecord)model),
            ["DOOR"] = (model, _) => DoorEncoder.EncodeNew((DoorRecord)model),
            ["LIGH"] = (model, _) => LighEncoder.EncodeNew((LightRecord)model),
            ["STAT"] = (model, _) => StatEncoder.EncodeNew((StaticRecord)model),
            ["SCOL"] = (model, context) => ScolEncoder.EncodeNew(
                (StaticCollectionRecord)model,
                context.MasterFormIds,
                context.EmittedNewStatFormIds),
            ["CONT"] = (model, _) => ContEncoder.EncodeNew((ContainerRecord)model),
            ["FURN"] = (model, _) => FurnEncoder.EncodeNew((FurnitureRecord)model),
            ["TERM"] = (model, _) => TermEncoder.EncodeNew((TerminalRecord)model),
            ["PROJ"] = (model, _) => ProjEncoder.EncodeNew((ProjectileRecord)model),
            ["EXPL"] = (model, _) => ExplEncoder.EncodeNew((ExplosionRecord)model),
            ["IMOD"] = (model, _) => ImodEncoder.EncodeNew((WeaponModRecord)model),
            ["ARMA"] = (model, _) => ArmaEncoder.EncodeNew((ArmaRecord)model),
            ["RCPE"] = (model, _) => RcpeEncoder.EncodeNew((RecipeRecord)model),
            ["RCCT"] = (model, _) => RcctEncoder.EncodeNew((RecipeCategoryRecord)model),
            ["COBJ"] = (model, _) => CobjEncoder.EncodeNew((ConstructibleObjectRecord)model),
            ["EYES"] = (model, _) => EyesEncoder.EncodeNew((EyesRecord)model),
            ["HAIR"] = (model, _) => HairEncoder.EncodeNew((HairRecord)model),
            ["REPU"] = (model, _) => RepuEncoder.EncodeNew((ReputationRecord)model),
            ["AVIF"] = (model, _) => AvifEncoder.EncodeNew((ActorValueInfoRecord)model),
            ["MUSC"] = (model, _) => MuscEncoder.EncodeNew((MusicTypeRecord)model),
            ["MESG"] = (model, _) => MesgEncoder.EncodeNew((MessageRecord)model),
            ["NOTE"] = (model, _) => NoteEncoder.EncodeNew((NoteRecord)model),
            ["FLST"] = (model, _) => FlstEncoder.EncodeNew((FormListRecord)model),
            ["LVLI"] = (model, _) => LvliEncoder.EncodeNew((LeveledListRecord)model),
            ["LVLN"] = (model, _) => LvliEncoder.EncodeNew((LeveledListRecord)model),
            ["LVLC"] = (model, _) => LvliEncoder.EncodeNew((LeveledListRecord)model),
            ["CREA"] = (model, _) => CreaEncoder.EncodeNew((CreatureRecord)model),
            ["CLAS"] = (model, _) => ClasEncoder.EncodeNew((ClassRecord)model),
            ["SOUN"] = (model, _) => SounEncoder.EncodeNew((SoundRecord)model),
            ["TXST"] = (model, _) => TxstEncoder.EncodeNew((TextureSetRecord)model),
            ["LTEX"] = (model, _) => LtexEncoder.EncodeNew((LandscapeTextureRecord)model),
            ["CHAL"] = (model, _) => ChalEncoder.EncodeNew((ChallengeRecord)model),
            ["BPTD"] = (model, _) => BptdEncoder.EncodeNew((BodyPartDataRecord)model),
            ["ENCH"] = (model, _) => EnchEncoder.EncodeNew((EnchantmentRecord)model),
            ["SPEL"] = (model, _) => SpelEncoder.EncodeNew((SpellRecord)model),
            ["PERK"] = (model, _) => PerkEncoder.EncodeNew((PerkRecord)model),
            ["MGEF"] = (model, _) => MgefEncoder.EncodeNew((BaseEffectRecord)model),
            ["WRLD"] = (model, _) => WrldEncoder.EncodeNew((WorldspaceRecord)model),
            ["RACE"] = (model, _) => RaceEncoder.EncodeNew((RaceRecord)model),
            ["CSTY"] = (model, _) => CstyEncoder.EncodeNew((CombatStyleRecord)model),
            ["LGTM"] = (model, _) => LgtmEncoder.EncodeNew((LightingTemplateRecord)model),
            ["WATR"] = (model, _) => WatrEncoder.EncodeNew((WaterRecord)model),
            ["WTHR"] = (model, _) => WthrEncoder.EncodeNew((WeatherRecord)model),
            // Close encoder coverage for every type with a runtime reader.
            ["ECZN"] = (model, _) => EczEncoder.EncodeNew((EncounterZoneRecord)model),
            ["MICN"] = (model, _) => MicnEncoder.EncodeNew((MenuIconRecord)model),
            ["VTYP"] = (model, _) => VtypEncoder.EncodeNew((VoiceTypeRecord)model),
            ["CCRD"] = (model, _) => CcrdEncoder.EncodeNew((CaravanCardRecord)model),
            ["INGR"] = (model, _) => IngrEncoder.EncodeNew((IngredientRecord)model),
            ["LSCT"] = (model, _) => LsctEncoder.EncodeNew((LoadScreenTypeRecord)model),
            ["IDLE"] = (model, _) => IdleEncoder.EncodeNew((IdleAnimationRecord)model),
            ["IPCT"] = (model, _) => IpctEncoder.EncodeNew((ImpactDataRecord)model),
            ["HDPT"] = (model, _) => HdptEncoder.EncodeNew((HeadPartRecord)model),
            ["CPTH"] = (model, _) => CpthEncoder.EncodeNew((CameraPathRecord)model),
            ["ALOC"] = (model, _) => AlocEncoder.EncodeNew((AudioLocationControllerRecord)model),
            ["DEBR"] = (model, _) => DebrEncoder.EncodeNew((DebrisRecord)model),
            ["REGN"] = (model, _) => RegnEncoder.EncodeNew((RegionRecord)model),
            // RADS/DEHY/HUNG/SLPD share one encoder — the model type is identical across all four.
            ["RADS"] = (model, _) => SurvivalStageEncoder.EncodeNew((SurvivalStageRecord)model),
            ["DEHY"] = (model, _) => SurvivalStageEncoder.EncodeNew((SurvivalStageRecord)model),
            ["HUNG"] = (model, _) => SurvivalStageEncoder.EncodeNew((SurvivalStageRecord)model),
            ["SLPD"] = (model, _) => SurvivalStageEncoder.EncodeNew((SurvivalStageRecord)model)
        };

    public static IReadOnlyCollection<string> GetSupportedRecordTypes()
    {
        return Encoders.Keys.ToArray();
    }

    public static EncodedRecord? TryEncode(
        string recordType,
        object model,
        NewTopLevelRecordEncodingContext context)
    {
        return Encoders.TryGetValue(recordType, out var encoder)
            ? encoder(model, context)
            : null;
    }
}
