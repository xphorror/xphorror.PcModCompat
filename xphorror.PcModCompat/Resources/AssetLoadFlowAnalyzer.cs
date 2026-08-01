using System.Collections.Immutable;
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

public sealed class AssetLoadFlowReport
{
    public IReadOnlyList<AssetLoadFlowBinding> ProvenBindings { get; init; } = Array.Empty<AssetLoadFlowBinding>();
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
        var pathLiterals = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<string>();

        foreach (var path in assemblyPaths
                     .Where(File.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                AnalyzeAssembly(path, bindings, pathLiterals, issues);
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
        HashSet<string> pathLiterals)
    {
        var il = body.GetILContent();
        if (il.Length == 0)
            return;

        var instructions = Decode(il);
        var touchesAssetBundleApi = false;
        foreach (var instruction in instructions)
        {
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                continue;
            var identity = GetMethodIdentity(reader, instruction.Token);
            if (identity.Contains("AssetBundle.LoadAllAssets", StringComparison.Ordinal) ||
                identity.Contains("AssetBundle.LoadAsset", StringComparison.Ordinal) ||
                identity.Contains("AssetBundle.LoadFromFile", StringComparison.Ordinal))
            {
                touchesAssetBundleApi = true;
                break;
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

    private readonly record struct IlOp(OpCode OpCode, int Offset, int Token);

    private static List<IlOp> Decode(ImmutableArray<byte> il)
    {
        var list = new List<IlOp>();
        var i = 0;
        while (i < il.Length)
        {
            var offset = i;
            var b = il[i++];
            if (b == 0xFE)
            {
                if (i >= il.Length)
                    break;
                var second = il[i++];
                // Most FE opcodes used here have either 0 or 4-byte operands.
                if (second is 0x15 or 0x06 or 0x07)
                {
                    if (i + 4 > il.Length) break;
                    var token = ReadI32(il, i);
                    i += 4;
                    list.Add(new IlOp(OpCodes.Nop, offset, token));
                }
                else
                {
                    list.Add(new IlOp(OpCodes.Nop, offset, 0));
                }
                continue;
            }

            var op = SingleByteOpCode(b);
            var size = OperandSize(op);
            var operandToken = 0;
            if (size == 4 && IsTokenOp(op))
            {
                if (i + 4 > il.Length) break;
                operandToken = ReadI32(il, i);
            }
            i += size;
            list.Add(new IlOp(op, offset, operandToken));
        }
        return list;
    }

    private static int ReadI32(ImmutableArray<byte> il, int index)
        => il[index] | (il[index + 1] << 8) | (il[index + 2] << 16) | (il[index + 3] << 24);

    private static bool IsTokenOp(OpCode op)
        => op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Ldstr ||
           op == OpCodes.Stsfld || op == OpCodes.Ldsfld || op == OpCodes.Castclass ||
           op == OpCodes.Isinst || op == OpCodes.Newobj || op == OpCodes.Box ||
           op == OpCodes.Ldtoken || op == OpCodes.Newarr || op == OpCodes.Unbox_Any;

    private static OpCode SingleByteOpCode(byte b)
        => b switch
        {
            0x00 => OpCodes.Nop,
            0x02 => OpCodes.Ldarg_0,
            0x03 => OpCodes.Ldarg_1,
            0x06 => OpCodes.Ldloc_0,
            0x07 => OpCodes.Ldloc_1,
            0x08 => OpCodes.Ldloc_2,
            0x09 => OpCodes.Ldloc_3,
            0x0A => OpCodes.Stloc_0,
            0x0B => OpCodes.Stloc_1,
            0x0C => OpCodes.Stloc_2,
            0x0D => OpCodes.Stloc_3,
            0x11 => OpCodes.Ldloc_S,
            0x12 => OpCodes.Ldloca_S,
            0x13 => OpCodes.Stloc_S,
            0x14 => OpCodes.Ldnull,
            0x25 => OpCodes.Dup,
            0x26 => OpCodes.Pop,
            0x28 => OpCodes.Call,
            0x2A => OpCodes.Ret,
            0x2B => OpCodes.Br_S,
            0x2C => OpCodes.Brfalse_S,
            0x2D => OpCodes.Brtrue_S,
            0x38 => OpCodes.Br,
            0x39 => OpCodes.Brfalse,
            0x3A => OpCodes.Brtrue,
            0x6F => OpCodes.Callvirt,
            0x72 => OpCodes.Ldstr,
            0x73 => OpCodes.Newobj,
            0x74 => OpCodes.Castclass,
            0x75 => OpCodes.Isinst,
            0x7E => OpCodes.Ldsfld,
            0x80 => OpCodes.Stsfld,
            0x8C => OpCodes.Box,
            0x8D => OpCodes.Newarr,
            0xA5 => OpCodes.Unbox_Any,
            0xD0 => OpCodes.Ldtoken,
            0xDD => OpCodes.Leave,
            0xDE => OpCodes.Leave_S,
            _ => OpCodes.Nop
        };

    private static int OperandSize(OpCode op)
    {
        if (op == OpCodes.Br_S || op == OpCodes.Brfalse_S || op == OpCodes.Brtrue_S ||
            op == OpCodes.Ldloc_S || op == OpCodes.Stloc_S || op == OpCodes.Ldloca_S ||
            op == OpCodes.Leave_S)
            return 1;
        if (op == OpCodes.Br || op == OpCodes.Brfalse || op == OpCodes.Brtrue || op == OpCodes.Leave)
            return 4;
        if (IsTokenOp(op))
            return 4;
        return 0;
    }

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
