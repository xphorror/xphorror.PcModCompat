namespace StArray.ModManager.Tests;

public sealed class AndroidCryptoJniContractTests
{
    [Test]
    public void CoreClrHostForwardsDotnetProxyTrustManagerVerifier()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "mono_droid_coreclr.c"));
        var buildScript = File.ReadAllText(Path.Combine(root, "build_android_single.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(
                source,
                Does.Contain("Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate"));
            Assert.That(
                source,
                Does.Contain("g_verify_remote_certificate"));
            Assert.That(
                source,
                Does.Contain("Runtime crypto JNI verifier forwarding enabled"));
            Assert.That(
                buildScript,
                Does.Contain("Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate"));
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Android")) &&
                File.Exists(Path.Combine(directory.FullName, "build_android_single.ps1")))
                return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail("Could not find StArray.ModManager repository root from test directory");
        return string.Empty;
    }
}
