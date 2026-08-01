using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public class PcCompatHarmonyRemainingAbiTests
{
    private sealed class Sample
    {
        private int value;

        private Sample()
        {
        }

        private int Property { get; set; }

        public int ReadValue() => value;

        public int ReadProperty() => Property;
    }

    [SetUp]
    public void Reset()
    {
        HarmonyRegistry.ClearDiagnostics();
        FileLog.LogWriter = null;
        FileLog.indentChar = '\t';
        FileLog.indentLevel = 0;
        FileLog.SetBuffer([]);
        Harmony.ClearSwitch("pccompat.test.switch");
    }

    [Test]
    public void PatchInfoMirrorsTheMutableDataContainer()
    {
        var callback = typeof(PcCompatHarmonyRemainingAbiTests)
            .GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        var info = new PatchInfo();

        info.AddPrefix(callback, "owner", Priority.High, ["before"], ["after"], true);
        info.AddPostfix(callback, "owner", Priority.Low, [], [], false);

        Assert.Multiple(() =>
        {
            Assert.That(info.prefixes, Has.Length.EqualTo(1));
            Assert.That(info.postfixes, Has.Length.EqualTo(1));
            Assert.That(info.Debugging, Is.True);
            Assert.That(info.prefixes[0].owner, Is.EqualTo("owner"));
            Assert.That(info.prefixes[0].priority, Is.EqualTo(Priority.High));
        });

        info.RemovePrefix("owner");
        info.RemovePatch(callback);
        Assert.Multiple(() =>
        {
            Assert.That(info.prefixes, Is.Empty);
            Assert.That(info.postfixes, Is.Empty);
        });
    }

    [Test]
    public void FileLogExposesTheCompletePublicIlFormattingSurface()
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        FileLog.LogWriter = writer;

        FileLog.LogILComment(1, "comment");
        FileLog.LogIL(2, OpCodes.Nop);
        FileLog.LogIL(3, OpCodes.Ldstr, "text");
        FileLog.LogILBlockBegin(4, new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        FileLog.LogILBlockEnd(5, new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
        FileLog.FlushBuffer();
        writer.Flush();

        stream.Position = 0;
        var text = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("IL_0001: // comment"));
            Assert.That(text, Does.Contain("IL_0002: nop"));
            Assert.That(text, Does.Contain("IL_0003: ldstr"));
            Assert.That(text, Does.Contain("\"text\""));
            Assert.That(text, Does.Contain(".try"));
            Assert.That(text, Does.Contain("end handler"));
        });
    }

    [Test]
    public void RuntimeFlagsAndLocalSwitchRegistryRoundTrip()
    {
        Assert.That(AccessTools.IsNetFrameworkRuntime && AccessTools.IsNetCoreRuntime, Is.False);

        Harmony.SetSwitch("pccompat.test.switch", true);
        Assert.Multiple(() =>
        {
            Assert.That(Harmony.TryGetSwitch("pccompat.test.switch", out var value), Is.True);
            Assert.That(value, Is.EqualTo(true));
            Assert.That(Harmony.TryIsSwitchEnabled("pccompat.test.switch", out var enabled), Is.True);
            Assert.That(enabled, Is.True);
        });

        Harmony.ClearSwitch("pccompat.test.switch");
        Assert.That(Harmony.TryGetSwitch("pccompat.test.switch", out _), Is.False);
    }

    [Test]
    public void ReflectionFallbacksCoverMethodInvokerAndFastAccess()
    {
        var args = new object[] { 2, 3 };
        var handler = HarmonyLib.MethodInvoker.GetHandler(
            typeof(PcCompatHarmonyRemainingAbiTests)
                .GetMethod(nameof(Add), BindingFlags.NonPublic | BindingFlags.Static)!);
        var result = handler(null!, args);

        var sample = FastAccess.CreateInstantiationHandler<Sample>()();
        var field = typeof(Sample).GetField("value", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var property = typeof(Sample).GetProperty("Property", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FastAccess.CreateSetterHandler<Sample, int>(field)(sample, 7);
        FastAccess.CreateSetterHandler<Sample, int>(property)(sample, 9);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(5));
            Assert.That(args[0], Is.EqualTo(5));
            Assert.That(FastAccess.CreateGetterHandler<Sample, int>(field)(sample), Is.EqualTo(7));
            Assert.That(FastAccess.CreateGetterHandler<Sample, int>(property)(sample), Is.EqualTo(9));
            Assert.That(sample.ReadValue(), Is.EqualTo(7));
            Assert.That(sample.ReadProperty(), Is.EqualTo(9));
        });
    }

    [Test]
    public void RuntimeIlOnlyApisExistAndFailAtTheCallsiteWithDiagnostics()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(FastInvokeHandler), Is.Not.Null);
            Assert.That(typeof(GetterHandler<,>), Is.Not.Null);
            Assert.That(typeof(SetterHandler<,>), Is.Not.Null);
            Assert.That(typeof(InstantiationHandler<>), Is.Not.Null);
            Assert.That(typeof(RefResult<>), Is.Not.Null);
            Assert.That(typeof(AccessTools).GetNestedType("FieldRef`2"), Is.Not.Null);
            Assert.That(typeof(AccessTools).GetNestedType("StructFieldRef`2"), Is.Not.Null);
            Assert.That(typeof(AccessTools).GetNestedType("FieldRef`1"), Is.Not.Null);
        });

        Assert.Throws<NotSupportedException>(() =>
            AccessTools.FieldRefAccess<Sample, int>("value"));
        Assert.Throws<NotSupportedException>(() =>
            HarmonyLib.MethodInvoker.GetHandler(typeof(Sample).GetMethod(nameof(Sample.ReadValue))!, true));
        Assert.Throws<NotSupportedException>(() =>
            new DelegateTypeFactory().CreateDelegateType(
                typeof(Sample).GetMethod(nameof(Sample.ReadValue))!));
        Assert.Throws<NotSupportedException>(() => PatchProcessor.CreateILGenerator());
        Assert.Throws<NotSupportedException>(() =>
            PatchProcessor.GetOriginalInstructions(
                typeof(Sample).GetMethod(nameof(Sample.ReadValue))!));

        var unavailable = HarmonyRegistry.SnapshotDiagnostics()
            .Where(diagnostic => diagnostic.Code == "HarmonyUnavailable")
            .Select(diagnostic => diagnostic.Api)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(unavailable, Does.Contain("AccessTools.FieldRefAccess"));
            Assert.That(unavailable, Does.Contain("MethodInvoker.GetHandler"));
            Assert.That(unavailable, Does.Contain("DelegateTypeFactory.CreateDelegateType"));
            Assert.That(unavailable, Does.Contain("PatchProcessor.CreateILGenerator"));
            Assert.That(unavailable, Does.Contain("PatchProcessor.GetOriginalInstructions"));
        });
    }

    private static bool Prefix() => true;

    private static int Add(ref int value, int amount)
    {
        value += amount;
        return value;
    }
}
