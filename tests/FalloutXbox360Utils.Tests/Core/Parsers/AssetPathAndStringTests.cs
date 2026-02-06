using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Tests for EsmRecordFormat asset path helpers and string validation via ScanForRecords.
/// </summary>
public class AssetPathAndStringTests
{
    #region IsAssetPath - Valid Extensions

    [Theory]
    [InlineData("meshes\\weapons\\pistol.nif")]
    [InlineData("meshes/weapons/pistol.nif")]
    [InlineData("meshes\\anim.kf")]
    [InlineData("meshes\\anim.hkx")]
    [InlineData("textures\\wall.dds")]
    [InlineData("textures\\wall.ddx")]
    [InlineData("textures\\wall.tga")]
    [InlineData("textures\\wall.bmp")]
    [InlineData("sound\\fx\\shot.wav")]
    [InlineData("sound\\fx\\shot.mp3")]
    [InlineData("sound\\fx\\shot.ogg")]
    [InlineData("sound\\fx\\shot.lip")]
    [InlineData("scripts\\script.psc")]
    [InlineData("scripts\\script.pex")]
    [InlineData("meshes\\face.egm")]
    [InlineData("meshes\\face.egt")]
    [InlineData("meshes\\face.tri")]
    [InlineData("speedtree\\tree.spt")]
    [InlineData("data\\config.txt")]
    [InlineData("data\\config.xml")]
    [InlineData("data\\FalloutNV.esm")]
    [InlineData("data\\plugin.esp")]
    public void IsAssetPath_ValidExtension_ReturnsTrue(string path)
    {
        Assert.True(EsmRecordFormat.IsAssetPath(path));
    }

    #endregion

    #region IsAssetPath - Invalid Cases

    [Fact]
    public void IsAssetPath_NoSeparator_ReturnsFalse()
    {
        Assert.False(EsmRecordFormat.IsAssetPath("file.nif"));
    }

    [Fact]
    public void IsAssetPath_NoExtension_ReturnsFalse()
    {
        Assert.False(EsmRecordFormat.IsAssetPath("meshes\\weapons\\pistol"));
    }

    [Fact]
    public void IsAssetPath_UnknownExtension_ReturnsFalse()
    {
        Assert.False(EsmRecordFormat.IsAssetPath("meshes\\weapons\\pistol.xyz"));
    }

    [Fact]
    public void IsAssetPath_DotAtEnd_ReturnsFalse()
    {
        Assert.False(EsmRecordFormat.IsAssetPath("meshes\\weapons\\pistol."));
    }

    [Fact]
    public void IsAssetPath_NoContent_ReturnsFalse()
    {
        Assert.False(EsmRecordFormat.IsAssetPath(""));
    }

    [Fact]
    public void IsAssetPath_JustExtension_ReturnsFalse()
    {
        Assert.False(EsmRecordFormat.IsAssetPath(".nif"));
    }

    #endregion

    #region CleanAssetPath

    [Fact]
    public void CleanAssetPath_BackslashesNormalized()
    {
        var result = EsmRecordFormat.CleanAssetPath("meshes\\weapons\\pistol.nif");
        Assert.Equal("meshes/weapons/pistol.nif", result);
    }

    [Fact]
    public void CleanAssetPath_LeadingSlashesRemoved()
    {
        var result = EsmRecordFormat.CleanAssetPath("///meshes/pistol.nif");
        Assert.Equal("meshes/pistol.nif", result);
    }

    [Fact]
    public void CleanAssetPath_ConvertedToLowercase()
    {
        var result = EsmRecordFormat.CleanAssetPath("Meshes\\Weapons\\Pistol.NIF");
        Assert.Equal("meshes/weapons/pistol.nif", result);
    }

    [Fact]
    public void CleanAssetPath_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", EsmRecordFormat.CleanAssetPath(""));
    }

    [Fact]
    public void CleanAssetPath_Null_ReturnsNull()
    {
        Assert.Null(EsmRecordFormat.CleanAssetPath(null!));
    }

    [Fact]
    public void CleanAssetPath_AlreadyClean_ReturnsUnchanged()
    {
        var result = EsmRecordFormat.CleanAssetPath("meshes/pistol.nif");
        Assert.Equal("meshes/pistol.nif", result);
    }

    [Fact]
    public void CleanAssetPath_MixedSlashes_Normalized()
    {
        var result = EsmRecordFormat.CleanAssetPath("meshes/weapons\\pistol.nif");
        Assert.Equal("meshes/weapons/pistol.nif", result);
    }

    #endregion

    #region ScanForRecords - EDID Validation (String Tests via Public API)

    [Fact]
    public void ScanForRecords_ValidEdid_Accepted()
    {
        var data = BuildEdidSubrecord("TestWeapon");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Single(result.EditorIds);
        Assert.Equal("TestWeapon", result.EditorIds[0].Name);
    }

    [Fact]
    public void ScanForRecords_EdidTooShort_Rejected()
    {
        // Editor ID with single character (needs >= 2)
        var data = BuildEdidSubrecord("A");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Empty(result.EditorIds);
    }

    [Fact]
    public void ScanForRecords_EdidStartsWithDigit_Rejected()
    {
        var data = BuildEdidSubrecord("1Invalid");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Empty(result.EditorIds);
    }

    [Fact]
    public void ScanForRecords_EdidWithSpecialChars_Rejected()
    {
        var data = BuildEdidSubrecord("Test-Item");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Empty(result.EditorIds);
    }

    [Fact]
    public void ScanForRecords_EdidWithUnderscore_Accepted()
    {
        var data = BuildEdidSubrecord("Test_Item");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Single(result.EditorIds);
    }

    [Fact]
    public void ScanForRecords_EdidRepeatedPattern_Rejected()
    {
        // "katSkatSkatS" has pattern "katS" repeating 3 times
        var data = BuildEdidSubrecord("katSkatSkatS");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Empty(result.EditorIds);
    }

    [Fact]
    public void ScanForRecords_EdidLongValidName_Accepted()
    {
        var data = BuildEdidSubrecord("VDialogueDocMitchellTopic001");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Single(result.EditorIds);
    }

    #endregion

    #region ScanForRecords - FULL Text Validation

    [Fact]
    public void ScanForRecords_ValidFullText_Accepted()
    {
        var data = BuildTextSubrecord("FULL", "Hunting Rifle");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Single(result.FullNames);
        Assert.Equal("Hunting Rifle", result.FullNames[0].Text);
    }

    [Fact]
    public void ScanForRecords_FullTooShort_Rejected()
    {
        // Text with single character (needs >= 2)
        var data = BuildTextSubrecord("FULL", "A");
        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Empty(result.FullNames);
    }

    #endregion

    #region ScanForRecords - NAME Subrecords

    [Fact]
    public void ScanForRecords_ValidNameFormId_Accepted()
    {
        // NAME subrecord with 4-byte FormID
        var data = new byte[32];
        data[0] = (byte)'N';
        data[1] = (byte)'A';
        data[2] = (byte)'M';
        data[3] = (byte)'E';
        data[4] = 0x04; // Length = 4
        data[5] = 0x00;
        // FormID 0x00012345 in little-endian
        data[6] = 0x45;
        data[7] = 0x23;
        data[8] = 0x01;
        data[9] = 0x00;

        var result = EsmRecordFormat.ScanForRecords(data);
        Assert.Single(result.NameReferences);
        Assert.Equal(0x00012345u, result.NameReferences[0].BaseFormId);
    }

    #endregion

    #region Helpers

    private static byte[] BuildEdidSubrecord(string editorId)
    {
        var editorIdBytes = Encoding.ASCII.GetBytes(editorId + "\0");
        var data = new byte[Math.Max(32, 6 + editorIdBytes.Length + 10)];
        data[0] = (byte)'E';
        data[1] = (byte)'D';
        data[2] = (byte)'I';
        data[3] = (byte)'D';
        data[4] = (byte)(editorIdBytes.Length & 0xFF);
        data[5] = (byte)((editorIdBytes.Length >> 8) & 0xFF);
        Array.Copy(editorIdBytes, 0, data, 6, editorIdBytes.Length);
        return data;
    }

    private static byte[] BuildTextSubrecord(string signature, string text)
    {
        var textBytes = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[Math.Max(32, 6 + textBytes.Length + 10)];
        data[0] = (byte)signature[0];
        data[1] = (byte)signature[1];
        data[2] = (byte)signature[2];
        data[3] = (byte)signature[3];
        data[4] = (byte)(textBytes.Length & 0xFF);
        data[5] = (byte)((textBytes.Length >> 8) & 0xFF);
        Array.Copy(textBytes, 0, data, 6, textBytes.Length);
        return data;
    }

    #endregion
}
