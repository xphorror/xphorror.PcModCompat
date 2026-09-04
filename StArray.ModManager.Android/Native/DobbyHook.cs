using System.Globalization;
using StArray.ModManager.Runtime;
using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Android.Native;

public class DobbyHook :
    IGenerationScopedHook,
    IRuntimeMethodCompatibilityHook,
    IManagedCallbackGateAwareHook,
    INativeModOperationProvider
{
    public bool SupportsRuntimeUnhook => false;
    public bool SupportsOwnerControl => Dobby.SupportsOwnerControl;

    public nint Hook(nint target, nint detour)
    {
        var owner = HookHelper.CurrentOwnerId ?? nameof(DobbyHook);
        if (Dobby.Hook(target, detour, out var origin, owner) != 0)
            return nint.Zero;
        return origin;
    }

    public bool SupportsCompatibility(RuntimeMethodCompatibilityKind kind)
        => Dobby.SupportsCompatibility(kind);

    public nint HookWithManagedCallbackGate(nint target, nint detour)
    {
        var owner = HookHelper.CurrentOwnerId ?? nameof(DobbyHook);
        return Dobby.HookWithManagedCallbackGate(
            target,
            detour,
            out var origin,
            owner) == 0
            ? origin
            : nint.Zero;
    }

    public nint HookCompatible(
        nint target,
        nint detour,
        RuntimeMethodCompatibilityKind kind)
    {
        var owner = HookHelper.CurrentOwnerId ?? nameof(DobbyHook);
        return Dobby.HookCompatible(target, detour, out var origin, kind, owner) == 0
            ? origin
            : nint.Zero;
    }

    public nint HookCompatibleWithManagedCallbackGate(
        nint target,
        nint detour,
        RuntimeMethodCompatibilityKind kind)
    {
        var owner = HookHelper.CurrentOwnerId ?? nameof(DobbyHook);
        return Dobby.HookCompatibleWithManagedCallbackGate(
            target,
            detour,
            out var origin,
            kind,
            owner) == 0
            ? origin
            : nint.Zero;
    }

    public bool Unhook(nint target)
    {
        var owner = HookHelper.CurrentOwnerId;
        return !string.IsNullOrWhiteSpace(owner) &&
               Dobby.RetireOwnerTarget(owner, target);
    }

    public bool SetOwnerEnabled(string owner, bool enabled)
        => Dobby.SetOwnerEnabled(owner, enabled);

    public bool SetOwnerGenerationEnabled(string owner, long generation, bool enabled)
        => Dobby.SetOwnerGenerationEnabled(owner, generation, enabled);

    public bool RetireOwnerTarget(string owner, nint target)
        => Dobby.RetireOwnerTarget(owner, target);

    public bool RetireOwnerGenerationTarget(string owner, long generation, nint target)
        => Dobby.RetireOwnerGenerationTarget(owner, generation, target);

    public int RetireOwner(string owner)
        => Dobby.RetireOwner(owner);

    public int RetireOwnerGeneration(string owner, long generation)
        => Dobby.RetireOwnerGeneration(owner, generation);

    public int GetRetainedLayerCount(string owner)
        => Dobby.GetOwnerRetainedLayerCount(owner);

    public int GetRetainedLayerCount(string owner, long generation)
        => Dobby.GetOwnerGenerationRetainedLayerCount(owner, generation);

    public int GetUntrackedCallbackLayerCount(string owner, long generation)
        => Dobby.GetUntrackedCallbackLayerCount(owner, generation);

    public bool OpenGeneration(string owner, long generation)
        => Dobby.OpenNativeOperationGeneration(owner, generation);

    public bool TryBeginOperation(
        string owner,
        long generation,
        string name,
        out ModNativeOperationToken token)
        => Dobby.TryBeginNativeOperation(owner, generation, name, out token);

    public int GetCancellationState(in ModNativeOperationToken token)
        => Dobby.GetNativeOperationCancellationState(token);

    public bool EndOperation(in ModNativeOperationToken token)
        => Dobby.EndNativeOperation(token);

    public bool CancelGenerationAndWait(
        string owner,
        long generation,
        uint timeoutMilliseconds)
        => Dobby.CancelNativeOperationGenerationAndWait(
            owner,
            generation,
            timeoutMilliseconds);

    public bool ResumeGeneration(string owner, long generation)
        => Dobby.ResumeNativeOperationGeneration(owner, generation);

    public bool RetireGeneration(string owner, long generation)
        => Dobby.RetireNativeOperationGeneration(owner, generation);

    public int GetActiveOperationCount(string owner, long generation)
        => Dobby.GetActiveNativeOperationCount(owner, generation);

    public nint GetFunction(string library, string name)
    {
        return Dobby.SymbolResolver(library, name);
    }

    public nint GetFunctionRVA(string library, long rva)
    {
        var soName = library.EndsWith(".so", StringComparison.Ordinal)
            ? library
            : library + ".so";

        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            if (!line.EndsWith(soName, StringComparison.Ordinal))
                continue;

            var dash = line.IndexOf('-');
            if (dash < 0)
                continue;

            if (long.TryParse(
                    line.AsSpan(0, dash),
                    NumberStyles.HexNumber,
                    provider: null,
                    out var baseAddress))
            {
                return (nint)baseAddress + (nint)rva;
            }
        }

        return nint.Zero;
    }
}
