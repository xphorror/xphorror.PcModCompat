namespace Xphorror.PcModCompat;

// Import-time resolution of a patch target's exact runtime signature.
//
// The native resolver (validate_method_identity in pccompat_hook_rules.cpp) is deliberately strict:
// it refuses a target unless the declared return type and every parameter type match the runtime
// metadata exactly. That is what keeps hook installation fail-closed. The importer, however, only
// reads the MOD assembly, so it knows the target's *name* and at best the argument-type list the
// author wrote in [HarmonyPatch(..., new[] { typeof(x) })] - never the return type, and never the
// parameter types of an attribute that omitted them.
//
// PcCompatCallbackDomainMappings exists because of that gap: every supported target carries a
// hand-audited signature. This resolver is the general answer. Import runs inside the game process
// with IL2CPP already loaded, so the host can answer "what is the exact signature of this method"
// from live metadata, and the importer can then emit a rule the strict native resolver accepts.
//
// The provider is registered by the platform host (Android) and left null everywhere else. With no
// provider the importer behaves exactly as it did before this type existed: targets outside the
// verified catalog stay unmapped and no hook is installed. Nothing here ever guesses.
public sealed class PcCompatTargetSignatureRequest
{
    // Empty means "the host decides" - the importer usually knows only that a Harmony attribute
    // named a type, not which assembly image holds it.
    public string AssemblyName { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public required string TypeName { get; init; }
    public required string MethodName { get; init; }

    // Argument types as written in the patch attribute. Present only when the author supplied them;
    // the host uses them to disambiguate overloads, never to fabricate a signature.
    public IReadOnlyList<string> ArgumentTypeNames { get; init; } = Array.Empty<string>();
    public bool HasArgumentTypeNames { get; init; }

    public override string ToString()
    {
        var owner = string.IsNullOrEmpty(Namespace) ? TypeName : Namespace + "." + TypeName;
        var arguments = HasArgumentTypeNames ? "(" + string.Join(", ", ArgumentTypeNames) + ")" : "(?)";
        return (string.IsNullOrEmpty(AssemblyName) ? string.Empty : "[" + AssemblyName + "]") +
               owner + "." + MethodName + arguments;
    }
}

// What the host read back out of runtime metadata. Every field is required because a partially
// resolved signature is useless: the strict native resolver would reject it anyway.
public sealed class PcCompatResolvedTargetSignature
{
    public required string AssemblyName { get; init; }
    public string Namespace { get; init; } = string.Empty;
    public required string TypeName { get; init; }
    public required string MethodName { get; init; }
    public required bool IsStatic { get; init; }
    public required string ReturnType { get; init; }
    public IReadOnlyList<string> ParameterTypes { get; init; } = Array.Empty<string>();

    public override string ToString()
    {
        var owner = string.IsNullOrEmpty(Namespace) ? TypeName : Namespace + "." + TypeName;
        return $"[{AssemblyName}]{(IsStatic ? "static " : string.Empty)}{ReturnType} {owner}.{MethodName}" +
               "(" + string.Join(", ", ParameterTypes) + ")";
    }
}

public static class PcCompatTargetSignatureResolver
{
    // Returning false with a populated error is the normal outcome for an absent or ambiguous
    // target; the importer turns that into an audit entry, not an exception.
    public delegate bool ProviderDelegate(
        PcCompatTargetSignatureRequest request,
        out PcCompatResolvedTargetSignature? signature,
        out string error);

    private static ProviderDelegate? s_provider;

    public static void RegisterProvider(ProviderDelegate? provider)
        => Volatile.Write(ref s_provider, provider);

    public static bool IsProviderRegistered
        => Volatile.Read(ref s_provider) != null;

    public static bool TryResolve(
        PcCompatTargetSignatureRequest request,
        out PcCompatResolvedTargetSignature? signature,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        signature = null;
        error = string.Empty;

        var provider = Volatile.Read(ref s_provider);
        if (provider == null)
        {
            error = "no runtime target signature provider is registered";
            return false;
        }

        // A provider that throws is a host defect, not a MOD defect: report it like any other
        // resolution failure so one bad target cannot abort the whole import.
        try
        {
            if (!provider(request, out var resolved, out var providerError))
            {
                error = string.IsNullOrWhiteSpace(providerError)
                    ? "runtime target signature resolution failed"
                    : providerError;
                return false;
            }

            if (resolved == null)
            {
                error = "runtime target signature provider reported success without a signature";
                return false;
            }

            if (!IsConsistent(request, resolved, out var mismatch))
            {
                error = mismatch;
                return false;
            }

            signature = resolved;
            return true;
        }
        catch (Exception exception)
        {
            error = $"runtime target signature provider threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    // The host is trusted to read metadata correctly but not to answer a different question than
    // the one asked. A signature for another type or method would silently install a hook on the
    // wrong entry point, so it is rejected here rather than downstream.
    private static bool IsConsistent(
        PcCompatTargetSignatureRequest request,
        PcCompatResolvedTargetSignature resolved,
        out string mismatch)
    {
        mismatch = string.Empty;

        if (string.IsNullOrWhiteSpace(resolved.AssemblyName))
        {
            mismatch = $"resolved signature for {request} has no assembly name";
            return false;
        }

        if (!string.Equals(resolved.TypeName, request.TypeName, StringComparison.Ordinal))
        {
            mismatch = $"resolved signature type {resolved.TypeName} does not match requested {request.TypeName}";
            return false;
        }

        if (!string.Equals(resolved.MethodName, request.MethodName, StringComparison.Ordinal))
        {
            mismatch = $"resolved signature method {resolved.MethodName} does not match requested {request.MethodName}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(resolved.ReturnType))
        {
            mismatch = $"resolved signature for {request} has no return type";
            return false;
        }

        if (resolved.ParameterTypes.Any(string.IsNullOrWhiteSpace))
        {
            mismatch = $"resolved signature for {request} has an empty parameter type";
            return false;
        }

        // When the author wrote an explicit argument list the arity has to agree, otherwise the
        // host resolved a different overload than the attribute named.
        if (request.HasArgumentTypeNames && resolved.ParameterTypes.Count != request.ArgumentTypeNames.Count)
        {
            mismatch = $"resolved signature for {request} has {resolved.ParameterTypes.Count} parameters, " +
                       $"attribute declared {request.ArgumentTypeNames.Count}";
            return false;
        }

        return true;
    }
}
