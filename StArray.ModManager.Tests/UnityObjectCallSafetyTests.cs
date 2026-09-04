using System.Runtime.InteropServices;
using StArray.ModManager.Il2Cpp;

namespace StArray.ModManager.Tests;

public sealed class UnityObjectCallSafetyTests
{
    [TestCase("Destroy", new[] { "UnityEngine.Object" }, (int)UnityObjectCallKind.DestroyObject)]
    [TestCase("Destroy", new[] { "UnityEngine.Object", "System.Single" }, (int)UnityObjectCallKind.DestroyObjectDelayed)]
    [TestCase("DestroyImmediate", new[] { "UnityEngine.Object" }, (int)UnityObjectCallKind.DestroyImmediateObject)]
    [TestCase("DestroyImmediate", new[] { "UnityEngine.Object", "System.Boolean" }, (int)UnityObjectCallKind.DestroyImmediateObjectAllowAssets)]
    [TestCase("DontDestroyOnLoad", new[] { "UnityEngine.Object" }, (int)UnityObjectCallKind.DontDestroyOnLoadObject)]
    [TestCase("op_Implicit", new[] { "UnityEngine.Object" }, (int)UnityObjectCallKind.ObjectImplicit)]
    public void ClassifiesGuardedUnityObjectCalls(
        string methodName,
        string[] parameterTypes,
        int expected)
    {
        Assert.That(
            UnityObjectCallSafety.Classify(
                "UnityEngine",
                "Object",
                methodName,
                parameterTypes),
            Is.EqualTo((UnityObjectCallKind)expected));
    }

    [Test]
    public void RejectsTombstoneCachedPointerBeforeCallingUnityNative()
    {
        var reader = new FakeMemoryReader();
        var managedObject = unchecked((nint)0x7CB5E2CA20);
        reader.Pointers[managedObject] = unchecked((nint)0x7DB024FD30);
        reader.Pointers[managedObject + 0x10] = unchecked((nint)0x7400000001);
        reader.Readable.Add((unchecked((nint)0x7DB024FD30), (nuint)nint.Size));

        Assert.That(
            UnityObjectCallSafety.IsCallableUnityObject(managedObject, 0x10, reader),
            Is.False);
    }

    [Test]
    public void AcceptsLiveUnityObjectWithReadableNativeInstance()
    {
        var reader = new FakeMemoryReader();
        var managedObject = (nint)0x1000;
        var klass = (nint)0x2000;
        var nativeObject = (nint)0x3000;
        reader.Pointers[managedObject] = klass;
        reader.Pointers[managedObject + 0x10] = nativeObject;
        reader.Readable.Add((klass, (nuint)nint.Size));
        reader.Readable.Add((nativeObject, 12));

        Assert.That(
            UnityObjectCallSafety.IsCallableUnityObject(managedObject, 0x10, reader),
            Is.True);
    }

    [Test]
    public void TreatsDestroyedUnityObjectAsNull()
    {
        var reader = new FakeMemoryReader();
        var managedObject = (nint)0x1000;
        var klass = (nint)0x2000;
        reader.Pointers[managedObject] = klass;
        reader.Pointers[managedObject + 0x10] = nint.Zero;
        reader.Readable.Add((klass, (nuint)nint.Size));

        Assert.That(
            UnityObjectCallSafety.IsCallableUnityObject(managedObject, 0x10, reader),
            Is.False);
    }

    [Test]
    public void GuardStubCanBeBoundAsThirdPartyPrivateDelegate()
    {
        var pointer = UnityObjectCallSafety.GetStubPointerForTesting(
            UnityObjectCallKind.DestroyObject);

        Assert.Multiple(() =>
        {
            Assert.That(pointer, Is.Not.EqualTo(nint.Zero));
            ThirdPartyDestroyDelegate? callback = null;
            Assert.DoesNotThrow(() => callback =
                Marshal.GetDelegateForFunctionPointer<ThirdPartyDestroyDelegate>(pointer));
            Assert.That(callback, Is.Not.Null);
            Assert.DoesNotThrow(() => callback!(nint.Zero, nint.Zero));
        });
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ThirdPartyDestroyDelegate(nint instance, nint methodInfo);

    private sealed class FakeMemoryReader : IProcessMemoryReader
    {
        internal Dictionary<nint, nint> Pointers { get; } = new();
        internal HashSet<(nint Address, nuint Size)> Readable { get; } = new();

        public bool TryRead(nint address, Span<byte> destination) => false;

        public bool TryReadPointer(nint address, out nint value)
            => Pointers.TryGetValue(address, out value);

        public bool IsReadable(nint address, nuint size)
            => Readable.Contains((address, size));
    }
}
