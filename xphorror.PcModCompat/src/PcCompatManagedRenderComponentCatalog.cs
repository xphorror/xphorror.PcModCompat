using System.Reflection;

namespace Xphorror.PcModCompat;

/// <summary>
/// Describes managed render-component shapes for which the host has a complete native callback
/// adapter. Component names and MOD ids are discovered from metadata; they are not allowlisted here.
/// </summary>
public static class PcCompatManagedRenderComponentCatalog
{
    public sealed record Entry(
        string ModId,
        string ComponentAssembly,
        string ComponentType,
        string HostAssembly,
        string BaseType,
        string HostType,
        string RenderMethod,
        string RenderParameterType,
        string Reason);

    private sealed record Capability(
        string BaseAssembly,
        string BaseType,
        string HostAssembly,
        string HostType,
        string RenderMethod,
        string RenderParameterType,
        string Reason);

    private static readonly Capability[] Capabilities =
    [
        new(
            BaseAssembly: "UnityEngine.UI",
            BaseType: "UnityEngine.UI.MaskableGraphic",
            HostAssembly: "UnityEngine.UI",
            HostType: "UnityEngine.UI.RawImage",
            RenderMethod: "OnPopulateMesh",
            RenderParameterType: "UnityEngine.UI.VertexHelper",
            Reason: "shape-verified MaskableGraphic override hosted by RawImage; the managed " +
                    "OnPopulateMesh completely replaces the host mesh callback")
    ];

    public static bool TryDescribeMetadataType(
        string modId,
        string componentAssembly,
        string componentType,
        string baseAssembly,
        string baseType,
        bool isAbstract,
        bool hasGenericParameters,
        int matchingDeclaredRenderMethods,
        out PcCompatManagedRenderComponentDescriptor descriptor)
    {
        descriptor = null!;
        if (isAbstract || hasGenericParameters || matchingDeclaredRenderMethods != 1)
            return false;
        var capability = Capabilities.SingleOrDefault(candidate =>
            string.Equals(candidate.BaseAssembly, baseAssembly, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.BaseType, baseType, StringComparison.Ordinal));
        if (capability == null)
            return false;

        descriptor = new PcCompatManagedRenderComponentDescriptor
        {
            ModId = modId,
            ComponentAssembly = componentAssembly,
            ComponentType = componentType,
            BaseAssembly = capability.BaseAssembly,
            BaseType = capability.BaseType,
            HostAssembly = capability.HostAssembly,
            HostType = capability.HostType,
            RenderMethod = capability.RenderMethod,
            RenderParameterType = capability.RenderParameterType,
            Reason = capability.Reason
        };
        return true;
    }

    public static bool TryMatchRuntimeType(string modId, Type componentType, out Entry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(componentType);
        entry = null!;
        if (componentType.IsAbstract || componentType.ContainsGenericParameters)
            return false;

        var baseType = componentType.BaseType;
        var capability = Capabilities.SingleOrDefault(candidate =>
            string.Equals(candidate.BaseType, baseType?.FullName, StringComparison.Ordinal) &&
            string.Equals(
                candidate.BaseAssembly,
                baseType?.Assembly.GetName().Name,
                StringComparison.OrdinalIgnoreCase));
        if (capability == null)
            return false;

        var matches = componentType.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Count(method =>
                method.Name == capability.RenderMethod &&
                method.ReturnType == typeof(void) &&
                method.GetParameters() is [{ ParameterType.FullName: var parameterType }] &&
                parameterType == capability.RenderParameterType);
        if (matches != 1)
            return false;

        entry = new Entry(
            modId,
            componentType.Assembly.GetName().Name ?? string.Empty,
            componentType.FullName ?? string.Empty,
            capability.HostAssembly,
            capability.BaseType,
            capability.HostType,
            capability.RenderMethod,
            capability.RenderParameterType,
            capability.Reason);
        return true;
    }

    public static IReadOnlyList<Entry> DistinctHostTargets(
        IReadOnlyList<PcCompatManagedRenderComponentDescriptor> descriptors)
        => descriptors
            .GroupBy(
                descriptor => descriptor.HostAssembly + "!" + descriptor.HostType + "::" +
                              descriptor.RenderMethod,
                StringComparer.Ordinal)
            .Select(group => FromDescriptor(group.First()))
            .OrderBy(entry => entry.HostType, StringComparer.Ordinal)
            .ThenBy(entry => entry.RenderMethod, StringComparer.Ordinal)
            .ToArray();

    public static Entry FromDescriptor(PcCompatManagedRenderComponentDescriptor descriptor)
        => new(
            descriptor.ModId,
            descriptor.ComponentAssembly,
            descriptor.ComponentType,
            descriptor.HostAssembly,
            descriptor.BaseType,
            descriptor.HostType,
            descriptor.RenderMethod,
            descriptor.RenderParameterType,
            descriptor.Reason);
}
