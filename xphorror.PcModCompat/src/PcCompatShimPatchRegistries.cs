using System.Reflection;
using System.Runtime.Loader;

namespace Xphorror.PcModCompat;

/// <summary>
/// The shim-side logical patch registries the host reads back. JALib's <c>JAPatcher</c> and Harmony's
/// <c>HarmonyRegistry</c> deliberately expose the same duck-typed statics - <c>RegisteredPatchCount</c>,
/// <c>SnapshotRegisteredPatches()</c>, <c>ClearRegisteredPatches()</c> - and the same record property
/// names, so a single reader serves both instead of two copies that can drift apart.
///
/// Nothing here touches a physical hook. The native HookBroker owns those for the whole process, and a
/// MOD calling Patch/Unpatch only moves entries in these registries.
/// </summary>
internal static class PcCompatShimPatchRegistries
{
    internal readonly record struct Registry(
        string AssemblyName,
        string TypeName,
        string DescriptorSource,
        bool Required);

    internal static readonly Registry[] All =
    [
        // JALib is the managed oracle. Without its registry there is no patch truth to read at all, so a
        // missing type means the shim payload is wrong and under-reporting would hide that.
        new("JALib", "JALib.Core.Patch.JAPatcher", "managed_oracle", Required: true),
        // Harmony descriptors are additive on top of that. An older shim payload paired with a newer host
        // must not stop every MOD from loading over patches we can simply report as unavailable.
        new("0Harmony", "HarmonyLib.HarmonyRegistry", "shim_harmony_registry", Required: false)
    ];

    /// <summary>
    /// Resolves a required registry type inside the MOD's load context, throwing when it is absent.
    /// </summary>
    internal static Type Resolve(AssemblyLoadContext context, Registry registry)
    {
        var assembly = context.LoadFromAssemblyName(new AssemblyName(registry.AssemblyName));
        return assembly.GetType(registry.TypeName, throwOnError: true)!;
    }

    /// <summary>
    /// Resolves a registry type, tolerating absence for the optional ones. The failure is logged rather
    /// than swallowed: a shim assembly that loads but has no registry type is a stale payload, and that
    /// is only diagnosable if the reason survives.
    /// </summary>
    internal static bool TryResolve(AssemblyLoadContext context, Registry registry, out Type registryType)
    {
        try
        {
            registryType = Resolve(context, registry);
            return true;
        }
        catch (Exception exception) when (!registry.Required)
        {
            StArray.ModManager.Manager.Logger.Warn(
                nameof(PcCompatShimPatchRegistries),
                $"shim patch registry unavailable assembly={registry.AssemblyName} " +
                $"type={registry.TypeName}: {exception.GetType().Name}: {exception.Message}");
            registryType = null!;
            return false;
        }
    }

    internal static Array? Snapshot(Type registryType)
        => (Array?)registryType
            .GetMethod("SnapshotRegisteredPatches", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, null);

    internal static PropertyInfo? CountProperty(Type registryType)
        => registryType.GetProperty("RegisteredPatchCount", BindingFlags.Public | BindingFlags.Static);

    /// <summary>
    /// Returns a cheap monotonic change reader. Harmony exposes a dedicated revision that changes
    /// on Patch, Unpatch and clear; older/JALib registries use RegisteredPatchCount as their host
    /// revision contract. The delegate is compiled once so the per-frame liveness check performs no
    /// reflection or allocation.
    /// </summary>
    internal static Func<int>? ChangeVersionReader(Type registryType)
    {
        var property = registryType.GetProperty("Revision", BindingFlags.Public | BindingFlags.Static)
                       ?? CountProperty(registryType);
        if (property?.PropertyType != typeof(int) || property.GetMethod is not { IsStatic: true } getter)
            return null;
        return getter.CreateDelegate<Func<int>>();
    }
}
