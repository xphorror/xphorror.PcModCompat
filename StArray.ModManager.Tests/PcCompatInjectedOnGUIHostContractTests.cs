namespace StArray.ModManager.Tests;

public sealed class PcCompatInjectedOnGUIHostContractTests
{
    [Test]
    public void AndroidUsesBeginGuiFallbackUntilArm64ClassInjectionIsAvailable()
    {
        var root = FindModManagerRoot();
        var host = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatInjectedOnGUIHost.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatManagedSelfRenderBridge.cs"));
        var startup = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "Managed.cs"));
        var bootstrap = File.ReadAllText(Path.Combine(
            root,
            "StArray.ModManager.Android",
            "PcCompat",
            "PcCompatIl2CppInteropBootstrap.cs"));
        var migrationBuild = File.ReadAllText(Path.Combine(
            root,
            "xphorror.PcModCompat",
            "build_interop_migration.ps1"));

        Assert.Multiple(() =>
        {
            Assert.That(host, Does.Contain("UnityEngine.MonoBehaviour"));
            Assert.That(host, Does.Contain("DefineType("));
            Assert.That(host, Does.Contain("DefineConstructor("));
            Assert.That(host, Does.Contain("typeof(IntPtr)"));
            Assert.That(host, Does.Contain("DefineMethod(").And.Contain("\"OnGUI\","));
            Assert.That(host, Does.Contain("ClassInjector.RegisterTypeInIl2Cpp"));
            Assert.That(host, Does.Contain("PcCompatResourceBundleLoader.TryScheduleUnityMainWork"));
            Assert.That(host, Does.Contain("PcCompatUnityMainExecutionContext.IsActive"));
            Assert.That(host, Does.Contain("CreateGameObject("));
            Assert.That(host, Does.Contain("AddComponent("));
            Assert.That(host, Does.Contain("DontDestroyOnLoad("));
            Assert.That(host, Does.Contain("SetEnabled(component, false)"));
            Assert.That(host, Does.Contain("_wrapBehaviour(((Il2CppObjectBase)result).Pointer)"));
            Assert.That(host, Does.Contain("IsDispatchReady"));
            Assert.That(host, Does.Contain("Architecture.Arm64"));
            Assert.That(host, Does.Contain("GetBaseException()"));
            Assert.That(host, Does.Not.Contain("RVA"));
            Assert.That(host, Does.Not.Contain("Dobby"));

            Assert.That(bootstrap, Does.Contain("ValidateConstructor(monoBehaviour, typeof(IntPtr))"));
            Assert.That(bootstrap, Does.Contain(
                "ValidateInstanceMethod(gameObject, \"AddComponent\", typeof(Il2CppSystem.Type))"));
            Assert.That(bootstrap, Does.Contain(
                "ValidateInstanceMethod(behaviour, \"set_enabled\", typeof(bool))"));
            Assert.That(bootstrap, Does.Contain(
                "ValidateConstructor(behaviour, typeof(IntPtr))"));
            Assert.That(migrationBuild, Does.Contain(
                "detourProvider = 'hook_broker_infrastructure'"));
            Assert.That(migrationBuild, Does.Contain(
                "classInjection = 'arm64_upstream_unsupported_not_attempted'"));

            Assert.That(bridge, Does.Contain("PcCompatInjectedOnGUIHost.SetDemand(enabled)"));
            Assert.That(bridge, Does.Contain("PcCompatInjectedOnGUIHost.IsDispatchReady"));
            Assert.That(bridge, Does.Contain("DispatchOnGUIFromInjectedHost"));
            Assert.That(bridge, Does.Contain(
                "enabled && !PcCompatInjectedOnGUIHost.IsDispatchReady ? 1 : 0"));

            var queueInstall = startup.IndexOf(
                "PcCompatResourceBundleLoader.Install();",
                StringComparison.Ordinal);
            var hostInstall = startup.IndexOf(
                "PcCompatInjectedOnGUIHost.Install();",
                StringComparison.Ordinal);
            Assert.That(hostInstall, Is.GreaterThan(queueInstall));
        });
    }

    private static string FindModManagerRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "StArray.ModManager.Android")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate StArray.ModManager repository root.");
    }
}
