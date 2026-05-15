using System.Text.Json;
using FalloutXbox360Utils.CLI.Commands.Dmp;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI.Commands.Dmp;

public class CellWorldspaceAuthorityTests
{
    [Fact]
    public void Load_ReadsHexCellMap()
    {
        var path = WriteTempJson(
            """
            {
              "cells": {
                "0x00001000": "0x0000003C",
                "00001001": "0000003D",
                "not-a-form-id": "0x0000003E"
              }
            }
            """);
        try
        {
            var result = CellWorldspaceAuthorityJson.Load(path);

            Assert.Null(result.Warning);
            Assert.Equal(path, result.Path);
            Assert.NotNull(result.Cells);
            Assert.Equal(2, result.Cells.Count);
            Assert.Equal(0x3Cu, result.Cells[0x1000]);
            Assert.Equal(0x3Du, result.Cells[0x1001]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_PcEsmEntriesWinOverCorpusAuthority()
    {
        var pcEsm = new Dictionary<uint, uint>
        {
            [0x1000] = 0x3C,
            [0x1001] = 0x3D
        };
        var authority = new Dictionary<uint, uint>
        {
            [0x1000] = 0x99,
            [0x1002] = 0x40
        };

        var merged = CellWorldspaceAuthorityJson.Merge(pcEsm, authority);

        Assert.NotNull(merged);
        Assert.Equal(0x3Cu, merged[0x1000]);
        Assert.Equal(0x3Du, merged[0x1001]);
        Assert.Equal(0x40u, merged[0x1002]);
    }

    [Fact]
    public void Builder_KeepsFirstAssignmentAndRecordsConflicts()
    {
        var builder = new CellWorldspaceAuthorityBuilder();

        Assert.True(builder.TryAddOrFlag(0x1000, 0x3C, "first"));
        Assert.False(builder.TryAddOrFlag(0x1000, 0x3D, "second"));

        Assert.Equal(0x3Cu, builder.CellToWorldspace[0x1000]);
        var conflicts = Assert.Single(builder.Conflicts);
        Assert.Equal(0x1000u, conflicts.Key);
        Assert.Equal([0x3Cu, 0x3Du], conflicts.Value.OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task WriteAsync_EmitsCellsWorldspacesConflictsAndSources()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cell-authority-{Guid.NewGuid():N}.json");
        try
        {
            await CellWorldspaceAuthorityJson.WriteAsync(
                path,
                new Dictionary<uint, uint> { [0x1000] = 0x3C },
                new Dictionary<uint, HashSet<uint>> { [0x1000] = [0x3C, 0x3D] },
                new Dictionary<uint, string> { [0x3C] = "TestWorld" },
                [new CellWorldspaceAuthoritySource("dmp", @"Dumps\sample.dmp", 1, 2)],
                TestContext.Current.CancellationToken);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken));
            Assert.Equal("0x0000003C", doc.RootElement.GetProperty("cells").GetProperty("0x00001000").GetString());
            Assert.Equal("TestWorld", doc.RootElement.GetProperty("worldspaces").GetProperty("0x0000003C").GetString());
            Assert.Equal(
                ["0x0000003C", "0x0000003D"],
                doc.RootElement.GetProperty("conflicts").GetProperty("0x00001000")
                    .EnumerateArray()
                    .Select(e => e.GetString()!)
                    .ToArray());
            Assert.Equal("Dumps/sample.dmp",
                doc.RootElement.GetProperty("sources")[0].GetProperty("path").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cell-authority-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
