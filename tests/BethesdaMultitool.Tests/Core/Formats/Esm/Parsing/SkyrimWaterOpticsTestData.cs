using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

internal static class SkyrimWaterOpticsTestData
{
    private static readonly byte[] Opacity = [30];
    private static readonly byte[] Flags = [0];

    // Installed PC Skyrim.esm DefaultWater WATR18, DNAM at file +0E84F66C, 228 bytes.
    // Copied from canonical EsmAnalyzer output, not a second ESM reader.
    // DNAM SHA256: 15627F49A3CCD20E9547603D9C5F20B23FA496D6EE7128CCFB1E5D9ADD68A0C0
    internal static byte[] DefaultDnam()
    {
        return Convert.FromHexString(
            "CDCCCC3D0000B4420000003F0000803F00407F440000803FCDCC4C3D00000000" +
            "000000000000DC422534250005100500677A750032CDCDCDCDCCCC3D9A99193F" +
            "F6287C3F000000403333333FCDCCCC3D9A99593FCDCC4C3F48E17A3FCDCC4C3D" +
            "00707A45000087430000524300006143E3A59B3CF4FD543CA69BC43D00C0C145" +
            "CDCC4C3E7B146E3F000061446666663F0000FAC30000C844000010410000FA43" +
            "0000000000401C46000020410000F0440078D1450000F4436519323FE561213F" +
            "C5FEF23E7B14AE3E9A99D93FCDCC4C406666663F0000003FCDCCCC3DCDCC4C3E" +
            "00800945");
    }

    internal static WaterRecord ParseDefaultWater()
    {
        // Minimal synthetic record framing around the exact retail DNAM and repeated NNAMs.
        var body = new List<byte>();
        Append(body, "EDID", Encoding.ASCII.GetBytes("DefaultWater\0"));
        var normal = Encoding.ASCII.GetBytes("Data\\Textures\\Water\\DefaultWater.dds\0");
        for (var layer = 0; layer < 3; layer++)
            Append(body, "NNAM", normal);
        Append(body, "ANAM", Opacity);
        Append(body, "FNAM", Flags);
        Append(body, "DNAM", DefaultDnam());
        var file = new byte[24 + body.Count];
        body.CopyTo(file, 24);
        var record = new DetectedMainRecord("WATR", (uint)body.Count, 0, 0x18, 0, false)
        {
            HeaderSize = 24
        };
        var scan = new EsmRecordScanResult { Game = BethesdaGame.Skyrim, MainRecords = [record] };
        var context = new RecordParserContext(scan, null, new ByteArrayMemoryAccessor(file), file.Length, null);
        return Assert.Single(new MiscEnvironmentHandler(context).ParseWater());
    }

    private static void Append(List<byte> body, string signature, byte[] value)
    {
        body.AddRange(Encoding.ASCII.GetBytes(signature));
        var size = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(size, checked((ushort)value.Length));
        body.AddRange(size);
        body.AddRange(value);
    }
}