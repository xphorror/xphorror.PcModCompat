using System.Collections;

namespace Xphorror.PcModCompat;

public static class PcCompatKeyViewerPresentationDefaults
{
    public static bool TryClearLegacyTouchLabels(
        IList target,
        int laneCount,
        out int[] changedIndices,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(target);
        changedIndices = Array.Empty<int>();
        if (laneCount < 0 || target.IsReadOnly || target.Count < laneCount)
        {
            error = $"label collection has {target.Count} entries for {laneCount} lanes";
            return false;
        }
        for (var index = 0; index < laneCount; ++index)
        {
            var current = target[index];
            if (current == null || current is string)
                continue;
            error = $"label collection entry {index} is {current.GetType().FullName}, expected string";
            return false;
        }

        List<int>? changed = null;
        for (var index = 0; index < laneCount; ++index)
        {
            if (!string.Equals(target[index] as string, $"T{index + 1}", StringComparison.Ordinal))
                continue;
            target[index] = null;
            (changed ??= []).Add(index);
        }
        changedIndices = changed?.ToArray() ?? Array.Empty<int>();
        error = null;
        return true;
    }

    public static bool TryFill(
        IList target,
        IReadOnlyList<string> defaults,
        out int changed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(defaults);
        changed = 0;
        error = null;
        if (target.IsReadOnly || target.IsFixedSize && target.Count < defaults.Count)
        {
            error = $"label collection has {target.Count} entries for {defaults.Count} lanes";
            return false;
        }
        if (target.Count < defaults.Count)
        {
            error = $"label collection has {target.Count} entries for {defaults.Count} lanes";
            return false;
        }

        for (var index = 0; index < defaults.Count; ++index)
        {
            var current = target[index];
            if (current != null && current is not string)
            {
                error = $"label collection entry {index} is {current.GetType().FullName}, expected string";
                return false;
            }
        }
        for (var index = 0; index < defaults.Count; ++index)
        {
            var current = target[index];
            if (!string.IsNullOrWhiteSpace(current as string))
                continue;
            target[index] = defaults[index];
            ++changed;
        }
        return true;
    }
}

public sealed class PcCompatKeyViewerPresentationProjection
{
    private IList? _target;
    private string?[] _original = Array.Empty<string?>();
    private string?[] _projected = Array.Empty<string?>();
    private bool[] _owned = Array.Empty<bool>();

    public bool TryApply(
        IList target,
        IReadOnlyList<string> desired,
        bool adoptLegacyTouchLabels,
        out int[] changedIndices,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        changedIndices = Array.Empty<int>();
        if (!TryValidate(target, desired.Count, out error))
            return false;

        if (!ReferenceEquals(_target, target) || _owned.Length != desired.Count)
        {
            RestoreCore();
            _target = target;
            _original = new string?[desired.Count];
            _projected = new string?[desired.Count];
            _owned = new bool[desired.Count];
        }

        List<int>? changed = null;
        for (var index = 0; index < desired.Count; ++index)
        {
            var current = target[index] as string;
            if (_owned[index] && !string.Equals(
                    current,
                    _projected[index],
                    StringComparison.Ordinal))
            {
                _owned[index] = false;
                _original[index] = null;
                _projected[index] = null;
            }

            if (!_owned[index])
            {
                var legacyTouchLabel = $"T{index + 1}";
                if (!string.IsNullOrWhiteSpace(current) &&
                    (!adoptLegacyTouchLabels || !string.Equals(
                        current,
                        legacyTouchLabel,
                        StringComparison.Ordinal)))
                {
                    continue;
                }
                _owned[index] = true;
                _original[index] = string.IsNullOrWhiteSpace(current) ? current : null;
            }

            var next = desired[index];
            _projected[index] = next;
            if (string.Equals(current, next, StringComparison.Ordinal))
                continue;
            target[index] = next;
            (changed ??= []).Add(index);
        }

        changedIndices = changed?.ToArray() ?? Array.Empty<int>();
        error = null;
        return true;
    }

    public bool TryRestore(out int[] changedIndices, out string? error)
    {
        if (_target == null)
        {
            changedIndices = Array.Empty<int>();
            error = null;
            return true;
        }
        if (!TryValidate(_target, _owned.Length, out error))
        {
            changedIndices = Array.Empty<int>();
            return false;
        }

        var changed = RestoreCore();
        changedIndices = changed?.ToArray() ?? Array.Empty<int>();
        _target = null;
        _original = Array.Empty<string?>();
        _projected = Array.Empty<string?>();
        _owned = Array.Empty<bool>();
        error = null;
        return true;
    }

    private List<int>? RestoreCore()
    {
        if (_target == null)
            return null;
        List<int>? changed = null;
        for (var index = 0; index < _owned.Length; ++index)
        {
            if (!_owned[index] || !string.Equals(
                    _target[index] as string,
                    _projected[index],
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.Equals(
                    _target[index] as string,
                    _original[index],
                    StringComparison.Ordinal))
            {
                _target[index] = _original[index];
                (changed ??= []).Add(index);
            }
            _owned[index] = false;
        }
        return changed;
    }

    private static bool TryValidate(IList target, int requiredCount, out string? error)
    {
        if (target.IsReadOnly || target.Count < requiredCount)
        {
            error = $"label collection has {target.Count} entries for {requiredCount} lanes";
            return false;
        }
        for (var index = 0; index < requiredCount; ++index)
        {
            var current = target[index];
            if (current == null || current is string)
                continue;
            error = $"label collection entry {index} is {current.GetType().FullName}, expected string";
            return false;
        }
        error = null;
        return true;
    }
}
