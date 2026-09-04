using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Xphorror.PcModCompat.Resources;

public sealed class AssetLoadFlowBinding
{
    public required string AssetName { get; init; }
    public required string FieldType { get; init; }
    public required string FieldName { get; init; }
    public required string DeclaringType { get; init; }
    public required string MethodName { get; init; }
    public required string AssemblyPath { get; init; }
    public int IlOffset { get; init; }
    public string ExpectedTypeHint { get; init; } = string.Empty;
}

public enum AssetLoadFlowRequestKind
{
    LoadAssetByName = 0,
    LoadAllAssets = 1
}

public sealed class AssetLoadFlowRequest
{
    public required AssetLoadFlowRequestKind Kind { get; init; }
    public string AssetName { get; init; } = string.Empty;
    public required string ExpectedTypeHint { get; init; }
    public required string DeclaringType { get; init; }
    public required string MethodName { get; init; }
    public required string AssemblyPath { get; init; }
    public int IlOffset { get; init; }
}

public sealed class AssetLoadFlowReport
{
    public IReadOnlyList<AssetLoadFlowBinding> ProvenBindings { get; init; } = Array.Empty<AssetLoadFlowBinding>();
    public IReadOnlyList<AssetLoadFlowRequest> ProvenRequests { get; init; } = Array.Empty<AssetLoadFlowRequest>();
    public IReadOnlyList<string> LoadFromFilePathLiterals { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Static-only recovery of LoadAsset/LoadAllAssets string literals and the
/// subsequent static field stores. Does not execute MOD code.
/// </summary>
public static class AssetLoadFlowAnalyzer
{
    public static AssetLoadFlowReport AnalyzeAssemblies(IEnumerable<string> assemblyPaths)
    {
        var bindings = new List<AssetLoadFlowBinding>();
        var requests = new List<AssetLoadFlowRequest>();
        var pathLiterals = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<string>();

        foreach (var path in assemblyPaths
                     .Where(File.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                AnalyzeAssembly(path, bindings, requests, pathLiterals, issues);
            }
            catch (Exception ex)
            {
                issues.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new AssetLoadFlowReport
        {
            ProvenBindings = bindings
                .GroupBy(binding => binding.AssetName + "\0" + binding.FieldType + "\0" + binding.FieldName, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(binding => binding.AssetName, StringComparer.Ordinal)
                .ThenBy(binding => binding.FieldName, StringComparer.Ordinal)
                .ToArray(),
            ProvenRequests = requests
                .GroupBy(
                    request => request.Kind + "\0" + request.AssetName + "\0" + request.ExpectedTypeHint,
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(request => request.Kind)
                .ThenBy(request => request.AssetName, StringComparer.Ordinal)
                .ThenBy(request => request.ExpectedTypeHint, StringComparer.Ordinal)
                .ToArray(),
            LoadFromFilePathLiterals = pathLiterals.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Issues = issues
        };
    }

    public static AssetLoadFlowReport AnalyzeModFolder(string modFolder)
    {
        var assemblies = Directory.Exists(modFolder)
            ? Directory.GetFiles(modFolder, "*.dll", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        return AnalyzeAssemblies(assemblies);
    }

    private static void AnalyzeAssembly(
        string assemblyPath,
        List<AssetLoadFlowBinding> bindings,
        List<AssetLoadFlowRequest> requests,
        HashSet<string> pathLiterals,
        List<string> issues)
    {
        using var stream = File.Open(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
        {
            issues.Add(Path.GetFileName(assemblyPath) + ": no metadata");
            return;
        }

        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var typeName = GetTypeFullName(reader, typeHandle);
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;

                MethodBodyBlock body;
                try
                {
                    body = pe.GetMethodBody(method.RelativeVirtualAddress);
                }
                catch (Exception ex)
                {
                    issues.Add($"{typeName}.{reader.GetString(method.Name)}: body decode failed: {ex.Message}");
                    continue;
                }

                AnalyzeMethod(
                    reader,
                    assemblyPath,
                    typeName,
                    reader.GetString(method.Name),
                    body,
                    bindings,
                    requests,
                    pathLiterals);
            }
        }
    }

    private static void AnalyzeMethod(
        MetadataReader reader,
        string assemblyPath,
        string declaringType,
        string methodName,
        MethodBodyBlock body,
        List<AssetLoadFlowBinding> bindings,
        List<AssetLoadFlowRequest> requests,
        HashSet<string> pathLiterals)
    {
        var il = body.GetILContent();
        if (il.Length == 0)
            return;

        var instructions = Decode(il);
        var touchesAssetBundleApi = false;
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                continue;
            if (!TryGetAssetBundleCall(reader, instruction.Token, out var call))
                continue;

            touchesAssetBundleApi = true;
            if (call.MethodName.Equals("LoadAsset", StringComparison.Ordinal) &&
                TryResolveStringArgument(reader, instructions, index, call, out var assetName))
            {
                requests.Add(new AssetLoadFlowRequest
                {
                    Kind = AssetLoadFlowRequestKind.LoadAssetByName,
                    AssetName = assetName,
                    ExpectedTypeHint = InferExpectedType(reader, instructions, index, call),
                    DeclaringType = declaringType,
                    MethodName = methodName,
                    AssemblyPath = assemblyPath,
                    IlOffset = instruction.Offset
                });
            }
            else if (call.MethodName.Equals("LoadAllAssets", StringComparison.Ordinal) &&
                     call.GenericTypeArguments.Length == 1)
            {
                requests.Add(new AssetLoadFlowRequest
                {
                    Kind = AssetLoadFlowRequestKind.LoadAllAssets,
                    ExpectedTypeHint = NormalizeExpectedTypeHint(call.GenericTypeArguments[0]),
                    DeclaringType = declaringType,
                    MethodName = methodName,
                    AssemblyPath = assemblyPath,
                    IlOffset = instruction.Offset
                });
            }
        }

        if (!touchesAssetBundleApi)
            return;

        var assetNameLiterals = new List<(string Name, int Offset)>();
        var stores = new List<(string FieldType, string FieldName, int Offset)>();
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode == OpCodes.Ldstr)
            {
                var value = reader.GetUserString(MetadataTokens.UserStringHandle(instruction.Token));
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (value.Contains("bundle", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains('/') ||
                    value.Contains('\\'))
                {
                    pathLiterals.Add(value);
                    continue;
                }
                if (value is "Unity Version: " or "2022")
                    continue;
                if (value.Contains(' ') && !value.Contains("SDF", StringComparison.Ordinal) && value.Length > 40)
                    continue;
                assetNameLiterals.Add((value, instruction.Offset));
                continue;
            }

            if (instruction.OpCode == OpCodes.Stsfld &&
                TryGetField(reader, instruction.Token, out var fieldType, out var fieldName))
            {
                if (fieldName.Equals("_bundle", StringComparison.Ordinal) ||
                    fieldType.Contains("AssetBundle", StringComparison.Ordinal))
                    continue;
                stores.Add((fieldType, fieldName, instruction.Offset));
            }
        }

        if (assetNameLiterals.Count == 0 || stores.Count == 0)
            return;

        // LoadAllAssets + switch(asset.name) rarely keeps ldstr adjacent to stsfld.
        // Recover by matching case-name literals to static fields using name and type.
        var usedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (assetName, offset) in assetNameLiterals
                     .GroupBy(item => item.Name, StringComparer.Ordinal)
                     .Select(group => group.First())
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var expected = MapExpectedTypeHint(string.Empty, assetName);
            var candidates = stores
                .Where(store => !usedFields.Contains(store.FieldName))
                .Select(store => new
                {
                    store.FieldType,
                    store.FieldName,
                    store.Offset,
                    Score = ScorePair(assetName, expected, store.FieldName, store.FieldType)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.FieldName, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
                continue;

            // Require a unique best score so we do not invent ambiguous bindings.
            if (candidates.Length > 1 && candidates[0].Score == candidates[1].Score)
                continue;

            var best = candidates[0];
            usedFields.Add(best.FieldName);
            bindings.Add(new AssetLoadFlowBinding
            {
                AssetName = assetName,
                FieldType = best.FieldType,
                FieldName = best.FieldName,
                DeclaringType = declaringType,
                MethodName = methodName,
                AssemblyPath = assemblyPath,
                IlOffset = offset,
                ExpectedTypeHint = string.IsNullOrWhiteSpace(expected)
                    ? MapExpectedTypeHint(best.FieldType, assetName)
                    : expected
            });
        }
    }

    private static int ScorePair(string assetName, string expectedFromName, string fieldName, string fieldType)
    {
        var score = 0;
        var expected = string.IsNullOrWhiteSpace(expectedFromName)
            ? MapExpectedTypeHint(fieldType, assetName)
            : expectedFromName;

        if (FieldTypeMatchesExpected(fieldType, expected))
            score += 50;
        else if (!string.IsNullOrWhiteSpace(expected) && !FieldTypeMatchesExpected(fieldType, expected))
            return 0;

        if (fieldName.Equals(assetName, StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (fieldName.Contains(assetName, StringComparison.OrdinalIgnoreCase) ||
                 assetName.Contains(fieldName, StringComparison.OrdinalIgnoreCase))
            score += 25;
        else if (assetName.Equals("ProgressBar", StringComparison.OrdinalIgnoreCase) &&
                 fieldName.Contains("Progress", StringComparison.OrdinalIgnoreCase))
            score += 35;
        else if (assetName.Contains("SDF", StringComparison.OrdinalIgnoreCase) &&
                 fieldName.Contains("Font", StringComparison.OrdinalIgnoreCase))
            score += 35;
        else if (assetName.Equals("SideImage", StringComparison.OrdinalIgnoreCase) &&
                 fieldName.Contains("Side", StringComparison.OrdinalIgnoreCase))
            score += 35;
        else if (assetName.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                 fieldName.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (assetName.Equals("KeyBackground", StringComparison.OrdinalIgnoreCase) &&
                 fieldName.Contains("KeyBackground", StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (assetName.Equals("KeyOutline", StringComparison.OrdinalIgnoreCase) &&
                 fieldName.Contains("KeyOutline", StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (assetName.Equals("GhostRain", StringComparison.OrdinalIgnoreCase) &&
                 fieldName.Contains("GhostRain", StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (score < 50)
            return 0; // type match alone without name evidence is not enough

        return score;
    }

    private static bool FieldTypeMatchesExpected(string fieldType, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return true;
        if (expected.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase))
            return fieldType.Contains("TMP_FontAsset", StringComparison.OrdinalIgnoreCase) ||
                   fieldType.Contains("Font", StringComparison.OrdinalIgnoreCase);
        return fieldType.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string MapExpectedTypeHint(string fieldType, string assetName)
    {
        if (fieldType.Contains("TMP_FontAsset", StringComparison.Ordinal))
            return "TMP_FontAsset";
        if (fieldType.Contains("GameObject", StringComparison.Ordinal))
            return "GameObject";
        if (fieldType.Contains("Sprite", StringComparison.Ordinal))
            return "Sprite";
        if (fieldType.Contains("Texture2D", StringComparison.Ordinal))
            return "Texture2D";
        if (fieldType.Contains("Material", StringComparison.Ordinal))
            return "Material";
        if (fieldType.Contains("Font", StringComparison.Ordinal))
            return "Font";
        if (assetName.Contains("ProgressBar", StringComparison.OrdinalIgnoreCase))
            return "GameObject";
        if (assetName.Contains("SDF", StringComparison.OrdinalIgnoreCase))
            return "TMP_FontAsset";
        return fieldType;
    }

    private static bool TryGetField(MetadataReader reader, int token, out string fieldType, out string fieldName)
    {
        fieldType = string.Empty;
        fieldName = string.Empty;
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.FieldDefinition)
            {
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                fieldName = reader.GetString(field.Name);
                fieldType = field.DecodeSignature(new TypeProvider(), genericContext: null);
                return true;
            }

            if (handle.Kind == HandleKind.MemberReference)
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                if (member.GetKind() != MemberReferenceKind.Field)
                    return false;
                fieldName = reader.GetString(member.Name);
                fieldType = member.DecodeFieldSignature(new TypeProvider(), genericContext: null);
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool TryGetAssetBundleCall(
        MetadataReader reader,
        int token,
        out AssetBundleCall call)
    {
        call = default;
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            var genericArguments = ImmutableArray<string>.Empty;
            if (handle.Kind == HandleKind.MethodSpecification)
            {
                var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                genericArguments = specification.DecodeSignature(new TypeProvider(), genericContext: null);
                handle = specification.Method;
            }

            string declaringType;
            string methodName;
            MethodSignature<string> signature;
            if (handle.Kind == HandleKind.MemberReference)
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                if (member.GetKind() != MemberReferenceKind.Method)
                    return false;
                declaringType = GetTypeName(reader, member.Parent);
                methodName = reader.GetString(member.Name);
                signature = member.DecodeMethodSignature(new TypeProvider(), genericContext: null);
            }
            else if (handle.Kind == HandleKind.MethodDefinition)
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                declaringType = GetTypeFullName(reader, method.GetDeclaringType());
                methodName = reader.GetString(method.Name);
                signature = method.DecodeSignature(new TypeProvider(), genericContext: null);
            }
            else
            {
                return false;
            }

            if (!declaringType.Equals("UnityEngine.AssetBundle", StringComparison.Ordinal) ||
                methodName is not ("LoadAsset" or "LoadAllAssets" or "LoadFromFile"))
                return false;

            call = new AssetBundleCall(
                methodName,
                genericArguments,
                signature.ReturnType,
                signature.ParameterTypes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveStringArgument(
        MetadataReader reader,
        IReadOnlyList<IlOp> instructions,
        int callIndex,
        AssetBundleCall call,
        out string value)
    {
        if (callIndex <= 0)
        {
            value = string.Empty;
            return false;
        }

        // LoadAsset(string) and LoadAsset<T>(string): the name is the final stack producer.
        if (call.ParameterTypes.Length == 1)
            return TryResolveStringProducer(reader, instructions, callIndex - 1, depth: 0, out value);

        // LoadAsset(string, Type): accept the canonical ldstr/ldtoken/GetTypeFromHandle form.
        // More complex Type dataflow is deliberately left unproven instead of guessing a nearby literal.
        if (call.ParameterTypes.Length == 2 &&
            call.ParameterTypes[1].Equals("System.Type", StringComparison.Ordinal) &&
            callIndex >= 3 &&
            instructions[callIndex - 1].OpCode is var typeCall &&
            (typeCall == OpCodes.Call || typeCall == OpCodes.Callvirt) &&
            instructions[callIndex - 2].OpCode == OpCodes.Ldtoken)
        {
            return TryResolveStringProducer(reader, instructions, callIndex - 3, depth: 0, out value);
        }
        value = string.Empty;
        return false;
    }

    private static bool TryResolveStringProducer(
        MetadataReader reader,
        IReadOnlyList<IlOp> instructions,
        int index,
        int depth,
        out string value)
    {
        value = string.Empty;
        if (index < 0 || depth > 4)
            return false;
        var instruction = instructions[index];
        if (instruction.OpCode == OpCodes.Ldstr)
        {
            value = reader.GetUserString(MetadataTokens.UserStringHandle(instruction.Token));
            return !string.IsNullOrWhiteSpace(value);
        }
        if (!TryGetLocalIndex(instruction, load: true, out var localIndex))
            return false;

        for (var producer = index - 1; producer >= 0; producer--)
        {
            if (!TryGetLocalIndex(instructions[producer], load: false, out var storedIndex) ||
                storedIndex != localIndex)
                continue;
            return TryResolveStringProducer(reader, instructions, producer - 1, depth + 1, out value);
        }
        return false;
    }

    private static bool TryGetLocalIndex(IlOp instruction, bool load, out int index)
    {
        var op = instruction.OpCode;
        if (load)
        {
            if (op == OpCodes.Ldloc_0) { index = 0; return true; }
            if (op == OpCodes.Ldloc_1) { index = 1; return true; }
            if (op == OpCodes.Ldloc_2) { index = 2; return true; }
            if (op == OpCodes.Ldloc_3) { index = 3; return true; }
            if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S)
            {
                index = instruction.Operand;
                return true;
            }
        }
        else
        {
            if (op == OpCodes.Stloc_0) { index = 0; return true; }
            if (op == OpCodes.Stloc_1) { index = 1; return true; }
            if (op == OpCodes.Stloc_2) { index = 2; return true; }
            if (op == OpCodes.Stloc_3) { index = 3; return true; }
            if (op == OpCodes.Stloc || op == OpCodes.Stloc_S)
            {
                index = instruction.Operand;
                return true;
            }
        }
        index = -1;
        return false;
    }

    private static string InferExpectedType(
        MetadataReader reader,
        IReadOnlyList<IlOp> instructions,
        int callIndex,
        AssetBundleCall call)
    {
        if (call.GenericTypeArguments.Length == 1)
            return NormalizeExpectedTypeHint(call.GenericTypeArguments[0]);

        for (var index = callIndex + 1; index < Math.Min(instructions.Count, callIndex + 4); index++)
        {
            var instruction = instructions[index];
            if (instruction.OpCode == OpCodes.Castclass &&
                TryGetTypeName(reader, instruction.Token, out var castType))
                return NormalizeExpectedTypeHint(castType);
            if ((instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Stsfld) &&
                TryGetField(reader, instruction.Token, out var fieldType, out _))
                return NormalizeExpectedTypeHint(fieldType);
        }

        if (call.ParameterTypes.Any(type => type.Equals("System.Type", StringComparison.Ordinal)))
        {
            for (var index = callIndex - 1; index >= Math.Max(0, callIndex - 10); index--)
            {
                if (instructions[index].OpCode == OpCodes.Ldtoken &&
                    TryGetTypeName(reader, instructions[index].Token, out var tokenType))
                    return NormalizeExpectedTypeHint(tokenType);
            }
        }
        return NormalizeExpectedTypeHint(call.ReturnType);
    }

    private static string NormalizeExpectedTypeHint(string typeName)
    {
        var value = typeName.Trim();
        if (value.StartsWith("UnityEngine.", StringComparison.Ordinal))
            return value["UnityEngine.".Length..];
        if (value.Equals("TMPro.TMP_FontAsset", StringComparison.Ordinal))
            return "TMP_FontAsset";
        return value is "Object" or "System.Object" ? "Object" : value;
    }

    private static bool TryGetTypeName(MetadataReader reader, int token, out string typeName)
    {
        try
        {
            typeName = GetTypeName(reader, MetadataTokens.EntityHandle(token));
            return typeName.Length != 0;
        }
        catch
        {
            typeName = string.Empty;
            return false;
        }
    }

    private static string GetTypeName(MetadataReader reader, EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFullName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => GetTypeReferenceFullName(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(new TypeProvider(), genericContext: null),
            _ => string.Empty
        };

    private static string GetMethodIdentity(MetadataReader reader, int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return GetTypeFullName(reader, method.GetDeclaringType()) + "." + reader.GetString(method.Name);
            }
            if (handle.Kind == HandleKind.MemberReference)
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                var parent = member.Parent;
                var typeName = parent.Kind switch
                {
                    HandleKind.TypeReference => GetTypeReferenceFullName(reader, (TypeReferenceHandle)parent),
                    HandleKind.TypeDefinition => GetTypeFullName(reader, (TypeDefinitionHandle)parent),
                    _ => parent.Kind.ToString()
                };
                return typeName + "." + reader.GetString(member.Name);
            }
        }
        catch
        {
            // ignore
        }
        return string.Empty;
    }

    private static string GetTypeFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        if (type.IsNested)
            return GetTypeFullName(reader, type.GetDeclaringType()) + "+" + name;
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string GetTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private readonly record struct AssetBundleCall(
        string MethodName,
        ImmutableArray<string> GenericTypeArguments,
        string ReturnType,
        ImmutableArray<string> ParameterTypes);

    private readonly record struct IlOp(OpCode OpCode, int Offset, int Operand)
    {
        public int Token => Operand;
    }

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(opCode => opCode.Value)
        .ToDictionary(group => group.Key, group => group.First());

    private static List<IlOp> Decode(ImmutableArray<byte> il)
    {
        var list = new List<IlOp>();
        var i = 0;
        while (i < il.Length)
        {
            var offset = i;
            var first = il[i++];
            var value = first == 0xFE
                ? i < il.Length ? unchecked((short)(0xFE00 | il[i++])) : (short)0
                : (short)first;
            if (!OpCodesByValue.TryGetValue(value, out var op))
                break;

            var size = OperandSize(op, il, i);
            if (size < 0 || i + size > il.Length)
                break;
            var operand = size switch
            {
                1 => il[i],
                2 => il[i] | il[i + 1] << 8,
                >= 4 => ReadI32(il, i),
                _ => 0
            };
            i += size;
            list.Add(new IlOp(op, offset, operand));
        }
        return list;
    }

    private static int ReadI32(ImmutableArray<byte> il, int index)
        => il[index] | (il[index + 1] << 8) | (il[index + 2] << 16) | (il[index + 3] << 24);

    private static int OperandSize(OpCode op, ImmutableArray<byte> il, int index)
        => op.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
                OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch when index + 4 <= il.Length =>
                checked(4 + ReadI32(il, index) * 4),
            _ => -1
        };

    private sealed class TypeProvider : ISignatureTypeProvider<string, object?>
    {
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
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => GetTypeFullName(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => GetTypeReferenceFullName(reader, handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
