using System.Text;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

public sealed class ModRuntimeIsolationBindingTests
{
    [Test]
    public void LoadingGenerationAcceptsOneMatchingManifest()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "manifest-owner");
        var manifest = CreateManifest(key, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.That(session.TryBindIsolationManifest(key, manifest, out var hash), Is.True);
        var snapshot = session.SnapshotIsolationManifest();
        Assert.Multiple(() =>
        {
            Assert.That(hash, Has.Length.EqualTo(64));
            Assert.That(snapshot.Hash, Is.EqualTo(hash));
            Assert.That(snapshot.Manifest?.ModId, Is.EqualTo(key.ModId));
            Assert.That(session.TryBindIsolationManifest(key, manifest, out var repeated), Is.True);
            Assert.That(repeated, Is.EqualTo(hash));
        });

        var changed = CreateManifest(
            key,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.That(session.TryBindIsolationManifest(key, changed, out _), Is.False);
        Assert.That(session.TryPublishActive(key), Is.True);
        Assert.That(session.TryBindIsolationManifest(key, manifest, out _), Is.False);
        Retire(session, key);
    }

    [Test]
    public void ManifestIdentityMismatchAndStaleGenerationAreRejected()
    {
        var session = new ModRuntimeSession();
        var first = session.BeginLoad(ModEntry.NativeLoaderKind, "manifest-stale");
        var wrongOwner = CreateManifest(first, Hash) with { ModId = "other" };
        var wrongLoader = CreateManifest(first, Hash) with { LoaderKind = "xphorror.PcModCompat" };

        Assert.Multiple(() =>
        {
            Assert.That(session.TryBindIsolationManifest(first, wrongOwner, out _), Is.False);
            Assert.That(session.TryBindIsolationManifest(first, wrongLoader, out _), Is.False);
        });
        Assert.That(session.TryBindIsolationManifest(first, CreateManifest(first, Hash), out _), Is.True);
        Assert.That(session.TryAbortLoad(first), Is.True);

        var second = session.BeginLoad(ModEntry.NativeLoaderKind, first.ModId);
        Assert.Multiple(() =>
        {
            Assert.That(session.SnapshotIsolationManifest().Manifest, Is.Null);
            Assert.That(session.TryBindIsolationManifest(first, CreateManifest(first, Hash), out _), Is.False);
            Assert.That(session.TryBindIsolationManifest(second, CreateManifest(second, Hash), out _), Is.True);
        });
        Assert.That(session.TryPublishActive(second), Is.True);
        Retire(session, second);
    }

    [Test]
    public void BootstrapBindingFailureAbortsLoadingGenerationAndClosesDomain()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "starray-bootstrap-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var invalidAssembly = Path.Combine(root, "broken.dll");
        File.WriteAllText(invalidAssembly, "not a managed assembly", new UTF8Encoding(false));
        var session = new ModRuntimeSession();
        var mod = new ModEntry
        {
            Id = "broken-bootstrap",
            Name = "broken-bootstrap",
            EntryPoint = invalidAssembly,
            FolderPath = root,
            LoaderKind = ModEntry.NativeLoaderKind,
            RuntimeSession = session
        };
        var loader = new ModLoader(root);
        try
        {
            loader.AddMod(mod);
            Assert.That(loader.LoadMod(mod), Is.False);
            var snapshot = session.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.State, Is.EqualTo(ModRuntimeLifecycleState.Faulted));
                Assert.That(snapshot.ActiveCallbacks, Is.Zero);
                Assert.That(session.DomainToken.IsValid, Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private const string Hash =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static ModIsolationManifest CreateManifest(ModRuntimeKey key, string hash) => new()
    {
        ModId = key.ModId,
        LoaderKind = key.LoaderKind,
        OriginalAssembly = new ModAssemblyIdentity
        {
            Name = "Fixture",
            Version = "1.0.0.0",
            ModuleVersionId = "11111111-2222-3333-4444-555555555555",
            Sha256 = hash,
            ApiSurfaceHash = Hash,
            FileSize = 1
        },
        Features =
        [
            new ModIsolationFeatureRecord
            {
                FeatureId = "entry",
                Level = ModIsolationCapabilityLevel.Guarded
            }
        ]
    };

    private static void Retire(ModRuntimeSession session, ModRuntimeKey key)
    {
        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(key), Is.True);
    }
}
