namespace Xphorror.PcModCompat;

public sealed class PcCompatPatchRegistry
{
    private readonly object _lock = new();
    private readonly List<PcCompatPatchDescriptor> _patches = new();

    public IReadOnlyList<PcCompatPatchDescriptor> Snapshot()
    {
        lock (_lock)
            return _patches.ToArray();
    }

    public IReadOnlyList<PcCompatPatchDescriptor> SnapshotByKind(PcCompatPatchKind kind)
    {
        lock (_lock)
            return _patches.Where(patch => patch.Kind == kind).ToArray();
    }

    public IReadOnlyList<PcCompatPatchDescriptor> FindByTarget(string targetType, string targetMethod)
    {
        lock (_lock)
            return _patches
                .Where(patch =>
                    string.Equals(patch.TargetType, targetType, StringComparison.Ordinal) &&
                    string.Equals(patch.TargetMethod, targetMethod, StringComparison.Ordinal))
                .ToArray();
    }

    public PcCompatPatchDescriptor? FindCallback(string callbackType, string callbackMethod)
    {
        lock (_lock)
            return _patches.FirstOrDefault(patch =>
                string.Equals(patch.CallbackType, callbackType, StringComparison.Ordinal) &&
                string.Equals(patch.CallbackMethod, callbackMethod, StringComparison.Ordinal));
    }

    public void Register(PcCompatPatchDescriptor descriptor)
    {
        lock (_lock)
            _patches.Add(descriptor);
    }

    public bool UpdateStatus(
        string modId,
        string callbackType,
        string callbackMethod,
        PcCompatPatchStatus status,
        string reason)
    {
        lock (_lock)
        {
            var patch = _patches.FirstOrDefault(candidate =>
                string.Equals(candidate.ModId, modId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.CallbackType, callbackType, StringComparison.Ordinal) &&
                string.Equals(candidate.CallbackMethod, callbackMethod, StringComparison.Ordinal));
            if (patch == null)
                return false;

            patch.Status = status;
            patch.Reason = reason;
            return true;
        }
    }

    public void RemoveMod(string modId)
    {
        lock (_lock)
            _patches.RemoveAll(patch => string.Equals(patch.ModId, modId, StringComparison.OrdinalIgnoreCase));
    }
}
