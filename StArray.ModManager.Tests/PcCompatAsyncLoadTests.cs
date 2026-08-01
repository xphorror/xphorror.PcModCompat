using Xphorror.PcModCompat;
using StArray.ModManager.Manager;
using StArray.ModManager.Resources;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

public class PcCompatAsyncLoadTests
{
    [TearDown]
    public void ClearManagedInstallContextProbe()
    {
        PcCompatRuntime.RegisterManagedInstallContextProbe(null);
        PcCompatRuntime.RegisterUnityMainThreadProbe(null);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(null);
    }

    [Test]
    public void AndroidInstallContextRejectsBackgroundFinalizationBeforeModCodeRuns()
    {
        var prepared = new PcCompatPreparedMod
        {
            Manifest = new PcModManifest
            {
                FolderPath = Path.GetTempPath(),
                Id = "thread-guard-fixture",
                DisplayName = "thread-guard-fixture"
            },
            StaticScan = new PcCompatStaticPatchScanReport
            {
                ModId = "thread-guard-fixture"
            },
            CallbackTranslation = new PcCompatCallbackTranslationReport
            {
                ModId = "thread-guard-fixture"
            },
            HasRecipe = false
        };
        PcCompatRuntime.RegisterManagedInstallContextProbe(() => false);

        var exception = Assert.Throws<InvalidOperationException>(
            () => PcCompatRuntime.RegisterPreparedMod(prepared));

        Assert.That(
            exception!.Message,
            Does.Contain("UnityMain finalization callback"));
    }

    [Test]
    public void ImportedJipperReachesLoadedStateAndKeepsSettingsPlugin()
    {
        var repoRoot = FindRepoRoot();
        var source = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(source), Is.True, $"missing fixture: {source}");

        var modsRoot = Path.Combine(Path.GetTempPath(), "pccompat-import-" + Guid.NewGuid().ToString("N"));
        var imported = Path.Combine(modsRoot, "JipperResourcePack");
        Directory.CreateDirectory(imported);
        try
        {
            foreach (var name in new[]
                     {
                         "Info.json",
                         "JAModInfo.json",
                         "JAMod.Bootstrap.dll",
                         "JipperResourcePack.dll"
                     })
            {
                File.Copy(Path.Combine(source, name), Path.Combine(imported, name));
            }

            var loader = new ModLoader(modsRoot);
            loader.ScanMods();
            var mod = loader.Mods.Single(entry => entry.Id == "JipperResourcePack");

            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
            Assert.That(mod.PluginInstance, Is.InstanceOf<IModSettings>());

            loader.ToggleMod(mod);
            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loading));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mod.LoadState == ModLoadState.Loading && DateTime.UtcNow < deadline)
            {
                loader.UpdatePendingLoads();
                Thread.Sleep(10);
            }

            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loaded), mod.LoadError);
            Assert.That(mod.PluginInstance, Is.InstanceOf<IModSettings>());
            Assert.That(PcCompatRuntime.GetRecipeReport(mod.Id), Is.Not.Null);

            loader.UnloadMod(mod);
            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
            Assert.That(mod.IsEnabled, Is.False);
            Assert.That(mod.PluginInstance, Is.InstanceOf<IModSettings>());
        }
        finally
        {
            Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void LoadImportedModStartsPcCompatLoadAndReturnsImportedEntry()
    {
        var repoRoot = FindRepoRoot();
        var source = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(source), Is.True, $"missing fixture: {source}");

        var modsRoot = Path.Combine(Path.GetTempPath(), "pccompat-import-load-" + Guid.NewGuid().ToString("N"));
        var imported = Path.Combine(modsRoot, "JipperResourcePack");
        Directory.CreateDirectory(imported);
        try
        {
            foreach (var name in new[]
                     {
                         "Info.json",
                         "JAModInfo.json",
                         "JAMod.Bootstrap.dll",
                         "JipperResourcePack.dll"
                     })
            {
                File.Copy(Path.Combine(source, name), Path.Combine(imported, name));
            }

            var loader = new ModLoader(modsRoot);
            var mod = loader.LoadImportedMod(imported);

            Assert.That(mod, Is.Not.Null);
            Assert.That(mod!.Id, Is.EqualTo("JipperResourcePack"));
            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loading));
            Assert.That(mod.IsEnabled, Is.True);
            Assert.That(mod.PluginInstance, Is.InstanceOf<IModSettings>());

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mod.LoadState == ModLoadState.Loading && DateTime.UtcNow < deadline)
            {
                loader.UpdatePendingLoads();
                Thread.Sleep(10);
            }

            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loaded), mod.LoadError);

            loader.UnloadMod(mod);
        }
        finally
        {
            Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    [NonParallelizable]
    public void AsyncLoadFinalizationRunsOnlyThroughConfiguredScheduler()
    {
        var repoRoot = FindRepoRoot();
        var source = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(source), Is.True, $"missing fixture: {source}");

        var modsRoot = Path.Combine(Path.GetTempPath(), "pccompat-scheduled-load-" + Guid.NewGuid().ToString("N"));
        var imported = Path.Combine(modsRoot, "JipperResourcePack");
        Directory.CreateDirectory(imported);
        try
        {
            foreach (var name in new[]
                     {
                         "Info.json",
                         "JAModInfo.json",
                         "JAMod.Bootstrap.dll",
                         "JipperResourcePack.dll"
                     })
            {
                File.Copy(Path.Combine(source, name), Path.Combine(imported, name));
            }

            Action? scheduled = null;
            var scheduleCalls = 0;
            var loader = new ModLoader(modsRoot);
            loader.SetPendingLoadCompletionScheduler(work =>
            {
                scheduleCalls++;
                scheduled = work;
                return true;
            });
            var mod = loader.LoadImportedMod(imported)!;
            var asyncPlugin = (IAsyncModPlugin)mod.PluginInstance!;

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!asyncPlugin.IsLoadReady && DateTime.UtcNow < deadline)
                Thread.Sleep(10);

            Assert.That(asyncPlugin.IsLoadReady, Is.True);
            Assert.That(loader.PendingAsyncLoadCount, Is.EqualTo(1));
            Assert.That(loader.RequestPendingLoadUpdate(), Is.True);
            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loading));
            Assert.That(scheduled, Is.Not.Null);
            Assert.That(loader.RequestPendingLoadUpdate(), Is.True);
            Assert.That(scheduleCalls, Is.EqualTo(1));

            scheduled!();

            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.Loaded), mod.LoadError);
            Assert.That(loader.PendingAsyncLoadCount, Is.Zero);
            loader.UnloadMod(mod);
        }
        finally
        {
            Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void RefreshImportedModDiscoversEntryWithoutStartingLoad()
    {
        var repoRoot = FindRepoRoot();
        var source = Path.Combine(repoRoot, "JipperResourcePack_release");
        Assume.That(Directory.Exists(source), Is.True, $"missing fixture: {source}");

        var modsRoot = Path.Combine(Path.GetTempPath(), "pccompat-import-refresh-" + Guid.NewGuid().ToString("N"));
        var imported = Path.Combine(modsRoot, "JipperResourcePack");
        Directory.CreateDirectory(imported);
        try
        {
            foreach (var name in new[]
                     {
                         "Info.json",
                         "JAModInfo.json",
                         "JAMod.Bootstrap.dll",
                         "JipperResourcePack.dll"
                     })
            {
                File.Copy(Path.Combine(source, name), Path.Combine(imported, name));
            }

            var loader = new ModLoader(modsRoot);
            var mod = loader.RefreshImportedMod(imported);

            Assert.That(mod, Is.Not.Null);
            Assert.That(mod!.Id, Is.EqualTo("JipperResourcePack"));
            Assert.That(mod.LoadState, Is.EqualTo(ModLoadState.NotLoaded));
            Assert.That(mod.IsEnabled, Is.False);
            Assert.That(mod.PluginInstance, Is.InstanceOf<IModSettings>());
        }
        finally
        {
            Directory.Delete(modsRoot, recursive: true);
        }
    }

    [Test]
    public void PrepareModReportsOrderedBackgroundStages()
    {
        var root = CreateTempMod();
        try
        {
            var manifest = CreateManifest(root);
            var progress = new List<(float Value, string Stage)>();

            var prepared = PcCompatRuntime.PrepareMod(
                manifest,
                (value, stage) => progress.Add((value, stage)));

            Assert.That(prepared.Manifest, Is.SameAs(manifest));
            Assert.That(progress.Select(item => item.Value), Is.Ordered);
            Assert.That(progress.Select(item => item.Stage), Is.EqualTo(new[]
            {
                "Scanning PATCH metadata",
                "Translating callbacks",
                "Rewriting managed assembly",
                "Compiling native rules",
                "Waiting for main-thread install"
            }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void PrepareModCarriesRegisteredManagedRewriteBundle()
    {
        var root = CreateTempMod();
        try
        {
            var manifest = CreateManifest(root);
            var rewrittenPath = Path.Combine(root, "compiled", "rewritten.dll");
            var expected = new PcCompatManagedAssemblyBundleInfo
            {
                CacheKey = "rewrite-key",
                BundleDirectory = Path.GetDirectoryName(rewrittenPath)!,
                InputAssemblyPath = manifest.EntryAssemblyPath,
                RewrittenAssemblyPath = rewrittenPath,
                ReportPath = Path.Combine(root, "rewrite-report.json"),
                CompleteMarkerPath = Path.Combine(root, "complete.marker"),
                CacheHit = true,
                RewrittenInstructions = 234,
                PassthroughInstructions = 22
            };
            PcCompatManagedAssemblyRewrite.RegisterProvider((candidate, scan, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.That(candidate, Is.SameAs(manifest));
                Assert.That(scan.ModId, Is.EqualTo(manifest.Id));
                return expected;
            });

            var prepared = PcCompatRuntime.PrepareMod(manifest);

            Assert.That(prepared.ManagedAssemblyBundle, Is.SameAs(expected));
        }
        finally
        {
            PcCompatManagedAssemblyRewrite.RegisterProvider(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void PrepareModHonorsPreCancelledToken()
    {
        var root = CreateTempMod();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                PcCompatRuntime.PrepareMod(CreateManifest(root), cancellationToken: cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ManagedRewriteFailureRemainsAnIndependentCapabilityError()
    {
        var root = CreateTempMod();
        try
        {
            var manifest = CreateManifest(root);
            PcCompatManagedAssemblyRewrite.RegisterProvider((candidate, scan, token) =>
            {
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("missing generated proxy surface");
            });

            var prepared = PcCompatRuntime.PrepareMod(manifest);

            Assert.That(prepared.ManagedAssemblyBundle, Is.Null);
            Assert.That(prepared.ManagedAssemblyError, Does.Contain("missing generated proxy surface"));
            Assert.That(prepared.StaticScan, Is.Not.Null);
            Assert.That(prepared.CallbackTranslation, Is.Not.Null);
        }
        finally
        {
            PcCompatManagedAssemblyRewrite.RegisterProvider(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [NonParallelizable]
    public void CompleteLoadFailureReplacesFinalizingProgressWithFailure()
    {
        var root = CreateTempMod();
        var previousOracle = Environment.GetEnvironmentVariable(
            "STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE");
        try
        {
            Environment.SetEnvironmentVariable("STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE", "1");
            PcCompatManagedAssemblyRewrite.RegisterProvider(null);
            var plugin = new PcCompatModPlugin(CreateManifest(root));
            plugin.BeginLoad();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!plugin.IsLoadReady && DateTime.UtcNow < deadline)
                Thread.Sleep(10);

            Assert.That(plugin.IsLoadReady, Is.True);
            Assert.Catch<Exception>(() => plugin.CompleteLoad());
            var progress = plugin.GetLoadProgress();
            Assert.Multiple(() =>
            {
                Assert.That(progress.Progress, Is.EqualTo(1f));
                Assert.That(progress.Stage, Does.Contain(L10n.Get("PcCompat_LoadStage_Failed")));
                Assert.That(progress.Stage, Does.Not.Contain(L10n.Get("PcCompat_LoadStage_Finalizing")));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE",
                previousOracle);
            PcCompatManagedAssemblyRewrite.RegisterProvider(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void RuntimeAssemblyPathFallsBackWhenCoreClrReportsEmptyLocation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pccompat-runtime-assembly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var expected = Path.Combine(root, "StArray.ModManager.dll");
            File.WriteAllBytes(expected, [0x4d, 0x5a]);

            var resolved = PcCompatManagedAssemblyRewrite.ResolveRuntimeAssemblyPath(
                string.Empty,
                "StArray.ModManager",
                root);

            Assert.That(resolved, Is.EqualTo(Path.GetFullPath(expected)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [NonParallelizable]
    public void UnregisterFailsClosedWhenNativeBundleRetirementFails()
    {
        var manifest = CreateManifest(Path.GetTempPath());
        var retireCalls = 0;
        PcCompatRuntime.RegisterNativeRuleBundleRetireSink(_ =>
        {
            retireCalls++;
            return false;
        });

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PcCompatRuntime.UnregisterMod(manifest));
            Assert.That(exception!.Message, Does.Contain("managed session preserved"));
            Assert.That(retireCalls, Is.EqualTo(1));
        }
        finally
        {
            PcCompatRuntime.RegisterNativeRuleBundleRetireSink(null);
            PcCompatRuntime.UnregisterMod(manifest);
        }
    }

    [Test]
    [NonParallelizable]
    public void BackgroundUnregisterRunsEntireTransactionOnScheduledUnityMain()
    {
        var manifest = CreateManifest(Path.GetTempPath());
        var callerThread = Environment.CurrentManagedThreadId;
        var schedulerCalls = 0;
        var retireThread = 0;
        var retireUnityMain = false;
        PcCompatRuntime.RegisterManagedInstallContextProbe(
            () => PcCompatUnityMainExecutionContext.IsActive);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(work =>
        {
            schedulerCalls++;
            _ = Task.Run(() =>
            {
                using var unityMain = PcCompatUnityMainExecutionContext.Enter();
                work();
            });
            return true;
        });
        PcCompatRuntime.RegisterNativeRuleBundleRetireSink(_ =>
        {
            retireThread = Environment.CurrentManagedThreadId;
            retireUnityMain = PcCompatUnityMainExecutionContext.IsActive;
            return true;
        });

        try
        {
            PcCompatRuntime.UnregisterMod(manifest);

            Assert.Multiple(() =>
            {
                Assert.That(schedulerCalls, Is.EqualTo(1));
                Assert.That(retireUnityMain, Is.True);
                Assert.That(retireThread, Is.Not.EqualTo(callerThread));
            });
        }
        finally
        {
            PcCompatRuntime.RegisterNativeRuleBundleRetireSink(null);
        }
    }

    [Test]
    [NonParallelizable]
    public void VerifiedUnityMainThreadRunsUnregisterInlineWithoutSelfQueueing()
    {
        var manifest = CreateManifest(Path.GetTempPath());
        var schedulerCalls = 0;
        var retireUnityMain = false;
        var retireThread = 0;
        var callerThread = Environment.CurrentManagedThreadId;
        PcCompatRuntime.RegisterManagedInstallContextProbe(
            () => PcCompatUnityMainExecutionContext.IsActive);
        PcCompatRuntime.RegisterUnityMainThreadProbe(() => true);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(_ =>
        {
            schedulerCalls++;
            return false;
        });
        PcCompatRuntime.RegisterNativeRuleBundleRetireSink(_ =>
        {
            retireUnityMain = PcCompatUnityMainExecutionContext.IsActive;
            retireThread = Environment.CurrentManagedThreadId;
            return true;
        });

        try
        {
            PcCompatRuntime.UnregisterMod(manifest);

            Assert.Multiple(() =>
            {
                Assert.That(schedulerCalls, Is.Zero);
                Assert.That(retireUnityMain, Is.True);
                Assert.That(retireThread, Is.EqualTo(callerThread));
            });
        }
        finally
        {
            PcCompatRuntime.RegisterNativeRuleBundleRetireSink(null);
        }
    }

    [Test]
    [NonParallelizable]
    public void BackgroundUnregisterFailsBeforeRetirementWhenUnityMainQueueRejectsWork()
    {
        var manifest = CreateManifest(Path.GetTempPath());
        var retireCalls = 0;
        PcCompatRuntime.RegisterManagedInstallContextProbe(() => false);
        PcCompatRuntime.RegisterUnityMainWorkScheduler(_ => false);
        PcCompatRuntime.RegisterNativeRuleBundleRetireSink(_ =>
        {
            retireCalls++;
            return true;
        });

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PcCompatRuntime.UnregisterMod(manifest));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain("work queue rejected"));
                Assert.That(retireCalls, Is.Zero);
            });
        }
        finally
        {
            PcCompatRuntime.RegisterNativeRuleBundleRetireSink(null);
        }
    }

    private static string CreateTempMod()
    {
        var path = Path.Combine(Path.GetTempPath(), "pccompat-async-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static PcModManifest CreateManifest(string path)
        => new()
        {
            FolderPath = path,
            Id = "async-test",
            DisplayName = "Async Test",
            Kind = PcModKind.UnityModManager
        };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StArray.ModManager.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("StArray.ModManager repo root was not found.");
    }
}
