using System.Collections.Concurrent;
using dnlib.DotNet;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class NativeModShadowPackageTests
{
    [Test]
    public void IdenticalClosureReusesVerifiedContentAddressedPackage()
    {
        using var fixture = ShadowFixture.Create();

        var first = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);
        var second = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);

        Assert.Multiple(() =>
        {
            Assert.That(first.CacheHit, Is.False);
            Assert.That(second.CacheHit, Is.True);
            Assert.That(second.CacheKey, Is.EqualTo(first.CacheKey));
            Assert.That(second.PackageDirectory, Is.EqualTo(first.PackageDirectory));
            Assert.That(second.EntryAssemblyPath, Is.Not.EqualTo(fixture.EntryPath));
            Assert.That(File.ReadAllBytes(second.EntryAssemblyPath),
                Is.EqualTo(File.ReadAllBytes(fixture.EntryPath)));
            Assert.That(second.Assemblies, Is.Not.Empty);
        });
        second.Verify();
    }

    [Test]
    public void SourceContentChangePublishesNewPackageKey()
    {
        using var fixture = ShadowFixture.Create();
        var first = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);

        using (var stream = new FileStream(fixture.EntryPath, FileMode.Append, FileAccess.Write))
            stream.WriteByte(0x5a);
        var second = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);

        Assert.Multiple(() =>
        {
            Assert.That(second.CacheKey, Is.Not.EqualTo(first.CacheKey));
            Assert.That(second.PackageDirectory, Is.Not.EqualTo(first.PackageDirectory));
            Assert.That(second.CacheHit, Is.False);
        });
        second.Verify();
    }

    [Test]
    public void TamperedCachedAssemblyIsRejectedAndRebuilt()
    {
        using var fixture = ShadowFixture.Create();
        var first = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);
        using (var stream = new FileStream(
                   first.EntryAssemblyPath,
                   FileMode.Append,
                   FileAccess.Write))
        {
            stream.WriteByte(0xa5);
        }

        var rebuilt = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt.CacheKey, Is.EqualTo(first.CacheKey));
            Assert.That(rebuilt.CacheHit, Is.False);
            Assert.That(File.ReadAllBytes(rebuilt.EntryAssemblyPath),
                Is.EqualTo(File.ReadAllBytes(fixture.EntryPath)));
        });
        rebuilt.Verify();
    }

    [Test]
    public void TamperedShadowManifestIsRejectedAndRebuilt()
    {
        using var fixture = ShadowFixture.Create();
        var first = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);
        var manifestPath = Path.Combine(first.PackageDirectory, "shadow-package.json");
        File.AppendAllText(manifestPath, " ", new System.Text.UTF8Encoding(false));

        var rebuilt = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt.CacheKey, Is.EqualTo(first.CacheKey));
            Assert.That(rebuilt.CacheHit, Is.False);
            Assert.That(File.ReadAllText(manifestPath), Does.Not.EndWith(" "));
        });
        rebuilt.Verify();
    }

    [Test]
    public void ConcurrentPreparationPublishesOneFinalPackage()
    {
        using var fixture = ShadowFixture.Create();
        var packages = new ConcurrentBag<NativeModShadowPackage>();

        Parallel.For(0, 8, _ => packages.Add(NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath)));

        var results = packages.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Length.EqualTo(8));
            Assert.That(results.Select(package => package.CacheKey).Distinct().ToArray(),
                Has.Length.EqualTo(1));
            Assert.That(results.Select(package => package.PackageDirectory).Distinct().ToArray(),
                Has.Length.EqualTo(1));
            Assert.That(Directory.GetDirectories(
                    Path.Combine(
                        fixture.CacheRoot,
                        NativeModShadowPackageManifest.CurrentFormatVersion)),
                Has.Length.EqualTo(1));
        });
        results[0].Verify();
    }

    [Test]
    public void PrivateDependencyClosureExcludesUnreferencedAssemblies()
    {
        using var fixture = ShadowFixture.CreateGeneratedClosure();

        var package = NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath);

        Assert.Multiple(() =>
        {
            Assert.That(package.Assemblies.Select(record => record.OriginalIdentity.Name),
                Is.EquivalentTo(new[] { "Private.Entry", "Private.Dependency" }));
            Assert.That(File.Exists(Path.Combine(
                package.PackageDirectory,
                "Private.Unrelated.dll")), Is.False);
        });
    }

    [Test]
    public void MissingPrivateDependencyFailsBeforePackagePublication()
    {
        using var fixture = ShadowFixture.CreateGeneratedClosure();
        File.Delete(Path.Combine(fixture.ModDirectory, "Private.Dependency.dll"));

        Assert.Throws<FileNotFoundException>(() => NativeModShadowPackage.Prepare(
            fixture.CacheRoot,
            fixture.ModDirectory,
            fixture.EntryPath));
        Assert.That(Directory.Exists(fixture.CacheRoot), Is.False);
    }

    private sealed class ShadowFixture : IDisposable
    {
        private ShadowFixture(string root, string modDirectory, string cacheRoot, string entryPath)
        {
            Root = root;
            ModDirectory = modDirectory;
            CacheRoot = cacheRoot;
            EntryPath = entryPath;
        }

        internal string Root { get; }
        internal string ModDirectory { get; }
        internal string CacheRoot { get; }
        internal string EntryPath { get; }

        internal static ShadowFixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "starray-native-shadow-" + Guid.NewGuid().ToString("N"));
            var modDirectory = Path.Combine(root, "Fixture");
            var cacheRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(modDirectory);
            var entryPath = Path.Combine(modDirectory, "Fixture.dll");
            File.Copy(typeof(NativeModShadowPackageTests).Assembly.Location, entryPath);
            return new ShadowFixture(root, modDirectory, cacheRoot, entryPath);
        }

        internal static ShadowFixture CreateGeneratedClosure()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "starray-native-shadow-closure-" + Guid.NewGuid().ToString("N"));
            var modDirectory = Path.Combine(root, "Fixture");
            var cacheRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(modDirectory);
            var dependencyPath = Path.Combine(modDirectory, "Private.Dependency.dll");
            var entryPath = Path.Combine(modDirectory, "Private.Entry.dll");
            CreateAssembly(dependencyPath, "Private.Dependency");
            CreateAssembly(
                entryPath,
                "Private.Entry",
                baseAssembly: "Private.Dependency",
                baseType: "Private.Dependency.Api");
            CreateAssembly(
                Path.Combine(modDirectory, "Private.Unrelated.dll"),
                "Private.Unrelated");
            return new ShadowFixture(root, modDirectory, cacheRoot, entryPath);
        }

        private static void CreateAssembly(
            string path,
            string assemblyName,
            string? baseAssembly = null,
            string? baseType = null)
        {
            using var module = new ModuleDefUser(Path.GetFileName(path))
            {
                Kind = ModuleKind.Dll
            };
            new AssemblyDefUser(assemblyName, new Version(1, 0, 0, 0)).Modules.Add(module);
            ITypeDefOrRef baseTypeReference = module.CorLibTypes.Object.TypeDefOrRef;
            if (baseAssembly != null && baseType != null)
            {
                var separator = baseType.LastIndexOf('.');
                var typeNamespace = separator < 0 ? string.Empty : baseType[..separator];
                var typeName = separator < 0 ? baseType : baseType[(separator + 1)..];
                baseTypeReference = new TypeRefUser(
                    module,
                    typeNamespace,
                    typeName,
                    new AssemblyRefUser(new AssemblyNameInfo(baseAssembly)));
            }
            module.Types.Add(new TypeDefUser(
                assemblyName,
                "Api",
                baseTypeReference)
            {
                Attributes = TypeAttributes.Public | TypeAttributes.Class
            });
            module.Write(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
