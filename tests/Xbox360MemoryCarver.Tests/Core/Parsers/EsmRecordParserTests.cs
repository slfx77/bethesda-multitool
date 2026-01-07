using System.Text;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

public class EsmRecordParserTests
{
    [Fact]
    public void ScanForRecords_EmptyData_ReturnsEmptyResult()
    {
        // Arrange
        var data = Array.Empty<byte>();

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.EditorIds);
        Assert.Empty(result.GameSettings);
        Assert.Empty(result.ScriptSources);
        Assert.Empty(result.FormIdReferences);
    }

    [Fact]
    public void ScanForRecords_DataTooShort_ReturnsEmptyResult()
    {
        // Arrange - Less than 8 bytes
        var data = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.EditorIds);
    }

    [Fact]
    public void ScanForRecords_ValidEdid_ReturnsEditorId()
    {
        // Arrange - EDID record with "TestItem" as editor ID
        var editorId = "TestItem";
        var editorIdBytes = Encoding.ASCII.GetBytes(editorId + "\0");

        var data = new byte[50];
        data[0] = (byte)'E';
        data[1] = (byte)'D';
        data[2] = (byte)'I';
        data[3] = (byte)'D';
        data[4] = (byte)editorIdBytes.Length; // Length low
        data[5] = 0x00; // Length high
        Array.Copy(editorIdBytes, 0, data, 6, editorIdBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.EditorIds);
        Assert.Equal(editorId, result.EditorIds[0].Name);
        Assert.Equal(0, result.EditorIds[0].Offset);
    }

    [Fact]
    public void ScanForRecords_DuplicateEdids_DeduplicatesResults()
    {
        // Arrange - Two identical EDID records
        var editorId = "TestItem";
        var editorIdBytes = Encoding.ASCII.GetBytes(editorId + "\0");

        var data = new byte[100];
        var offset = 0;

        // First EDID
        data[offset++] = (byte)'E';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)'I';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)editorIdBytes.Length;
        data[offset++] = 0x00;
        Array.Copy(editorIdBytes, 0, data, offset, editorIdBytes.Length);
        offset += editorIdBytes.Length + 10;

        // Second identical EDID
        data[offset++] = (byte)'E';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)'I';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)editorIdBytes.Length;
        data[offset++] = 0x00;
        Array.Copy(editorIdBytes, 0, data, offset, editorIdBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.EditorIds); // Deduplicated
    }

    [Fact]
    public void ScanForRecords_InvalidEdid_SingleChar_Skipped()
    {
        // Arrange - EDID with only 1 character (invalid)
        var data = new byte[20];
        data[0] = (byte)'E';
        data[1] = (byte)'D';
        data[2] = (byte)'I';
        data[3] = (byte)'D';
        data[4] = 0x02; // Length = 2
        data[5] = 0x00;
        data[6] = (byte)'X';
        data[7] = 0x00;

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.EditorIds);
    }

    [Fact]
    public void ScanForRecords_EdidStartsWithNumber_Skipped()
    {
        // Arrange - EDID starting with number (invalid)
        var editorId = "123Test";
        var editorIdBytes = Encoding.ASCII.GetBytes(editorId + "\0");

        var data = new byte[30];
        data[0] = (byte)'E';
        data[1] = (byte)'D';
        data[2] = (byte)'I';
        data[3] = (byte)'D';
        data[4] = (byte)editorIdBytes.Length;
        data[5] = 0x00;
        Array.Copy(editorIdBytes, 0, data, 6, editorIdBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.EditorIds);
    }

    [Fact]
    public void ScanForRecords_ValidGmst_ReturnsGameSetting()
    {
        // Arrange - GMST record with "fActorStrengthMultiplier"
        var settingName = "fActorStrengthMultiplier";
        var settingBytes = Encoding.ASCII.GetBytes(settingName + "\0");

        var data = new byte[50];
        data[0] = (byte)'G';
        data[1] = (byte)'M';
        data[2] = (byte)'S';
        data[3] = (byte)'T';
        data[4] = (byte)settingBytes.Length;
        data[5] = 0x00;
        Array.Copy(settingBytes, 0, data, 6, settingBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.GameSettings);
        Assert.Equal(settingName, result.GameSettings[0].Name);
    }

    [Theory]
    [InlineData("fTestValue")] // float
    [InlineData("iTestValue")] // int
    [InlineData("sTestValue")] // string
    [InlineData("bTestValue")] // bool
    public void ScanForRecords_ValidGmstPrefixes_Accepted(string settingName)
    {
        // Arrange
        var settingBytes = Encoding.ASCII.GetBytes(settingName + "\0");

        var data = new byte[50];
        data[0] = (byte)'G';
        data[1] = (byte)'M';
        data[2] = (byte)'S';
        data[3] = (byte)'T';
        data[4] = (byte)settingBytes.Length;
        data[5] = 0x00;
        Array.Copy(settingBytes, 0, data, 6, settingBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.GameSettings);
    }

    [Fact]
    public void ScanForRecords_InvalidGmstPrefix_Skipped()
    {
        // Arrange - GMST with invalid prefix
        var settingName = "xInvalidPrefix";
        var settingBytes = Encoding.ASCII.GetBytes(settingName + "\0");

        var data = new byte[50];
        data[0] = (byte)'G';
        data[1] = (byte)'M';
        data[2] = (byte)'S';
        data[3] = (byte)'T';
        data[4] = (byte)settingBytes.Length;
        data[5] = 0x00;
        Array.Copy(settingBytes, 0, data, 6, settingBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.GameSettings);
    }

    [Fact]
    public void ScanForRecords_ValidSctx_ReturnsScriptSource()
    {
        // Arrange - SCTX with script source containing keywords
        var scriptText = "if GetStage MyQuest >= 10\n  Enable\nendif";
        var scriptBytes = Encoding.ASCII.GetBytes(scriptText);

        var data = new byte[100];
        data[0] = (byte)'S';
        data[1] = (byte)'C';
        data[2] = (byte)'T';
        data[3] = (byte)'X';
        data[4] = (byte)scriptBytes.Length;
        data[5] = 0x00;
        Array.Copy(scriptBytes, 0, data, 6, scriptBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.ScriptSources);
        Assert.Contains("GetStage", result.ScriptSources[0].Text);
    }

    [Fact]
    public void ScanForRecords_SctxWithoutKeywords_Skipped()
    {
        // Arrange - SCTX without any recognized keywords
        var text = "some random text without script keywords";
        var textBytes = Encoding.ASCII.GetBytes(text);

        var data = new byte[100];
        data[0] = (byte)'S';
        data[1] = (byte)'C';
        data[2] = (byte)'T';
        data[3] = (byte)'X';
        data[4] = (byte)textBytes.Length;
        data[5] = 0x00;
        Array.Copy(textBytes, 0, data, 6, textBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.ScriptSources);
    }

    [Fact]
    public void ScanForRecords_SctxTooShort_Skipped()
    {
        // Arrange - SCTX with length <= 10
        var data = new byte[20];
        data[0] = (byte)'S';
        data[1] = (byte)'C';
        data[2] = (byte)'T';
        data[3] = (byte)'X';
        data[4] = 0x05; // Length = 5
        data[5] = 0x00;
        data[6] = (byte)'t';
        data[7] = (byte)'e';
        data[8] = (byte)'s';
        data[9] = (byte)'t';
        data[10] = 0x00;

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.ScriptSources);
    }

    [Fact]
    public void ScanForRecords_ValidScro_ReturnsFormIdReference()
    {
        // Arrange - SCRO with FormID 0x0012AB34
        var data = new byte[20];
        data[0] = (byte)'S';
        data[1] = (byte)'C';
        data[2] = (byte)'R';
        data[3] = (byte)'O';
        data[4] = 0x04; // Length = 4
        data[5] = 0x00;
        data[6] = 0x34; // FormID little-endian: 0x0012AB34
        data[7] = 0xAB;
        data[8] = 0x12;
        data[9] = 0x00;

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.FormIdReferences);
        Assert.Equal(0x0012AB34u, result.FormIdReferences[0].FormId);
    }

    [Fact]
    public void ScanForRecords_ScroWithZeroFormId_Skipped()
    {
        // Arrange
        var data = new byte[20];
        data[0] = (byte)'S';
        data[1] = (byte)'C';
        data[2] = (byte)'R';
        data[3] = (byte)'O';
        data[4] = 0x04;
        data[5] = 0x00;
        data[6] = 0x00; // FormID = 0
        data[7] = 0x00;
        data[8] = 0x00;
        data[9] = 0x00;

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.FormIdReferences);
    }

    [Fact]
    public void ScanForRecords_ScroWithInvalidModIndex_Skipped()
    {
        // Arrange - FormID with mod index > 0x0F (invalid for base game)
        var data = new byte[20];
        data[0] = (byte)'S';
        data[1] = (byte)'C';
        data[2] = (byte)'R';
        data[3] = (byte)'O';
        data[4] = 0x04;
        data[5] = 0x00;
        data[6] = 0x34;
        data[7] = 0xAB;
        data[8] = 0x12;
        data[9] = 0xFF; // High byte = 0xFF (invalid mod index)

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.FormIdReferences);
    }

    [Fact]
    public void ScanForRecords_ScroWrongLength_Skipped()
    {
        // Arrange - SCRO with length != 4
        var data = new byte[20];
        data[0] = (byte)'S';
        data[1] = (byte)'C';
        data[2] = (byte)'R';
        data[3] = (byte)'O';
        data[4] = 0x05; // Length = 5 (should be 4)
        data[5] = 0x00;

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(result.FormIdReferences);
    }

    [Fact]
    public void ScanForRecords_DuplicateScro_Deduplicated()
    {
        // Arrange - Two SCRO with same FormID
        var data = new byte[40];
        var offset = 0;

        // First SCRO
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'R';
        data[offset++] = (byte)'O';
        data[offset++] = 0x04;
        data[offset++] = 0x00;
        data[offset++] = 0x34;
        data[offset++] = 0xAB;
        data[offset++] = 0x12;
        data[offset++] = 0x00;

        offset += 5; // Padding

        // Second SCRO with same FormID
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'R';
        data[offset++] = (byte)'O';
        data[offset++] = 0x04;
        data[offset++] = 0x00;
        data[offset++] = 0x34;
        data[offset++] = 0xAB;
        data[offset++] = 0x12;
        data[offset] = 0x00;

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.FormIdReferences);
    }

    [Fact]
    public void ScanForRecords_MixedRecordTypes_ReturnsAll()
    {
        // Arrange - Data with EDID, GMST, and SCRO
        var data = new byte[100];
        var offset = 0;

        // EDID
        var editorId = "TestWeapon\0";
        data[offset++] = (byte)'E';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)'I';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)editorId.Length;
        data[offset++] = 0x00;
        foreach (var c in editorId) data[offset++] = (byte)c;

        offset += 5; // Padding

        // GMST
        var setting = "fDamageMultiplier\0";
        data[offset++] = (byte)'G';
        data[offset++] = (byte)'M';
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'T';
        data[offset++] = (byte)setting.Length;
        data[offset++] = 0x00;
        foreach (var c in setting) data[offset++] = (byte)c;

        offset += 5; // Padding

        // SCRO
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'R';
        data[offset++] = (byte)'O';
        data[offset++] = 0x04;
        data[offset++] = 0x00;
        data[offset++] = 0x01;
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset] = 0x00;

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.EditorIds);
        Assert.Single(result.GameSettings);
        Assert.Single(result.FormIdReferences);
    }

    [Fact]
    public void CorrelateFormIdsToNames_EmptyData_ReturnsEmptyDictionary()
    {
        // Arrange
        var data = Array.Empty<byte>();

        // Act
        var result = EsmRecordFormat.CorrelateFormIdsToNames(data);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void CorrelateFormIdsToNames_WithExistingScan_UsesProvidedScan()
    {
        // Arrange
        var data = new byte[10];
        var existingScan = new EsmRecordScanResult
        {
            EditorIds = [new EdidRecord("TestItem", 50)]
        };

        // Act
        var result = EsmRecordFormat.CorrelateFormIdsToNames(data, existingScan);

        // Assert - Should use existing scan, not rescan
        Assert.Empty(result); // No FormID found at offset 50 - 200
    }

    [Theory]
    [InlineData("Enable")]
    [InlineData("Disable")]
    [InlineData("MoveTo")]
    [InlineData("SetStage")]
    [InlineData("GetStage")]
    [InlineData("if something")]
    [InlineData("endif")]
    [InlineData("someREFvalue")]
    public void ScanForRecords_SctxWithKeyword_Accepted(string scriptContent)
    {
        // Arrange - Pad to ensure length > 10
        var paddedContent = scriptContent.PadRight(15);
        var scriptBytes = Encoding.ASCII.GetBytes(paddedContent);

        var data = new byte[50];
        data[0] = (byte)'S';
        data[1] = (byte)'C';
        data[2] = (byte)'T';
        data[3] = (byte)'X';
        data[4] = (byte)scriptBytes.Length;
        data[5] = 0x00;
        Array.Copy(scriptBytes, 0, data, 6, scriptBytes.Length);

        // Act
        var result = EsmRecordFormat.ScanForRecords(data);

        // Assert
        Assert.Single(result.ScriptSources);
    }
}