using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Runtime;

/// <summary>
/// Maps ABI-adapted logical handles and executable guarded-call aliases back to their
/// physical methods. HookHelper always installs hooks on the physical target, so a
/// compatibility or safety wrapper cannot split the target's owner chain.
/// </summary>
internal static class RuntimeMethodCompatibility
{
    private static readonly ConcurrentDictionary<nint, TargetDescriptor> Handles = new();

    public static bool IsSupported(RuntimeMethodCompatibilityKind kind)
        => HookHelper.Instance is IRuntimeMethodCompatibilityHook provider &&
           provider.SupportsCompatibility(kind);

    public static nint CreateHandle(nint target, RuntimeMethodCompatibilityKind kind)
    {
        if (target == nint.Zero || kind == RuntimeMethodCompatibilityKind.None)
            return nint.Zero;

        var gcHandle = GCHandle.Alloc(new TargetDescriptor(target, kind, true));
        var handle = GCHandle.ToIntPtr(gcHandle);
        if (handle == nint.Zero || !Handles.TryAdd(handle, (TargetDescriptor)gcHandle.Target!))
        {
            gcHandle.Free();
            return nint.Zero;
        }
        return handle;
    }

    public static bool RegisterPassThroughHandle(nint handle, nint target)
    {
        if (handle == nint.Zero || target == nint.Zero || handle == target)
            return false;
        return Handles.TryAdd(
            handle,
            new TargetDescriptor(target, RuntimeMethodCompatibilityKind.None, false));
    }

    public static bool TryResolveHandle(
        nint handle,
        out nint target,
        out RuntimeMethodCompatibilityKind kind)
    {
        if (Handles.TryGetValue(handle, out var descriptor))
        {
            target = descriptor.Target;
            kind = descriptor.Kind;
            return true;
        }

        target = handle;
        kind = RuntimeMethodCompatibilityKind.None;
        return false;
    }

    public static void ReleaseHandle(nint handle)
    {
        if (!Handles.TryRemove(handle, out var descriptor) || !descriptor.OwnsGcHandle)
            return;
        var gcHandle = GCHandle.FromIntPtr(handle);
        if (gcHandle.IsAllocated)
            gcHandle.Free();
    }

    private sealed record TargetDescriptor(
        nint Target,
        RuntimeMethodCompatibilityKind Kind,
        bool OwnsGcHandle);
}
