using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class ModDataDomainTests
{
    [Test]
    public void TokenHasStableBlittableShapeAndOwnerScopePublishesIt()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "domain-shape");
        var token = session.DomainToken;

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<ModDataDomainToken>(), Is.EqualTo(24));
            Assert.That(token.IsValid, Is.True);
            Assert.That(token.LoaderKind, Is.EqualTo(ModDataDomainLoaderKind.AndroidManaged));
            Assert.That(ModDataDomainRuntime.CurrentToken.IsValid, Is.False);
        });

        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
            Assert.That(ModDataDomainRuntime.CurrentToken, Is.EqualTo(token));

        Assert.That(ModDataDomainRuntime.CurrentToken.IsValid, Is.False);
        Retire(session, key);
    }

    [Test]
    public void StaticSlotsAreIsolatedAndNestedScopesRestoreTheCaller()
    {
        var sessionA = new ModRuntimeSession();
        var keyA = sessionA.BeginLoad(ModEntry.NativeLoaderKind, "domain-a");
        var sessionB = new ModRuntimeSession();
        var keyB = sessionB.BeginLoad("xphorror.PcModCompat", "domain-b");

        using (HookHelper.EnterOwnerScope(keyA.OwnerId, sessionA, keyA))
        {
            ModDataDomainRuntime.SetStaticSlot(17, "A");
            Assert.That(ModDataDomainRuntime.GetOrCreateStaticSlot(17, () => "wrong"), Is.EqualTo("A"));

            using (HookHelper.EnterOwnerScope(keyB.OwnerId, sessionB, keyB))
            {
                Assert.That(ModDataDomainRuntime.TryGetStaticSlot<string>(17, out _), Is.False);
                ModDataDomainRuntime.SetStaticSlot(17, "B");
                Assert.That(ModDataDomainRuntime.CurrentToken, Is.EqualTo(sessionB.DomainToken));
            }

            Assert.Multiple(() =>
            {
                Assert.That(ModDataDomainRuntime.CurrentToken, Is.EqualTo(sessionA.DomainToken));
                Assert.That(ModDataDomainRuntime.TryGetStaticSlot<string>(17, out var value), Is.True);
                Assert.That(value, Is.EqualTo("A"));
            });
        }

        Retire(sessionB, keyB);
        Retire(sessionA, keyA);
    }

    [Test]
    public void RetiredTokenIsRejectedAndReusedSlotChangesGeneration()
    {
        var firstSession = new ModRuntimeSession();
        var firstKey = firstSession.BeginLoad(ModEntry.NativeLoaderKind, "domain-reload");
        var firstToken = firstSession.DomainToken;
        Retire(firstSession, firstKey);

        Assert.Multiple(() =>
        {
            Assert.That(ModDataDomainRegistry.TryResolve(firstToken, out _), Is.False);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var _ = ModDataDomainRuntime.EnterScope(firstToken);
            });
        });

        var secondSession = new ModRuntimeSession();
        var secondKey = secondSession.BeginLoad(ModEntry.NativeLoaderKind, "domain-reload-2");
        var secondToken = secondSession.DomainToken;
        Assert.Multiple(() =>
        {
            Assert.That(secondToken.SlotIndex, Is.EqualTo(firstToken.SlotIndex));
            Assert.That(secondToken.Generation, Is.Not.EqualTo(firstToken.Generation));
            Assert.That(secondToken.ProcessCookie, Is.EqualTo(firstToken.ProcessCookie));
        });
        Retire(secondSession, secondKey);
    }

    [Test]
    public void ScopeRetainedPastRetirementCannotUseStaleDomain()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "domain-stale-scope");
        using var scope = ModDataDomainRuntime.EnterScope(session.DomainToken);
        ModDataDomainRuntime.SetStaticSlot(5, 17);

        Retire(session, key);

        Assert.Multiple(() =>
        {
            Assert.That(ModDataDomainRuntime.CurrentToken.IsValid, Is.False);
            Assert.Throws<InvalidOperationException>(() =>
                ModDataDomainRuntime.GetStaticSlot<int>(5));
            Assert.Throws<InvalidOperationException>(() =>
                ModDataDomainRuntime.SetStaticSlot(5, 29));
        });
    }

    [Test]
    public void ForgedCookieAndLoaderKindAreRejected()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "domain-forgery");
        var token = session.DomainToken;
        var forgedCookie = new ModDataDomainToken(
            token.ProcessCookie ^ 1UL,
            token.Generation,
            token.SlotIndex,
            token.LoaderKind);
        var forgedKind = new ModDataDomainToken(
            token.ProcessCookie,
            token.Generation,
            token.SlotIndex,
            ModDataDomainLoaderKind.PcCompat);

        Assert.Multiple(() =>
        {
            Assert.That(ModDataDomainRegistry.TryResolve(forgedCookie, out _), Is.False);
            Assert.That(ModDataDomainRegistry.TryResolve(forgedKind, out _), Is.False);
        });
        Retire(session, key);
    }

    [Test]
    public void DomainCallbackLeaseParticipatesInRuntimeQuiescence()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "domain-callback");
        Assert.That(session.TryPublishActive(key), Is.True);
        Assert.That(ModDataDomainRuntime.TryEnterCallback(session.DomainToken, out var lease), Is.True);
        Assert.That(ModDataDomainRuntime.CurrentToken, Is.EqualTo(session.DomainToken));

        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.False);
        Assert.That(ModDataDomainRuntime.TryEnterCallback(session.DomainToken, out _), Is.False);

        lease!.Dispose();
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(key), Is.True);
        Assert.That(ModDataDomainRuntime.CurrentToken.IsValid, Is.False);
    }

    [Test]
    public void StaticSlotTypeCannotChangeWithinOneDomain()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "domain-slot-type");
        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
        {
            ModDataDomainRuntime.SetStaticSlot(9, 42);
            Assert.Throws<InvalidOperationException>(
                () => ModDataDomainRuntime.SetStaticSlot(9, "wrong"));
        }
        Retire(session, key);
    }

    [Test]
    public void StaticSlotReferenceIsStableAndDomainLocal()
    {
        var sessionA = new ModRuntimeSession();
        var keyA = sessionA.BeginLoad(ModEntry.NativeLoaderKind, "domain-ref-a");
        var sessionB = new ModRuntimeSession();
        var keyB = sessionB.BeginLoad(ModEntry.NativeLoaderKind, "domain-ref-b");

        using (HookHelper.EnterOwnerScope(keyA.OwnerId, sessionA, keyA))
        {
            ref var value = ref ModDataDomainRuntime.GetStaticSlotReference<int>(23);
            value = 71;
            Assert.That(ModDataDomainRuntime.GetStaticSlot<int>(23), Is.EqualTo(71));
        }
        using (HookHelper.EnterOwnerScope(keyB.OwnerId, sessionB, keyB))
            Assert.That(ModDataDomainRuntime.GetStaticSlot<int>(23), Is.Zero);
        using (HookHelper.EnterOwnerScope(keyA.OwnerId, sessionA, keyA))
            Assert.That(ModDataDomainRuntime.GetStaticSlot<int>(23), Is.EqualTo(71));

        Retire(sessionB, keyB);
        Retire(sessionA, keyA);
    }

    [Test]
    public void OwnerAwareStaticSlotsAreIsolatedByClosedGenericOwner()
    {
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "domain-generic-owner");
        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
        {
            ModDataDomainRuntime.SetStaticSlotForOwner<int, GenericOwner<int>>(29, 11);
            ModDataDomainRuntime.SetStaticSlotForOwner<int, GenericOwner<string>>(29, 22);

            Assert.Multiple(() =>
            {
                Assert.That(
                    ModDataDomainRuntime.GetStaticSlotForOwner<int, GenericOwner<int>>(29),
                    Is.EqualTo(11));
                Assert.That(
                    ModDataDomainRuntime.GetStaticSlotForOwner<int, GenericOwner<string>>(29),
                    Is.EqualTo(22));
            });

            ref var intOwner = ref ModDataDomainRuntime
                .GetStaticSlotReferenceForOwner<int, GenericOwner<int>>(29);
            intOwner = 31;

            Assert.Multiple(() =>
            {
                Assert.That(
                    ModDataDomainRuntime.GetStaticSlotForOwner<int, GenericOwner<int>>(29),
                    Is.EqualTo(31));
                Assert.That(
                    ModDataDomainRuntime.GetStaticSlotForOwner<int, GenericOwner<string>>(29),
                    Is.EqualTo(22));
            });
        }
        Retire(session, key);
    }

    [Test]
    public void StaticInitializerFailureIsStickyWithinDomain()
    {
        Volatile.Write(ref _throwingInitializerCalls, 0);
        var session = new ModRuntimeSession();
        var key = session.BeginLoad(ModEntry.NativeLoaderKind, "domain-cctor-failure");
        var handle = typeof(ModDataDomainTests)
            .GetMethod(
                nameof(ThrowingInitializer),
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)!
            .MethodHandle;

        using (HookHelper.EnterOwnerScope(key.OwnerId, session, key))
        {
            Assert.Throws<InvalidOperationException>(() =>
                ModDataDomainRuntime.EnsureStaticTypeInitialized(31, handle));
            Assert.Throws<InvalidOperationException>(() =>
                ModDataDomainRuntime.EnsureStaticTypeInitialized(31, handle));
        }
        Assert.That(Volatile.Read(ref _throwingInitializerCalls), Is.EqualTo(1));
        Retire(session, key);
    }

    private static int _throwingInitializerCalls;

    private sealed class GenericOwner<T>
    {
    }

    private static void ThrowingInitializer()
    {
        Interlocked.Increment(ref _throwingInitializerCalls);
        throw new InvalidOperationException("expected domain initializer failure");
    }

    private static void Retire(ModRuntimeSession session, ModRuntimeKey key)
    {
        if (session.Snapshot().State == ModRuntimeLifecycleState.Loading)
            Assert.That(session.TryPublishActive(key), Is.True);
        Assert.That(session.TryBeginRetirement(key), Is.True);
        Assert.That(session.WaitForQuiescence(key, TimeSpan.Zero), Is.True);
        Assert.That(session.TryCompleteRetirement(key), Is.True);
    }
}
