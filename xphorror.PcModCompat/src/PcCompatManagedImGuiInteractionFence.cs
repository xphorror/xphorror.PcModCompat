namespace Xphorror.PcModCompat;

internal enum PcCompatManagedImGuiEventKind : byte
{
    Layout,
    Repaint,
    Input
}

/// <summary>
/// A settings-surface transaction is not stable merely because input is delivered
/// during a Layout callback. MOD code can read layout state before the control
/// that receives the deferred value, so that Layout can still build the old tree.
/// After a commit Layout, non-Layout events are held until one more Layout builds
/// a tree from the committed MOD state.
/// </summary>
internal enum PcCompatManagedImGuiTransactionState : byte
{
    Stable,
    InputPending,
    CommitLayout,
    AwaitingRebuildLayout,
    RebuildLayout,
    StableVerification,
    Recovering
}

internal sealed class PcCompatManagedImGuiInteractionFence
{
    private const int PendingLayoutLifetime = 2;
    private const int PendingControlLimit = 256;

    private readonly Dictionary<ControlKey, PendingValue> _pending = [];
    private readonly Dictionary<OccurrenceKey, int> _occurrences = [];
    private bool _frameOpen;
    private bool _deliveredDuringLayout;
    private PcCompatManagedImGuiEventKind _frameEventKind;
    private PcCompatManagedImGuiTransactionState _state;
    private int _layoutEpoch;
    private int _legacyRawCursor;
    private int _legacyHostCursor;
    private long _pendingSequence;

    public PcCompatManagedImGuiTransactionState State => _state;

    public int PendingCount => _pending.Count;

    public int LayoutEpoch => _layoutEpoch;

    public bool ShouldDispatch(PcCompatManagedImGuiEventKind eventKind)
    {
        if (_frameOpen)
            throw new InvalidOperationException("IMGUI interaction fence dispatch was queried during an active frame.");

        return _state is not PcCompatManagedImGuiTransactionState.AwaitingRebuildLayout and
               not PcCompatManagedImGuiTransactionState.Recovering ||
               eventKind == PcCompatManagedImGuiEventKind.Layout;
    }

    public void BeginFrame(bool layoutFrame)
        => BeginFrame(layoutFrame
            ? PcCompatManagedImGuiEventKind.Layout
            : PcCompatManagedImGuiEventKind.Input);

    public void BeginFrame(PcCompatManagedImGuiEventKind eventKind)
    {
        if (_frameOpen)
            throw new InvalidOperationException("IMGUI interaction fence frame re-entry was rejected.");
        if (!ShouldDispatch(eventKind))
        {
            throw new InvalidOperationException(
                "IMGUI interaction fence rejected a non-Layout frame while a layout rebuild is pending.");
        }

        _frameOpen = true;
        _frameEventKind = eventKind;
        _deliveredDuringLayout = false;
        _legacyRawCursor = 0;
        _legacyHostCursor = 0;
        _occurrences.Clear();

        if (eventKind != PcCompatManagedImGuiEventKind.Layout)
            return;

        _layoutEpoch++;
        _state = _state switch
        {
            PcCompatManagedImGuiTransactionState.InputPending =>
                PcCompatManagedImGuiTransactionState.CommitLayout,
            PcCompatManagedImGuiTransactionState.AwaitingRebuildLayout or
                PcCompatManagedImGuiTransactionState.Recovering =>
                PcCompatManagedImGuiTransactionState.RebuildLayout,
            // A missing Repaint between two Layout events is already safe: both
            // Layouts use the same committed MOD state.
            PcCompatManagedImGuiTransactionState.StableVerification =>
                PcCompatManagedImGuiTransactionState.Stable,
            _ => _state
        };
    }

    public void EndFrame()
        => EndFrame(completed: true);

    public void EndFrame(bool completed)
    {
        if (!_frameOpen)
            return;
        try
        {
            if (completed)
                CompleteFrame();
        }
        finally
        {
            _frameOpen = false;
            _deliveredDuringLayout = false;
            _frameEventKind = PcCompatManagedImGuiEventKind.Input;
            _occurrences.Clear();
        }
    }

    public void MarkRecoverableLayoutFailure()
    {
        if (_frameOpen)
            throw new InvalidOperationException("IMGUI layout recovery was requested during an active frame.");

        // Input was captured against a tree Unity has rejected. It must never be
        // replayed into a later, potentially unrelated control.
        _pending.Clear();
        _state = PcCompatManagedImGuiTransactionState.Recovering;
    }

    public void Reset()
    {
        if (_frameOpen)
            throw new InvalidOperationException("IMGUI interaction fence reset was requested during an active frame.");

        _pending.Clear();
        _occurrences.Clear();
        _state = PcCompatManagedImGuiTransactionState.Stable;
        _layoutEpoch = 0;
        _legacyRawCursor = 0;
        _legacyHostCursor = 0;
        _pendingSequence = 0;
    }

    public bool ResolveRawButton(bool activated)
        => ResolveButton(CreateLegacyKey(ControlLane.Raw, ControlKind.Button), activated);

    public bool ResolveRawButton(bool activated, int callsiteToken)
        => ResolveButton(CreateTokenKey(ControlLane.Raw, ControlKind.Button, callsiteToken), activated);

    public bool ResolveRawToggle(bool current, bool observed)
        => ResolveValue(CreateLegacyKey(ControlLane.Raw, ControlKind.Toggle), current, observed);

    public bool ResolveRawToggle(bool current, bool observed, int callsiteToken)
        => ResolveValue(CreateTokenKey(ControlLane.Raw, ControlKind.Toggle, callsiteToken), current, observed);

    public string ResolveRawText(string current, string observed)
        => ResolveValue(CreateLegacyKey(ControlLane.Raw, ControlKind.Text), current, observed);

    public string ResolveRawText(string current, string observed, int callsiteToken)
        => ResolveValue(CreateTokenKey(ControlLane.Raw, ControlKind.Text, callsiteToken), current, observed);

    public bool ResolveHostButton(bool activated)
        => ResolveButton(CreateLegacyKey(ControlLane.Host, ControlKind.Button), activated);

    public bool ResolveHostButton(bool activated, int callsiteToken)
        => ResolveButton(CreateTokenKey(ControlLane.Host, ControlKind.Button, callsiteToken), activated);

    public T ResolveHostValue<T>(T current, T observed)
        => ResolveValue(CreateLegacyKey(ControlLane.Host, ControlKind.Value), current, observed);

    public T ResolveHostValue<T>(T current, T observed, int callsiteToken)
        => ResolveValue(CreateTokenKey(ControlLane.Host, ControlKind.Value, callsiteToken), current, observed);

    public T ResolveRawValue<T>(T current, T observed)
        => ResolveValue(CreateLegacyKey(ControlLane.Raw, ControlKind.Value), current, observed);

    public T ResolveRawValue<T>(T current, T observed, int callsiteToken)
        => ResolveValue(CreateTokenKey(ControlLane.Raw, ControlKind.Value, callsiteToken), current, observed);

    private bool ResolveButton(ControlKey key, bool activated)
    {
        if (!_frameOpen)
            return activated;

        if (_frameEventKind == PcCompatManagedImGuiEventKind.Layout)
        {
            if (_state == PcCompatManagedImGuiTransactionState.CommitLayout &&
                TryTakePending(key, out bool pending) && pending)
            {
                _deliveredDuringLayout = true;
                return true;
            }

            // Unity buttons do not activate in Layout. Returning false also
            // prevents an unexpected native result from mutating MOD state while
            // a rebuild Layout is being assembled.
            return false;
        }

        if (activated)
            QueuePending(key, true);
        return false;
    }

    private T ResolveValue<T>(ControlKey key, T current, T observed)
    {
        if (!_frameOpen)
            return observed;

        if (_frameEventKind == PcCompatManagedImGuiEventKind.Layout)
        {
            if (_state == PcCompatManagedImGuiTransactionState.CommitLayout &&
                TryTakePending(key, out T pending))
            {
                _deliveredDuringLayout = true;
                return pending;
            }

            // Value controls must not introduce a state transition while a
            // normal/rebuild Layout is traversing the old tree.
            return current;
        }

        if (!EqualityComparer<T>.Default.Equals(current, observed))
            QueuePending(key, observed);
        return current;
    }

    private void CompleteFrame()
    {
        if (_frameEventKind == PcCompatManagedImGuiEventKind.Layout)
        {
            if (_state == PcCompatManagedImGuiTransactionState.CommitLayout)
            {
                if (_deliveredDuringLayout)
                {
                    // Any unmatched input belongs to the old control tree. Once
                    // a value has changed that tree, replaying it later would be
                    // worse than dropping a stale interaction.
                    _pending.Clear();
                    _state = PcCompatManagedImGuiTransactionState.AwaitingRebuildLayout;
                    return;
                }

                TrimExpiredPending();
                _state = _pending.Count == 0
                    ? PcCompatManagedImGuiTransactionState.Stable
                    : PcCompatManagedImGuiTransactionState.InputPending;
                return;
            }

            if (_state == PcCompatManagedImGuiTransactionState.RebuildLayout)
            {
                _state = PcCompatManagedImGuiTransactionState.StableVerification;
                return;
            }

            TrimExpiredPending();
            return;
        }

        if (_state == PcCompatManagedImGuiTransactionState.StableVerification)
        {
            _state = _pending.Count == 0
                ? PcCompatManagedImGuiTransactionState.Stable
                : PcCompatManagedImGuiTransactionState.InputPending;
        }
    }

    private void QueuePending(ControlKey key, object? value)
    {
        if (!_pending.ContainsKey(key) && _pending.Count >= PendingControlLimit)
            EvictOldestPending();

        _pending[key] = new PendingValue(value, _layoutEpoch, ++_pendingSequence);
        if (_state is PcCompatManagedImGuiTransactionState.Stable or
            PcCompatManagedImGuiTransactionState.StableVerification)
        {
            _state = PcCompatManagedImGuiTransactionState.InputPending;
        }
    }

    private bool TryTakePending<T>(ControlKey key, out T value)
    {
        if (_pending.Remove(key, out var pending) && pending.Value is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    private void TrimExpiredPending()
    {
        while (true)
        {
            ControlKey? expired = null;
            foreach (var pair in _pending)
            {
                if (_layoutEpoch - pair.Value.LayoutEpoch >= PendingLayoutLifetime)
                {
                    expired = pair.Key;
                    break;
                }
            }

            if (expired is not { } key)
                return;
            _pending.Remove(key);
        }
    }

    private void EvictOldestPending()
    {
        ControlKey? oldest = null;
        var oldestSequence = long.MaxValue;
        foreach (var pair in _pending)
        {
            if (pair.Value.Sequence >= oldestSequence)
                continue;
            oldest = pair.Key;
            oldestSequence = pair.Value.Sequence;
        }

        if (oldest is { } key)
            _pending.Remove(key);
    }

    private ControlKey CreateLegacyKey(ControlLane lane, ControlKind kind)
    {
        var token = lane == ControlLane.Raw ? _legacyRawCursor++ : _legacyHostCursor++;
        return new ControlKey(lane, kind, token, 0);
    }

    private ControlKey CreateTokenKey(ControlLane lane, ControlKind kind, int callsiteToken)
    {
        var occurrenceKey = new OccurrenceKey(lane, kind, callsiteToken);
        if (!_occurrences.TryGetValue(occurrenceKey, out var occurrence))
            occurrence = 0;
        _occurrences[occurrenceKey] = occurrence + 1;
        return new ControlKey(lane, kind, callsiteToken, occurrence);
    }

    private enum ControlLane : byte
    {
        Raw,
        Host
    }

    private enum ControlKind : byte
    {
        Button,
        Toggle,
        Text,
        Value
    }

    private readonly record struct OccurrenceKey(ControlLane Lane, ControlKind Kind, int CallsiteToken);
    private readonly record struct ControlKey(ControlLane Lane, ControlKind Kind, int CallsiteToken, int Occurrence);
    private readonly record struct PendingValue(object? Value, int LayoutEpoch, long Sequence);
}
