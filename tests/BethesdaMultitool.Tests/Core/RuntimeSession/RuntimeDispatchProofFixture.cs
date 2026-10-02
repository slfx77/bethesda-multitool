using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

internal static class RuntimeDispatchProofFixture
{
    private const string Window = "558bec83ec588b45208945f4837df400750732c0e96c040000c745fc000000000fb64d2485c974138b4d1ce8104ce5ff0fb6d085d27404c64524008b45108b088b550c8a040a8845fb8b4d108b1183c2018b451089100fbe4dfb894db88b55b883ea478955b8837db8330f87760200008b45b80fb6883ccc5a00ff248d2ccc5a008b55108b028b4d0c668b1401668955f08b45108b0883c1028b5510890a8b4520500fbf4df0518b4d1ce8a1fcffff8945fc837dfc000f84d20100008b55fc837a0800750c0fb6452485c00f849b0100000fbe4dfb83f947751c8b55fc8b4a08e83ba2f7ff8b4508dd18b001e994030000e9740100000fbe4dfb83f95a752a8b55fc8b4a08e8f61a2a008945c48b4508508d4dc451e8b603000083c408b001e961030000e9410100008b55108b028b4d0c8a14018855fb8b45108b0883c1018b5510890a0fbe45fb83f8580f8419010000c745ec000000008b4dfc8379080074198b55fc8b4a08e86448e5ff83f84775098b45fc8b4808894dec837dec0074108b4dece8d819ffff8945f4e9cd000000c745e8000000008b55fc837a0800742b8b45fc8b4808e82548e5ff8945b4837db43a7c17837db4407e08837db4697402eb098b4dfc8b51088955e8837de8000f84880000008b4de8e8437a02008bc8e84cffe6ff85c0746a8b4de8e8307a02008bc8e839ffe6ff8945e48b4de8e8fe192a00508b4de8e8852a2000508b4de4e8ac98fcff8945e0837de000740b8b4de0e8db14f1ff8945f48b45e08945bc8b4dbc894dc0837dc000740f6a018b4dc0e8fc8fe9ff8945b0eb07c745b000000000eb0b8b4de8e8e6a9fbff8945f4837df400750732c0e91b020000eb208b55fc837a0c0074108b4508d9eedd18b001e902020000eb0732c0e9f9010000eb0732c0e9f0010000eb4f8b4d108b118b450cdb04108b4d08dd198b55108b0283c0048b4d108901b001e9ca0100006a088b55108b450c0302508b4d0851e8f149e5ff83c40c8b55108b0283c0088b4d108901b001e99f0100000fbe55fb83fa580f853b0100008b45108b088b550c0fbf040a8945d88b4d108b1183c2028b451089108b4d108b1183c2028b451089108b4dd851e85b46000083c4048945dc837ddc00750c32c0e94d010000e9ef0000008b55148955d4837dfc0074448b45fc83780800743bc745d4000000008b4dfc83790800742b8b55fc8b4a08e86346e5ff8945ac837dac3a7c17837dac407e08837dac697402eb098b45fc8b4808894dd48b55dc8b42148945d00fb64d2485c9742c837dd00074248b5520528b451c508b4d18518b55d4528b4510508b4d0c518b55d052e84b01000083c41ceb628b45dc0fb6481085c974118b55dc0fb6421085c0744c837dd40074468b4ddc8b51188955cc837dcc0074338b4510508b4d08518b5520528b451c508b4d18518b55d4528b450c508b4dd051ff55cc83c4200fb6d085d27406b001eb5feb0432c0eb59eb558b45108b088b550c668b040a668945c88b4d108b1183c2028b451089108a4dfb884da8807da866740e807da86c7408807da8737402eb1a8b551c520fbf45c8508b4df4e82ac5ffff8b4d08dd19b001eb0632c0eb0232c08be55dc3";
    internal const string NativeHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal static JsonObject Proof(string boundary) => new()
    {
        ["schemaVersion"] = 1, ["route"] = "pc-expression-X-command", ["boundary"] = boundary,
        ["status"] = "observed", ["reason"] = "repeated-main-image-read", ["processId"] = 99,
        ["processCreationFileTime"] = 134353560780109826UL,
        ["executableFileSha256"] = RuntimeScriptOffsetCalibration.ExecutableHash,
        ["nativeImageBase"] = 0x55000000, ["nativeSha256"] = NativeHash,
        ["session"] = "synthetic-bracket", ["captureGeneration"] = 4, ["connectionGeneration"] = 2,
        ["imageBase"] = 0x00400000, ["address"] = 0x005AC7A0, ["length"] = 1161,
        ["sha256"] = RuntimeScriptOffsetCalibration.WindowHash, ["bytesHex"] = Window
    };
    internal static async Task<RuntimeTraceDocument> Trace(JsonObject entry, string pluginHash, bool proofs = true,
        bool wrongExecutable = false, Action<JsonObject, JsonObject, JsonObject>? amend = null)
    {
        var identity = new JsonObject { ["backend"] = "xnvse", ["processId"] = 99,
            ["executableFileSha256"] = wrongExecutable ? new string('f', 64) : RuntimeScriptOffsetCalibration.ExecutableHash,
            ["processStartedUtc"] = "2026-10-01T19:21:18.0109826Z",
            ["loadedRuntimeModules"] = new JsonArray(new JsonObject { ["mappedFile"] = @"\Device\fixture\NvseRuntimeBridge.dll",
                ["sha256"] = NativeHash, ["imageBase"] = 0x55000000, ["hashScope"] = "mapped-module-backing-file-on-disk" }),
            ["activePluginIdentityStatus"] = "complete", ["activePlugins"] = new JsonArray(new JsonObject {
                ["index"] = 0, ["name"] = "FalloutNV.esm", ["sha256"] = pluginHash, ["status"] = "verified", ["hashScope"] = "fixture" }) };
        var sequence = checked((ulong)entry["sequence"]!.GetValue<int>());
        var start = new JsonObject { ["kind"] = "capture-start", ["protocol"] = 1, ["sequence"] = sequence - 1,
            ["dropped"] = 0, ["session"] = "synthetic-bracket" };
        var end = new JsonObject { ["kind"] = "capture-end", ["protocol"] = 1, ["sequence"] = sequence + 1,
            ["dropped"] = 0, ["status"] = "completed" };
        if (proofs) { start["dispatchWindow"] = Proof("start"); end["dispatchWindow"] = Proof("end"); }
        amend?.Invoke(identity, start, end);
        var header = new JsonObject { ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 1, ["identity"] = identity };
        var text = string.Join('\n', header.ToJsonString(), start.ToJsonString(), entry.ToJsonString(), end.ToJsonString(),
            "{\"kind\":\"capture-footer\",\"status\":\"completed\",\"events\":3,\"dropped\":0,\"snapshots\":0,\"errors\":0}");
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await RuntimeTraceImporter.ReadDocumentAsync(stream, TestContext.Current.CancellationToken);
    }

    // Actual PC046 seq31 row; tests add a SYNTHETIC boundary envelope. The closed PC046 trace stays uncalibrated.
    internal static JsonObject ActualCommand() => JsonNode.Parse("""
        {
          "protocol": 1,
          "kind": "message-choice-read",
          "sequence": 31,
          "requestId": 0,
          "frame": 7294,
          "qpc": 7620647406615,
          "dropped": 0,
          "command": "GetButtonPressed",
          "opcode": 4127,
          "handlerReturned": true,
          "callerAddress": 5950392,
          "callerImageBase": 4194304,
          "callerRva": 1756088,
          "scriptFormId": 1069616,
          "scriptAddress": 1040446592,
          "scriptFlags": 9,
          "temporaryScript": false,
          "engineTargetFormId": 1069618,
          "messageOwnerFormId": 1069618,
          "commandLocation": {
            "status": "observed",
            "basis": "raw-sdk-command-arguments",
            "normalizedOffsetStatus": "unverified",
            "bytecodeStableAcrossCall": true,
            "before": {
              "scriptDataAddress": 1124219872,
              "opcodeOffsetPointer": 1760728,
              "opcodeOffsetStatus": "observed",
              "opcodeOffset": 83,
              "script": {
                "status": "observed",
                "address": 1040446592,
                "formId": 1069616,
                "formType": 17,
                "flags": 9,
                "dataAddress": 1124219872,
                "dataLength": 156,
                "headerHex": "9470030111f4033e0900000030521000a83255180000000000000000030000009c000000010000000000010000000000e03b0243"
              },
              "bytecode": {
                "status": "observed",
                "sha256": "c57f166e9cb0521c4181fbaa899879dd0b43f06a480b745265fff9c1558da7f0",
                "length": 156,
                "byteOrder": "little",
                "scope": "entire-Script-data",
                "limit": 65536
              },
              "pointerRangeStatus": "data-start"
            },
            "after": {
              "scriptDataAddress": 1124219872,
              "opcodeOffsetPointer": 1760728,
              "opcodeOffsetStatus": "observed",
              "opcodeOffset": 83,
              "script": {
                "status": "observed",
                "address": 1040446592,
                "formId": 1069616,
                "formType": 17,
                "flags": 9,
                "dataAddress": 1124219872,
                "dataLength": 156,
                "headerHex": "9470030111f4033e0900000030521000a83255180000000000000000030000009c000000010000000000010000000000e03b0243"
              },
              "bytecode": {
                "status": "observed",
                "sha256": "c57f166e9cb0521c4181fbaa899879dd0b43f06a480b745265fff9c1558da7f0",
                "length": 156,
                "byteOrder": "little",
                "scope": "entire-Script-data",
                "limit": 65536
              },
              "pointerRangeStatus": "data-start"
            }
          },
          "threadId": 32276,
          "observedScriptCallId": null,
          "scriptCallStatus": "Unavailable",
          "returnType": 0,
          "returnTypeStatus": "observed",
          "resultStatus": "observed",
          "resultBits": "bff0000000000000",
          "numericValue": -1,
          "value": -1,
          "evidence": "get-button-pressed-return"
        }
        """)!.AsObject();
}
