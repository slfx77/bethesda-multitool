namespace SampleGenerator;

/// <summary>
///     Checks that run without any private media: path containment, catalog well-formedness, and
///     the mirror/move primitives against a temporary tree.
/// </summary>
internal static class SampleGeneratorSelfTests
{
    private static int _failures;

    internal static int Run()
    {
        _failures = 0;

        CatalogNamesAreUniqueAndWellFormed();
        CatalogDatesParse();
        CatalogDateProvenanceIsDeclared();
        DestinationPathsStayInsideTheirRoot();
        DeletionRefusesOutsideItsRoot();
        MirrorAndMoveRoundTrip();
        CoffTimestampReaderRejectsNonPe();
        LayoutMovesAreDistinct();
        TreeMovingPartsClaimAnUnusedDestination();

        Console.WriteLine(_failures == 0
            ? "Self-test: all checks passed."
            : $"Self-test: {_failures} check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    private static void CatalogNamesAreUniqueAndWellFormed()
    {
        var names = SampleGeneratorCatalog.Builds.Select(SampleGeneratorCatalog.DirectoryName).ToArray();

        var duplicates = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        Check(duplicates.Length == 0,
            $"catalog directory names are unique (duplicates: {string.Join("; ", duplicates)})");

        foreach (var name in names)
        {
            Check(name.Length > 0 && !name.Contains("()", StringComparison.Ordinal) &&
                  name.EndsWith(')') && name.Contains(" (", StringComparison.Ordinal),
                $"well-formed catalog name: '{name}'");
            Check(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
                $"catalog name is a legal path segment: '{name}'");
        }

        Check(SampleGeneratorCatalog.Builds.All(b => b.Parts.Length > 0),
            "every catalog entry declares at least one source part");
    }

    private static void CatalogDatesParse()
    {
        foreach (var entry in SampleGeneratorCatalog.Builds)
        {
            if (entry.Date is null)
            {
                continue;
            }

            var parsed = DateTime.TryParseExact(
                entry.Date, "yyyy-M-d", null, System.Globalization.DateTimeStyles.None, out var date);
            Check(parsed, $"catalog date parses as yyyy-M-d: '{entry.Date}' ({entry.Game})");

            if (parsed)
            {
                // Nothing here predates Arena's development or postdates this machine's clock; a
                // date outside that window means a junk timestamp reached the catalog, which is
                // exactly the Morrowind 2030-10-02 failure this guards against.
                Check(date.Year is >= 1993 and <= 2027,
                    $"catalog date is plausible: {entry.Date} ({entry.Game}, {entry.Platform})");
            }
        }
    }

    private static void CatalogDateProvenanceIsDeclared()
    {
        foreach (var entry in SampleGeneratorCatalog.Builds)
        {
            Check(entry.Date is null == (entry.DateSource == DateProvenance.None),
                $"date and provenance agree: {entry.Game} ({entry.Platform}) " +
                $"date='{entry.Date}' source={entry.DateSource}");

            // A release-date fallback is a judgement call, so it must say why on the record.
            if (entry.DateSource == DateProvenance.ReleaseDate)
            {
                Check(!string.IsNullOrWhiteSpace(entry.Notes),
                    $"release-date fallback explains itself: {entry.Game} ({entry.Platform})");
            }
        }
    }

    private static void DestinationPathsStayInsideTheirRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BethesdaSampleGeneratorSelfTest");
        Directory.CreateDirectory(root);
        try
        {
            var inside = SampleGeneratorPathSafety.ResolveDestinationPath(root, @"a\b\c.txt");
            Check(SampleGeneratorPathSafety.IsStrictDescendant(inside, root),
                "a relative destination resolves inside its root");

            Check(Throws(() => SampleGeneratorPathSafety.ResolveDestinationPath(root, @"..\escape.txt")),
                "a traversal destination is rejected");
            Check(Throws(() => SampleGeneratorPathSafety.ResolveDestinationPath(root, @"C:\rooted.txt")),
                "a rooted destination is rejected");
            Check(Throws(() => SampleGeneratorPathSafety.ResolveDestinationPath(root, "")),
                "an empty destination is rejected");
            Check(!SampleGeneratorPathSafety.IsStrictDescendant(root, root),
                "a root is not a strict descendant of itself");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void DeletionRefusesOutsideItsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BethesdaSampleGeneratorSelfTestDelete");
        var sibling = Path.Combine(Path.GetTempPath(), "BethesdaSampleGeneratorSelfTestSibling");
        Directory.CreateDirectory(Path.Combine(root, "child"));
        Directory.CreateDirectory(sibling);
        try
        {
            Check(Throws(() => SampleGeneratorFileOperations.DeleteTreeUnder(sibling, root)),
                "deleting a sibling of the allowed root is refused");
            Check(Throws(() => SampleGeneratorFileOperations.DeleteTreeUnder(root, root)),
                "deleting the allowed root itself is refused");

            SampleGeneratorFileOperations.DeleteTreeUnder(Path.Combine(root, "child"), root);
            Check(!Directory.Exists(Path.Combine(root, "child")),
                "deleting a strict descendant succeeds");
        }
        finally
        {
            TryDelete(root);
            TryDelete(sibling);
        }
    }

    private static void MirrorAndMoveRoundTrip()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "BethesdaSampleGeneratorSelfTestMirror");
        var source = Path.Combine(scratch, "src");
        var mirrored = Path.Combine(scratch, "mirror");
        var moved = Path.Combine(scratch, "moved");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            File.WriteAllText(Path.Combine(source, "root.txt"), "root");
            File.WriteAllText(Path.Combine(source, "nested", "leaf.txt"), "leaf-content");

            var (files, bytes) = SampleGeneratorFileOperations.MirrorTree(source, mirrored);
            Check(files == 2, $"mirror copies every file (got {files}, expected 2)");
            Check(bytes == 4 + 12, $"mirror reports the byte total (got {bytes}, expected 16)");
            Check(File.ReadAllText(Path.Combine(mirrored, "nested", "leaf.txt")) == "leaf-content",
                "mirror preserves nested content");

            var (movedFiles, _) = SampleGeneratorFileOperations.MoveTree(mirrored, moved);
            Check(movedFiles == 2, $"move reports the file count (got {movedFiles}, expected 2)");
            Check(!Directory.Exists(mirrored), "move leaves no source directory behind");
            Check(File.Exists(Path.Combine(moved, "nested", "leaf.txt")),
                "move preserves nested content");

            Directory.CreateDirectory(Path.Combine(scratch, "occupied"));
            File.WriteAllText(Path.Combine(scratch, "occupied", "existing.txt"), "x");
            Check(Throws(() => SampleGeneratorFileOperations.MoveTree(source, Path.Combine(scratch, "occupied"))),
                "move refuses a non-empty destination");
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private static void CoffTimestampReaderRejectsNonPe()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "BethesdaSampleGeneratorSelfTestPe");
        Directory.CreateDirectory(scratch);
        try
        {
            var notPe = Path.Combine(scratch, "notpe.bin");
            File.WriteAllBytes(notPe, new byte[128]);
            Check(SampleGeneratorFileOperations.ReadCoffTimestamp(notPe) is null,
                "a file with no PE signature yields no timestamp");

            var tooShort = Path.Combine(scratch, "short.bin");
            File.WriteAllBytes(tooShort, [0x4D, 0x5A]);
            Check(SampleGeneratorFileOperations.ReadCoffTimestamp(tooShort) is null,
                "a truncated file yields no timestamp");

            // A minimal PE whose COFF timestamp is a known value, so the reader is checked against
            // an independently-constructed expectation rather than against itself.
            var pe = Path.Combine(scratch, "fake.exe");
            var image = new byte[0x100];
            image[0] = 0x4D;
            image[1] = 0x5A;
            BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
            image[0x80] = 0x50;
            image[0x81] = 0x45;
            BitConverter.GetBytes(1_000_000_000u).CopyTo(image, 0x88);
            File.WriteAllBytes(pe, image);
            var stamp = SampleGeneratorFileOperations.ReadCoffTimestamp(pe);
            Check(stamp == new DateTime(2001, 9, 9, 1, 46, 40, DateTimeKind.Utc),
                $"COFF timestamp 1000000000 reads as 2001-09-09 01:46:40 (got {stamp:O})");
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    /// <summary>
    ///     A part that moves or mirrors a whole TREE refuses a destination that already has content
    ///     (MoveTree does, and MirrorTree would silently merge), so no such part may land on a
    ///     destination an earlier part of the same build already populated. The constraint is
    ///     invisible in the catalog table — it only shows up as a mid-run failure after the earlier
    ///     part has already been moved — so it is checked here rather than discovered at 60 GB in.
    /// </summary>
    private static void TreeMovingPartsClaimAnUnusedDestination()
    {
        foreach (var entry in SampleGeneratorCatalog.Builds)
        {
            var claimed = new List<string>();
            foreach (var part in entry.Parts)
            {
                var destination = part.DestinationSubdir ?? ".";
                var isTreePart = part.Kind is SourceKind.StagedTree or SourceKind.SteamGame
                    or SourceKind.MediaTree or SourceKind.MediaArchive;

                if (isTreePart)
                {
                    Check(!claimed.Contains(destination, StringComparer.OrdinalIgnoreCase),
                        $"tree part claims an unused destination: {SampleGeneratorCatalog.DirectoryName(entry)} " +
                        $"part {part.Kind}:{part.Hint} -> '{destination}'");
                }

                claimed.Add(destination);
            }
        }
    }

    private static void LayoutMovesAreDistinct()
    {
        var sources = SampleGeneratorLayout.Moves.Select(m => m.From).ToArray();
        var destinations = SampleGeneratorLayout.Moves.Select(m => m.To).ToArray();
        Check(sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() == sources.Length,
            "layout move sources are distinct");
        Check(destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() == destinations.Length,
            "layout move destinations are distinct");
        Check(SampleGeneratorLayout.Moves.All(m => !Path.IsPathRooted(m.From) && !Path.IsPathRooted(m.To)),
            "layout moves are Sample-relative");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return true;
        }
    }

    private static void Check(bool condition, string description)
    {
        if (condition)
        {
            return;
        }

        _failures++;
        Console.Error.WriteLine($"  FAIL: {description}");
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup failure is not a test failure.
        }
    }
}
