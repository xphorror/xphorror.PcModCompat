using System.Buffers.Binary;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

public sealed class NativeModUnmanagedIdentityTests
{
    [Test]
    public void ElfIdentityReaderExtractsArm64AbiAndGnuBuildId()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"starray-elf-identity-{Guid.NewGuid():N}.so");
        var buildId = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray();
        File.WriteAllBytes(path, CreateElfWithBuildId(buildId));
        try
        {
            Assert.That(NativeModElfIdentityReader.TryRead(path, out var identity), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(identity.ElfClass, Is.EqualTo(2));
                Assert.That(identity.DataEncoding, Is.EqualTo(1));
                Assert.That(identity.Machine, Is.EqualTo(183));
                Assert.That(identity.OsAbi, Is.Zero);
                Assert.That(identity.AbiVersion, Is.Zero);
                Assert.That(identity.BuildId, Is.EqualTo(Convert.ToHexString(buildId).ToLowerInvariant()));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ElfIdentityReaderRejectsUnboundedProgramHeaderShape()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"starray-elf-bounds-{Guid.NewGuid():N}.so");
        var data = CreateElfWithBuildId([0x01, 0x02, 0x03, 0x04]);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(54, 2), 4097);
        File.WriteAllBytes(path, data);
        try
        {
            Assert.That(NativeModElfIdentityReader.TryRead(path, out _), Is.False);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void RuntimeGenerationRebindsLibrariesLoadedDuringDiscovery()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"starray-elf-generation-{Guid.NewGuid():N}.so");
        File.WriteAllBytes(path, CreateElfWithBuildId([0xaa, 0xbb, 0xcc, 0xdd]));
        var context = new NativeModAssemblyLoadContext("identity-mod", path);
        try
        {
            context.RegisterUnmanagedLibrary("identity", path, (nint)0x1234, (nint)0x8000);
            Assert.That(context.SnapshotUnmanagedLibraries().Single().LoadGeneration, Is.Zero);

            context.BindRuntimeGeneration(7);
            var identity = context.SnapshotUnmanagedLibraries().Single();
            Assert.Multiple(() =>
            {
                Assert.That(identity.ModId, Is.EqualTo("identity-mod"));
                Assert.That(identity.LoadGeneration, Is.EqualTo(7));
                Assert.That(identity.DlopenHandle, Is.EqualTo((nint)0x1234));
                Assert.That(identity.BaseAddress, Is.EqualTo((nint)0x8000));
                Assert.That(identity.CanonicalPath, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(identity.ContextRetired, Is.False);
            });
        }
        finally
        {
            context.Unload();
            File.Delete(path);
        }
    }

    [Test]
    public void ReleasingContextRetainsRetiredIdentityWithoutClaimingPhysicalUnload()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"starray-elf-retired-{Guid.NewGuid():N}.so");
        File.WriteAllBytes(path, CreateElfWithBuildId([0x10, 0x20, 0x30, 0x40]));
        var context = new NativeModAssemblyLoadContext("provisional-folder", path);
        context.RegisterUnmanagedLibrary("retired", path, (nint)0x4567, (nint)0x9000);
        var plugin = new IdentityPlugin();
        var session = new ModRuntimeSession();
        var state = new NativeModLoadState(
            path,
            context,
            typeof(NativeModUnmanagedIdentityTests).Assembly,
            plugin,
            session);
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, plugin.Id);
        state.BindRuntimeKey(key);
        try
        {
            Assert.That(state.CurrentUnmanagedLibraries.Single().LoadGeneration, Is.EqualTo(1));
            state.ReleaseContext();

            var retired = state.RetiredUnmanagedLibraries.Single();
            var retiredOwned = state.RetiredOwnedResources.Single();
            Assert.Multiple(() =>
            {
                Assert.That(state.CurrentUnmanagedLibraries, Is.Empty);
                Assert.That(state.CurrentOwnedResources, Is.Empty);
                Assert.That(retired.ModId, Is.EqualTo(plugin.Id));
                Assert.That(retired.LoadGeneration, Is.EqualTo(key.Generation));
                Assert.That(retired.ContextRetired, Is.True);
                Assert.That(retired.DlopenHandle, Is.EqualTo((nint)0x4567));
                Assert.That(retired.BaseAddress, Is.EqualTo((nint)0x9000));
                Assert.That(retiredOwned.Kind, Is.EqualTo(ModOwnedResourceKind.NativeLibrary));
                Assert.That(retiredOwned.Identity, Is.EqualTo(Path.GetFullPath(path)));
            });
        }
        finally
        {
            state.ReleaseContext();
            File.Delete(path);
        }
    }

    [Test]
    public void RetiringContextRejectsLateUnmanagedRegistration()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"starray-elf-retiring-{Guid.NewGuid():N}.so");
        File.WriteAllBytes(path, CreateElfWithBuildId([0x55, 0x66, 0x77, 0x88]));
        var context = new NativeModAssemblyLoadContext("retiring-mod", path);
        try
        {
            context.BeginRetirement();
            Assert.That(
                () => context.RegisterUnmanagedLibrary(
                    "late",
                    path,
                    (nint)0x1111,
                    (nint)0x2222),
                Throws.InvalidOperationException);
            Assert.That(context.SnapshotUnmanagedLibraries(), Is.Empty);
        }
        finally
        {
            context.Unload();
            File.Delete(path);
        }
    }

    [Test]
    public void ProcessMapParserFindsOnlySharedObjectsInsideModRoot()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "native map fixture"));
        var first = Path.Combine(root, "libfirst.so");
        var second = Path.Combine(root, "sub", "libsecond.so.1");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "liboutside.so");
        var text = string.Join('\n',
            $"70001000-70002000 r--p 00001000 00:00 1 {first}",
            $"70002000-70004000 r-xp 00002000 00:00 1 {first}",
            $"71000000-71001000 r-xp 00000000 00:00 2 {second} (deleted)",
            $"72000000-72001000 r-xp 00000000 00:00 3 {outside}",
            $"73000000-73001000 r-xp 00000000 00:00 4 {Path.Combine(root, "not-a-library.dat")}");

        var mappings = NativeModProcessMapReader.Parse(new StringReader(text), root);

        Assert.Multiple(() =>
        {
            Assert.That(mappings, Has.Count.EqualTo(2));
            Assert.That(mappings[0].CanonicalPath, Is.EqualTo(Path.GetFullPath(first)));
            Assert.That(mappings[0].BaseAddress, Is.EqualTo((nint)0x70000000));
            Assert.That(mappings[1].CanonicalPath, Is.EqualTo(Path.GetFullPath(second)));
            Assert.That(mappings[1].BaseAddress, Is.EqualTo((nint)0x71000000));
        });
    }

    private static byte[] CreateElfWithBuildId(byte[] buildId)
    {
        const int headerSize = 64;
        const int programHeaderSize = 56;
        var descriptorSize = Align4(buildId.Length);
        var noteSize = 12 + 4 + descriptorSize;
        var data = new byte[headerSize + programHeaderSize + noteSize];
        data[0] = 0x7f;
        data[1] = (byte)'E';
        data[2] = (byte)'L';
        data[3] = (byte)'F';
        data[4] = 2;
        data[5] = 1;
        data[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18, 2), 183);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32, 8), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(52, 2), headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(54, 2), programHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(56, 2), 1);

        var programHeader = data.AsSpan(headerSize, programHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[0..4], 4);
        BinaryPrimitives.WriteUInt64LittleEndian(
            programHeader[8..16],
            headerSize + programHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[32..40], (ulong)noteSize);

        var note = data.AsSpan(headerSize + programHeaderSize, noteSize);
        BinaryPrimitives.WriteUInt32LittleEndian(note[0..4], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(note[4..8], (uint)buildId.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(note[8..12], 3);
        note[12] = (byte)'G';
        note[13] = (byte)'N';
        note[14] = (byte)'U';
        buildId.CopyTo(note[16..]);
        return data;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private sealed class IdentityPlugin : IModPlugin
    {
        public string Id => "identity-plugin";
        public string Name => Id;
        public string Version => "1";
        public string Author => "tests";
        public string Description => "tests";
        public IReadOnlyList<string> Dependencies => Array.Empty<string>();
        public void OnLoad() { }
        public void OnUnload() { }
    }
}
