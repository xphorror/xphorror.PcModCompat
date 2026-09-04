namespace StArray.ModManager.Tests;

public sealed class AndroidNetworkingContractTests
{
    [Test]
    public void AndroidLibraryDeclaresInternetPermission()
    {
        var manifest = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Android",
            "library",
            "src",
            "main",
            "AndroidManifest.xml"));

        Assert.That(
            manifest,
            Does.Contain("<uses-permission android:name=\"android.permission.INTERNET\" />"));
    }

    [Test]
    public void AndroidRuntimePackagesManagedAndNativeHttpDependencies()
    {
        var root = FindRepositoryRoot();
        var runtimeManifest = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "generated",
            "runtime_manifest.generated.h"));

        Assert.Multiple(() =>
        {
            Assert.That(runtimeManifest, Does.Contain("System.Net.Http.dll"));
            Assert.That(runtimeManifest, Does.Contain("System.Net.NameResolution.dll"));
            Assert.That(runtimeManifest, Does.Contain("System.Net.Security.dll"));
            Assert.That(runtimeManifest, Does.Contain("System.Net.Sockets.dll"));
            Assert.That(runtimeManifest, Does.Contain("libSystem.Native.so"));
            Assert.That(
                runtimeManifest,
                Does.Contain("libSystem.Security.Cryptography.Native.Android.so"));
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Android")) &&
                Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager")))
                return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail("Could not find StArray.ModManager repository root from test directory");
        return string.Empty;
    }
}
