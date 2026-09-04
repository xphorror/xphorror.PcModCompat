using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Xphorror.PcModCompat.Tools;

var options = CommandLineOptions.Parse(args);
if (options is null)
    return 2;

try
{
    var scanner = new ProxySurfaceScanner(options);
    var report = scanner.Scan();
    await report.WriteAsync(options);
    Console.WriteLine(
        $"Surface scanned assemblies={report.ModAssemblies.Count} refs={report.ScannedReferenceCount} " +
        $"accepted={report.AcceptedEntries.Count} ignored={report.IgnoredReferences.Count} -> {options.OutputPath}");
    return 0;
}
catch (DecoderFallbackException exception)
{
    Console.Error.WriteLine($"Input is not valid UTF-8: {exception.Message}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

internal sealed class ProxySurfaceScanner
{
    private static readonly HashSet<Code> FieldCodes =
    [
        Code.Ldfld,
        Code.Stfld,
        Code.Ldsfld,
        Code.Stsfld
    ];

    private static readonly HashSet<Code> MethodCodes =
    [
        Code.Call,
        Code.Callvirt,
        Code.Newobj
    ];

    private readonly CommandLineOptions _options;
    private readonly Dictionary<TypeKey, AndroidTypeRecord> _androidTypes;
    private readonly Dictionary<SimpleTypeKey, int> _androidSimpleTypeCounts;
    private readonly HashSet<string> _manualEntries;
    private readonly List<string> _accepted = [];
    private readonly List<IgnoredReferenceRecord> _ignored = [];
    private int _scannedReferenceCount;

    public ProxySurfaceScanner(CommandLineOptions options)
    {
        _options = options;
        _androidTypes = LoadAndroidCatalog(options.AndroidCatalogPath);
        _androidSimpleTypeCounts = _androidTypes.Values
            .GroupBy(type => SimpleTypeKey.Create(type.AssemblyName, type.Name))
            .ToDictionary(group => group.Key, group => group.Count());
        _manualEntries = string.IsNullOrWhiteSpace(options.ManualSurfacePath)
            ? new HashSet<string>(StringComparer.Ordinal)
            : ReadStrictUtf8Lines(options.ManualSurfacePath)
                .Select(ProxySurfaceIdentity.NormalizeEntry)
                .Where(entry => !ProxySurfaceIdentity.IsManagedBridgeOwnedEntry(entry))
                .ToHashSet(StringComparer.Ordinal);
    }

    public ProxySurfaceScanReport Scan()
    {
        var modAssemblies = ResolveModAssemblies(_options.ModPath);
        foreach (var assemblyPath in modAssemblies)
            ScanAssembly(assemblyPath);

        var merged = _manualEntries
            .Concat(_accepted)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(SurfaceSortKey, StringComparer.Ordinal)
            .ThenBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        return new ProxySurfaceScanReport(
            "xphorror.pcmod-proxy-surface-scan.v1",
            DateTime.UtcNow.ToString("O"),
            Path.GetFullPath(_options.ModPath),
            _options.ManualSurfacePath is null ? null : Path.GetFullPath(_options.ManualSurfacePath),
            Path.GetFullPath(_options.AndroidCatalogPath),
            modAssemblies.Select(Path.GetFullPath).ToArray(),
            _scannedReferenceCount,
            _accepted.Distinct(StringComparer.Ordinal).OrderBy(entry => entry, StringComparer.Ordinal).ToArray(),
            merged,
            _ignored);
    }

    private void ScanAssembly(string assemblyPath)
    {
        using var module = ModuleDefMD.Load(assemblyPath);
        foreach (var typeReference in module.GetTypeRefs())
        {
            _scannedReferenceCount++;
            TryAcceptTypeReference(assemblyPath, typeReference);
        }

        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods.Where(method => method.HasBody))
        {
            foreach (var reflectedMember in ReflectionSurfaceFlowScanner.Scan(method))
            {
                _scannedReferenceCount++;
                TryAcceptReflectedMember(method, reflectedMember);
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (FieldCodes.Contains(instruction.OpCode.Code) && instruction.Operand is IField field)
                {
                    _scannedReferenceCount++;
                    TryAcceptField(method, instruction, field);
                    continue;
                }

                if (MethodCodes.Contains(instruction.OpCode.Code) && instruction.Operand is IMethod target)
                {
                    _scannedReferenceCount++;
                    TryAcceptMethod(method, instruction, target);
                }
            }
        }
    }

    private void TryAcceptTypeReference(string assemblyPath, TypeRef typeReference)
    {
        var assemblyName = typeReference.DefinitionAssembly?.Name?.String;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            IgnoreMetadata(assemblyPath, typeReference.FullName, "declaring assembly missing");
            return;
        }
        if (!IsProxyCandidateAssembly(assemblyName))
        {
            IgnoreMetadata(assemblyPath, typeReference.FullName, "managed runtime assembly");
            return;
        }

        var typeName = ProxySurfaceIdentity.NormalizeTypeName(typeReference.FullName);
        if (!HasAndroidType(
                assemblyName,
                typeName,
                typeReference.DeclaringType is null ? null : typeReference.Name.String))
        {
            IgnoreMetadata(assemblyPath, typeReference.FullName, "type absent from Android catalog");
            return;
        }

        _accepted.Add(string.Join(
            "|",
            "T",
            NormalizeAssemblyName(assemblyName),
            typeName));
    }

    private void TryAcceptField(MethodDef owner, Instruction instruction, IField field)
    {
        var assemblyName = field.DeclaringType.DefinitionAssembly?.Name?.String;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            Ignore(owner, instruction, "field", field.FullName, "declaring assembly missing");
            return;
        }
        if (!IsProxyCandidateAssembly(assemblyName))
        {
            Ignore(owner, instruction, "field", field.FullName, "managed runtime assembly");
            return;
        }

        var typeName = SurfaceDeclaringTypeName(field.DeclaringType);
        if (!HasAndroidType(
                assemblyName,
                typeName,
                field.DeclaringType.DeclaringType is null ? null : field.DeclaringType.Name.String))
        {
            Ignore(owner, instruction, "field", field.FullName, "declaring type absent from Android catalog");
            return;
        }

        _accepted.Add(string.Join(
            "|",
            "F",
            NormalizeAssemblyName(assemblyName),
            ProxySurfaceIdentity.NormalizeTypeName(typeName),
            field.Name.String));
    }

    private void TryAcceptMethod(MethodDef owner, Instruction instruction, IMethod target)
    {
        var declaringType = target.DeclaringType;
        var assemblyName = declaringType.DefinitionAssembly?.Name?.String;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            Ignore(owner, instruction, "method", target.FullName, "declaring assembly missing");
            return;
        }
        if (!IsProxyCandidateAssembly(assemblyName))
        {
            Ignore(owner, instruction, "method", target.FullName, "managed runtime assembly");
            return;
        }

        var typeName = SurfaceDeclaringTypeName(declaringType);
        if (!HasAndroidType(
                assemblyName,
                typeName,
                declaringType.DeclaringType is null ? null : declaringType.Name.String))
        {
            Ignore(owner, instruction, "method", target.FullName, "declaring type absent from Android catalog");
            return;
        }

        var signature = target.MethodSig;
        if (signature is null)
        {
            Ignore(owner, instruction, "method", target.FullName, "method signature missing");
            return;
        }

        var genericArity = target is MethodSpec methodSpec
            ? methodSpec.GenericInstMethodSig?.GenericArguments.Count ?? 0
            : (int)signature.GenParamCount;
        var entry = string.Join(
            "|",
            "M",
            NormalizeAssemblyName(assemblyName),
            ProxySurfaceIdentity.NormalizeTypeName(typeName),
            signature.HasThis ? "instance" : "static",
            genericArity.ToString(),
            ProxySurfaceIdentity.NormalizeTypeName(TypeIdentity(signature.RetType)),
            target.Name.String,
            string.Join(";", signature.Params.Select(type =>
                ProxySurfaceIdentity.NormalizeTypeName(TypeIdentity(type)))));
        if (ProxySurfaceIdentity.IsManagedBridgeOwnedEntry(entry))
        {
            Ignore(owner, instruction, "method", target.FullName, "managed call bridge owns method");
            return;
        }
        _accepted.Add(entry);
    }

    private void TryAcceptReflectedMember(MethodDef owner, ReflectedMemberReference reference)
    {
        var assemblyName = reference.DeclaringType.DefinitionAssembly?.Name?.String;
        var target = $"{reference.DeclaringType.FullName}::{reference.MemberName}";
        var kind = "reflected-" + reference.Kind.ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            Ignore(owner, reference.Instruction, kind, target, "declaring assembly missing");
            return;
        }
        if (!IsProxyCandidateAssembly(assemblyName))
        {
            Ignore(owner, reference.Instruction, kind, target, "managed runtime assembly");
            return;
        }

        var typeName = SurfaceDeclaringTypeName(reference.DeclaringType);
        if (!HasAndroidType(
                assemblyName,
                typeName,
                reference.DeclaringType.DeclaringType is null
                    ? null
                    : reference.DeclaringType.Name.String))
        {
            Ignore(owner, reference.Instruction, kind, target, "declaring type absent from Android catalog");
            return;
        }

        var prefix = reference.Kind switch
        {
            ReflectedMemberKind.Field => "RF",
            ReflectedMemberKind.Property => "RP",
            ReflectedMemberKind.Method => "RN",
            _ => throw new ArgumentOutOfRangeException(nameof(reference.Kind), reference.Kind, null)
        };
        _accepted.Add(string.Join(
            "|",
            prefix,
            NormalizeAssemblyName(assemblyName),
            ProxySurfaceIdentity.NormalizeTypeName(typeName),
            reference.MemberName));
    }

    private bool HasAndroidType(
        string assemblyName,
        string fullName,
        string? nestedSimpleName = null)
    {
        if (_androidTypes.ContainsKey(TypeKey.Create(assemblyName, fullName)))
            return true;
        return nestedSimpleName is not null &&
               _androidSimpleTypeCounts.GetValueOrDefault(
                   SimpleTypeKey.Create(assemblyName, nestedSimpleName)) == 1;
    }

    private static bool IsProxyCandidateAssembly(string assemblyName)
    {
        var normalized = NormalizeAssemblyName(assemblyName);
        return normalized is "Assembly-CSharp" or "RDTools" or "Unity.TextMeshPro" ||
               normalized.StartsWith("UnityEngine.", StringComparison.Ordinal);
    }

    private void Ignore(MethodDef method, Instruction instruction, string kind, string target, string reason)
    {
        if (!_options.IncludeIgnored)
            return;

        _ignored.Add(new IgnoredReferenceRecord(
            method.FullName,
            instruction.Offset,
            kind,
            target,
            reason));
    }

    private void IgnoreMetadata(string assemblyPath, string target, string reason)
    {
        if (!_options.IncludeIgnored)
            return;

        _ignored.Add(new IgnoredReferenceRecord(
            $"<metadata:{Path.GetFileName(assemblyPath)}>",
            0,
            "type",
            target,
            reason));
    }

    private static Dictionary<TypeKey, AndroidTypeRecord> LoadAndroidCatalog(string path)
    {
        using var stream = File.OpenRead(path);
        var catalog = JsonSerializer.Deserialize<AndroidTypeCatalog>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException($"Invalid Android catalog: {path}");

        return catalog.Types
            .GroupBy(type => TypeKey.Create(type.AssemblyName, type.FullName))
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static string[] ResolveModAssemblies(string path)
    {
        if (File.Exists(path))
            return [Path.GetFullPath(path)];
        if (!Directory.Exists(path))
            throw new FileNotFoundException("MOD path is neither a DLL nor a directory.", path);

        return Directory.EnumerateFiles(path, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(IsManagedAssembly)
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var module = ModuleDefMD.Load(path);
            return module.Assembly is not null;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ReadStrictUtf8Lines(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), true, 64 * 1024);
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length != 0 && !value.StartsWith("#", StringComparison.Ordinal))
                yield return value;
        }
    }

    private static string TypeIdentity(TypeSig type)
    {
        type = type.RemovePinnedAndModifiers();
        return type switch
        {
            GenericMVar methodVariable => "!!" + methodVariable.Number,
            GenericVar typeVariable => "!" + typeVariable.Number,
            SZArraySig array => TypeIdentity(array.Next) + "[]",
            ByRefSig byRef => TypeIdentity(byRef.Next) + "&",
            PtrSig pointer => TypeIdentity(pointer.Next) + "*",
            GenericInstSig generic =>
                generic.GenericType.TypeDefOrRef.FullName + "<" +
                string.Join(",", generic.GenericArguments.Select(TypeIdentity)) + ">",
            _ => type.FullName
        };
    }

    private static string SurfaceDeclaringTypeName(ITypeDefOrRef declaringType)
        => declaringType is TypeSpec { TypeSig: GenericInstSig genericInstance }
            ? genericInstance.GenericType.TypeDefOrRef.FullName
            : declaringType.FullName;

    private static string SurfaceSortKey(string entry)
        => entry.StartsWith("T|", StringComparison.Ordinal) ? "0|" + entry :
           entry.StartsWith("F|", StringComparison.Ordinal) ? "1|" + entry :
           entry.StartsWith("RF|", StringComparison.Ordinal) ? "2|" + entry :
           entry.StartsWith("G|", StringComparison.Ordinal) ? "3|" + entry :
           entry.StartsWith("P|", StringComparison.Ordinal) ? "4|" + entry :
           entry.StartsWith("RP|", StringComparison.Ordinal) ? "5|" + entry :
           entry.StartsWith("N|", StringComparison.Ordinal) ? "6|" + entry :
           entry.StartsWith("RN|", StringComparison.Ordinal) ? "7|" + entry :
           entry.StartsWith("M|", StringComparison.Ordinal) ? "8|" + entry :
           "9|" + entry;

    private static string NormalizeAssemblyName(string value)
    {
        var result = value.Trim();
        return result.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }

}

internal static class ProxySurfaceIdentity
{
    // Surface manifests use '/' for nested types. dnlib may expose either '/' or '+'.
    public static string NormalizeTypeName(string value) => value.Trim().Replace('+', '/');

    public static string NormalizeEntry(string value) => ManagedBridgeOwnedSurface.Normalize(value);

    public static bool IsManagedBridgeOwnedEntry(string value)
        => ManagedBridgeOwnedSurface.Contains(value);
}

internal sealed record ProxySurfaceScanReport(
    string FormatVersion,
    string GeneratedUtc,
    string ModPath,
    string? ManualSurfacePath,
    string AndroidCatalogPath,
    IReadOnlyList<string> ModAssemblies,
    int ScannedReferenceCount,
    IReadOnlyList<string> AcceptedEntries,
    IReadOnlyList<string> MergedEntries,
    IReadOnlyList<IgnoredReferenceRecord> IgnoredReferences)
{
    public async Task WriteAsync(CommandLineOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        await File.WriteAllTextAsync(
            options.OutputPath,
            "# Generated by xphorror ProxySurfaceScanner. UTF-8. Runtime still verifies Android metadata.\n" +
            string.Join('\n', MergedEntries) + "\n",
            new UTF8Encoding(false));

        if (!string.IsNullOrWhiteSpace(options.ReportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath)!);
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = options.Pretty,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            await File.WriteAllTextAsync(
                options.ReportPath,
                JsonSerializer.Serialize(this, jsonOptions),
                new UTF8Encoding(false));
        }
    }
}

internal sealed record IgnoredReferenceRecord(
    string Method,
    uint IlOffset,
    string Kind,
    string Target,
    string Reason);

internal readonly record struct TypeKey(string AssemblyName, string FullName)
{
    public static TypeKey Create(string assemblyName, string fullName)
        => new(NormalizeAssemblyName(assemblyName), NormalizeTypeName(fullName));

    private static string NormalizeAssemblyName(string value)
    {
        var result = value.Trim();
        return result.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }

    private static string NormalizeTypeName(string value) => value.Trim().Replace('/', '+');
}

internal readonly record struct SimpleTypeKey(string AssemblyName, string Name)
{
    public static SimpleTypeKey Create(string assemblyName, string name)
        => new(NormalizeAssemblyName(assemblyName), name.Trim());

    private static string NormalizeAssemblyName(string value)
    {
        var result = value.Trim();
        return result.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }
}

internal sealed class AndroidTypeCatalog
{
    public string? FormatVersion { get; init; }
    public IReadOnlyList<AndroidTypeRecord> Types { get; init; } = Array.Empty<AndroidTypeRecord>();
}

internal sealed class AndroidTypeRecord
{
    public required string AssemblyName { get; init; }
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string Kind { get; init; }
    public int Line { get; init; }
}

internal sealed class CommandLineOptions
{
    public required string ModPath { get; init; }
    public required string AndroidCatalogPath { get; init; }
    public string? ManualSurfacePath { get; init; }
    public required string OutputPath { get; init; }
    public string? ReportPath { get; init; }
    public bool IncludeIgnored { get; init; }
    public bool Pretty { get; init; }

    public static CommandLineOptions? Parse(string[] args)
    {
        string? mod = null;
        string? catalog = null;
        string? manualSurface = null;
        string? output = null;
        string? report = null;
        var includeIgnored = false;
        var pretty = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--mod" when index + 1 < args.Length:
                    mod = args[++index];
                    break;
                case "--android-catalog" when index + 1 < args.Length:
                    catalog = args[++index];
                    break;
                case "--manual-surface" when index + 1 < args.Length:
                    manualSurface = args[++index];
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--report" when index + 1 < args.Length:
                    report = args[++index];
                    break;
                case "--include-ignored":
                    includeIgnored = true;
                    break;
                case "--pretty":
                    pretty = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[index]}");
                    PrintUsage();
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(mod) ||
            string.IsNullOrWhiteSpace(catalog) ||
            string.IsNullOrWhiteSpace(output))
        {
            PrintUsage();
            return null;
        }

        var result = new CommandLineOptions
        {
            ModPath = Path.GetFullPath(mod),
            AndroidCatalogPath = Path.GetFullPath(catalog),
            ManualSurfacePath = string.IsNullOrWhiteSpace(manualSurface) ? null : Path.GetFullPath(manualSurface),
            OutputPath = Path.GetFullPath(output),
            ReportPath = string.IsNullOrWhiteSpace(report) ? null : Path.GetFullPath(report),
            IncludeIgnored = includeIgnored,
            Pretty = pretty
        };

        if (!File.Exists(result.AndroidCatalogPath))
            throw new FileNotFoundException("Android catalog is missing.", result.AndroidCatalogPath);
        if (result.ManualSurfacePath is not null && !File.Exists(result.ManualSurfacePath))
            throw new FileNotFoundException("Manual surface file is missing.", result.ManualSurfacePath);
        if (!File.Exists(result.ModPath) && !Directory.Exists(result.ModPath))
            throw new FileNotFoundException("MOD path is missing.", result.ModPath);
        return result;
    }

    private static void PrintUsage() => Console.WriteLine(
        "ProxySurfaceScanner --mod <mod.dll|mod-dir> --android-catalog <catalog.json> " +
        "--output <surface.txt> [--manual-surface <members.txt>] [--report <report.json>] " +
        "[--include-ignored] [--pretty]");
}
