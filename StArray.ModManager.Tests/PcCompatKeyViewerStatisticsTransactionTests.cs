using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatKeyViewerStatisticsTransactionTests
{
    [Test]
    public void TransactionRestoresManagedFieldsFileAndSaveSink()
    {
        var root = Path.Combine(Path.GetTempPath(), "starray-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "stats.dat");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var fixture = new StatisticsFixture();
        var feature = CreateFeature();
        var state = new PcCompatManagedExecutionState(
            "statistics-fixture",
            1,
            PcCompatManagedExecutionPhase.Update);

        try
        {
            Assert.That(
                PcCompatKeyViewerStatisticsTransaction.TryCreate(
                    AssemblyLoadContext.Default,
                    new PcModManifest
                    {
                        Id = "statistics-fixture",
                        DisplayName = "statistics-fixture",
                        FolderPath = root
                    },
                    fixture,
                    state,
                    [new PcCompatKeyViewerStatisticsFeature(
                        feature,
                        new PcCompatKeyViewerFeatureOverride
                        {
                            FeatureId = feature.Id
                        })],
                    out var transaction,
                    out var error),
                Is.True,
                error);

            fixture.Count[0] = 99;
            fixture.TotalCount = 100;
            fixture.Held[0] = true;
            fixture.PressTimes.Enqueue(99);
            fixture.KeyPressTimes[0].Enqueue(99);
            fixture.KpsState = 100;
            fixture.SavePending = true;
            fixture.DirtyVersion = 100;
            File.WriteAllBytes(path, [9, 9, 9]);

            Assert.That(transaction!.TryRestore(out error), Is.True, error);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Count, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(fixture.TotalCount, Is.EqualTo(3));
                Assert.That(fixture.Held, Is.EqualTo(new[] { false, false }));
                Assert.That(fixture.PressTimes, Is.EqualTo(new long[] { 10, 20 }));
                Assert.That(fixture.KeyPressTimes[0], Is.EqualTo(new long[] { 30 }));
                Assert.That(fixture.KpsState, Is.EqualTo(2));
                Assert.That(fixture.SavePending, Is.False);
                Assert.That(fixture.DirtyVersion, Is.EqualTo(4));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                Assert.That(fixture.SaveCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void VerifiedSelfRenderFeatureWithoutStaticPersistenceUsesBoundedOwnerOverlaySnapshot()
    {
        const string modId = "statistics-overlay-fixture";
        const long generation = 7;
        var root = Path.Combine(Path.GetTempPath(), "starray-stats-overlay-" + Guid.NewGuid().ToString("N"));
        var installRoot = Path.Combine(root, "install");
        var dataRoot = Path.Combine(root, "runtime");
        var overlayRoot = Path.Combine(dataRoot, "data");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(overlayRoot);
        var profilePath = Path.Combine(overlayRoot, "config", "profiles", "Default.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, "before");
        PcCompatManagedPathBridge.ClearAllRootsForTests();
        PcCompatManagedPathBridge.BindRoots(modId, generation, new PcCompatModPathRoots
        {
            InstallRoot = installRoot,
            ConfigRoot = Path.Combine(dataRoot, "config"),
            CacheRoot = Path.Combine(dataRoot, "cache"),
            LogRoot = Path.Combine(dataRoot, "log"),
            TempRoot = Path.Combine(dataRoot, "temp"),
            DataOverlayRoot = overlayRoot
        });
        var fixture = new StatisticsFixture();
        var feature = CreateFeature(staticPersistence: false);
        try
        {
            Assert.That(PcCompatKeyViewerStatisticsTransaction.TryCreate(
                    AssemblyLoadContext.Default,
                    new PcModManifest
                    {
                        Id = modId,
                        DisplayName = modId,
                        FolderPath = installRoot
                    },
                    fixture,
                    new PcCompatManagedExecutionState(
                        modId,
                        generation,
                        PcCompatManagedExecutionPhase.Update),
                    [new PcCompatKeyViewerStatisticsFeature(
                        feature,
                        new PcCompatKeyViewerFeatureOverride { FeatureId = feature.Id })],
                    out var transaction,
                    out var error),
                Is.True,
                error);

            fixture.Count[0] = 99;
            fixture.TotalCount = 100;
            File.WriteAllText(profilePath, "during");
            var createdPath = Path.Combine(overlayRoot, "config", "profiles", "Replay.json");
            File.WriteAllText(createdPath, "new");

            Assert.That(transaction!.TryRestore(out error), Is.True, error);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Count, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(fixture.TotalCount, Is.EqualTo(3));
                Assert.That(File.ReadAllText(profilePath), Is.EqualTo("before"));
                Assert.That(File.Exists(createdPath), Is.False);
                Assert.That(fixture.SaveCalls, Is.Zero);
            });
        }
        finally
        {
            PcCompatManagedPathBridge.ClearAllRootsForTests();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void StaticPersistencePathSnapshotsTheBoundOverlayInsteadOfTheInstallLayer()
    {
        const string modId = "statistics-static-overlay-fixture";
        const long generation = 8;
        var root = Path.Combine(Path.GetTempPath(), "starray-stats-static-overlay-" + Guid.NewGuid().ToString("N"));
        var installRoot = Path.Combine(root, "install");
        var dataRoot = Path.Combine(root, "runtime");
        var overlayRoot = Path.Combine(dataRoot, "data");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(overlayRoot);
        var installPath = Path.Combine(installRoot, "stats.dat");
        var overlayPath = Path.Combine(overlayRoot, "stats.dat");
        File.WriteAllText(installPath, "package");
        File.WriteAllText(overlayPath, "before");
        PcCompatManagedPathBridge.ClearAllRootsForTests();
        PcCompatManagedPathBridge.BindRoots(modId, generation, new PcCompatModPathRoots
        {
            InstallRoot = installRoot,
            ConfigRoot = Path.Combine(dataRoot, "config"),
            CacheRoot = Path.Combine(dataRoot, "cache"),
            LogRoot = Path.Combine(dataRoot, "log"),
            TempRoot = Path.Combine(dataRoot, "temp"),
            DataOverlayRoot = overlayRoot
        });
        var fixture = new StatisticsFixture();
        var feature = CreateFeature();
        try
        {
            Assert.That(PcCompatKeyViewerStatisticsTransaction.TryCreate(
                    AssemblyLoadContext.Default,
                    new PcModManifest
                    {
                        Id = modId,
                        DisplayName = modId,
                        FolderPath = installRoot
                    },
                    fixture,
                    new PcCompatManagedExecutionState(
                        modId,
                        generation,
                        PcCompatManagedExecutionPhase.Update),
                    [new PcCompatKeyViewerStatisticsFeature(
                        feature,
                        new PcCompatKeyViewerFeatureOverride { FeatureId = feature.Id })],
                    out var transaction,
                    out var error),
                Is.True,
                error);

            File.WriteAllText(overlayPath, "during");
            Assert.That(transaction!.TryRestore(out error), Is.True, error);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(overlayPath), Is.EqualTo("before"));
                Assert.That(File.ReadAllText(installPath), Is.EqualTo("package"));
            });
        }
        finally
        {
            PcCompatManagedPathBridge.ClearAllRootsForTests();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void FailedSaveSinkLeavesTransactionRetryable()
    {
        var root = Path.Combine(Path.GetTempPath(), "starray-stats-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "stats.dat");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var fixture = new StatisticsFixture { FailingSaveCalls = 1 };
        var feature = CreateFeature();
        try
        {
            Assert.That(
                PcCompatKeyViewerStatisticsTransaction.TryCreate(
                    AssemblyLoadContext.Default,
                    new PcModManifest
                    {
                        Id = "statistics-fixture",
                        DisplayName = "statistics-fixture",
                        FolderPath = root
                    },
                    fixture,
                    new PcCompatManagedExecutionState(
                        "statistics-fixture",
                        1,
                        PcCompatManagedExecutionPhase.Update),
                    [new PcCompatKeyViewerStatisticsFeature(
                        feature,
                        new PcCompatKeyViewerFeatureOverride { FeatureId = feature.Id })],
                    out var transaction,
                    out var error),
                Is.True,
                error);

            fixture.Count[0] = 99;
            fixture.TotalCount = 100;
            File.WriteAllBytes(path, [9]);
            Assert.That(transaction!.TryRestore(out error), Is.False);
            Assert.That(error, Does.Contain("save sink failure"));

            fixture.Count[0] = 77;
            fixture.TotalCount = 78;
            File.WriteAllBytes(path, [8]);
            Assert.That(transaction.TryRestore(out error), Is.True, error);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Count, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(fixture.TotalCount, Is.EqualTo(3));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                Assert.That(fixture.SaveCalls, Is.EqualTo(2));
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static PcCompatKeyViewerFeatureAdapter CreateFeature(bool staticPersistence = true)
    {
        var assemblyName = typeof(StatisticsFixture).Assembly.GetName().Name!;
        var typeName = typeof(StatisticsFixture).FullName!;
        PcCompatKeyViewerRoleBinding Role(string role, string member, string kind)
            => new()
            {
                Role = role,
                AssemblyName = assemblyName,
                TypeName = typeName,
                MemberName = member,
                MemberKind = kind,
                Evidence = new PcCompatAdapterEvidence
                {
                    Status = PcCompatAdapterEvidenceStatus.Proven,
                    Evidence = ["transaction fixture"]
                }
            };

        var evidence = new PcCompatAdapterEvidence
        {
            Status = PcCompatAdapterEvidenceStatus.Proven,
            Evidence = ["transaction fixture"]
        };
        var roles = new List<PcCompatKeyViewerRoleBinding>
        {
            Role("HeldState", nameof(StatisticsFixture.Held), "Field"),
            Role("CountState", nameof(StatisticsFixture.Count), "Field"),
            Role("TotalState", nameof(StatisticsFixture.TotalCount), "Field"),
            Role("KpsWindow", nameof(StatisticsFixture.PressTimes), "Field"),
            Role("KpsWindow", nameof(StatisticsFixture.KeyPressTimes), "Field"),
            Role("KpsState", nameof(StatisticsFixture.KpsState), "Field"),
            Role("PersistencePendingState", nameof(StatisticsFixture.SavePending), "Field"),
            Role("PersistenceDirtyState", nameof(StatisticsFixture.DirtyVersion), "Field")
        };
        if (staticPersistence)
            roles.Add(Role("PersistenceSink", nameof(StatisticsFixture.Save), "Method"));
        return new PcCompatKeyViewerFeatureAdapter
        {
            Id = "statistics",
            DisplayName = "Statistics",
            Backend = PcCompatKeyViewerBackend.ManagedSelfRender,
            Roles = roles,
            CountSemantics = new PcCompatKeyViewerCountSemantics
            {
                Clock = "fixture",
                ResetEntryPoint = "fixture",
                PersistencePath = staticPersistence ? "stats.dat" : null
            },
            Visibility = new PcCompatKeyViewerPredicate
            {
                Kind = "fixture",
                Expression = "true",
                Evidence = evidence
            },
            InputActivation = new PcCompatKeyViewerPredicate
            {
                Kind = "fixture",
                Expression = "true",
                Evidence = evidence
            },
            Capabilities = new PcCompatKeyViewerEvidenceMatrix
            {
                Input = evidence,
                Lane = evidence,
                Transition = evidence,
                Count = evidence,
                Kps = evidence,
                Rain = evidence,
                Presentation = evidence,
                Visibility = evidence,
                InputActivation = evidence,
                Settings = evidence,
                Persistence = evidence
            }
        };
    }

    private sealed class StatisticsFixture
    {
        public int[] Count = [1, 2];
        public int TotalCount = 3;
        public bool[] Held = [false, false];
        public Queue<long> PressTimes = new([10, 20]);
        public Queue<long>[] KeyPressTimes = [new Queue<long>([30]), new Queue<long>()];
        public int KpsState = 2;
        public bool SavePending;
        public int DirtyVersion = 4;
        public int SaveCalls;
        public int FailingSaveCalls;

        public void Save()
        {
            ++SaveCalls;
            if (FailingSaveCalls-- > 0)
                throw new InvalidOperationException("save sink failure");
        }
    }
}
