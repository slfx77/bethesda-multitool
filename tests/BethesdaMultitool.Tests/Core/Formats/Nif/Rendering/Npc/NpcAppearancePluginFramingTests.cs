using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcAppearancePluginFramingTests
{
    private const uint NpcFormId = 0x00123456;
    private const uint RaceFormId = 0x00000907;

    [Theory]
    [InlineData(20, false)]
    [InlineData(20, true)]
    [InlineData(24, false)]
    [InlineData(24, true)]
    public void AppearanceIndex_UsesDetectedRecordFraming_AndPreservesEndianness(
        int headerSize,
        bool bigEndian)
    {
        var esm = BuildPlugin(headerSize, bigEndian);

        var detected = PluginFormat.Detect(esm);
        Assert.Equal(headerSize, detected.RecordHeaderSize);
        Assert.Equal(headerSize, detected.GroupHeaderSize);

        var records = EsmRecordParser.ScanAllRecords(esm, bigEndian);
        var record = Assert.Single(records);
        Assert.Equal("NPC_", record.Signature);
        Assert.Equal(NpcFormId, record.FormId);
        Assert.Equal(headerSize, record.RecordHeaderSize);

        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian);
        var npc = Assert.Contains(NpcFormId, index.Npcs);
        Assert.Equal("FramingNpc", npc.EditorId);
        Assert.Equal("Framing NPC", npc.FullName);
        Assert.Equal(RaceFormId, npc.RaceFormId);
        Assert.Equal([1.25f, -2.5f], Assert.IsType<float[]>(npc.FaceGenSymmetric));
    }

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public void RetailOblivion_AppearanceIndex_PopulatesNpcsAndFaceGen()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));

        var esm = File.ReadAllBytes(esmPath!);
        Assert.Equal(20, PluginFormat.Detect(esm).RecordHeaderSize);

        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);

        Assert.True(index.Npcs.Count > 1000, $"Expected >1000 Oblivion NPCs; got {index.Npcs.Count}.");
        Assert.True(index.Races.Count > 5, $"Expected Oblivion races; got {index.Races.Count}.");
        Assert.Contains(index.Npcs.Values, npc => npc.FaceGenSymmetric is { Length: > 0 });
        var reynald = Assert.Contains(0x000222A8u, index.Npcs);
        Assert.NotNull(reynald.InventoryItems);
        Assert.Equal(7, reynald.InventoryItems!.Count);
        Assert.Contains(0x0001C830u, index.Armors);
        Assert.Contains(0x0001C884u, index.Armors);
        Assert.Contains(0x0001C883u, index.Armors);
    }

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public void RetailOblivion_BeastTailMetadata_ReachesGenderedNpcAppearances()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));

        var esm = File.ReadAllBytes(esmPath!);
        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);
        var argonian = Assert.Contains(0x00023FE9u, index.Races);
        Assert.Equal(@"Characters\Argonian\Tail.NIF", argonian.MaleTailPath);
        Assert.Equal(@"Characters\Argonian\Tail.NIF", argonian.FemaleTailPath);
        Assert.Equal(@"Characters\Argonian\Male\tail.dds", argonian.MaleTailTexturePath);
        Assert.Equal(@"Characters\Argonian\Female\tail.dds", argonian.FemaleTailTexturePath);

        var khajiit = Assert.Contains(0x000223C7u, index.Races);
        Assert.Equal(@"Characters\Khajiit\KhajiitTail.NIF", khajiit.MaleTailPath);
        Assert.Equal(@"Characters\Khajiit\KhajiitTail.NIF", khajiit.FemaleTailPath);
        // Retail authors Female\tail.dds in both Khajiit gender sections; retain the master value.
        Assert.Equal(@"Characters\Khajiit\Female\tail.dds", khajiit.MaleTailTexturePath);
        Assert.Equal(@"Characters\Khajiit\Female\tail.dds", khajiit.FemaleTailTexturePath);

        var factory = new NpcAppearanceFactory(index);
        var ocheeva = factory.Build(
            0x000224EC,
            Assert.Contains(0x000224ECu, index.Npcs),
            "Oblivion.esm");
        Assert.True(ocheeva.IsFemale);
        Assert.Equal(@"meshes\Characters\Argonian\Tail.NIF", ocheeva.TailNifPath);
        Assert.Equal(@"textures\Characters\Argonian\Female\tail.dds", ocheeva.TailTexturePath);
        Assert.Equal(@"meshes\characters\_Male\skeletonbeast.nif", ocheeva.SkeletonNifPath);

        var mraajDar = factory.Build(
            0x00023E35,
            Assert.Contains(0x00023E35u, index.Npcs),
            "Oblivion.esm");
        Assert.False(mraajDar.IsFemale);
        Assert.Equal(@"meshes\Characters\Khajiit\KhajiitTail.NIF", mraajDar.TailNifPath);
        Assert.Equal(@"textures\Characters\Khajiit\Female\tail.dds", mraajDar.TailTexturePath);
        Assert.Equal(@"meshes\characters\_Male\skeletonbeast.nif", mraajDar.SkeletonNifPath);

        var meshesPath = Path.Combine(Path.GetDirectoryName(esmPath!)!, "Oblivion - Meshes.bsa");
        Assert.SkipWhen(!File.Exists(meshesPath), RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        using var meshArchives = MeshArchiveSet.Open(meshesPath, null);
        var skeleton = NpcCompositionPlanner.BuildSkeletonComposition(
            ocheeva,
            meshArchives,
            new NpcCompositionCaches(),
            new NpcCompositionOptions());
        Assert.NotNull(skeleton);
        Assert.Contains("Bip01 TailRoot", skeleton!.BodySkinningBones!);
        // tailIndex, not index: an outer scope in this method already binds `index`, and C#
        // refuses a nested declaration of the same name (CS0136).
        for (var tailIndex = 1; tailIndex <= 8; tailIndex++)
        {
            var boneName = $"Bip01 Tail{tailIndex:00}";
            Assert.Contains(boneName, skeleton.BodySkinningBones!);
            Assert.Contains(boneName, skeleton.AnimationOverrides!);
        }
    }

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public void RetailOblivion_Mazoga_UsesPlayerLevelTierAndCoherentOneHandPose()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));
        var meshesPath = Path.Combine(Path.GetDirectoryName(esmPath!)!, "Oblivion - Meshes.bsa");
        Assert.SkipWhen(!File.Exists(meshesPath), RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));

        var esm = File.ReadAllBytes(esmPath!);
        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);
        var factory = new NpcAppearanceFactory(index);
        var noContextAppearance = factory.Build(
            0x00085969,
            Assert.Contains(0x00085969u, index.Npcs),
            "Oblivion.esm");
        var noContextWeapon = Assert.IsType<WeaponVisual>(noContextAppearance.WeaponVisual);
        Assert.False(noContextWeapon.IsVisible);
        Assert.Equal(WeaponVisualSourceKind.OmittedLeveledContextRequired, noContextWeapon.SourceKind);
        Assert.Equal(0x0003ABC0u, noContextWeapon.LeveledListTrace?.ListFormId);

        var appearance = factory.Build(
            0x00085969,
            Assert.Contains(0x00085969u, index.Npcs),
            "Oblivion.esm",
            previewPlayerLevel: 10);

        Assert.Equal(BethesdaGame.Oblivion, index.Game);
        var shield = Assert.Single(appearance.EquippedItems!, item =>
            item.MeshPath.EndsWith(@"Armor\Iron\Shield.NIF", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0x2000u, shield.BipedFlags);
        Assert.Equal(EquipmentAttachmentMode.LeftWristRigid, shield.AttachmentMode);

        var weapon = Assert.IsType<WeaponVisual>(appearance.WeaponVisual);
        var authoredLongswords = Assert.Contains(0x0003ABC0u, index.LeveledItemRecords);
        Assert.Equal("LL0NPCWeaponLongswordLvl100", authoredLongswords.EditorId);
        Assert.Equal(0, authoredLongswords.ChanceNone);
        Assert.Equal(0x02, authoredLongswords.Flags);
        Assert.Equal(
            new ushort[] { 1, 2, 4, 6, 9, 12, 16, 20 },
            authoredLongswords.Entries.Select(static entry => entry.Level).ToArray());
        Assert.Equal(
            new uint[] { 0x00000C0C, 0x000229BA, 0x0002521F, 0x00035DD1,
                0x000229B3, 0x00035E5F, 0x00035E6E, 0x00035E76 },
            authoredLongswords.Entries.Select(static entry => entry.FormId).ToArray());
        Assert.Equal(0x000229B3u, weapon.WeaponFormId); // level 10 -> highest eligible tier 9 (Elven)
        Assert.Equal(WeaponVisualSourceKind.EsmBestWeapon, weapon.SourceKind);
        Assert.Equal(WeaponType.OneHandMelee, weapon.WeaponType);
        Assert.Equal(WeaponAttachmentMode.HolsterPose, weapon.AttachmentMode);
        Assert.Equal("onehandidle.kf", weapon.AttachmentPoseKfPath);
        Assert.Equal((ushort)10, weapon.LeveledListTrace?.PreviewPlayerLevel);
        Assert.Equal((ushort)9, weapon.LeveledListTrace?.SelectedEntryLevel);

        var levelTwentyAppearance = factory.Build(
            0x00085969,
            Assert.Contains(0x00085969u, index.Npcs),
            "Oblivion.esm",
            previewPlayerLevel: 20);
        Assert.Equal(0x00035E76u, levelTwentyAppearance.WeaponVisual?.WeaponFormId);
        Assert.Equal((ushort)20, levelTwentyAppearance.WeaponVisual?.LeveledListTrace?.SelectedEntryLevel);

        using var meshArchives = MeshArchiveSet.Open(meshesPath, null);
        var skeletonCaches = new NpcCompositionCaches();
        var defaultIdleSkeleton = NpcCompositionPlanner.BuildSkeletonComposition(
            noContextAppearance,
            meshArchives,
            skeletonCaches,
            new NpcCompositionOptions());
        var skeleton = NpcCompositionPlanner.BuildSkeletonComposition(
            appearance,
            meshArchives,
            skeletonCaches,
            new NpcCompositionOptions());
        Assert.NotNull(defaultIdleSkeleton);
        Assert.NotNull(skeleton);
        Assert.Equal(2, skeletonCaches.SkeletonPlans.Count);
        Assert.Null(defaultIdleSkeleton!.BodyPoseKfPath);
        Assert.Equal("onehandidle.kf", skeleton!.BodyPoseKfPath);
        Assert.Same(skeleton.BodySkinningBones, skeleton.WeaponAttachmentBones);
        Assert.NotEqual(
            Assert.Contains("Bip01 R Hand", defaultIdleSkeleton.BodySkinningBones!).Translation,
            Assert.Contains("Bip01 R Hand", skeleton.BodySkinningBones!).Translation);

        var pose = NpcWeaponAttachmentResolver.LoadWeaponHolsterPose(
            appearance.SkeletonNifPath,
            meshArchives,
            weapon.HolsterProfileKey!,
            false,
            weapon.AttachmentPoseKfPath);

        Assert.NotNull(pose);
        Assert.Contains("Weapon", pose!.WorldTransforms.Keys);
        Assert.Contains("Bip01 R Hand", pose.WorldTransforms.Keys);
        Assert.Contains("Bip01 L Forearm", pose.WorldTransforms.Keys);
        var bodyRightHand = Assert.Contains("Bip01 R Hand", skeleton.BodySkinningBones!);
        var attachmentRightHand = Assert.Contains("Bip01 R Hand", pose.WorldTransforms);
        Assert.InRange(
            Vector3.Distance(bodyRightHand.Translation, attachmentRightHand.Translation),
            0f,
            0.01f);
        Assert.True(NpcWeaponAttachmentResolver.TryResolveEquipmentAttachmentTransform(
            shield,
            pose.WorldTransforms,
            out var shieldNode,
            out var shieldTransform,
            out var shieldOmitReason), shieldOmitReason);
        Assert.Equal("Bip01 L Forearm", shieldNode);
        Assert.True(shieldTransform.Translation.LengthSquared() > 1f);

        var weaponTransform = NpcWeaponAttachmentResolver.ResolveWeaponHolsterAttachmentTransform(
            pose,
            "Weapon");
        Assert.True(weaponTransform.HasValue);
        Assert.True(weaponTransform.Value.Translation.LengthSquared() > 1f);
        Assert.InRange(
            Vector3.Distance(bodyRightHand.Translation, weaponTransform.Value.Translation),
            5f,
            8f);
    }

    [Fact]
    public void OblivionRaceLayout_MapsGenderedEarsAndSharedFacePartsWithoutIndexShift()
    {
        var esm = BuildOblivionRacePlugin();

        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);
        var race = Assert.Contains(RaceFormId, index.Races);

        Assert.Equal("HeadHuman.nif", race.MaleHeadModelPath);
        Assert.Equal("HeadHuman.nif", race.FemaleHeadModelPath);
        Assert.Equal("EarsMale.nif", race.MaleEarModelPath);
        Assert.Equal("EarsFemale.nif", race.FemaleEarModelPath);
        Assert.Equal("MaleHead.dds", race.MaleEarTexturePath);
        Assert.Equal("FemaleHead.dds", race.FemaleEarTexturePath);
        Assert.Equal("MouthHuman.nif", race.MaleMouthModelPath);
        Assert.Equal("MouthHuman.nif", race.FemaleMouthModelPath);
        Assert.Equal("TeethLower.nif", race.MaleLowerTeethModelPath);
        Assert.Equal("TeethUpper.nif", race.MaleUpperTeethModelPath);
        Assert.Equal("TongueHuman.nif", race.MaleTongueModelPath);
        Assert.Equal("EyeLeft.nif", race.MaleEyeLeftModelPath);
        Assert.Equal("EyeRight.nif", race.MaleEyeRightModelPath);
        Assert.Equal(race.MaleEyeLeftModelPath, race.FemaleEyeLeftModelPath);
        Assert.Equal(race.MaleEyeRightModelPath, race.FemaleEyeRightModelPath);
        Assert.Equal("MaleBody.dds", race.MaleBodyTexturePath);
        Assert.Equal("FemaleBody.dds", race.FemaleBodyTexturePath);
        Assert.Equal(@"characters\_male\upperbody.nif", race.MaleUpperBodyPath);
        Assert.Equal(@"characters\_male\femaleupperbody.nif", race.FemaleUpperBodyPath);
        Assert.Equal(@"characters\_male\lowerbody.nif", race.MaleLowerBodyPath);
        Assert.Equal(@"characters\_male\femalehand.nif", race.FemaleHandPath);
        Assert.Equal(@"characters\_male\foot.nif", race.MaleFootPath);
        Assert.Equal("MaleTail.nif", race.MaleTailPath);
        Assert.Equal("FemaleTail.nif", race.FemaleTailPath);
        Assert.Equal("MaleLeg.dds", race.MaleLowerBodyTexturePath);
        Assert.Equal("FemaleHand.dds", race.FemaleHandTexturePath);
        Assert.Equal("MaleFoot.dds", race.MaleFootTexturePath);
        Assert.Equal("MaleTail.dds", race.MaleTailTexturePath);
        Assert.Equal("FemaleTail.dds", race.FemaleTailTexturePath);
        Assert.Equal(50, Assert.IsType<float[]>(race.MaleFaceGenSymmetric).Length);
        Assert.Equal(50, Assert.IsType<float[]>(race.FemaleFaceGenSymmetric).Length);
    }

    [Fact]
    public void OblivionClothingRecord_IsIndexedAsRenderableEquipment()
    {
        var esm = BuildOblivionClothingPlugin();

        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);
        var clothing = Assert.Contains(0x0001C884u, index.Armors);

        Assert.Equal("MiddleShirt03", clothing.EditorId);
        Assert.Equal(0x0Cu, clothing.BipedFlags);
        Assert.Equal(@"Clothes\MiddleClass\03\M\Shirt.NIF", clothing.MaleBipedModelPath);
    }

    private static byte[] BuildPlugin(int headerSize, bool bigEndian)
    {
        var hedrData = new byte[12];
        WriteSingle(hedrData, 0, headerSize == 20 ? 1.0f : 1.34f, bigEndian);
        WriteUInt32(hedrData, 4, 1, bigEndian);
        WriteUInt32(hedrData, 8, 0x800, bigEndian);

        var tes4Payload = BuildSubrecord("HEDR", hedrData, bigEndian);
        var tes4 = BuildRecord("TES4", 0, tes4Payload, headerSize, bigEndian);

        var raceData = new byte[4];
        WriteUInt32(raceData, 0, RaceFormId, bigEndian);
        var faceGenData = new byte[8];
        WriteSingle(faceGenData, 0, 1.25f, bigEndian);
        WriteSingle(faceGenData, 4, -2.5f, bigEndian);

        var npcPayload = Concat(
            BuildSubrecord("EDID", Encoding.ASCII.GetBytes("FramingNpc\0"), bigEndian),
            BuildSubrecord("FULL", Encoding.ASCII.GetBytes("Framing NPC\0"), bigEndian),
            BuildSubrecord("RNAM", raceData, bigEndian),
            BuildSubrecord("FGGS", faceGenData, bigEndian));
        var npc = BuildRecord("NPC_", NpcFormId, npcPayload, headerSize, bigEndian);
        var group = BuildGroup("NPC_", npc, headerSize, bigEndian);

        return Concat(tes4, group);
    }

    private static byte[] BuildOblivionRacePlugin()
    {
        const int headerSize = 20;
        const bool bigEndian = false;
        var hedrData = new byte[12];
        WriteSingle(hedrData, 0, 1.0f, bigEndian);
        WriteUInt32(hedrData, 4, 1, bigEndian);
        WriteUInt32(hedrData, 8, 0x800, bigEndian);

        var tes4 = BuildRecord(
            "TES4",
            0,
            BuildSubrecord("HEDR", hedrData, bigEndian),
            headerSize,
            bigEndian);
        var faceCoefficients = new byte[200];
        WriteSingle(faceCoefficients, 0, 0.25f, bigEndian);

        var racePayload = Concat(
            BuildSubrecord("EDID", Encoding.ASCII.GetBytes("Imperial\0"), bigEndian),
            BuildSubrecord("NAM0", [], bigEndian),
            IndexedModel(0, "HeadHuman.nif"),
            BuildSubrecord("ICON", Encoding.ASCII.GetBytes("HeadHuman.dds\0"), bigEndian),
            IndexedModel(1, "EarsMale.nif"),
            BuildSubrecord("ICON", Encoding.ASCII.GetBytes("MaleHead.dds\0"), bigEndian),
            IndexedModel(2, "EarsFemale.nif"),
            BuildSubrecord("ICON", Encoding.ASCII.GetBytes("FemaleHead.dds\0"), bigEndian),
            IndexedModel(3, "MouthHuman.nif"),
            IndexedModel(4, "TeethLower.nif"),
            IndexedModel(5, "TeethUpper.nif"),
            IndexedModel(6, "TongueHuman.nif"),
            IndexedModel(7, "EyeLeft.nif"),
            IndexedModel(8, "EyeRight.nif"),
            BuildSubrecord("NAM1", [], bigEndian),
            BuildSubrecord("MNAM", [], bigEndian),
            BuildSubrecord("MODL", Encoding.ASCII.GetBytes("MaleTail.nif\0"), bigEndian),
            IndexedIcon(0, "MaleBody.dds"),
            IndexedIcon(1, "MaleLeg.dds"),
            IndexedIcon(2, "MaleHand.dds"),
            IndexedIcon(3, "MaleFoot.dds"),
            IndexedIcon(4, "MaleTail.dds"),
            BuildSubrecord("FNAM", [], bigEndian),
            BuildSubrecord("MODL", Encoding.ASCII.GetBytes("FemaleTail.nif\0"), bigEndian),
            IndexedIcon(0, "FemaleBody.dds"),
            IndexedIcon(1, "FemaleLeg.dds"),
            IndexedIcon(2, "FemaleHand.dds"),
            IndexedIcon(3, "FemaleFoot.dds"),
            IndexedIcon(4, "FemaleTail.dds"),
            BuildSubrecord("FGGS", faceCoefficients, bigEndian));
        var race = BuildRecord("RACE", RaceFormId, racePayload, headerSize, bigEndian);
        return Concat(tes4, BuildGroup("RACE", race, headerSize, bigEndian));

        byte[] IndexedModel(uint index, string path)
        {
            return Concat(
                UInt32Subrecord("INDX", index),
                BuildSubrecord("MODL", Encoding.ASCII.GetBytes(path + "\0"), bigEndian));
        }

        byte[] IndexedIcon(uint index, string path)
        {
            return Concat(
                UInt32Subrecord("INDX", index),
                BuildSubrecord("ICON", Encoding.ASCII.GetBytes(path + "\0"), bigEndian));
        }

        byte[] UInt32Subrecord(string signature, uint value)
        {
            var data = new byte[4];
            WriteUInt32(data, 0, value, bigEndian);
            return BuildSubrecord(signature, data, bigEndian);
        }
    }

    private static byte[] BuildOblivionClothingPlugin()
    {
        const int headerSize = 20;
        const bool bigEndian = false;
        var hedrData = new byte[12];
        WriteSingle(hedrData, 0, 1.0f, bigEndian);
        WriteUInt32(hedrData, 4, 1, bigEndian);
        WriteUInt32(hedrData, 8, 0x800, bigEndian);
        var tes4 = BuildRecord(
            "TES4",
            0,
            BuildSubrecord("HEDR", hedrData, bigEndian),
            headerSize,
            bigEndian);

        var bmdt = new byte[4];
        WriteUInt32(bmdt, 0, 0x0C, bigEndian);
        var clothingPayload = Concat(
            BuildSubrecord("EDID", Encoding.ASCII.GetBytes("MiddleShirt03\0"), bigEndian),
            BuildSubrecord("BMDT", bmdt, bigEndian),
            BuildSubrecord(
                "MODL",
                Encoding.ASCII.GetBytes(@"Clothes\MiddleClass\03\M\Shirt.NIF" + "\0"),
                bigEndian));
        var clothing = BuildRecord("CLOT", 0x0001C884, clothingPayload, headerSize, bigEndian);
        return Concat(tes4, BuildGroup("CLOT", clothing, headerSize, bigEndian));
    }

    private static byte[] BuildRecord(
        string signature,
        uint formId,
        byte[] payload,
        int headerSize,
        bool bigEndian)
    {
        var record = new byte[headerSize + payload.Length];
        WriteSignature(record, 0, signature, bigEndian);
        WriteUInt32(record, 4, (uint)payload.Length, bigEndian);
        WriteUInt32(record, 8, 0, bigEndian);
        WriteUInt32(record, 12, formId, bigEndian);
        WriteUInt32(record, 16, 0, bigEndian);
        if (headerSize == 24)
        {
            WriteUInt16(record, 20, 0, bigEndian);
            WriteUInt16(record, 22, 0, bigEndian);
        }

        payload.CopyTo(record, headerSize);
        return record;
    }

    private static byte[] BuildGroup(
        string label,
        byte[] payload,
        int headerSize,
        bool bigEndian)
    {
        var group = new byte[headerSize + payload.Length];
        WriteSignature(group, 0, "GRUP", bigEndian);
        WriteUInt32(group, 4, (uint)group.Length, bigEndian);
        WriteSignature(group, 8, label, bigEndian);
        WriteUInt32(group, 12, 0, bigEndian);
        WriteUInt32(group, 16, 0, bigEndian);
        if (headerSize == 24)
        {
            WriteUInt32(group, 20, 0, bigEndian);
        }

        payload.CopyTo(group, headerSize);
        return group;
    }

    private static byte[] BuildSubrecord(string signature, byte[] data, bool bigEndian)
    {
        var subrecord = new byte[6 + data.Length];
        WriteSignature(subrecord, 0, signature, bigEndian);
        WriteUInt16(subrecord, 4, checked((ushort)data.Length), bigEndian);
        data.CopyTo(subrecord, 6);
        return subrecord;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static void WriteSignature(byte[] target, int offset, string signature, bool bigEndian)
    {
        var bytes = Encoding.ASCII.GetBytes(signature);
        if (bigEndian)
        {
            Array.Reverse(bytes);
        }

        bytes.CopyTo(target, offset);
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset, 2), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);
        }
    }

    private static void WriteUInt32(byte[] target, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);
        }
    }

    private static void WriteSingle(byte[] target, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(target.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset, 4), value);
        }
    }
}
