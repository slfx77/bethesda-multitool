using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public class AssetPathCollectorTests
{
    [Fact]
    public void Collect_RecordPaths_InferMeshesAndTexturesPrefix()
    {
        // WeaponRecord.ModelPath stores paths relative to meshes\, ICON/MICO relative to
        // textures\. The collector infers the prefix from the file extension so the resulting
        // request path matches what the runtime queries.
        var records = new RecordCollection
        {
            Weapons =
            [
                new WeaponRecord
                {
                    FormId = 0x100,
                    EditorId = "TestGun",
                    ModelPath = "Weapons\\TestGun\\testgun.nif",
                    InventoryIconPath = "Interface\\Icons\\testgun.dds"
                }
            ],
            TextureSets =
            [
                new TextureSetRecord
                {
                    FormId = 0x200,
                    EditorId = "TestTxst",
                    // TextureSet DiffuseTexture already carries the textures\ prefix in fopdoc.
                    DiffuseTexture = "Textures\\Armor\\testset_d.dds",
                    NormalTexture = "armor\\testset_n.dds"  // no prefix — should infer textures\
                }
            ]
        };

        var paths = AssetPathCollector.Collect(records, dmpFilePath: null, NullConversionProgressSink.Instance);

        // Meshes path inferred for .nif from a record field that has no meshes\ prefix.
        Assert.Contains("meshes\\weapons\\testgun\\testgun.nif", paths);
        // Textures path inferred for .dds (ICON is relative to textures\).
        Assert.Contains("textures\\interface\\icons\\testgun.dds", paths);
        // Already-prefixed textures\ path preserved as-is (just lowercased).
        Assert.Contains("textures\\armor\\testset_d.dds", paths);
        // Bare path with .dds extension gets textures\ prefix inferred.
        Assert.Contains("textures\\armor\\testset_n.dds", paths);
    }

    [Fact]
    public void Collect_NormalizesSlashesAndStripsDataPrefix()
    {
        var records = new RecordCollection
        {
            Weapons =
            [
                new WeaponRecord
                {
                    FormId = 0x100,
                    EditorId = "T",
                    ModelPath = "Data\\Weapons/forward/slashes.nif"
                }
            ]
        };

        var paths = AssetPathCollector.Collect(records, dmpFilePath: null, NullConversionProgressSink.Instance);

        // Slashes flipped, data\ stripped, meshes\ inferred from .nif.
        Assert.Contains("meshes\\weapons\\forward\\slashes.nif", paths);
        Assert.DoesNotContain("data\\weapons\\forward\\slashes.nif", paths);
        Assert.DoesNotContain("weapons\\forward\\slashes.nif", paths);
    }

    [Fact]
    public void Collect_DmpScan_RejectsGarbagePrefixedCandidates()
    {
        // The DMP raw-byte scanner walks printable-byte runs separated by nulls. When a
        // path string lives in memory adjacent to register-save bytes or printf format
        // characters, the scanner picks them up with it. For DMP candidates we apply a
        // strict guard: the FIRST path segment must equal "meshes" / "textures" / etc.
        // Anything else (garbage-prefixed candidates) is dropped entirely — we don't try
        // to "rescue" them by trimming, because we can't distinguish a real unprefixed
        // path from byte noise in the DMP.
        using var tempFile = new TempFile();
        var payload = new List<byte>();
        void AppendNullTerm(string s)
        {
            payload.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
            payload.Add(0);
        }

        // Real paths with no garbage — pass through.
        AppendNullTerm("meshes\\characters\\_male\\skeleton.nif");
        AppendNullTerm("meshes\\creatures\\centaur\\skeleton.nif");
        AppendNullTerm("textures\\armor\\helm.dds");
        // Garbage-prefixed variants — dropped, not "rescued".
        AppendNullTerm("zcharacters\\_male\\skeleton.nif");
        AppendNullTerm("%hr\"meshes\\characters\\_male\\skeleton.nif");
        AppendNullTerm("eegxeegxcharacters\\_male\\skeleton.nif");
        // Basename-only — dropped (no directory).
        AppendNullTerm("skeleton.nif");
        File.WriteAllBytes(tempFile.Path, payload.ToArray());

        var paths = AssetPathCollector.Collect(new RecordCollection(), tempFile.Path,
            NullConversionProgressSink.Instance);

        // Clean paths kept.
        Assert.Contains("meshes\\characters\\_male\\skeleton.nif", paths);
        Assert.Contains("meshes\\creatures\\centaur\\skeleton.nif", paths);
        Assert.Contains("textures\\armor\\helm.dds", paths);

        // No garbage-prefixed paths leaked through under any rewriting.
        Assert.DoesNotContain("zcharacters\\_male\\skeleton.nif", paths);
        Assert.DoesNotContain("meshes\\zcharacters\\_male\\skeleton.nif", paths);
        Assert.DoesNotContain("eegxeegxcharacters\\_male\\skeleton.nif", paths);
    }

    [Fact]
    public void Collect_BasenameOnly_IsRejected()
    {
        // A record that somehow stores just "skeleton.nif" (no directory) is too ambiguous
        // to be packed — could match dozens of candidates. Reject rather than guess.
        var records = new RecordCollection
        {
            Weapons =
            [
                new WeaponRecord { FormId = 0x100, EditorId = "T", ModelPath = "skeleton.nif" }
            ]
        };

        var paths = AssetPathCollector.Collect(records, dmpFilePath: null, NullConversionProgressSink.Instance);

        Assert.DoesNotContain("meshes\\skeleton.nif", paths);
        Assert.DoesNotContain("skeleton.nif", paths);
    }

    [Fact]
    public void Collect_LeadingBackslashOnRecordField_IsHandled()
    {
        // Record fields sometimes have a stray leading "\". Should not produce "\foo.nif".
        var records = new RecordCollection
        {
            Weapons =
            [
                new WeaponRecord
                {
                    FormId = 0x100,
                    EditorId = "T",
                    ModelPath = "\\weapons\\rifle\\rifle.nif"
                }
            ]
        };

        var paths = AssetPathCollector.Collect(records, dmpFilePath: null, NullConversionProgressSink.Instance);

        Assert.Contains("meshes\\weapons\\rifle\\rifle.nif", paths);
        Assert.DoesNotContain("\\weapons\\rifle\\rifle.nif", paths);
    }

    [Fact]
    public void Collect_FiltersNonAssetExtensions()
    {
        // SoundRecord.FileName can carry .wav (asset) or .psc/.txt (non-asset). Verify the
        // collector only retains values whose extension is in the packable set.
        var records = new RecordCollection
        {
            Sounds =
            [
                new SoundRecord
                {
                    FormId = 0x300,
                    EditorId = "Goodwav",
                    FileName = "Sound\\FX\\testfx.wav"
                },
                new SoundRecord
                {
                    FormId = 0x301,
                    EditorId = "BadExt",
                    FileName = "Sound\\FX\\testfx.psc" // not an asset extension
                }
            ]
        };

        var paths = AssetPathCollector.Collect(records, dmpFilePath: null, NullConversionProgressSink.Instance);

        Assert.Contains("sound\\fx\\testfx.wav", paths);
        Assert.DoesNotContain("sound\\fx\\testfx.psc", paths);
    }

    [Fact]
    public void Collect_DerivesEgmEgtTriSiblingsForEveryNif()
    {
        var records = new RecordCollection
        {
            ModelPathIndex = { [0x500] = "Characters\\Hair\\HairSpikey.nif" }
        };

        var paths = AssetPathCollector.Collect(records, dmpFilePath: null, NullConversionProgressSink.Instance);

        // After prefix inference, all four siblings live under meshes\.
        Assert.Contains("meshes\\characters\\hair\\hairspikey.nif", paths);
        Assert.Contains("meshes\\characters\\hair\\hairspikey.egm", paths);
        Assert.Contains("meshes\\characters\\hair\\hairspikey.egt", paths);
        Assert.Contains("meshes\\characters\\hair\\hairspikey.tri", paths);
    }

    [Fact]
    public void Collect_FromDmp_FindsEmbeddedAssetPaths()
    {
        // Write a tiny binary that embeds three null-terminated asset paths plus some
        // junk bytes. The collector should surface only the path-shaped, asset-extension ones.
        using var tempFile = new TempFile();
        var payload = new List<byte>();

        void AppendNullTerm(string s)
        {
            payload.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
            payload.Add(0);
        }

        AppendNullTerm("meshes\\embedded\\hidden.nif");      // should be picked up
        AppendNullTerm("not an asset path, just a sentence."); // ignored — no asset ext
        payload.AddRange([0x01, 0x02, 0x03, 0xFF, 0x00]);    // binary noise
        AppendNullTerm("textures\\embed_test.dds");          // should be picked up
        AppendNullTerm("some_unrelated_string.txt");         // ignored — not an asset ext

        File.WriteAllBytes(tempFile.Path, payload.ToArray());

        var records = new RecordCollection();
        var paths = AssetPathCollector.Collect(records, tempFile.Path, NullConversionProgressSink.Instance);

        Assert.Contains("meshes\\embedded\\hidden.nif", paths);
        Assert.Contains("textures\\embed_test.dds", paths);
        Assert.DoesNotContain("some_unrelated_string.txt", paths);
    }

    [Fact]
    public void NormalizePath_StripsLeadingSeparatorsAndDataPrefix()
    {
        Assert.Equal("meshes\\a.nif", AssetPathCollector.NormalizePath("\\Data\\Meshes/a.nif"));
        Assert.Equal("meshes\\a.nif", AssetPathCollector.NormalizePath("/data/meshes/a.nif"));
        Assert.Equal("meshes\\a.nif", AssetPathCollector.NormalizePath("Meshes\\a.nif"));
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"asset-pack-test-{Guid.NewGuid():N}.bin");

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
