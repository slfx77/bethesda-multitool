using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a Climate (CLMT) record. A worldspace points at one CLMT via WRLD CNAM; the
///     climate supplies the weather cycle, sun / sun-glare textures, and the sunrise / sunset /
///     moon timing that drives the time-of-day sun curve. Starfield's WSLT choices are emitted as
///     WTHS references without conflating them with legacy WTHR choices. Without this encoder a proto-only
///     climate is stripped from the output, and any worldspace whose CNAM referenced it falls
///     back to engine defaults — the worldspace loses its weather list and day/night curve.
///     <para>
///         Canonical order from xEdit <c>wbRecord(CLMT)</c> (wbDefinitionsFNV.pas):
///         EDID(req), WLST?, FNAM?, GNAM?, MODL?, TNAM?.
///     </para>
/// </summary>
public sealed class ClmtEncoder : IRecordEncoder
{
    public string RecordType => "CLMT";

    public Type ModelType => typeof(ClimateRecord);

    /// <summary>Encode a new CLMT record from scratch in xEdit canonical order.</summary>
    internal static EncodedRecord EncodeNew(ClimateRecord climate)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(climate.EditorId))
        {
            warnings.Add($"New CLMT 0x{climate.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", climate.EditorId ?? string.Empty));

        if (climate.WeatherTypes is { Count: > 0 } weathers)
        {
            subs.Add(new EncodedSubrecord("WLST", EncodeWlst(weathers)));
        }

        if (climate.WeatherSettingsTypes is { Count: > 0 } weatherSettings)
        {
            subs.Add(new EncodedSubrecord("WSLT", EncodeWslt(weatherSettings)));
        }

        if (!string.IsNullOrEmpty(climate.SunTexture))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FNAM", climate.SunTexture));
        }

        if (!string.IsNullOrEmpty(climate.SunGlareTexture))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("GNAM", climate.SunGlareTexture));
        }

        if (!string.IsNullOrEmpty(climate.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", climate.ModelPath));
        }

        if (climate.Timing is { } timing)
        {
            subs.Add(new EncodedSubrecord("TNAM", EncodeTnam(timing)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     CLMT WLST payload: an array of 12-byte little-endian entries
    ///     (uint32 WTHR FormID, int32 Chance, uint32 GLOB FormID), per xEdit
    ///     <c>wbArrayS(WLST, …)</c>.
    /// </summary>
    internal static byte[] EncodeWlst(IReadOnlyList<ClimateWeatherEntry> weathers)
    {
        var bytes = new byte[weathers.Count * 12];
        for (var i = 0; i < weathers.Count; i++)
        {
            var offset = i * 12;
            var entry = weathers[i];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), entry.WeatherFormId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4, 4), entry.Chance);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8, 4), entry.GlobalFormId);
        }

        return bytes;
    }

    /// <summary>
    ///     Starfield CLMT WSLT payload: an array of 12-byte little-endian entries
    ///     (uint32 WTHS FormID, int32 Chance, uint32 GLOB FormID), per xEdit's SF1 definition.
    /// </summary>
    internal static byte[] EncodeWslt(IReadOnlyList<ClimateWeatherSettingsEntry> weatherSettings)
    {
        var bytes = new byte[weatherSettings.Count * 12];
        for (var i = 0; i < weatherSettings.Count; i++)
        {
            var offset = i * 12;
            var entry = weatherSettings[i];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), entry.WeatherSettingsFormId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4, 4), entry.Chance);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8, 4), entry.GlobalFormId);
        }

        return bytes;
    }

    /// <summary>
    ///     CLMT TNAM payload (five or six raw bytes, no endianness concerns). The four time fields are
    ///     stored in 10-minute units — the engine multiplies by 1/6 to get hours
    ///     (<c>TESClimate::Load</c> → <c>climate+0x60</c>, read back by
    ///     <c>Sky::GetSunriseBegin</c>). <see cref="ClimateTimingData" /> holds them raw, so this
    ///     is a straight copy.
    /// </summary>
    internal static byte[] EncodeTnam(ClimateTimingData timing)
    {
        if (!timing.HasMoonPhaseLength)
        {
            return
            [
                timing.SunriseBegin,
                timing.SunriseEnd,
                timing.SunsetBegin,
                timing.SunsetEnd,
                timing.Volatility
            ];
        }

        return
        [
            timing.SunriseBegin,
            timing.SunriseEnd,
            timing.SunsetBegin,
            timing.SunsetEnd,
            timing.Volatility,
            timing.MoonPhaseLength
        ];
    }
}
