using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.SyntheticStructFactory;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic;

/// <summary>
///     Synthetic offset reader tests for the WEAP path of
///     <see cref="RuntimeItemReader" /> (FormType 0x28, TESObjectWEAP).
///     Pins the pointer offsets the production reader looks up: the ammo pointer
///     at runtime +184 (= PDB +168 + build shift +16) and the pickup-sound
///     pointer at runtime +252 (= PDB +236 + build shift +16). Phase 1B.11
///     anchors validated both at 100% across all DMP families.
/// </summary>
public sealed class WeapOffsetReaderTests
{
    private const byte WeapFormType = 0x28;
    private const byte AmmoFormType = 0x29;
    private const byte SoundFormType = 0x0D;

    private const uint WeapVa = 0x40100000;
    private const uint AmmoVa = 0x40200000;
    private const uint PickupSoundVa = 0x40300000;

    [Fact]
    public void ReadRuntimeWeapon_ResolvesAmmoPointerToFormId()
    {
        const uint weapFormId = 0x000F6634;
        const uint ammoFormId = 0x0008B6D0;
        var buffer = BuildWeap(weapFormId, ammoPtr: AmmoVa);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, WeapVa)
            .WithPointerTarget(AmmoVa, BuildTesForm(AmmoFormType, ammoFormId));
        var reader = new RuntimeItemReader(fixture.BuildContext());

        var weap = reader.ReadRuntimeWeapon(
            fixture.MakeEntry(weapFormId, WeapFormType, WeapVa));

        Assert.NotNull(weap);
        Assert.Equal(weapFormId, weap.FormId);
        Assert.Equal(ammoFormId, weap.AmmoFormId);
    }

    [Fact]
    public void ReadRuntimeWeapon_ResolvesPickupSoundPointerToFormId()
    {
        const uint weapFormId = 0x000F6635;
        const uint pickupSoundFormId = 0x0001F222;
        var buffer = BuildWeap(weapFormId, pickupSoundPtr: PickupSoundVa);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, WeapVa)
            .WithPointerTarget(PickupSoundVa, BuildTesForm(SoundFormType, pickupSoundFormId));
        var reader = new RuntimeItemReader(fixture.BuildContext());

        var weap = reader.ReadRuntimeWeapon(
            fixture.MakeEntry(weapFormId, WeapFormType, WeapVa));

        Assert.NotNull(weap);
        Assert.Equal(pickupSoundFormId, weap.PickupSoundFormId);
    }

    [Fact]
    public void ReadRuntimeWeapon_NullAmmoPointer_YieldsNullAmmoFormId()
    {
        const uint weapFormId = 0x000F6636;
        var buffer = BuildWeap(weapFormId, ammoPtr: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, WeapVa);
        var reader = new RuntimeItemReader(fixture.BuildContext());

        var weap = reader.ReadRuntimeWeapon(
            fixture.MakeEntry(weapFormId, WeapFormType, WeapVa));

        Assert.NotNull(weap);
        Assert.Null(weap.AmmoFormId);
    }

    [Fact]
    public void ReadRuntimeWeapon_FormIdMismatch_ReturnsNull()
    {
        const uint bufferFormId = 0x000F66AA;
        const uint entryFormId = 0x000F6601;
        var buffer = BuildWeap(bufferFormId);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, WeapVa);
        var reader = new RuntimeItemReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeWeapon(
            fixture.MakeEntry(entryFormId, WeapFormType, WeapVa)));
    }

    [Fact]
    public void ReadRuntimeWeapon_WrongFormType_ReturnsNull()
    {
        const uint formId = 0x000F6637;
        var buffer = BuildWeap(formId);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, WeapVa);
        var reader = new RuntimeItemReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeWeapon(
            fixture.MakeEntry(formId, formType: 0x19 /* BOOK, not WEAP */, WeapVa)));
    }

    /// <summary>
    ///     Builds a minimal 24-byte TESForm header buffer suitable as a pointer
    ///     target. FollowPointerToFormId reads exactly 24 bytes to validate
    ///     FormType + FormID.
    /// </summary>
    private static byte[] BuildTesForm(byte formType, uint formId)
    {
        var buf = new byte[24];
        WriteFormHeader(buf, 0, formType, formId);
        return buf;
    }
}
