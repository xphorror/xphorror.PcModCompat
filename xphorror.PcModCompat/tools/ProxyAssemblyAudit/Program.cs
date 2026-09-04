using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Xphorror.PcModCompat.Tools;

var options = CommandLineOptions.Parse(args);
if (options is null)
    return 2;

try
{
    var audit = ProxyAuditor.Audit(options.InputDirectory);
    Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath)!);
    await File.WriteAllTextAsync(
        options.ReportPath,
        JsonSerializer.Serialize(audit, new JsonSerializerOptions
        {
            WriteIndented = options.Pretty,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }),
        new UTF8Encoding(false));

    Console.WriteLine(
        $"Audited {audit.Assemblies.Count} proxy assemblies; types={audit.Assemblies.Sum(item => item.TypeCount)}, " +
        $"genericInitializers={audit.Assemblies.Sum(item => item.GenericInitializerCount)}, " +
        $"issues={audit.Issues.Count} -> {options.ReportPath}");
    foreach (var issue in audit.Issues)
        Console.Error.WriteLine(issue);
    return audit.Issues.Count == 0 ? 0 : 4;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

internal static class ProxyAuditor
{
    private static readonly HashSet<string> ForbiddenAssemblyReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "Il2CppInterop.HarmonySupport",
        "Iced",
        "TerraFX.Interop.Windows"
    };

    private static readonly HashSet<string> ForbiddenMemberReferences = new(StringComparer.Ordinal)
    {
        "GetIl2CppMethod",
        "GetIl2CppMethodByToken",
        "il2cpp_class_get_type"
    };

    public static AuditReport Audit(string inputDirectory)
    {
        var issues = new List<string>();
        var assemblyReports = new List<AssemblyAuditRecord>();
        foreach (var path in Directory.EnumerateFiles(inputDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
            {
                issues.Add($"Proxy is not a managed assembly: {path}");
                continue;
            }

            var reader = pe.GetMetadataReader();
            var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
            var references = reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            foreach (var reference in references.Where(ForbiddenAssemblyReferences.Contains))
                issues.Add($"{assemblyName} references forbidden assembly {reference}.");

            var memberReferenceNames = reader.MemberReferences
                .Select(handle => reader.GetString(reader.GetMemberReference(handle).Name))
                .ToArray();
            foreach (var forbidden in memberReferenceNames.Where(ForbiddenMemberReferences.Contains).Distinct())
                issues.Add($"{assemblyName} uses forbidden method lookup {forbidden}.");

            var typeReferenceNames = reader.TypeReferences
                .Select(handle => reader.GetString(reader.GetTypeReference(handle).Name))
                .ToArray();
            if (typeReferenceNames.Contains("AddressAttribute", StringComparer.Ordinal))
                issues.Add($"{assemblyName} references AddressAttribute.");

            assemblyReports.Add(new AssemblyAuditRecord(
                assemblyName,
                Path.GetFileName(path),
                stream.Length,
                reader.TypeDefinitions.Count - 1,
                reader.MethodDefinitions.Count,
                reader.FieldDefinitions.Count,
                reader.PropertyDefinitions.Count,
                reader.TypeDefinitions.Count(handle =>
                    reader.GetTypeDefinition(handle).GetGenericParameters().Count != 0),
                references,
                memberReferenceNames.Count(name => name == "GetIl2CppClass"),
                memberReferenceNames.Count(name => name == "GetIl2CppField"),
                memberReferenceNames.Count(name => name == "GetIl2CppMethodExact")));

            if (assemblyName == "Assembly-CSharp")
                AuditAssemblyCSharp(reader, issues);
            else if (assemblyName == "Il2Cppmscorlib")
                AuditGeneratedCorlib(reader, pe, issues);
            else if (assemblyName == "UnityEngine.AssetBundleModule")
                AuditAssetBundleSurface(reader, issues);
            else if (assemblyName == "UnityEngine.CoreModule")
            {
                AuditAsyncOperationSurface(reader, issues);
                AuditHudCoreSurface(reader, issues);
            }
            else if (assemblyName == "UnityEngine.UIModule")
                AuditHudCanvasSurface(reader, issues);
            else if (assemblyName == "UnityEngine.AudioModule")
                AuditRequiredType(reader, "UnityEngine", "AudioClip", issues);
            else if (assemblyName == "UnityEngine.UI")
                AuditHudUiSurface(reader, issues);
            else if (assemblyName == "Unity.TextMeshPro")
                AuditHudTextSurface(reader, issues);
            else if (assemblyName == "UnityEngine.TextRenderingModule")
                AuditRequiredType(reader, "UnityEngine", "Font", issues);
            else if (assemblyName == "UnityEngine.TextCoreFontEngineModule")
                AuditTmpFontTextCoreSurface(reader, issues);

            AuditAllGenericProxyStaticConstructors(reader, pe, assemblyName, issues);
            AuditNativePointerProducerGuards(reader, pe, assemblyName, issues);
            AuditManagedBridgeOwnedSurface(reader, assemblyName, issues);
        }

        foreach (var expected in new[]
                 {
                     "Assembly-CSharp", "RDTools", "UnityEngine.CoreModule",
                     "UnityEngine.UIModule", "UnityEngine.AudioModule",
                     "UnityEngine.TextRenderingModule", "UnityEngine.TextCoreFontEngineModule",
                     "UnityEngine.AssetBundleModule",
                     "UnityEngine.IMGUIModule", "UnityEngine.InputLegacyModule",
                     "UnityEngine.UI", "Unity.TextMeshPro", "Il2Cppmscorlib"
                 })
        {
            if (assemblyReports.All(report => !report.AssemblyName.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                issues.Add($"Required proxy assembly is missing: {expected}.");
        }

        return new AuditReport(
            "xphorror.il2cpp-proxy-audit.v1",
            DateTime.UtcNow.ToString("O"),
            Path.GetFullPath(inputDirectory),
            "metadata_only",
            assemblyReports,
            issues);
    }

    private static void AuditGeneratedCorlib(
        MetadataReader reader,
        PEReader pe,
        List<string> issues)
    {
        var objectType = FindType(reader, "Il2CppSystem", "Object");
        if (objectType is null)
        {
            issues.Add("Generated corlib does not contain Il2CppSystem.Object.");
        }
        else
        {
            var pointerConstructor = FindMethods(reader, objectType.Value, ".ctor", 1).SingleOrDefault();
            if (pointerConstructor.IsNil)
            {
                issues.Add("Generated corlib Il2CppSystem.Object(IntPtr) is missing.");
            }
            else
            {
                var definition = reader.GetMethodDefinition(pointerConstructor);
                if (definition.RelativeVirtualAddress == 0)
                {
                    issues.Add("Generated corlib Il2CppSystem.Object(IntPtr) has no method body.");
                }
                else
                {
                    var il = pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes() ?? [];
                    if (il.Length <= 3 || !il.Contains((byte)0x2A))
                        issues.Add("Generated corlib Il2CppSystem.Object(IntPtr) is still a throw-null reference stub.");
                }
            }
        }

        var nullableType = FindType(reader, "Il2CppSystem", "Nullable`1");
        if (nullableType is null || FindMethods(reader, nullableType.Value, ".ctor", 1).Count < 2)
            issues.Add("Generated corlib Il2CppSystem.Nullable<T>(T) is missing.");

        var listType = FindType(reader, "Il2CppSystem.Collections.Generic", "List`1");
        if (listType is null)
        {
            issues.Add("Generated corlib Il2CppSystem.Collections.Generic.List<T> is missing.");
        }
        else
        {
            if (FindMethods(reader, listType.Value, "get_Count", 0).Count != 1)
                issues.Add("Generated corlib List<T>.Count getter is missing or ambiguous.");
            if (FindMethods(reader, listType.Value, "get_Item", 1).Count != 1)
                issues.Add("Generated corlib List<T>.Item getter is missing or ambiguous.");
            if (FindMethods(reader, listType.Value, ".ctor", 1).Count < 2)
                issues.Add("Generated corlib List<T> capacity or IntPtr constructor is missing.");
            if (FindMethods(reader, listType.Value, "Add", 1).Count != 1)
                issues.Add("Generated corlib List<T>.Add is missing or ambiguous.");
        }

        var delegateType = FindType(reader, "Il2CppSystem", "Delegate");
        if (delegateType is null)
        {
            issues.Add("Generated corlib Il2CppSystem.Delegate is missing.");
        }
        else
        {
            foreach (var field in new[]
                     {
                         "method_ptr", "invoke_impl", "m_target",
                         "method", "method_code", "method_info"
                     })
            {
                if (!HasReadWriteProperty(reader, delegateType.Value, field))
                {
                    issues.Add(
                        $"Generated corlib Delegate.{field} accessors are missing or ambiguous.");
                }
            }
        }

        var actionType = FindType(reader, "Il2CppSystem", "Action");
        if (actionType is null)
        {
            issues.Add("Generated corlib Il2CppSystem.Action is missing.");
        }
        else
        {
            // DelegateSupport.ConvertDelegateCore activates delegate proxies through the
            // (Object, IntPtr) constructor on Unity 2021.2+ (MustUseDelegateConstructor).
            var delegateConstructor = FindMethods(reader, actionType.Value, ".ctor", 2).SingleOrDefault();
            if (delegateConstructor.IsNil)
            {
                issues.Add("Generated corlib Action(Object, IntPtr) delegate constructor is missing.");
            }
            else if (reader.GetMethodDefinition(delegateConstructor).RelativeVirtualAddress == 0)
            {
                issues.Add("Generated corlib Action(Object, IntPtr) delegate constructor has no method body.");
            }
        }

        var methodInfoType = FindType(reader, "Il2CppSystem.Reflection", "MethodInfo");
        if (methodInfoType is null)
        {
            issues.Add("Generated corlib Il2CppSystem.Reflection.MethodInfo is missing.");
        }
        else
        {
            AuditRequiredMethodSignature(
                reader,
                methodInfoType.Value,
                "MakeGenericMethod",
                isStatic: false,
                genericParameterCount: 0,
                "Il2CppSystem.Reflection.MethodInfo",
                ["Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1<Il2CppSystem.Type>"],
                issues);
        }

        var typeType = FindType(reader, "Il2CppSystem", "Type");
        if (typeType is null)
        {
            issues.Add("Generated corlib Il2CppSystem.Type is missing.");
        }
        else
        {
            foreach (var property in new[]
                     {
                         "_impl", "TypeHandle", "FullName", "IsByRef", "IsPrimitive"
                     })
            {
                if (!HasReadableProperty(reader, typeType.Value, property))
                    issues.Add($"Generated corlib Type.{property} getter is missing or ambiguous.");
            }

            AuditRequiredMethod(reader, typeType, "internal_from_handle", 1, isStatic: true, issues);
            AuditRequiredMethod(reader, typeType, "GetMethod", 1, isStatic: false, issues);
            AuditRequiredMethod(
                reader,
                typeType,
                "MakeGenericType",
                1,
                isStatic: false,
                issues,
                allowOverloads: true);
        }

        var runtimeTypeHandle = FindType(reader, "Il2CppSystem", "RuntimeTypeHandle");
        if (runtimeTypeHandle is null || !HasField(reader, runtimeTypeHandle.Value, "value"))
            issues.Add("Generated corlib RuntimeTypeHandle.value is missing.");

        AuditRequiredGetter(
            reader,
            FindType(reader, "Il2CppSystem.Reflection", "MemberInfo"),
            "DeclaringType",
            issues);
        AuditRequiredMethod(
            reader,
            FindType(reader, "Il2CppSystem.Reflection", "MethodBase"),
            "GetParameters",
            0,
            isStatic: false,
            issues);
        AuditRequiredGetter(
            reader,
            FindType(reader, "Il2CppSystem.Reflection", "MethodInfo"),
            "ReturnType",
            issues);
        AuditRequiredGetter(
            reader,
            FindType(reader, "Il2CppSystem.Reflection", "ParameterInfo"),
            "ParameterType",
            issues);
    }

    private static bool HasReadWriteProperty(
        MetadataReader reader,
        TypeDefinition type,
        string propertyName)
    {
        var matches = type.GetProperties()
            .Where(handle =>
                reader.GetString(reader.GetPropertyDefinition(handle).Name) == propertyName)
            .ToArray();
        if (matches.Length != 1)
            return false;

        var accessors = reader.GetPropertyDefinition(matches[0]).GetAccessors();
        return !accessors.Getter.IsNil && !accessors.Setter.IsNil;
    }

    private static void AuditGenericProxyStaticConstructor(
        MetadataReader reader,
        PEReader pe,
        TypeDefinition type,
        string identity,
        List<string> issues)
    {
        var constructors = FindMethods(reader, type, ".cctor", 0);
        if (constructors.Count != 1)
        {
            issues.Add($"Generated generic proxy static constructor is missing or ambiguous: {identity}.");
            return;
        }

        var definition = reader.GetMethodDefinition(constructors[0]);
        if (definition.RelativeVirtualAddress == 0)
        {
            issues.Add($"Generated generic proxy static constructor has no body: {identity}.");
            return;
        }

        var calls = ReadMethodOperandNames(reader, pe, definition);
        if (calls.Contains("il2cpp_class_get_type", StringComparer.Ordinal))
        {
            issues.Add(
                $"Generated generic proxy static constructor calls raw il2cpp_class_get_type: {identity}.");
        }

        var isMethodStore = identity.Contains("MethodInfoStoreGeneric_", StringComparison.Ordinal);
        var requiredHelpers = isMethodStore
            ? new[]
            {
                "GetIl2CppTypeForClass",
                "GetNativeClassPointerForGenericArgument",
                "RequireIl2CppObject",
                "RequireIl2CppMethod"
            }
            : new[]
            {
                "RequireIl2CppClass",
                "GetIl2CppTypeForClass",
                "GetNativeClassPointerForGenericArgument"
            };
        foreach (var required in requiredHelpers)
        {
            if (!calls.Contains(required, StringComparer.Ordinal))
            {
                issues.Add(
                    $"Generated generic proxy static constructor lacks guarded helper {required}: {identity}.");
            }
        }

        if (!isMethodStore && calls.Count(name => name == "RequireIl2CppClass") < 2)
        {
            issues.Add(
                $"Generated generic proxy static constructor does not guard the inflated class: {identity}.");
        }
    }

    private static void AuditAllGenericProxyStaticConstructors(
        MetadataReader reader,
        PEReader pe,
        string assemblyName,
        List<string> issues)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (type.GetGenericParameters().Count == 0)
                continue;

            AuditGenericProxyStaticConstructor(
                reader,
                pe,
                type,
                assemblyName + "!" + GetTypeDefinitionFullName(reader, type),
                issues);
        }
    }

    private static void AuditNativePointerProducerGuards(
        MetadataReader reader,
        PEReader pe,
        string assemblyName,
        List<string> issues)
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeIdentity = assemblyName + "!" + GetTypeDefinitionFullName(reader, type);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;

                var calls = ReadMethodOperandNames(reader, pe, method);
                var methodIdentity = typeIdentity + "::" + reader.GetString(method.Name);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_object_new",
                    "RequireIl2CppClass",
                    "RequireIl2CppObject",
                    methodIdentity,
                    issues);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_object_get_virtual_method",
                    "RequireIl2CppMethod",
                    "RequireIl2CppMethod",
                    methodIdentity,
                    issues);
                if (calls.Contains("il2cpp_object_get_virtual_method", StringComparer.Ordinal) &&
                    !calls.Contains("Il2CppObjectBaseToPtrNotNull", StringComparer.Ordinal))
                {
                    issues.Add(
                        $"Generated proxy virtual dispatch accepts a null instance: {methodIdentity}.");
                }
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_object_unbox",
                    "RequireIl2CppObject",
                    "RequireIl2CppPointer",
                    methodIdentity,
                    issues);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_object_get_class",
                    "RequireIl2CppObject",
                    "RequireIl2CppClass",
                    methodIdentity,
                    issues);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_value_box",
                    "RequireIl2CppClass",
                    "RequireIl2CppObject",
                    methodIdentity,
                    issues);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_class_from_type",
                    "RequireIl2CppPointer",
                    "RequireIl2CppClass",
                    methodIdentity,
                    issues);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_class_value_size",
                    "RequireIl2CppClass",
                    null,
                    methodIdentity,
                    issues);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_class_is_valuetype",
                    "RequireIl2CppClass",
                    null,
                    methodIdentity,
                    issues);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_method_get_from_reflection",
                    "RequireIl2CppObject",
                    "RequireIl2CppMethod",
                    methodIdentity,
                    issues);
                AuditPointerProducerGuards(
                    calls,
                    "il2cpp_method_get_object",
                    "RequireIl2CppClass",
                    "RequireIl2CppObject",
                    methodIdentity,
                    issues);
                AuditRequiredCallBeforeProducer(
                    calls,
                    "il2cpp_method_get_object",
                    "RequireIl2CppMethod",
                    methodIdentity,
                    issues);
                AuditRequiredCallBeforeProducer(
                    calls,
                    "il2cpp_runtime_invoke",
                    "RequireIl2CppMethod",
                    methodIdentity,
                    issues);
            }
        }
    }

    private static void AuditPointerProducerGuards(
        IReadOnlyList<string> calls,
        string producer,
        string? requiredBefore,
        string? requiredAfter,
        string methodIdentity,
        List<string> issues)
    {
        for (var index = 0; index < calls.Count; index++)
        {
            if (!calls[index].Equals(producer, StringComparison.Ordinal))
                continue;

            if (requiredBefore is not null &&
                (index == 0 || !calls[index - 1].Equals(requiredBefore, StringComparison.Ordinal)))
            {
                issues.Add(
                    $"Generated proxy method calls {producer} without an adjacent preceding " +
                    $"{requiredBefore}: {methodIdentity}.");
            }

            if (requiredAfter is not null &&
                (index + 1 >= calls.Count ||
                 !calls[index + 1].Equals(requiredAfter, StringComparison.Ordinal)))
            {
                issues.Add(
                    $"Generated proxy method calls {producer} without an adjacent following " +
                    $"{requiredAfter}: {methodIdentity}.");
            }
        }
    }

    private static void AuditRequiredCallBeforeProducer(
        IReadOnlyList<string> calls,
        string producer,
        string requiredCall,
        string methodIdentity,
        List<string> issues)
    {
        for (var index = 0; index < calls.Count; index++)
        {
            if (!calls[index].Equals(producer, StringComparison.Ordinal))
                continue;

            var found = false;
            for (var candidate = index - 1; candidate >= 0; candidate--)
            {
                if (!calls[candidate].Equals(requiredCall, StringComparison.Ordinal))
                    continue;
                found = true;
                break;
            }

            if (!found)
            {
                issues.Add(
                    $"Generated proxy method calls {producer} before {requiredCall}: {methodIdentity}.");
            }
        }
    }

    private static IReadOnlyList<string> ReadMethodOperandNames(
        MetadataReader reader,
        PEReader pe,
        MethodDefinition method)
    {
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
        var result = new List<string>();
        var offset = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            ushort value = first;
            if (first == 0xFE)
            {
                if (offset >= il.Length)
                    throw new InvalidDataException("Truncated two-byte IL opcode.");
                value = (ushort)(0xFE00 | il[offset++]);
            }

            if (!IlOpCodes.TryGetValue(value, out var opCode))
                throw new InvalidDataException($"Unknown IL opcode 0x{value:X4}.");

            var operandOffset = offset;
            var operandSize = GetOperandSize(opCode.OperandType, il, operandOffset);
            if (operandOffset + operandSize > il.Length)
                throw new InvalidDataException($"Truncated IL operand for {opCode.Name}.");

            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(
                    il.AsSpan(operandOffset, sizeof(int)));
                if (ResolveMethodOperandName(reader, MetadataTokens.EntityHandle(token)) is { } name)
                    result.Add(name);
            }

            offset += operandSize;
        }

        return result;
    }

    private static string? ResolveMethodOperandName(MetadataReader reader, EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.MethodDefinition => reader.GetString(
                reader.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
            HandleKind.MemberReference => reader.GetString(
                reader.GetMemberReference((MemberReferenceHandle)handle).Name),
            HandleKind.MethodSpecification => ResolveMethodOperandName(
                reader,
                reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
            _ => null
        };

    private static int GetOperandSize(OperandType operandType, byte[] il, int operandOffset)
        => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
                OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => GetInlineSwitchSize(il, operandOffset),
            _ => throw new InvalidDataException($"Unsupported IL operand type: {operandType}.")
        };

    private static int GetInlineSwitchSize(byte[] il, int operandOffset)
    {
        if (operandOffset + sizeof(int) > il.Length)
            throw new InvalidDataException("Truncated IL switch operand.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(operandOffset, sizeof(int)));
        if (count < 0 || count > (il.Length - operandOffset - sizeof(int)) / sizeof(int))
            throw new InvalidDataException("Invalid IL switch target count.");
        return sizeof(int) + count * sizeof(int);
    }

    private static readonly IReadOnlyDictionary<ushort, OpCode> IlOpCodes =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    private static bool HasReadableProperty(
        MetadataReader reader,
        TypeDefinition type,
        string propertyName)
    {
        var matches = type.GetProperties()
            .Where(handle =>
                reader.GetString(reader.GetPropertyDefinition(handle).Name) == propertyName)
            .ToArray();
        return matches.Length == 1 &&
               !reader.GetPropertyDefinition(matches[0]).GetAccessors().Getter.IsNil;
    }

    private static bool HasField(
        MetadataReader reader,
        TypeDefinition type,
        string fieldName)
        => type.GetFields().Count(handle =>
            reader.GetString(reader.GetFieldDefinition(handle).Name) == fieldName) == 1;

    private static void AuditRequiredGetter(
        MetadataReader reader,
        TypeDefinition? type,
        string propertyName,
        List<string> issues)
    {
        if (type is null)
        {
            issues.Add($"Generated corlib owner for {propertyName} is missing.");
            return;
        }

        if (!HasReadableProperty(reader, type.Value, propertyName))
            issues.Add($"Generated corlib {propertyName} getter is missing or ambiguous.");
    }

    private static TypeDefinition? FindType(MetadataReader reader, string typeNamespace, string typeName)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Namespace) == typeNamespace &&
                reader.GetString(type.Name) == typeName)
            {
                return type;
            }
        }
        return null;
    }

    private static List<MethodDefinitionHandle> FindMethods(
        MetadataReader reader,
        TypeDefinition type,
        string methodName,
        int parameterCount)
        => type.GetMethods()
            .Where(handle =>
            {
                var method = reader.GetMethodDefinition(handle);
                return reader.GetString(method.Name) == methodName &&
                       method.GetParameters().Count(parameterHandle =>
                           reader.GetParameter(parameterHandle).SequenceNumber > 0) == parameterCount;
            })
            .ToList();

    private static void AuditAssetBundleSurface(MetadataReader reader, List<string> issues)
    {
        var assetBundle = AuditRequiredType(reader, "UnityEngine", "AssetBundle", issues);
        var createRequest = AuditRequiredType(
            reader,
            "UnityEngine",
            "AssetBundleCreateRequest",
            issues);
        var assetRequest = AuditRequiredType(
            reader,
            "UnityEngine",
            "AssetBundleRequest",
            issues);
        AuditRequiredMethod(reader, assetBundle, "LoadFromFileAsync", 1, isStatic: true, issues);
        AuditRequiredMethod(reader, assetBundle, "LoadAssetAsync", 2, isStatic: false, issues);
        AuditRequiredMethod(reader, createRequest, "get_assetBundle", 0, isStatic: false, issues);
        AuditRequiredMethod(reader, assetRequest, "get_asset", 0, isStatic: false, issues);
    }

    private static void AuditManagedBridgeOwnedSurface(
        MetadataReader reader,
        string assemblyName,
        List<string> issues)
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = ManagedBridgeOwnedSurface.Normalize(
                GetTypeDefinitionFullName(reader, type));
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var signature = method.DecodeSignature(
                    ProxySignatureTypeProvider.Instance,
                    genericContext: null);
                var entry = string.Join(
                    '|',
                    "M",
                    assemblyName,
                    typeName,
                    method.Attributes.HasFlag(MethodAttributes.Static) ? "static" : "instance",
                    method.GetGenericParameters().Count,
                    ManagedBridgeOwnedSurface.Normalize(signature.ReturnType),
                    reader.GetString(method.Name),
                    string.Join(
                        ';',
                        signature.ParameterTypes.Select(ManagedBridgeOwnedSurface.Normalize)));
                if (ManagedBridgeOwnedSurface.Contains(entry))
                {
                    issues.Add(
                        $"Bridge-owned method leaked into Android native proxy surface: {entry}.");
                }
            }
        }
    }

    private static void AuditAsyncOperationSurface(MetadataReader reader, List<string> issues)
    {
        var asyncOperation = AuditRequiredType(
            reader,
            "UnityEngine",
            "AsyncOperation",
            issues);
        AuditRequiredMethod(reader, asyncOperation, "get_isDone", 0, isStatic: false, issues);
        AuditRequiredType(reader, "UnityEngine", "TextAsset", issues);
    }

    private static void AuditHudCoreSurface(MetadataReader reader, List<string> issues)
    {
        var unityObject = AuditRequiredType(reader, "UnityEngine", "Object", issues);
        var gameObject = AuditRequiredType(reader, "UnityEngine", "GameObject", issues);
        var component = AuditRequiredType(reader, "UnityEngine", "Component", issues);
        var transform = AuditRequiredType(reader, "UnityEngine", "Transform", issues);
        var rectTransform = AuditRequiredType(reader, "UnityEngine", "RectTransform", issues);
        var vector2 = AuditRequiredType(reader, "UnityEngine", "Vector2", issues);
        var vector3 = AuditRequiredType(reader, "UnityEngine", "Vector3", issues);
        var color = AuditRequiredType(reader, "UnityEngine", "Color", issues);
        AuditFloatValueTypeLayout(
            reader,
            vector2,
            "UnityEngine.Vector2",
            issues,
            ("x", 0),
            ("y", 4));
        AuditFloatValueTypeLayout(
            reader,
            vector3,
            "UnityEngine.Vector3",
            issues,
            ("x", 0),
            ("y", 4),
            ("z", 8));
        AuditFloatValueTypeLayout(
            reader,
            color,
            "UnityEngine.Color",
            issues,
            ("r", 0),
            ("g", 4),
            ("b", 8),
            ("a", 12));
        AuditRequiredType(reader, "UnityEngine", "Sprite", issues);
        AuditRequiredType(reader, "UnityEngine", "Texture", issues);
        AuditRequiredType(reader, "UnityEngine", "Texture2D", issues);
        AuditRequiredType(reader, "UnityEngine", "Material", issues);
        AuditRequiredType(reader, "UnityEngine", "Shader", issues);
        AuditRequiredType(reader, "UnityEngine", "TextAsset", issues);

        AuditRequiredMethod(reader, unityObject, "Instantiate", 1, true, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, unityObject, "Instantiate", 1, true, issues, genericParameterCount: 1);
        AuditRequiredMethod(reader, unityObject, "Instantiate", 2, true, issues, genericParameterCount: 1);
        AuditRequiredMethod(reader, unityObject, "FindObjectOfType", 0, true, issues, genericParameterCount: 1);
        AuditRequiredMethod(reader, unityObject, "FindObjectsByType", 1, true, issues, genericParameterCount: 1);
        AuditRequiredMethod(reader, unityObject, "Destroy", 1, true, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, unityObject, "DontDestroyOnLoad", 1, true, issues, genericParameterCount: 0);
        AuditRequiredMethod(
            reader,
            gameObject,
            ".ctor",
            2,
            false,
            issues,
            genericParameterCount: 0,
            allowOverloads: true);
        AuditRequiredMethod(reader, gameObject, "AddComponent", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, gameObject, "AddComponent", 0, false, issues, genericParameterCount: 1);
        AuditRequiredMethod(reader, gameObject, "GetComponent", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, gameObject, "GetComponent", 0, false, issues, genericParameterCount: 1);
        AuditRequiredMethod(reader, gameObject, "get_transform", 0, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, gameObject, "SetActive", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, component, "GetComponent", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, component, "GetComponent", 0, false, issues, genericParameterCount: 1);
        AuditRequiredMethod(reader, transform, "Find", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, transform, "SetParent", 2, false, issues, genericParameterCount: 0);
        foreach (var setter in new[]
                 {
                     "set_anchorMin", "set_anchorMax", "set_pivot",
                     "set_anchoredPosition", "set_sizeDelta"
                 })
        {
            AuditRequiredMethod(reader, rectTransform, setter, 1, false, issues, genericParameterCount: 0);
        }
        AuditRequiredMethod(reader, rectTransform, "get_sizeDelta", 0, false, issues, genericParameterCount: 0);
    }

    private static void AuditFloatValueTypeLayout(
        MetadataReader reader,
        TypeDefinition? type,
        string fullTypeName,
        List<string> issues,
        params (string Name, int Offset)[] expected)
    {
        if (type is null)
            return;

        var layout = type.Value.Attributes & TypeAttributes.LayoutMask;
        if (layout is not TypeAttributes.SequentialLayout and not TypeAttributes.ExplicitLayout)
        {
            issues.Add($"Generated proxy value type has no deterministic layout: {fullTypeName}.");
            return;
        }

        var fields = type.Value.GetFields()
            .Select(handle => reader.GetFieldDefinition(handle))
            .Where(field => !field.Attributes.HasFlag(FieldAttributes.Static))
            .ToArray();
        var actualNames = fields.Select(field => reader.GetString(field.Name)).ToArray();
        if (!actualNames.SequenceEqual(expected.Select(field => field.Name), StringComparer.Ordinal))
        {
            issues.Add(
                $"Generated proxy value type field order mismatch: {fullTypeName}; " +
                $"expected=[{string.Join(",", expected.Select(field => field.Name))}] " +
                $"actual=[{string.Join(",", actualNames)}].");
            return;
        }

        for (var index = 0; index < fields.Length; index++)
        {
            var signature = reader.GetBlobBytes(fields[index].Signature);
            if (!signature.SequenceEqual(new byte[] { 0x06, 0x0C }))
            {
                issues.Add(
                    $"Generated proxy value type field is not System.Single: " +
                    $"{fullTypeName}.{actualNames[index]} signature={Convert.ToHexString(signature)}.");
            }

            var offset = fields[index].GetOffset();
            if (layout == TypeAttributes.ExplicitLayout && offset != expected[index].Offset)
            {
                issues.Add(
                    $"Generated proxy value type field offset mismatch: " +
                    $"{fullTypeName}.{actualNames[index]} expected={expected[index].Offset} actual={offset}.");
            }
            else if (layout == TypeAttributes.SequentialLayout && offset >= 0)
            {
                issues.Add(
                    $"Sequential generated proxy unexpectedly carries an explicit field offset: " +
                    $"{fullTypeName}.{actualNames[index]}={offset}.");
            }
        }
    }

    private static void AuditHudCanvasSurface(MetadataReader reader, List<string> issues)
    {
        var canvas = AuditRequiredType(reader, "UnityEngine", "Canvas", issues);
        AuditRequiredMethod(reader, canvas, "set_renderMode", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, canvas, "set_sortingOrder", 1, false, issues, genericParameterCount: 0);
    }

    private static void AuditHudUiSurface(MetadataReader reader, List<string> issues)
    {
        var scaler = AuditRequiredType(reader, "UnityEngine.UI", "CanvasScaler", issues);
        var fitter = AuditRequiredType(reader, "UnityEngine.UI", "ContentSizeFitter", issues);
        var graphic = AuditRequiredType(reader, "UnityEngine.UI", "Graphic", issues);
        var image = AuditRequiredType(reader, "UnityEngine.UI", "Image", issues);
        AuditRequiredMethod(reader, scaler, "set_uiScaleMode", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, scaler, "set_referenceResolution", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, scaler, "set_matchWidthOrHeight", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, fitter, "set_horizontalFit", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, fitter, "set_verticalFit", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, image, "set_type", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, graphic, "set_color", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, graphic, "set_raycastTarget", 1, false, issues, genericParameterCount: 0);
    }

    private static void AuditHudTextSurface(MetadataReader reader, List<string> issues)
    {
        var fontAsset = AuditRequiredType(reader, "TMPro", "TMP_FontAsset", issues);
        var tmpAsset = AuditRequiredType(reader, "TMPro", "TMP_Asset", issues);
        var character = AuditRequiredType(reader, "TMPro", "TMP_Character", issues);
        var textElement = AuditRequiredType(reader, "TMPro", "TMP_TextElement", issues);
        AuditRequiredType(reader, "TMPro", "TextMeshProUGUI", issues);
        var shaderUtilities = AuditRequiredType(reader, "TMPro", "ShaderUtilities", issues);
        if (shaderUtilities is not null &&
            !HasReadableProperty(reader, shaderUtilities.Value, "ShaderRef_MobileSDF"))
        {
            issues.Add("TMPro.ShaderUtilities.ShaderRef_MobileSDF getter is missing or ambiguous.");
        }
        var text = AuditRequiredType(reader, "TMPro", "TMP_Text", issues);
        AuditRequiredMethod(reader, text, "get_rectTransform", 0, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, text, "get_font", 0, false, issues, genericParameterCount: 0);
        foreach (var setter in new[]
                 {
                     "set_text", "set_fontSize", "set_font", "set_alignment",
                     "set_richText"
                 })
        {
            AuditRequiredMethod(reader, text, setter, 1, false, issues, genericParameterCount: 0);
        }
        AuditRequiredMethod(reader, tmpAsset, "get_faceInfo", 0, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, tmpAsset, "set_faceInfo", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, tmpAsset, "get_material", 0, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, tmpAsset, "set_material", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(reader, tmpAsset, "set_hashCode", 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(
            reader, tmpAsset, "set_materialHashCode", 1, false, issues, genericParameterCount: 0);
        foreach (var setter in new[]
                 {
                     "set_atlasPopulationMode", "set_glyphTable", "set_characterTable",
                     "set_atlasTextures", "set_isMultiAtlasTexturesEnabled", "set_atlasWidth",
                     "set_atlasHeight", "set_atlasPadding", "set_atlasRenderMode"
                 })
            AuditRequiredMethod(reader, fontAsset, setter, 1, false, issues, genericParameterCount: 0);
        AuditRequiredMethod(
            reader, fontAsset, "ReadFontAssetDefinition", 0, false, issues, genericParameterCount: 0);
        if (fontAsset is not null)
        {
            foreach (var parameters in new[]
                     {
                         new[] { "System.String", "System.Boolean" },
                         new[] { "System.String", "System.String&", "System.Boolean" }
                     })
            {
                AuditRequiredMethodSignature(
                    reader,
                    fontAsset.Value,
                    "TryAddCharacters",
                    isStatic: false,
                    genericParameterCount: 0,
                    returnType: "System.Boolean",
                    parameterTypes: parameters,
                    issues);
            }
        }
        foreach (var property in new[]
                 {
                     "m_ElementType", "m_Unicode", "m_TextAsset", "m_Glyph", "m_GlyphIndex",
                     "m_Scale"
                 })
        {
            if (textElement is not null && !HasReadWriteProperty(reader, textElement.Value, property))
                issues.Add($"TMPro.TMP_TextElement.{property} field property is missing.");
        }
        if (character is not null && FindMethods(reader, character.Value, ".ctor", 3).Count != 0)
            issues.Add("TMP_Character parameter constructor must not enter the runtime proxy surface.");
        foreach (var property in new[]
                 {
                     "normalStyle", "normalSpacingOffset", "boldStyle", "boldSpacing",
                     "italicStyle", "tabSize", "m_AtlasTexture", "m_AtlasTextureIndex"
                 })
        {
            if (fontAsset is not null && !HasReadWriteProperty(reader, fontAsset.Value, property))
                issues.Add($"TMPro.TMP_FontAsset.{property} field property is missing.");
        }
    }

    private static void AuditTmpFontTextCoreSurface(MetadataReader reader, List<string> issues)
    {
        var face = AuditRequiredType(reader, "UnityEngine.TextCore", "FaceInfo", issues);
        var metrics = AuditRequiredType(reader, "UnityEngine.TextCore", "GlyphMetrics", issues);
        var rect = AuditRequiredType(reader, "UnityEngine.TextCore", "GlyphRect", issues);
        var glyph = AuditRequiredType(reader, "UnityEngine.TextCore", "Glyph", issues);
        foreach (var property in new[]
                 {
                     "m_FaceIndex", "m_FamilyName", "m_StyleName", "m_PointSize", "m_Scale",
                     "m_UnitsPerEM", "m_LineHeight", "m_AscentLine", "m_CapLine", "m_MeanLine",
                     "m_Baseline", "m_DescentLine", "m_SuperscriptOffset", "m_SuperscriptSize",
                     "m_SubscriptOffset", "m_SubscriptSize", "m_UnderlineOffset",
                     "m_UnderlineThickness", "m_StrikethroughOffset", "m_StrikethroughThickness",
                     "m_TabWidth"
                 })
        {
            if (face is not null && !HasReadWriteProperty(reader, face.Value, property))
                issues.Add($"UnityEngine.TextCore.FaceInfo.{property} field property is missing.");
        }
        foreach (var property in new[]
                 {
                     "m_Width", "m_Height", "m_HorizontalBearingX",
                     "m_HorizontalBearingY", "m_HorizontalAdvance"
                 })
        {
            if (metrics is not null && !HasField(reader, metrics.Value, property))
                issues.Add($"UnityEngine.TextCore.GlyphMetrics.{property} field is missing.");
        }
        foreach (var property in new[] { "m_X", "m_Y", "m_Width", "m_Height" })
        {
            if (rect is not null && !HasField(reader, rect.Value, property))
                issues.Add($"UnityEngine.TextCore.GlyphRect.{property} field is missing.");
        }
        if (face is not null && FindMethods(reader, face.Value, ".ctor", 20).Count != 0)
            issues.Add("FaceInfo parameter constructor must not enter the runtime proxy surface.");
        if (metrics is not null && FindMethods(reader, metrics.Value, ".ctor", 5).Count != 0)
            issues.Add("GlyphMetrics parameter constructor must not enter the runtime proxy surface.");
        if (rect is not null && FindMethods(reader, rect.Value, ".ctor", 4).Count != 0)
            issues.Add("GlyphRect parameter constructor must not enter the runtime proxy surface.");
        foreach (var property in new[]
                 {
                     "m_Index", "m_Metrics", "m_GlyphRect", "m_Scale", "m_AtlasIndex",
                     "m_ClassDefinitionType"
                 })
        {
            if (glyph is not null && !HasReadWriteProperty(reader, glyph.Value, property))
                issues.Add($"UnityEngine.TextCore.Glyph.{property} field property is missing.");
        }
        if (glyph is not null && FindMethods(reader, glyph.Value, ".ctor", 5).Count != 0)
            issues.Add("Glyph parameter constructor must not enter the runtime proxy surface.");
    }

    private static TypeDefinition? AuditRequiredType(
        MetadataReader reader,
        string typeNamespace,
        string typeName,
        List<string> issues)
    {
        var type = FindType(reader, typeNamespace, typeName);
        if (type is null)
            issues.Add($"Required generated proxy type is missing: {typeNamespace}.{typeName}.");
        return type;
    }

    private static void AuditRequiredMethod(
        MetadataReader reader,
        TypeDefinition? type,
        string methodName,
        int parameterCount,
        bool isStatic,
        List<string> issues,
        int? genericParameterCount = null,
        bool allowOverloads = false)
    {
        if (type is null)
            return;
        var candidates = FindMethods(reader, type.Value, methodName, parameterCount)
            .Where(handle =>
            {
                var method = reader.GetMethodDefinition(handle);
                return method.Attributes.HasFlag(MethodAttributes.Static) == isStatic &&
                       (!genericParameterCount.HasValue ||
                        method.GetGenericParameters().Count == genericParameterCount.Value);
            })
            .ToArray();
        if (candidates.Length == 0 || (!allowOverloads && candidates.Length != 1))
        {
            var definition = type.Value;
            var namedCandidates = FindMethods(reader, type.Value, methodName, parameterCount)
                .Select(handle =>
                {
                    var method = reader.GetMethodDefinition(handle);
                    return $"static={method.Attributes.HasFlag(MethodAttributes.Static)}," +
                           $"generic={method.GetGenericParameters().Count}," +
                           $"signature={Convert.ToHexString(reader.GetBlobBytes(method.Signature))}";
                });
            issues.Add(
                $"Required generated proxy method is missing or ambiguous: " +
                $"{reader.GetString(definition.Namespace)}.{reader.GetString(definition.Name)}." +
                $"{methodName}/{parameterCount} static={isStatic} " +
                $"genericArity={genericParameterCount?.ToString() ?? "any"}; " +
                $"candidates=[{string.Join(" | ", namedCandidates)}].");
        }
    }

    private static void AuditRequiredMethodSignature(
        MetadataReader reader,
        TypeDefinition type,
        string methodName,
        bool isStatic,
        int genericParameterCount,
        string returnType,
        IReadOnlyList<string> parameterTypes,
        List<string> issues)
    {
        var candidates = type.GetMethods()
            .Select(handle => (Handle: handle, Definition: reader.GetMethodDefinition(handle)))
            .Where(candidate =>
                reader.GetString(candidate.Definition.Name) == methodName &&
                candidate.Definition.Attributes.HasFlag(MethodAttributes.Static) == isStatic &&
                candidate.Definition.GetGenericParameters().Count == genericParameterCount)
            .Select(candidate =>
            {
                var signature = candidate.Definition.DecodeSignature(
                    ProxySignatureTypeProvider.Instance,
                    genericContext: null);
                return (candidate.Handle, Signature: signature);
            })
            .ToArray();
        var matchCount = candidates.Count(candidate =>
            candidate.Signature.ReturnType == returnType &&
            candidate.Signature.ParameterTypes.SequenceEqual(parameterTypes, StringComparer.Ordinal));
        if (matchCount == 1)
            return;

        var typeName = GetTypeDefinitionFullName(reader, type);
        var available = candidates.Select(candidate =>
            candidate.Signature.ReturnType + " " + methodName + "(" +
            string.Join(",", candidate.Signature.ParameterTypes) + ")");
        issues.Add(
            $"Required generated proxy method signature is missing or ambiguous: " +
            $"{typeName}.{methodName}({string.Join(",", parameterTypes)}) -> {returnType}; " +
            $"matches={matchCount}; candidates=[{string.Join(" | ", available)}].");
    }

    private static string GetTypeDefinitionFullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        if (type.IsNested)
            return GetTypeDefinitionFullName(reader, reader.GetTypeDefinition(type.GetDeclaringType())) + "+" + name;
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string GetTypeReferenceFullName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return GetTypeReferenceFullName(
                       reader,
                       (TypeReferenceHandle)type.ResolutionScope) + "/" + name;
        }

        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private sealed class ProxySignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public static readonly ProxySignatureTypeProvider Instance = new();

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            => typeCode switch
            {
                PrimitiveTypeCode.Boolean => "System.Boolean",
                PrimitiveTypeCode.Byte => "System.Byte",
                PrimitiveTypeCode.Char => "System.Char",
                PrimitiveTypeCode.Double => "System.Double",
                PrimitiveTypeCode.Int16 => "System.Int16",
                PrimitiveTypeCode.Int32 => "System.Int32",
                PrimitiveTypeCode.Int64 => "System.Int64",
                PrimitiveTypeCode.IntPtr => "System.IntPtr",
                PrimitiveTypeCode.Object => "System.Object",
                PrimitiveTypeCode.SByte => "System.SByte",
                PrimitiveTypeCode.Single => "System.Single",
                PrimitiveTypeCode.String => "System.String",
                PrimitiveTypeCode.TypedReference => "System.TypedReference",
                PrimitiveTypeCode.UInt16 => "System.UInt16",
                PrimitiveTypeCode.UInt32 => "System.UInt32",
                PrimitiveTypeCode.UInt64 => "System.UInt64",
                PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
                PrimitiveTypeCode.Void => "System.Void",
                _ => typeCode.ToString()
            };
        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind)
            => GetTypeDefinitionFullName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind)
            => GetTypeReferenceFullName(reader, handle);

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private static void AuditAssemblyCSharp(MetadataReader reader, List<string> issues)
    {
        AuditRequiredType(reader, string.Empty, "HitMargin", issues);

        var controller = AuditRequiredType(reader, string.Empty, "scrController", issues);
        if (controller is not null)
        {
            var controllerProperties = controller.Value.GetProperties()
                .Select(handle => reader.GetPropertyDefinition(handle))
                .ToDictionary(property => reader.GetString(property.Name), StringComparer.Ordinal);
            AuditReadableWritableProperty(
                reader,
                controllerProperties,
                "txtLevelNameOriginalPosition",
                "Nullable`1",
                issues);
        }

        TypeDefinition? tracker = null;
        foreach (var handle in reader.TypeDefinitions)
        {
            var candidate = reader.GetTypeDefinition(handle);
            if (reader.GetString(candidate.Namespace).Length == 0 &&
                reader.GetString(candidate.Name) == "scrMarginTracker")
            {
                tracker = candidate;
                break;
            }
        }

        if (tracker is null)
        {
            issues.Add("Assembly-CSharp proxy does not contain scrMarginTracker.");
            return;
        }

        var properties = tracker.Value.GetProperties()
            .Select(handle => reader.GetPropertyDefinition(handle))
            .ToDictionary(property => reader.GetString(property.Name), StringComparer.Ordinal);
        AuditReadableProperty(properties, "hitMarginsCount", false, issues);
        AuditReadableProperty(properties, "percentAcc", true, issues);
        AuditReadableProperty(properties, "percentXAcc", true, issues);

        var rdString = AuditRequiredType(reader, string.Empty, "RDString", issues);
        AuditRequiredMethod(
            reader,
            rdString,
            "SetLocalizedFont",
            1,
            isStatic: true,
            issues,
            genericParameterCount: 0);
    }

    private static void AuditReadableWritableProperty(
        MetadataReader reader,
        IReadOnlyDictionary<string, PropertyDefinition> properties,
        string name,
        string expectedValueTypeSuffix,
        List<string> issues)
    {
        if (!properties.TryGetValue(name, out var property))
        {
            issues.Add($"scrController proxy property is missing: {name}.");
            return;
        }

        var accessors = property.GetAccessors();
        if (accessors.Getter.IsNil || accessors.Setter.IsNil)
        {
            issues.Add($"scrController.{name} must expose both getter and setter.");
            return;
        }

        var getterSignature = reader.GetMethodDefinition(accessors.Getter).DecodeSignature(
            ProxySignatureTypeProvider.Instance,
            genericContext: null);
        var setterSignature = reader.GetMethodDefinition(accessors.Setter).DecodeSignature(
            ProxySignatureTypeProvider.Instance,
            genericContext: null);
        var getterType = getterSignature.ReturnType;
        var setterType = setterSignature.ParameterTypes.Length == 1
            ? setterSignature.ParameterTypes[0]
            : string.Empty;
        if (!getterType.Contains(expectedValueTypeSuffix, StringComparison.Ordinal) ||
            !string.Equals(getterType, setterType, StringComparison.Ordinal))
        {
            issues.Add(
                $"scrController.{name} has unexpected nullable value type: " +
                $"getter={getterType}; setter={setterType}.");
        }
    }

    private static void AuditReadableProperty(
        IReadOnlyDictionary<string, PropertyDefinition> properties,
        string name,
        bool requireReadOnly,
        List<string> issues)
    {
        if (!properties.TryGetValue(name, out var property))
        {
            issues.Add($"scrMarginTracker proxy property is missing: {name}.");
            return;
        }

        var accessors = property.GetAccessors();
        if (accessors.Getter.IsNil)
            issues.Add($"scrMarginTracker.{name} has no getter.");
        if (requireReadOnly && !accessors.Setter.IsNil)
            issues.Add($"scrMarginTracker.{name} unexpectedly exposes a setter.");
    }
}

internal sealed record AuditReport(
    string FormatVersion,
    string GeneratedUtc,
    string InputDirectory,
    string RuntimeAddressPolicy,
    IReadOnlyList<AssemblyAuditRecord> Assemblies,
    IReadOnlyList<string> Issues);

internal sealed record AssemblyAuditRecord(
    string AssemblyName,
    string FileName,
    long Size,
    int TypeCount,
    int MethodCount,
    int FieldCount,
    int PropertyCount,
    int GenericInitializerCount,
    IReadOnlyList<string> AssemblyReferences,
    int ExactClassLookupReferences,
    int ExactFieldLookupReferences,
    int ExactMethodLookupReferences);

internal sealed class CommandLineOptions
{
    public required string InputDirectory { get; init; }
    public required string ReportPath { get; init; }
    public bool Pretty { get; init; }

    public static CommandLineOptions? Parse(string[] args)
    {
        string? input = null;
        string? report = null;
        var pretty = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input" when index + 1 < args.Length: input = args[++index]; break;
                case "--report" when index + 1 < args.Length: report = args[++index]; break;
                case "--pretty": pretty = true; break;
                case "--help" or "-h": PrintUsage(); return null;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[index]}");
                    PrintUsage();
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(report))
        {
            PrintUsage();
            return null;
        }
        input = Path.GetFullPath(input);
        if (!Directory.Exists(input))
            throw new DirectoryNotFoundException($"Proxy input directory is missing: {input}");
        return new CommandLineOptions
        {
            InputDirectory = input,
            ReportPath = Path.GetFullPath(report),
            Pretty = pretty
        };
    }

    private static void PrintUsage()
        => Console.WriteLine("ProxyAssemblyAudit --input <proxy-dir> --report <report.json> [--pretty]");
}
