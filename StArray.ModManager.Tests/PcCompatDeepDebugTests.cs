using System.Reflection;
using System.Reflection.Emit;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatDeepDebugTests
{
    [Test]
    public void DeepDiagnosticsAreCompileTimeDisabledByDefault()
    {
        var messages = new List<string>();
        var key = Guid.NewGuid().ToString("N");
        void Capture(Logger.Level level, string tag, string message)
        {
            if (level == Logger.Level.Info && tag == "PcCompatDeepDebug")
                messages.Add(message);
        }

        Logger.OnLog += Capture;
        try
        {
            PcCompatDeepDebug.WriteSampled(
                "sampling-contract",
                key,
                count => $"sample={count}",
                first: 8,
                periodic: 0);
            PcCompatDeepDebug.WriteState("state-contract", key, "a", "value=a");
            PcCompatDeepDebug.Write("sink-contract", "payload");
        }
        finally
        {
            Logger.OnLog -= Capture;
        }

        Assert.Multiple(() =>
        {
            Assert.That(messages, Is.Empty);
            Assert.That(
                PcCompatDeepDebug.ShouldSample("sampling-contract", key, out var count),
                Is.False);
            Assert.That(count, Is.Zero);
        });
    }

    [Test]
    public void FieldSnapshotsKeepDetailButHaveAHardPerMessageLimit()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PcCompat.DeepDebug.Fields." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var typeBuilder = assembly.DefineDynamicModule("main").DefineType("LargeDiagnosticState");
        for (var index = 0; index < 72; ++index)
            typeBuilder.DefineField("Field" + index, typeof(string), FieldAttributes.Public);
        var type = typeBuilder.CreateType()!;
        var instance = Activator.CreateInstance(type)!;
        foreach (var field in type.GetFields())
            field.SetValue(instance, new string('x', 512));

        var snapshot = PcCompatDeepDebug.DescribeFields(instance);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Has.Length.EqualTo(PcCompatDeepDebug.MaxFieldSnapshotLength));
            Assert.That(snapshot, Does.EndWith("; <snapshot-truncated>"));
            Assert.That(snapshot, Does.Contain("LargeDiagnosticState.Field0="));
        });
    }

    [Test]
    public void DeepProbeCoverageIsGenericAndUsesOneSearchablePrefix()
    {
        var root = FindModManagerRoot();
        var sourceRoot = Path.Combine(root, "xphorror.PcModCompat", "src");
        var expected = new Dictionary<string, string[]>
        {
            ["PcCompatManagedComponentBridge.cs"] =
                ["component-register", "component-inventory", "component-lifecycle"],
            ["PcCompatLegacyInputBridge.cs"] = ["input-query", "callsite=0x"],
            ["PcCompatKeyViewerConsumerRuntime.cs"] = ["consumer-plan", "consumer-state"],
            ["PcCompatKeyViewerPreviewRuntime.cs"] = ["touch-registration", "touch-event"],
            ["PcCompatManagedFontBridge.cs"] = ["font-create", "font-setter"],
            ["PcCompatVirtualBundleRegistry.cs"] =
                ["virtual-session", "virtual-materialize", "virtual-liveness", "virtual-load"]
        };

        foreach (var pair in expected)
        {
            var source = File.ReadAllText(Path.Combine(sourceRoot, pair.Key));
            foreach (var marker in pair.Value)
                Assert.That(source, Does.Contain(marker), $"{pair.Key} is missing {marker}");
        }
        var debugInfrastructure = File.ReadAllText(Path.Combine(sourceRoot, "PcCompatDeepDebug.cs"));
        Assert.That(debugInfrastructure, Does.Not.Contain("JipperKeyViewer"));
        Assert.That(debugInfrastructure, Does.Contain("Conditional(\"PCCOMPAT_DEEP_DEBUG\")"));
        Assert.That(PcCompatDeepDebug.Prefix, Is.EqualTo("[DEBUG-jpkv-deep-v1]"));
    }

    private static string FindModManagerRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "xphorror.PcModCompat")) &&
                Directory.Exists(Path.Combine(current.FullName, "StArray.ModManager.Tests")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the StArray.ModManager repository root.");
    }
}
