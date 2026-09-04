namespace StArray.ModManager.Runtime;

/// <summary>
/// A generation-bound native operation. Dispose only after the native worker has stopped
/// accessing MOD code, native callbacks, and Unity resources.
/// </summary>
public interface IModNativeOperationLease : IDisposable
{
    /// <summary>Token that can be copied into a private native worker.</summary>
    ModNativeOperationToken Token { get; }

    /// <summary>True after Host cancellation or when the token is no longer current.</summary>
    bool IsCancellationRequested { get; }
}

/// <summary>
/// Registers private unmanaged work against the currently executing MOD generation. Native
/// workers may poll the token through the public C ABI in modmanager_native_operation_client.h.
/// </summary>
public static class ModNativeOperations
{
    public static IModNativeOperationLease Begin(string name)
    {
        if (TryBegin(name, out var lease))
            return lease!;
        throw new InvalidOperationException(
            "A native MOD operation requires an active MOD scope and native registry.");
    }

    public static bool TryBegin(
        string name,
        out IModNativeOperationLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return HookHelper.TryBeginNativeOperation(name, out lease);
    }
}
