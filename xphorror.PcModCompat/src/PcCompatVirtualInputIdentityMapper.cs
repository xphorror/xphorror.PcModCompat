using StArray.ModManager.Interop;

namespace Xphorror.PcModCompat;

/// <summary>
/// Maps the stable VirtualInput V2 key names to the same canonical Unity/Win32 domains used by
/// rewritten PC MOD input calls. The V2 path never enters the Android raw input journal.
/// </summary>
internal static class PcCompatVirtualInputIdentityMapper
{
    internal static IReadOnlyList<PcCompatCanonicalInputIdentity> Map(string? canonicalKey)
    {
        if (TryMapToAndroidKeyCode(canonicalKey, out var keyCode))
        {
            var raw = new PcCompatKeyViewerRawEvent(
                0,
                0,
                0,
                0,
                0,
                PcCompatKeyViewerInputOrigin.ReplayVirtual,
                PcCompatKeyViewerRawSource.Keyboard,
                PcCompatKeyViewerRawPhase.Down,
                keyCode,
                -1,
                0,
                0,
                0,
                -1,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);
            return PcCompatExternalInputIdentityMapper.Map(raw);
        }

        var normalized = Normalize(canonicalKey);
        if (normalized.StartsWith('f') &&
            int.TryParse(normalized.AsSpan(1), out var function) &&
            function is >= 13 and <= 24)
        {
            var unity = function <= 15 ? 281 + function : 654 + function;
            return
            [
                new(PcCompatInputIdentityKind.UnityKeyCode, unity),
                new(PcCompatInputIdentityKind.WindowsVirtualKey, 0x6F + function)
            ];
        }
        return Array.Empty<PcCompatCanonicalInputIdentity>();
    }

    internal static bool TryMapToAndroidKeyCode(string? canonicalKey, out int keyCode)
    {
        keyCode = -1;
        var normalized = Normalize(canonicalKey);
        if (normalized.Length == 1 && normalized[0] is >= 'a' and <= 'z')
        {
            keyCode = 29 + normalized[0] - 'a';
            return true;
        }
        if (normalized.Length == 1 && normalized[0] is >= '0' and <= '9')
        {
            keyCode = 7 + normalized[0] - '0';
            return true;
        }
        if (normalized.Length == 2 && normalized[0] == '_' && normalized[1] is >= '0' and <= '9')
        {
            keyCode = 7 + normalized[1] - '0';
            return true;
        }
        if (normalized.StartsWith("alpha", StringComparison.Ordinal) &&
            normalized.Length == 6 && normalized[5] is >= '0' and <= '9')
        {
            keyCode = 7 + normalized[5] - '0';
            return true;
        }
        if (normalized.StartsWith('f') &&
            int.TryParse(normalized.AsSpan(1), out var function) &&
            function is >= 1 and <= 12)
        {
            keyCode = 130 + function;
            return true;
        }
        if ((normalized.StartsWith("keypad", StringComparison.Ordinal) ||
             normalized.StartsWith("numpad", StringComparison.Ordinal)) &&
            int.TryParse(normalized.AsSpan(6), out var keypad) &&
            keypad is >= 0 and <= 9)
        {
            keyCode = 144 + keypad;
            return true;
        }

        keyCode = normalized switch
        {
            "home" => 122,
            "escape" or "esc" => 111,
            "uparrow" or "up" => 19,
            "downarrow" or "down" => 20,
            "leftarrow" or "left" => 21,
            "rightarrow" or "right" => 22,
            "tab" => 61,
            "space" => 62,
            "enter" or "return" => 66,
            "backspace" or "back" => 67,
            "graveaccent" or "backquote" => 68,
            "minus" => 69,
            "equal" or "equals" => 70,
            "leftbracket" or "lbracket" => 71,
            "rightbracket" or "rbracket" => 72,
            "backslash" => 73,
            "semicolon" => 74,
            "apostrophe" or "quote" => 75,
            "slash" => 76,
            "comma" => 55,
            "period" => 56,
            "leftalt" => 57,
            "rightalt" => 58,
            "leftshift" => 59,
            "rightshift" => 60,
            "menu" => 82,
            "pageup" or "pgup" => 92,
            "pagedown" or "pgdown" or "pgdn" => 93,
            "delete" or "del" or "forwarddelete" => 112,
            "leftctrl" or "leftcontrol" => 113,
            "rightctrl" or "rightcontrol" => 114,
            "capslock" => 115,
            "scrolllock" => 116,
            "leftsuper" => 117,
            "rightsuper" => 118,
            "printscreen" => 120,
            "pause" => 121,
            "end" => 123,
            "insert" => 124,
            "numlock" => 143,
            "keypaddivide" or "numpaddivide" => 154,
            "keypadmultiply" or "numpadmultiply" => 155,
            "keypadsubtract" or "numpadsubtract" or "keypadminus" => 156,
            "keypadadd" or "numpadadd" or "keypadplus" or "plus" => 157,
            "keypaddecimal" or "numpaddecimal" => 158,
            "keypadenter" or "numpadenter" => 160,
            "keypadequal" or "numpadequal" => 161,
            _ => -1
        };
        return keyCode >= 0;
    }

    private static string Normalize(string? canonicalKey)
    {
        var normalized = (canonicalKey ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .ToLowerInvariant();
        if (normalized.Length == 4 && normalized.StartsWith("key", StringComparison.Ordinal) &&
            normalized[3] is >= 'a' and <= 'z')
            return normalized[3].ToString();
        if (normalized.Length == 6 && normalized.StartsWith("digit", StringComparison.Ordinal) &&
            normalized[5] is >= '0' and <= '9')
            return normalized[5].ToString();
        return normalized switch
        {
            "arrowup" => "uparrow",
            "arrowdown" => "downarrow",
            "arrowleft" => "leftarrow",
            "arrowright" => "rightarrow",
            "controlleft" => "leftctrl",
            "controlright" => "rightctrl",
            "shiftleft" => "leftshift",
            "shiftright" => "rightshift",
            "altleft" => "leftalt",
            "altright" => "rightalt",
            "metaleft" or "osleft" => "leftsuper",
            "metaright" or "osright" => "rightsuper",
            "bracketleft" => "leftbracket",
            "bracketright" => "rightbracket",
            _ => normalized
        };
    }
}

/// <summary>Single process-wide V2 fan-out. Each verified Adapter keeps its own actor/state.</summary>
internal static class PcCompatVirtualInputAdapterHub
{
    private static readonly PcCompatModActorRuntime.PcCompatModActorHandle Actor =
        PcCompatModActorRuntime.Register(
            "pccompat:keyviewer:virtual-input-hub",
            failure => SafeLog("virtual input hub faulted: " + failure),
            mailboxCapacity: ModInteropConstants.VirtualInputQueueCapacity);
    private static int _registered;
    private static readonly object CacheGate = new();
    private static bool _active;
    private static long _sessionGeneration;
    private static int _deliveryEpoch;
    private static int _queuedEventUnits;
    private static readonly Dictionary<string, VirtualInputEvent> HeldKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, VirtualInputEvent> HeldPointers = [];

    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            ModInteropBroker.RegisterVirtualInputHostSink(Post);
    }

    private static bool Post(VirtualInputBatch batch)
    {
        var deliveryEpoch = 0;
        lock (CacheGate)
        {
            switch (batch.Kind)
            {
                case VirtualInputBatchKind.Started:
                    _active = true;
                    _sessionGeneration = batch.SessionGeneration;
                    HeldKeys.Clear();
                    HeldPointers.Clear();
                    break;
                case VirtualInputBatchKind.Ended:
                    _active = false;
                    HeldKeys.Clear();
                    HeldPointers.Clear();
                    break;
                default:
                    if (!_active || _sessionGeneration != batch.SessionGeneration)
                        return true;
                    foreach (var input in batch.Events)
                    {
                        if (input.Device == VirtualInputDevice.Keyboard &&
                            !string.IsNullOrWhiteSpace(input.CanonicalKey))
                        {
                            if (input.Phase == VirtualInputPhase.Down)
                                HeldKeys[input.CanonicalKey!] = input;
                            else if (input.Phase is VirtualInputPhase.Up or VirtualInputPhase.Cancel)
                                HeldKeys.Remove(input.CanonicalKey!);
                        }
                        else if (input.Device == VirtualInputDevice.Touch && input.PointerId >= 0)
                        {
                            if (input.Phase is VirtualInputPhase.Down or VirtualInputPhase.Move)
                                HeldPointers[input.PointerId] = input;
                            else if (input.Phase is VirtualInputPhase.Up or VirtualInputPhase.Cancel)
                                HeldPointers.Remove(input.PointerId);
                        }
                    }
                    break;
            }
            deliveryEpoch = _deliveryEpoch;
        }
        var units = Math.Max(1, batch.Events.Count);
        if (Interlocked.Add(ref _queuedEventUnits, units) >
            ModInteropConstants.VirtualInputQueueCapacity)
        {
            Interlocked.Add(ref _queuedEventUnits, -units);
            BreakSession(batch.SessionGeneration, deliveryEpoch, "event queue overflow");
            return false;
        }
        if (PcCompatModActorRuntime.TryPost(
                Actor,
                () =>
                {
                    try
                    {
                        lock (CacheGate)
                        {
                            if (deliveryEpoch == _deliveryEpoch)
                                PcCompatKeyViewerPreviewRuntime.DispatchVirtualInput(batch);
                        }
                    }
                    finally
                    {
                        Interlocked.Add(ref _queuedEventUnits, -units);
                    }
                }))
        {
            return true;
        }
        Interlocked.Add(ref _queuedEventUnits, -units);
        BreakSession(batch.SessionGeneration, deliveryEpoch, "actor mailbox rejected batch");
        return false;
    }

    private static void BreakSession(long sessionGeneration, int deliveryEpoch, string reason)
    {
        VirtualInputEvent[] cancelEvents;
        lock (CacheGate)
        {
            if (deliveryEpoch != _deliveryEpoch)
                return;
            ++_deliveryEpoch;
            cancelEvents = HeldKeys.Values
                .Concat(HeldPointers.Values)
                .Select(input => input with { Phase = VirtualInputPhase.Cancel })
                .ToArray();
            _active = false;
            HeldKeys.Clear();
            HeldPointers.Clear();
        }
        PcCompatKeyViewerPreviewRuntime.DispatchVirtualInput(new VirtualInputBatch(
            VirtualInputBatchKind.Cancelled,
            sessionGeneration,
            cancelEvents));
        PcCompatKeyViewerPreviewRuntime.DispatchVirtualInput(new VirtualInputBatch(
            VirtualInputBatchKind.Ended,
            sessionGeneration));
        SafeLog($"virtual input adapter hub circuit broken session={sessionGeneration}: {reason}");
    }

    internal static void Synchronize(Action<VirtualInputBatch> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (PcCompatModActorRuntime.TryPost(
                Actor,
                () =>
                {
                    lock (CacheGate)
                    {
                        if (!_active)
                            return;
                        sink(new VirtualInputBatch(
                            VirtualInputBatchKind.Started,
                            _sessionGeneration));
                        var events = HeldKeys.Values
                            .Concat(HeldPointers.Values)
                            .Select(input => input with
                            {
                                Sequence = 0,
                                Phase = VirtualInputPhase.Down
                            })
                            .ToArray();
                        sink(new VirtualInputBatch(
                            VirtualInputBatchKind.Snapshot,
                            _sessionGeneration,
                            events));
                    }
                }))
        {
            return;
        }
        SafeLog("virtual input adapter synchronization was rejected by the actor mailbox");
    }

    private static void SafeLog(string message)
    {
        try { StArray.ModManager.Manager.Logger.Warn(nameof(PcCompatVirtualInputAdapterHub), message); }
        catch { }
    }
}
