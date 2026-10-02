using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeGuestRunBindingTests
{
    private const string Session = "00112233445566778899aabbccddeeff";
    private static readonly RuntimeGuestRunAdmission Admission = new(
        Path.GetFullPath("guest-profile.json"), new string('a', 64), 123,
        "2026-09-30T00:00:00.0000000Z", new string('b', 64), new("game", "storage", "content", "cache", "config", null));

    [Fact]
    public void Admission_is_tied_to_process_lifetime_and_capture()
    {
        using var json = JsonDocument.Parse(Bound().ToJsonString());
        var binding = RuntimeGuestRunBinding.Validate(json.RootElement, Admission, Session);
        Assert.Equal(Admission.ProfileSha256, binding.ManifestSha256);
        Assert.Equal(7UL, binding.BindingSerial);
        Assert.Equal($"guest-run-bind/1\n{Admission.ProfilePath}\n{Admission.ProfileSha256}\n{Session}",
            RuntimeGuestRunBinding.Payload(Admission, Session));
    }

    [Theory]
    [InlineData("session", "another-capture")]
    [InlineData("runManifestSha256", "another-profile")]
    [InlineData("processId", "999")]
    [InlineData("processStartedFileTime", "1")]
    [InlineData("connectionGeneration", "0")]
    [InlineData("captureEpoch", "0")]
    [InlineData("bindingSerial", "0")]
    [InlineData("windowIdentity", "0")]
    [InlineData("status", "queued")]
    [InlineData("origin", "controller")]
    public void Foreign_or_unfinished_binding_cannot_admit_controller_input(string field, string value)
    {
        var row = Bound();
        row[field] = ulong.TryParse(value, out var number) ? JsonValue.Create(number) : JsonValue.Create(value);
        using var json = JsonDocument.Parse(row.ToJsonString());
        Assert.Throws<InvalidDataException>(() => RuntimeGuestRunBinding.Validate(json.RootElement, Admission, Session));
    }

    private static JsonObject Bound() => new()
    {
        ["kind"] = "guest-run-bound", ["origin"] = "bridge-control", ["status"] = "bound",
        ["session"] = Session, ["runManifestSha256"] = Admission.ProfileSha256,
        ["processId"] = 123, ["processStartedFileTime"] =
            new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(),
        ["connectionGeneration"] = 3, ["captureEpoch"] = 5, ["bindingSerial"] = 7, ["windowIdentity"] = 9
    };
}
