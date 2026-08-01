using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatResourceSessionPlanTests
{
    [Test]
    public void RejectsResourceRecipeWhoseIdentityDoesNotMatchManifest()
    {
        var tempRoot = CreateTempRoot("identity");
        var modFolder = Path.Combine(tempRoot, "ExpectedMod");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, "DifferentMod", bundlePath, sha);
        var manifest = CreateManifest(modFolder, "ExpectedMod");

        try
        {
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.False);
            Assert.That(PcCompatResourceRecipeRuntime.GetPlan(manifest.Id), Is.Null);
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void RevalidatesCandidateHashImmediatelyBeforeCallingLoadSink()
    {
        var tempRoot = CreateTempRoot("hash-recheck");
        var modFolder = Path.Combine(tempRoot, "Sample");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, "Sample", bundlePath, sha);
        var manifest = CreateManifest(modFolder, "Sample");
        var previousGate = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        var sinkCalls = 0;

        try
        {
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            File.WriteAllBytes(bundlePath, [0x00, 0x01, 0x02, 0x03]);
            Environment.SetEnvironmentVariable(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable, "1");
            PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(
                (modId, candidateSha, path) =>
                {
                    sinkCalls++;
                    return new PcCompatResourceLoadResult
                    {
                        Success = true,
                        ModId = modId,
                        CandidateSha256Hex = candidateSha,
                        Path = path
                    };
                },
                (_, _) => { });

            var result = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("mismatch").IgnoreCase);
            Assert.That(sinkCalls, Is.Zero, "unverified bytes must never cross the UnityMain load boundary");
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previousGate);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void FailedCandidateLoadIsMemoizedUntilSessionUnload()
    {
        var tempRoot = CreateTempRoot("memoized-failure");
        var modFolder = Path.Combine(tempRoot, "Sample");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, "Sample", bundlePath, sha);
        var manifest = CreateManifest(modFolder, "Sample");
        var previousGate = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        var sinkCalls = 0;
        var unloadCalls = 0;

        try
        {
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Environment.SetEnvironmentVariable(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable, "1");
            PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(
                (modId, candidateSha, path) =>
                {
                    sinkCalls++;
                    return new PcCompatResourceLoadResult
                    {
                        Success = false,
                        ModId = modId,
                        CandidateSha256Hex = candidateSha,
                        Path = path,
                        Error = "controlled trial failed"
                    };
                },
                (_, _) => unloadCalls++);

            var first = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            var second = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(first.Success, Is.False);
            Assert.That(first.CacheHit, Is.False);
            Assert.That(second.Success, Is.False);
            Assert.That(second.CacheHit, Is.True);
            Assert.That(sinkCalls, Is.EqualTo(1), "a failed candidate must not be retried until the MOD session is reloaded");
            Assert.That(
                PcCompatResourceRecipeRuntime.GetPlan(manifest.Id)!.Candidates.Single().Status,
                Is.EqualTo(PcCompatResourceCandidateStatus.LoadFailed));
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            Assert.That(unloadCalls, Is.Zero, "failed candidates were never loaded and must not reach the unload sink");
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previousGate);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void BuildsPlatformGatedPlanForPublishedJipperRecipe()
    {
        var modFolder = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..",
            "JipperResourcePack_release"));
        var recipePath = Path.Combine(modFolder, ".pccompat", "resource_recipe.bin");
        if (!File.Exists(recipePath))
            Assert.Ignore("Jipper resource_recipe.bin is not published in this workspace.");

        Assert.That(PcCompatResourceRecipe.TryRead(recipePath, out var document, out var error), Is.True, error);
        var manifest = new PcModManifest
        {
            FolderPath = modFolder,
            Id = document.ModId,
            DisplayName = document.ModId,
            Author = "test",
            Version = "0",
            AssemblyName = "JipperResourcePack.dll",
            EntryMethod = "JipperResourcePack.Main.Load",
            Kind = PcModKind.JAMod
        };

        var plan = PcCompatResourceRecipeRuntime.BuildSessionPlan(manifest, document);
        Assert.That(plan.ModId, Is.EqualTo(document.ModId));
        Assert.That(plan.FeatureGroups.Select(group => group.Id), Does.Contain("overlay.progress_bar"));
        Assert.That(plan.FeatureGroups.Select(group => group.Id), Does.Contain("overlay.font"));
        Assert.That(plan.Candidates.Any(candidate =>
                candidate.PlatformHint is "Linux" or "Windows" or "Mac" &&
                !candidate.AutoLoadAllowed),
            Is.True,
            "desktop bundles must remain controlled even when built with Unity 6000.3.x");
        Assert.That(plan.Candidates.Any(candidate =>
                candidate.FileName.Contains("2022", StringComparison.OrdinalIgnoreCase) &&
                !candidate.AutoLoadAllowed),
            Is.True);

        // Controlled desktop candidates stay fail-closed even before the UnityMain sink boundary.
        var controlled = plan.Candidates.First(candidate =>
            candidate.PlatformHint == "Linux" && !candidate.AutoLoadAllowed);
        // Plan is not registered into the runtime session here; explicit ensure needs TryLoadForMod.
        Assert.That(
            PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(document.ModId, controlled.Sha256Hex).Success,
            Is.False);
    }

    [Test]
    public void RejectedPolicyCandidatesAreNotAutoLoadable()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var document = new PcCompatResourceRecipeDocument
        {
            ModId = "Sample",
            RecipeId = PcCompatResourceRecipe.IndexedBundleRecipeId,
            Compatibility = "partial",
            TargetUnityVersion = "6000.3.10f1",
            Candidates =
            [
                new PcCompatResourceCandidate
                {
                    SourcePath = "C:/missing/bundle",
                    FileName = "bundle",
                    PlatformHint = "Windows",
                    UnityVersion = "2022.3.62f2",
                    VersionGate = "ForcedOnly",
                    LoadPolicy = "Rejected",
                    Sha256Hex = sha,
                    IndexSucceeded = true
                }
            ],
            FeatureGroups =
            [
                new PcCompatResourceFeatureGroup
                {
                    Id = "bundle.primary",
                    DisplayName = "Primary",
                    SelectedCandidateSha256Hex = sha,
                    SelectedPlatform = "Windows",
                    LoadPolicy = "Rejected",
                    AssetNames = ["ProgressBar"]
                }
            ]
        };
        var manifest = new PcModManifest
        {
            FolderPath = Path.GetTempPath(),
            Id = "Sample",
            DisplayName = "Sample",
            Author = "test",
            Version = "0",
            AssemblyName = "Sample.dll",
            EntryMethod = "Sample.Main.Load",
            Kind = PcModKind.UnityModManager
        };

        var plan = PcCompatResourceRecipeRuntime.BuildSessionPlan(manifest, document);
        Assert.That(plan.Candidates, Has.Count.EqualTo(1));
        Assert.That(plan.Candidates[0].AutoLoadAllowed, Is.False);
        Assert.That(plan.Candidates[0].Status, Is.EqualTo(PcCompatResourceCandidateStatus.Rejected));
    }

    [Test]
    public void PrefersCompiledResourcesDirectoryOverMissingSourcePath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pccompat-resource-plan-" + Guid.NewGuid().ToString("N"));
        var modFolder = Path.Combine(tempRoot, "SampleMod");
        var compiledResources = Path.Combine(tempRoot, "compiled", "Sample", "cachekey", "resources");
        Directory.CreateDirectory(modFolder);
        Directory.CreateDirectory(compiledResources);

        byte[] cachedBytes = [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00];
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(cachedBytes)).ToLowerInvariant();
        var cachedBundle = Path.Combine(compiledResources, sha[..16] + "_jipperresourcepackbundle");
        File.WriteAllBytes(cachedBundle, cachedBytes);

        try
        {
            var document = new PcCompatResourceRecipeDocument
            {
                ModId = "Sample",
                RecipeId = PcCompatResourceRecipe.IndexedBundleRecipeId,
                Compatibility = "partial",
                TargetUnityVersion = "6000.3.10f1",
                Candidates =
                [
                    new PcCompatResourceCandidate
                    {
                        SourcePath = Path.Combine(modFolder, "missing-original-bundle"),
                        FileName = "jipperresourcepackbundle",
                        PlatformHint = "Android",
                        UnityVersion = "6000.3.10f1",
                        VersionGate = "Auto",
                        LoadPolicy = "AutoLoad",
                        Sha256Hex = sha,
                        IndexSucceeded = true
                    }
                ],
                FeatureGroups =
                [
                    new PcCompatResourceFeatureGroup
                    {
                        Id = "overlay.progress_bar",
                        DisplayName = "ProgressBar",
                        SelectedCandidateSha256Hex = sha,
                        SelectedPlatform = "Android",
                        LoadPolicy = "AutoLoad",
                        AssetNames = ["ProgressBar"]
                    }
                ]
            };
            var manifest = new PcModManifest
            {
                FolderPath = modFolder,
                Id = "Sample",
                DisplayName = "Sample",
                Author = "test",
                Version = "0",
                AssemblyName = "Sample.dll",
                EntryMethod = "Sample.Main.Load",
                Kind = PcModKind.UnityModManager
            };

            var plan = PcCompatResourceRecipeRuntime.BuildSessionPlan(
                manifest,
                document,
                compiledResources);
            Assert.That(plan.CompiledResourcesDirectory, Is.EqualTo(Path.GetFullPath(compiledResources)));
            Assert.That(plan.Candidates, Has.Count.EqualTo(1));
            Assert.That(plan.Candidates[0].Status, Is.EqualTo(PcCompatResourceCandidateStatus.Ready));
            Assert.That(plan.Candidates[0].AutoLoadAllowed, Is.True);
            Assert.That(plan.Candidates[0].ResolvedPath, Is.EqualTo(Path.GetFullPath(cachedBundle)));
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

    [Test]
    public void RuntimeLoadRemainsDisabledUnlessEnvironmentGateIsSet()
    {
        var previous = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                null);
            Assert.That(PcCompatResourceRecipeRuntime.IsRuntimeLoadEnabled(), Is.False);

            var result = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(
                "Sample",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable));

            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                "1");
            Assert.That(PcCompatResourceRecipeRuntime.IsRuntimeLoadEnabled(), Is.True);

            // Even when the gate is open, missing session plan still fail-closes.
            result = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(
                "SampleMissingPlan",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("session plan"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previous);
        }
    }

    [Test]
    public void ReadinessSummaryIsReadOnlyAndDoesNotRequireLoadSink()
    {
        var summary = PcCompatResourceRecipeRuntime.GetReadinessSummary("NoSuchMod");
        Assert.That(summary.ModId, Is.EqualTo("NoSuchMod"));
        Assert.That(summary.RecipeLoaded, Is.False);
        Assert.That(summary.FeatureGroupCount, Is.EqualTo(0));
        Assert.That(summary.CandidateCount, Is.EqualTo(0));
        Assert.That(summary.FeatureGroupIds, Is.Empty);
    }

    [Test]
    public void TryLoadForModReturnsFalseWhenRecipeFileIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pccompat-missing-recipe-" + Guid.NewGuid().ToString("N"));
        var modFolder = Path.Combine(tempRoot, "EmptyMod");
        Directory.CreateDirectory(modFolder);
        try
        {
            var manifest = new PcModManifest
            {
                FolderPath = modFolder,
                Id = "EmptyMod",
                DisplayName = "EmptyMod",
                Author = "test",
                Version = "0",
                AssemblyName = "EmptyMod.dll",
                EntryMethod = "EmptyMod.Main.Load",
                Kind = PcModKind.UnityModManager
            };

            Assert.That(
                PcCompatResourceRecipeRuntime.TryLoadForMod(manifest),
                Is.False);
            Assert.That(PcCompatResourceRecipeRuntime.GetPlan(manifest.Id), Is.Null);
            Assert.That(PcCompatResourceRecipeRuntime.GetReadinessSummary(manifest.Id).RecipeLoaded, Is.False);
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

    [Test]
    public void TryLoadForModPublishesReadinessWithoutLoadingBundles()
    {
        var modFolder = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..",
            "JipperResourcePack_release"));
        var recipePath = Path.Combine(modFolder, ".pccompat", "resource_recipe.bin");
        if (!File.Exists(recipePath))
            Assert.Ignore("Jipper resource_recipe.bin is not published in this workspace.");

        Assert.That(
            PcCompatResourceRecipe.TryRead(recipePath, out var document, out var error),
            Is.True,
            error);

        var previous = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                null);

            var manifest = new PcModManifest
            {
                FolderPath = modFolder,
                Id = document.ModId,
                DisplayName = document.ModId,
                Author = "test",
                Version = "0",
                AssemblyName = "JipperResourcePack.dll",
                EntryMethod = "JipperResourcePack.Main.Load",
                Kind = PcModKind.JAMod
            };

            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            var summary = PcCompatResourceRecipeRuntime.GetReadinessSummary(manifest.Id);
            Assert.That(summary.RecipeLoaded, Is.True);
            Assert.That(summary.Compatibility, Is.EqualTo("partial"));
            Assert.That(summary.FeatureGroupCount, Is.GreaterThanOrEqualTo(4));
            Assert.That(summary.ReadyCandidateCount, Is.Zero);
            Assert.That(summary.ControlledCandidateCount, Is.EqualTo(3));
            Assert.That(summary.RuntimeLoadEnabled, Is.False);
            Assert.That(summary.FeatureGroupIds, Does.Contain("overlay.progress_bar"));
            Assert.That(summary.FeatureGroupIds, Does.Contain("overlay.font"));

            // Default gate still blocks ensure, so no AssetBundle side effects.
            var plan = PcCompatResourceRecipeRuntime.GetPlan(manifest.Id);
            Assert.That(plan, Is.Not.Null);
            var controlled = plan!.Candidates.First(candidate =>
                candidate.PlatformHint == "Linux" && !candidate.AutoLoadAllowed);
            var ensure = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, controlled.Sha256Hex);
            Assert.That(ensure.Success, Is.False);
            Assert.That(ensure.Error, Does.Contain(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable));

            var featureEnsure = PcCompatResourceRecipeRuntime.TryEnsureFeatureGroupLoaded(
                manifest.Id,
                "overlay.progress_bar");
            Assert.That(featureEnsure.Success, Is.False);
            Assert.That(featureEnsure.Error, Does.Contain(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable));
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(document.ModId);
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previous);
        }
    }

    [Test]
    public void ControlledCandidateRequiresSessionBoundAuthorization()
    {
        var tempRoot = CreateTempRoot("controlled-authorization");
        var modFolder = Path.Combine(tempRoot, "Sample");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, "Sample", bundlePath, sha, "Linux", "Controlled", "ControlledLoad");
        var manifest = CreateManifest(modFolder, "Sample");
        var previousGate = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        var sinkCalls = 0;

        try
        {
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Environment.SetEnvironmentVariable(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable, "1");
            PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(
                request =>
                {
                    sinkCalls++;
                    return new PcCompatResourceLoadResult
                    {
                        Success = true,
                        ModId = request.ModId,
                        CandidateSha256Hex = request.CandidateSha256Hex,
                        Path = request.Path,
                        SessionGeneration = request.SessionGeneration
                    };
                },
                _ => { });

            var blocked = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(blocked.Success, Is.False);
            Assert.That(blocked.Error, Does.Contain("confirmation").IgnoreCase);
            Assert.That(sinkCalls, Is.Zero);

            Assert.That(
                PcCompatResourceRecipeRuntime.TryAuthorizeCandidateLoad(
                    manifest.Id,
                    sha,
                    PcCompatResourceLoadAuthorization.Controlled,
                    out var authorizationError),
                Is.True,
                authorizationError);
            var loaded = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(loaded.Success, Is.True, loaded.Error);
            Assert.That(sinkCalls, Is.EqualTo(1));

            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            blocked = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(blocked.Success, Is.False, "authorization must not survive a session reload");
            Assert.That(blocked.Error, Does.Contain("confirmation").IgnoreCase);
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previousGate);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void ForceRequiredCandidateRejectsControlledAuthorization()
    {
        var tempRoot = CreateTempRoot("forced-authorization");
        var modFolder = Path.Combine(tempRoot, "Sample");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, "Sample", bundlePath, sha, "Windows", "ForcedOnly", "ForceRequired");
        var manifest = CreateManifest(modFolder, "Sample");

        try
        {
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Assert.That(
                PcCompatResourceRecipeRuntime.TryAuthorizeCandidateLoad(
                    manifest.Id,
                    sha,
                    PcCompatResourceLoadAuthorization.Controlled,
                    out var controlledError),
                Is.False);
            Assert.That(controlledError, Does.Contain("forced").IgnoreCase);
            Assert.That(
                PcCompatResourceRecipeRuntime.TryAuthorizeCandidateLoad(
                    manifest.Id,
                    sha,
                    PcCompatResourceLoadAuthorization.Forced,
                    out var forcedError),
                Is.True,
                forcedError);
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void QueuedLoadCompletesOnlyForMatchingSessionGeneration()
    {
        var tempRoot = CreateTempRoot("async-completion");
        var modFolder = Path.Combine(tempRoot, "Sample");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, "Sample", bundlePath, sha);
        var manifest = CreateManifest(modFolder, "Sample");
        var previousGate = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        PcCompatResourceLoadRequest? queuedRequest = null;
        var unloads = new List<PcCompatResourceUnloadRequest>();

        try
        {
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Environment.SetEnvironmentVariable(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable, "1");
            PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(
                request =>
                {
                    queuedRequest = request;
                    return new PcCompatResourceLoadResult
                    {
                        Success = false,
                        Pending = true,
                        ModId = request.ModId,
                        CandidateSha256Hex = request.CandidateSha256Hex,
                        Path = request.Path,
                        SessionGeneration = request.SessionGeneration
                    };
                },
                request => unloads.Add(request));

            var queued = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(queued.Pending, Is.True);
            Assert.That(queuedRequest, Is.Not.Null);
            Assert.That(
                PcCompatResourceRecipeRuntime.GetPlan(manifest.Id)!.Candidates.Single().Status,
                Is.EqualTo(PcCompatResourceCandidateStatus.LoadQueued));

            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            var staleResult = new PcCompatResourceLoadResult
            {
                Success = true,
                ModId = manifest.Id,
                CandidateSha256Hex = sha,
                Path = bundlePath,
                SessionGeneration = queuedRequest!.SessionGeneration
            };
            Assert.That(
                PcCompatResourceRecipeRuntime.CompleteBundleLoad(queuedRequest, staleResult),
                Is.False);
            Assert.That(unloads, Has.Count.EqualTo(1));
            Assert.That(unloads[0].SessionGeneration, Is.EqualTo(queuedRequest.SessionGeneration));
            Assert.That(
                PcCompatResourceRecipeRuntime.GetPlan(manifest.Id)!.Candidates.Single().Status,
                Is.EqualTo(PcCompatResourceCandidateStatus.Ready));
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previousGate);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void RetryableSchedulerFailureDoesNotPoisonSessionAttemptCache()
    {
        var tempRoot = CreateTempRoot("retryable-scheduler");
        var modFolder = Path.Combine(tempRoot, "Sample");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, "Sample", bundlePath, sha);
        var manifest = CreateManifest(modFolder, "Sample");
        var previousGate = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        var calls = 0;

        try
        {
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Environment.SetEnvironmentVariable(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable, "1");
            PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(
                request =>
                {
                    calls++;
                    return new PcCompatResourceLoadResult
                    {
                        Success = calls > 1,
                        Retryable = calls == 1,
                        ModId = request.ModId,
                        CandidateSha256Hex = request.CandidateSha256Hex,
                        Path = request.Path,
                        Error = calls == 1 ? "UnityMain hook not ready" : null,
                        SessionGeneration = request.SessionGeneration
                    };
                },
                _ => { });

            var first = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(first.Success, Is.False);
            Assert.That(first.Retryable, Is.True);
            Assert.That(
                PcCompatResourceRecipeRuntime.GetPlan(manifest.Id)!.Candidates.Single().Status,
                Is.EqualTo(PcCompatResourceCandidateStatus.Ready));

            var second = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(second.Success, Is.True, second.Error);
            Assert.That(calls, Is.EqualTo(2));
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previousGate);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void MatchingQueuedCompletionPublishesLoadedStateAndCacheHit()
    {
        var tempRoot = CreateTempRoot("matching-completion");
        var modFolder = Path.Combine(tempRoot, "Sample");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, "Sample", bundlePath, sha);
        var manifest = CreateManifest(modFolder, "Sample");
        var previousGate = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        PcCompatResourceLoadRequest? queuedRequest = null;
        var sinkCalls = 0;
        var refreshCalls = 0;
        var unloads = new List<PcCompatResourceUnloadRequest>();

        try
        {
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Environment.SetEnvironmentVariable(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable, "1");
            PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(
                request =>
                {
                    sinkCalls++;
                    queuedRequest = request;
                    return new PcCompatResourceLoadResult
                    {
                        Success = false,
                        Pending = true,
                        ModId = request.ModId,
                        CandidateSha256Hex = request.CandidateSha256Hex,
                        Path = request.Path,
                        SessionGeneration = request.SessionGeneration
                    };
                },
                request => unloads.Add(request));
            PcCompatResourceRecipeRuntime.RegisterResourceConsumerRefreshSink(() => refreshCalls++);

            var queued = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(queued.Pending, Is.True);
            Assert.That(queuedRequest, Is.Not.Null);
            Assert.That(queuedRequest!.ExpectedFileSize, Is.EqualTo(new FileInfo(bundlePath).Length));
            Assert.That(PcCompatResourceRecipeRuntime.GetReadinessSummary(manifest.Id).QueuedCandidateCount, Is.EqualTo(1));

            var completed = new PcCompatResourceLoadResult
            {
                Success = true,
                ModId = manifest.Id,
                CandidateSha256Hex = sha,
                Path = bundlePath,
                SessionGeneration = queuedRequest.SessionGeneration
            };
            Assert.That(PcCompatResourceRecipeRuntime.CompleteBundleLoad(queuedRequest, completed), Is.True);
            var summary = PcCompatResourceRecipeRuntime.GetReadinessSummary(manifest.Id);
            Assert.That(summary.QueuedCandidateCount, Is.Zero);
            Assert.That(summary.LoadedCandidateCount, Is.EqualTo(1));
            Assert.That(refreshCalls, Is.EqualTo(1), "successful completion must wake waiting resource consumers");

            var cached = PcCompatResourceRecipeRuntime.TryEnsureCandidateLoaded(manifest.Id, sha);
            Assert.That(cached.Success, Is.True);
            Assert.That(cached.CacheHit, Is.True);
            Assert.That(sinkCalls, Is.EqualTo(1));
            Assert.That(refreshCalls, Is.EqualTo(2), "a cached success must also wake consumers that were gated earlier");

            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            Assert.That(unloads, Has.Count.EqualTo(1));
            Assert.That(unloads[0].SessionGeneration, Is.EqualTo(queuedRequest.SessionGeneration));
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
            PcCompatResourceRecipeRuntime.ClearResourceConsumerRefreshSink();
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previousGate);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void ResolvesOnlyCurrentLoadedHighConfidenceBinding()
    {
        var tempRoot = CreateTempRoot("loaded-binding");
        var modFolder = Path.Combine(tempRoot, "Sample");
        Directory.CreateDirectory(modFolder);
        var bundlePath = Path.Combine(modFolder, "bundle");
        File.WriteAllBytes(bundlePath, [0x55, 0x6e, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00]);
        var sha = Sha256(bundlePath);
        var recipePath = Path.Combine(modFolder, "resource_recipe.bin");
        var manifest = CreateManifest(modFolder, "Sample");
        var previousGate = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);

        try
        {
            WriteResourceRecipe(
                recipePath,
                manifest.Id,
                bundlePath,
                sha,
                featureGroupId: "overlay.font",
                bindingAssetName: "MAPLESTORY_OTF_BOLD SDF",
                bindingExpectedType: "TMP_FontAsset",
                bindingConfidence: "Proven");
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Environment.SetEnvironmentVariable(PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable, "1");
            PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(
                request => new PcCompatResourceLoadResult
                {
                    Success = true,
                    ModId = request.ModId,
                    CandidateSha256Hex = request.CandidateSha256Hex,
                    Path = request.Path,
                    SessionGeneration = request.SessionGeneration
                },
                _ => { });

            Assert.That(
                PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                    manifest.Id,
                    "overlay.font",
                    "TMP_FontAsset",
                    out _),
                Is.False,
                "a recipe binding is not consumable before its selected candidate loads");

            Assert.That(
                PcCompatResourceRecipeRuntime.TryEnsureFeatureGroupLoaded(manifest.Id, "overlay.font").Success,
                Is.True);
            Assert.That(
                PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                    manifest.Id,
                    "overlay.font",
                    "TMP_FontAsset",
                    out var first),
                Is.True);
            Assert.That(first.AssetName, Is.EqualTo("MAPLESTORY_OTF_BOLD SDF"));
            Assert.That(first.CandidateSha256Hex, Is.EqualTo(sha));

            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Assert.That(
                PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                    manifest.Id,
                    "overlay.font",
                    "TMP_FontAsset",
                    out _),
                Is.False,
                "a previous session's successful load must not authorize a new session");

            Assert.That(
                PcCompatResourceRecipeRuntime.TryEnsureFeatureGroupLoaded(manifest.Id, "overlay.font").Success,
                Is.True);
            Assert.That(
                PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                    manifest.Id,
                    "overlay.font",
                    "TMP_FontAsset",
                    out var second),
                Is.True);
            Assert.That(second.SessionGeneration, Is.GreaterThan(first.SessionGeneration));

            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            WriteResourceRecipe(
                recipePath,
                manifest.Id,
                bundlePath,
                sha,
                featureGroupId: "overlay.font",
                bindingAssetName: "MAPLESTORY_OTF_BOLD SDF",
                bindingExpectedType: "TMP_FontAsset",
                bindingConfidence: "SemanticMatch");
            Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
            Assert.That(
                PcCompatResourceRecipeRuntime.TryEnsureFeatureGroupLoaded(manifest.Id, "overlay.font").Success,
                Is.True);
            Assert.That(
                PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                    manifest.Id,
                    "overlay.font",
                    "TMP_FontAsset",
                    out _),
                Is.False,
                "semantic/fuzzy bindings require an explicit consumer and cannot auto-bind");
        }
        finally
        {
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
            Environment.SetEnvironmentVariable(
                PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
                previousGate);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempRoot(string suffix)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "pccompat-resource-" + suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static PcModManifest CreateManifest(string modFolder, string modId)
        => new()
        {
            FolderPath = modFolder,
            Id = modId,
            DisplayName = modId,
            Author = "test",
            Version = "0",
            AssemblyName = modId + ".dll",
            EntryMethod = modId + ".Main.Load",
            Kind = PcModKind.UnityModManager
        };

    private static string Sha256(string path)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void WriteResourceRecipe(
        string path,
        string modId,
        string bundlePath,
        string sha256Hex,
        string platformHint = "Android",
        string versionGate = "Auto",
        string loadPolicy = "AutoLoad",
        string featureGroupId = "bundle.primary",
        string? bindingAssetName = null,
        string? bindingExpectedType = null,
        string? bindingConfidence = null)
    {
        var fileName = Path.GetFileName(bundlePath);
        var fileSize = new FileInfo(bundlePath).Length;
        var assetNames = bindingAssetName == null
            ? "[]"
            : System.Text.Json.JsonSerializer.Serialize(new[] { bindingAssetName });
        var bindings = bindingAssetName == null
            ? "[]"
            : System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new
                {
                    featureGroupId,
                    assetName = bindingAssetName,
                    expectedType = bindingExpectedType ?? "Object",
                    confidence = bindingConfidence ?? "Proven",
                    reason = "runtime consumption test"
                }
            });
        var json = $$"""
        {
          "modId":{{System.Text.Json.JsonSerializer.Serialize(modId)}},
          "recipeId":"xphorror.resource.indexed_bundle.v1",
          "compatibility":"partial",
          "targetUnityVersion":"6000.3.10f1",
          "candidates":[{
            "sourcePath":{{System.Text.Json.JsonSerializer.Serialize(bundlePath)}},
            "fileName":{{System.Text.Json.JsonSerializer.Serialize(fileName)}},
            "platformHint":{{System.Text.Json.JsonSerializer.Serialize(platformHint)}},
            "unityVersion":"6000.3.10f1",
            "versionGate":{{System.Text.Json.JsonSerializer.Serialize(versionGate)}},
            "loadPolicy":{{System.Text.Json.JsonSerializer.Serialize(loadPolicy)}},
            "fileSize":{{fileSize}},
            "sha256Hex":"{{sha256Hex}}",
            "hasEmbeddedTypeTree":true,
            "indexSucceeded":true,
            "directoryEntries":[],
            "assets":[],
            "warnings":[]
          }],
          "featureGroups":[{
            "id":{{System.Text.Json.JsonSerializer.Serialize(featureGroupId)}},
            "displayName":"Primary",
            "selectedCandidateSha256Hex":"{{sha256Hex}}",
            "selectedPlatform":{{System.Text.Json.JsonSerializer.Serialize(platformHint)}},
            "loadPolicy":{{System.Text.Json.JsonSerializer.Serialize(loadPolicy)}},
            "assetNames":{{assetNames}},
            "notes":[]
          }],
          "bindings":{{bindings}}
        }
        """;
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var header = new byte[PcCompatResourceRecipe.HeaderSize];
        System.Text.Encoding.ASCII.GetBytes("XPHRRESC").CopyTo(header, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), PcCompatResourceRecipe.SchemaVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), PcCompatResourceRecipe.HeaderSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), checked((uint)payload.Length));
        System.Security.Cryptography.SHA256.HashData(payload).CopyTo(header, 20);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(52, 4),
            checked((uint)(header.Length + payload.Length)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56, 4), Crc32(payload));
        using var stream = File.Create(path);
        stream.Write(header);
        stream.Write(payload);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; ++bit)
            {
                var mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }

        return ~crc;
    }
}
