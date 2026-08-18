using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Menus;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Menus;

/// <summary>
///     Covers the Xbox 360 <c>final_master_xml.dat</c> interface container and the mapping back
///     onto the subfoldered <c>menus\</c> layout the PC engine opens. Getting either wrong leaves
///     the converted build with no interface, which faults during boot rather than degrading.
/// </summary>
public class FinalMasterXmlTests
{
    /// <summary>Builds a synthetic container in the retail big-endian layout.</summary>
    private static byte[] BuildContainer(params (string Name, string Xml)[] entries)
    {
        using var ms = new MemoryStream();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(header, 100);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)entries.Length);
        ms.Write(header);

        foreach (var (name, xml) in entries)
        {
            // 128-byte fixed name field, NUL-terminated, 0xFD filler as the retail file uses.
            var field = new byte[128];
            Array.Fill(field, (byte)0xFD);
            var nameBytes = Encoding.ASCII.GetBytes(name);
            nameBytes.CopyTo(field, 0);
            field[nameBytes.Length] = 0;
            ms.Write(field);

            var body = Encoding.ASCII.GetBytes(xml);
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
            ms.Write(len);
            ms.Write(body);
        }

        var bytes = ms.ToArray();
        // Payload size is the file length minus the 12-byte header.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)(bytes.Length - 12));
        return bytes;
    }

    [Fact]
    public void Read_RecoversEveryEntryInOrder()
    {
        var data = BuildContainer(
            ("globals.xml", "<menu name=\"Globals\"/>"),
            ("hud_main_menu.xml", "<menu name=\"HUDMainMenu\"><rect/></menu>"),
            ("quantity_menu.xml", "<menu name=\"QuantityMenu\"/>"));

        var entries = FinalMasterXmlArchive.Read(data);

        Assert.Equal(3, entries.Count);
        Assert.Equal("globals.xml", entries[0].Name);
        Assert.Equal("hud_main_menu.xml", entries[1].Name);
        Assert.Equal("quantity_menu.xml", entries[2].Name);
        Assert.Equal("<menu name=\"HUDMainMenu\"><rect/></menu>", Encoding.ASCII.GetString(entries[1].Xml));
    }

    [Fact]
    public void IsContainer_AcceptsWellFormed_RejectsOtherPayloads()
    {
        Assert.True(FinalMasterXmlArchive.IsContainer(BuildContainer(("globals.xml", "<menu/>"))));
        Assert.False(FinalMasterXmlArchive.IsContainer("DDS "u8));
        Assert.False(FinalMasterXmlArchive.IsContainer([]));
    }

    [Fact]
    public void Read_TruncatedPayload_ThrowsRatherThanReturningPartialMenus()
    {
        var data = BuildContainer(("globals.xml", "<menu name=\"Globals\"/>"));
        var truncated = data[..(data.Length - 5)];

        Assert.Throws<InvalidDataException>(() => FinalMasterXmlArchive.Read(truncated));
    }

    [Theory]
    // Console names are flat; the PC engine hard-codes a subfolder for many of them.
    [InlineData("globals.xml", "menus\\globals.xml")]
    [InlineData("quantity_menu.xml", "menus\\quantity_menu.xml")]
    [InlineData("hud_main_menu.xml", "menus\\main\\hud_main_menu.xml")]
    [InlineData("inventory_menu.xml", "menus\\main\\inventory_menu.xml")]
    [InlineData("safe_zone.xml", "menus\\main\\safe_zone.xml")]
    [InlineData("char_gen_menu.xml", "menus\\chargen\\char_gen_menu.xml")]
    [InlineData("SPECIALBookMenu.xml", "menus\\chargen\\SPECIALBookMenu.xml")]
    [InlineData("TextEditMenu.xml", "menus\\dialog\\TextEditMenu.xml")]
    [InlineData("dialog_menu.xml", "menus\\dialog\\dialog_menu.xml")]
    [InlineData("start_menu.xml", "menus\\options\\start_menu.xml")]
    [InlineData("main_menu.xml", "menus\\options\\main_menu.xml")]
    [InlineData("skill_perk.xml", "menus\\generic\\skill_perk.xml")]
    public void ToPcPath_MapsConsoleNameToEngineLayout(string consoleName, string expected)
    {
        Assert.Equal(expected, FinalMasterXmlLayout.ToPcPath(consoleName));
    }

    [Fact]
    public void MenusAbsentFromConsoleBuild_ListsTheKnownGap()
    {
        // The console container declares 40 of the 49 menu classes vanilla PC ships. This list is
        // what a conversion cannot supply from the 360 build alone.
        Assert.Equal(9, FinalMasterXmlLayout.MenusAbsentFromConsoleBuild.Count);
        Assert.Contains("menus\\book_menu.xml", FinalMasterXmlLayout.MenusAbsentFromConsoleBuild);
        Assert.Contains("menus\\options\\save_menu.xml", FinalMasterXmlLayout.MenusAbsentFromConsoleBuild);
    }

    [Theory]
    // The backfill takes the whole menus\ tree from a PC donor, including the prefabs the
    // donor's own menus <include>; the console container is flattened and needs none of them.
    [InlineData("menus\\book_menu.xml", true)]
    [InlineData("menus\\prefabs\\box.xml", true)]
    [InlineData("menus\\options\\save_menu.xml", true)]
    // The console ships its own dictionary, so the donor's copy must not displace it.
    [InlineData("menus\\falloutdict.txt", false)]
    // Nothing outside the interface tree is a backfill candidate.
    [InlineData("lodsettings\\wastelandnv.dlodsettings", false)]
    [InlineData("facegen\\si.ctl", false)]
    public void IsBackfillCandidate_TakesTheInterfaceTreeExceptTheConsoleDictionary(
        string archivePath, bool expected)
    {
        Assert.Equal(expected, FinalMasterXmlLayout.IsBackfillCandidate(archivePath));
    }

    [Fact]
    public void ConsoleMenuPaths_AndBackfillCandidates_OverlapOnlyWhereConsoleWins()
    {
        // A donor entry whose path a console document already occupies must be skipped by the
        // caller, not by IsBackfillCandidate — otherwise the flattened console menu would be
        // silently replaced by the PC layout.
        var consolePath = FinalMasterXmlLayout.ToPcPath("quantity_menu.xml");
        Assert.Equal("menus\\quantity_menu.xml", consolePath);
        Assert.True(FinalMasterXmlLayout.IsBackfillCandidate(consolePath));
    }
}
