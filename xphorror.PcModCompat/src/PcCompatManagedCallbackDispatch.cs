using System.Buffers.Binary;
using System.Buffers;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using StArray.ModManager.Manager;

namespace Xphorror.PcModCompat;

// Native bridge surface for the managed callback dispatcher. Implementations live in
// the Android PcCompatManagedSelfRenderBridge and are registered at install time.
public delegate int PcCompatManagedEventDrainHandler(string modId, byte[] output, out ulong dropped);

public delegate bool PcCompatManagedBoxedValueHandler(IntPtr boxed, out string? typeName, out long value);

public delegate void PcCompatManagedHitMarginSnapshotHandler(bool valid, ReadOnlySpan<int> counts);

public sealed record PcCompatManagedPrefixOrderEntry(
    uint PatchId,
    string TargetType,
    string TargetMethod,
    string Owner,
    int Priority,
    long RegistrationIndex,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> After);

public delegate void PcCompatManagedPrefixOrderPlanHandler(
    string modId,
    IReadOnlyList<PcCompatManagedPrefixOrderEntry> entries);

public sealed record PcCompatManagedPostfixOrderEntry(
    uint PatchId,
    string TargetType,
    string TargetMethod,
    string Owner,
    int Priority,
    long RegistrationIndex,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> After);

public delegate void PcCompatManagedPostfixOrderPlanHandler(
    string modId,
    IReadOnlyList<PcCompatManagedPostfixOrderEntry> entries);

public enum PcCompatManagedPrefixResultKind : uint
{
    Void = 0,
    Boolean = 1,
    Int32 = 2
}

[StructLayout(LayoutKind.Sequential)]
public struct PcCompatManagedPrefixInvocationV2
{
    public const uint CurrentAbiVersion = 2;
    public const uint ExpectedSize = 96;
    public const int MaximumArgumentCount = 6;

    public uint StructSize;
    public uint AbiVersion;
    public uint ArgumentCount;
    public PcCompatManagedPrefixResultKind ResultKind;
    public ulong Instance;
    public ulong InvocationId;
    public uint RunOriginal;
    public uint ResultValid;
    public ulong ResultValue;
    public ulong Argument0;
    public ulong Argument1;
    public ulong Argument2;
    public ulong Argument3;
    public ulong Argument4;
    public ulong Argument5;

    public readonly bool HasValidLayout
        => StructSize == ExpectedSize &&
           AbiVersion == CurrentAbiVersion &&
           ArgumentCount <= MaximumArgumentCount;

    public readonly ulong GetArgument(int index) => index switch
    {
        0 => Argument0,
        1 => Argument1,
        2 => Argument2,
        3 => Argument3,
        4 => Argument4,
        5 => Argument5,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetArgument(int index, ulong value)
    {
        switch (index)
        {
            case 0: Argument0 = value; break;
            case 1: Argument1 = value; break;
            case 2: Argument2 = value; break;
            case 3: Argument3 = value; break;
            case 4: Argument4 = value; break;
            case 5: Argument5 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}

internal readonly struct PcCompatManagedPostfixInvocation
{
    public readonly ulong InvocationId;
    public readonly PcCompatManagedPrefixResultKind ResultKind;
    public readonly uint ResultValid;
    public readonly ulong ResultValue;
    public readonly uint RunOriginal;

    public PcCompatManagedPostfixInvocation(
        ulong invocationId,
        PcCompatManagedPrefixResultKind resultKind,
        uint resultValid,
        ulong resultValue,
        uint runOriginal)
    {
        InvocationId = invocationId;
        ResultKind = resultKind;
        ResultValid = resultValid;
        ResultValue = resultValue;
        RunOriginal = runOriginal;
    }
}

/// <summary>Runtime counters for the managed callback dispatcher (diagnostics export).</summary>
public sealed record PcCompatManagedEventDispatchStats(
    int ParsedRules,
    int BoundCallbacks,
    int DisabledCallbacks,
    long DrainCalls,
    long DrainedEvents,
    ulong NativeDroppedEvents,
    long DrainBudgetExhaustedFrames,
    long HitMarginSnapshots,
    long InvalidHitMarginSnapshots,
    uint LastHitMarginSnapshotGeneration,
    uint LastNonZeroHitMarginSnapshotGeneration,
    string LastNonZeroHitMarginCounts,
    long DispatchedCallbacks,
    long FailedCallbacks,
    string SkipReasons,
    string LastError,
    string CallbackStats);

/// <summary>
/// One managed-event rule recovered from ui_recipe.bin (rule op == ManagedEventCallback).
/// The rule id string carries the patch id and the callback identity; the target
/// record carries the signature used for argument binding.
/// </summary>
public sealed class PcCompatManagedEventRuleInfo
{
    public required uint PatchId { get; init; }
    public required string TargetType { get; init; }
    public required string TargetMethod { get; init; }
    public required bool TargetIsStatic { get; init; }
    public required string TargetReturnType { get; init; }
    public required IReadOnlyList<string> ParameterTypes { get; init; }
    public required string CallbackType { get; init; }
    public required string CallbackMethod { get; init; }
    public PcCompatPatchKind PatchKind { get; init; } = PcCompatPatchKind.Postfix;
}

/// <summary>
/// Live callback registration snapshotted from the shim JAPatcher registry inside the
/// MOD's ALC. Unlike the plain patch descriptors this keeps the actual MethodInfo (and
/// the delegate target for instance callbacks) so the dispatcher can invoke it.
/// </summary>
internal sealed class PcCompatShimCallbackRegistration
{
    public required string TargetType { get; init; }
    public required string TargetMethod { get; init; }
    public required string Kind { get; init; }
    public required string CallbackType { get; init; }
    public required string CallbackMethod { get; init; }
    public required MethodInfo Method { get; init; }
    public MethodBase? OriginalMethod { get; init; }
    public required Func<bool> IsActive { get; init; }
    public object? Target { get; init; }
    public string Owner { get; init; } = string.Empty;
    public long RegistrationIndex { get; init; }
    public int Priority { get; init; } = -1;
    public IReadOnlyList<string> Before { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> After { get; init; } = Array.Empty<string>();
}

public static class PcCompatManagedEventRecipeReader
{
    private const int HeaderSize = 96;
    private const int SectionEntrySize = 24;
    private const int TargetRecordSize = 48;
    private const int RuleRecordSize = 36;
    private const uint ManagedEventOpCode = 21; // PcCompatRuleOp.ManagedEventCallback
    private const uint ManagedPrefixOpCode = 23; // PcCompatRuleOp.ManagedSynchronousPrefix
    private const string RuleIdPrefix = "managed_event:";
    private const string PrefixRuleIdPrefix = "managed_prefix:";
    private static readonly byte[] Magic = "XPHUIRCP"u8.ToArray();

    public static PcCompatManagedEventRuleInfo[] Read(string recipePath)
    {
        var data = File.ReadAllBytes(recipePath);
        if (data.Length < HeaderSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("managed event recipe read failed: bad magic");

        var sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var sectionTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(88));
        if (sectionCount > 64 || sectionTableOffset > data.Length ||
            sectionCount * SectionEntrySize > data.Length - sectionTableOffset)
            throw new InvalidDataException("managed event recipe read failed: bad section table");

        (uint Offset, uint Size) strings = (0, 0);
        (uint Offset, uint Size) parameters = (0, 0);
        (uint Offset, uint Size) targets = (0, 0);
        (uint Offset, uint Size) rules = (0, 0);
        for (uint index = 0; index < sectionCount; ++index)
        {
            var cursor = checked((int)(sectionTableOffset + index * SectionEntrySize));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor));
            var entry = (
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 8)));
            switch (type)
            {
                case 1: strings = entry; break;
                case 2: parameters = entry; break;
                case 3: targets = entry; break;
                case 4: rules = entry; break;
            }
        }

        if (strings.Size == 0 || targets.Size == 0 || rules.Size == 0)
            return Array.Empty<PcCompatManagedEventRuleInfo>();

        string ReadString(uint stringOffset)
        {
            if (stringOffset >= strings.Size)
                return string.Empty;
            var start = checked((int)(strings.Offset + stringOffset));
            var end = checked((int)(strings.Offset + strings.Size));
            var length = data.AsSpan(start, end - start).IndexOf((byte)0);
            return length <= 0 ? string.Empty : Encoding.UTF8.GetString(data, start, length);
        }

        var targetCount = targets.Size / TargetRecordSize;
        var result = new List<PcCompatManagedEventRuleInfo>();
        for (uint targetIndex = 0; targetIndex < targetCount; ++targetIndex)
        {
            var targetCursor = checked((int)(targets.Offset + targetIndex * TargetRecordSize));
            var targetType = ReadString(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(targetCursor + 12)));
            var targetMethod = ReadString(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(targetCursor + 16)));
            var targetReturnType = ReadString(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(targetCursor + 20)));
            var parameterStart = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(targetCursor + 28));
            var parameterCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(targetCursor + 32));
            var targetFlags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(targetCursor + 34));
            var ruleStart = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(targetCursor + 40));
            var ruleCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(targetCursor + 44));
            if (ruleCount == 0)
                continue;

            var parameterTypes = new string[parameterCount];
            for (var parameterIndex = 0; parameterIndex < parameterCount; ++parameterIndex)
            {
                var refCursor = checked((int)(parameters.Offset + (parameterStart + (uint)parameterIndex) * sizeof(uint)));
                parameterTypes[parameterIndex] = ReadString(
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(refCursor)));
            }

            for (uint ruleIndex = 0; ruleIndex < ruleCount; ++ruleIndex)
            {
                var ruleCursor = checked((int)(rules.Offset + (ruleStart + ruleIndex) * RuleRecordSize));
                var opCode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ruleCursor + 16));
                if (opCode is not (ManagedEventOpCode or ManagedPrefixOpCode))
                    continue;

                var ruleId = ReadString(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ruleCursor)));
                var patchKind = opCode == ManagedPrefixOpCode
                    ? PcCompatPatchKind.Prefix
                    : PcCompatPatchKind.Postfix;
                if (!TryParseRuleId(
                        ruleId,
                        patchKind == PcCompatPatchKind.Prefix ? PrefixRuleIdPrefix : RuleIdPrefix,
                        out var patchId,
                        out var callbackType,
                        out var callbackMethod))
                    continue;

                result.Add(new PcCompatManagedEventRuleInfo
                {
                    PatchId = patchId,
                    TargetType = targetType,
                    TargetMethod = targetMethod,
                    TargetIsStatic = (targetFlags & 1) != 0,
                    TargetReturnType = targetReturnType,
                    ParameterTypes = parameterTypes,
                    CallbackType = callbackType,
                    CallbackMethod = callbackMethod,
                    PatchKind = patchKind
                });
            }
        }

        return result.ToArray();
    }

    private static bool TryParseRuleId(
        string ruleId,
        string prefix,
        out uint patchId,
        out string callbackType,
        out string callbackMethod)
    {
        patchId = 0;
        callbackType = string.Empty;
        callbackMethod = string.Empty;
        if (!ruleId.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var parts = ruleId.Split(':');
        // managed_event:<patchId>:<callbackType>:<callbackMethod>
        if (parts.Length != 4 ||
            !uint.TryParse(parts[1], out patchId) ||
            patchId == 0 ||
            parts[2].Length == 0 ||
            parts[3].Length == 0)
            return false;

        callbackType = parts[2];
        callbackMethod = parts[3];
        return true;
    }
}

/// <summary>
/// Per-session managed callback dispatcher. Drains the native per-MOD event ring at
/// frame start and invokes the MOD's own postfix callbacks on UnityMain. Binding plans
/// are derived from the callback MethodInfo parameter shapes: no-arg direct call,
/// int/enum/float raw slot conversion, __instance proxy wrapping, ___field proxy member
/// reads, and boxed enum recovery through the native boxed-value bridge.
/// </summary>
public sealed class PcCompatManagedCallbackDispatcher
{
    public const int EventRecordSize = 184;
    public const int BufferEventCapacity = 128;
    public const int BufferSize = EventRecordSize * BufferEventCapacity;
    internal const int DispatchSequenceOffset = 144;
    private const int InvocationIdOffset = 152;
    private const int ResultKindOffset = 160;
    private const int ResultValidOffset = 164;
    private const int ResultValueOffset = 168;
    private const int RunOriginalOffset = 176;

    private const int MaxDrainBatchesPerFrame = 8;
    private const int HitSnapshotGenerationOffset = 64;
    private const int HitSnapshotValidOffset = 68;
    private const int HitSnapshotLengthOffset = 72;
    private const int HitSnapshotAttachedOffset = 76;
    private const int HitSnapshotCountsOffset = 80;
    private const int HitSnapshotMaxCounts = 16;
    private const int MaxCallbackFailures = 8;
    private static readonly long CallbackRetryDelayTicks = Stopwatch.Frequency;
    private const string LogTag = "PcModCompat";

    private readonly string _modId;
    private readonly Dictionary<uint, CallbackBinding> _bindings;
    private readonly Dictionary<uint, CallbackBinding> _prefixBindings;
    private readonly PcCompatManagedStateStore _stateStore;
    private readonly PcCompatManagedPrefixOrderEntry[] _prefixOrderPlan;
    private readonly PcCompatManagedPostfixOrderEntry[] _postfixOrderPlan;
    private readonly List<string> _skipReasons = new();
    private int _drainFailureCount;
    private int _parsedRuleCount;
    private long _drainCalls;
    private long _drainedEvents;
    private long _nativeDroppedEvents;
    private long _drainBudgetExhaustedFrames;
    private long _hitMarginSnapshots;
    private long _invalidHitMarginSnapshots;
    private int _lastHitMarginSnapshotGeneration;
    private int _lastPublishedHitMarginSnapshotGeneration = -1;
    private int _lastNonZeroHitMarginSnapshotGeneration;
    private readonly int[] _lastNonZeroHitMarginCounts = new int[HitSnapshotMaxCounts];
    private long _dispatchedCallbacks;
    private long _failedCallbacks;
    private string? _lastError;

    private PcCompatManagedCallbackDispatcher(
        string modId,
        Dictionary<uint, CallbackBinding> bindings,
        Dictionary<uint, CallbackBinding> prefixBindings,
        PcCompatManagedPrefixOrderEntry[] prefixOrderPlan,
        PcCompatManagedPostfixOrderEntry[] postfixOrderPlan)
    {
        _modId = modId;
        _bindings = bindings;
        _prefixBindings = prefixBindings;
        _stateStore = new PcCompatManagedStateStore(
            bindings.Values.SelectMany(binding => binding.StateKeys));
        _prefixOrderPlan = prefixOrderPlan;
        _postfixOrderPlan = postfixOrderPlan;
    }

    public int RuleCount => _bindings.Count + _prefixBindings.Count;

    public int PrefixRuleCount => _prefixBindings.Count;

    public IReadOnlyList<PcCompatManagedPrefixOrderEntry> PrefixOrderPlan => _prefixOrderPlan;

    public IReadOnlyList<PcCompatManagedPostfixOrderEntry> PostfixOrderPlan => _postfixOrderPlan;

    public PcCompatManagedEventDispatchStats SnapshotStats()
        => new(
            Volatile.Read(ref _parsedRuleCount),
            RuleCount,
            _bindings.Values.Concat(_prefixBindings.Values).Count(binding => binding.Disabled),
            Interlocked.Read(ref _drainCalls),
            Interlocked.Read(ref _drainedEvents),
            unchecked((ulong)Interlocked.Read(ref _nativeDroppedEvents)),
            Interlocked.Read(ref _drainBudgetExhaustedFrames),
            Interlocked.Read(ref _hitMarginSnapshots),
            Interlocked.Read(ref _invalidHitMarginSnapshots),
            unchecked((uint)Volatile.Read(ref _lastHitMarginSnapshotGeneration)),
            unchecked((uint)Volatile.Read(ref _lastNonZeroHitMarginSnapshotGeneration)),
            string.Join(',', _lastNonZeroHitMarginCounts),
            Interlocked.Read(ref _dispatchedCallbacks),
            Interlocked.Read(ref _failedCallbacks),
            string.Join(" | ", _skipReasons),
            Volatile.Read(ref _lastError) ?? "none",
            string.Join(
                " | ",
                _bindings.Values.Concat(_prefixBindings.Values)
                    .OrderBy(binding => binding.Identity, StringComparer.Ordinal)
                    .Select(binding => binding.SnapshotStats())));

    internal static PcCompatManagedCallbackDispatcher Build(
        string modId,
        string recipePath,
        IReadOnlyList<PcCompatShimCallbackRegistration> registrations,
        IReadOnlyList<PcCompatPatchDescriptor> descriptors)
    {
        var rules = PcCompatManagedEventRecipeReader.Read(recipePath);
        var bindings = new Dictionary<uint, CallbackBinding>();
        var prefixBindings = new Dictionary<uint, CallbackBinding>();
        var prefixOrderPlan = new List<PcCompatManagedPrefixOrderEntry>();
        var postfixOrderPlan = new List<PcCompatManagedPostfixOrderEntry>();
        var skipReasons = new List<string>();
        foreach (var rule in rules)
        {
            var destination = rule.PatchKind == PcCompatPatchKind.Prefix
                ? prefixBindings
                : bindings;
            if (destination.ContainsKey(rule.PatchId))
                continue;

            var registration = registrations
                .Where(candidate =>
                    string.Equals(candidate.TargetType, rule.TargetType, StringComparison.Ordinal) &&
                    string.Equals(candidate.TargetMethod, rule.TargetMethod, StringComparison.Ordinal) &&
                    string.Equals(candidate.CallbackType, rule.CallbackType, StringComparison.Ordinal) &&
                    string.Equals(candidate.CallbackMethod, rule.CallbackMethod, StringComparison.Ordinal) &&
                    string.Equals(candidate.Kind, rule.PatchKind.ToString(), StringComparison.Ordinal) &&
                    candidate.IsActive())
                .OrderByDescending(candidate => candidate.RegistrationIndex)
                .FirstOrDefault();
            if (registration == null)
            {
                skipReasons.Add($"patch={rule.PatchId} {rule.CallbackType}.{rule.CallbackMethod}: no shim registration");
                Logger.Warn(
                    LogTag,
                    $"mod={modId} managed_event skip patch={rule.PatchId} " +
                    $"callback={rule.CallbackType}.{rule.CallbackMethod} reason=no shim registration");
                continue;
            }

            if (TryBuildBinding(
                    modId,
                    rule,
                    registration,
                    registrations,
                    out var binding,
                    out var invalidReason))
            {
                destination.Add(rule.PatchId, binding);
                if (rule.PatchKind == PcCompatPatchKind.Prefix)
                {
                    prefixOrderPlan.Add(new PcCompatManagedPrefixOrderEntry(
                        rule.PatchId,
                        rule.TargetType,
                        rule.TargetMethod,
                        registration.Owner,
                        registration.Priority == -1 ? 400 : registration.Priority,
                        registration.RegistrationIndex,
                        registration.Before.ToArray(),
                        registration.After.ToArray()));
                }
                else if (rule.PatchKind == PcCompatPatchKind.Postfix)
                {
                    postfixOrderPlan.Add(new PcCompatManagedPostfixOrderEntry(
                        rule.PatchId,
                        rule.TargetType,
                        rule.TargetMethod,
                        registration.Owner,
                        registration.Priority == -1 ? 400 : registration.Priority,
                        registration.RegistrationIndex,
                        registration.Before.ToArray(),
                        registration.After.ToArray()));
                }
                var descriptor = descriptors.FirstOrDefault(candidate =>
                    string.Equals(candidate.CallbackType, rule.CallbackType, StringComparison.Ordinal) &&
                    string.Equals(candidate.CallbackMethod, rule.CallbackMethod, StringComparison.Ordinal) &&
                    string.Equals(candidate.TargetType, rule.TargetType, StringComparison.Ordinal) &&
                    string.Equals(candidate.TargetMethod, rule.TargetMethod, StringComparison.Ordinal));
                if (descriptor != null)
                {
                    descriptor.Status = PcCompatPatchStatus.Supported;
                    descriptor.Reason = $"managed event dispatch active (patchId={rule.PatchId})";
                }
            }
            else
            {
                skipReasons.Add(
                    $"patch={rule.PatchId} {rule.CallbackType}.{rule.CallbackMethod}: {invalidReason}");
                Logger.Warn(
                    LogTag,
                    $"mod={modId} managed_event skip patch={rule.PatchId} " +
                    $"callback={rule.CallbackType}.{rule.CallbackMethod} reason={invalidReason}");
            }
        }

        var dispatcher = new PcCompatManagedCallbackDispatcher(
            modId,
            bindings,
            prefixBindings,
            prefixOrderPlan.ToArray(),
            postfixOrderPlan.ToArray())
        {
            _parsedRuleCount = rules.Length
        };
        dispatcher._skipReasons.AddRange(skipReasons.Take(8));
        return dispatcher;
    }

    internal bool TryDispatchSynchronousPrefix(
        uint patchId,
        ref PcCompatManagedPrefixInvocationV2 invocation,
        PcCompatManagedBoxedValueHandler boxedValueReader,
        out bool runOriginal)
    {
        runOriginal = invocation.RunOriginal != 0;
        if (!invocation.HasValidLayout)
            return false;
        if (!_prefixBindings.TryGetValue(patchId, out var binding) || binding.Disabled)
            return false;

        var before = invocation;
        try
        {
            binding.InvokePrefix(ref invocation, boxedValueReader, _stateStore);
            runOriginal = invocation.RunOriginal != 0;
            Interlocked.Increment(ref _dispatchedCallbacks);
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _failedCallbacks);
            var cause = exception is TargetInvocationException { InnerException: not null } targetInvocation
                ? targetInvocation.InnerException!
                : exception;
            Volatile.Write(ref _lastError, $"{binding.Identity} -> {cause.GetType().Name}: {cause.Message}");
            binding.NoteFailure(_modId, exception);
            invocation = before;
            runOriginal = before.RunOriginal != 0;
            return false;
        }
    }

    internal bool DrainAndDispatch(
        PcCompatManagedEventDrainHandler drain,
        PcCompatManagedBoxedValueHandler boxedValueReader,
        byte[] buffer,
        PcCompatManagedHitMarginSnapshotHandler? publishHitMarginsSnapshot = null,
        Action? refreshHitMarginsFallback = null,
        PcCompatManagedEventDispatchCollector? collector = null,
        PcCompatManagedModSession? session = null)
    {
        if (_bindings.Count == 0)
            return true;

        var budgetExhausted = false;
        for (var batch = 0; batch < MaxDrainBatchesPerFrame; ++batch)
        {
            Interlocked.Increment(ref _drainCalls);
            int count;
            ulong nativeDropped;
            try
            {
                count = drain(_modId, buffer, out nativeDropped);
            }
            catch (Exception exception)
            {
                if (++_drainFailureCount <= 3)
                    Logger.Error(LogTag, $"mod={_modId} managed_event drain failed: {exception.Message}");
                return false;
            }
            RecordNativeDropped(nativeDropped);

            if (count <= 0)
                break;

            Interlocked.Add(ref _drainedEvents, count);
            var recordCount = Math.Min(count, buffer.Length / EventRecordSize);
            for (var index = 0; index < recordCount; ++index)
            {
                var cursor = index * EventRecordSize;
                if (collector != null)
                {
                    if (session == null)
                        throw new InvalidOperationException("Managed event collection requires an owning session.");
                    collector.Enqueue(session, this, buffer, cursor);
                }
                else
                {
                    DispatchRecord(
                        buffer,
                        cursor,
                        boxedValueReader,
                        publishHitMarginsSnapshot,
                        refreshHitMarginsFallback);
                }
            }

            if (count < buffer.Length / EventRecordSize)
                break;
            budgetExhausted = batch + 1 == MaxDrainBatchesPerFrame;
        }

        if (budgetExhausted)
            Interlocked.Increment(ref _drainBudgetExhaustedFrames);
        return true;
    }

    internal void DispatchRecord(
        byte[] buffer,
        int cursor,
        PcCompatManagedBoxedValueHandler boxedValueReader,
        PcCompatManagedHitMarginSnapshotHandler? publishHitMarginsSnapshot = null,
        Action? refreshHitMarginsFallback = null)
    {
        var patchId = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(cursor));
        var argCount = Math.Min(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(cursor + 4)),
            6u);
        var instance = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(cursor + 8));
        var postfixInvocation = new PcCompatManagedPostfixInvocation(
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(cursor + InvocationIdOffset)),
            (PcCompatManagedPrefixResultKind)BinaryPrimitives.ReadUInt32LittleEndian(
                buffer.AsSpan(cursor + ResultKindOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(cursor + ResultValidOffset)),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(cursor + ResultValueOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(cursor + RunOriginalOffset)));
        if (!_bindings.TryGetValue(patchId, out var binding) || binding.Disabled)
            return;

        PublishEventHitMarginSnapshot(
            buffer,
            cursor,
            publishHitMarginsSnapshot,
            refreshHitMarginsFallback);

        try
        {
            binding.Invoke(
                instance,
                buffer,
                cursor + 16,
                argCount,
                boxedValueReader,
                postfixInvocation,
                _stateStore);
            Interlocked.Increment(ref _dispatchedCallbacks);
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _failedCallbacks);
            var cause = exception is TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException!
                : exception;
            var summary = $"{binding.Identity} -> {cause.GetType().Name}: {cause.Message}";
            Volatile.Write(
                ref _lastError,
                summary.Length <= 600 ? summary : summary[..600]);
            binding.NoteFailure(_modId, exception);
        }
    }

    private void RecordNativeDropped(ulong nativeDropped)
    {
        var current = unchecked((ulong)Interlocked.Read(ref _nativeDroppedEvents));
        while (nativeDropped > current)
        {
            var observed = unchecked((ulong)Interlocked.CompareExchange(
                ref _nativeDroppedEvents,
                unchecked((long)nativeDropped),
                unchecked((long)current)));
            if (observed == current)
            {
                if (nativeDropped != 0)
                    Volatile.Write(ref _lastError, $"native managed-event ring dropped={nativeDropped}");
                return;
            }
            current = observed;
        }
    }

    private void PublishEventHitMarginSnapshot(
        byte[] buffer,
        int cursor,
        PcCompatManagedHitMarginSnapshotHandler? publish,
        Action? fallback)
    {
        var attached = BinaryPrimitives.ReadUInt32LittleEndian(
            buffer.AsSpan(cursor + HitSnapshotAttachedOffset)) != 0;
        if (!attached)
            return;

        var generation = BinaryPrimitives.ReadUInt32LittleEndian(
            buffer.AsSpan(cursor + HitSnapshotGenerationOffset));
        Volatile.Write(ref _lastHitMarginSnapshotGeneration, unchecked((int)generation));
        if (unchecked((uint)Volatile.Read(ref _lastPublishedHitMarginSnapshotGeneration)) == generation)
            return;
        var valid = BinaryPrimitives.ReadUInt32LittleEndian(
            buffer.AsSpan(cursor + HitSnapshotValidOffset)) != 0;
        var length = Math.Min(
            BinaryPrimitives.ReadUInt32LittleEndian(
                buffer.AsSpan(cursor + HitSnapshotLengthOffset)),
            (uint)HitSnapshotMaxCounts);
        if (!valid)
        {
            publish?.Invoke(false, ReadOnlySpan<int>.Empty);
            Volatile.Write(ref _lastPublishedHitMarginSnapshotGeneration, unchecked((int)generation));
            Interlocked.Increment(ref _hitMarginSnapshots);
            return;
        }
        if (length == 0 || publish == null)
        {
            Interlocked.Increment(ref _invalidHitMarginSnapshots);
            fallback?.Invoke();
            return;
        }

        var bytes = buffer.AsSpan(
            cursor + HitSnapshotCountsOffset,
            checked((int)length * sizeof(int)));
        var counts = MemoryMarshal.Cast<byte, int>(bytes);
        var total = 0;
        for (var index = 0; index < counts.Length; ++index)
            total += counts[index];
        if (total != 0)
        {
            for (var index = 0; index < _lastNonZeroHitMarginCounts.Length; ++index)
            {
                Volatile.Write(
                    ref _lastNonZeroHitMarginCounts[index],
                    index < counts.Length ? counts[index] : 0);
            }
            Volatile.Write(
                ref _lastNonZeroHitMarginSnapshotGeneration,
                unchecked((int)generation));
        }
        publish(true, counts);
        Volatile.Write(ref _lastPublishedHitMarginSnapshotGeneration, unchecked((int)generation));
        Interlocked.Increment(ref _hitMarginSnapshots);
    }

    private static bool TryBuildBinding(
        string modId,
        PcCompatManagedEventRuleInfo rule,
        PcCompatShimCallbackRegistration registration,
        IReadOnlyList<PcCompatShimCallbackRegistration> registrations,
        out CallbackBinding binding,
        out string invalidReason)
    {
        binding = null!;
        invalidReason = string.Empty;

        var method = registration.Method;
        var parameters = method.GetParameters();
        var synchronousPrefix = rule.PatchKind == PcCompatPatchKind.Prefix;
        if (synchronousPrefix && method.ReturnType is not null &&
            method.ReturnType != typeof(void) && method.ReturnType != typeof(bool))
        {
            invalidReason = $"Prefix return type {method.ReturnType.FullName} is not void or bool";
            return false;
        }
        if (parameters.Length == 0)
        {
            binding = new CallbackBinding(
                modId,
                rule,
                method,
                registration.Target,
                registration.IsActive,
                Array.Empty<ParamBinding>(),
                synchronousPrefix);
            return true;
        }

        var targetProxyType = ResolveProxyType(rule.TargetType);

        // The dependency-closed proxy surface trims methods the rewritten MOD never
        // calls — which is exactly the set of patch targets (they are patched, not
        // invoked). A missing proxy method therefore cannot fail the binding: argument
        // names fall back to positional mapping in declaration order, which is how
        // JALib/Harmony callbacks declare a prefix of the original parameters.
        var targetProxyMethod = targetProxyType == null ? null : ResolveProxyMethod(targetProxyType, rule);
        var targetParameters = targetProxyMethod?.GetParameters();

        var bindings = new List<ParamBinding>(parameters.Length);
        var positionalIndex = 0;
        foreach (var parameter in parameters)
        {
            var name = parameter.Name ?? string.Empty;
            if (name == "__originalMethod")
            {
                if (parameter.ParameterType.IsByRef || parameter.IsOut)
                {
                    invalidReason = "__originalMethod cannot be ref/out";
                    return false;
                }
                var originalMethod = registration.OriginalMethod ?? targetProxyMethod;
                if (originalMethod == null)
                {
                    invalidReason = "__originalMethod has no registered or uniquely resolved target MethodBase";
                    return false;
                }
                if (!parameter.ParameterType.IsInstanceOfType(originalMethod))
                {
                    invalidReason = $"__originalMethod type {parameter.ParameterType.FullName} " +
                                    $"cannot accept {originalMethod.GetType().FullName}";
                    return false;
                }
                bindings.Add(new OriginalMethodParamBinding(originalMethod));
                continue;
            }
            if (name == "__args")
            {
                if (!synchronousPrefix)
                {
                    invalidReason = "Postfix __args cannot write back after the native invocation returned";
                    return false;
                }
                if (parameter.ParameterType != typeof(object[]) || parameter.IsOut)
                {
                    invalidReason = "__args must be object[]";
                    return false;
                }
                if (!TryCreateArgsBinding(rule, targetParameters, out var argsBinding, out invalidReason))
                    return false;
                bindings.Add(argsBinding);
                continue;
            }
            if (name == "__state")
            {
                var stateType = parameter.ParameterType.IsByRef
                    ? parameter.ParameterType.GetElementType()!
                    : parameter.ParameterType;
                var declaringType = method.DeclaringType;
                if (declaringType == null || stateType == typeof(void))
                {
                    invalidReason = "__state callback has no declaring type or valid state type";
                    return false;
                }
                if (!synchronousPrefix && TryFindConflictingPrefixStateType(
                        rule,
                        declaringType,
                        stateType,
                        registrations,
                        out var prefixStateType))
                {
                    invalidReason = $"__state type {stateType.FullName} conflicts with Prefix state type " +
                                    prefixStateType.FullName;
                    return false;
                }
                bindings.Add(new StateParamBinding(
                    stateType,
                    declaringType,
                    parameter.ParameterType.IsByRef || parameter.IsOut));
                continue;
            }
            if (name == "__result")
            {
                if (!synchronousPrefix)
                {
                    invalidReason = "Postfix __result requires the synchronous result event bridge";
                    return false;
                }
                if (!TryCreateResultBinding(parameter, rule, out var resultBinding, out invalidReason))
                    return false;
                bindings.Add(resultBinding);
                continue;
            }
            if (name == "__runOriginal")
            {
                if (!synchronousPrefix)
                {
                    invalidReason = "__runOriginal is only available to a synchronous Prefix";
                    return false;
                }
                var runOriginalType = parameter.ParameterType.IsByRef
                    ? parameter.ParameterType.GetElementType()
                    : parameter.ParameterType;
                if (runOriginalType != typeof(bool))
                {
                    invalidReason = "__runOriginal must be bool or ref bool";
                    return false;
                }
                bindings.Add(new RunOriginalParamBinding(parameter.ParameterType.IsByRef));
                continue;
            }
            if (name == "__exception")
            {
                invalidReason = "__exception requires the Finalizer exception bridge";
                return false;
            }
            if (name == "__instance")
            {
                if (parameter.ParameterType.IsByRef || parameter.IsOut)
                {
                    invalidReason = "ref/out __instance is not supported by the current invocation ABI";
                    return false;
                }
                if (targetProxyType == null)
                {
                    invalidReason = "target proxy type not found: " + rule.TargetType;
                    return false;
                }
                var wrapType = parameter.ParameterType == typeof(object)
                    ? targetProxyType
                    : parameter.ParameterType;
                var ctor = FindPointerConstructor(wrapType);
                if (ctor == null)
                {
                    invalidReason = $"__instance wrap type {wrapType.FullName} has no (IntPtr) constructor";
                    return false;
                }
                bindings.Add(new InstanceParamBinding(ctor));
                continue;
            }

            if (name.StartsWith("___", StringComparison.Ordinal) && name.Length > 3)
            {
                if (targetProxyType == null)
                {
                    invalidReason = "target proxy type not found: " + rule.TargetType;
                    return false;
                }
                var fieldName = name[3..];
                var member = FindProxyMember(targetProxyType, fieldName);
                var instanceCtor = FindPointerConstructor(targetProxyType);
                if (member == null || instanceCtor == null)
                {
                    invalidReason = $"field {fieldName} not found on proxy {targetProxyType.FullName}";
                    return false;
                }
                var memberType = GetMemberType(member);
                if (memberType == null || !CanReadMember(member))
                {
                    invalidReason = $"field {fieldName} has no readable proxy member";
                    return false;
                }
                var fieldParameterType = parameter.ParameterType.IsByRef
                    ? parameter.ParameterType.GetElementType()!
                    : parameter.ParameterType;
                if (NormalizeProxyTypeName(fieldParameterType) != NormalizeProxyTypeName(memberType))
                {
                    invalidReason = $"field {fieldName} type {memberType.FullName} does not match callback type {fieldParameterType.FullName}";
                    return false;
                }
                var fieldWriteBack = parameter.ParameterType.IsByRef || parameter.IsOut;
                if (fieldWriteBack && (!synchronousPrefix || !CanWriteMember(member)))
                {
                    invalidReason = $"field {fieldName} ref/out requires a writable synchronous proxy member";
                    return false;
                }
                bindings.Add(new FieldParamBinding(instanceCtor, member, fieldWriteBack, parameter.IsOut));
                continue;
            }

            int slotIndex;
            if (name.Length > 2 && name[0] == '_' && name[1] == '_' &&
                int.TryParse(name[2..], out var explicitIndex))
            {
                slotIndex = explicitIndex;
            }
            else
            {
                slotIndex = targetParameters != null
                    ? FindTargetParameterIndex(targetParameters, name)
                    : -1;
                if (slotIndex < 0)
                {
                    // Harmony allows declaring only a prefix of the original parameters;
                    // fall back to positional matching when names are unavailable.
                    slotIndex = positionalIndex < rule.ParameterTypes.Count ? positionalIndex : -1;
                }
            }

            ++positionalIndex;
            if (slotIndex < 0 || slotIndex >= rule.ParameterTypes.Count)
            {
                invalidReason = $"parameter {name} does not map to a target argument slot";
                return false;
            }

            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            var writeBack = parameter.ParameterType.IsByRef || parameter.IsOut;
            if (writeBack && !synchronousPrefix)
            {
                invalidReason = $"parameter {name}: Postfix ref/out requires the synchronous result event bridge";
                return false;
            }
            if (!TryCreateSlotConversion(parameterType, slotIndex, out var conversion, out var conversionError))
            {
                invalidReason = $"parameter {name}: {conversionError}";
                return false;
            }
            if (writeBack && !conversion.CanWriteBack)
            {
                invalidReason = $"parameter {name}: ref/out {parameterType.FullName} is not a primitive or enum slot";
                return false;
            }
            bindings.Add(new ArgParamBinding(slotIndex, conversion, writeBack, parameter.IsOut));
        }

        binding = new CallbackBinding(
            modId,
            rule,
            method,
            registration.Target,
            registration.IsActive,
            bindings.ToArray(),
            synchronousPrefix);
        return true;
    }

    private static bool TryCreateArgsBinding(
        PcCompatManagedEventRuleInfo rule,
        ParameterInfo[]? targetParameters,
        out ParamBinding binding,
        out string error)
    {
        binding = null!;
        error = string.Empty;
        if (rule.ParameterTypes.Count > PcCompatManagedPrefixInvocationV2.MaximumArgumentCount)
        {
            error = $"__args target arity {rule.ParameterTypes.Count} exceeds invocation capacity " +
                    PcCompatManagedPrefixInvocationV2.MaximumArgumentCount;
            return false;
        }

        var conversions = new SlotConversion[rule.ParameterTypes.Count];
        for (var index = 0; index < conversions.Length; ++index)
        {
            var parameterType = targetParameters != null
                ? UnwrapByRef(targetParameters[index].ParameterType)
                : ResolveSlotType(rule.ParameterTypes[index]);
            if (parameterType == null)
            {
                error = $"__args slot {index} type {rule.ParameterTypes[index]} has no managed proxy";
                return false;
            }
            if (!TryCreateSlotConversion(parameterType, index, out var conversion, out var conversionError))
            {
                error = $"__args slot {index}: {conversionError}";
                return false;
            }
            if (!conversion.CanWriteBackFromArgs)
            {
                error = $"__args slot {index} type {parameterType.FullName} has no safe native write-back";
                return false;
            }
            conversions[index] = conversion;
        }

        binding = new ArgsParamBinding(conversions);
        return true;
    }

    private static Type UnwrapByRef(Type type)
        => type.IsByRef ? type.GetElementType()! : type;

    private static Type? ResolveSlotType(string typeName)
        => Type.GetType(typeName, throwOnError: false) ?? ResolveProxyType(typeName);

    private static bool TryFindConflictingPrefixStateType(
        PcCompatManagedEventRuleInfo rule,
        Type declaringType,
        Type postfixStateType,
        IReadOnlyList<PcCompatShimCallbackRegistration> registrations,
        out Type prefixStateType)
    {
        prefixStateType = null!;
        var candidates = registrations
            .Where(candidate =>
                string.Equals(candidate.Kind, nameof(PcCompatPatchKind.Prefix), StringComparison.Ordinal) &&
                string.Equals(candidate.TargetType, rule.TargetType, StringComparison.Ordinal) &&
                string.Equals(candidate.TargetMethod, rule.TargetMethod, StringComparison.Ordinal) &&
                candidate.Method.DeclaringType == declaringType &&
                candidate.IsActive())
            .SelectMany(candidate => candidate.Method.GetParameters())
            .Where(parameter => string.Equals(parameter.Name, "__state", StringComparison.Ordinal))
            .Select(parameter => parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType)
            .Distinct()
            .ToArray();
        if (candidates.Length == 0 || candidates.Contains(postfixStateType))
            return false;
        prefixStateType = candidates[0];
        return true;
    }

    private static bool TryCreateResultBinding(
        ParameterInfo parameter,
        PcCompatManagedEventRuleInfo rule,
        out ParamBinding binding,
        out string error)
    {
        binding = null!;
        error = string.Empty;
        if (string.Equals(rule.TargetReturnType, "System.Void", StringComparison.Ordinal))
        {
            error = "__result cannot bind to a void target";
            return false;
        }

        var byRef = parameter.ParameterType.IsByRef;
        var resultType = byRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
        if (NormalizeProxyTypeName(resultType) != rule.TargetReturnType)
        {
            error = $"__result type {resultType.FullName} does not match target return {rule.TargetReturnType}";
            return false;
        }
        if (!TryCreateSlotConversion(resultType, -1, out var conversion, out var conversionError) ||
            !conversion.CanWriteBack)
        {
            error = $"__result: {conversionError}";
            return false;
        }

        var resultKind = resultType == typeof(bool)
            ? PcCompatManagedPrefixResultKind.Boolean
            : PcCompatManagedPrefixResultKind.Int32;
        if (resultType != typeof(bool) &&
            resultType != typeof(byte) && resultType != typeof(sbyte) &&
            resultType != typeof(short) && resultType != typeof(ushort) &&
            resultType != typeof(int) && resultType != typeof(uint) &&
            resultType != typeof(char) && !resultType.IsEnum)
        {
            error = $"__result type {resultType.FullName} has no installed native return dispatcher";
            return false;
        }

        binding = new ResultParamBinding(conversion, resultKind, byRef || parameter.IsOut);
        return true;
    }

    private static int FindTargetParameterIndex(ParameterInfo[] targetParameters, string name)
    {
        if (string.IsNullOrEmpty(name))
            return -1;
        for (var index = 0; index < targetParameters.Length; ++index)
        {
            if (string.Equals(targetParameters[index].Name, name, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static bool TryCreateSlotConversion(
        Type parameterType,
        int slotIndex,
        out SlotConversion conversion,
        out string error)
    {
        conversion = null!;
        error = string.Empty;

        if (parameterType == typeof(Enum))
        {
            // The IL2CPP side passes a boxed game enum; the concrete enum type is only
            // known from the boxed object's class at dispatch time.
            conversion = new BoxedEnumSlotConversion(slotIndex);
            return true;
        }

        if (parameterType.IsEnum)
        {
            conversion = new EnumSlotConversion(parameterType, slotIndex);
            return true;
        }

        if (IsIntegerLike(parameterType) ||
            parameterType == typeof(float) ||
            parameterType == typeof(double))
        {
            conversion = new PrimitiveSlotConversion(parameterType, slotIndex);
            return true;
        }

        var ctor = FindPointerConstructor(parameterType);
        if (ctor != null)
        {
            conversion = new ProxyWrapSlotConversion(ctor, slotIndex);
            return true;
        }

        error = $"unsupported callback parameter type {parameterType.FullName}";
        return false;
    }

    private static bool IsIntegerLike(Type type)
        => type == typeof(bool) ||
           type == typeof(byte) || type == typeof(sbyte) ||
           type == typeof(short) || type == typeof(ushort) ||
           type == typeof(int) || type == typeof(uint) ||
           type == typeof(long) || type == typeof(ulong) ||
           type == typeof(char);

    private static ConstructorInfo? FindPointerConstructor(Type type)
        => type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, binder: null, new[] { typeof(IntPtr) }, modifiers: null);

    private static MemberInfo? FindProxyMember(Type proxyType, string fieldName)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var property = proxyType.GetProperty(fieldName, flags);
        if (property != null && property.CanRead)
            return property;
        return proxyType.GetField(fieldName, flags);
    }

    private static Type? GetMemberType(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => null
        };

    private static bool CanReadMember(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.GetMethod != null,
            FieldInfo => true,
            _ => false
        };

    private static bool CanWriteMember(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.SetMethod != null,
            FieldInfo field => !field.IsInitOnly,
            _ => false
        };

    private static readonly Dictionary<string, Type?> ProxyTypeCache = new(StringComparer.Ordinal);

    private static Type? ResolveProxyType(string fullName)
    {
        lock (ProxyTypeCache)
        {
            if (ProxyTypeCache.TryGetValue(fullName, out var cached))
                return cached;
        }

        Type? fallback = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;
            Type? type;
            try
            {
                type = assembly.GetType(fullName, throwOnError: false);
            }
            catch
            {
                continue;
            }
            if (type == null)
                continue;
            // The shared proxy assemblies live in the Default ALC; prefer them over any
            // same-named type that a MOD ALC might carry.
            if (AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default)
            {
                lock (ProxyTypeCache)
                    ProxyTypeCache[fullName] = type;
                return type;
            }
            fallback ??= type;
        }

        lock (ProxyTypeCache)
            ProxyTypeCache[fullName] = fallback;
        return fallback;
    }

    private static MethodInfo? ResolveProxyMethod(Type proxyType, PcCompatManagedEventRuleInfo rule)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var candidates = proxyType
            .GetMethods(flags)
            .Where(method =>
                method.Name == rule.TargetMethod &&
                method.GetParameters().Length == rule.ParameterTypes.Count &&
                method.IsStatic == rule.TargetIsStatic)
            .ToArray();
        if (candidates.Length == 1)
            return candidates[0];
        if (candidates.Length == 0)
            return null;

        foreach (var candidate in candidates)
        {
            var parameters = candidate.GetParameters();
            var matches = true;
            for (var index = 0; index < parameters.Length; ++index)
            {
                if (NormalizeProxyTypeName(parameters[index].ParameterType) != rule.ParameterTypes[index])
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
                return candidate;
        }

        return null;
    }

    private static string NormalizeProxyTypeName(Type type)
    {
        var name = type.FullName ?? type.Name;
        // Il2CppInterop prefixes BCL namespaces with "Il2Cpp" (Il2CppSystem.Action),
        // while game/Unity types keep their original names.
        return name.StartsWith("Il2Cpp", StringComparison.Ordinal) ? name["Il2Cpp".Length..] : name;
    }

    private sealed class PcCompatManagedStateStore
    {
        private const int MaximumRetainedStates = 16384;

        private sealed class Entry
        {
            public object? Value;
            public int Remaining;
        }

        private readonly Dictionary<(ulong InvocationId, string Key), Entry> _entries = new();
        private readonly Dictionary<string, int> _expectedPostfixCount;

        public PcCompatManagedStateStore(IEnumerable<string> postfixStateKeys)
        {
            _expectedPostfixCount = postfixStateKeys
                .GroupBy(key => key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        }

        public object? Get(
            ulong invocationId,
            string key,
            Type type,
            bool initialize)
        {
            var entryKey = (invocationId, key);
            if (!_entries.TryGetValue(entryKey, out var entry))
            {
                if (!initialize)
                    return type.IsValueType ? Activator.CreateInstance(type) : null;
                if (!_expectedPostfixCount.TryGetValue(key, out var expected) || expected <= 0)
                    return type.IsValueType ? Activator.CreateInstance(type) : null;
                entry = new Entry
                {
                    Value = type.IsValueType ? Activator.CreateInstance(type) : null,
                    Remaining = expected
                };
                _entries[entryKey] = entry;
            }
            return entry.Value;
        }

        public void Set(ulong invocationId, string key, object? value)
        {
            if (!_expectedPostfixCount.TryGetValue(key, out var expected) || expected <= 0)
                return;
            var entryKey = (invocationId, key);
            if (!_entries.TryGetValue(entryKey, out var entry))
            {
                if (_entries.Count >= MaximumRetainedStates)
                {
                    var oldestInvocation = _entries.Keys.Min(candidate => candidate.InvocationId);
                    foreach (var stale in _entries.Keys
                                 .Where(candidate => candidate.InvocationId == oldestInvocation)
                                 .ToArray())
                    {
                        _entries.Remove(stale);
                    }
                }
                entry = new Entry
                {
                    Remaining = expected
                };
                _entries[entryKey] = entry;
            }
            entry.Value = value;
        }

        public void Release(ulong invocationId, IEnumerable<string> keys)
        {
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                var entryKey = (invocationId, key);
                if (!_entries.TryGetValue(entryKey, out var entry))
                    continue;
                if (--entry.Remaining <= 0)
                    _entries.Remove(entryKey);
            }
        }
    }

    private abstract class ParamBinding
    {
        public virtual bool AffectsOriginal => false;

        public abstract object? Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader);

        public abstract object? ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore);

        public virtual void WriteBackPrefix(
            object? value,
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedStateStore stateStore)
        {
        }

        public virtual object? ResolvePostfix(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            in PcCompatManagedPostfixInvocation invocation,
            PcCompatManagedStateStore stateStore)
            => Resolve(instance, buffer, argsOffset, argCount, boxedValueReader);
    }

    private sealed class InstanceParamBinding : ParamBinding
    {
        private readonly Func<IntPtr, object> _construct;

        public InstanceParamBinding(ConstructorInfo ctor) => _construct = CompilePointerConstructor(ctor);

        public override object? Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader)
        {
            if (instance == 0)
                return null;
            return _construct((IntPtr)instance);
        }

        public override object? ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
            => invocation.Instance == 0
                ? null
                : _construct((IntPtr)invocation.Instance);
    }

    private sealed class FieldParamBinding : ParamBinding
    {
        private readonly Func<IntPtr, object> _construct;
        private readonly Func<object, object?> _readMember;
        private readonly Action<object, object?>? _writeMember;
        private readonly Type _memberType;
        private readonly bool _writeBack;
        private readonly bool _isOut;

        public FieldParamBinding(
            ConstructorInfo instanceCtor,
            MemberInfo member,
            bool writeBack,
            bool isOut)
        {
            _construct = CompilePointerConstructor(instanceCtor);
            _readMember = CompileMemberReader(member);
            _writeMember = writeBack ? CompileMemberWriter(member) : null;
            _memberType = GetMemberType(member)
                          ?? throw new InvalidOperationException("Proxy member has no value type.");
            _writeBack = writeBack;
            _isOut = isOut;
        }

        public override bool AffectsOriginal
            => _writeBack || !_memberType.IsValueType;

        public override object? Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader)
        {
            if (instance == 0)
                return null;
            return _readMember(_construct((IntPtr)instance));
        }

        public override object? ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
            => invocation.Instance == 0
                ? null
                : _isOut
                    ? null
                    : _readMember(_construct((IntPtr)invocation.Instance));

        public override void WriteBackPrefix(
            object? value,
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedStateStore stateStore)
        {
            if (!_writeBack || _writeMember == null || invocation.Instance == 0)
                return;
            _writeMember(_construct((IntPtr)invocation.Instance), value);
        }

    }

    private sealed class ArgParamBinding : ParamBinding
    {
        private readonly int _slot;
        private readonly SlotConversion _conversion;
        private readonly bool _writeBack;
        private readonly bool _isOut;

        public ArgParamBinding(int slot, SlotConversion conversion, bool writeBack = false, bool isOut = false)
        {
            _slot = slot;
            _conversion = conversion;
            _writeBack = writeBack;
            _isOut = isOut;
        }

        public override bool AffectsOriginal => _writeBack || _conversion.AffectsOriginal;

        public override object? Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader)
        {
            if ((uint)_slot >= argCount)
                return _conversion.FallbackValue;
            var raw = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(argsOffset + _slot * sizeof(ulong)));
            return _conversion.Convert(raw, boxedValueReader);
        }

        public override object? ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
        {
            if ((uint)_slot >= invocation.ArgumentCount)
                return _conversion.FallbackValue;
            if (_isOut)
                return _conversion.FallbackValue;
            return _conversion.Convert(invocation.GetArgument(_slot), boxedValueReader);
        }

        public override void WriteBackPrefix(
            object? value,
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedStateStore stateStore)
        {
            if (!_writeBack || (uint)_slot >= invocation.ArgumentCount)
                return;
            invocation.SetArgument(_slot, _conversion.ConvertBack(value));
        }
    }

    private sealed class ResultParamBinding : ParamBinding
    {
        private readonly SlotConversion _conversion;
        private readonly PcCompatManagedPrefixResultKind _resultKind;
        private readonly bool _writeBack;

        public ResultParamBinding(
            SlotConversion conversion,
            PcCompatManagedPrefixResultKind resultKind,
            bool writeBack)
        {
            _conversion = conversion;
            _resultKind = resultKind;
            _writeBack = writeBack;
        }

        public override bool AffectsOriginal => _writeBack;

        public override object? Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader)
            => throw new InvalidOperationException("__result requires a synchronous Prefix invocation.");

        public override object? ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
        {
            ValidateKind(invocation.ResultKind);
            return _conversion.Convert(invocation.ResultValue, boxedValueReader);
        }

        public override void WriteBackPrefix(
            object? value,
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedStateStore stateStore)
        {
            if (!_writeBack)
                return;
            ValidateKind(invocation.ResultKind);
            invocation.ResultValue = _conversion.ConvertBack(value);
            invocation.ResultValid = 1;
        }

        private void ValidateKind(PcCompatManagedPrefixResultKind actual)
        {
            if (actual != _resultKind)
                throw new InvalidOperationException(
                    $"managed Prefix result kind mismatch expected={_resultKind} actual={actual}");
        }
    }

    private sealed class RunOriginalParamBinding : ParamBinding
    {
        private readonly bool _writeBack;

        public RunOriginalParamBinding(bool writeBack) => _writeBack = writeBack;

        public override bool AffectsOriginal => _writeBack;

        public override object? Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader)
            => throw new InvalidOperationException("__runOriginal requires a synchronous Prefix invocation.");

        public override object ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
            => invocation.RunOriginal != 0;

        public override void WriteBackPrefix(
            object? value,
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedStateStore stateStore)
        {
            if (_writeBack)
                invocation.RunOriginal = value is true ? 1u : 0u;
        }
    }

    private sealed class OriginalMethodParamBinding : ParamBinding
    {
        private readonly MethodBase _method;

        public OriginalMethodParamBinding(MethodBase method) => _method = method;

        public override object Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader)
            => _method;

        public override object ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
            => _method;
    }

    private sealed class ArgsParamBinding : ParamBinding
    {
        private readonly SlotConversion[] _conversions;

        public ArgsParamBinding(SlotConversion[] conversions) => _conversions = conversions;

        public override bool AffectsOriginal => true;

        public override object? Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader)
            => throw new InvalidOperationException("__args requires a synchronous Prefix invocation.");

        public override object ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
        {
            if (invocation.ArgumentCount != _conversions.Length)
                throw new InvalidOperationException("__args invocation arity does not match the bound target.");
            var args = new object?[_conversions.Length];
            for (var index = 0; index < args.Length; ++index)
                args[index] = _conversions[index].Convert(invocation.GetArgument(index), boxedValueReader);
            return args;
        }

        public override void WriteBackPrefix(
            object? value,
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedStateStore stateStore)
        {
            if (value is not object?[] args || args.Length != _conversions.Length)
                throw new InvalidOperationException("__args callback returned an invalid argument array.");
            for (var index = 0; index < args.Length; ++index)
                invocation.SetArgument(index, _conversions[index].ConvertBackFromArgs(args[index]));
        }
    }

    private sealed class StateParamBinding : ParamBinding
    {
        private readonly Type _stateType;
        private readonly string _stateKey;
        private readonly bool _writeBack;

        public StateParamBinding(
            Type stateType,
            Type declaringType,
            bool writeBack)
        {
            _stateType = stateType;
            _stateKey = (declaringType.AssemblyQualifiedName ?? declaringType.FullName ?? declaringType.Name) +
                        "|" + (stateType.AssemblyQualifiedName ?? stateType.FullName ?? stateType.Name);
            _writeBack = writeBack;
        }

        public string StateKey => _stateKey;

        public override object? Resolve(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader)
            => throw new InvalidOperationException("__state requires a managed invocation context.");

        public override object? ResolvePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
            => stateStore.Get(invocation.InvocationId, _stateKey, _stateType, initialize: true);

        public override object? ResolvePostfix(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            in PcCompatManagedPostfixInvocation invocation,
            PcCompatManagedStateStore stateStore)
            => stateStore.Get(invocation.InvocationId, _stateKey, _stateType, initialize: false);

        public override void WriteBackPrefix(
            object? value,
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedStateStore stateStore)
        {
            if (_writeBack)
                stateStore.Set(invocation.InvocationId, _stateKey, value);
        }
    }

    private abstract class SlotConversion
    {
        public virtual object? FallbackValue => null;

        public virtual bool CanWriteBack => false;

        public virtual bool CanWriteBackFromArgs => CanWriteBack;

        public virtual bool AffectsOriginal => false;

        public abstract object? Convert(ulong raw, PcCompatManagedBoxedValueHandler boxedValueReader);

        public virtual ulong ConvertBack(object? value)
            => throw new InvalidOperationException("slot conversion does not support write-back");

        public virtual ulong ConvertBackFromArgs(object? value) => ConvertBack(value);
    }

    private sealed class PrimitiveSlotConversion : SlotConversion
    {
        private readonly Type _type;

        public PrimitiveSlotConversion(Type type, int slot) => _type = type;

        public override object? FallbackValue => _type.IsValueType ? Activator.CreateInstance(_type) : null;

        public override bool CanWriteBack => true;

        public override object Convert(ulong raw, PcCompatManagedBoxedValueHandler boxedValueReader)
        {
            if (_type == typeof(bool)) return raw != 0;
            if (_type == typeof(byte)) return (byte)raw;
            if (_type == typeof(sbyte)) return unchecked((sbyte)raw);
            if (_type == typeof(short)) return (short)raw;
            if (_type == typeof(ushort)) return (ushort)raw;
            if (_type == typeof(char)) return (char)raw;
            if (_type == typeof(int)) return (int)raw;
            if (_type == typeof(uint)) return (uint)raw;
            if (_type == typeof(long)) return (long)raw;
            if (_type == typeof(ulong)) return raw;
            if (_type == typeof(float)) return BitConverter.Int32BitsToSingle((int)raw);
            if (_type == typeof(double)) return BitConverter.Int64BitsToDouble((long)raw);
            throw new InvalidOperationException("unsupported primitive conversion: " + _type.FullName);
        }

        public override ulong ConvertBack(object? value)
        {
            if (value == null)
                return 0;
            if (_type == typeof(bool)) return (bool)value ? 1u : 0u;
            if (_type == typeof(byte)) return (byte)value;
            if (_type == typeof(sbyte)) return unchecked((ulong)(sbyte)value);
            if (_type == typeof(short)) return unchecked((ulong)(short)value);
            if (_type == typeof(ushort)) return (ushort)value;
            if (_type == typeof(char)) return (char)value;
            if (_type == typeof(int)) return unchecked((ulong)(int)value);
            if (_type == typeof(uint)) return (uint)value;
            if (_type == typeof(long)) return unchecked((ulong)(long)value);
            if (_type == typeof(ulong)) return (ulong)value;
            if (_type == typeof(float)) return unchecked((uint)BitConverter.SingleToInt32Bits((float)value));
            if (_type == typeof(double)) return unchecked((ulong)BitConverter.DoubleToInt64Bits((double)value));
            throw new InvalidOperationException("unsupported primitive write-back: " + _type.FullName);
        }
    }

    private sealed class EnumSlotConversion : SlotConversion
    {
        private readonly Type _enumType;

        public EnumSlotConversion(Type enumType, int slot) => _enumType = enumType;

        public override object? FallbackValue => Activator.CreateInstance(_enumType);

        public override bool CanWriteBack => true;

        public override object Convert(ulong raw, PcCompatManagedBoxedValueHandler boxedValueReader)
        {
            var underlying = Enum.GetUnderlyingType(_enumType);
            var value = underlying == typeof(long) || underlying == typeof(ulong)
                ? System.Convert.ChangeType(raw, underlying)
                : System.Convert.ChangeType(unchecked((int)raw), underlying);
            return Enum.ToObject(_enumType, value);
        }

        public override ulong ConvertBack(object? value)
        {
            if (value == null)
                return 0;
            var underlying = Enum.GetUnderlyingType(_enumType);
            var converted = System.Convert.ChangeType(value, underlying);
            if (underlying == typeof(byte)) return (byte)converted;
            if (underlying == typeof(sbyte)) return unchecked((ulong)(sbyte)converted);
            if (underlying == typeof(short)) return unchecked((ulong)(short)converted);
            if (underlying == typeof(ushort)) return (ushort)converted;
            if (underlying == typeof(int)) return unchecked((ulong)(int)converted);
            if (underlying == typeof(uint)) return (uint)converted;
            if (underlying == typeof(long)) return unchecked((ulong)(long)converted);
            if (underlying == typeof(ulong)) return (ulong)converted;
            throw new InvalidOperationException("unsupported enum write-back: " + _enumType.FullName);
        }
    }

    private sealed class ProxyWrapSlotConversion : SlotConversion
    {
        private readonly Func<IntPtr, object> _construct;
        private readonly Func<object, IntPtr>? _readPointer;

        public ProxyWrapSlotConversion(ConstructorInfo ctor, int slot)
        {
            _construct = CompilePointerConstructor(ctor);
            var pointer = ctor.DeclaringType?.GetProperty(
                "Pointer",
                BindingFlags.Public | BindingFlags.Instance);
            _readPointer = pointer?.PropertyType == typeof(IntPtr)
                ? CompilePointerReader(pointer)
                : null;
        }

        public override bool AffectsOriginal => true;

        public override bool CanWriteBackFromArgs => _readPointer != null;

        public override object? Convert(ulong raw, PcCompatManagedBoxedValueHandler boxedValueReader)
        {
            if (raw == 0)
                return null;
            return _construct((IntPtr)raw);
        }

        public override ulong ConvertBackFromArgs(object? value)
            => value == null
                ? 0
                : unchecked((ulong)(_readPointer
                    ?? throw new InvalidOperationException("proxy has no readable IntPtr Pointer property"))(value).ToInt64());
    }

    private static Func<IntPtr, object> CompilePointerConstructor(ConstructorInfo constructor)
    {
        var pointer = Expression.Parameter(typeof(IntPtr), "pointer");
        return Expression.Lambda<Func<IntPtr, object>>(
            Expression.Convert(Expression.New(constructor, pointer), typeof(object)),
            pointer).Compile();
    }

    private static Func<object, IntPtr> CompilePointerReader(PropertyInfo property)
    {
        var source = Expression.Parameter(typeof(object), "source");
        var declaringType = property.DeclaringType
                            ?? throw new InvalidOperationException("Pointer property has no declaring type.");
        return Expression.Lambda<Func<object, IntPtr>>(
            Expression.Property(Expression.Convert(source, declaringType), property),
            source).Compile();
    }

    private static Func<object, object?> CompileMemberReader(MemberInfo member)
    {
        var source = Expression.Parameter(typeof(object), "source");
        var declaringType = member.DeclaringType
                            ?? throw new InvalidOperationException("Proxy member has no declaring type.");
        Expression access = member switch
        {
            PropertyInfo property => Expression.Property(
                Expression.Convert(source, declaringType),
                property),
            FieldInfo field => Expression.Field(
                Expression.Convert(source, declaringType),
                field),
            _ => throw new NotSupportedException("Unsupported proxy member: " + member.MemberType)
        };
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(access, typeof(object)),
            source).Compile();
    }

    private static Action<object, object?> CompileMemberWriter(MemberInfo member)
    {
        var source = Expression.Parameter(typeof(object), "source");
        var value = Expression.Parameter(typeof(object), "value");
        var declaringType = member.DeclaringType
                            ?? throw new InvalidOperationException("Proxy member has no declaring type.");
        var memberType = GetMemberType(member)
                         ?? throw new InvalidOperationException("Proxy member has no value type.");
        Expression access = member switch
        {
            PropertyInfo property => Expression.Property(
                Expression.Convert(source, declaringType),
                property),
            FieldInfo field => Expression.Field(
                Expression.Convert(source, declaringType),
                field),
            _ => throw new NotSupportedException("Unsupported proxy member: " + member.MemberType)
        };
        var assignment = Expression.Assign(access, Expression.Convert(value, memberType));
        return Expression.Lambda<Action<object, object?>>(assignment, source, value).Compile();
    }

    private sealed class BoxedEnumSlotConversion : SlotConversion
    {
        public BoxedEnumSlotConversion(int slot) { }

        public override bool AffectsOriginal => true;

        public override object? Convert(ulong raw, PcCompatManagedBoxedValueHandler boxedValueReader)
        {
            if (raw == 0)
                return null;
            if (!boxedValueReader((IntPtr)raw, out var typeName, out var value) || string.IsNullOrEmpty(typeName))
                throw new InvalidOperationException("boxed enum read failed");
            var enumType = ResolveProxyType(typeName!);
            if (enumType == null || !enumType.IsEnum)
                throw new InvalidOperationException("boxed enum proxy type not found: " + typeName);
            return Enum.ToObject(enumType, value);
        }
    }

    private sealed class CallbackBinding
    {
        private const int MaximumExactArraysPerArity = 8;

        [ThreadStatic]
        private static Dictionary<int, Stack<object?[]>>? t_exactInvokeArrays;

        private readonly MethodInfo _method;
        private readonly object? _target;
        private readonly Func<bool> _isActive;
        private readonly ParamBinding[] _parameters;
        private readonly string _identity;
        private readonly Action? _directAction;
        private readonly Func<bool>? _directBool;
        private readonly Action<object?[]>? _compiledInvoker;
        private readonly Func<object?[], object?>? _compiledResultInvoker;
        private readonly object?[]? _invokeArgs;
        private readonly bool _synchronousPrefix;
        private readonly bool _affectsOriginal;
        private readonly bool _requiresExactReflectionArray;
        private readonly string[] _stateKeys;
        private int _failureCount;
        private long _successCount;
        private long _totalFailureCount;
        private long _retryAfterTimestamp;
        private long _totalInvokeTicks;
        private long _maximumInvokeTicks;
        private long _overTwoMillisecondCount;

        public CallbackBinding(
            string modId,
            PcCompatManagedEventRuleInfo rule,
            MethodInfo method,
            object? target,
            Func<bool> isActive,
            ParamBinding[] parameters,
            bool synchronousPrefix)
        {
            _method = method;
            _target = target;
            _isActive = isActive;
            _parameters = parameters;
            _synchronousPrefix = synchronousPrefix;
            _requiresExactReflectionArray = method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef);
            _stateKeys = parameters
                .OfType<StateParamBinding>()
                .Select(parameter => parameter.StateKey)
                .ToArray();
            _affectsOriginal = method.ReturnType == typeof(bool) ||
                               parameters.Any(parameter => parameter.AffectsOriginal);
            TryPrepareMethod(method);
            _invokeArgs = parameters.Length == 0 || synchronousPrefix
                ? null
                : new object?[parameters.Length];
            if (parameters.Length == 0 && method.ReturnType == typeof(void))
            {
                try
                {
                    _directAction = (Action)(method.IsStatic
                        ? method.CreateDelegate(typeof(Action))
                        : method.CreateDelegate(typeof(Action), target));
                    RuntimeHelpers.PrepareDelegate(_directAction);
                }
                catch
                {
                    // Reflection remains the compatibility fallback for unusual
                    // callback ownership or visibility shapes.
                }
            }
            else if (parameters.Length == 0 && synchronousPrefix && method.ReturnType == typeof(bool))
            {
                try
                {
                    _directBool = (Func<bool>)(method.IsStatic
                        ? method.CreateDelegate(typeof(Func<bool>))
                        : method.CreateDelegate(typeof(Func<bool>), target));
                    RuntimeHelpers.PrepareDelegate(_directBool);
                }
                catch
                {
                    // Reflection remains the compatibility fallback.
                }
            }
            else if (parameters.Length != 0 && synchronousPrefix)
            {
                try
                {
                    if (!parameters.Any(parameter => parameter.GetType() == typeof(ResultParamBinding)) &&
                        !method.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
                    {
                        _compiledResultInvoker = CompileCallbackResultInvoker(method, target);
                        RuntimeHelpers.PrepareDelegate(_compiledResultInvoker);
                    }
                }
                catch
                {
                    // Reflection remains the compatibility fallback.
                }
            }
            else if (parameters.Length != 0)
            {
                try
                {
                    _compiledInvoker = CompileCallbackInvoker(method, target);
                    RuntimeHelpers.PrepareDelegate(_compiledInvoker);
                }
                catch
                {
                    // Reflection remains the fallback for runtime/private method shapes
                    // that the expression compiler cannot access.
                }
            }
            _identity = $"kind={rule.PatchKind} patch={rule.PatchId} callback={rule.CallbackType}.{rule.CallbackMethod} " +
                        $"target={rule.TargetType}.{rule.TargetMethod}";
        }

        private static void TryPrepareMethod(MethodInfo method)
        {
            if (method.ContainsGenericParameters)
                return;
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
            catch
            {
                // Lazy JIT remains available for unsupported runtime method shapes.
            }
        }

        public bool Disabled
        {
            get
            {
                try
                {
                    if (!_isActive())
                        return true;
                }
                catch
                {
                    return true;
                }
                return Volatile.Read(ref _failureCount) >= MaxCallbackFailures &&
                       Stopwatch.GetTimestamp() < Volatile.Read(ref _retryAfterTimestamp);
            }
        }

        public string Identity => _identity;

        public IReadOnlyList<string> StateKeys => _stateKeys;

        public void Invoke(
            ulong instance,
            byte[] buffer,
            int argsOffset,
            uint argCount,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            in PcCompatManagedPostfixInvocation invocation,
            PcCompatManagedStateStore stateStore)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                if (_directAction != null)
                {
                    _directAction();
                }
                else if (_invokeArgs == null)
                {
                    _method.Invoke(_target, null);
                }
                else
                {
                    for (var index = 0; index < _parameters.Length; ++index)
                        _invokeArgs[index] = _parameters[index].ResolvePostfix(
                            instance,
                            buffer,
                            argsOffset,
                            argCount,
                            boxedValueReader,
                            invocation,
                            stateStore);

                    try
                    {
                        if (_compiledInvoker != null)
                            _compiledInvoker(_invokeArgs);
                        else
                            _method.Invoke(_target, _invokeArgs);
                    }
                    finally
                    {
                        Array.Clear(_invokeArgs);
                    }
                }
                Interlocked.Increment(ref _successCount);
                Volatile.Write(ref _failureCount, 0);
                Volatile.Write(ref _retryAfterTimestamp, 0);
            }
            finally
            {
                stateStore.Release(invocation.InvocationId, _stateKeys);
                RecordInvokeDuration(Stopwatch.GetTimestamp() - started);
            }
        }

        public void InvokePrefix(
            ref PcCompatManagedPrefixInvocationV2 invocation,
            PcCompatManagedBoxedValueHandler boxedValueReader,
            PcCompatManagedStateStore stateStore)
        {
            if (!_synchronousPrefix)
                throw new InvalidOperationException("Callback binding is not a synchronous Prefix.");
            if (invocation.RunOriginal == 0 && _affectsOriginal)
                return;

            var started = Stopwatch.GetTimestamp();
            object? result = null;
            object?[]? invokeArgs = null;
            try
            {
                if (_directAction != null)
                {
                    _directAction();
                }
                else if (_directBool != null)
                {
                    result = _directBool();
                }
                else if (_parameters.Length == 0)
                {
                    result = _method.Invoke(_target, null);
                }
                else
                {
                    var pooled = !_requiresExactReflectionArray;
                    invokeArgs = pooled
                        ? ArrayPool<object?>.Shared.Rent(_parameters.Length)
                        : RentExactInvokeArray(_parameters.Length);
                    for (var index = 0; index < _parameters.Length; ++index)
                    {
                        invokeArgs[index] = _parameters[index].ResolvePrefix(
                            ref invocation,
                            boxedValueReader,
                            stateStore);
                    }

                    result = _compiledResultInvoker != null
                        ? _compiledResultInvoker(invokeArgs)
                        : _method.Invoke(_target, invokeArgs);

                    for (var index = 0; index < _parameters.Length; ++index)
                        _parameters[index].WriteBackPrefix(invokeArgs[index], ref invocation, stateStore);
                }

                if (_method.ReturnType == typeof(bool))
                    invocation.RunOriginal = result is true ? 1u : 0u;

                Interlocked.Increment(ref _successCount);
                Volatile.Write(ref _failureCount, 0);
                Volatile.Write(ref _retryAfterTimestamp, 0);
            }
            finally
            {
                if (invokeArgs != null)
                {
                    Array.Clear(invokeArgs, 0, _parameters.Length);
                    if (_requiresExactReflectionArray)
                        ReturnExactInvokeArray(invokeArgs);
                    else
                        ArrayPool<object?>.Shared.Return(invokeArgs);
                }
                RecordInvokeDuration(Stopwatch.GetTimestamp() - started);
            }
        }

        public void NoteFailure(string modId, Exception exception)
        {
            var failures = Interlocked.Increment(ref _failureCount);
            Interlocked.Increment(ref _totalFailureCount);
            if (failures >= MaxCallbackFailures)
            {
                Volatile.Write(
                    ref _retryAfterTimestamp,
                    Stopwatch.GetTimestamp() + CallbackRetryDelayTicks);
            }
            var cause = exception is TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException!
                : exception;
            if (failures <= 3 || failures == MaxCallbackFailures)
                Logger.Error(
                    LogTag,
                    $"mod={modId} managed_event dispatch failed {failures}/{MaxCallbackFailures} " +
                    $"{_identity}: {cause.GetType().Name}: {cause.Message}");
        }

        public string SnapshotStats()
        {
            var retryAfter = Volatile.Read(ref _retryAfterTimestamp);
            var retryMilliseconds = retryAfter == 0
                ? 0
                : Math.Max(
                    0,
                    (long)((retryAfter - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency));
            return $"{_identity} ok={Interlocked.Read(ref _successCount)} " +
                   $"failed={Interlocked.Read(ref _totalFailureCount)} " +
                   $"streak={Volatile.Read(ref _failureCount)} " +
                   $"backoff={(Disabled ? 1 : 0)} retryMs={retryMilliseconds} " +
                   $"avgUs={AverageInvokeMicroseconds()} " +
                   $"maxUs={TicksToMicroseconds(Interlocked.Read(ref _maximumInvokeTicks))} " +
                   $"over2ms={Interlocked.Read(ref _overTwoMillisecondCount)}";
        }

        private static Action<object?[]> CompileCallbackInvoker(MethodInfo method, object? target)
        {
            var values = Expression.Parameter(typeof(object[]), "values");
            var parameters = method.GetParameters();
            var arguments = new Expression[parameters.Length];
            for (var index = 0; index < parameters.Length; ++index)
            {
                arguments[index] = Expression.Convert(
                    Expression.ArrayIndex(values, Expression.Constant(index)),
                    parameters[index].ParameterType);
            }

            Expression? instance = null;
            if (!method.IsStatic)
            {
                instance = Expression.Constant(
                    target ?? throw new InvalidOperationException("Instance callback target is null."),
                    method.DeclaringType!);
            }
            var call = Expression.Call(instance, method, arguments);
            Expression body = method.ReturnType == typeof(void)
                ? call
                : Expression.Block(call, Expression.Empty());
            return Expression.Lambda<Action<object?[]>>(body, values).Compile();
        }

        private static object?[] RentExactInvokeArray(int length)
        {
            var pools = t_exactInvokeArrays ??= new Dictionary<int, Stack<object?[]>>();
            if (pools.TryGetValue(length, out var pool) && pool.Count != 0)
                return pool.Pop();
            return new object?[length];
        }

        private static void ReturnExactInvokeArray(object?[] values)
        {
            var pools = t_exactInvokeArrays ??= new Dictionary<int, Stack<object?[]>>();
            if (!pools.TryGetValue(values.Length, out var pool))
            {
                pool = new Stack<object?[]>();
                pools.Add(values.Length, pool);
            }
            if (pool.Count < MaximumExactArraysPerArity)
                pool.Push(values);
        }

        private static Func<object?[], object?> CompileCallbackResultInvoker(MethodInfo method, object? target)
        {
            var values = Expression.Parameter(typeof(object[]), "values");
            var parameters = method.GetParameters();
            var arguments = new Expression[parameters.Length];
            for (var index = 0; index < parameters.Length; ++index)
            {
                arguments[index] = Expression.Convert(
                    Expression.ArrayIndex(values, Expression.Constant(index)),
                    parameters[index].ParameterType);
            }

            Expression? instance = null;
            if (!method.IsStatic)
            {
                instance = Expression.Constant(
                    target ?? throw new InvalidOperationException("Instance callback target is null."),
                    method.DeclaringType!);
            }
            var call = Expression.Call(instance, method, arguments);
            Expression body = method.ReturnType == typeof(void)
                ? Expression.Block(call, Expression.Constant(null, typeof(object)))
                : Expression.Convert(call, typeof(object));
            return Expression.Lambda<Func<object?[], object?>>(body, values).Compile();
        }

        private void RecordInvokeDuration(long elapsedTicks)
        {
            Interlocked.Add(ref _totalInvokeTicks, elapsedTicks);
            if (elapsedTicks * 500L >= Stopwatch.Frequency)
                Interlocked.Increment(ref _overTwoMillisecondCount);

            var maximum = Interlocked.Read(ref _maximumInvokeTicks);
            while (elapsedTicks > maximum)
            {
                var previous = Interlocked.CompareExchange(
                    ref _maximumInvokeTicks,
                    elapsedTicks,
                    maximum);
                if (previous == maximum)
                    break;
                maximum = previous;
            }
        }

        private long AverageInvokeMicroseconds()
        {
            var calls = Interlocked.Read(ref _successCount) +
                        Interlocked.Read(ref _totalFailureCount);
            return calls == 0
                ? 0
                : TicksToMicroseconds(Interlocked.Read(ref _totalInvokeTicks) / calls);
        }

        private static long TicksToMicroseconds(long ticks)
            => ticks <= 0 ? 0 : ticks * 1_000_000L / Stopwatch.Frequency;
    }
}

internal sealed class PcCompatManagedEventDispatchCollector
{
    private const int InitialCapacity = 256;
    private static readonly EntryComparer Comparer = new();
    private byte[] _records = new byte[InitialCapacity * PcCompatManagedCallbackDispatcher.EventRecordSize];
    private Entry[] _entries = new Entry[InitialCapacity];
    private int _count;
    private long _ordinal;

    public void Reset()
    {
        if (_count != 0)
            Array.Clear(_entries, 0, _count);
        _count = 0;
        _ordinal = 0;
    }

    public void Enqueue(
        PcCompatManagedModSession session,
        PcCompatManagedCallbackDispatcher dispatcher,
        byte[] source,
        int sourceOffset)
    {
        EnsureCapacity(_count + 1);
        var destinationOffset = _count * PcCompatManagedCallbackDispatcher.EventRecordSize;
        Buffer.BlockCopy(
            source,
            sourceOffset,
            _records,
            destinationOffset,
            PcCompatManagedCallbackDispatcher.EventRecordSize);
        _entries[_count++] = new Entry(
            session,
            dispatcher,
            destinationOffset,
            BinaryPrimitives.ReadUInt64LittleEndian(
                source.AsSpan(sourceOffset + PcCompatManagedCallbackDispatcher.DispatchSequenceOffset)),
            _ordinal++);
    }

    public bool DispatchAll(PcCompatManagedBoxedValueHandler boxedValueReader)
    {
        if (_count == 0)
            return true;

        Array.Sort(_entries, 0, _count, Comparer);
        var success = true;
        for (var index = 0; index < _count; ++index)
        {
            ref readonly var entry = ref _entries[index];
            success &= entry.Session.DispatchCollectedManagedCallback(
                entry.Dispatcher,
                _records,
                entry.RecordOffset,
                boxedValueReader);
        }
        return success;
    }

    private void EnsureCapacity(int capacity)
    {
        if (capacity <= _entries.Length)
            return;
        var next = Math.Max(capacity, checked(_entries.Length * 2));
        Array.Resize(ref _entries, next);
        Array.Resize(
            ref _records,
            checked(next * PcCompatManagedCallbackDispatcher.EventRecordSize));
    }

    private readonly record struct Entry(
        PcCompatManagedModSession Session,
        PcCompatManagedCallbackDispatcher Dispatcher,
        int RecordOffset,
        ulong Sequence,
        long Ordinal);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public int Compare(Entry left, Entry right)
        {
            var sequence = left.Sequence.CompareTo(right.Sequence);
            return sequence != 0 ? sequence : left.Ordinal.CompareTo(right.Ordinal);
        }
    }
}
