using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AsmResolver.DotNet;

namespace Il2CppInterop.Generator;

public class GeneratorOptions
{
    private readonly Dictionary<string, Dictionary<string, TypeSelection>> _typeAllowList =
        new(StringComparer.OrdinalIgnoreCase);

    public List<AssemblyDefinition>? Source { get; set; }
    public string? OutputDir { get; set; }

    public string? UnityBaseLibsDir { get; set; }
    public List<string> AdditionalAssembliesBlacklist { get; } = new();
    public int TypeDeobfuscationCharsPerUniquifier { get; set; } = 2;
    public int TypeDeobfuscationMaxUniquifiers { get; set; } = 10;
    public string? GameAssemblyPath { get; set; }
    public bool Verbose { get; set; }
    public bool NoXrefCache { get; set; }
    public bool RuntimeMetadataOnly { get; set; }
    public bool HasTypeAllowList => _typeAllowList.Count != 0;
    public Regex? ObfuscatedNamesRegex { get; set; }
    public Dictionary<string, string> RenameMap { get; } = new();
    public bool PassthroughNames { get; set; }
    public bool Parallel { get; set; } = true;

    public PrefixMode Il2CppPrefixMode { get; set; } = PrefixMode.OptIn;
    public HashSet<string> NamespacesAndAssembliesToPrefix { get; } =
        new() { "System", "mscorlib", "Microsoft", "Mono", "I18N" };
    public HashSet<string> NamespacesAndAssembliesToNotPrefix { get; } =
        new() { "Assembly-CSharp", "Unity" };

    public List<string> DeobfuscationGenerationAssemblies { get; } = new();
    public string? DeobfuscationNewAssembliesPath { get; set; }

    public void ReadTypeAllowList(string fileName)
    {
        using var reader = new StreamReader(
            fileName,
            new UTF8Encoding(false, true),
            true,
            64 * 1024);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal))
                continue;

            var parts = value.Split('|');
            if (parts.Length == 2)
            {
                GetOrCreateSelection(parts[0], parts[1]).Mode = MemberMode.All;
                continue;
            }

            if (parts.Length < 4)
                throw InvalidAllowListEntry(fileName, lineNumber);

            switch (parts[0])
            {
                case "T" when parts.Length == 4:
                {
                    var selection = GetOrCreateSelection(parts[1], parts[2]);
                    selection.Mode = parts[3] switch
                    {
                        "all" => MemberMode.All,
                        "layout" when selection.Mode != MemberMode.All => MemberMode.Layout,
                        "skeleton" => selection.Mode,
                        _ => throw InvalidAllowListEntry(fileName, lineNumber)
                    };
                    break;
                }
                case "F" when parts.Length == 4:
                    GetOrCreateSelection(parts[1], parts[2]).Fields.Add(parts[3]);
                    break;
                case "P" when parts.Length == 4:
                    GetOrCreateSelection(parts[1], parts[2]).Properties.Add(parts[3]);
                    break;
                case "M" when parts.Length == 8:
                {
                    if (parts[3] is not ("static" or "instance") ||
                        !int.TryParse(parts[4], out var genericArity) || genericArity < 0)
                        throw InvalidAllowListEntry(fileName, lineNumber);
                    var methodIdentity = BuildMethodIdentity(
                        parts[3] == "static",
                        genericArity,
                        parts[5],
                        parts[6],
                        parts[7].Length == 0 ? Array.Empty<string>() : parts[7].Split(';'));
                    GetOrCreateSelection(parts[1], parts[2]).Methods.Add(methodIdentity);
                    break;
                }
                default:
                    throw InvalidAllowListEntry(fileName, lineNumber);
            }
        }

        if (_typeAllowList.Count == 0)
            throw new InvalidDataException($"Type allowlist is empty: {fileName}");
        if (!_typeAllowList.TryGetValue("mscorlib", out var corlib) ||
            !corlib.ContainsKey("System.Object") || !corlib.ContainsKey("System.Attribute"))
            throw new InvalidDataException(
                "Type allowlist must contain System.Object and System.Attribute skeletons from mscorlib.");
    }

    public bool ShouldGenerateAssembly(string? assemblyName)
        => !HasTypeAllowList ||
           (assemblyName is not null && _typeAllowList.ContainsKey(NormalizeAssemblyName(assemblyName)));

    public bool ShouldGenerateType(TypeDefinition type)
    {
        if (!HasTypeAllowList)
            return true;

        var assemblyName = NormalizeAssemblyName(type.DeclaringModule?.Assembly?.Name?.ToString() ?? string.Empty);
        if (!_typeAllowList.TryGetValue(assemblyName, out var types))
            return false;
        return types.ContainsKey("*") || types.ContainsKey(NormalizeTypeName(type.FullName));
    }

    public bool ShouldGenerateField(TypeDefinition type, FieldDefinition field)
    {
        var selection = GetSelection(type);
        return selection is null ||
               selection.Mode == MemberMode.All ||
               (selection.Mode == MemberMode.Layout && !field.IsStatic) ||
               selection.Fields.Contains(field.Name ?? string.Empty);
    }

    public bool ShouldGenerateMethod(TypeDefinition type, MethodDefinition method)
    {
        var selection = GetSelection(type);
        if (selection is null || selection.Mode == MemberMode.All)
            return true;
        if (method.Signature is null)
            return false;

        return selection.Methods.Contains(BuildMethodIdentity(
            method.IsStatic,
            method.GenericParameters.Count,
            method.Signature.ReturnType.FullName,
            method.Name ?? string.Empty,
            method.Signature.ParameterTypes.Select(parameter => parameter.FullName)));
    }

    public bool ShouldGenerateProperty(TypeDefinition type, PropertyDefinition property)
    {
        var selection = GetSelection(type);
        return selection is null ||
               selection.Mode == MemberMode.All ||
               selection.Properties.Contains(property.Name ?? string.Empty);
    }

    private TypeSelection? GetSelection(TypeDefinition type)
    {
        if (!HasTypeAllowList)
            return null;
        var assemblyName = NormalizeAssemblyName(type.DeclaringModule?.Assembly?.Name?.ToString() ?? string.Empty);
        if (!_typeAllowList.TryGetValue(assemblyName, out var types))
            return new TypeSelection();
        if (types.TryGetValue("*", out var wildcard))
            return wildcard;
        return types.TryGetValue(NormalizeTypeName(type.FullName), out var selection)
            ? selection
            : new TypeSelection();
    }

    private TypeSelection GetOrCreateSelection(string assemblyName, string typeName)
    {
        assemblyName = NormalizeAssemblyName(assemblyName);
        typeName = NormalizeTypeName(typeName);
        if (!_typeAllowList.TryGetValue(assemblyName, out var types))
            _typeAllowList[assemblyName] = types = new Dictionary<string, TypeSelection>(StringComparer.Ordinal);
        if (!types.TryGetValue(typeName, out var selection))
            types[typeName] = selection = new TypeSelection();
        return selection;
    }

    private static InvalidDataException InvalidAllowListEntry(string fileName, int lineNumber)
        => new(
            $"Invalid proxy allowlist entry at {fileName}:{lineNumber}; expected " +
            "assembly|type, T|assembly|type|all|layout|skeleton, F/P|assembly|type|name, or " +
            "M|assembly|type|static|instance|genericArity|returnType|name|param1;param2.");

    private static string BuildMethodIdentity(
        bool isStatic,
        int genericArity,
        string returnType,
        string methodName,
        IEnumerable<string> parameterTypes)
        => (isStatic ? "static" : "instance") + "|" + genericArity + "|" +
           NormalizeTypeName(returnType) + "|" + methodName + "|" +
           string.Join(";", parameterTypes.Select(NormalizeTypeName));

    private static string NormalizeAssemblyName(string value)
    {
        var result = value.Trim();
        return result.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }

    private static string NormalizeTypeName(string value)
        => value.Trim().Replace('/', '+');

    private sealed class TypeSelection
    {
        public MemberMode Mode { get; set; }
        public HashSet<string> Fields { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Properties { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Methods { get; } = new(StringComparer.Ordinal);
    }

    private enum MemberMode
    {
        Skeleton,
        Layout,
        All
    }

    /// <summary>
    ///     Reads a rename map from the specified name into the specified instance of options
    /// </summary>
    public void ReadRenameMap(string fileName)
    {
        using var fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
        ReadRenameMap(fileStream, fileName.EndsWith(".gz"));
    }

    /// <summary>
    ///     Reads a rename map from the specified name into the specified instance of options.
    ///     The stream is not closed by this method.
    /// </summary>
    public void ReadRenameMap(Stream fileStream, bool isGzip)
    {
        if (isGzip)
        {
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress, true);
            ReadRenameMap(gzipStream, false);
            return;
        }

        using var reader = new StreamReader(fileStream, Encoding.UTF8, false, 65536, true);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
            var split = line.Split(';');
            if (split.Length < 2) continue;
            RenameMap[split[0]] = split[1];
        }
    }

    public enum PrefixMode
    {
        /// <summary>
        ///     Only specified namespaces and assemblies will be renamed.
        /// </summary>
        OptIn,
        /// <summary>
        ///     Only specified namespaces and assemblies will not be renamed.
        /// </summary>
        OptOut
    }
}
