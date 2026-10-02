using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>The native bridge's binding to one capture and one process lifetime.</summary>
internal sealed record RuntimeGuestRunBinding(string ManifestSha256, ulong ConnectionGeneration,
    ulong CaptureEpoch, ulong BindingSerial, ulong WindowIdentity)
{
    internal static string Payload(RuntimeGuestRunAdmission admission, string session)
    {
        if (!Path.IsPathFullyQualified(admission.ProfilePath) || admission.ProfilePath.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
            admission.ProfileSha256 is not { Length: 64 } digest || digest.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)) ||
            !Guid.TryParseExact(session, "N", out _))
            throw new InvalidDataException("Guest binding identity is invalid.");
        return $"guest-run-bind/1\n{admission.ProfilePath}\n{digest}\n{session}";
    }

    internal static RuntimeGuestRunBinding Validate(JsonElement row, RuntimeGuestRunAdmission admission, string session)
    {
        if (Text(row, "kind") != "guest-run-bound" || Text(row, "origin") != "bridge-control" ||
            Text(row, "status") != "bound" || Text(row, "session") != session ||
            Text(row, "runManifestSha256") != admission.ProfileSha256 ||
            !Number(row, "processId", out var pid) || pid != (ulong)admission.ProcessId ||
            !DateTimeOffset.TryParse(admission.ProcessStartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var started) ||
            !Number(row, "processStartedFileTime", out var processTime) || processTime != (ulong)started.UtcDateTime.ToFileTimeUtc() ||
            !Number(row, "connectionGeneration", out var generation) || generation == 0 ||
            !Number(row, "captureEpoch", out var epoch) || epoch == 0 ||
            !Number(row, "bindingSerial", out var serial) || serial == 0 ||
            !Number(row, "windowIdentity", out var window) || window == 0)
            throw new InvalidDataException("Native guest binding does not match this capture and process.");
        return new(admission.ProfileSha256, generation, epoch, serial, window);
    }

    private static string? Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Number(JsonElement row, string name, out ulong value)
    {
        value = 0;
        return row.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Number && found.TryGetUInt64(out value);
    }
}
