using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public class PcCompatHarmonySynchronousPrefixTests
{
    private static int s_lastValue;
    private static int s_lastState;
    private static int s_observerAfterSkipCount;
    private static MethodBase? s_lastOriginalMethod;
    private static object?[]? s_lastPostfixArgs;
    private static bool s_lastPostfixBooleanResult;
    private static SyntheticMode s_lastPostfixEnumResult;

    [Test]
    public void BinaryRecipeRoundTripsSynchronousPrefixIdentity()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PrefixRule(nameof(BlockOriginalPrefix), "System.Int32");
        var path = WriteRecipe(manifest, rule);
        try
        {
            var read = PcCompatManagedEventRecipeReader.Read(path).Single();
            Assert.Multiple(() =>
            {
                Assert.That(read.PatchKind, Is.EqualTo(PcCompatPatchKind.Prefix));
                Assert.That(read.PatchId, Is.EqualTo(1));
                Assert.That(read.CallbackType, Is.EqualTo(typeof(PcCompatHarmonySynchronousPrefixTests).FullName));
                Assert.That(read.CallbackMethod, Is.EqualTo(nameof(BlockOriginalPrefix)));
                Assert.That(read.ParameterTypes, Is.EqualTo(new[] { "System.Int32" }));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void ManagedBindingUsesBooleanPrefixResultAsOriginalDecision()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PrefixRule(nameof(BlockOriginalPrefix), "System.Int32");
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(BlockOriginalPrefix));
            var prefixFrame = CreateInvocation(
                PcCompatManagedPrefixResultKind.Void,
                37);
            var invocation = new object?[]
            {
                1u,
                prefixFrame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                true
            };

            s_lastValue = 0;
            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, invocation)!;

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(((PcCompatManagedPrefixInvocationV2)invocation[1]!).RunOriginal, Is.EqualTo(0u));
                Assert.That(s_lastValue, Is.EqualTo(37));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void PrefixExceptionFailsOpenAndLeavesOriginalEnabled()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PrefixRule(nameof(ThrowingPrefix), "System.Int32");
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(ThrowingPrefix));
            var prefixFrame = CreateInvocation(
                PcCompatManagedPrefixResultKind.Void,
                12);
            var invocation = new object?[]
            {
                1u,
                prefixFrame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                false
            };

            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, invocation)!;
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.False);
                Assert.That(((PcCompatManagedPrefixInvocationV2)invocation[1]!).RunOriginal, Is.EqualTo(1u));
                Assert.That(stats.FailedCallbacks, Is.EqualTo(1));
                Assert.That(stats.LastError, Does.Contain(nameof(InvalidOperationException)));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void PrimitiveRefAndOutPrefixArgumentsAreWrittenBackToInvocationFrame()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PrefixRule(
            nameof(MutateRefAndOutPrefix),
            "System.Int32",
            "System.Single");
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(MutateRefAndOutPrefix));
            var frame = CreateInvocation(PcCompatManagedPrefixResultKind.Void, 7, 0);
            var invocation = new object?[]
            {
                1u,
                frame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                true
            };

            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, invocation)!;
            var updated = (PcCompatManagedPrefixInvocationV2)invocation[1]!;
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True, stats.SkipReasons + " " + stats.LastError);
                Assert.That(updated.GetArgument(0), Is.EqualTo(19ul));
                Assert.That(BitConverter.Int32BitsToSingle(unchecked((int)updated.GetArgument(1))), Is.EqualTo(1.25f));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void RefResultPrefixCanSkipOriginalWithManagedResult()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PrefixRuleWithReturn(
            nameof(SetManagedResultAndSkip),
            "System.Int32");
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(SetManagedResultAndSkip));
            var frame = CreateInvocation(PcCompatManagedPrefixResultKind.Int32);
            var invocation = new object?[]
            {
                1u,
                frame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                true
            };

            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, invocation)!;
            var updated = (PcCompatManagedPrefixInvocationV2)invocation[1]!;
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True, stats.SkipReasons + " " + stats.LastError);
                Assert.That(updated.RunOriginal, Is.EqualTo(0u));
                Assert.That(updated.ResultValid, Is.EqualTo(1u));
                Assert.That(updated.ResultValue, Is.EqualTo(73ul));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void PrefixStateIsPairedWithDeferredPostfixByInvocationId()
    {
        var (manifest, _) = ReadSampleManifest();
        var prefixRule = StateRule(nameof(CaptureStatePrefix), "Prefix", 11);
        var postfixRule = StateRule(nameof(ConsumeStatePostfix), "Postfix", 12);
        var path = WriteRecipe(manifest, prefixRule, postfixRule);
        try
        {
            var dispatcher = BuildDispatcherMany(
                path,
                new RegistrationSpec(prefixRule, nameof(CaptureStatePrefix), "Prefix"),
                new RegistrationSpec(postfixRule, nameof(ConsumeStatePostfix), "Postfix"));
            var frame = CreateInvocation(PcCompatManagedPrefixResultKind.Void);
            frame.InvocationId = 4242;
            var prefixInvocation = new object?[]
            {
                11u,
                frame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                true
            };
            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, prefixInvocation)!;

            var record = new byte[PcCompatManagedCallbackDispatcher.EventRecordSize];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 12);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(152), 4242);
            s_lastState = 0;
            dispatcher.GetType()
                .GetMethod("DispatchRecord", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, new object?[]
                {
                    record,
                    0,
                    new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                    null,
                    null
                });

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(s_lastState, Is.EqualTo(41));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void StateTypeConflictRejectsPostfixBindingAtBuildTime()
    {
        var (manifest, _) = ReadSampleManifest();
        var prefixRule = StateRule(nameof(CaptureStatePrefix), "Prefix", 11);
        var postfixRule = StateRule(nameof(ConsumeMismatchedStatePostfix), "Postfix", 13);
        var path = WriteRecipe(manifest, prefixRule, postfixRule);
        try
        {
            var dispatcher = BuildDispatcherMany(
                path,
                new RegistrationSpec(prefixRule, nameof(CaptureStatePrefix), "Prefix"),
                new RegistrationSpec(postfixRule, nameof(ConsumeMismatchedStatePostfix), "Postfix"));
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(stats.BoundCallbacks, Is.EqualTo(1));
                Assert.That(stats.SkipReasons, Does.Contain("__state type System.String conflicts with Prefix state type System.Int32"));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestCase("Prefix", 41u)]
    [TestCase("Postfix", 42u)]
    public void OriginalMethodUsesRegistrationIdentity(string kind, uint patchId)
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = OriginalMethodRule(nameof(CaptureOriginalMethod), kind, patchId);
        var path = WriteRecipe(manifest, rule);
        var original = typeof(SyntheticFieldProxy).GetMethod(nameof(SyntheticFieldProxy.Run))!;
        try
        {
            var dispatcher = BuildDispatcherMany(
                path,
                new RegistrationSpec(rule, nameof(CaptureOriginalMethod), kind, OriginalMethod: original));
            s_lastOriginalMethod = null;

            if (kind == "Prefix")
            {
                var invocation = new object?[]
                {
                    patchId,
                    CreateInvocation(PcCompatManagedPrefixResultKind.Void),
                    new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                    true
                };
                var handled = (bool)dispatcher.GetType()
                    .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dispatcher, invocation)!;
                Assert.That(handled, Is.True);
            }
            else
            {
                var record = new byte[PcCompatManagedCallbackDispatcher.EventRecordSize];
                BinaryPrimitives.WriteUInt32LittleEndian(record, patchId);
                dispatcher.GetType()
                    .GetMethod("DispatchRecord", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(dispatcher, new object?[]
                    {
                        record,
                        0,
                        new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                        null,
                        null
                    });
            }

            Assert.That(s_lastOriginalMethod, Is.SameAs(original));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void PrefixArgsArrayWritesPrimitiveEnumAndProxySlotsBack()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = ArgsRule(nameof(MutateArgsPrefix));
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(MutateArgsPrefix));
            var frame = CreateInvocation(
                PcCompatManagedPrefixResultKind.Void,
                7,
                BitConverter.SingleToUInt32Bits(1.25f),
                (uint)SyntheticMode.First,
                0x1234);
            var invocation = new object?[]
            {
                51u,
                frame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                true
            };

            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, invocation)!;
            var updated = (PcCompatManagedPrefixInvocationV2)invocation[1]!;

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That((int)updated.GetArgument(0), Is.EqualTo(99));
                Assert.That(BitConverter.Int32BitsToSingle((int)updated.GetArgument(1)), Is.EqualTo(2.5f));
                Assert.That((SyntheticMode)updated.GetArgument(2), Is.EqualTo(SyntheticMode.Second));
                Assert.That(updated.GetArgument(3), Is.EqualTo(0x9876));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void DeferredPostfixArgsAreReadOnlySnapshotsOfPrimitiveEnumAndProxySlots()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PostfixArgsRule(
            nameof(ReadAndMutateArgsPostfix),
            52,
            typeof(SyntheticArgsTarget).FullName!,
            typeof(int).FullName!,
            typeof(float).FullName!,
            typeof(SyntheticMode).FullName!,
            typeof(SyntheticFieldProxy).FullName!);
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(ReadAndMutateArgsPostfix), "Postfix");
            var record = new byte[PcCompatManagedCallbackDispatcher.EventRecordSize];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 52);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 4);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 7);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), BitConverter.SingleToUInt32Bits(1.25f));
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), (uint)SyntheticMode.First);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), 0x1234);
            s_lastPostfixArgs = null;

            dispatcher.GetType()
                .GetMethod("DispatchRecord", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, new object?[]
                {
                    record,
                    0,
                    new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                    null,
                    null
                });

            var args = s_lastPostfixArgs!;
            Assert.Multiple(() =>
            {
                Assert.That(args, Has.Length.EqualTo(4));
                Assert.That(args[0], Is.EqualTo(7));
                Assert.That(args[1], Is.EqualTo(1.25f));
                Assert.That(args[2], Is.EqualTo(SyntheticMode.First));
                Assert.That(((SyntheticFieldProxy)args[3]!).Pointer, Is.EqualTo((IntPtr)0x1234));
                Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(16)), Is.EqualTo(7ul));
                Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24)), Is.EqualTo((ulong)BitConverter.SingleToUInt32Bits(1.25f)));
                Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32)), Is.EqualTo((ulong)SyntheticMode.First));
                Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40)), Is.EqualTo(0x1234ul));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void DeferredPostfixArgsRejectRefOrOutTargetParameters()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PostfixArgsRule(
            nameof(ReadAndMutateArgsPostfix),
            53,
            typeof(SyntheticRefArgsTarget).FullName!,
            "System.Int32&");
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(ReadAndMutateArgsPostfix), "Postfix");
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(stats.BoundCallbacks, Is.EqualTo(0));
                Assert.That(stats.SkipReasons, Does.Contain("deferred Postfix __args cannot expose ref/out target parameters"));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void DeferredPostfixArgsRejectMoreThanSixTargetParameters()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PostfixArgsRule(
            nameof(ReadAndMutateArgsPostfix),
            54,
            "SyntheticSevenArgsTarget",
            Enumerable.Repeat(typeof(int).FullName!, 7).ToArray());
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(ReadAndMutateArgsPostfix), "Postfix");
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(stats.BoundCallbacks, Is.EqualTo(0));
                Assert.That(stats.SkipReasons, Does.Contain("exceeds invocation capacity 6"));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void DeferredPostfixArgsRejectUnsupportedStructSlots()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PostfixArgsRule(
            nameof(ReadAndMutateArgsPostfix),
            55,
            typeof(SyntheticStructArgsTarget).FullName!,
            typeof(SyntheticValue).FullName!);
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(ReadAndMutateArgsPostfix), "Postfix");
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(stats.BoundCallbacks, Is.EqualTo(0));
                Assert.That(stats.SkipReasons, Does.Contain("unsupported callback parameter type"));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void DeferredPostfixReadsBooleanAndEnumResultsByValue()
    {
        var (manifest, _) = ReadSampleManifest();
        var booleanRule = PostfixResultRule(
            nameof(CaptureBooleanResultPostfix),
            56,
            typeof(SyntheticBooleanResultTarget).FullName!,
            typeof(bool).FullName!);
        var enumRule = PostfixResultRule(
            nameof(CaptureEnumResultPostfix),
            57,
            typeof(SyntheticEnumResultTarget).FullName!,
            typeof(SyntheticMode).FullName!);
        var path = WriteRecipe(manifest, booleanRule, enumRule);
        try
        {
            var dispatcher = BuildDispatcherMany(
                path,
                new RegistrationSpec(booleanRule, nameof(CaptureBooleanResultPostfix), "Postfix"),
                new RegistrationSpec(enumRule, nameof(CaptureEnumResultPostfix), "Postfix"));
            s_lastPostfixBooleanResult = false;
            s_lastPostfixEnumResult = default;

            DispatchPostfixResult(
                dispatcher,
                56,
                PcCompatManagedPrefixResultKind.Boolean,
                1);
            DispatchPostfixResult(
                dispatcher,
                57,
                PcCompatManagedPrefixResultKind.Int32,
                (uint)SyntheticMode.Second);

            Assert.Multiple(() =>
            {
                Assert.That(s_lastPostfixBooleanResult, Is.True);
                Assert.That(s_lastPostfixEnumResult, Is.EqualTo(SyntheticMode.Second));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void SharedGenericPostfixSkipsNonEnumBoxAndStillDispatchesRealEnum()
    {
        var (manifest, _) = ReadSampleManifest();
        const uint patchId = 73;
        var rule = PostfixArgsRule(
            nameof(CaptureBoxedEnumPostfix),
            patchId,
            typeof(SyntheticBoxedEnumTarget).FullName!,
            typeof(Enum).FullName!);
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(
                path,
                rule,
                nameof(CaptureBoxedEnumPostfix),
                "Postfix");
            var record = new byte[PcCompatManagedCallbackDispatcher.EventRecordSize];
            BinaryPrimitives.WriteUInt32LittleEndian(record, patchId);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 0x1234);
            var boxedType = typeof(bool).FullName!;
            var boxedValue = 1L;
            bool ReadBoxed(IntPtr boxed, out string? typeName, out long value)
            {
                typeName = boxedType;
                value = boxedValue;
                return boxed == (IntPtr)0x1234;
            }

            s_lastBoxedEnum = null;
            DispatchRecord(dispatcher, record, ReadBoxed);
            var afterNonEnum = SnapshotStats(dispatcher);
            boxedType = typeof(SyntheticMode).FullName!;
            boxedValue = (long)SyntheticMode.Second;
            DispatchRecord(dispatcher, record, ReadBoxed);
            var afterEnum = SnapshotStats(dispatcher);

            Assert.Multiple(() =>
            {
                Assert.That(s_lastBoxedEnum, Is.EqualTo(SyntheticMode.Second));
                Assert.That(afterNonEnum.FailedCallbacks, Is.Zero);
                Assert.That(afterNonEnum.DispatchedCallbacks, Is.Zero);
                Assert.That(afterEnum.FailedCallbacks, Is.Zero);
                Assert.That(afterEnum.DispatchedCallbacks, Is.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void SharedGenericPrefixSkipsNonEnumBoxWithoutBlockingOriginal()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PrefixRule(nameof(CaptureBoxedEnumPrefix), typeof(Enum).FullName!);
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(CaptureBoxedEnumPrefix));
            var frame = CreateInvocation(PcCompatManagedPrefixResultKind.Void, 0x1234);
            bool ReadBoxed(IntPtr boxed, out string? typeName, out long value)
            {
                typeName = typeof(float).FullName;
                value = 0;
                return boxed == (IntPtr)0x1234;
            }
            var invocation = new object?[]
            {
                1u,
                frame,
                new PcCompatManagedBoxedValueHandler(ReadBoxed),
                true
            };

            s_lastBoxedEnum = null;
            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, invocation)!;
            var updated = (PcCompatManagedPrefixInvocationV2)invocation[1]!;
            var stats = SnapshotStats(dispatcher);

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.False);
                Assert.That(updated.RunOriginal, Is.EqualTo(1u));
                Assert.That(s_lastBoxedEnum, Is.Null);
                Assert.That(stats.FailedCallbacks, Is.Zero);
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void DeferredPostfixRejectsResultWriteBack()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PostfixResultRule(
            nameof(MutateResultPostfix),
            58,
            typeof(SyntheticBooleanResultTarget).FullName!,
            typeof(bool).FullName!);
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(MutateResultPostfix), "Postfix");
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(stats.BoundCallbacks, Is.EqualTo(0));
                Assert.That(stats.SkipReasons, Does.Contain("deferred Postfix cannot write back ref/out __result"));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void ObserverPrefixStillRunsAfterOriginalWasSkipped()
    {
        var (manifest, _) = ReadSampleManifest();
        var stopRule = StateRule(nameof(StopOriginalPrefix), "Prefix", 21);
        var observerRule = StateRule(nameof(ObserveAfterSkipPrefix), "Prefix", 22);
        var path = WriteRecipe(manifest, stopRule, observerRule);
        try
        {
            var dispatcher = BuildDispatcherMany(
                path,
                new RegistrationSpec(stopRule, nameof(StopOriginalPrefix), "Prefix"),
                new RegistrationSpec(observerRule, nameof(ObserveAfterSkipPrefix), "Prefix"));
            var frame = CreateInvocation(PcCompatManagedPrefixResultKind.Void);
            var method = dispatcher.GetType().GetMethod(
                "TryDispatchSynchronousPrefix",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var stopInvocation = new object?[]
            {
                21u,
                frame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                true
            };
            Assert.That((bool)method.Invoke(dispatcher, stopInvocation)!, Is.True);
            frame = (PcCompatManagedPrefixInvocationV2)stopInvocation[1]!;
            s_observerAfterSkipCount = 0;
            var observerInvocation = new object?[]
            {
                22u,
                frame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                false
            };
            Assert.That((bool)method.Invoke(dispatcher, observerInvocation)!, Is.True);
            frame = (PcCompatManagedPrefixInvocationV2)observerInvocation[1]!;

            Assert.Multiple(() =>
            {
                Assert.That(frame.RunOriginal, Is.EqualTo(0u));
                Assert.That(s_observerAfterSkipCount, Is.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void PrefixFieldRefWritesThroughGeneratedProxyAccessor()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = FieldRule(nameof(MutateProxyField));
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(MutateProxyField));
            SyntheticFieldProxy.Value = 5;
            var frame = CreateInvocation(PcCompatManagedPrefixResultKind.Void);
            frame.Instance = 0x1234;
            var invocation = new object?[]
            {
                31u,
                frame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                true
            };

            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, invocation)!;

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(SyntheticFieldProxy.Value, Is.EqualTo(14));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void PrefixRefInstanceWritesReplacementPointerBackToInvocationFrame()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = FieldRule(nameof(ReplaceInstancePrefix));
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(ReplaceInstancePrefix));
            var frame = CreateInvocation(PcCompatManagedPrefixResultKind.Void);
            frame.Instance = 0x1234;
            var invocation = new object?[]
            {
                31u,
                frame,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                true
            };

            var handled = (bool)dispatcher.GetType()
                .GetMethod("TryDispatchSynchronousPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dispatcher, invocation)!;
            var updated = (PcCompatManagedPrefixInvocationV2)invocation[1]!;

            Assert.Multiple(() =>
            {
                Assert.That(handled, Is.True);
                Assert.That(updated.Instance, Is.EqualTo(0x9876ul));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void DeferredPostfixRejectsRefInstanceWriteBack()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = OriginalMethodRule(nameof(MutateInstancePostfix), "Postfix", 59);
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(MutateInstancePostfix), "Postfix");
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(stats.BoundCallbacks, Is.EqualTo(0));
                Assert.That(stats.SkipReasons, Does.Contain("deferred Postfix cannot write back ref/out __instance"));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void PrefixRefInstanceRejectsStaticTarget()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = StaticInstanceRule(nameof(ReplaceStaticInstancePrefix));
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(path, rule, nameof(ReplaceStaticInstancePrefix));
            var stats = (PcCompatManagedEventDispatchStats)dispatcher.GetType()
                .GetMethod("SnapshotStats")!
                .Invoke(dispatcher, null)!;

            Assert.Multiple(() =>
            {
                Assert.That(stats.BoundCallbacks, Is.EqualTo(0));
                Assert.That(stats.SkipReasons, Does.Contain("ref/out __instance cannot bind to a static target"));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void NativeDispatchersDecideBeforeOriginalAndForwardMethodInfo()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "Android", "library", "src", "main", "cpp", "core", "pccompat_hook_rules.cpp"));
        var dispatchers = new[]
        {
            "dispatcher_instance_void0", "dispatcher_instance_void1", "dispatcher_instance_void_int1",
            "dispatcher_instance_void_ptr_float_int", "dispatcher_instance_void3",
            "dispatcher_instance_void_bool_bool_ptr_bool", "dispatcher_instance_bool1",
            "dispatcher_instance_bool2", "dispatcher_instance_bool_bool_int", "dispatcher_static_void1",
            "dispatcher_static_int_float_float_bool_float_float_double", "dispatcher_instance_void_color1",
            "dispatcher_instance_void_int_bool", "dispatcher_instance_void_ptr_bool"
        };

        foreach (var dispatcher in dispatchers)
        {
            var start = source.IndexOf(dispatcher + "(", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), dispatcher);
            var end = source.IndexOf("\n}\n", start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start), dispatcher);
            var body = source[start..end];
            var gate = body.IndexOf("pccompat_runtime_enabled", StringComparison.Ordinal);
            var prefix = body.IndexOf("run_managed_prefix_rules", StringComparison.Ordinal);
            var bypassOriginal = body.IndexOf("original(", gate, StringComparison.Ordinal);
            var guardedOriginal = body.IndexOf("original(", prefix, StringComparison.Ordinal);
            var instanceRefresh = body.IndexOf("self = args.instance;", prefix, StringComparison.Ordinal);
            if (dispatcher == "dispatcher_instance_void_color1")
                instanceRefresh = body.IndexOf("self = color_args.instance;", prefix, StringComparison.Ordinal);
            Assert.Multiple(() =>
            {
                Assert.That(gate, Is.GreaterThanOrEqualTo(0), dispatcher);
                Assert.That(prefix, Is.GreaterThanOrEqualTo(0), dispatcher);
                Assert.That(gate, Is.LessThan(prefix), dispatcher);
                Assert.That(bypassOriginal, Is.GreaterThan(gate).And.LessThan(prefix), dispatcher);
                Assert.That(guardedOriginal, Is.GreaterThan(prefix), dispatcher);
                Assert.That(body, Does.Contain("method_info"), dispatcher);
                if (dispatcher.StartsWith("dispatcher_instance_", StringComparison.Ordinal))
                {
                    Assert.That(instanceRefresh, Is.GreaterThan(prefix), dispatcher);
                    Assert.That(instanceRefresh, Is.LessThan(guardedOriginal), dispatcher);
                }
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<PcCompatManagedPrefixInvocationV2>(), Is.EqualTo(96));
            Assert.That(source, Does.Contain("sizeof(PcCompatManagedPrefixInvocationV2) == 96"));
            Assert.That(source, Does.Contain("modmanager_pccompat_get_managed_prefix_invocation_size"));
            Assert.That(source, Does.Contain("invocation.arguments[index] = args.raw_args[index]"));
            Assert.That(source, Does.Contain("args.raw_args[index] = invocation.arguments[index]"));
            Assert.That(source, Does.Contain("args.instance = reinterpret_cast<void *>("));
            Assert.That(source, Does.Contain("refresh_fixed_args_after_managed_prefix(args)"));
            Assert.That(source, Does.Contain("modmanager_pccompat_set_managed_prefix_callback"));
            Assert.That(source, Does.Contain("modmanager_pccompat_read_bundle_mod_id"));
            Assert.That(source, Does.Contain("g_managed_prefix_callback_depth >= 32"));
            Assert.That(source, Does.Contain("in_flight_prefixes"));
            Assert.That(source, Does.Contain("modmanager_pccompat_begin_managed_prefix_order_plan"));
            Assert.That(source, Does.Contain("modmanager_pccompat_add_managed_prefix_order"));
            Assert.That(source, Does.Contain("modmanager_pccompat_commit_managed_prefix_order_plan"));
            Assert.That(source, Does.Contain("modmanager_pccompat_begin_managed_postfix_order_plan"));
            Assert.That(source, Does.Contain("modmanager_pccompat_add_managed_postfix_order"));
            Assert.That(source, Does.Contain("modmanager_pccompat_commit_managed_postfix_order_plan"));
            Assert.That(source, Does.Contain("build_managed_prefix_dispatch_snapshot"));
            Assert.That(source, Does.Contain("build_managed_event_dispatch_snapshot"));
            Assert.That(source, Does.Contain("managed_prefix_before"));
            Assert.That(source, Does.Contain("managed_prefix_after"));
            Assert.That(source, Does.Contain("managed_event_before"));
            Assert.That(source, Does.Contain("managed_event_after"));
        });
    }

    [Test]
    public void ManagedBindingPublishesRuntimeOwnerAndOrderingPlan()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PrefixRule(nameof(BlockOriginalPrefix), "System.Int32");
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(
                path,
                rule,
                nameof(BlockOriginalPrefix),
                owner: "owner.current",
                priority: 700,
                registrationIndex: 42,
                before: ["owner.later"],
                after: ["owner.earlier"]);
            var plan = (IReadOnlyList<PcCompatManagedPrefixOrderEntry>)dispatcher.GetType()
                .GetProperty("PrefixOrderPlan")!
                .GetValue(dispatcher)!;

            var entry = plan.Single();
            Assert.Multiple(() =>
            {
                Assert.That(entry.PatchId, Is.EqualTo(1));
                Assert.That(entry.Owner, Is.EqualTo("owner.current"));
                Assert.That(entry.Priority, Is.EqualTo(700));
                Assert.That(entry.RegistrationIndex, Is.EqualTo(42));
                Assert.That(entry.Before, Is.EqualTo(new[] { "owner.later" }));
                Assert.That(entry.After, Is.EqualTo(new[] { "owner.earlier" }));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Test]
    public void ManagedPostfixBindingPublishesRuntimeOwnerAndOrderingPlan()
    {
        var (manifest, _) = ReadSampleManifest();
        var rule = PostfixRule(nameof(PostfixOrderCallback));
        var path = WriteRecipe(manifest, rule);
        try
        {
            var dispatcher = BuildDispatcher(
                path,
                rule,
                nameof(PostfixOrderCallback),
                kind: "Postfix",
                owner: "owner.postfix",
                priority: 650,
                registrationIndex: 77,
                before: ["owner.postfix-later"],
                after: ["owner.postfix-earlier"]);
            var plan = (IReadOnlyList<PcCompatManagedPostfixOrderEntry>)dispatcher.GetType()
                .GetProperty("PostfixOrderPlan")!
                .GetValue(dispatcher)!;

            var entry = plan.Single();
            Assert.Multiple(() =>
            {
                Assert.That(entry.PatchId, Is.EqualTo(1));
                Assert.That(entry.Owner, Is.EqualTo("owner.postfix"));
                Assert.That(entry.Priority, Is.EqualTo(650));
                Assert.That(entry.RegistrationIndex, Is.EqualTo(77));
                Assert.That(entry.Before, Is.EqualTo(new[] { "owner.postfix-later" }));
                Assert.That(entry.After, Is.EqualTo(new[] { "owner.postfix-earlier" }));
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    private static bool BlockOriginalPrefix(int value)
    {
        s_lastValue = value;
        return false;
    }

    private static void ThrowingPrefix(int value)
        => throw new InvalidOperationException($"prefix failed for {value}");

    private static bool MutateRefAndOutPrefix(ref int value, out float ratio)
    {
        value += 12;
        ratio = 1.25f;
        return true;
    }

    private static bool SetManagedResultAndSkip(ref int __result)
    {
        __result = 73;
        return false;
    }

    private static void CaptureStatePrefix(out int __state)
        => __state = 41;

    private static void ConsumeStatePostfix(int __state)
        => s_lastState = __state;

    private static void ConsumeMismatchedStatePostfix(string __state)
    {
    }

    private static void CaptureOriginalMethod(MethodBase __originalMethod)
        => s_lastOriginalMethod = __originalMethod;

    private static void MutateArgsPrefix(object[] __args)
    {
        Assert.Multiple(() =>
        {
            Assert.That(__args[0], Is.EqualTo(7));
            Assert.That(__args[1], Is.EqualTo(1.25f));
            Assert.That(__args[2], Is.EqualTo(SyntheticMode.First));
            Assert.That(((SyntheticFieldProxy)__args[3]).Pointer, Is.EqualTo((IntPtr)0x1234));
        });
        __args[0] = 99;
        __args[1] = 2.5f;
        __args[2] = SyntheticMode.Second;
        __args[3] = new SyntheticFieldProxy((IntPtr)0x9876);
    }

    private static void ReadAndMutateArgsPostfix(object[] __args)
    {
        s_lastPostfixArgs = __args.ToArray();
        __args[0] = 99;
        __args[1] = 2.5f;
        __args[2] = SyntheticMode.Second;
        __args[3] = new SyntheticFieldProxy((IntPtr)0x9876);
    }

    private static void CaptureBooleanResultPostfix(bool __result)
        => s_lastPostfixBooleanResult = __result;

    private static void CaptureEnumResultPostfix(SyntheticMode __result)
        => s_lastPostfixEnumResult = __result;

    private static Enum? s_lastBoxedEnum;

    private static void CaptureBoxedEnumPostfix(Enum value)
        => s_lastBoxedEnum = value;

    private static void CaptureBoxedEnumPrefix(Enum value)
        => s_lastBoxedEnum = value;

    private static void MutateResultPostfix(ref bool __result)
        => __result = false;

    private static bool StopOriginalPrefix() => false;

    private static void ObserveAfterSkipPrefix()
        => ++s_observerAfterSkipCount;

    private static void MutateProxyField(ref int ___InstanceValue)
        => ___InstanceValue += 9;

    private static void ReplaceInstancePrefix(ref SyntheticFieldProxy __instance)
    {
        Assert.That(__instance.Pointer, Is.EqualTo((IntPtr)0x1234));
        __instance = new SyntheticFieldProxy((IntPtr)0x9876);
    }

    private static void MutateInstancePostfix(ref SyntheticFieldProxy __instance)
        => __instance = new SyntheticFieldProxy((IntPtr)0x9876);

    private static void ReplaceStaticInstancePrefix(ref SyntheticStaticProxy __instance)
        => __instance = new SyntheticStaticProxy((IntPtr)0x9876);

    private static void PostfixOrderCallback()
    {
    }

    private static bool NoBoxedValue(IntPtr boxed, out string? typeName, out long value)
    {
        typeName = null;
        value = 0;
        return false;
    }

    private static PcCompatCompiledRule PrefixRule(string callbackMethod, params string[] parameterTypes)
        => PrefixRuleWithReturn(callbackMethod, "System.Void", parameterTypes);

    private static PcCompatCompiledRule PrefixRuleWithReturn(
        string callbackMethod,
        string targetReturnType,
        params string[] parameterTypes)
        => new()
        {
            Id = $"managed_prefix:1:{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = "SyntheticTarget",
            TargetMethod = "Run",
            TargetIsStatic = false,
            TargetReturnType = targetReturnType,
            TargetParameterTypes = parameterTypes,
            ParamCount = parameterTypes.Length,
            Stage = PcCompatRuleStage.BeforeOriginal,
            Op = PcCompatRuleOp.ManagedSynchronousPrefix,
            RequiredCapabilities = PcCompatCapability.SkipOriginal,
            Source = "managed_prefix:test"
        };

    private static PcCompatCompiledRule PostfixRule(string callbackMethod)
        => new()
        {
            Id = $"managed_event:1:{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = "SyntheticTarget",
            TargetMethod = "Run",
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes = Array.Empty<string>(),
            ParamCount = 0,
            Stage = PcCompatRuleStage.AfterOriginal,
            Op = PcCompatRuleOp.ManagedEventCallback,
            RequiredCapabilities = PcCompatCapability.None,
            Source = "managed_event:test"
        };

    private static PcCompatCompiledRule StateRule(string callbackMethod, string kind, uint patchId)
        => new()
        {
            Id = $"{(kind == "Prefix" ? "managed_prefix" : "managed_event")}:{patchId}:" +
                 $"{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = "SyntheticTarget",
            TargetMethod = "Run",
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes = Array.Empty<string>(),
            ParamCount = 0,
            Stage = kind == "Prefix" ? PcCompatRuleStage.BeforeOriginal : PcCompatRuleStage.AfterOriginal,
            Op = kind == "Prefix"
                ? PcCompatRuleOp.ManagedSynchronousPrefix
                : PcCompatRuleOp.ManagedEventCallback,
            RequiredCapabilities = kind == "Prefix"
                ? PcCompatCapability.SkipOriginal
                : PcCompatCapability.None,
            Source = "managed_state:test"
        };

    private static PcCompatCompiledRule FieldRule(string callbackMethod)
        => new()
        {
            Id = $"managed_prefix:31:{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = typeof(SyntheticFieldProxy).FullName!,
            TargetMethod = nameof(SyntheticFieldProxy.Run),
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes = Array.Empty<string>(),
            ParamCount = 0,
            Stage = PcCompatRuleStage.BeforeOriginal,
            Op = PcCompatRuleOp.ManagedSynchronousPrefix,
            RequiredCapabilities = PcCompatCapability.SkipOriginal,
            Source = "managed_field:test"
        };

    private static PcCompatCompiledRule StaticInstanceRule(string callbackMethod)
        => new()
        {
            Id = $"managed_prefix:60:{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = typeof(SyntheticStaticProxy).FullName!,
            TargetMethod = nameof(SyntheticStaticProxy.Run),
            TargetIsStatic = true,
            TargetReturnType = "System.Void",
            TargetParameterTypes = Array.Empty<string>(),
            ParamCount = 0,
            Stage = PcCompatRuleStage.BeforeOriginal,
            Op = PcCompatRuleOp.ManagedSynchronousPrefix,
            RequiredCapabilities = PcCompatCapability.SkipOriginal,
            Source = "managed_static_instance:test"
        };

    private static PcCompatCompiledRule OriginalMethodRule(string callbackMethod, string kind, uint patchId)
        => new()
        {
            Id = $"{(kind == "Prefix" ? "managed_prefix" : "managed_event")}:{patchId}:" +
                 $"{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = typeof(SyntheticFieldProxy).FullName!,
            TargetMethod = nameof(SyntheticFieldProxy.Run),
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes = Array.Empty<string>(),
            ParamCount = 0,
            Stage = kind == "Prefix" ? PcCompatRuleStage.BeforeOriginal : PcCompatRuleStage.AfterOriginal,
            Op = kind == "Prefix"
                ? PcCompatRuleOp.ManagedSynchronousPrefix
                : PcCompatRuleOp.ManagedEventCallback,
            RequiredCapabilities = kind == "Prefix"
                ? PcCompatCapability.SkipOriginal
                : PcCompatCapability.None,
            Source = "managed_original_method:test"
        };

    private static PcCompatCompiledRule ArgsRule(string callbackMethod)
        => new()
        {
            Id = $"managed_prefix:51:{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = typeof(SyntheticArgsTarget).FullName!,
            TargetMethod = nameof(SyntheticArgsTarget.Run),
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes =
            [
                typeof(int).FullName!,
                typeof(float).FullName!,
                typeof(SyntheticMode).FullName!,
                typeof(SyntheticFieldProxy).FullName!
            ],
            ParamCount = 4,
            Stage = PcCompatRuleStage.BeforeOriginal,
            Op = PcCompatRuleOp.ManagedSynchronousPrefix,
            RequiredCapabilities = PcCompatCapability.SkipOriginal,
            Source = "managed_args:test"
        };

    private static PcCompatCompiledRule PostfixArgsRule(
        string callbackMethod,
        uint patchId,
        string targetType,
        params string[] parameterTypes)
        => new()
        {
            Id = $"managed_event:{patchId}:{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = targetType,
            TargetMethod = nameof(SyntheticArgsTarget.Run),
            TargetIsStatic = false,
            TargetReturnType = "System.Void",
            TargetParameterTypes = parameterTypes,
            ParamCount = parameterTypes.Length,
            Stage = PcCompatRuleStage.AfterOriginal,
            Op = PcCompatRuleOp.ManagedEventCallback,
            RequiredCapabilities = PcCompatCapability.None,
            Source = "managed_postfix_args:test"
        };

    private static PcCompatCompiledRule PostfixResultRule(
        string callbackMethod,
        uint patchId,
        string targetType,
        string targetReturnType)
        => new()
        {
            Id = $"managed_event:{patchId}:{typeof(PcCompatHarmonySynchronousPrefixTests).FullName}:{callbackMethod}",
            FeatureId = "managed_callback",
            TargetType = targetType,
            TargetMethod = "Run",
            TargetIsStatic = false,
            TargetReturnType = targetReturnType,
            TargetParameterTypes = Array.Empty<string>(),
            ParamCount = 0,
            Stage = PcCompatRuleStage.AfterOriginal,
            Op = PcCompatRuleOp.ManagedEventCallback,
            RequiredCapabilities = PcCompatCapability.None,
            Source = "managed_postfix_result:test"
        };

    private static void DispatchPostfixResult(
        object dispatcher,
        uint patchId,
        PcCompatManagedPrefixResultKind resultKind,
        ulong resultValue)
    {
        var record = new byte[PcCompatManagedCallbackDispatcher.EventRecordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record, patchId);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(160), (uint)resultKind);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(164), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(168), resultValue);
        dispatcher.GetType()
            .GetMethod("DispatchRecord", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, new object?[]
            {
                record,
                0,
                new PcCompatManagedBoxedValueHandler(NoBoxedValue),
                null,
                null
            });
    }

    private static void DispatchRecord(
        object dispatcher,
        byte[] record,
        PcCompatManagedBoxedValueHandler boxedValueReader)
        => dispatcher.GetType()
            .GetMethod("DispatchRecord", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dispatcher, new object?[]
            {
                record,
                0,
                boxedValueReader,
                null,
                null
            });

    private static PcCompatManagedEventDispatchStats SnapshotStats(object dispatcher)
        => (PcCompatManagedEventDispatchStats)dispatcher.GetType()
            .GetMethod("SnapshotStats")!
            .Invoke(dispatcher, null)!;

    private static string WriteRecipe(PcModManifest manifest, params PcCompatCompiledRule[] rules)
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ui_recipe.bin");
        PcCompatUiRecipeBinary.Write(path, manifest, new PcCompatRecipeCompileReport
        {
            ModId = manifest.Id,
            RecipeId = "prefix-test",
            Compatibility = "supported",
            Rules = rules,
            RequiredCapabilities = PcCompatCapability.SkipOriginal
        }, 143);
        return path;
    }

    private static PcCompatManagedPrefixInvocationV2 CreateInvocation(
        PcCompatManagedPrefixResultKind resultKind,
        params ulong[] arguments)
    {
        var frame = new PcCompatManagedPrefixInvocationV2
        {
            StructSize = PcCompatManagedPrefixInvocationV2.ExpectedSize,
            AbiVersion = PcCompatManagedPrefixInvocationV2.CurrentAbiVersion,
            ArgumentCount = (uint)arguments.Length,
            ResultKind = resultKind,
            RunOriginal = 1
        };
        for (var index = 0; index < arguments.Length; ++index)
            frame.SetArgument(index, arguments[index]);
        return frame;
    }

    private static object BuildDispatcher(
        string recipePath,
        PcCompatCompiledRule rule,
        string callbackMethod,
        string kind = "Prefix",
        string owner = "",
        int priority = -1,
        long registrationIndex = 0,
        string[]? before = null,
        string[]? after = null)
        => BuildDispatcherMany(
            recipePath,
            new RegistrationSpec(
                rule,
                callbackMethod,
                kind,
                owner,
                priority,
                registrationIndex,
                before,
                after));

    private static object BuildDispatcherMany(
        string recipePath,
        params RegistrationSpec[] specs)
    {
        var assembly = typeof(PcCompatManagedCallbackDispatcher).Assembly;
        var registrationType = assembly.GetType(
            "Xphorror.PcModCompat.PcCompatShimCallbackRegistration",
            throwOnError: true)!;
        var registrations = Array.CreateInstance(registrationType, specs.Length);
        for (var index = 0; index < specs.Length; ++index)
        {
            var spec = specs[index];
            var registration = Activator.CreateInstance(registrationType)!;
            void Set(string name, object? value) => registrationType.GetProperty(name)!.SetValue(registration, value);
            Set("TargetType", spec.Rule.TargetType);
            Set("TargetMethod", spec.Rule.TargetMethod);
            Set("Kind", spec.Kind);
            Set("CallbackType", typeof(PcCompatHarmonySynchronousPrefixTests).FullName!);
            Set("CallbackMethod", spec.CallbackMethod);
            Set("Method", typeof(PcCompatHarmonySynchronousPrefixTests).GetMethod(
                spec.CallbackMethod,
                BindingFlags.Static | BindingFlags.NonPublic)!);
            Set("OriginalMethod", spec.OriginalMethod);
            Set("IsActive", new Func<bool>(() => true));
            Set("Target", null);
            Set("Owner", spec.Owner);
            Set("Priority", spec.Priority);
            Set("RegistrationIndex", spec.RegistrationIndex);
            Set("Before", spec.Before ?? Array.Empty<string>());
            Set("After", spec.After ?? Array.Empty<string>());
            registrations.SetValue(registration, index);
        }
        return typeof(PcCompatManagedCallbackDispatcher)
            .GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[]
            {
                "prefix-test",
                recipePath,
                registrations,
                Array.Empty<PcCompatPatchDescriptor>()
            })!;
    }

    private sealed record RegistrationSpec(
        PcCompatCompiledRule Rule,
        string CallbackMethod,
        string Kind,
        string Owner = "",
        int Priority = -1,
        long RegistrationIndex = 0,
        string[]? Before = null,
        string[]? After = null,
        MethodBase? OriginalMethod = null);

    public sealed class SyntheticFieldProxy
    {
        public SyntheticFieldProxy(IntPtr pointer) => Pointer = pointer;

        public IntPtr Pointer { get; }

        public static int Value { get; set; }

        public int InstanceValue
        {
            get => Value;
            set => Value = value;
        }

        public void Run()
        {
        }
    }

    private sealed class SyntheticStaticProxy
    {
        public SyntheticStaticProxy(IntPtr pointer) => Pointer = pointer;

        public IntPtr Pointer { get; }

        public static void Run()
        {
        }
    }

    private sealed class SyntheticRefArgsTarget
    {
        public void Run(ref int value)
        {
        }
    }

    private readonly struct SyntheticValue
    {
        public SyntheticValue(int value) => Value = value;

        public int Value { get; }
    }

    private sealed class SyntheticStructArgsTarget
    {
        public void Run(SyntheticValue value)
        {
        }
    }

    private sealed class SyntheticBooleanResultTarget
    {
        public bool Run() => true;
    }

    private sealed class SyntheticEnumResultTarget
    {
        public SyntheticMode Run() => SyntheticMode.First;
    }

    private sealed class SyntheticBoxedEnumTarget
    {
        public void Run(Enum value) { }
    }

    private enum SyntheticMode
    {
        First = 1,
        Second = 2
    }

    private sealed class SyntheticArgsTarget
    {
        public void Run(int value, float ratio, SyntheticMode mode, SyntheticFieldProxy proxy)
        {
        }
    }

    private static (PcModManifest Manifest, string SampleDir) ReadSampleManifest()
    {
        var sampleDir = Path.Combine(FindRepoRoot(), "JipperResourcePack_release");
        Assume.That(Directory.Exists(sampleDir), Is.True, $"missing sample mod dir: {sampleDir}");
        Assert.That(PcModManifestReader.TryRead(sampleDir, out var manifest, out var error), Is.True, error);
        return (manifest!, sampleDir);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.WorkDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "StArray.ModManager.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
