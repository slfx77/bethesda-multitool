using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeLiveIdentityTests
{
    [Theory]
    [InlineData("main changed")]
    [InlineData("edit")]
    public async Task Refresh_binds_engine_order_to_backing_files_and_rehashes_only_changed_metadata(string replacement)
    {
        using var files = new Fixture();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(files.File("Main.esm"), "main", token);
        await File.WriteAllTextAsync(files.File("Addon.esp"), "addon", token);
        await File.WriteAllTextAsync(files.File("Inactive.esp"), "inactive", token);
        var hashes = 0;
        var reader = new RuntimeLiveIdentity(_ => ("mapped-game", files.Root), async (path, cancellationToken) =>
        {
            ++hashes;
            return Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken)));
        });
        var identity = Json("{\"processId\":1,\"activePluginIdentityStatus\":\"unavailable\"}");
        var engine = Engine(["Addon.esp", "Main.esm"]);
        var first = await reader.RefreshAsync(identity, engine, token);
        Assert.Equal("complete", first.GetProperty("activePluginIdentityStatus").GetString());
        Assert.Equal("Addon.esp", first.GetProperty("activePlugins")[0].GetProperty("name").GetString());
        Assert.Equal(2, hashes);
        Assert.Equal(files.File("Main.esm"), first.GetProperty("activePlugins")[1].GetProperty("backingPath").GetString());
        var second = await reader.RefreshAsync(first, engine, token);
        Assert.Equal(2, hashes);
        Assert.Equal(2, second.GetProperty("pluginHashCache").GetProperty("reused").GetInt32());
        await File.WriteAllTextAsync(files.File("Main.esm"), replacement, token);
        File.SetLastWriteTimeUtc(files.File("Main.esm"), DateTime.UtcNow.AddSeconds(5));
        var third = await reader.RefreshAsync(second, engine, token);
        Assert.Equal(3, hashes);
        Assert.NotEqual(first.GetProperty("activePlugins")[1].GetProperty("sha256").GetString(),
            third.GetProperty("activePlugins")[1].GetProperty("sha256").GetString());
        File.Delete(files.File("Addon.esp"));
        var missing = await reader.RefreshAsync(third, engine, token);
        Assert.Equal("partial", missing.GetProperty("activePluginIdentityStatus").GetString());
        Assert.Equal("unavailable", missing.GetProperty("activePlugins")[0].GetProperty("status").GetString());
        Assert.False(missing.GetProperty("activePlugins")[0].TryGetProperty("sha256", out _));
    }

    [Theory]
    [InlineData("../Main.esm")]
    [InlineData("Main.txt")]
    [InlineData("")]
    public async Task Invalid_engine_names_do_not_bind_files_or_claim_complete_identity(string name)
    {
        using var files = new Fixture();
        var reader = new RuntimeLiveIdentity(_ => ("mapped-game", files.Root), (_, _) => throw new InvalidOperationException("Must not hash"));
        var result = await reader.RefreshAsync(Json("{}"), Engine([name]), TestContext.Current.CancellationToken);
        Assert.Equal("partial", result.GetProperty("activePluginIdentityStatus").GetString());
        Assert.False(result.GetProperty("activePlugins")[0].TryGetProperty("sha256", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_loaded_state_or_getter_clears_old_identity(bool loaded)
    {
        var reader = new RuntimeLiveIdentity(_ => ("mapped-game", "unused"));
        var result = await reader.RefreshAsync(Json("{\"activePlugins\":[{\"name\":\"stale.esm\"}],\"activePluginIdentityStatus\":\"complete\"}"),
            Engine([], loaded), TestContext.Current.CancellationToken);
        Assert.Equal("unavailable", result.GetProperty("activePluginIdentityStatus").GetString());
        Assert.Equal(0, result.GetProperty("activePlugins").GetArrayLength());
    }

    private static Func<JsonElement, CancellationToken, Task<JsonElement>> Engine(string[] names, bool loaded = true) => (request, _) =>
    {
        if (request.GetProperty("op").GetString() == "snapshot") return Task.FromResult(Json(loaded
            ? "{\"status\":\"completed\",\"snapshot\":{\"player.cell\":{\"status\":\"observed\",\"formId\":1}}}"
            : "{\"status\":\"completed\",\"snapshot\":{\"player.cell\":{\"status\":\"unavailable\"}}}"));
        var expression = request.GetProperty("expression").GetString()!;
        var value = expression == "GetNumLoadedMods"
            ? new JsonObject { ["type"] = "number", ["value"] = names.Length }
            : new JsonObject { ["type"] = "string", ["value"] = names[int.Parse(expression.AsSpan("GetNthModName ".Length), System.Globalization.CultureInfo.InvariantCulture)], ["complete"] = true };
        return Task.FromResult(Json(new JsonObject { ["status"] = "completed", ["result"] = value }.ToJsonString()));
    };

    private static JsonElement Json(string text) { using var json = JsonDocument.Parse(text); return json.RootElement.Clone(); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmt-live-identity-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string File(string name) => Path.Combine(Root, name);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
