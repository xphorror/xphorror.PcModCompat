using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using StArray.ModManager.Runtime;

const string formatVersion = "starray-cil-semantic-pack-v1";
var options = Options.Parse(args);
if (options == null)
    return 2;

var stagingRoot = Path.Combine(
    Path.GetDirectoryName(options.OutputPath)!,
    ".semantic-pack-staging-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stagingRoot);
try
{
    var methodStreamPath = Path.Combine(stagingRoot, "methods.jsonl");
    var sourceIdentity = SourceTreeIdentity.Read(options.SourceRoot);
    var assemblies = SemanticPackBuilder.WriteMethodStream(
        options.AssemblyPaths,
        methodStreamPath);
    var methodStreamSha256 = HashFile(methodStreamPath);
    var manifest = new SemanticPackManifest
    {
        FormatVersion = formatVersion,
        GameVersion = options.GameVersion,
        MethodStreamSha256 = methodStreamSha256,
        SourceTreeSha256 = sourceIdentity.Sha256,
        SourceFileCount = sourceIdentity.FileCount,
        Assemblies = assemblies
    };

    var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
        manifest,
        JsonOptions.Indented);
    var stagingPack = Path.Combine(stagingRoot, "semantic-pack.zip");
    SemanticPackBuilder.WriteArchive(
        stagingPack,
        manifestBytes,
        methodStreamPath);
    var packSha256 = HashFile(stagingPack);
    Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
    File.Move(stagingPack, options.OutputPath, overwrite: true);

    var identity = new ModSemanticPackIdentity
    {
        FormatVersion = formatVersion,
        GameVersion = options.GameVersion,
        PackSha256 = packSha256,
        MethodStreamSha256 = methodStreamSha256,
        SourceTreeSha256 = sourceIdentity.Sha256,
        SourceFileCount = sourceIdentity.FileCount,
        Assemblies = assemblies.Select(assembly => assembly.Identity).ToArray()
    };
    var identityBytes = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions.Indented);
    File.WriteAllBytes(options.IdentityPath, identityBytes);

    Console.WriteLine(
        $"CIL_SEMANTIC_PACK=PASS format={formatVersion} game={options.GameVersion} " +
        $"assemblies={assemblies.Count} methods={assemblies.Sum(item => item.MethodBodyCount)} " +
        $"instructions={assemblies.Sum(item => item.InstructionCount)} " +
        $"sourceFiles={sourceIdentity.FileCount} methodsSha256={methodStreamSha256} " +
        $"packSha256={packSha256} output={options.OutputPath}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    if (Directory.Exists(stagingRoot))
        Directory.Delete(stagingRoot, recursive: true);
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

internal static class SemanticPackBuilder
{
    private static readonly DateTimeOffset ArchiveTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static IReadOnlyList<AssemblySemanticSummary> WriteMethodStream(
        IReadOnlyList<string> assemblyPaths,
        string outputPath)
    {
        var summaries = new List<AssemblySemanticSummary>();
        using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            bufferSize: 128 * 1024,
            leaveOpen: false)
        {
            NewLine = "\n"
        };

        foreach (var path in assemblyPaths.OrderBy(
                     AssemblySimpleName,
                     StringComparer.Ordinal))
        {
            using var module = ModuleDefMD.Load(path);
            var identity = AssemblyIdentityReader.Read(module, path);
            var methodBodies = 0;
            long instructionCount = 0;
            foreach (var type in module.GetTypes().OrderBy(type => type.MDToken.Raw))
            foreach (var method in type.Methods.OrderBy(method => method.MDToken.Raw))
            {
                if (!method.HasBody)
                    continue;
                var record = MethodSemanticRecord.Create(identity.Name, method);
                writer.Write(JsonSerializer.Serialize(record, JsonOptions.Compact));
                writer.Write('\n');
                methodBodies++;
                instructionCount += record.Instructions.Count;
            }

            summaries.Add(new AssemblySemanticSummary
            {
                Identity = identity,
                TypeCount = module.GetTypes().Count(),
                MethodCount = module.GetTypes().Sum(type => type.Methods.Count),
                MethodBodyCount = methodBodies,
                InstructionCount = instructionCount
            });
        }

        writer.Flush();
        return summaries;
    }

    internal static void WriteArchive(
        string outputPath,
        byte[] manifestBytes,
        string methodStreamPath)
    {
        using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(archive, "manifest.json", manifestBytes);

        var methodsEntry = archive.CreateEntry("methods.jsonl", CompressionLevel.SmallestSize);
        methodsEntry.LastWriteTime = ArchiveTimestamp;
        methodsEntry.ExternalAttributes = 0;
        using var destination = methodsEntry.Open();
        using var source = new FileStream(
            methodStreamPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        source.CopyTo(destination, 128 * 1024);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = ArchiveTimestamp;
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string AssemblySimpleName(string path)
    {
        using var module = ModuleDefMD.Load(path);
        return module.Assembly?.Name?.String ?? module.Name.String;
    }
}

internal static class AssemblyIdentityReader
{
    internal static ModAssemblyIdentity Read(ModuleDefMD module, string path)
    {
        var assembly = module.Assembly;
        var publicKeyToken = assembly?.PublicKeyToken?.Data is { Length: > 0 } token
            ? Convert.ToHexString(token).ToLowerInvariant()
            : string.Empty;
        return new ModAssemblyIdentity
        {
            Name = assembly?.Name?.String ?? module.Name.String,
            Version = assembly?.Version?.ToString() ?? "0.0.0.0",
            ModuleVersionId = (module.Mvid ?? Guid.Empty).ToString("D"),
            Sha256 = HashFile(path),
            ApiSurfaceHash = ComputeApiSurfaceHash(module),
            PublicKeyToken = publicKeyToken,
            FileSize = new FileInfo(path).Length
        };
    }

    private static string ComputeApiSurfaceHash(ModuleDefMD module)
    {
        var lines = new List<string>();
        foreach (var type in module.GetTypes().Where(IsExternallyVisible))
        {
            lines.Add(
                $"T|{type.FullName}|attrs=0x{(uint)type.Attributes:X8}|base={TypeIdentity(type.BaseType)}|" +
                $"gp={GenericParameters(type.GenericParameters)}");
            foreach (var implementation in type.Interfaces
                         .Select(item => TypeIdentity(item.Interface))
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                lines.Add($"I|{type.FullName}|{implementation}");
            }
            foreach (var field in type.Fields.Where(IsExternallyVisible))
            {
                lines.Add(
                    $"F|{type.FullName}|{field.Name}|{field.FieldSig?.Type.FullName}|" +
                    $"attrs=0x{(ushort)field.Attributes:X4}|const={field.Constant?.Value}");
            }
            foreach (var method in type.Methods.Where(IsExternallyVisible))
            {
                lines.Add(
                    $"M|{type.FullName}|{method.Name}|{method.MethodSig}|" +
                    $"attrs=0x{(ushort)method.Attributes:X4}|impl=0x{(ushort)method.ImplAttributes:X4}|" +
                    $"gp={GenericParameters(method.GenericParameters)}|params={Parameters(method)}");
            }
            foreach (var property in type.Properties.Where(HasVisibleAccessor))
                lines.Add($"P|{type.FullName}|{property.FullName}|attrs=0x{(ushort)property.Attributes:X4}");
            foreach (var @event in type.Events.Where(HasVisibleAccessor))
                lines.Add($"E|{type.FullName}|{@event.FullName}|attrs=0x{(ushort)@event.Attributes:X4}");
        }

        lines.Sort(StringComparer.Ordinal);
        var canonical = string.Join('\n', lines) + "\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static bool IsExternallyVisible(TypeDef type) =>
        type.IsPublic || type.IsNestedPublic || type.IsNestedFamily ||
        type.IsNestedFamilyOrAssembly;

    private static bool IsExternallyVisible(MethodDef method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsExternallyVisible(FieldDef field) =>
        field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static bool HasVisibleAccessor(PropertyDef property) =>
        property.GetMethods.Concat(property.SetMethods).Concat(property.OtherMethods)
            .Any(IsExternallyVisible);

    private static bool HasVisibleAccessor(EventDef @event) =>
        @event.AddMethod is { } add && IsExternallyVisible(add) ||
        @event.RemoveMethod is { } remove && IsExternallyVisible(remove) ||
        @event.InvokeMethod is { } invoke && IsExternallyVisible(invoke) ||
        @event.OtherMethods.Any(IsExternallyVisible);

    private static string TypeIdentity(ITypeDefOrRef? type) =>
        type == null
            ? string.Empty
            : $"{type.DefinitionAssembly?.FullName ?? "<module>"}!{type.FullName}";

    private static string GenericParameters(IEnumerable<GenericParam> parameters) =>
        string.Join(
            ";",
            parameters.OrderBy(parameter => parameter.Number).Select(parameter =>
                $"{parameter.Number}:{parameter.Name}:0x{(ushort)parameter.Flags:X4}:" +
                string.Join(
                    ",",
                    parameter.GenericParamConstraints
                        .Select(constraint => TypeIdentity(constraint.Constraint))
                        .OrderBy(value => value, StringComparer.Ordinal))));

    private static string Parameters(MethodDef method) =>
        string.Join(
            ";",
            method.ParamDefs.OrderBy(parameter => parameter.Sequence).Select(parameter =>
                $"{parameter.Sequence}:{parameter.Name}:0x{(ushort)parameter.Attributes:X4}:" +
                $"{parameter.Constant?.Value}"));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal sealed record MethodSemanticRecord
{
    public required string Assembly { get; init; }
    public required uint MetadataToken { get; init; }
    public required string DeclaringType { get; init; }
    public required string Name { get; init; }
    public required string Signature { get; init; }
    public required bool InitLocals { get; init; }
    public required ushort MaxStack { get; init; }
    public required IReadOnlyList<MethodParameterRecord> Parameters { get; init; }
    public required IReadOnlyList<MethodLocalRecord> Locals { get; init; }
    public required IReadOnlyList<MethodInstructionRecord> Instructions { get; init; }
    public required IReadOnlyList<MethodExceptionRegionRecord> ExceptionRegions { get; init; }

    internal static MethodSemanticRecord Create(string assembly, MethodDef method)
    {
        var body = method.Body;
        return new MethodSemanticRecord
        {
            Assembly = assembly,
            MetadataToken = method.MDToken.Raw,
            DeclaringType = method.DeclaringType.FullName,
            Name = method.Name.String,
            Signature = method.MethodSig?.ToString() ?? string.Empty,
            InitLocals = body.InitLocals,
            MaxStack = body.MaxStack,
            Parameters = method.ParamDefs
                .OrderBy(parameter => parameter.Sequence)
                .Select(parameter => new MethodParameterRecord(
                    parameter.Sequence,
                    parameter.Name.String,
                    (ushort)parameter.Attributes,
                    OperandValue(parameter.Constant?.Value)))
                .ToArray(),
            Locals = body.Variables
                .Select(local => new MethodLocalRecord(
                    local.Index,
                    local.Type.FullName,
                    local.Type is PinnedSig))
                .ToArray(),
            Instructions = body.Instructions
                .Select(instruction => MethodInstructionRecord.Create(instruction))
                .ToArray(),
            ExceptionRegions = body.ExceptionHandlers
                .Select(MethodExceptionRegionRecord.Create)
                .ToArray()
        };
    }

    private static string? OperandValue(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}

internal sealed record MethodInstructionRecord(
    uint Offset,
    short OpCodeValue,
    string OpCode,
    string OperandKind,
    string? Operand)
{
    internal static MethodInstructionRecord Create(Instruction instruction)
    {
        var (kind, operand) = EncodeOperand(instruction.Operand);
        return new MethodInstructionRecord(
            instruction.Offset,
            instruction.OpCode.Value,
            instruction.OpCode.Name,
            kind,
            operand);
    }

    private static (string Kind, string? Value) EncodeOperand(object? operand) => operand switch
    {
        null => ("none", null),
        string value => ("string", value),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double =>
            ("number", Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture)),
        Instruction target => ("branch", target.Offset.ToString("X8")),
        IList<Instruction> targets =>
            ("switch", string.Join(",", targets.Select(target => target.Offset.ToString("X8")))),
        IMethod method => ("method", MemberIdentity(method)),
        IField field => ("field", MemberIdentity(field)),
        ITypeDefOrRef type => ("type", TypeIdentity(type)),
        TypeSig type => ("type-signature", type.FullName),
        Local local => ("local", local.Index.ToString()),
        Parameter parameter => ("parameter", parameter.Index.ToString()),
        CallingConventionSig signature => ("call-site", signature.ToString()),
        byte[] bytes => ("blob", Convert.ToHexString(bytes).ToLowerInvariant()),
        _ => throw new InvalidDataException(
            $"Unsupported CIL operand {operand.GetType().FullName}: {operand}")
    };

    private static string MemberIdentity(IMemberRef member) =>
        $"{member.DeclaringType?.DefinitionAssembly?.FullName ?? "<module>"}!{member.FullName}";

    private static string TypeIdentity(ITypeDefOrRef type) =>
        $"{type.DefinitionAssembly?.FullName ?? "<module>"}!{type.FullName}";
}

internal sealed record MethodExceptionRegionRecord(
    string HandlerType,
    string? CatchType,
    uint? TryStart,
    uint? TryEnd,
    uint? HandlerStart,
    uint? HandlerEnd,
    uint? FilterStart)
{
    internal static MethodExceptionRegionRecord Create(ExceptionHandler handler) => new(
        handler.HandlerType.ToString(),
        handler.CatchType?.FullName,
        handler.TryStart?.Offset,
        handler.TryEnd?.Offset,
        handler.HandlerStart?.Offset,
        handler.HandlerEnd?.Offset,
        handler.FilterStart?.Offset);
}

internal sealed record MethodParameterRecord(
    ushort Sequence,
    string Name,
    ushort Attributes,
    string? DefaultValue);

internal sealed record MethodLocalRecord(int Index, string Type, bool Pinned);

internal sealed record AssemblySemanticSummary
{
    public required ModAssemblyIdentity Identity { get; init; }
    public required int TypeCount { get; init; }
    public required int MethodCount { get; init; }
    public required int MethodBodyCount { get; init; }
    public required long InstructionCount { get; init; }
}

internal sealed record SemanticPackManifest
{
    public required string FormatVersion { get; init; }
    public required string GameVersion { get; init; }
    public required string MethodStreamSha256 { get; init; }
    public required string SourceTreeSha256 { get; init; }
    public required int SourceFileCount { get; init; }
    public required IReadOnlyList<AssemblySemanticSummary> Assemblies { get; init; }
}

internal sealed record SourceTreeIdentity(string Sha256, int FileCount)
{
    internal static SourceTreeIdentity Read(string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
            return new SourceTreeIdentity(string.Empty, 0);
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Decompiled source root was not found: {root}");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoding = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal)
            .ToArray();
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var text = File.ReadAllText(path, encoding);
            var contentHash = SHA256.HashData(encoding.GetBytes(text));
            hash.AppendData(encoding.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(contentHash);
            hash.AppendData([0]);
        }
        return new SourceTreeIdentity(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            files.Length);
    }
}

internal sealed record Options(
    IReadOnlyList<string> AssemblyPaths,
    string? SourceRoot,
    string GameVersion,
    string OutputPath,
    string IdentityPath)
{
    internal static Options? Parse(string[] args)
    {
        var assemblies = new List<string>();
        string? sourceRoot = null;
        string? gameVersion = null;
        string? output = null;
        string? identity = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--assembly" when index + 1 < args.Length:
                    assemblies.Add(Path.GetFullPath(args[++index]));
                    break;
                case "--source-root" when index + 1 < args.Length:
                    sourceRoot = Path.GetFullPath(args[++index]);
                    break;
                case "--game-version" when index + 1 < args.Length:
                    gameVersion = args[++index];
                    break;
                case "--output" when index + 1 < args.Length:
                    output = Path.GetFullPath(args[++index]);
                    break;
                case "--identity" when index + 1 < args.Length:
                    identity = Path.GetFullPath(args[++index]);
                    break;
                default:
                    return Usage($"Unknown or incomplete argument: {args[index]}");
            }
        }

        if (assemblies.Count == 0 || string.IsNullOrWhiteSpace(gameVersion) ||
            string.IsNullOrWhiteSpace(output))
        {
            return Usage("--assembly, --game-version and --output are required.");
        }
        foreach (var assembly in assemblies)
        {
            if (!File.Exists(assembly))
                return Usage($"Assembly was not found: {assembly}");
        }
        if (assemblies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != assemblies.Count)
            return Usage("Duplicate --assembly path.");

        identity ??= output + ".identity.json";
        return new Options(assemblies, sourceRoot, gameVersion.Trim(), output, identity);
    }

    private static Options? Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine(
            "Usage: CilSemanticPack --assembly <dll> [--assembly <dll> ...] " +
            "[--source-root <decompiled>] --game-version <version> --output <pack.zip> " +
            "[--identity <identity.json>]");
        return null;
    }
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static readonly JsonSerializerOptions Indented = new(Compact)
    {
        WriteIndented = true
    };
}
