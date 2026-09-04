using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat;

public sealed class PcCompatStaticPatchScanReport
{
    public const string CurrentFormatVersion = "static-patch-scan-v4-render-components";

    public string FormatVersion { get; init; } = CurrentFormatVersion;
    public required string ModId { get; init; }
    public int TargetGameRevision { get; init; }
    public IReadOnlyList<string> AssembliesScanned { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PcCompatPatchDescriptor> Patches { get; init; } = Array.Empty<PcCompatPatchDescriptor>();
    public IReadOnlyList<PcCompatManagedRenderComponentDescriptor> ManagedRenderComponents { get; init; } =
        Array.Empty<PcCompatManagedRenderComponentDescriptor>();
    public IReadOnlyList<PcCompatStaticPatchScanIssue> Issues { get; init; } = Array.Empty<PcCompatStaticPatchScanIssue>();

    [JsonIgnore]
    public IReadOnlyList<PcCompatPatchDescriptor> ActivePatches
        => Patches.Where(patch => patch.IsApplicableToRevision(TargetGameRevision)).ToArray();

    public string ToJson()
        => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class PcCompatManagedRenderComponentDescriptor
{
    public required string ModId { get; init; }
    public required string ComponentAssembly { get; init; }
    public required string ComponentType { get; init; }
    public required string BaseAssembly { get; init; }
    public required string BaseType { get; init; }
    public required string HostAssembly { get; init; }
    public required string HostType { get; init; }
    public required string RenderMethod { get; init; }
    public required string RenderParameterType { get; init; }
    public required string Reason { get; init; }
}

public sealed class PcCompatStaticPatchScanIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? AssemblyPath { get; init; }
    public string? CallbackType { get; init; }
    public string? CallbackMethod { get; init; }
    public int? IlOffset { get; init; }
}

public static class PcCompatStaticPatchScanner
{
    public const int DefaultTargetGameRevision = 143;
    private const string JAPatchAttributeName = "JALib.Core.Patch.JAPatchAttribute";

    public static PcCompatStaticPatchScanReport Scan(
        PcModManifest manifest,
        int targetGameRevision = DefaultTargetGameRevision)
    {
        var assemblies = ResolveAssemblyPaths(manifest).ToArray();
        var patches = new List<PcCompatPatchDescriptor>();
        var renderComponents = new List<PcCompatManagedRenderComponentDescriptor>();
        var issues = new List<PcCompatStaticPatchScanIssue>();
        var scanned = new List<string>();

        if (assemblies.Length == 0)
        {
            issues.Add(new PcCompatStaticPatchScanIssue
            {
                Code = "AssemblyNotFound",
                Message = "No PC MOD assembly was found from manifest metadata."
            });
        }

        foreach (var assemblyPath in assemblies)
        {
            ScanAssembly(assemblyPath, manifest.Id, patches, renderComponents, issues);
            scanned.Add(assemblyPath);
        }

        return BuildReport(
            manifest.Id,
            targetGameRevision,
            scanned,
            patches,
            renderComponents,
            issues);
    }

    private static PcCompatStaticPatchScanReport BuildReport(
        string modId,
        int targetGameRevision,
        IReadOnlyList<string> scanned,
        IReadOnlyList<PcCompatPatchDescriptor> patches,
        IReadOnlyList<PcCompatManagedRenderComponentDescriptor> renderComponents,
        List<PcCompatStaticPatchScanIssue> issues)
    {
        var normalizedPatches = PcCompatKnownModPatchActivationPolicy.Apply(modId, patches, issues);
        return new PcCompatStaticPatchScanReport
        {
            ModId = modId,
            TargetGameRevision = targetGameRevision,
            AssembliesScanned = scanned,
            Patches = normalizedPatches
                .OrderBy(patch => patch.CallbackType, StringComparer.Ordinal)
                .ThenBy(patch => patch.CallbackMethod, StringComparer.Ordinal)
                .ThenBy(patch => patch.TargetType, StringComparer.Ordinal)
                .ThenBy(patch => patch.TargetMethod, StringComparer.Ordinal)
                .ThenBy(patch => patch.MinVersion)
                .ToArray(),
            ManagedRenderComponents = renderComponents
                .GroupBy(
                    item => item.ComponentAssembly + "\0" + item.ComponentType,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.ComponentAssembly, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ComponentType, StringComparer.Ordinal)
                .ToArray(),
            Issues = issues.ToArray()
        };
    }

    /// <summary>
    /// Scans an explicit set of assemblies instead of resolving them from a manifest. Useful for
    /// inspecting a single DLL and for tests that need a purpose-built patch class.
    /// </summary>
    public static PcCompatStaticPatchScanReport ScanAssemblies(
        string modId,
        IEnumerable<string> assemblyPaths,
        int targetGameRevision = DefaultTargetGameRevision)
    {
        var patches = new List<PcCompatPatchDescriptor>();
        var renderComponents = new List<PcCompatManagedRenderComponentDescriptor>();
        var issues = new List<PcCompatStaticPatchScanIssue>();
        var scanned = new List<string>();

        foreach (var candidate in assemblyPaths)
        {
            if (!File.Exists(candidate))
            {
                issues.Add(Issue("AssemblyNotFound", "Assembly path does not exist.", candidate));
                continue;
            }

            var assemblyPath = Path.GetFullPath(candidate);
            ScanAssembly(assemblyPath, modId, patches, renderComponents, issues);
            scanned.Add(assemblyPath);
        }

        return BuildReport(
            modId,
            targetGameRevision,
            scanned,
            patches,
            renderComponents,
            issues);
    }

    private static IEnumerable<string> ResolveAssemblyPaths(PcModManifest manifest)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(manifest.JAModAssemblyFullPath) &&
            File.Exists(manifest.JAModAssemblyFullPath))
        {
            var path = Path.GetFullPath(manifest.JAModAssemblyFullPath);
            if (seen.Add(path))
                yield return path;
        }

        if (!string.IsNullOrWhiteSpace(manifest.EntryAssemblyPath) &&
            File.Exists(manifest.EntryAssemblyPath))
        {
            var path = Path.GetFullPath(manifest.EntryAssemblyPath);
            if (seen.Add(path))
                yield return path;
        }

        if (!Directory.Exists(manifest.FolderPath))
            yield break;

        foreach (var candidate in Directory.GetFiles(manifest.FolderPath, "*.dll")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(candidate);
            if (seen.Add(path))
                yield return path;
        }
    }

    private static void ScanAssembly(
        string assemblyPath,
        string modId,
        List<PcCompatPatchDescriptor> patches,
        List<PcCompatManagedRenderComponentDescriptor> renderComponents,
        List<PcCompatStaticPatchScanIssue> issues)
    {
        try
        {
            using var stream = File.Open(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                issues.Add(Issue("AssemblyHasNoMetadata", "File is not a managed metadata assembly.", assemblyPath));
                return;
            }

            var reader = peReader.GetMetadataReader();
            var assemblyName = reader.IsAssembly
                ? reader.GetString(reader.GetAssemblyDefinition().Name)
                : Path.GetFileNameWithoutExtension(assemblyPath);
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                var callbackType = GetTypeDefinitionFullName(reader, typeHandle);
                TryDiscoverManagedRenderComponent(
                    reader,
                    typeHandle,
                    type,
                    modId,
                    assemblyName,
                    callbackType,
                    renderComponents);

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var callbackMethod = reader.GetString(method.Name);
                    var callbackParameterTypeNames = PcCompatMetadataNames.GetMethodParameterTypes(reader, methodHandle);

                    foreach (var attributeHandle in method.GetCustomAttributes())
                    {
                        var attribute = reader.GetCustomAttribute(attributeHandle);
                        var attributeType = GetAttributeTypeFullName(reader, attribute);
                        if (attributeType == JAPatchAttributeName || attributeType.EndsWith(".JAPatchAttribute", StringComparison.Ordinal))
                        {
                            DecodeJAPatchAttribute(
                                reader,
                                attribute,
                                assemblyPath,
                                 modId,
                                 callbackType,
                                 callbackMethod,
                                 callbackParameterTypeNames,
                                 patches,
                                 issues);
                        }
                    }
                }
            }

            // Harmony aggregation is class-scoped rather than attribute-scoped: a patch method's
            // target comes from merging the class-level attributes with its own, so it needs its own
            // pass over the type table.
            PcCompatHarmonyAttributeAggregator.Scan(
                reader,
                assemblyPath,
                modId,
                patches,
                issues);

            PcCompatDynamicAddPatchScanner.Scan(
                reader,
                peReader,
                assemblyPath,
                modId,
                patches,
                issues);
        }
        catch (BadImageFormatException ex)
        {
            issues.Add(Issue("BadManagedImage", ex.Message, assemblyPath));
        }
        catch (Exception ex)
        {
            issues.Add(Issue("MetadataReadFailed", $"{ex.GetType().Name}: {ex.Message}", assemblyPath));
        }
    }

    private static void TryDiscoverManagedRenderComponent(
        MetadataReader reader,
        TypeDefinitionHandle typeHandle,
        TypeDefinition type,
        string modId,
        string componentAssembly,
        string componentType,
        List<PcCompatManagedRenderComponentDescriptor> renderComponents)
    {
        if (!TryGetTypeIdentity(reader, type.BaseType, componentAssembly, out var baseAssembly, out var baseType))
            return;

        var matchingMethods = 0;
        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (!string.Equals(reader.GetString(method.Name), "OnPopulateMesh", StringComparison.Ordinal) ||
                (method.Attributes & System.Reflection.MethodAttributes.Static) != 0 ||
                PcCompatMetadataNames.GetMethodReturnType(reader, methodHandle) != "System.Void")
            {
                continue;
            }
            var parameters = PcCompatMetadataNames.GetMethodParameterTypes(reader, methodHandle);
            if (parameters.Count == 1 && parameters[0] == "UnityEngine.UI.VertexHelper")
                ++matchingMethods;
        }

        if (PcCompatManagedRenderComponentCatalog.TryDescribeMetadataType(
                modId,
                componentAssembly,
                componentType,
                baseAssembly,
                baseType,
                (type.Attributes & System.Reflection.TypeAttributes.Abstract) != 0,
                type.GetGenericParameters().Count != 0,
                matchingMethods,
                out var descriptor))
        {
            renderComponents.Add(descriptor);
        }
    }

    private static bool TryGetTypeIdentity(
        MetadataReader reader,
        EntityHandle handle,
        string currentAssembly,
        out string assemblyName,
        out string typeName)
    {
        assemblyName = string.Empty;
        typeName = string.Empty;
        if (handle.IsNil)
            return false;
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            assemblyName = currentAssembly;
            typeName = GetTypeDefinitionFullName(reader, (TypeDefinitionHandle)handle);
            return true;
        }
        if (handle.Kind != HandleKind.TypeReference)
            return false;

        var referenceHandle = (TypeReferenceHandle)handle;
        var reference = reader.GetTypeReference(referenceHandle);
        typeName = GetTypeReferenceFullName(reader, referenceHandle);
        var scope = reference.ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
            scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        assemblyName = scope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : currentAssembly;
        return true;
    }

    private static void DecodeJAPatchAttribute(
        MetadataReader reader,
        CustomAttribute attribute,
        string assemblyPath,
        string modId,
        string callbackType,
        string callbackMethod,
        IReadOnlyList<string> callbackParameterTypeNames,
        List<PcCompatPatchDescriptor> patches,
        List<PcCompatStaticPatchScanIssue> issues)
    {
        try
        {
            var value = attribute.DecodeValue(PcCompatAttributeTypeProvider.Instance);
            if (value.FixedArguments.Length < 4)
            {
                issues.Add(Issue(
                    "UnsupportedJAPatchConstructor",
                    $"JAPatch attribute has {value.FixedArguments.Length} fixed arguments; expected at least 4.",
                    assemblyPath,
                    callbackType,
                    callbackMethod));
                return;
            }

            var targetType = NormalizeSerializedType(ReadString(value.FixedArguments[0].Value));
            var targetMethod = ReadString(value.FixedArguments[1].Value);
            var patchKind = ParsePatchKind(value.FixedArguments[2].Value);
            var needInstance = ReadBoolean(value.FixedArguments[3].Value);
            var minVersion = 0;
            var maxVersion = int.MaxValue;
            var tryingCatch = true;
            IReadOnlyList<string> argumentTypeNames = Array.Empty<string>();
            var priority = -1;
            IReadOnlyList<string> before = Array.Empty<string>();
            IReadOnlyList<string> after = Array.Empty<string>();

            foreach (var named in value.NamedArguments)
            {
                switch (named.Name)
                {
                    case "MinVersion":
                        minVersion = ReadInt32(named.Value);
                        break;
                    case "MaxVersion":
                        maxVersion = ReadInt32(named.Value);
                        break;
                    case "TryingCatch":
                        tryingCatch = ReadBoolean(named.Value);
                        break;
                    case "ArgumentTypesType":
                        argumentTypeNames = ReadTypeArray(named.Value);
                        break;
                    case "Priority":
                        priority = ReadInt32(named.Value);
                        break;
                    case "Before":
                        before = ReadStringArray(named.Value);
                        break;
                    case "After":
                        after = ReadStringArray(named.Value);
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetMethod))
            {
                issues.Add(Issue(
                    "UnsupportedJAPatchTarget",
                    "JAPatch target type or method name could not be decoded.",
                    assemblyPath,
                    callbackType,
                    callbackMethod));
                return;
            }

            patches.Add(new PcCompatPatchDescriptor
            {
                ModId = modId,
                TargetType = targetType,
                TargetMethod = targetMethod,
                Kind = patchKind,
                CallbackType = callbackType,
                CallbackMethod = callbackMethod,
                CallbackAssemblyPath = Path.GetFullPath(assemblyPath),
                CallbackParameterTypeNames = callbackParameterTypeNames,
                Priority = priority,
                Before = before,
                After = after,
                NeedInstance = needInstance,
                MinVersion = minVersion,
                MaxVersion = maxVersion,
                TryingCatch = tryingCatch,
                ArgumentTypeNames = argumentTypeNames,
                Source = "static_attribute",
                Status = PcCompatPatchStatus.RegisteredOnly,
                Reason = "statically discovered; callback translation is pending"
            });
        }
        catch (Exception ex)
        {
            issues.Add(Issue(
                "JAPatchDecodeFailed",
                $"{ex.GetType().Name}: {ex.Message}",
                assemblyPath,
                callbackType,
                callbackMethod));
        }
    }

    private static PcCompatStaticPatchScanIssue Issue(
        string code,
        string message,
        string? assemblyPath = null,
        string? callbackType = null,
        string? callbackMethod = null)
        => new()
        {
            Code = code,
            Message = message,
            AssemblyPath = assemblyPath,
            CallbackType = callbackType,
            CallbackMethod = callbackMethod
        };

    private static string GetAttributeTypeFullName(MetadataReader reader, CustomAttribute attribute)
    {
        return attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => GetMemberReferenceParentFullName(
                reader,
                reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
            HandleKind.MethodDefinition => GetTypeDefinitionFullName(
                reader,
                reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
            _ => string.Empty
        };
    }

    private static string GetMemberReferenceParentFullName(MetadataReader reader, EntityHandle parent)
    {
        return parent.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceFullName(reader, (TypeReferenceHandle)parent),
            HandleKind.TypeDefinition => GetTypeDefinitionFullName(reader, (TypeDefinitionHandle)parent),
            HandleKind.TypeSpecification => "<type-specification>",
            _ => string.Empty
        };
    }

    private static string GetTypeDefinitionFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return GetTypeDefinitionFullName(reader, declaring) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
    }

    private static string GetTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
    }

    private static string NormalizeSerializedType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var comma = value.IndexOf(',');
        return (comma >= 0 ? value[..comma] : value).Trim();
    }

    private static string ReadString(object? value)
        => value?.ToString() ?? string.Empty;

    private static int ReadInt32(object? value)
        => value == null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

    private static bool ReadBoolean(object? value)
        => value != null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);

    private static PcCompatPatchKind ParsePatchKind(object? value)
        => PcCompatPatchKinds.FromJALibValue(ReadInt32(value));

    private static IReadOnlyList<string> ReadTypeArray(object? value)
    {
        if (value is not ImmutableArray<CustomAttributeTypedArgument<string>> values)
            return Array.Empty<string>();

        return values
            .Select(item => NormalizeSerializedType(ReadString(item.Value)))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringArray(object? value)
    {
        if (value is not ImmutableArray<CustomAttributeTypedArgument<string>> values)
            return Array.Empty<string>();

        return values
            .Select(item => ReadString(item.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private sealed class PcCompatAttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly PcCompatAttributeTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            => typeCode.ToString();

        public string GetSystemType()
            => "System.Type";

        public string GetSZArrayType(string elementType)
            => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => GetTypeDefinitionFullName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => GetTypeReferenceFullName(reader, handle);

        public string GetTypeFromSerializedName(string name)
            => NormalizeSerializedType(name);

        public PrimitiveTypeCode GetUnderlyingEnumType(string type)
            => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type)
            => type == "System.Type";
    }
}
