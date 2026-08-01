using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatRecipeBundleResourceCacheTests
{
    [Test]
    public void UnsafeModIdCannotEscapeCompiledCacheRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pccompat-cache-path-" + Guid.NewGuid().ToString("N"));
        var modsRoot = Path.Combine(tempRoot, "mods");
        var modFolder = Path.Combine(modsRoot, "SampleMod");
        Directory.CreateDirectory(modFolder);
        try
        {
            var manifest = new PcModManifest
            {
                FolderPath = modFolder,
                Id = "..",
                DisplayName = "unsafe-id",
                Version = "1",
                AssemblyName = "Sample.dll",
                EntryMethod = "Sample.Main.Load",
                Kind = PcModKind.UnityModManager
            };
            var report = new PcCompatRecipeCompileReport
            {
                ModId = "..",
                RecipeId = "xphorror.recipe.verified_fixed_op.v1",
                Compatibility = "partial",
                Rules =
                [
                    new PcCompatCompiledRule
                    {
                        Id = "overlay.hide",
                        FeatureId = "overlay",
                        TargetType = "scrController",
                        TargetMethod = "QuitToMainMenu",
                        TargetIsStatic = false,
                        TargetReturnType = "System.Void",
                        TargetParameterTypes = Array.Empty<string>(),
                        Stage = PcCompatRuleStage.AfterOriginal,
                        Op = PcCompatRuleOp.OverlayHide,
                        RequiredCapabilities = PcCompatCapability.UiOverlay |
                                               PcCompatCapability.AfterOriginalObserve
                    }
                ]
            };

            var bundle = PcCompatRecipeBundleCache.Write(manifest, report);
            var compiledRoot = Path.GetFullPath(Path.Combine(modsRoot, "compiled"));
            var relative = Path.GetRelativePath(compiledRoot, bundle.BundleDirectory);
            Assert.That(Path.IsPathRooted(relative), Is.False);
            Assert.That(relative, Is.Not.EqualTo("..").And.Not.StartWith(".." + Path.DirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void WritesAutoLoadCandidatesIntoCompiledResourcesDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pccompat-bundle-cache-" + Guid.NewGuid().ToString("N"));
        var modsRoot = Path.Combine(tempRoot, "mods");
        var modFolder = Path.Combine(modsRoot, "SampleMod");
        var pccompatDir = Path.Combine(modFolder, ".pccompat");
        Directory.CreateDirectory(pccompatDir);

        var sourceBundle = Path.Combine(modFolder, "jipperresourcepackbundle");
        File.WriteAllBytes(sourceBundle, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourceBundle))).ToLowerInvariant();

        // Minimal valid resource_recipe.bin via the import-time emitter path is heavy here.
        // Instead write a tiny hand-crafted recipe with string enums using the public reader-compatible format.
        var recipeJson =
            $$"""
            {
              "modId":"SampleMod",
              "recipeId":"xphorror.resource.indexed_bundle.v1",
              "compatibility":"partial",
              "targetUnityVersion":"6000.3.10f1",
              "candidates":[
                {
                  "sourcePath":{{System.Text.Json.JsonSerializer.Serialize(sourceBundle)}},
                  "fileName":"jipperresourcepackbundle",
                  "platformHint":"Android",
                  "unityVersion":"6000.3.10f1",
                  "versionGate":"Auto",
                  "loadPolicy":"AutoLoad",
                  "fileSize":8,
                  "sha256Hex":"{{sha}}",
                  "hasEmbeddedTypeTree":true,
                  "indexSucceeded":true,
                  "directoryEntries":[],
                  "assets":[{"name":"ProgressBar","typeName":"GameObject","pathId":1,"typeId":1,"container":"","assetsFileName":""}],
                  "warnings":[]
                },
                {
                  "sourcePath":{{System.Text.Json.JsonSerializer.Serialize(Path.Combine(modFolder, "jipperresourcepackbundle2022"))}},
                  "fileName":"jipperresourcepackbundle2022",
                  "platformHint":"Linux",
                  "unityVersion":"2022.3.62f2",
                  "versionGate":"ForcedOnly",
                  "loadPolicy":"Rejected",
                  "fileSize":8,
                  "sha256Hex":"1111111111111111111111111111111111111111111111111111111111111111",
                  "hasEmbeddedTypeTree":true,
                  "indexSucceeded":true,
                  "directoryEntries":[],
                  "assets":[],
                  "warnings":[]
                }
              ],
              "featureGroups":[
                {
                  "id":"overlay.progress_bar",
                  "displayName":"ProgressBar",
                  "selectedCandidateSha256Hex":"{{sha}}",
                  "selectedPlatform":"Android",
                  "loadPolicy":"AutoLoad",
                  "assetNames":["ProgressBar"],
                  "notes":[]
                }
              ],
              "bindings":[
                {
                  "featureGroupId":"overlay.progress_bar",
                  "assetName":"ProgressBar",
                  "expectedType":"GameObject",
                  "confidence":"Proven",
                  "reason":"test"
                }
              ]
            }
            """;
        WriteResourceRecipeBin(Path.Combine(pccompatDir, "resource_recipe.bin"), recipeJson);
        File.WriteAllText(Path.Combine(pccompatDir, "resource_report.json"), recipeJson);

        try
        {
            var manifest = new PcModManifest
            {
                FolderPath = modFolder,
                Id = "SampleMod",
                DisplayName = "SampleMod",
                Author = "test",
                Version = "0.0.1",
                AssemblyName = "SampleMod.dll",
                EntryMethod = "SampleMod.Main.Load",
                Kind = PcModKind.UnityModManager
            };
            var report = new PcCompatRecipeCompileReport
            {
                ModId = "SampleMod",
                RecipeId = "xphorror.recipe.verified_fixed_op.v1",
                Compatibility = "partial",
                Rules =
                [
                    new PcCompatCompiledRule
                    {
                        Id = "domain.overlay.game_start",
                        FeatureId = "overlay",
                        TargetType = "scnGame",
                        TargetMethod = "Play",
                        TargetIsStatic = false,
                        TargetReturnType = "System.Boolean",
                        TargetParameterTypes = ["System.Int32", "System.Boolean"],
                        Stage = PcCompatRuleStage.AfterOriginal,
                        Op = PcCompatRuleOp.OverlayShow,
                        RequiredCapabilities = PcCompatCapability.UiOverlay | PcCompatCapability.AfterOriginalObserve
                    }
                ]
            };

            var bundle = PcCompatRecipeBundleCache.Write(manifest, report);
            Assert.That(bundle.ResourceRecipePath, Is.Not.Null.And.Not.Empty);
            Assert.That(File.Exists(bundle.ResourceRecipePath!), Is.True);
            Assert.That(bundle.ResourceDirectory, Is.Not.Null.And.Not.Empty);
            Assert.That(Directory.Exists(bundle.ResourceDirectory!), Is.True);

            var copied = Directory.GetFiles(bundle.ResourceDirectory!)
                .Where(path => !path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.That(copied, Has.Length.EqualTo(1));
            Assert.That(Path.GetFileName(copied[0]), Does.StartWith(sha + "_"));
            Assert.That(File.ReadAllBytes(copied[0]), Is.EqualTo(File.ReadAllBytes(sourceBundle)));
            Assert.That(File.Exists(Path.Combine(bundle.ResourceDirectory!, "manifest.json")), Is.True);

            // Rejected candidates must not be copied.
            Assert.That(copied.Any(path => path.Contains("2022", StringComparison.OrdinalIgnoreCase)), Is.False);

            Assert.That(
                PcCompatResourceRecipe.TryRead(bundle.ResourceRecipePath!, out var document, out var error),
                Is.True,
                error ?? "resource recipe read failed");
            var plan = PcCompatResourceRecipeRuntime.BuildSessionPlan(
                manifest,
                document,
                bundle.ResourceDirectory);
            Assert.That(plan.Candidates.Single(c => c.Sha256Hex == sha).ResolvedPath, Is.EqualTo(Path.GetFullPath(copied[0])));

            File.WriteAllBytes(copied[0], [0x00, 0x01, 0x02]);
            var repaired = PcCompatRecipeBundleCache.Write(manifest, report);
            var repairedCandidate = Directory.GetFiles(repaired.ResourceDirectory!)
                .Single(candidate => !candidate.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
            Assert.That(
                File.ReadAllBytes(repairedCandidate),
                Is.EqualTo(File.ReadAllBytes(sourceBundle)),
                "a complete marker must not turn a corrupted resource candidate into a cache hit");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void WriteResourceRecipeBin(string path, string json)
    {
        // Mirror ResourceRecipeBinary header layout without depending on AssetsTools.
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var header = new byte[64];
        System.Text.Encoding.ASCII.GetBytes("XPHRRESC").CopyTo(header, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)64).CopyTo(header, 10);
        BitConverter.GetBytes(1u).CopyTo(header, 12);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(header, 16);
        System.Security.Cryptography.SHA256.HashData(payload).CopyTo(header, 20);
        BitConverter.GetBytes((uint)(64 + payload.Length)).CopyTo(header, 52);
        BitConverter.GetBytes(Crc32(payload)).CopyTo(header, 56);

        using var stream = File.Create(path);
        stream.Write(header);
        stream.Write(payload);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                var mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }
        return ~crc;
    }
}
