using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace StArray.ModManager.Runtime;

internal sealed record NativeModMetadataDescriptor
{
    private const int MaxConstantStringHelperDepth = 8;

    internal required string PluginTypeName { get; init; }
    internal required string Id { get; init; }
    internal required string Name { get; init; }
    internal required string Version { get; init; }
    internal required string Author { get; init; }
    internal required string Description { get; init; }
    internal IReadOnlyList<string> Dependencies { get; init; } = [];
    internal ModIsolationCapabilityLevel CapabilityLevel { get; init; } =
        ModIsolationCapabilityLevel.Guarded;

    /// <summary>
    /// Creates a discovery-only descriptor after the concrete native plugin type has already
    /// been proven from metadata, but its identity getters are not statically interpretable.
    /// The values are host metadata only; the plugin is still constructed later inside its
    /// isolated runtime domain. This is intentionally not a permission to execute getters
    /// during scanning.
    /// </summary>
    internal static NativeModMetadataDescriptor CreateDiscoveryFallback(
        string pluginTypeName,
        string id,
        string name,
        string version,
        string author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return new NativeModMetadataDescriptor
        {
            PluginTypeName = pluginTypeName,
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            Version = string.IsNullOrWhiteSpace(version) ? "0.0.0" : version,
            Author = author ?? string.Empty,
            Description = string.Empty,
            Dependencies = Array.Empty<string>()
        };
    }

    /// <summary>
    /// Proves the native MOD entry type without evaluating any plugin code or identity
    /// getter. Obfuscated Android MODs may keep identity metadata opaque; the loader only
    /// needs this fact to choose the native loader before it considers Info.json.
    /// </summary>
    internal static bool TryReadPluginTypeName(
        string assemblyPath,
        out string? pluginTypeName,
        out string? reason)
    {
        pluginTypeName = null;
        reason = null;
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                throw new BadImageFormatException("managed metadata is absent");
            var metadata = peReader.GetMetadataReader();
            var plugin = FindPluginType(metadata);
            if (plugin.IsNil)
                throw new InvalidDataException("no concrete IModPlugin implementation was proven");

            pluginTypeName = TypeName(metadata, plugin);
            return true;
        }
        catch (Exception exception) when (exception is
            BadImageFormatException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException)
        {
            reason = exception.Message;
            return false;
        }
    }

    internal static bool TryRead(
        string assemblyPath,
        out NativeModMetadataDescriptor? descriptor,
        out string? reason)
    {
        descriptor = null;
        reason = null;
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                throw new BadImageFormatException("managed metadata is absent");
            var metadata = peReader.GetMetadataReader();
            var plugin = FindPluginType(metadata);
            if (plugin.IsNil)
                throw new InvalidDataException("no concrete IModPlugin implementation was proven");

            var type = metadata.GetTypeDefinition(plugin);
            var pluginTypeName = TypeName(metadata, plugin);
            var id = ReadStringGetter(metadata, peReader, type, "Id");
            var name = ReadStringGetter(metadata, peReader, type, "Name");
            var author = ReadStringGetter(metadata, peReader, type, "Author");
            var description = ReadStringGetter(metadata, peReader, type, "Description");
            var version = ReadVersion(metadata);
            var dependencies = ReadEmptyDependencies(metadata, peReader, type);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(description))
            {
                throw new InvalidDataException("one or more required plugin identity getters are not constant");
            }

            descriptor = new NativeModMetadataDescriptor
            {
                PluginTypeName = pluginTypeName,
                Id = id,
                Name = name,
                Version = version,
                Author = author,
                Description = description,
                Dependencies = dependencies
            };
            return true;
        }
        catch (Exception exception) when (exception is
            BadImageFormatException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static TypeDefinitionHandle FindPluginType(MetadataReader metadata)
    {
        var declaredType = ReadDeclaredPluginType(metadata);
        if (!string.IsNullOrWhiteSpace(declaredType))
        {
            var declared = metadata.TypeDefinitions.FirstOrDefault(handle =>
                string.Equals(
                    TypeName(metadata, handle),
                    declaredType,
                    StringComparison.Ordinal));
            if (!declared.IsNil && ImplementsModPlugin(metadata, declared))
                return declared;
            throw new InvalidDataException(
                $"ModEntryPointAttribute type is missing or incompatible: {declaredType}");
        }

        TypeDefinitionHandle match = default;
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var attributes = type.Attributes;
            if ((attributes & System.Reflection.TypeAttributes.Interface) != 0 ||
                (attributes & System.Reflection.TypeAttributes.Abstract) != 0)
            {
                continue;
            }

            if (!ImplementsModPlugin(metadata, handle))
                continue;
            if (!match.IsNil)
                throw new InvalidDataException(
                    "multiple IModPlugin implementations require ModEntryPointAttribute");
            match = handle;
        }
        return match;
    }

    private static bool ImplementsModPlugin(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        return type.GetInterfaceImplementations().Any(implementationHandle =>
            IsModPluginInterface(
                metadata,
                metadata.GetInterfaceImplementation(implementationHandle).Interface));
    }

    private static string? ReadDeclaredPluginType(MetadataReader metadata)
    {
        var assembly = metadata.GetAssemblyDefinition();
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (!IsAttributeType(
                    metadata,
                    attribute.Constructor,
                    "StArray.ModManager.Runtime",
                    "ModEntryPointAttribute"))
            {
                continue;
            }
            var reader = metadata.GetBlobReader(attribute.Value);
            if (reader.ReadUInt16() != 1)
                throw new InvalidDataException("ModEntryPointAttribute has an invalid prolog");
            var serialized = reader.ReadSerializedString();
            if (string.IsNullOrWhiteSpace(serialized))
                throw new InvalidDataException("ModEntryPointAttribute has no plugin type");
            var separator = serialized.IndexOf(',');
            return (separator < 0 ? serialized : serialized[..separator]).Trim();
        }
        return null;
    }

    private static bool IsModPluginInterface(
        MetadataReader metadata,
        EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeReference)
            return false;
        var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
        if (!string.Equals(metadata.GetString(reference.Name), "IModPlugin", StringComparison.Ordinal) ||
            !string.Equals(metadata.GetString(reference.Namespace), "StArray.ModManager.Runtime", StringComparison.Ordinal))
        {
            return false;
        }
        return reference.ResolutionScope.Kind == HandleKind.AssemblyReference &&
               string.Equals(
                   metadata.GetString(metadata.GetAssemblyReference(
                       (AssemblyReferenceHandle)reference.ResolutionScope).Name),
                   "StArray.ModManager",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadStringGetter(
        MetadataReader metadata,
        PEReader peReader,
        TypeDefinition type,
        string propertyName)
    {
        var method = FindGetter(metadata, type, propertyName);
        if (method.IsNil)
            throw new InvalidDataException($"getter {propertyName} is missing");
        return ReadConstantStringMethod(
            metadata,
            peReader,
            method,
            propertyName,
            depth: 0,
            activeHelpers: new HashSet<MethodDefinitionHandle>());
    }

    /// <summary>
    /// Reads the small compiler-generated constant getter shape without executing managed
    /// code. A compiler may move the ldstr into a private helper (for example, under
    /// PrivateImplementationDetails); that helper is safe only when the entire call chain is
    /// proven to remain a same-assembly, static, parameterless string computation.
    /// </summary>
    private static string ReadConstantStringMethod(
        MetadataReader metadata,
        PEReader peReader,
        MethodDefinitionHandle method,
        string propertyName,
        int depth,
        HashSet<MethodDefinitionHandle> activeHelpers)
    {
        if (depth > MaxConstantStringHelperDepth)
        {
            throw new InvalidDataException(
                $"getter {propertyName} constant helper chain exceeds " +
                $"{MaxConstantStringHelperDepth} levels");
        }

        if (!activeHelpers.Add(method))
            throw new InvalidDataException(
                $"getter {propertyName} constant helper chain contains a cycle");

        try
        {
            var definition = metadata.GetMethodDefinition(method);
            if (definition.RelativeVirtualAddress == 0)
                throw new InvalidDataException($"getter {propertyName} has no method body");
            var body = peReader.GetMethodBody(definition.RelativeVirtualAddress);
            var il = body.GetILBytes()
                     ?? throw new InvalidDataException($"getter {propertyName} has no IL bytes");

            if (il.Length == 6 && il[0] == 0x72 && il[5] == 0x2a)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(1, 4));
                var userString = MetadataTokens.UserStringHandle(token);
                if (userString.IsNil)
                {
                    throw new InvalidDataException(
                        $"getter {propertyName} has an invalid string token");
                }

                return metadata.GetUserString(userString);
            }

            // call <same-assembly static string Helper()>; ret
            if (il.Length == 6 && il[0] == 0x28 && il[5] == 0x2a)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(1, 4));
                if (!TryResolveSameAssemblyMethod(
                        metadata,
                        token,
                        out var helper) ||
                    !IsParameterlessStaticStringMethod(metadata, helper))
                {
                    throw new InvalidDataException(
                        $"getter {propertyName} calls an unproven constant helper");
                }

                return ReadConstantStringMethod(
                    metadata,
                    peReader,
                    helper,
                    propertyName,
                    depth + 1,
                    activeHelpers);
            }

            throw new InvalidDataException(
                $"getter {propertyName} is not a proven constant string getter");
        }
        finally
        {
            activeHelpers.Remove(method);
        }
    }

    private static bool TryResolveSameAssemblyMethod(
        MetadataReader metadata,
        int token,
        out MethodDefinitionHandle method)
    {
        method = default;
        EntityHandle handle;
        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (handle.Kind == HandleKind.MethodDefinition)
        {
            method = (MethodDefinitionHandle)handle;
            return true;
        }

        // A MemberRef can still refer to a method defined in this assembly. Resolve it only
        // when its parent is a local TypeDef and its signature matches exactly; TypeRef,
        // TypeSpec and MethodSpec are intentionally rejected as not locally proven.
        if (handle.Kind != HandleKind.MemberReference)
            return false;

        var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
        if (member.Parent.Kind != HandleKind.TypeDefinition)
            return false;

        var memberName = metadata.GetString(member.Name);
        var memberSignature = metadata.GetBlobBytes(member.Signature);
        var declaringType = metadata.GetTypeDefinition((TypeDefinitionHandle)member.Parent);
        foreach (var candidate in declaringType.GetMethods())
        {
            var definition = metadata.GetMethodDefinition(candidate);
            if (!string.Equals(metadata.GetString(definition.Name), memberName, StringComparison.Ordinal))
                continue;
            if (!metadata.GetBlobBytes(definition.Signature).AsSpan().SequenceEqual(memberSignature))
                continue;
            method = candidate;
            return true;
        }

        return false;
    }

    private static bool IsParameterlessStaticStringMethod(
        MetadataReader metadata,
        MethodDefinitionHandle method)
    {
        var definition = metadata.GetMethodDefinition(method);
        if ((definition.Attributes & System.Reflection.MethodAttributes.Static) == 0 ||
            definition.GetGenericParameters().Any())
        {
            return false;
        }

        var signature = metadata.GetBlobReader(definition.Signature);
        if (signature.RemainingBytes < 3)
            return false;

        var callingConvention = signature.ReadByte();
        if ((callingConvention & 0x0f) != 0 ||
            (callingConvention & 0x10) != 0 ||
            (callingConvention & 0x20) != 0)
        {
            return false;
        }

        if (signature.ReadCompressedInteger() != 0 || signature.ReadByte() != 0x0e)
            return false;

        return signature.RemainingBytes == 0;
    }

    private static string ReadVersion(MetadataReader metadata)
    {
        var assembly = metadata.GetAssemblyDefinition();
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (!IsAttributeType(
                    metadata,
                    attribute.Constructor,
                    "System.Reflection",
                    "AssemblyInformationalVersionAttribute"))
            {
                continue;
            }
            var reader = metadata.GetBlobReader(attribute.Value);
            if (reader.ReadUInt16() != 1)
                break;
            var version = reader.ReadSerializedString();
            if (string.IsNullOrWhiteSpace(version))
                break;
            var buildMetadata = version.IndexOf('+');
            return buildMetadata < 0 ? version : version[..buildMetadata];
        }
        return assembly.Version.ToString();
    }

    private static IReadOnlyList<string> ReadEmptyDependencies(
        MetadataReader metadata,
        PEReader peReader,
        TypeDefinition type)
    {
        var method = FindGetter(metadata, type, "Dependencies");
        if (method.IsNil)
            throw new InvalidDataException("getter Dependencies is missing");
        var definition = metadata.GetMethodDefinition(method);
        if (definition.RelativeVirtualAddress == 0)
            throw new InvalidDataException("getter Dependencies has no method body");
        var il = peReader.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes()
                 ?? throw new InvalidDataException("getter Dependencies has no IL bytes");
        if (il.Length == 2 && il[0] == 0x14 && il[1] == 0x2a)
            return Array.Empty<string>();
        if (il.Length == 6 && il[0] == 0x28 && il[5] == 0x2a)
        {
            var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(1, 4));
            var calledMethod = MetadataTokens.EntityHandle(token);
            if (calledMethod.Kind == HandleKind.MethodSpecification)
                calledMethod = metadata.GetMethodSpecification(
                    (MethodSpecificationHandle)calledMethod).Method;
            if (calledMethod.Kind == HandleKind.MemberReference)
            {
                var member = metadata.GetMemberReference(
                    (MemberReferenceHandle)calledMethod);
                if (string.Equals(metadata.GetString(member.Name), "Empty", StringComparison.Ordinal) &&
                    member.Parent.Kind == HandleKind.TypeReference)
                {
                    var parent = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                    if (string.Equals(metadata.GetString(parent.Name), "Array", StringComparison.Ordinal) &&
                        string.Equals(metadata.GetString(parent.Namespace), "System", StringComparison.Ordinal))
                    {
                        return Array.Empty<string>();
                    }
                }
            }
        }
        throw new InvalidDataException("Dependencies getter is not a proven empty dependency provider");
    }

    private static MethodDefinitionHandle FindGetter(
        MetadataReader metadata,
        TypeDefinition type,
        string propertyName)
        => type.GetMethods().FirstOrDefault(method =>
        {
            var definition = metadata.GetMethodDefinition(method);
            return string.Equals(
                metadata.GetString(definition.Name),
                "get_" + propertyName,
                StringComparison.Ordinal);
        });

    private static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(type.Name);
        var declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
            return TypeName(metadata, declaringType) + "+" + name;
        var ns = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static bool IsAttributeType(
        MetadataReader metadata,
        EntityHandle constructor,
        string expectedNamespace,
        string expectedName)
    {
        EntityHandle declaringType;
        if (constructor.Kind == HandleKind.MemberReference)
            declaringType = metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent;
        else if (constructor.Kind == HandleKind.MethodDefinition)
            declaringType = metadata.GetMethodDefinition(
                (MethodDefinitionHandle)constructor).GetDeclaringType();
        else
            return false;

        string typeNamespace;
        string typeName;
        if (declaringType.Kind == HandleKind.TypeReference)
        {
            var type = metadata.GetTypeReference((TypeReferenceHandle)declaringType);
            typeNamespace = metadata.GetString(type.Namespace);
            typeName = metadata.GetString(type.Name);
        }
        else if (declaringType.Kind == HandleKind.TypeDefinition)
        {
            var type = metadata.GetTypeDefinition((TypeDefinitionHandle)declaringType);
            typeNamespace = metadata.GetString(type.Namespace);
            typeName = metadata.GetString(type.Name);
        }
        else
        {
            return false;
        }
        return string.Equals(typeNamespace, expectedNamespace, StringComparison.Ordinal) &&
               string.Equals(typeName, expectedName, StringComparison.Ordinal);
    }
}
