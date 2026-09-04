using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatManagedVirtualBundleActivationTests
{
    private const string ModId = "managed.virtual.activation.test";
    private string _root = null!;
    private string? _previousRuntimeLoadGate;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "pccompat-managed-virtual-activation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousRuntimeLoadGate = Environment.GetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable);
        PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
        PcCompatResourceRecipeRuntime.Unload(ModId);
        PcCompatVirtualBundleRegistry.RemoveMod(ModId);
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(null);
        PcCompatVirtualBundleRegistry.RegisterArrayFactory(null);
    }

    [TearDown]
    public void TearDown()
    {
        PcCompatVirtualBundleRegistry.RemoveMod(ModId);
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(null);
        PcCompatVirtualBundleRegistry.RegisterArrayFactory(null);
        PcCompatResourceRecipeRuntime.Unload(ModId);
        PcCompatResourceRecipeRuntime.ClearBundleLoadSink();
        Environment.SetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
            _previousRuntimeLoadGate);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void RewrittenActivationUsesVirtualBundleWithoutInvokingLegacyUnityLoadSink()
    {
        var bundlePath = Path.Combine(_root, "bundle");
        File.WriteAllBytes(bundlePath, "desktop-bundle-fixture"u8.ToArray());
        var candidateSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundlePath)))
            .ToLowerInvariant();
        var recipePath = Path.Combine(_root, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, bundlePath, candidateSha);
        var manifest = new PcModManifest
        {
            FolderPath = _root,
            Id = ModId,
            DisplayName = ModId,
            AssemblyName = "fixture.dll",
            Kind = PcModKind.UnityModManager
        };

        Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
        Assert.That(
            PcCompatResourceRecipeRuntime.TryGetSessionGeneration(ModId, out var generation),
            Is.True);
        var resourceIrPath = Path.Combine(_root, "resource_ir.bin");
        File.WriteAllText(resourceIrPath, "fixture");
        PcCompatVirtualBundleRegistry.RegisterSession(
            ModId,
            generation,
            _root,
            resourceIrPath,
            new PcCompatResourceIrDocument
            {
                ModId = ModId,
                TargetUnityVersion = "6000.3.10f1",
                Bundles =
                [
                    new PcCompatResourceIrBundle
                    {
                        Id = "vb.0123456789abcdef0123456789abcdef",
                        CandidateSha256Hex = candidateSha,
                        SourceFileName = "bundle",
                        SourceRelativePath = "bundle",
                        PlatformHint = "Android",
                        UnityVersion = "6000.3.10f1",
                        LoadPolicy = "AutoLoad",
                        SelectedForRuntime = true
                    }
                ]
            });
        Assert.That(PcCompatVirtualBundleRegistry.HasSession(ModId, generation), Is.True);

        var legacyLoadCalls = 0;
        Environment.SetEnvironmentVariable(
            PcCompatResourceRecipeRuntime.RuntimeLoadEnvironmentVariable,
            "1");
        PcCompatResourceRecipeRuntime.RegisterBundleLoadSink(
            request =>
            {
                legacyLoadCalls++;
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

        var target = new ManagedTarget();
        using var session = CreateSession(manifest, target, generation);
        var activationNotifications = new List<long>();
        session.RegisterActivationCompletedObserver(
            completed => activationNotifications.Add(completed.ResourceSessionGeneration));
        Directory.CreateDirectory(Path.GetDirectoryName(session.ManagedFailureReportPath)!);
        File.WriteAllText(session.ManagedFailureReportPath, "stale failure");
        session.RequestActivation();

        Assert.That(session.TryDispatchUpdate(0.016f), Is.True);
        Assert.That(target.EnableCount, Is.EqualTo(1));
        Assert.That(session.EnableCompleted, Is.True);
        Assert.That(activationNotifications, Is.Empty);
        session.NotifyActivationCompletedObservers();
        Assert.That(activationNotifications, Is.EqualTo(new[] { generation }));
        Assert.That(File.Exists(session.ManagedFailureReportPath), Is.False);
        Assert.That(legacyLoadCalls, Is.Zero,
            "VirtualBundle activation must never ask Unity to load the desktop bundle.");
    }

    [Test]
    public void RejectedRawDesktopCandidateDoesNotBlockReadyVirtualBundleActivation()
    {
        const string bundleId = "vb.2123456789abcdef0123456789abcdef";
        const string assetId = "res.2123456789abcdef0123456789abcdef";
        var bundlePath = Path.Combine(_root, "bundle");
        File.WriteAllBytes(bundlePath, "desktop-bundle-fixture"u8.ToArray());
        var candidateSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundlePath)))
            .ToLowerInvariant();
        var recipePath = Path.Combine(_root, "resource_recipe.bin");
        WriteResourceRecipe(
            recipePath,
            bundlePath,
            candidateSha,
            platformHint: "Windows",
            unityVersion: "0.0.0",
            versionGate: "ForcedOnly",
            loadPolicy: "Rejected");
        var manifest = new PcModManifest
        {
            FolderPath = _root,
            Id = ModId,
            DisplayName = ModId,
            AssemblyName = "fixture.dll",
            Kind = PcModKind.UnityModManager
        };

        Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
        Assert.That(
            PcCompatResourceRecipeRuntime.TryGetSessionGeneration(ModId, out var generation),
            Is.True);
        Assert.That(
            PcCompatResourceRecipeRuntime.GetReadinessSummary(ModId).ReadyCandidateCount,
            Is.Zero,
            "the raw Windows candidate must remain rejected for direct Android Unity loading");

        var resourceIrPath = Path.Combine(_root, "resource_ir.bin");
        File.WriteAllText(resourceIrPath, "fixture");
        PcCompatVirtualBundleRegistry.RegisterSession(
            ModId,
            generation,
            _root,
            resourceIrPath,
            new PcCompatResourceIrDocument
            {
                ModId = ModId,
                TargetUnityVersion = "6000.3.10f1",
                Bundles =
                [
                    new PcCompatResourceIrBundle
                    {
                        Id = bundleId,
                        CandidateSha256Hex = candidateSha,
                        SourceFileName = "bundle",
                        SourceRelativePath = "bundle",
                        PlatformHint = "Windows",
                        UnityVersion = "0.0.0",
                        LoadPolicy = "Rejected",
                        SelectedForRuntime = true,
                        AssetIds = [assetId]
                    }
                ],
                Assets =
                [
                    new PcCompatResourceIrAsset
                    {
                        Id = assetId,
                        BundleId = bundleId,
                        Name = "Required",
                        SourceType = "MonoBehaviour",
                        ExpectedType = "TMPro.TMP_FontAsset",
                        Container = "CAB-activation",
                        AssetsFileName = "bundle.assets",
                        PathId = 1,
                        TypeId = 114,
                        RequiredByMod = true,
                        MaterializationKind = PcCompatResourceIrMaterializationKind.CapabilityReference,
                        Compatibility = PcCompatResourceIrCompatibility.Compatible,
                        CapabilityStableId = "font.test"
                    }
                ]
            });
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
            new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                new object()));

        var target = new ManagedTarget();
        using var session = CreateSession(manifest, target, generation);
        session.RequestActivation();

        Assert.That(session.TryDispatchUpdate(0.016f), Is.True);
        var virtualReadiness = PcCompatVirtualBundleRegistry.GetSessionReadiness(ModId, generation);
        Assert.Multiple(() =>
        {
            Assert.That(target.EnableCount, Is.EqualTo(1));
            Assert.That(session.EnableCompleted, Is.True);
            Assert.That(virtualReadiness.IsReady, Is.True);
            Assert.That(virtualReadiness.RequiredReadyCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void RewrittenActivationWaitsForRequiredVirtualAssetsBeforeCompatEnable()
    {
        const string bundleId = "vb.1123456789abcdef0123456789abcdef";
        const string assetId = "res.1123456789abcdef0123456789abcdef";
        var bundlePath = Path.Combine(_root, "bundle");
        File.WriteAllBytes(bundlePath, "desktop-bundle-fixture"u8.ToArray());
        var candidateSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundlePath)))
            .ToLowerInvariant();
        var recipePath = Path.Combine(_root, "resource_recipe.bin");
        WriteResourceRecipe(recipePath, bundlePath, candidateSha);
        var manifest = new PcModManifest
        {
            FolderPath = _root,
            Id = ModId,
            DisplayName = ModId,
            AssemblyName = "fixture.dll",
            Kind = PcModKind.UnityModManager
        };

        Assert.That(PcCompatResourceRecipeRuntime.TryLoadForMod(manifest, recipePath), Is.True);
        Assert.That(
            PcCompatResourceRecipeRuntime.TryGetSessionGeneration(ModId, out var generation),
            Is.True);
        var resourceIrPath = Path.Combine(_root, "resource_ir.bin");
        File.WriteAllText(resourceIrPath, "fixture");
        PcCompatVirtualBundleRegistry.RegisterSession(
            ModId,
            generation,
            _root,
            resourceIrPath,
            new PcCompatResourceIrDocument
            {
                ModId = ModId,
                TargetUnityVersion = "6000.3.10f1",
                Bundles =
                [
                    new PcCompatResourceIrBundle
                    {
                        Id = bundleId,
                        CandidateSha256Hex = candidateSha,
                        SourceFileName = "bundle",
                        SourceRelativePath = "bundle",
                        PlatformHint = "Android",
                        UnityVersion = "6000.3.10f1",
                        LoadPolicy = "AutoLoad",
                        SelectedForRuntime = true,
                        AssetIds = [assetId]
                    }
                ],
                Assets =
                [
                    new PcCompatResourceIrAsset
                    {
                        Id = assetId,
                        BundleId = bundleId,
                        Name = "Required",
                        SourceType = "MonoBehaviour",
                        ExpectedType = "TMPro.TMP_FontAsset",
                        Container = "CAB-activation",
                        AssetsFileName = "bundle.assets",
                        PathId = 1,
                        TypeId = 114,
                        RequiredByMod = true,
                        MaterializationKind = PcCompatResourceIrMaterializationKind.CapabilityReference,
                        Compatibility = PcCompatResourceIrCompatibility.Compatible,
                        CapabilityStableId = "font.test"
                    }
                ]
            });

        var proxy = new object();
        var resolverCalls = 0;
        PcCompatVirtualBundleRegistry.RegisterAssetResolver(_ =>
        {
            resolverCalls++;
            if (resolverCalls == 1)
                throw new PcCompatVirtualAssetPendingException("capability registry is still loading");
            return new PcCompatVirtualAssetResolveResult(
                PcCompatVirtualAssetResolveStatus.Ready,
                proxy);
        });
        PcCompatVirtualBundleRegistry.RegisterArrayFactory((_, values) => values.ToArray());

        var target = new ManagedTarget(() =>
        {
            var handle = PcCompatVirtualBundleRegistry.Acquire(ModId, generation, bundlePath);
            try
            {
                Assert.That(
                    PcCompatVirtualBundleRegistry.LoadAsset(
                        handle,
                        "Required",
                        "TMPro.TMP_FontAsset"),
                    Is.SameAs(proxy));
            }
            finally
            {
                PcCompatVirtualBundleRegistry.Release(handle);
            }
        });
        using var session = CreateSession(manifest, target, generation);
        session.RequestActivation();

        Assert.That(session.TryDispatchUpdate(0.016f), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(target.EnableCount, Is.Zero);
            Assert.That(session.ActivationPending, Is.True);
            Assert.That(session.ActivationFailed, Is.False);
            Assert.That(session.Lifecycle.State, Is.EqualTo(PcCompatManagedLifecycleState.Loaded));
            Assert.That(session.ActivationStatus, Does.Contain("capability registry is still loading"));
        });

        typeof(PcCompatManagedModSession)
            .GetField("_nextActivationPollTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, 0L);
        Assert.That(session.TryDispatchUpdate(0.016f), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(target.EnableCount, Is.EqualTo(1));
            Assert.That(session.EnableCompleted, Is.True);
            Assert.That(session.ActivationPending, Is.False);
            Assert.That(session.ActivationFailed, Is.False);
            Assert.That(resolverCalls, Is.EqualTo(2));
        });
    }

    private static PcCompatManagedModSession CreateSession(
        PcModManifest manifest,
        object instance,
        long generation)
    {
        var value = Activator.CreateInstance(
            typeof(PcCompatManagedModSession),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                manifest,
                AssemblyLoadContext.Default,
                typeof(PcCompatManagedVirtualBundleActivationTests).Assembly,
                instance,
                new object(),
                Array.Empty<PcCompatPatchDescriptor>(),
                false,
                false,
                true,
                false,
                generation,
                true,
                true
            ],
            culture: null);
        return (PcCompatManagedModSession)(value
            ?? throw new AssertionException("Could not construct managed session fixture."));
    }

    private static void WriteResourceRecipe(
        string path,
        string bundlePath,
        string sha256Hex,
        string platformHint = "Android",
        string unityVersion = "6000.3.10f1",
        string versionGate = "Auto",
        string loadPolicy = "AutoLoad")
    {
        var json = $$"""
        {
          "modId":{{JsonSerializer.Serialize(ModId)}},
          "recipeId":"xphorror.resource.indexed_bundle.v1",
          "compatibility":"partial",
          "targetUnityVersion":"6000.3.10f1",
          "candidates":[{
            "sourcePath":{{JsonSerializer.Serialize(bundlePath)}},
            "fileName":"bundle",
            "platformHint":{{JsonSerializer.Serialize(platformHint)}},
            "unityVersion":{{JsonSerializer.Serialize(unityVersion)}},
            "versionGate":{{JsonSerializer.Serialize(versionGate)}},
            "loadPolicy":{{JsonSerializer.Serialize(loadPolicy)}},
            "fileSize":{{new FileInfo(bundlePath).Length}},
            "sha256Hex":"{{sha256Hex}}",
            "hasEmbeddedTypeTree":true,
            "indexSucceeded":true,
            "directoryEntries":[],
            "assets":[],
            "warnings":[]
          }],
          "featureGroups":[{
            "id":"overlay.test",
            "displayName":"Managed activation test",
            "selectedCandidateSha256Hex":"{{sha256Hex}}",
            "selectedPlatform":{{JsonSerializer.Serialize(platformHint)}},
            "loadPolicy":{{JsonSerializer.Serialize(loadPolicy)}},
            "assetNames":[],
            "notes":[]
          }],
          "bindings":[]
        }
        """;
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[PcCompatResourceRecipe.HeaderSize];
        Encoding.ASCII.GetBytes("XPHRRESC").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(8, 2),
            PcCompatResourceRecipe.SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(10, 2),
            PcCompatResourceRecipe.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), checked((uint)payload.Length));
        SHA256.HashData(payload).CopyTo(header, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(52, 4),
            checked((uint)(header.Length + payload.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56, 4), Crc32(payload));
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

    private sealed class ManagedTarget(Action? onEnable = null)
    {
        public int EnableCount { get; private set; }
        public void CompatEnable()
        {
            EnableCount++;
            onEnable?.Invoke();
        }
        public void CompatDisable() { }
    }
}
